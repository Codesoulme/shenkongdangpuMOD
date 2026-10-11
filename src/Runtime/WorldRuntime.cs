using System.Collections.Concurrent;
using AIWorldPawnshop.Capabilities;
using AIWorldPawnshop.Models;
using AIWorldPawnshop.Models.EchoWeave;
using AIWorldPawnshop.Orchestration;
using AIWorldPawnshop.Model;
using AIWorldPawnshop.Storage;
using AIWorldPawnshop.GM;
using Il2Cpp;
using MelonLoader;

namespace AIWorldPawnshop.Runtime;

public sealed class WorldRuntime
{
    private readonly JsonFileStore _store;
    private readonly LocalFallbackEngine _fallback;
    private IModelProvider? _modelProvider;
    private readonly HttpClient _httpClient;
    private readonly string _dataRoot;
    private readonly string _worldId;
    private readonly EchoWeaveRuntime _echoWeave;
    private readonly ExistingAdapterCapabilities _capabilityAdapters;
    private readonly ConcurrentDictionary<string, EchoWeaveSnapshot> _turnSnapshots =
        new(StringComparer.OrdinalIgnoreCase);

    public ModConfig Config { get; private set; }
    public WorldState State { get; private set; }
    public EchoWeaveRuntime EchoWeave => _echoWeave;
    // Public name for the current orchestration layer; EchoWeave remains as a save-format compatibility alias.
    public EchoWeaveRuntime Gm => _echoWeave;

    public WorldRuntime(string dataRoot)
    {
        _dataRoot = dataRoot;
        _store = new JsonFileStore(dataRoot);
        Config = _store.LoadConfig();
        _fallback = new LocalFallbackEngine(_store.LoadLocalContent());
        _httpClient = new HttpClient
        {
            // Each provider request applies its own profile timeout. Keeping the
            // shared client infinite prevents an old profile from limiting a new one.
            Timeout = Timeout.InfiniteTimeSpan
        };
        RebuildModelProvider();
        _worldId = ResolveWorldId();
        State = _store.LoadWorld(_worldId);
        _echoWeave = new EchoWeaveRuntime(dataRoot, State.WorldId);
        _capabilityAdapters = new ExistingAdapterCapabilities(_echoWeave.Capabilities);
        var snapshotRevision = _echoWeave.Snapshot.StateRevision;
        if (snapshotRevision > State.StateRevision)
            State.StateRevision = snapshotRevision;
        var recovery = _echoWeave.Recover(State);
        if (recovery.RecoveredCount > 0)
            _store.Log($"Recovered {recovery.RecoveredCount} unfinished EchoWeave intent(s).");
        _store.SaveWorld(State);
        _echoWeave.Save();
    }

    public ConversationResponse Reply(ConversationRequest request)
    {
        CaptureTurnSnapshot(request);
        var response = _fallback.Reply(request);
        RecordConversation(request, response);
        return response;
    }

    public async Task<ConversationResponse> ReplyAsync(
        ConversationRequest request,
        CancellationToken cancellationToken = default)
    {
        CaptureTurnSnapshot(request);
        ConversationResponse? response = null;
        var modelProvider = _modelProvider;
        if (modelProvider is not null)
        {
            try
            {
                response = await modelProvider.CompleteAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                if (response is not null &&
                    !CharacterConsistencyGuard.IsConsistent(request, response, out var conflict))
                {
                    _store.Log($"Model character conflict; fallback used: {conflict}");
                    MelonLogger.Warning($"[当铺世界] 人物身份冲突，改用本地回复：{conflict}");
                    response = null;
                }
                if (response is not null)
                    MelonLogger.Msg($"[当铺世界] 模型已回复，角色 {request.CharacterKey}");

                if (response is not null && _modelProvider is OpenAiCompatibleProvider gmProvider)
                {
                    var gm = await gmProvider.CompleteGmAsync(request, response, cancellationToken).ConfigureAwait(false);
                    if (gm is not null)
                    {
                        response.WorldModifications = gm.WorldModifications;
                        // GM only replaces the character proposal when it
                        // actually produced a wallet decision.  A GM response
                        // without a wallet proposal must not erase an explicit
                        // amount already extracted from the conversation.
                        if (gm.WalletTransfer.Explicit)
                            response.WalletTransfer = gm.WalletTransfer;
                        response.NpcReply = response.NpcReply.Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                _store.Log("Model request failed; fallback used", ex);
                if (Config.DebugLog)
                    MelonLogger.Warning($"[当铺世界] 模型请求失败，使用本地回复：{ex.Message}");
            }
        }

        return response ?? _fallback.Reply(request);
    }

    public GameConversationContext BuildConversationContext(
        string scene,
        string characterKey,
        string characterName) =>
        NativeConversationContextBuilder.Build(scene, characterKey, characterName, State);

    public async Task<ModelConnectionResult> TestConnectionAsync(
        ModelProfile profile,
        CancellationToken cancellationToken = default)
    {
        return await OpenAiCompatibleProvider.TestConnectionAsync(
                _httpClient,
                profile,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public void ApplyConfig(ModConfig config)
    {
        Config = config.Clone();
        Config.Normalize();
        Config.SyncLegacyFieldsFromActive();
        _store.SaveConfig(Config);
        RebuildModelProvider();
        var profile = Config.GetActiveProfile();
        MelonLogger.Msg(
            $"[当铺世界] 已应用模型接口：{profile.Name} / {profile.BaseUrl} / " +
            $"{profile.Model}，联网请求：{profile.AllowNetwork}");
    }

    public void RecordConversation(ConversationRequest request, ConversationResponse response)
    {
        var turnKey = TurnKey(request);
        if (!_turnSnapshots.TryRemove(turnKey, out var snapshot))
            snapshot = _echoWeave.Capture(request, State);

        if (!_echoWeave.IsCurrent(snapshot, State))
        {
            StripWorldActions(response);
            _echoWeave.TryRecordAdapterOutcome(
                snapshot,
                "conversation.record",
                "回复返回时世界状态已变化，已保留文字并移除过期动作。",
                EchoWeaveIntentStatus.Conflict);
            _store.Log(
                $"Stale response stripped: {turnKey}, snapshot revision " +
                $"{snapshot.StateRevision}, current revision {State.StateRevision}.");
            return;
        }

        if (!_echoWeave.TryRecordConversation(
                snapshot,
                $"玩家与 {snapshot.CharacterDisplayName} 进行了一轮{snapshot.Scene}对话"))
        {
            StripWorldActions(response);
            _store.Log($"Duplicate conversation ignored: {snapshot.SessionId}/{snapshot.TurnId}");
            return;
        }

        ConversationActionResolver.Normalize(request, response);
        var gmResult = GmModificationEngine.Apply(State, response.WorldModifications);
        if (gmResult.Applied)
            _store.Log(gmResult.Message);
        var wallet = _capabilityAdapters.TransferWallet(request, response);
        if (wallet.Proposed)
        {
            MelonLogger.Msg($"[当铺世界] 顾客预算转账：{wallet.Message}");
            State.Facts.Add(new FactRecord
            {
                Day = State.CurrentDay,
                Kind = "native_wallet_transfer",
                Summary = wallet.Message,
                Visibility = "actor"
            });
            var walletSnapshot = _echoWeave.Capture(request, State);
            _echoWeave.TryRecordAdapterOutcome(
                walletSnapshot,
                "wallet.transfer.native",
                wallet.Message,
                wallet.Applied ? EchoWeaveIntentStatus.Fulfilled : EchoWeaveIntentStatus.Rejected,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["amount"] = wallet.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
        }
        var procurementResolution = ProcurementRequestResolver.Resolve(request, response);
        ApplyConversationResult(request, response);
        ApplyThirdPartyTask(request, response);
        var returnVisit = _capabilityAdapters.RequestReturnVisit(State, request, response);
        if (returnVisit.Proposed && !returnVisit.Scheduled)
            MelonLogger.Warning($"[当铺世界] 回访未登记：{returnVisit.Message}");
        var procurement = _capabilityAdapters.RequestGoods(
            State,
            request,
            response,
            procurementResolution);
        if (procurement.Proposed && !procurement.Scheduled)
        {
            MelonLogger.Warning(
                $"[当铺世界] 找货委托未登记：{procurement.Message}");
        }
        if (returnVisit.Proposed)
        {
            _echoWeave.TryRecordAdapterOutcome(
                snapshot,
                "visit.request.self",
                returnVisit.Message,
                returnVisit.Scheduled
                    ? EchoWeaveIntentStatus.Scheduled
                    : EchoWeaveIntentStatus.Rejected);
        }
        if (procurement.Proposed)
        {
            _echoWeave.TryRecordAdapterOutcome(
                snapshot,
                "procurement.request.goods",
                procurement.Message,
                procurement.Scheduled
                    ? EchoWeaveIntentStatus.Scheduled
                    : EchoWeaveIntentStatus.Rejected,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["request_key"] = procurement.RequestKey
                });
        }
        State.Facts.Add(new FactRecord
        {
            Day = State.CurrentDay,
            Kind = "conversation",
            Summary = $"玩家与 {request.CharacterKey} 进行了一轮{request.Scene}对话",
            Visibility = "actor"
        });
        TrimFacts();
        State.TouchRevision();
        _store.SaveWorld(State);
        _echoWeave.CompleteConversation(
            snapshot,
            EchoWeaveIntentStatus.Fulfilled,
            "对话产生的扩展状态已写入。");
        _echoWeave.Save();
    }

    private void ApplyThirdPartyTask(ConversationRequest request, ConversationResponse response)
    {
        var proposal = response.ThirdPartyTask;
        var decision = proposal.Decision.Trim().ToLowerInvariant();
        var pending = State.ThirdPartyTasks.FirstOrDefault(x =>
            x.TargetCharacterKey.Equals(request.CharacterKey, StringComparison.OrdinalIgnoreCase) &&
            x.Status.Equals("accepted", StringComparison.OrdinalIgnoreCase));
        if (pending is not null && (decision is "accept" or "accepted" or "reject" or "rejected"))
        {
            pending.Status = decision.StartsWith("accept", StringComparison.OrdinalIgnoreCase) ? "fulfilled" : "declined";
            pending.OutcomeSummary = decision.StartsWith("accept", StringComparison.OrdinalIgnoreCase)
                ? $"{pending.TargetDisplayName}接受了联系请求：{pending.Purpose}"
                : $"{pending.TargetDisplayName}拒绝了联系请求。";
            return;
        }
        if (!proposal.Explicit || !proposal.Accepted || string.IsNullOrWhiteSpace(proposal.TargetCharacterKey) || string.IsNullOrWhiteSpace(proposal.Purpose))
            return;
        if (proposal.TargetCharacterKey.Equals(request.CharacterKey, StringComparison.OrdinalIgnoreCase))
            return;
        if (!State.Characters.ContainsKey(proposal.TargetCharacterKey) &&
            !State.Characters.Values.Any(x => x.DisplayName.Equals(proposal.TargetDisplayName, StringComparison.OrdinalIgnoreCase)))
            return;
        var existing = State.ThirdPartyTasks.FirstOrDefault(x =>
            x.SourceCharacterKey.Equals(request.CharacterKey, StringComparison.OrdinalIgnoreCase) &&
            x.TargetCharacterKey.Equals(proposal.TargetCharacterKey, StringComparison.OrdinalIgnoreCase) &&
            x.Status is "proposed" or "accepted");
        if (existing is not null) return;
        var queueScheduled = false;
        try
        {
            var playerStore = PlayerStore.Instance;
            if (playerStore is not null)
                queueScheduled = NativeReturnVisitAdapter.QueueIfMissing(
                    playerStore,
                    proposal.TargetCharacterKey.Trim(),
                    proposal.DueInDays);
        }
        catch (Exception ex)
        {
            _store.Log("Third-party native queue unavailable", ex);
        }
        State.ThirdPartyTasks.Add(new ThirdPartyTaskState
        {
            SourceCharacterKey = request.CharacterKey,
            TargetCharacterKey = proposal.TargetCharacterKey.Trim(),
            TargetDisplayName = proposal.TargetDisplayName.Trim(),
            Purpose = proposal.Purpose.Trim()[..Math.Min(proposal.Purpose.Trim().Length, 180)],
            CreatedDay = State.CurrentDay,
            DueDay = State.CurrentDay + proposal.DueInDays,
            Status = queueScheduled ? "accepted" : "proposed",
            NativeQueueScheduled = queueScheduled,
            OutcomeSummary = queueScheduled
                ? $"{request.CharacterDisplayName}答应联系{proposal.TargetDisplayName}：{proposal.Purpose}"
                : $"{request.CharacterDisplayName}答应尝试联系{proposal.TargetDisplayName}，等待原版来客路径确认。"
        });
    }

    public bool ApplyNativeConversationActions(
        ConversationRequest request,
        ConversationResponse response)
    {
        var barter = _capabilityAdapters.OpenBarter(request, response);
        // The character model may have agreed before the native adapter
        // revalidated live ownership.  Do not leave an affirmative reply in
        // the conversation when that accepted proposal cannot be executed.
        if (response.BarterProposal.Accepted &&
            barter.Proposed &&
            !barter.Applied)
        {
            response.NpcReply = ConversationActionResolver.NaturalBarterFailureReply;
        }
        if (barter.Proposed)
            MelonLogger.Msg($"[当铺世界] 交换对话：{barter.Message}");
        var carried = barter.Applied
            ? new CarriedInventoryApplication()
            : _capabilityAdapters.RevealCarriedInventory(State, request, response);
        if (carried.Proposed)
            MelonLogger.Msg($"[当铺世界] 随身物对话：{carried.Message}");
        if (carried.Applied || barter.Applied)
        {
            State.TouchRevision();
            _store.SaveWorld(State);
            _echoWeave.Save();
        }
        var snapshot = _echoWeave.Capture(request, State);
        if (barter.Proposed)
            _echoWeave.TryRecordAdapterOutcome(
                snapshot,
                "barter.open.native",
                barter.Message,
                barter.Applied ? EchoWeaveIntentStatus.Approved : EchoWeaveIntentStatus.Rejected);
        if (carried.Proposed)
            _echoWeave.TryRecordAdapterOutcome(
                snapshot,
                "inventory.reveal.carried",
                carried.Message,
                carried.Applied ? EchoWeaveIntentStatus.Fulfilled : EchoWeaveIntentStatus.Rejected);
        return barter.Applied;
    }

    public NativeTradeApplication ApplyNativeTrade(
        ConversationRequest request,
        ConversationResponse response)
    {
        var result = _capabilityAdapters.AdjustTrade(request, response);
        if (!result.Invoked)
            return result;

        State.Facts.Add(new FactRecord
        {
            Day = State.CurrentDay,
            Kind = "native_trade_proposal",
            Summary =
                $"玩家通过交谈向 {request.CharacterKey} 提出 {result.Kind} {result.Percent}% 的议价，" +
                $"范围 {result.Scope}，" +
                result.Message,
            Visibility = "actor"
        });
        TrimFacts();
        State.TouchRevision();
        _store.SaveWorld(State);
        var snapshot = _echoWeave.Capture(request, State);
        _echoWeave.TryRecordAdapterOutcome(
            snapshot,
            result.Scope.Equals(BargainScopes.AllCurrentAndFuture, StringComparison.OrdinalIgnoreCase)
                ? "trade.adjust.all"
                : "trade.adjust.selected",
            result.Message,
            result.Accepted ? EchoWeaveIntentStatus.Fulfilled : EchoWeaveIntentStatus.Rejected,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["scope"] = result.Scope,
                ["percent"] = result.Percent.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        _echoWeave.Save();
        return result;
    }

    public bool ApplyNativeConversationExit(
        ConversationRequest request,
        ConversationResponse response) =>
        NativeConversationExitAdapter.Apply(request, response);

    public void AdvanceDay()
    {
        State.CurrentDay++;
        var resolutionKey = $"{State.WorldId}:{State.CurrentDay}:{State.RulesVersion}";
        if (State.LastDailyResolutionHash == resolutionKey)
            return;

        State.ActiveEvents.RemoveAll(x => State.CurrentDay >= x.StartDay + x.DurationDays);
        State.ActiveModifications.RemoveAll(x => x.DurationDays > 0 && State.CurrentDay >= x.StartDay + x.DurationDays);
        foreach (var task in State.ThirdPartyTasks.Where(x => x.Status.Equals("accepted", StringComparison.OrdinalIgnoreCase) && State.CurrentDay > x.DueDay))
        {
            task.Status = "expired";
            task.OutcomeSummary = "联系安排超过期限，未得到回应。";
        }
        NativeReturnVisitAdapter.DeferMissedVisits(State);
        NativeProcurementAdapter.ResolveDueRequests(State);
        State.CarriedInventories.RemoveAll(x => x.VisitDay < State.CurrentDay - 1);
        var newEvents = _fallback.BuildDailyEvents(State, State.CurrentDay);
        State.ActiveEvents.AddRange(newEvents);

        foreach (var eventState in newEvents)
        {
            State.News.Add(new NewsRecord
            {
                Day = State.CurrentDay,
                Title = eventState.Title,
                Body = eventState.Summary,
                Importance = eventState.NewsImportance,
                SourceEventKey = eventState.EventKey
            });
        }

        State.LastDailyResolutionHash = resolutionKey;
        State.TouchRevision();
        _store.SaveWorld(State);
        _echoWeave.Save();
        MelonLogger.Msg($"[当铺世界] 世界推进到第 {State.CurrentDay} 天，新增事件 {newEvents.Count} 个");
    }

    public void Save()
    {
        _store.SaveConfig(Config);
        _store.SaveWorld(State);
        _echoWeave.Save();
    }

    public void OnNativeDayEnded()
    {
        try
        {
            AdvanceDay();
        }
        catch (Exception ex)
        {
            _store.Log("Native day transition failed", ex);
            MelonLogger.Error($"[当铺世界] 日结算失败：{ex.Message}");
        }
    }

    public string PrepareNativeClient(StoreClient? client)
    {
        try
        {
            return NativeProcurementAdapter.PrepareClient(State, client);
        }
        catch (Exception ex)
        {
            _store.Log("Prepare procurement client failed", ex);
            MelonLogger.Error($"[当铺世界] 准备委托顾客失败：{ex.Message}");
            return string.Empty;
        }
    }

    public void CompleteNativeClientCreation(
        string requestKey,
        StoreClient? client,
        StoreClientInstance? instance)
    {
        try
        {
            var changed = NativeReturnVisitAdapter.RecordArrival(State, client);
            if (string.IsNullOrWhiteSpace(requestKey))
                changed |= TemporaryClientInventoryAdapter.InjectForBuyer(State, client, instance);
            changed |= NativeProcurementAdapter.CompleteClientCreation(
                    State,
                    requestKey,
                    client,
                    instance);
            if (changed)
            {
                State.TouchRevision();
                _store.SaveWorld(State);
                _echoWeave.Save();
            }
        }
        catch (Exception ex)
        {
            _store.Log("Complete procurement client creation failed", ex);
            MelonLogger.Error($"[当铺世界] 放入委托货物失败：{ex.Message}");
        }
    }

    public void OnNativeItemBought(GameItem? item, int cost)
    {
        try
        {
            if (NativeProcurementAdapter.RecordItemBought(State, item, cost))
            {
                State.TouchRevision();
                _store.SaveWorld(State);
                _echoWeave.Save();
            }
        }
        catch (Exception ex)
        {
            _store.Log("Record procurement purchase failed", ex);
            MelonLogger.Error($"[当铺世界] 记录委托成交失败：{ex.Message}");
        }
    }

    public void PrepareNativeItemBought(GameItem? item)
    {
        try
        {
            NativeProcurementAdapter.PrepareItemBought(State, item);
        }
        catch (Exception ex)
        {
            _store.Log("Restore procurement item value failed", ex);
            MelonLogger.Error($"[当铺世界] 恢复委托物品价值失败：{ex.Message}");
        }
    }

    public void OnNativeClientDismissed(StoreClient? client)
    {
        try
        {
            var changed = NativeProcurementAdapter.RecordClientDismissed(State, client);
            changed |= TemporaryClientInventoryAdapter.RestoreBuyerIntent(State, client);
            if (changed)
            {
                State.TouchRevision();
                _store.SaveWorld(State);
                _echoWeave.Save();
            }
        }
        catch (Exception ex)
        {
            _store.Log("Record procurement dismissal failed", ex);
            MelonLogger.Error($"[当铺世界] 记录委托离店失败：{ex.Message}");
        }
    }

    private string ResolveWorldId()
    {
        var marker = Path.Combine(_dataRoot, "world-id.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        if (File.Exists(marker))
            return File.ReadAllText(marker).Trim();

        var id = Guid.NewGuid().ToString("N");
        File.WriteAllText(marker, id);
        return id;
    }

    private void CaptureTurnSnapshot(ConversationRequest request)
    {
        var key = TurnKey(request);
        _turnSnapshots.GetOrAdd(key, _ => _echoWeave.Capture(request, State));
    }

    private static string TurnKey(ConversationRequest request) =>
        $"{request.ConversationId?.Trim()}:{request.TurnId}";

    private static void StripWorldActions(ConversationResponse response)
    {
        response.Signals = new ConversationSignals();
        response.MemoryCandidates = new List<MemoryCandidate>();
        response.TradeProposal = new TradeProposal();
        response.ProcurementProposal = new ProcurementProposal();
        response.ReturnVisitProposal = new ReturnVisitProposal();
        response.CarriedInventoryProposal = new CarriedInventoryProposal();
        response.BarterProposal = new BarterProposal();
        response.WalletTransfer = new WalletTransferProposal();
        response.WorldModifications = new List<WorldModificationProposal>();
    }

    private void RebuildModelProvider()
    {
        var profile = Config.GetActiveProfile();
        _modelProvider = Config.Enabled &&
                         profile.AllowNetwork &&
                         profile.Provider.Equals("openai-compatible", StringComparison.OrdinalIgnoreCase)
            ? new OpenAiCompatibleProvider(_httpClient, profile)
            : null;
    }

    private void ApplyConversationResult(ConversationRequest request, ConversationResponse response)
    {
        var characterKey = FirstNotBlank(
            request.GameContext.Character.Key,
            request.CharacterKey);
        if (string.IsNullOrWhiteSpace(characterKey))
            return;

        var factionKey = FirstNotBlank(
            request.GameContext.Character.FactionKey,
            request.FactionKey);
        var isNamedSpecial = request.GameContext.Character.IsNamedSpecialCharacter ||
                             request.Scene.Equals("phone", StringComparison.OrdinalIgnoreCase);
        var relationshipDelta = Math.Clamp(
            response.Signals.Warmth + response.Signals.Trust + response.Signals.Respect -
            response.Signals.Offense - response.Signals.Threat,
            -8,
            8);

        CharacterState? character = null;
        if (isNamedSpecial && !State.Characters.TryGetValue(characterKey, out character))
        {
            character = new CharacterState
            {
                CharacterKey = characterKey,
                DisplayName = string.IsNullOrWhiteSpace(request.CharacterDisplayName)
                    ? characterKey
                    : request.CharacterDisplayName,
                FactionKey = factionKey,
                IsNamedSpecialCharacter = true
            };
            State.Characters[characterKey] = character;
        }
        else if (character is not null)
        {
            character.IsNamedSpecialCharacter = true;
            if (!string.IsNullOrWhiteSpace(request.CharacterDisplayName))
                character.DisplayName = request.CharacterDisplayName;
            if (!string.IsNullOrWhiteSpace(factionKey))
                character.FactionKey = factionKey;
        }

        if (character is not null)
        {
            character.Relationship = Math.Clamp(
                character.Relationship + relationshipDelta,
                -100,
                100);
            character.InteractionCount++;
            character.LastInteractionDay = State.CurrentDay;
        }

        FactionState? faction = null;
        if (!string.IsNullOrWhiteSpace(factionKey))
        {
            if (!State.Factions.TryGetValue(factionKey, out faction))
            {
                faction = new FactionState { FactionKey = factionKey };
                State.Factions[factionKey] = faction;
            }

            var factionDelta = Math.Clamp(
                response.Signals.Cooperation + response.Signals.Respect +
                response.Signals.Credibility - response.Signals.Offense -
                response.Signals.Threat,
                -3,
                3);
            faction.Reputation = Math.Clamp(faction.Reputation + factionDelta, -100, 100);
            if (factionDelta != 0)
            {
                faction.RecentChanges.Add(factionDelta);
                if (faction.RecentChanges.Count > 12)
                    faction.RecentChanges.RemoveRange(0, faction.RecentChanges.Count - 12);
            }
        }

        foreach (var candidate in response.MemoryCandidates)
        {
            var summary = candidate.Summary?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(summary))
                continue;

            if (character is not null &&
                !character.Memories.Any(x => x.Summary.Equals(summary, StringComparison.OrdinalIgnoreCase)))
            {
                character.Memories.Add(CreateMemory(candidate, relationshipDelta));
            }

            if (faction is not null &&
                !faction.Memories.Any(x => x.Summary.Equals(summary, StringComparison.OrdinalIgnoreCase)))
            {
                faction.Memories.Add(CreateMemory(candidate, relationshipDelta));
            }
        }

        if (character is not null && character.Memories.Count > 12)
            character.Memories = character.Memories
                .OrderByDescending(x => x.Importance)
                .ThenByDescending(x => x.CreatedDay)
                .Take(12)
                .ToList();

        if (faction is not null && faction.Memories.Count > 12)
            faction.Memories = faction.Memories
                .OrderByDescending(x => x.Importance)
                .ThenByDescending(x => x.CreatedDay)
                .Take(12)
                .ToList();
    }

    private void TrimFacts()
    {
        if (State.Facts.Count > 200)
            State.Facts.RemoveRange(0, State.Facts.Count - 200);
    }

    private static string FirstNotBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private MemoryRecord CreateMemory(MemoryCandidate candidate, int relationshipDelta) => new()
    {
        Kind = candidate.Kind,
        Summary = candidate.Summary.Trim()[..Math.Min(candidate.Summary.Trim().Length, 160)],
        CreatedDay = State.CurrentDay,
        Importance = Math.Clamp(candidate.Importance, 0, 1),
        Valence = Math.Sign(relationshipDelta)
    };
}
