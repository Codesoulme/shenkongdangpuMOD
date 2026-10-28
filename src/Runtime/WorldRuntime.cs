using System.Collections.Concurrent;
using System.Diagnostics;
using AIWorldPawnshop.Capabilities;
using AIWorldPawnshop.Models;
using AIWorldPawnshop.Models.EchoWeave;
using AIWorldPawnshop.Orchestration;
using AIWorldPawnshop.Model;
using AIWorldPawnshop.Storage;
using AIWorldPawnshop.GM;
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
    private readonly GameKnowledgeCatalog? _gameKnowledge;
    private readonly ConcurrentDictionary<string, EchoWeaveSnapshot> _turnSnapshots =
        new(StringComparer.OrdinalIgnoreCase);

    public WorldModConfig Config { get; private set; }
    public WorldState State { get; private set; }
    public EchoWeaveRuntime EchoWeave => _echoWeave;
    // Public name for the current orchestration layer; EchoWeave remains as a save-format compatibility alias.
    public EchoWeaveRuntime Gm => _echoWeave;

    public WorldRuntime(string dataRoot, string? gameDirectory = null)
    {
        _dataRoot = dataRoot;
        _store = new JsonFileStore(dataRoot);
        if (!string.IsNullOrWhiteSpace(gameDirectory))
            _gameKnowledge = new GameKnowledgeCatalog(dataRoot, gameDirectory);
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
        var totalWatch = Stopwatch.StartNew();
        CaptureTurnSnapshot(request);
        ConversationResponse? response = null;
        var modelProvider = _modelProvider;
        if (modelProvider is not null)
        {
            try
            {
                response = await modelProvider.CompleteAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                LogPerformance(
                    $"stage=character_complete elapsed_ms={totalWatch.ElapsedMilliseconds} " +
                    $"has_response={response is not null}");
                if (response is not null &&
                    !CharacterConsistencyGuard.IsConsistent(request, response, out var conflict))
                {
                    _store.Log($"Model character conflict; fallback used: {conflict}");
                    MelonLogger.Warning($"[当铺世界] 人物身份冲突，改用本地回复：{conflict}");
                    response = null;
                }
                if (response is not null)
                    MelonLogger.Msg($"[当铺世界] 模型已回复，角色 {request.CharacterKey}");

                if (response is not null &&
                    _modelProvider is OpenAiCompatibleProvider gmProvider &&
                    ShouldRunGmReview(request, response))
                {
                    var gmWatch = Stopwatch.StartNew();
                    var gm = await gmProvider.CompleteGmAsync(request, response, cancellationToken).ConfigureAwait(false);
                    LogPerformance(
                        $"stage=gm_complete elapsed_ms={gmWatch.ElapsedMilliseconds} " +
                        $"has_response={gm is not null}");
                    if (gm is not null)
                    {
                        response.WorldModifications = gm.WorldModifications;
                        // GM only replaces the character proposal when it
                        // actually produced a wallet decision.  A GM response
                        // without a wallet proposal must not erase an explicit
                        // amount already extracted from the conversation.
                        if (gm.WalletTransfer.Explicit)
                            response.WalletTransfer = gm.WalletTransfer;
                        if (response.BarterProposal.Explicit)
                        {
                            if (!gm.BarterProposal.Explicit || !gm.BarterProposal.Accepted)
                            {
                                response.BarterProposal.Accepted = false;
                                response.BarterProposal.ValueAssessment =
                                    gm.BarterProposal.ValueAssessment;
                                response.BarterProposal.Reason = string.IsNullOrWhiteSpace(
                                    gm.BarterProposal.Reason)
                                    ? "GM 复核认为交换价值或前置条件不满足"
                                    : gm.BarterProposal.Reason;
                                if (response.WalletTransfer.Explicit &&
                                    response.WalletTransfer.Accepted)
                                {
                                    response.WalletTransfer = new WalletTransferProposal();
                                }
                                response.NpcReply = ConversationActionResolver.NaturalBarterFailureReply;
                            }
                            else
                            {
                                response.BarterProposal.ValueAssessment =
                                    gm.BarterProposal.ValueAssessment;
                            }
                        }
                        if (response.ThirdPartyTask.Explicit)
                        {
                            if (!gm.ThirdPartyTask.Explicit)
                            {
                                response.ThirdPartyTask.Accepted = false;
                                response.NpcReply = ThirdPartyUnavailableReply;
                            }
                            else if (!IsSameThirdPartyProposal(
                                         response.ThirdPartyTask,
                                         gm.ThirdPartyTask))
                            {
                                response.ThirdPartyTask.Accepted = false;
                                response.ThirdPartyTask.Reason =
                                    "世界监督结果与人物原提案不一致";
                                response.NpcReply = ThirdPartyUnavailableReply;
                            }
                            else
                            {
                                if (!gm.ThirdPartyTask.Accepted)
                                {
                                    response.ThirdPartyTask.Accepted = false;
                                    response.ThirdPartyTask.Reason = gm.ThirdPartyTask.Reason;
                                    response.NpcReply = ThirdPartyUnavailableReply;
                                }
                            }
                        }
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

        var usedFallback = response is null;
        response ??= _fallback.Reply(request);
        LogPerformance(
            $"stage=reply_ready total_ms={totalWatch.ElapsedMilliseconds} fallback={usedFallback}");
        return response;
    }

    public void LogPerformance(string message)
    {
        if (!Config.DebugLog)
            return;

        var entry = $"[PERF-GMCHAT] {message}";
        _store.Log(entry);
        MelonLogger.Msg($"[当铺世界] {entry}");
    }

    public GameConversationContext BuildConversationContext(
        string scene,
        string characterKey,
        string characterName) =>
        BuildAndObserveConversationContext(scene, characterKey, characterName);

    private GameConversationContext BuildAndObserveConversationContext(
        string scene, string characterKey, string characterName)
    {
        var context = NativeConversationContextBuilder.Build(scene, characterKey, characterName, State);
        _gameKnowledge?.Observe(context, State.CurrentDay);
        return context;
    }

    public void EnrichConversationContext(GameConversationContext context, string playerText) =>
        _gameKnowledge?.Enrich(context, playerText);

    public void TickGameKnowledge() => _gameKnowledge?.Tick();

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

    public void ApplyConfig(WorldModConfig config)
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
        var hasWorldModificationProposal = response.WorldModifications.Count > 0;
        var worldGateReason = string.Empty;
        var worldGateOpened = !hasWorldModificationProposal ||
                              _echoWeave.TryBeginCapability(
                                  snapshot,
                                  "world.modify",
                                  "登记 GM 世界数值修正",
                                  reason: out worldGateReason);
        if (!worldGateOpened && hasWorldModificationProposal)
        {
            response.WorldModifications.Clear();
            MelonLogger.Warning($"[当铺世界] GM 世界修正未通过执行闸门：{worldGateReason}");
        }

        var gmResult = GmModificationEngine.Apply(State, response.WorldModifications);
        if (hasWorldModificationProposal && !gmResult.Applied)
        {
            _echoWeave.TryRecordAdapterOutcome(
                snapshot,
                "world.modify",
                string.IsNullOrWhiteSpace(worldGateReason)
                    ? gmResult.Message
                    : worldGateReason,
                EchoWeaveIntentStatus.Rejected);
        }
        if (gmResult.Applied)
        {
            _store.Log(gmResult.Message);
            var economy = NativeWorldEconomyAdapter.ApplyCurrentClient(State);
            var hasCurrentClient = PlayerStore.Instance?.currentClientInstance is not null;
            if (hasCurrentClient && !economy.Verified)
                _store.Log($"GM 经济修正未能写入当前原版来客：{economy.Message}");
            if (economy.Applied && economy.Verified)
                NativeTradeAdapter.RefreshCurrentUi();
            if (!hasCurrentClient || economy.Verified)
                RecordWorldFact(
                    request,
                    "world_modification",
                    gmResult.Message + " " + economy.Message,
                    response.WorldModifications.Any(x =>
                        string.IsNullOrWhiteSpace(x.TargetKey) ||
                        x.TargetKey.Equals("global", StringComparison.OrdinalIgnoreCase))
                        ? "public"
                        : "actor");
            _echoWeave.TryRecordWorldOutcome(
                State.CurrentDay,
                "world.modify",
                $"{request.ConversationId}:{request.TurnId}",
                gmResult.Message + " " + economy.Message,
                economy.Verified
                    ? EchoWeaveIntentStatus.Fulfilled
                    : hasCurrentClient
                        ? EchoWeaveIntentStatus.Conflict
                        : EchoWeaveIntentStatus.Scheduled,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["proposal_count"] = response.WorldModifications.Count.ToString(),
                    ["native_projection_verified"] = economy.Verified.ToString(),
                    ["native_client_present"] = hasCurrentClient.ToString()
                });
            _echoWeave.TryRecordAdapterOutcome(
                snapshot,
                "world.modify",
                gmResult.Message + " " + economy.Message,
                economy.Verified
                    ? EchoWeaveIntentStatus.Fulfilled
                    : hasCurrentClient
                        ? EchoWeaveIntentStatus.Conflict
                        : EchoWeaveIntentStatus.Scheduled);
        }
        var walletGateReason = string.Empty;
        var walletGateOpened = !response.WalletTransfer.Explicit ||
                               !response.WalletTransfer.Accepted ||
                               _echoWeave.TryBeginCapability(
                                   snapshot,
                                   "wallet.transfer.native",
                                   $"登记顾客补付 {response.WalletTransfer.Amount} 信用点",
                                   reason: out walletGateReason,
                                   arguments: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                                   {
                                       ["amount"] = response.WalletTransfer.Amount.ToString(
                                           System.Globalization.CultureInfo.InvariantCulture)
                                   },
                                   timing: EchoWeaveSafeTiming.NativeTransactionConfirmed);
        if (!walletGateOpened && response.WalletTransfer.Explicit && response.WalletTransfer.Accepted)
        {
            response.WalletTransfer.Accepted = false;
            response.WalletTransfer.Reason = walletGateReason;
            MelonLogger.Warning($"[当铺世界] 顾客补付未通过执行闸门：{walletGateReason}");
        }

        var wallet = _capabilityAdapters.TransferWallet(request, response);
        if (response.BarterProposal.Explicit &&
            response.BarterProposal.Accepted &&
            response.WalletTransfer.Explicit &&
            response.WalletTransfer.Accepted &&
            !wallet.Deferred)
        {
            // Do not open a mixed offer that displays a cash leg which could
            // not be reserved from the customer's native budget.
            response.BarterProposal.Accepted = false;
            response.NpcReply = ConversationActionResolver.NaturalBarterFailureReply;
            MelonLogger.Warning($"[当铺世界] 混合交换补款未登记，已拒绝交换：{wallet.Message}");
        }
        if (wallet.Proposed)
        {
            MelonLogger.Msg($"[当铺世界] 顾客预算转账：{wallet.Message}");
            State.Facts.Add(new FactRecord
            {
                Day = State.CurrentDay,
                Kind = "native_wallet_transfer",
                Summary = $"{request.CharacterDisplayName}：{wallet.Message}",
                Visibility = "actor",
                SubjectCharacterKey = request.CharacterKey
            });
            var walletSnapshot = _echoWeave.Capture(request, State);
            _echoWeave.TryRecordAdapterOutcome(
                walletSnapshot,
                "wallet.transfer.native",
                wallet.Message,
                 wallet.Deferred
                     ? EchoWeaveIntentStatus.Scheduled
                     : wallet.Applied
                         ? EchoWeaveIntentStatus.Fulfilled
                         : EchoWeaveIntentStatus.Rejected,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["amount"] = wallet.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
        }
        var procurementResolution = ProcurementRequestResolver.Resolve(request, response);
        ApplyConversationResult(request, response);
        var thirdPartyGateReason = string.Empty;
        var thirdPartyGateOpened = !response.ThirdPartyTask.Explicit ||
                                   !response.ThirdPartyTask.Accepted ||
                                   _echoWeave.TryBeginCapability(
                                       snapshot,
                                       "visit.request.third_party",
                                       "登记第三方人物联系",
                                       timing: EchoWeaveSafeTiming.AfterDialogue,
                                       reason: out thirdPartyGateReason);
        if (!thirdPartyGateOpened && response.ThirdPartyTask.Explicit && response.ThirdPartyTask.Accepted)
        {
            response.ThirdPartyTask.Accepted = false;
            response.ThirdPartyTask.Reason = thirdPartyGateReason;
            response.NpcReply = ThirdPartyUnavailableReply;
            MelonLogger.Warning($"[当铺世界] 第三方联系未通过执行闸门：{thirdPartyGateReason}");
        }

        var thirdPartyTask = ApplyThirdPartyTask(request, response);
        var returnVisitGateReason = string.Empty;
        var returnVisitGateOpened = !response.ReturnVisitProposal.Explicit ||
                                    !response.ReturnVisitProposal.Accepted ||
                                    _echoWeave.TryBeginCapability(
                                        snapshot,
                                        "visit.request.self",
                                        "登记当前顾客回访",
                                        timing: EchoWeaveSafeTiming.AfterDialogue,
                                        reason: out returnVisitGateReason);
        if (!returnVisitGateOpened && response.ReturnVisitProposal.Explicit && response.ReturnVisitProposal.Accepted)
        {
            response.ReturnVisitProposal.Accepted = false;
            response.ReturnVisitProposal.Reason = returnVisitGateReason;
            MelonLogger.Warning($"[当铺世界] 回访未通过执行闸门：{returnVisitGateReason}");
        }

        var returnVisit = _capabilityAdapters.RequestReturnVisit(State, request, response);
        if (returnVisit.Proposed && !returnVisit.Scheduled)
            MelonLogger.Warning($"[当铺世界] 回访未登记：{returnVisit.Message}");
        var procurementGateReason = string.Empty;
        var procurementGateOpened = !procurementResolution.Explicit ||
                                    !procurementResolution.Accepted ||
                                    _echoWeave.TryBeginCapability(
                                        snapshot,
                                        "procurement.request.goods",
                                        $"登记找货委托 {procurementResolution.Category}",
                                        timing: EchoWeaveSafeTiming.AfterDialogue,
                                        reason: out procurementGateReason);
        if (!procurementGateOpened && procurementResolution.Explicit && procurementResolution.Accepted)
        {
            procurementResolution = new ProcurementResolution
            {
                Explicit = procurementResolution.Explicit,
                Accepted = false,
                Category = procurementResolution.Category,
                RequestedDescription = procurementResolution.RequestedDescription,
                DueInDays = procurementResolution.DueInDays,
                PromisedPremiumPercent = procurementResolution.PromisedPremiumPercent,
                AccessLevel = procurementResolution.AccessLevel,
                MaxRarityTier = procurementResolution.MaxRarityTier,
                AccessSearchModifier = procurementResolution.AccessSearchModifier,
                AccessReason = procurementResolution.AccessReason,
                Reason = procurementGateReason
            };
            MelonLogger.Warning($"[当铺世界] 找货委托未通过执行闸门：{procurementGateReason}");
        }

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
                // The gate opens before the native request key is allocated;
                // keep the completion key stable and retain the request key
                // in the human-readable receipt above.
                null);
        }
        if (response.ThirdPartyTask.Explicit)
        {
            var status = thirdPartyTask?.Status ?? string.Empty;
            var intentStatus = status switch
            {
                "accepted" => EchoWeaveIntentStatus.Scheduled,
                "proposed" => EchoWeaveIntentStatus.CapabilityPending,
                "arrived" => EchoWeaveIntentStatus.Scheduled,
                "fulfilled" => EchoWeaveIntentStatus.Fulfilled,
                _ => EchoWeaveIntentStatus.Rejected
            };
            _echoWeave.TryRecordAdapterOutcome(
                snapshot,
                "visit.request.third_party",
                thirdPartyTask?.OutcomeSummary ??
                FirstNotBlank(response.ThirdPartyTask.Reason, "第三方联系未通过可执行条件。"),
                intentStatus,
                null);
        }
        State.Facts.Add(new FactRecord
        {
            Day = State.CurrentDay,
            Kind = "conversation",
            Summary = $"玩家与 {request.CharacterKey} 进行了一轮{request.Scene}对话",
            Visibility = "actor",
            SubjectCharacterKey = request.CharacterKey
        });
        TrimFacts();
        State.TouchRevision();
        var saved = _store.SaveWorld(State);
        _echoWeave.CompleteConversation(
            snapshot,
            saved ? EchoWeaveIntentStatus.Fulfilled : EchoWeaveIntentStatus.Conflict,
            saved ? "对话产生的扩展状态已写入。" : "扩展状态保存失败，不能视为持久化完成。");
        _echoWeave.Save();
    }

    private ThirdPartyTaskState? ApplyThirdPartyTask(
        ConversationRequest request,
        ConversationResponse response)
    {
        var proposal = response.ThirdPartyTask;
        if (proposal.Explicit)
            _capabilityAdapters.RequireThirdPartyContact();
        var decision = proposal.Decision.Trim().ToLowerInvariant();
        var pending = State.ThirdPartyTasks.FirstOrDefault(x =>
            MatchesThirdPartyTarget(x, request) &&
            (x.Status.Equals("accepted", StringComparison.OrdinalIgnoreCase) ||
             x.Status.Equals("proposed", StringComparison.OrdinalIgnoreCase) ||
             x.Status.Equals("arrived", StringComparison.OrdinalIgnoreCase)));
        if (pending is not null &&
            CanResolveThirdPartyDecision(pending, decision, proposal.Explicit))
        {
            pending.Status = decision.StartsWith("accept", StringComparison.OrdinalIgnoreCase) ? "fulfilled" : "declined";
            if (pending.Status.Equals("declined", StringComparison.OrdinalIgnoreCase) &&
                pending.NativeQueueScheduled &&
                pending.NativeQueueOwned &&
                !string.IsNullOrWhiteSpace(pending.TargetNativeIdentifier) &&
                pending.NativeQueueDay is int queuedDay)
            {
                try
                {
                    NativeReturnVisitAdapter.TryRemoveQueuedClient(
                        PlayerStore.Instance!,
                        pending.TargetNativeIdentifier,
                        queuedDay);
                }
                catch (Exception ex)
                {
                    _store.Log("Third-party queue cancellation failed", ex);
                }
            }
            pending.OutcomeSummary = decision.StartsWith("accept", StringComparison.OrdinalIgnoreCase)
                ? $"{pending.TargetDisplayName}接受了联系请求：{pending.Purpose}"
                : $"{pending.TargetDisplayName}拒绝了联系请求。";
            State.Facts.Add(new FactRecord
            {
                Day = State.CurrentDay,
                Kind = "third_party_decision",
                Summary = pending.OutcomeSummary,
                Visibility = "actor",
                SubjectCharacterKey = request.CharacterKey
            });
            return pending;
        }
        if (!proposal.Explicit || !proposal.Accepted ||
            (string.IsNullOrWhiteSpace(proposal.TargetCharacterKey) &&
             string.IsNullOrWhiteSpace(proposal.TargetDisplayName)) ||
            string.IsNullOrWhiteSpace(proposal.Purpose))
            return null;
        if (!TryResolveThirdPartyTarget(
                State,
                proposal,
                out var targetKey,
                out var targetName,
                out var targetNativeIdentifier))
        {
            RejectThirdPartyProposal(response);
            return null;
        }
        if (targetKey.Equals(request.CharacterKey, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(targetNativeIdentifier) &&
             targetNativeIdentifier.Equals(
                 request.GameContext.Character.OriginalIdentifier,
                 StringComparison.OrdinalIgnoreCase)))
        {
            RejectThirdPartyProposal(response);
            return null;
        }
        if (string.IsNullOrWhiteSpace(targetNativeIdentifier))
        {
            RejectThirdPartyProposal(response);
            return null;
        }
        var dueInDays = Math.Clamp(proposal.DueInDays, 1, 7);
        var existing = State.ThirdPartyTasks.FirstOrDefault(x =>
            x.SourceCharacterKey.Equals(request.CharacterKey, StringComparison.OrdinalIgnoreCase) &&
            x.TargetCharacterKey.Equals(targetKey, StringComparison.OrdinalIgnoreCase) &&
            x.Status is "proposed" or "accepted");
        if (existing is not null) return existing;
        var queueScheduled = false;
        var queueOwned = false;
        int? nativeQueueDay = null;
        var queueMessage = "等待目标人物的原版联系路径确认";
        try
        {
            var playerStore = PlayerStore.Instance;
            if (playerStore is not null && !string.IsNullOrWhiteSpace(targetNativeIdentifier))
            {
                var queue = NativeReturnVisitAdapter.TryQueue(
                    playerStore,
                    targetNativeIdentifier,
                    dueInDays);
                queueScheduled = queue.Scheduled || queue.AlreadyQueued;
                queueOwned = queue.Scheduled;
                nativeQueueDay = queue.ExistingDay ?? (queueScheduled ? dueInDays : null);
                queueMessage = queue.Message;
            }
        }
        catch (Exception ex)
        {
            _store.Log("Third-party native queue unavailable", ex);
            queueMessage = "原版来客队列调用失败，等待后续重试";
        }
        if (!queueScheduled)
        {
            RejectThirdPartyProposal(response);
            MelonLogger.Warning($"[当铺世界] 第三方联系未登记：{queueMessage}");
            return null;
        }
        var task = new ThirdPartyTaskState
        {
            SourceCharacterKey = request.CharacterKey,
            TargetCharacterKey = targetKey,
            TargetNativeIdentifier = targetNativeIdentifier,
            TargetDisplayName = targetName,
            Purpose = proposal.Purpose.Trim()[..Math.Min(proposal.Purpose.Trim().Length, 180)],
            CreatedDay = State.CurrentDay,
            DueDay = State.CurrentDay + dueInDays,
            Status = queueScheduled ? "accepted" : "proposed",
            NativeQueueScheduled = queueScheduled,
            NativeQueueOwned = queueOwned,
            NativeQueueDay = nativeQueueDay,
            OutcomeSummary = queueScheduled
                ? $"{request.CharacterDisplayName}答应联系{proposal.TargetDisplayName}：{proposal.Purpose}"
                : $"{request.CharacterDisplayName}答应尝试联系{proposal.TargetDisplayName}：{queueMessage}。"
        };
        State.ThirdPartyTasks.Add(task);
        State.Facts.Add(new FactRecord
        {
            Day = State.CurrentDay,
            Kind = "third_party_contact",
            Summary = task.OutcomeSummary,
            Visibility = "actor",
            SubjectCharacterKey = request.CharacterKey
        });
        return task;
    }

    private static void RejectThirdPartyProposal(ConversationResponse response)
    {
        response.ThirdPartyTask.Accepted = false;
        response.ThirdPartyTask.Decision = "none";
        response.NpcReply = ThirdPartyUnavailableReply;
    }

    private static bool MatchesThirdPartyTarget(
        ThirdPartyTaskState task,
        ConversationRequest request)
    {
        var originalIdentifier = request.GameContext.Character.OriginalIdentifier;
        return task.TargetCharacterKey.Equals(request.CharacterKey, StringComparison.OrdinalIgnoreCase) ||
               task.TargetCharacterKey.Equals(request.GameContext.Character.Key, StringComparison.OrdinalIgnoreCase) ||
               task.TargetDisplayName.Equals(request.CharacterDisplayName, StringComparison.OrdinalIgnoreCase) ||
               (!string.IsNullOrWhiteSpace(originalIdentifier) &&
                task.TargetNativeIdentifier.Equals(originalIdentifier, StringComparison.OrdinalIgnoreCase));
    }

    public static bool CanResolveThirdPartyDecision(
        ThirdPartyTaskState task,
        string? decision,
        bool explicitProposal = true)
    {
        var normalized = decision?.Trim().ToLowerInvariant() ?? string.Empty;
        return explicitProposal &&
               task.Status.Equals("arrived", StringComparison.OrdinalIgnoreCase) &&
               normalized is "accept" or "accepted" or "reject" or "rejected";
    }

    public static bool IsSameThirdPartyProposal(
        ThirdPartyTaskProposal characterProposal,
        ThirdPartyTaskProposal gmProposal)
    {
        if (!characterProposal.Explicit || !gmProposal.Explicit)
            return false;

        var sameTarget = !string.IsNullOrWhiteSpace(characterProposal.TargetCharacterKey)
            ? characterProposal.TargetCharacterKey.Equals(
                gmProposal.TargetCharacterKey,
                StringComparison.OrdinalIgnoreCase)
            : characterProposal.TargetDisplayName.Equals(
                gmProposal.TargetDisplayName,
                StringComparison.OrdinalIgnoreCase);
        return sameTarget &&
               characterProposal.Purpose.Trim().Equals(
                   gmProposal.Purpose.Trim(),
                   StringComparison.OrdinalIgnoreCase) &&
               Math.Clamp(characterProposal.DueInDays, 1, 7) ==
               Math.Clamp(gmProposal.DueInDays, 1, 7) &&
               (!gmProposal.Accepted || characterProposal.Accepted);
    }

    public static bool TryResolveThirdPartyTarget(
        WorldState state,
        ThirdPartyTaskProposal proposal,
        out string targetKey,
        out string targetName)
    {
        return TryResolveThirdPartyTarget(
            state,
            proposal,
            out targetKey,
            out targetName,
            out _);
    }

    private static bool TryResolveThirdPartyTarget(
        WorldState state,
        ThirdPartyTaskProposal proposal,
        out string targetKey,
        out string targetName,
        out string targetNativeIdentifier)
    {
        targetKey = proposal.TargetCharacterKey.Trim();
        if (!state.Characters.ContainsKey(targetKey))
        {
            var matches = state.Characters.Values
                .Where(x => x.DisplayName.Equals(
                    proposal.TargetDisplayName,
                    StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToList();
            targetKey = matches.Count == 1
                ? matches[0].CharacterKey
                : string.Empty;
        }
        if (string.IsNullOrWhiteSpace(targetKey))
        {
            targetName = string.Empty;
            targetNativeIdentifier = string.Empty;
            return false;
        }

        targetName = state.Characters.TryGetValue(targetKey, out var targetCharacter)
            ? FirstNotBlank(proposal.TargetDisplayName, targetCharacter.DisplayName, targetKey)
            : FirstNotBlank(proposal.TargetDisplayName, targetKey);
        // Never feed the Mod's canonical key into the native queue.  A queue
        // entry is only safe when the original identifier was observed from
        // the game's StoreClient blueprint.
        targetNativeIdentifier = targetCharacter is { CanReturn: true } or { CanReturnAsBuyer: true }
            ? targetCharacter.OriginalIdentifier?.Trim() ?? string.Empty
            : string.Empty;
        return true;
    }

    public static bool TryResolveThirdPartyNativeIdentifier(
        WorldState state,
        ThirdPartyTaskProposal proposal,
        out string targetKey,
        out string targetName,
        out string targetNativeIdentifier) =>
        TryResolveThirdPartyTarget(
            state,
            proposal,
            out targetKey,
            out targetName,
            out targetNativeIdentifier);

    public bool ApplyNativeConversationActions(
        ConversationRequest request,
        ConversationResponse response)
    {
        var barterGateReason = string.Empty;
        var barterGateOpened = !response.BarterProposal.Explicit ||
                               !response.BarterProposal.Accepted ||
                               _echoWeave.TryBeginCapability(
                                   _echoWeave.Capture(request, State),
                                   "barter.open.native",
                                   "登记原版物品交换",
                                   timing: EchoWeaveSafeTiming.TradeUiReady,
                                   reason: out barterGateReason);
        if (!barterGateOpened && response.BarterProposal.Explicit && response.BarterProposal.Accepted)
        {
            response.BarterProposal.Accepted = false;
            response.BarterProposal.Reason = barterGateReason;
            response.NpcReply = ConversationActionResolver.NaturalBarterFailureReply;
            MelonLogger.Warning($"[当铺世界] 交换未通过执行闸门：{barterGateReason}");
        }

        var barter = _capabilityAdapters.OpenBarter(request, response);
        // The character model may have agreed before the native adapter
        // revalidated live ownership.  Do not leave an affirmative reply in
        // the conversation when that accepted proposal cannot be executed.
        if (response.BarterProposal.Accepted &&
            barter.Proposed &&
            !barter.Applied)
        {
            NativeWalletAdapter.CancelPending(request.CharacterKey);
            response.NpcReply = ConversationActionResolver.NaturalBarterFailureReply;
        }
        if (barter.Proposed)
            MelonLogger.Msg($"[当铺世界] 交换对话：{barter.Message}");
        if (barter.Proposed)
            RecordWorldFact(request, "native_barter", barter.Message, "actor");
        var carriedGateReason = string.Empty;
        var carriedGateOpened = !barter.Applied &&
                                (!response.CarriedInventoryProposal.Explicit ||
                                 !response.CarriedInventoryProposal.Accepted ||
                                 _echoWeave.TryBeginCapability(
                                     _echoWeave.Capture(request, State),
                                     "inventory.reveal.carried",
                                     "登记展示顾客随身货物",
                                     timing: EchoWeaveSafeTiming.AfterDialogue,
                                     reason: out carriedGateReason));
        if (!carriedGateOpened && response.CarriedInventoryProposal.Explicit && response.CarriedInventoryProposal.Accepted)
        {
            response.CarriedInventoryProposal.Accepted = false;
            response.CarriedInventoryProposal.Reason = carriedGateReason;
            response.NpcReply = "抱歉，那件东西现在拿不出来，先别把它算进交易。";
            MelonLogger.Warning($"[当铺世界] 随身货物展示未通过执行闸门：{carriedGateReason}");
        }

        var carried = barter.Applied || !carriedGateOpened
            ? new CarriedInventoryApplication()
            : _capabilityAdapters.RevealCarriedInventory(State, request, response);
        if (!barter.Proposed && carried.Proposed &&
            response.CarriedInventoryProposal.Accepted && !carried.Applied)
            response.NpcReply = "抱歉，那件东西现在拿不出来，先别把它算进交易。";
        if (carried.Proposed)
            MelonLogger.Msg($"[当铺世界] 随身物对话：{carried.Message}");
        if (carried.Proposed)
            RecordWorldFact(request, "native_inventory", carried.Message, "actor");
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
                barter.Applied ? EchoWeaveIntentStatus.Scheduled : EchoWeaveIntentStatus.Rejected);
        if (carried.Proposed)
            _echoWeave.TryRecordAdapterOutcome(
                snapshot,
                "inventory.reveal.carried",
                carried.Message,
                carried.Applied ? EchoWeaveIntentStatus.Fulfilled : EchoWeaveIntentStatus.Rejected);
        return barter.Applied || carried.Applied;
    }

    public NativeTradeApplication ApplyNativeTrade(
        ConversationRequest request,
        ConversationResponse response)
    {
        var requestedTradeScope = response.TradeProposal.Scope;
        if (string.IsNullOrWhiteSpace(requestedTradeScope) ||
            requestedTradeScope.Equals(BargainScopes.Unspecified, StringComparison.OrdinalIgnoreCase))
        {
            requestedTradeScope = BargainScopeResolver.Resolve(request.PlayerText, 1).Scope;
        }
        var tradeCapabilityKey = requestedTradeScope.Equals(
            BargainScopes.AllCurrentAndFuture,
            StringComparison.OrdinalIgnoreCase)
            ? "trade.adjust.all"
            : "trade.adjust.selected";

        if (response.TradeProposal.Explicit)
        {
            var gateReason = string.Empty;
            if (!_echoWeave.TryBeginCapability(
                    _echoWeave.Capture(request, State),
                    tradeCapabilityKey,
                    "登记原版议价调整",
                    timing: EchoWeaveSafeTiming.TradeUiReady,
                    reason: out gateReason))
            {
                MelonLogger.Warning($"[当铺世界] 议价未通过执行闸门：{gateReason}");
                return new NativeTradeApplication
                {
                    Proposed = true,
                    Invoked = false,
                    Message = gateReason
                };
            }
        }

        var result = _capabilityAdapters.AdjustTrade(request, response);
        if (!result.Invoked)
        {
            if (result.Proposed)
            {
                _echoWeave.TryRecordAdapterOutcome(
                    _echoWeave.Capture(request, State),
                    tradeCapabilityKey,
                    result.Message,
                    EchoWeaveIntentStatus.Rejected);
            }
            return result;
        }

        State.Facts.Add(new FactRecord
        {
            Day = State.CurrentDay,
            Kind = "native_trade_proposal",
            Summary =
                $"玩家通过交谈向 {request.CharacterDisplayName}（{request.CharacterKey}）提出 {result.Kind} {result.Percent}% 的议价，" +
                $"范围 {result.Scope}，" +
                result.Message,
            Visibility = "actor",
            SubjectCharacterKey = request.CharacterKey
        });
        TrimFacts();
        State.TouchRevision();
        _store.SaveWorld(State);
        var snapshot = _echoWeave.Capture(request, State);
        _echoWeave.TryRecordAdapterOutcome(
            snapshot,
            tradeCapabilityKey,
            result.Message,
            result.Accepted ? EchoWeaveIntentStatus.Fulfilled : EchoWeaveIntentStatus.Rejected,
            null);
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

        var expiredEvents = State.ActiveEvents
            .Where(x => State.CurrentDay >= x.StartDay + x.DurationDays)
            .ToList();
        var expiredModifications = State.ActiveModifications
            .Where(x => x.DurationDays > 0 && State.CurrentDay >= x.StartDay + x.DurationDays)
            .ToList();
        State.ActiveEvents.RemoveAll(x => expiredEvents.Contains(x));
        State.ActiveModifications.RemoveAll(x => expiredModifications.Contains(x));
        foreach (var expired in expiredEvents)
        {
            _echoWeave.TryRecordWorldOutcome(
                State.CurrentDay,
                "world.propose.event",
                expired.EventKey,
                $"跨天事件结束：{expired.Title}",
                EchoWeaveIntentStatus.Expired);
        }
        foreach (var expired in expiredModifications)
        {
            _echoWeave.TryRecordWorldOutcome(
                State.CurrentDay,
                "world.modify",
                expired.ModificationId,
                $"世界修正到期并恢复：{expired.Title}",
                EchoWeaveIntentStatus.Expired,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["target_key"] = expired.TargetKey,
                    ["field"] = expired.Field
                });
        }
        foreach (var task in State.ThirdPartyTasks.Where(x =>
                     (x.Status.Equals("accepted", StringComparison.OrdinalIgnoreCase) ||
                      x.Status.Equals("proposed", StringComparison.OrdinalIgnoreCase) ||
                      x.Status.Equals("arrived", StringComparison.OrdinalIgnoreCase)) &&
                     State.CurrentDay > x.DueDay +
                     (x.Status.Equals("arrived", StringComparison.OrdinalIgnoreCase) ? 1 : 0)))
        {
            if (task.NativeQueueOwned &&
                task.NativeQueueDay is int queuedDay &&
                !string.IsNullOrWhiteSpace(task.TargetNativeIdentifier))
            {
                try
                {
                    NativeReturnVisitAdapter.TryRemoveQueuedClient(
                        PlayerStore.Instance!,
                        task.TargetNativeIdentifier,
                        queuedDay);
                }
                catch (Exception ex)
                {
                    _store.Log("Expired third-party queue cleanup failed", ex);
                }
            }
            task.Status = "expired";
            task.OutcomeSummary = task.NativeQueueScheduled
                ? "联系安排超过期限，未得到回应。"
                : "联系任务未能进入原版来客队列，已超过等待期限。";
            _echoWeave.TryRecordWorldOutcome(
                State.CurrentDay,
                "visit.request.third_party",
                task.TaskKey,
                task.OutcomeSummary,
                EchoWeaveIntentStatus.Expired);
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
            _echoWeave.TryRecordWorldOutcome(
                State.CurrentDay,
                "world.propose.event",
                eventState.EventKey,
                $"每日事件生成并发布：{eventState.Title}",
                EchoWeaveIntentStatus.Fulfilled,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["item_group"] = eventState.ItemGroup,
                    ["duration_days"] = eventState.DurationDays.ToString(),
                    ["news_importance"] = eventState.NewsImportance.ToString()
                });
        }

        State.LastDailyResolutionHash = resolutionKey;
        var economyRefreshed = NativeWorldEconomyAdapter.RefreshCurrentClient(State);
        if ((expiredEvents.Count > 0 || expiredModifications.Count > 0 || newEvents.Count > 0) &&
            PlayerStore.Instance?.currentClientInstance is not null &&
            !economyRefreshed)
        {
            _store.Log("Cross-day native economy refresh was not verified.");
            _echoWeave.TryRecordWorldOutcome(
                State.CurrentDay,
                "world.modify",
                $"day-refresh:{State.CurrentDay}",
                "换日后当前顾客的原版经济字段未能完成校验。",
                EchoWeaveIntentStatus.Conflict);
        }
        State.TouchRevision();
        _store.SaveWorld(State);
        _echoWeave.Save();
        MelonLogger.Msg($"[当铺世界] 世界推进到第 {State.CurrentDay} 天，新增事件 {newEvents.Count} 个");
    }

    public void Save()
    {
        _gameKnowledge?.Save();
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
            changed |= NativeReturnVisitAdapter.RecordThirdPartyArrival(State, client);
            if (string.IsNullOrWhiteSpace(requestKey))
                changed |= TemporaryClientInventoryAdapter.InjectForBuyer(State, client, instance);
            changed |= NativeProcurementAdapter.CompleteClientCreation(
                    State,
                    requestKey,
                    client,
                    instance);
            var economy = NativeWorldEconomyAdapter.ApplyCurrentClient(State);
            var nativeIdentifier = client?.identifier ?? string.Empty;
            if (economy.Applied)
            {
                _echoWeave.TryRecordWorldOutcome(
                    State.CurrentDay,
                    "world.modify",
                    $"client:{nativeIdentifier}:{State.CurrentDay}",
                    economy.Message,
                    economy.Verified
                        ? EchoWeaveIntentStatus.Fulfilled
                        : EchoWeaveIntentStatus.Conflict,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["native_projection_verified"] = economy.Verified.ToString()
                    });
            }
            foreach (var arrived in State.ThirdPartyTasks.Where(x =>
                         x.ArrivedDay == State.CurrentDay &&
                         x.TargetNativeIdentifier.Equals(nativeIdentifier, StringComparison.OrdinalIgnoreCase)))
            {
                _echoWeave.TryRecordWorldOutcome(
                    State.CurrentDay,
                    "visit.request.third_party",
                    arrived.TaskKey,
                    arrived.OutcomeSummary,
                    EchoWeaveIntentStatus.Ready);
            }
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
            NativeWorldEconomyAdapter.RestoreClient(client);
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
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
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
        response.ThirdPartyTask = new ThirdPartyTaskProposal();
    }

    public static void StripUnsafeWorldActions(ConversationResponse response) =>
        StripWorldActions(response);

    private void RebuildModelProvider()
    {
        var profile = Config.GetActiveProfile();
        _modelProvider = Config.Enabled &&
                         profile.AllowNetwork &&
                         profile.Provider.Equals("openai-compatible", StringComparison.OrdinalIgnoreCase)
            ? new OpenAiCompatibleProvider(_httpClient, profile, LogPerformance)
            : null;
    }

    private static bool ShouldRunGmReview(
        ConversationRequest request,
        ConversationResponse response)
    {
        if (response.WorldModifications.Count > 0 ||
            response.RequiresGmReview ||
            response.TradeProposal.Explicit ||
            response.WalletTransfer.Explicit ||
            response.ProcurementProposal.Explicit ||
            response.ReturnVisitProposal.Explicit ||
            response.CarriedInventoryProposal.Explicit ||
            response.BarterProposal.Explicit ||
            response.ThirdPartyTask.Explicit)
            return true;

        // An unstructured world change still needs review, but merely asking
        // about police, prices, or the market does not. Require both a world
        // target in the player's turn and an affirmative commitment in the
        // character reply; refusals and informational chat stay one-request.
        var hasWorldTarget = ContainsAny(request.PlayerText,
            "警察", "巡逻", "小偷", "偷窃", "盗窃", "失窃", "警报", "防盗",
            "保险", "新闻", "供应", "供给", "需求", "市场", "缺货", "涨价",
            "降价", "事件", "治安", "价格", "物价", "安全", "保护", "环境",
            "情况", "生意", "客流", "库存", "以后", "长期", "永久", "这几天",
            "最近", "附近", "店门", "店里", "城市", "街区");
        if (!hasWorldTarget)
            return false;

        var accepted = ContainsAny(response.NpcReply,
            "我会", "我们会", "我答应", "答应你", "给你安排", "替你安排",
            "可以安排", "就这么办", "没问题", "行，我", "好，我", "当然可以");
        if (!accepted)
            return false;

        return !ContainsAny(response.NpcReply,
            "不会", "不能", "不行", "没法", "办不到", "做不到", "无法", "拒绝",
            "不能答应", "无法保证", "不归我管");
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private const string ThirdPartyUnavailableReply =
        "这件事我现在没法替你安排，你还是直接找对方谈吧。";

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
                OriginalIdentifier = request.GameContext.Character.OriginalIdentifier,
                OriginalRealName = request.GameContext.Character.OriginalRealName,
                FactionKey = factionKey,
                IsNamedSpecialCharacter = true,
                CanReturn = request.GameContext.Character.CanReturn,
                CanReturnAsBuyer = request.GameContext.Character.CanReturnAsBuyer
            };
            State.Characters[characterKey] = character;
        }
        else if (character is not null)
        {
            character.IsNamedSpecialCharacter = true;
            if (!string.IsNullOrWhiteSpace(request.CharacterDisplayName))
                character.DisplayName = request.CharacterDisplayName;
            if (!string.IsNullOrWhiteSpace(request.GameContext.Character.OriginalIdentifier))
                character.OriginalIdentifier = request.GameContext.Character.OriginalIdentifier;
            if (!string.IsNullOrWhiteSpace(request.GameContext.Character.OriginalRealName))
                character.OriginalRealName = request.GameContext.Character.OriginalRealName;
            if (!string.IsNullOrWhiteSpace(factionKey))
                character.FactionKey = factionKey;
            character.CanReturn |= request.GameContext.Character.CanReturn;
            character.CanReturnAsBuyer |= request.GameContext.Character.CanReturnAsBuyer;
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

            var evidence = candidate.Evidence?.Trim() ?? string.Empty;
            var grounded = evidence.Length >= 2 &&
                           (request.PlayerText.Contains(evidence, StringComparison.OrdinalIgnoreCase) ||
                            response.NpcReply.Contains(evidence, StringComparison.OrdinalIgnoreCase));

            if (character is not null &&
                !character.Memories.Any(x => x.Summary.Equals(summary, StringComparison.OrdinalIgnoreCase)))
            {
                character.Memories.Add(CreateMemory(
                    request, characterKey, candidate, relationshipDelta,
                    grounded ? evidence : request.PlayerText));
            }

            if (faction is not null && grounded &&
                candidate.Scope.Equals("faction", StringComparison.OrdinalIgnoreCase) &&
                !faction.Memories.Any(x => x.Summary.Equals(summary, StringComparison.OrdinalIgnoreCase)))
            {
                faction.Memories.Add(CreateMemory(
                    request, characterKey, candidate, relationshipDelta, evidence));
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

    private void RecordWorldFact(
        ConversationRequest request,
        string kind,
        string summary,
        string visibility)
    {
        if (string.IsNullOrWhiteSpace(summary))
            return;

        var speaker = FirstNotBlank(request.CharacterDisplayName, request.CharacterKey, "人物");
        State.Facts.Add(new FactRecord
        {
            Day = State.CurrentDay,
            Kind = kind,
            Summary = $"{speaker}：{summary.Trim()}",
            Visibility = visibility,
            SubjectCharacterKey = request.CharacterKey
        });
        TrimFacts();
    }

    private static string FirstNotBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private MemoryRecord CreateMemory(
        ConversationRequest request,
        string characterKey,
        MemoryCandidate candidate,
        int relationshipDelta,
        string evidence) => new()
    {
        Kind = candidate.Kind,
        Summary = candidate.Summary.Trim()[..Math.Min(candidate.Summary.Trim().Length, 160)],
        CreatedDay = State.CurrentDay,
        Importance = Math.Clamp(candidate.Importance, 0, 1),
        Valence = Math.Sign(relationshipDelta),
        SourceCharacterKey = characterKey,
        SourceConversationId = request.ConversationId,
        SourceTurnId = request.TurnId,
        Evidence = evidence[..Math.Min(evidence.Length, 160)]
    };
}
