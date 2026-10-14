using AIWorldPawnshop.Capabilities;
using AIWorldPawnshop.Models;
using AIWorldPawnshop.Models.EchoWeave;
using AIWorldPawnshop.Orchestration;
using AIWorldPawnshop.GM;
using AIWorldPawnshop.Model;
using AIWorldPawnshop.Runtime;
using AIWorldPawnshop.Storage;
using System.Net;
using System.Text;
using System.Text.Json;

var root = Path.Combine(Path.GetTempPath(), "AIWorldPawnshop-EchoWeaveSmoke", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

try
{
    RegistryContainsOnlyKnownCapabilities();
    DeduplicationKeyIsStable();
    LedgerRejectsDuplicateAndSurvivesCorruptTail();
    RuntimeRecoversUnfinishedConversation();
    DuplicateConversationReplayFlow();
    StateRevisionIsMonotonic();
    GmModificationFlow();
    GmGoalPathSafetyFlow();
    DailyEventFlow();
    NamedCharacterPersistenceFlow();
    LocalFallbackConversationFlow();
    ActionRefusalMustWinFlow();
    ModelProtocolCacheFlow();
    CodexClientRestrictionFlow();
    ModelTransientFailureDoesNotDoubleWaitFlow();
    ModelUsesSparseConversationContractFlow();
    ModelCasualTurnUsesCompactRequestFlow();
    ModelDetailedTurnUsesScopedInstructionsFlow();
    OriginalWorldLoreFlow();
    GameKnowledgeFingerprintAndVisibilityFlow();
    GmReviewRoutingFlow();
    ThirdPartyTaskCanonicalizationFlow();
    ThirdPartyCapabilityMappingFlow();
    WorldOutcomeLedgerFlow();
    StaleResponseStripsThirdPartyActionFlow();
    CrossDayNewsProjectionFlow();
    LegacyWorldNormalizationFlow();
    Console.WriteLine("EchoWeave phase-one smoke passed.");
    return 0;
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}

void RegistryContainsOnlyKnownCapabilities()
{
    var registry = BuiltInCapabilityCatalog.Create();
    Assert(registry.Count == 16, $"Expected 16 capabilities, got {registry.Count}.");
    Assert(registry.TryGet("procurement.request.goods", out var procurement),
        "Procurement wrapper is missing.");
    Assert(procurement.Descriptor.RiskLevel == EchoWeaveRiskLevel.R3SensitiveNative,
        "Procurement risk should be R3.");
    Assert(!registry.TryGet("runtime.compile.patch", out _),
        "Unknown runtime capability must not exist.");
}

void ThirdPartyCapabilityMappingFlow()
{
    var registry = BuiltInCapabilityCatalog.Create();
    var valueExecutors = new GmExecutorRegistry();
    var unmapped = GmInterfaceCatalog.Executable()
        .Where(x => !registry.TryGet(x.Executor, out _) &&
                    !valueExecutors.TryGet(x.Executor, out _))
        .Select(x => $"{x.Key}->{x.Executor}")
        .ToList();
    Assert(unmapped.Count == 0,
        "可修改接口存在未绑定的执行器：" + string.Join("，", unmapped));
    var descriptor = GmInterfaceCatalog.Describe("capability.third_party.contact");
    Assert(descriptor is { Status: "implemented", CanModify: true },
        "第三方联系接口没有标记为可执行实现。");
    Assert(registry.TryGet(descriptor!.Executor, out var capability),
        "第三方接口目录的执行键没有绑定到已注册能力。");
    Assert(capability.Descriptor.CapabilityKey == "visit.request.third_party",
        "第三方接口映射到了错误的能力。");
}

void WorldOutcomeLedgerFlow()
{
    var ledgerRoot = Path.Combine(root, "world-outcome-ledger");
    var runtime = new EchoWeaveRuntime(ledgerRoot, "world-outcome");
    Assert(runtime.TryRecordWorldOutcome(
            5,
            "world.propose.event",
            "event-one",
            "每日事件生成并发布",
            EchoWeaveIntentStatus.Fulfilled),
        "跨天世界结果没有写入统一账簿。");
    Assert(!runtime.TryRecordWorldOutcome(
            5,
            "world.propose.event",
            "event-one",
            "每日事件生成并发布",
            EchoWeaveIntentStatus.Fulfilled),
        "相同世界结果被重复写入。 ");
    var entry = runtime.Ledger.Entries.Single();
    Assert(entry.Kind == "world_outcome" &&
           entry.CapabilityKey == "world.propose.event" &&
           entry.Data["subject_key"] == "event-one",
        "跨天回执没有保留能力和主体标识。");
}

void StaleResponseStripsThirdPartyActionFlow()
{
    var response = new ConversationResponse
    {
        ThirdPartyTask = new ThirdPartyTaskProposal
        {
            Explicit = true,
            Accepted = true,
            TargetCharacterKey = "target"
        }
    };
    WorldRuntime.StripUnsafeWorldActions(response);
    Assert(!response.ThirdPartyTask.Explicit && !response.ThirdPartyTask.Accepted,
        "过期回复仍保留第三方联系动作。");
}

void NamedCharacterPersistenceFlow()
{
    var state = new WorldState { WorldId = "named-character-world", CurrentDay = 3 };
    state.Characters["officer-lun"] = new CharacterState
    {
        CharacterKey = "officer-lun",
        DisplayName = "伦警官",
        OriginalIdentifier = "native-officer-lun",
        OriginalRealName = "伦警官",
        IsNamedSpecialCharacter = true,
        Relationship = 12,
        Memories = new List<MemoryRecord>
        {
            new() { Summary = "答应帮忙查看店铺周围的情况", CreatedDay = 3 }
        }
    };
    state.Characters["juan"] = new CharacterState
    {
        CharacterKey = "juan",
        DisplayName = "胡安",
        Memories = new List<MemoryRecord>
        {
            new() { Summary = "讨论了进货", CreatedDay = 3 }
        }
    };
    var nextDay = JsonSerializer.Deserialize<WorldState>(JsonSerializer.Serialize(state))!;
    JsonFileStore.NormalizeLoadedWorld(nextDay, state.WorldId);
    nextDay.CurrentDay++;
    var restored = JsonSerializer.Deserialize<WorldState>(JsonSerializer.Serialize(nextDay))!;
    JsonFileStore.NormalizeLoadedWorld(restored, state.WorldId);
    var officer = restored.Characters["officer-lun"];
    Assert(restored.CurrentDay == 4 &&
           officer.OriginalIdentifier == "native-officer-lun" &&
           officer.IsNamedSpecialCharacter && officer.Relationship == 12 &&
           officer.Memories.Single().Summary == "答应帮忙查看店铺周围的情况" &&
           restored.Characters["juan"].Memories.Single().Summary == "讨论了进货",
        "跨天读取丢失命名人物身份、关系或专属记忆。");

    var request = new ConversationRequest
    {
        CharacterKey = officer.CharacterKey,
        GameContext = new GameConversationContext
        {
            Character = new NativeCharacterContext
            {
                Key = officer.CharacterKey,
                DisplayName = officer.DisplayName,
                OriginalIdentifier = officer.OriginalIdentifier,
                OriginalRealName = officer.OriginalRealName,
                IsNamedSpecialCharacter = true
            }
        }
    };
    var guard = typeof(WorldRuntime).Assembly.GetType(
        "AIWorldPawnshop.Runtime.CharacterConsistencyGuard", throwOnError: true)!;
    var check = guard.GetMethod("IsConsistent",
        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
    var matching = new object?[] { request, new ConversationResponse { NpcReply = "我是伦警官。" }, null };
    var conflicting = new object?[] { request, new ConversationResponse { NpcReply = "我是胡安。" }, null };
    Assert((bool)check.Invoke(null, matching)! && !(bool)check.Invoke(null, conflicting)!,
        "命名人物身份护栏未拦截冒用其他原版人物的回复。");
}

void CrossDayNewsProjectionFlow()
{
    var state = new WorldState { CurrentDay = 4 };
    state.ActiveEvents.Add(new WorldEventState
    {
        EventKey = "active-event",
        StartDay = 3,
        DurationDays = 2
    });
    state.ActiveModifications.Add(new WorldModificationState
    {
        TargetKey = "medical",
        StartDay = 2,
        DurationDays = 0
    });
    Assert(NativeNewsProjectionRules.ShouldProject(state, new NewsRecord
        {
            Day = 3,
            SourceEventKey = "active-event"
        }),
        "仍在生效的跨天事件没有继续投影到原版报纸。");
    Assert(NativeNewsProjectionRules.ShouldProject(state, new NewsRecord
        {
            Day = 2,
            SourceEventKey = "gm:medical"
        }),
        "仍在生效的永久 GM 修正没有继续投影到原版报纸。");
    Assert(!NativeNewsProjectionRules.ShouldProject(state, new NewsRecord
        {
            Day = 2,
            SourceEventKey = "expired-event"
        }),
        "已结束的旧新闻仍被投影到当前报纸。");
}

void LegacyWorldNormalizationFlow()
{
    var dataRoot = Path.Combine(root, "legacy-world");
    var worlds = Path.Combine(dataRoot, "Worlds");
    Directory.CreateDirectory(worlds);
    File.WriteAllText(
        Path.Combine(worlds, "legacy.json"),
        @"{
          ""worldId"": ""legacy"",
          ""activeModifications"": null,
          ""thirdPartyTasks"": [{
            ""taskKey"": null,
            ""sourceCharacterKey"": null,
            ""targetCharacterKey"": null,
            ""targetNativeIdentifier"": null,
            ""targetDisplayName"": null,
            ""purpose"": null,
            ""status"": null,
            ""outcomeSummary"": null
          }],
          ""characters"": {
            ""old-character"": {
              ""characterKey"": ""old-character"",
              ""displayName"": ""旧人物"",
              ""originalIdentifier"": null,
              ""originalRealName"": null
            }
          }
        }");
    var state = JsonSerializer.Deserialize<WorldState>(
        File.ReadAllText(Path.Combine(worlds, "legacy.json")),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    JsonFileStore.NormalizeLoadedWorld(state, "legacy");
    Assert(state.ActiveModifications is not null &&
           state.ThirdPartyTasks.Count == 1 &&
           state.ThirdPartyTasks[0].TaskKey.Length > 0 &&
           state.ThirdPartyTasks[0].Status == "proposed" &&
           state.Characters["old-character"].OriginalIdentifier == string.Empty,
        "旧存档没有安全迁移阶段四、五新增字段。");
}

void DeduplicationKeyIsStable()
{
    var left = EchoWeaveKeys.Create(
        "world", "session", 3, "trade.adjust.selected",
        new Dictionary<string, string> { ["percent"] = "10", ["scope"] = "current_item" });
    var right = EchoWeaveKeys.Create(
        "world", "session", 3, "trade.adjust.selected",
        new Dictionary<string, string> { ["scope"] = "current_item", ["percent"] = "10" });
    var other = EchoWeaveKeys.Create(
        "world", "session", 4, "trade.adjust.selected",
        new Dictionary<string, string> { ["scope"] = "current_item", ["percent"] = "10" });
    Assert(left == right, "Argument order changed the deduplication key.");
    Assert(left != other, "A new turn reused the old deduplication key.");
}

void LedgerRejectsDuplicateAndSurvivesCorruptTail()
{
    var key = EchoWeaveKeys.Create("world", "session", 1, "visit.request.self");
    var ledger = new IntentLedger(root, "world");
    var proposed = new IntentRecord
    {
        IntentId = "intent-one",
        WorldId = "world",
        SessionId = "session",
        TurnId = 1,
        CapabilityKey = "visit.request.self",
        DeduplicationKey = key,
        SnapshotRevision = "7",
        Status = EchoWeaveIntentStatus.Approved,
        CreatedDay = 2
    };

    Assert(ledger.TryPropose(proposed, "intent_proposed", "安排回访"),
        "First intent was not appended.");
    Assert(!ledger.TryPropose(proposed, "intent_proposed", "重复回访"),
        "Duplicate intent was appended.");
    Assert(ledger.TryBeginExecution(key, 2, out _), "Execution did not start.");

    File.AppendAllText(Path.Combine(root, "EchoWeaveLedger.jsonl"), "{broken-tail");
    var recovered = new IntentLedger(root, "world").Recover();
    Assert(recovered.ValidEntryCount == 2, $"Expected two valid entries, got {recovered.ValidEntryCount}.");
    Assert(recovered.OpenIntents.Count == 1, "Executing intent was not recovered from the valid prefix.");
}

void StateRevisionIsMonotonic()
{
    var state = new WorldState { StateRevision = 4 };
    Assert(state.TouchRevision() == 5, "Revision did not advance to five.");
    Assert(state.TouchRevision() == 6, "Revision did not advance to six.");
}

void GmModificationFlow()
{
    var state = new WorldState
    {
        WorldId = "gm-world",
        CurrentDay = 3,
        StateRevision = 0
    };
    var initial = new WorldModificationProposal
    {
        TargetKey = "global",
        Field = "price_modifier",
        Operation = "add",
        Value = 0.2,
        DurationDays = 2,
        Title = "市场波动",
        Summary = "测试价格修正",
        Evidence = "测试证据",
        Confidence = 0.9,
        Goal = "change_prices",
        Path = "price_modifier"
    };
    var first = GmModificationEngine.Apply(state, new[] { initial });
    Assert(first.Applied && state.ActiveModifications.Count == 1,
        "有效 GM 修正没有落入活动状态。");
    Assert(state.StateRevision == 1 && state.News.Count == 1,
        "首次 GM 修正没有推进修订号和新闻。");
    Assert(state.News[0].Body.Contains("至第 4 天", StringComparison.Ordinal),
        "有限期限新闻没有显示正确到期日。");

    var update = new WorldModificationProposal
    {
        TargetKey = initial.TargetKey,
        Field = initial.Field,
        Value = 0.1,
        Operation = "add",
        DurationDays = 4,
        Title = initial.Title,
        Summary = "测试价格修正更新",
        Evidence = initial.Evidence,
        Confidence = initial.Confidence,
        Goal = initial.Goal,
        Path = initial.Path
    };
    var second = GmModificationEngine.Apply(state, new[] { update });
    Assert(second.Applied && Math.Abs(state.ActiveModifications[0].Value - 0.3) < 0.0001 &&
           state.ActiveModifications[0].DurationDays == 4 &&
           state.ActiveModifications[0].Summary == "测试价格修正更新",
        "同日同字段的 GM 更新没有被正确合并。");
    Assert(state.StateRevision == 2 && state.News.Count == 2,
        "GM 更新没有产生可验证的状态变更记录。");

    var invalid = new WorldModificationProposal
    {
        TargetKey = initial.TargetKey,
        Field = "unknown_field",
        Operation = initial.Operation,
        Value = initial.Value,
        DurationDays = initial.DurationDays,
        Title = initial.Title,
        Summary = initial.Summary,
        Evidence = initial.Evidence,
        Confidence = initial.Confidence
    };
    var rejected = GmModificationEngine.Apply(state, new[] { invalid });
    Assert(!rejected.Applied && state.ActiveModifications.Count == 1 &&
           state.StateRevision == 2,
        "未知 GM 字段不应修改状态。");

    var permanent = new WorldModificationProposal
    {
        TargetKey = initial.TargetKey,
        Field = initial.Field,
        Operation = "replace",
        Value = -0.1,
        DurationDays = 0,
        Title = initial.Title,
        Summary = "永久修正",
        Evidence = initial.Evidence,
        Confidence = initial.Confidence,
        Goal = initial.Goal,
        Path = initial.Path
    };
    GmModificationEngine.Apply(state, new[] { permanent });
    Assert(state.ActiveModifications[0].DurationDays == 0 &&
           state.News.Last().Body.Contains("影响：price_modifier -0.1，永久", StringComparison.Ordinal),
        "永久 GM 修正的新闻没有标明永久期限。");
}

void GmGoalPathSafetyFlow()
{
    var state = new WorldState { CurrentDay = 1 };
    var unsupported = new WorldModificationProposal
    {
        TargetKey = "global",
        Field = "price_modifier",
        Operation = "add",
        Value = -0.1,
        DurationDays = 1,
        Title = "治安变化",
        Summary = "测试未接通路径",
        Evidence = "测试证据",
        Confidence = 0.9,
        Goal = "reduce_store_loss",
        Path = "burglary_attempt_rate"
    };
    var rejected = GmModificationEngine.Apply(state, new[] { unsupported });
    Assert(!rejected.Applied && state.ActiveModifications.Count == 0,
        "未接通的目标路径不应伪装成可执行世界修正。");
    Assert(GmInterfaceCatalog.FindExecutableGoalPaths("change_prices")
               .Any(x => x.Path.Equals("price_modifier", StringComparison.OrdinalIgnoreCase)),
        "已接通的价格目标路径没有出现在可执行目录。");
    var mismatched = new WorldModificationProposal
    {
        TargetKey = "global",
        Field = "supply_modifier",
        Operation = "add",
        Value = 0.1,
        DurationDays = 1,
        Title = "路径字段错配",
        Summary = "测试错配",
        Evidence = "测试证据",
        Confidence = 0.9,
        Goal = "change_prices",
        Path = "price_modifier"
    };
    Assert(!GmModificationEngine.Apply(state, new[] { mismatched }).Applied,
        "目标路径和字段错配时不应写入世界修正。");
}

void DailyEventFlow()
{
    var content = LocalContent.CreateDefaults();
    var engine = new LocalFallbackEngine(content);
    var state = new WorldState
    {
        WorldId = "event-world",
        WorldSeed = 77,
        RulesVersion = "0.3"
    };
    var first = engine.BuildDailyEvents(state, 5);
    var second = engine.BuildDailyEvents(state, 5);
    Assert(first.Count > 0 && first.Count == second.Count,
        "每日事件没有生成。");
    Assert(first.Select(x => x.EventKey).SequenceEqual(second.Select(x => x.EventKey)),
        "同一世界同一天的每日事件不稳定。");
    Assert(first.All(x => x.StartDay == 5 && x.DurationDays is >= 1 and <= 3),
        "每日事件的日期或期限越界。");
}

void LocalFallbackConversationFlow()
{
    var engine = new LocalFallbackEngine(LocalContent.CreateDefaults());
    var request = new ConversationRequest
    {
        ConversationId = "fallback-session",
        CharacterKey = "client-one",
        CharacterDisplayName = "测试顾客",
        Scene = "store",
        GameContext = new GameConversationContext
        {
            Character = new NativeCharacterContext
            {
                Key = "client-one",
                OriginalIdentifier = "client-one",
                DisplayName = "测试顾客"
            },
            Trade = new NativeTradeContext
            {
                ClientCarriedItems = new List<NativeItemContext>
                {
                    new() { RuntimeKey = 101, ItemKey = "water_small", DisplayName = "小瓶装水", Owner = "client" }
                },
                PlayerOfferedItems = new List<NativeItemContext>
                {
                    new() { RuntimeKey = 202, ItemKey = "paper", DisplayName = "报纸", Owner = "player" }
                }
            }
        }
    };

    request.PlayerText = "明天再来一趟吧";
    var returnReply = engine.Reply(request);
    Assert(returnReply.ReturnVisitProposal.Explicit && returnReply.ReturnVisitProposal.Accepted,
        "本地降级没有识别明确的未来回访邀请。");

    request.PlayerText = "你身上还有什么，拿出来我看看";
    var carriedReply = engine.Reply(request);
    Assert(carriedReply.CarriedInventoryProposal.Explicit && carriedReply.CarriedInventoryProposal.Accepted,
        "本地降级没有识别顾客随身物展示请求。");

    request.PlayerText = "用报纸换你的小瓶装水";
    var barterReply = engine.Reply(request);
    Assert(barterReply.BarterProposal.Explicit && barterReply.BarterProposal.Accepted &&
           barterReply.BarterProposal.ClientItemRuntimeKey == 101 &&
           barterReply.BarterProposal.RequiredPlayerItemKey == "paper",
        "本地降级没有按归属选择交换双方物品。");

    request.Scene = "phone";
    request.PlayerText = "明天再来一趟吧";
    var phoneReply = engine.Reply(request);
    Assert(!phoneReply.ReturnVisitProposal.Explicit && !phoneReply.BarterProposal.Explicit,
        "电话场景错误地产生店内动作提案。");
}

void ActionRefusalMustWinFlow()
{
    var request = new ConversationRequest
    {
        Scene = "store",
        CharacterKey = "client-one",
        PlayerText = "明天再来，身上的东西拿出来看看，再用报纸换水并补我5信用点",
        GameContext = new GameConversationContext
        {
            Character = new NativeCharacterContext
            {
                Key = "client-one",
                OriginalIdentifier = "client-one"
            }
        }
    };
    var rejected = new ConversationResponse
    {
        NpcReply = "不行，我不换，也不会给你信用点。",
        TradeProposal = new TradeProposal { Explicit = true },
        ProcurementProposal = new ProcurementProposal { Explicit = true, Accepted = true },
        ReturnVisitProposal = new ReturnVisitProposal { Explicit = true, Accepted = true },
        CarriedInventoryProposal = new CarriedInventoryProposal { Explicit = true, Accepted = true },
        BarterProposal = new BarterProposal { Explicit = true, Accepted = true },
        ThirdPartyTask = new ThirdPartyTaskProposal
        {
            Explicit = true,
            Accepted = true,
            Decision = "accept"
        },
        WorldModifications = new List<WorldModificationProposal> { new() },
        WalletTransfer = new WalletTransferProposal
        {
            Explicit = true,
            Accepted = true,
            Amount = 5,
            Source = "client_budget"
        }
    };
    var resolver = typeof(WorldRuntime).Assembly.GetType(
        "AIWorldPawnshop.Runtime.ConversationActionResolver", throwOnError: true)!;
    var acceptsReply = resolver.GetMethod("IsAcceptedReply",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    var refusesReply = resolver.GetMethod("IsRefusalReply",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    Assert(!(bool)acceptsReply.Invoke(null, new object[] { rejected.NpcReply })! &&
           !(bool)acceptsReply.Invoke(null, new object[] { "好像没法来，还是算了。" })! &&
           !(bool)acceptsReply.Invoke(null, new object[] { "我答应不了。" })! &&
           !(bool)acceptsReply.Invoke(null, new object[] { "可以考虑一下。" })!,
        "拒绝语被误判为同意，可能触发错误交易。");
    Assert((bool)acceptsReply.Invoke(null, new object[] { "好，明天我来。" })! &&
           (bool)acceptsReply.Invoke(null, new object[] { "我不想走，但可以换。" })! &&
           !(bool)refusesReply.Invoke(null, new object[] { "我不想走，但可以换。" })!,
        "拒绝离开却同意交易的句子被整体误判为拒绝。");
    var normalize = resolver.GetMethod("Normalize",
        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
    normalize.Invoke(null, new object[] { request, rejected });
    Assert(!rejected.TradeProposal.Explicit &&
           !rejected.ProcurementProposal.Accepted &&
           !rejected.ReturnVisitProposal.Accepted &&
           !rejected.CarriedInventoryProposal.Accepted &&
           !rejected.BarterProposal.Accepted &&
           !rejected.WalletTransfer.Accepted &&
           !rejected.ThirdPartyTask.Explicit &&
           rejected.WorldModifications.Count == 0,
        "人物已明确拒绝，结构化接受字段仍会触发动作。");

    request.PlayerText = "明天再来一趟吧";
    var ambiguous = new ConversationResponse { NpcReply = "好像没法来，还是算了。" };
    normalize.Invoke(null, new object[] { request, ambiguous });
    Assert(!ambiguous.ReturnVisitProposal.Accepted,
        "包含“好”的拒绝语被误判为同意回访。");
}

void ModelProtocolCacheFlow()
{
    var handler = new ProtocolFallbackHandler();
    using var http = new HttpClient(handler);
    var provider = new OpenAiCompatibleProvider(http, new ModelProfile
    {
        AllowNetwork = true,
        BaseUrl = "http://mock.local/v1",
        Model = "gpt-5-mini",
        Protocol = "auto",
        TimeoutMs = 5000,
        MaxOutputTokens = 500
    });
    var request = new ConversationRequest
    {
        ConversationId = "model-session",
        TurnId = 1,
        CharacterKey = "client-one",
        CharacterDisplayName = "测试顾客",
        PlayerText = "请联系那位已知人物。",
        GameContext = new GameConversationContext
        {
            GmKnowledge = new List<string> { "GM_ONLY_KNOWLEDGE_MARKER" },
            KnownCharacters = new List<KnownCharacterContext>
            {
                new()
                {
                    Key = "known-target",
                    DisplayName = "KNOWN_CHARACTER_MARKER",
                    NativeIdentifier = "native-known-target",
                    CanReturn = true
                }
            }
        }
    };

    var first = provider.CompleteAsync(request, CancellationToken.None).GetAwaiter().GetResult();
    Assert(first is not null && handler.RequestPaths.Count == 2 &&
           handler.RequestPaths[0].EndsWith("/responses", StringComparison.Ordinal) &&
           handler.RequestPaths[1].EndsWith("/chat/completions", StringComparison.Ordinal) &&
           handler.RequestBodies[0].Contains("GM_ONLY_KNOWLEDGE_MARKER", StringComparison.Ordinal) == false &&
           handler.RequestBodies[0].Contains("KNOWN_CHARACTER_MARKER", StringComparison.Ordinal),
        "自动协议首轮没有按预期从 Responses 回退到 Chat Completions。");

    request.TurnId = 2;
    var second = provider.CompleteAsync(request, CancellationToken.None).GetAwaiter().GetResult();
    Assert(second is not null && handler.RequestPaths.Count == 3 &&
           handler.RequestPaths[2].EndsWith("/chat/completions", StringComparison.Ordinal),
        "已确定可用协议后仍重复尝试失败协议，无法保证自由聊天速度。");
}

void CodexClientRestrictionFlow()
{
    using var http = new HttpClient(new CodexRestrictionHandler());
    var result = OpenAiCompatibleProvider.TestConnectionAsync(http, new ModelProfile
    {
        AllowNetwork = true,
        BaseUrl = "http://mock.local/v1",
        Model = "gpt-5.5",
        Protocol = "auto",
        TimeoutMs = 5000,
        MaxOutputTokens = 500
    }, CancellationToken.None).GetAwaiter().GetResult();

    Assert(!result.Succeeded && result.StatusCode == 403 &&
           result.Message.Contains("只允许官方 Codex 客户端", StringComparison.Ordinal) &&
           !result.Message.Contains("密钥未通过认证", StringComparison.Ordinal),
        "Codex 客户端限制没有被识别为明确的账号策略错误。");
}

void ModelTransientFailureDoesNotDoubleWaitFlow()
{
    var handler = new TransientFailureHandler();
    using var http = new HttpClient(handler);
    var provider = new OpenAiCompatibleProvider(http, new ModelProfile
    {
        AllowNetwork = true,
        BaseUrl = "http://mock.local/v1",
        Model = "gpt-5-mini",
        Protocol = "auto",
        TimeoutMs = 5000,
        MaxOutputTokens = 500
    });

    var response = provider.CompleteAsync(
            new ConversationRequest { PlayerText = "你好" },
            CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    Assert(response is null && handler.RequestCount == 1,
        "上游临时故障不应触发另一协议并让玩家再等一次。 ");
}

void ModelUsesSparseConversationContractFlow()
{
    var handler = new ProtocolFallbackHandler();
    using var http = new HttpClient(handler);
    var provider = new OpenAiCompatibleProvider(http, new ModelProfile
    {
        AllowNetwork = true,
        BaseUrl = "http://mock.local/v1",
        Model = "gpt-5-mini",
        Protocol = "chat_completions",
        TimeoutMs = 5000,
        MaxOutputTokens = 500
    });

    var response = provider.CompleteAsync(
            new ConversationRequest { PlayerText = "你好" },
            CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    var body = handler.RequestBodies.Single();
    using var document = JsonDocument.Parse(body);
    var prompt = document.RootElement
        .GetProperty("messages")[0]
        .GetProperty("content")
        .GetString() ?? string.Empty;
    Assert(response is not null &&
           prompt.Contains("未发生的行为字段必须省略", StringComparison.Ordinal) &&
           !prompt.Contains("没有有效委托时必须返回", StringComparison.Ordinal),
        "人物请求仍强迫普通闲聊生成全部空提案，结构化输出负担没有降下来。 ");
}

void ModelCasualTurnUsesCompactRequestFlow()
{
    var handler = new ProtocolFallbackHandler();
    using var http = new HttpClient(handler);
    var provider = new OpenAiCompatibleProvider(http, new ModelProfile
    {
        AllowNetwork = true,
        BaseUrl = "http://mock.local/v1",
        Model = "gpt-5-mini",
        Protocol = "chat_completions",
        TimeoutMs = 5000,
        MaxOutputTokens = 500
    });

    var request = new ConversationRequest
    {
        PlayerText = "今天天气怎么样？",
        GameContext = new GameConversationContext
        {
            OriginalCharacterProfile = CreateLoreProfile(),
            RelevantGameKnowledge = new List<string> { "VISIBLE_CATALOG_MARKER" },
            GmKnowledge = new List<string> { "GM_ONLY_MARKER" },
            KnownCharacters = new List<KnownCharacterContext>
            {
                new() { Key = "LARGE_DIRECTORY_MARKER", DisplayName = "无关人物" }
            },
            Procurement = new ProcurementConversationContext
            {
                CategoryAccess = new List<ProcurementCategoryAccessContext>
                {
                    new() { Category = "LARGE_PROCUREMENT_MARKER" }
                }
            }
        }
    };
    var response = provider.CompleteAsync(request, CancellationToken.None)
        .GetAwaiter().GetResult();

    using var document = JsonDocument.Parse(handler.RequestBodies.Single());
    var messages = document.RootElement.GetProperty("messages");
    var prompt = messages[0].GetProperty("content").GetString() ?? string.Empty;
    var payload = messages[1].GetProperty("content").GetString() ?? string.Empty;
    var maxTokens = document.RootElement.GetProperty("max_tokens").GetInt32();
    Assert(response is not null &&
           prompt.Contains("快速人物回复", StringComparison.Ordinal) &&
           !payload.Contains("LARGE_DIRECTORY_MARKER", StringComparison.Ordinal) &&
           !payload.Contains("LARGE_PROCUREMENT_MARKER", StringComparison.Ordinal) &&
           !payload.Contains("GM_ONLY_MARKER", StringComparison.Ordinal) &&
           payload.Contains("VISIBLE_CATALOG_MARKER", StringComparison.Ordinal) &&
           payload.Contains("world_background", StringComparison.Ordinal) &&
           payload.Contains("faction_background", StringComparison.Ordinal) &&
           maxTokens <= 220,
        "普通闲聊没有使用精简提示、上下文和输出预算。 ");
}

void ModelDetailedTurnUsesScopedInstructionsFlow()
{
    var handler = new ProtocolFallbackHandler();
    using var http = new HttpClient(handler);
    var provider = new OpenAiCompatibleProvider(http, new ModelProfile
    {
        AllowNetwork = true,
        BaseUrl = "http://mock.local/v1",
        Model = "gpt-5-mini",
        Protocol = "chat_completions",
        TimeoutMs = 5000,
        MaxOutputTokens = 500
    });

    var response = provider.CompleteAsync(
            new ConversationRequest
            {
                PlayerText = "我用报纸换你的小瓶装水，你再补五信用点，行吗？",
                GameContext = new GameConversationContext
                {
                    OriginalCharacterProfile = CreateLoreProfile(),
                    RelevantGameKnowledge = new List<string> { "VISIBLE_SCOPED_CATALOG_MARKER" },
                    Procurement = new ProcurementConversationContext
                    {
                        RarityBasis = "IRRELEVANT_PROCUREMENT_MARKER"
                    },
                    KnownCharacters = new List<KnownCharacterContext>
                    {
                        new() { Key = "IRRELEVANT_DIRECTORY_MARKER" }
                    }
                }
            },
            CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    using var document = JsonDocument.Parse(handler.RequestBodies.Single());
    var prompt = document.RootElement
        .GetProperty("messages")[0]
        .GetProperty("content")
        .GetString() ?? string.Empty;
    var payload = document.RootElement
        .GetProperty("messages")[1]
        .GetProperty("content")
        .GetString() ?? string.Empty;
    var maxTokens = document.RootElement.GetProperty("max_tokens").GetInt32();
    Assert(response is not null &&
           prompt.Contains("混合交换规则", StringComparison.Ordinal) &&
           prompt.Contains("补付规则", StringComparison.Ordinal) &&
           !prompt.Contains("未来找货规则", StringComparison.Ordinal) &&
           !payload.Contains("IRRELEVANT_PROCUREMENT_MARKER", StringComparison.Ordinal) &&
           !payload.Contains("IRRELEVANT_DIRECTORY_MARKER", StringComparison.Ordinal) &&
           payload.Contains("VISIBLE_SCOPED_CATALOG_MARKER", StringComparison.Ordinal) &&
           payload.Contains("world_background", StringComparison.Ordinal) &&
           payload.Contains("faction_background", StringComparison.Ordinal) &&
           maxTokens <= 360,
        "明确行为轮次仍携带无关能力说明或过高输出预算。 ");
}

OriginalCharacterProfileContext CreateLoreProfile()
{
    var profile = new OriginalCharacterProfileContext();
    OriginalWorldLoreCatalog.AddPublicKnowledge(profile);
    return profile;
}

void OriginalWorldLoreFlow()
{
    var profile = CreateLoreProfile();
    Assert(profile.WorldBackground.Count == 4 && profile.FactionBackground.Count == 6,
        "原版公开世界与群体资料未进入人物档案。 ");
    Assert(profile.FactionBackground.Any(x => x.Contains("顾客来源分类", StringComparison.Ordinal)) &&
           profile.FactionBackground.Any(x => x.Contains("革命军", StringComparison.Ordinal)) &&
           profile.KnowledgeBoundaries.Any(x => x.Contains("不代表你亲历", StringComparison.Ordinal)),
        "群体分类或人物知识边界缺失。 ");
    Assert(!JsonSerializer.Serialize(profile).Contains("脑部植入物", StringComparison.Ordinal),
        "剧情敏感资料泄露到普通人物背景。 ");
    var gmKnowledge = new List<string>();
    OriginalWorldLoreCatalog.AddGmKnowledge(gmKnowledge);
    Assert(gmKnowledge.Any(x => x.Contains("脑部植入物", StringComparison.Ordinal)),
        "GM 缺少原版改造体资料。 ");
}

void GameKnowledgeFingerprintAndVisibilityFlow()
{
    var gameRoot = Path.Combine(root, "knowledge-game");
    var dataRoot = Path.Combine(gameRoot, "Probably Stolen_Data");
    Directory.CreateDirectory(dataRoot);
    var assembly = Path.Combine(gameRoot, "GameAssembly.dll");
    File.WriteAllText(assembly, "game-content-a");
    var first = GameSourceFingerprint.Capture(gameRoot);
    File.SetLastWriteTimeUtc(assembly, DateTime.UtcNow.AddMinutes(-2));
    var sameContent = GameSourceFingerprint.Capture(gameRoot, first);
    Assert(first.Fingerprint == sameContent.Fingerprint,
        "资源时间变化但内容相同时不应触发重扫。 ");
    File.WriteAllText(assembly, "game-content-b");
    var changed = GameSourceFingerprint.Capture(gameRoot, sameContent);
    Assert(first.Fingerprint != changed.Fingerprint,
        "原版资源内容改变后未触发新指纹。 ");

    changed.Items.Add(new GameKnowledgeItem
    {
        Key = "water", Name = "瓶装水", Description = "可饮用的水",
        BaseValue = 10, ObservedByPlayer = true
    });
    changed.Items.Add(new GameKnowledgeItem
    {
        Key = "secret", Name = "秘密机械", Description = "尚未发现的剧情物品",
        BaseValue = 100, ObservedByPlayer = false
    });
    var context = new GameConversationContext();
    GameKnowledgeSearch.Enrich(changed, context, "瓶装水和秘密机械有什么区别？");
    Assert(
           context.RelevantGameKnowledge.Any(x => x.Contains("瓶装水", StringComparison.Ordinal)) &&
           !context.RelevantGameKnowledge.Any(x => x.Contains("秘密机械", StringComparison.Ordinal)) &&
           context.GmKnowledge.Any(x => x.Contains("秘密机械", StringComparison.Ordinal)),
        "目录检索未正确隔离玩家已见和未见的资料。 ");

    var visibility = new GameConversationContext
    {
        Trade = new NativeTradeContext
        {
            Item = new NativeItemContext { ItemKey = "counter_item" },
            ClientCarriedItems = new List<NativeItemContext>
            {
                new() { ItemKey = "hidden_bag_item" }
            },
            PlayerOfferedItems = new List<NativeItemContext>
            {
                new() { ItemKey = "player_counter_item" }
            }
        }
    };
    var visibleKeys = GameKnowledgeSearch.VisibleItems(visibility).Select(x => x.ItemKey).ToList();
    Assert(visibleKeys.SequenceEqual(new[] { "counter_item", "player_counter_item" }),
        "顾客未展示的随身物品不能自动标记为玩家已见。 ");
    Assert(!GameKnowledgeSearch.CanRecordPerson(new NativeCharacterContext
           { OriginalIdentifier = "unintroduced", IsIntroduced = false }) &&
           GameKnowledgeSearch.CanRecordPerson(new NativeCharacterContext
           { OriginalIdentifier = "introduced", IsIntroduced = true }),
        "未介绍的人物身份不能进入可检索目录。 ");
}

void GmReviewRoutingFlow()
{
    var method = typeof(WorldRuntime).GetMethod(
        "ShouldRunGmReview",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
    Assert(method is not null, "GM 审核路由方法缺失。");

    var casual = new ConversationRequest { PlayerText = "最近天气不错，聊点轻松的。" };
    var casualResponse = new ConversationResponse { NpcReply = "是啊，市场今天也挺平静。" };
    var casualNeedsGm = (bool)method!.Invoke(null, new object[] { casual, casualResponse })!;
    Assert(!casualNeedsGm, "普通自由聊天不应额外串行调用 GM。");

    var worldChange = new ConversationRequest { PlayerText = "你能让人这几天多巡逻店附近吗？" };
    var worldResponse = new ConversationResponse { NpcReply = "行，我会安排人手多巡逻几趟。" };
    var worldNeedsGm = (bool)method.Invoke(null, new object[] { worldChange, worldResponse })!;
    Assert(worldNeedsGm, "涉及世界状态因果的自由聊天必须进入 GM 审核。");

    var marketQuestion = new ConversationRequest { PlayerText = "最近市场怎么样？" };
    var marketAnswer = new ConversationResponse { NpcReply = "看起来还算平稳。" };
    var questionNeedsGm = (bool)method.Invoke(null, new object[] { marketQuestion, marketAnswer })!;
    Assert(!questionNeedsGm, "询问世界话题不应被误判为已承诺修改并串行调用 GM。 ");

    var refusedPatrol = new ConversationResponse { NpcReply = "这事我安排不了。" };
    var refusalNeedsGm = (bool)method.Invoke(null, new object[] { worldChange, refusedPatrol })!;
    Assert(!refusalNeedsGm, "人物已拒绝世界请求时不应继续调用 GM。 ");

    var unexpectedChange = new ConversationRequest { PlayerText = "能让钟声每天多响一次吗？" };
    var unexpectedAccepted = new ConversationResponse
    {
        NpcReply = "可以，我会让钟声每天多响一次。",
        RequiresGmReview = true
    };
    var unexpectedNeedsGm = (bool)method.Invoke(
        null,
        new object[] { unexpectedChange, unexpectedAccepted })!;
    Assert(unexpectedNeedsGm,
        "没有落入已知关键词、但人物明确答应改变世界状态的请求仍应进入 GM。 ");
}

void ThirdPartyTaskCanonicalizationFlow()
{
    var state = new WorldState();
    state.Characters["target-key"] = new CharacterState
    {
        CharacterKey = "target-key",
        DisplayName = "目标人物",
        OriginalIdentifier = "native-target",
        CanReturn = true
    };
    var proposal = new ThirdPartyTaskProposal
    {
        Explicit = true,
        Accepted = true,
        TargetCharacterKey = string.Empty,
        TargetDisplayName = "目标人物",
        Purpose = "转告一条消息",
        DueInDays = 99
    };
    Assert(WorldRuntime.TryResolveThirdPartyTarget(
        state,
        proposal,
        out var targetKey,
        out var targetName) &&
           targetKey == "target-key" && targetName == "目标人物",
        "第三方任务没有规范显示名到规范目标 key。");
    Assert(WorldRuntime.TryResolveThirdPartyNativeIdentifier(
        state,
        proposal,
        out _,
        out _,
        out var nativeIdentifier) &&
           nativeIdentifier == "native-target",
        "第三方任务没有保留原版人物标识，不能安全进入原版来客队列。");
    Assert(Math.Clamp(proposal.DueInDays, 1, 7) == 7,
        "第三方任务期限没有应用 1 至 7 天边界。");

    state.Characters["duplicate-target"] = new CharacterState
    {
        CharacterKey = "duplicate-target",
        DisplayName = "目标人物",
        OriginalIdentifier = "native-duplicate",
        CanReturn = true
    };
    Assert(!WorldRuntime.TryResolveThirdPartyTarget(
            state,
            new ThirdPartyTaskProposal { TargetDisplayName = "目标人物" },
            out _,
            out _),
        "重名人物被任意选中，第三方联系必须要求唯一目标。");

    var task = new ThirdPartyTaskState { Status = "accepted" };
    Assert(!WorldRuntime.CanResolveThirdPartyDecision(task, "accept"),
        "目标尚未到场时就提前完成了二次决定。");
    task.Status = "arrived";
    Assert(WorldRuntime.CanResolveThirdPartyDecision(task, "reject"),
        "目标到场后的明确拒绝没有进入二次决定。");

    var candidate = new ThirdPartyTaskProposal
    {
        Explicit = true,
        Accepted = true,
        TargetCharacterKey = "target-key",
        TargetDisplayName = "目标人物",
        Purpose = "转告一条消息",
        DueInDays = 2
    };
    Assert(WorldRuntime.IsSameThirdPartyProposal(candidate, new ThirdPartyTaskProposal
        {
            Explicit = true,
            Accepted = true,
            TargetCharacterKey = "target-key",
            TargetDisplayName = "目标人物",
            Purpose = "转告一条消息",
            DueInDays = 2
        }),
        "世界监督没有保留人物原始第三方提案。");
    Assert(!WorldRuntime.IsSameThirdPartyProposal(candidate, new ThirdPartyTaskProposal
        {
            Explicit = true,
            Accepted = true,
            TargetCharacterKey = "duplicate-target",
            Purpose = "转告一条消息",
            DueInDays = 2
        }),
        "世界监督可以替换人物选定的第三方目标。");
}

void RuntimeRecoversUnfinishedConversation()
{
    var runtimeRoot = Path.Combine(root, "runtime-recovery");
    var state = new WorldState { WorldId = "recover-world", StateRevision = 9, CurrentDay = 4 };
    var request = new ConversationRequest
    {
        ConversationId = "conversation-one",
        TurnId = 2,
        CharacterKey = "client-one",
        CharacterDisplayName = "测试顾客",
        Scene = "store",
        PlayerText = "明天再来",
        GameContext = new GameConversationContext
        {
            Character = new NativeCharacterContext
            {
                Key = "client-one",
                DisplayName = "测试顾客"
            }
        }
    };

    var first = new EchoWeaveRuntime(runtimeRoot, state.WorldId);
    var snapshot = first.Capture(request, state);
    Assert(first.IsCurrent(snapshot, state), "Fresh snapshot should match the world revision.");
    Assert(first.TryRecordConversation(snapshot, "测试未闭合对话"),
        "Conversation execution did not start.");
    state.TouchRevision();
    Assert(!first.IsCurrent(snapshot, state), "Advanced state did not invalidate the old snapshot.");

    var restarted = new EchoWeaveRuntime(runtimeRoot, state.WorldId);
    var report = restarted.Recover(state);
    Assert(report.RecoveredCount == 1, "Restart did not find the unfinished conversation.");
    var afterRecovery = new IntentLedger(runtimeRoot, state.WorldId).Recover();
    Assert(afterRecovery.OpenIntents.Count == 0, "Recovered execution remained open.");
}

void DuplicateConversationReplayFlow()
{
    var runtimeRoot = Path.Combine(root, "duplicate-conversation");
    var state = new WorldState { WorldId = "duplicate-world", CurrentDay = 2 };
    var request = new ConversationRequest
    {
        ConversationId = "conversation-one",
        TurnId = 1,
        CharacterKey = "client-one",
        Scene = "store"
    };
    var runtime = new EchoWeaveRuntime(runtimeRoot, state.WorldId);
    var snapshot = runtime.Capture(request, state);
    Assert(runtime.TryRecordConversation(snapshot, "首次回复"),
        "首次回复未能登记执行。");
    Assert(runtime.CompleteConversation(snapshot, EchoWeaveIntentStatus.Fulfilled, "已执行"),
        "首次回复未能完成执行。");

    var entryCount = runtime.Ledger.Entries.Count;
    Assert(!runtime.TryRecordConversation(snapshot, "重复回复") &&
           runtime.Ledger.Entries.Count == entryCount,
        "相同回合重复返回时重新登记了执行。");
    var restarted = new EchoWeaveRuntime(runtimeRoot, state.WorldId);
    Assert(!restarted.TryRecordConversation(snapshot, "重启后重复回复") &&
           restarted.Ledger.Entries.Count == entryCount,
        "重启后相同回合重新登记了执行。");
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

sealed class ProtocolFallbackHandler : HttpMessageHandler
{
    public List<string> RequestPaths { get; } = new();
    public List<string> RequestBodies { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestPaths.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
        RequestBodies.Add(request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken));
        if (request.RequestUri?.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal) == true)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(
                    "{\"error\":{\"message\":\"unsupported\"}}",
                    Encoding.UTF8,
                    "application/json")
            };
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"choices\":[{\"message\":{\"content\":\"{\\\"npc_reply\\\":\\\"好的。\\\"}\"}}]}",
                Encoding.UTF8,
                "application/json")
        };
    }
}

sealed class TransientFailureHandler : HttpMessageHandler
{
    public int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestCount++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent(
                "{\"error\":{\"message\":\"temporary upstream failure\"}}",
                Encoding.UTF8,
                "application/json")
        });
    }
}

sealed class CodexRestrictionHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(
                "{\"error\":{\"message\":\"This account only allows Codex official clients\",\"type\":\"forbidden_error\"}}",
                Encoding.UTF8,
                "application/json")
        });
    }
}
