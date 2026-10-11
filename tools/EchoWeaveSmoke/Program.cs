using AIWorldPawnshop.Capabilities;
using AIWorldPawnshop.Models;
using AIWorldPawnshop.Models.EchoWeave;
using AIWorldPawnshop.Orchestration;
using AIWorldPawnshop.GM;
using AIWorldPawnshop.Model;
using AIWorldPawnshop.Runtime;
using System.Net;
using System.Text;

var root = Path.Combine(Path.GetTempPath(), "AIWorldPawnshop-EchoWeaveSmoke", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

try
{
    RegistryContainsOnlyKnownCapabilities();
    DeduplicationKeyIsStable();
    LedgerRejectsDuplicateAndSurvivesCorruptTail();
    RuntimeRecoversUnfinishedConversation();
    StateRevisionIsMonotonic();
    GmModificationFlow();
    DailyEventFlow();
    LocalFallbackConversationFlow();
    ModelProtocolCacheFlow();
    GmReviewRoutingFlow();
    ThirdPartyTaskCanonicalizationFlow();
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
        PlayerText = "今天天气怎么样？",
        GameContext = new GameConversationContext
        {
            GmKnowledge = new List<string> { "GM_ONLY_KNOWLEDGE_MARKER" }
        }
    };

    var first = provider.CompleteAsync(request, CancellationToken.None).GetAwaiter().GetResult();
    Assert(first is not null && handler.RequestPaths.Count == 2 &&
           handler.RequestPaths[0].EndsWith("/responses", StringComparison.Ordinal) &&
           handler.RequestPaths[1].EndsWith("/chat/completions", StringComparison.Ordinal) &&
           handler.RequestBodies[0].Contains("GM_ONLY_KNOWLEDGE_MARKER", StringComparison.Ordinal) == false,
        "自动协议首轮没有按预期从 Responses 回退到 Chat Completions。");

    request.TurnId = 2;
    var second = provider.CompleteAsync(request, CancellationToken.None).GetAwaiter().GetResult();
    Assert(second is not null && handler.RequestPaths.Count == 3 &&
           handler.RequestPaths[2].EndsWith("/chat/completions", StringComparison.Ordinal),
        "已确定可用协议后仍重复尝试失败协议，无法保证自由聊天速度。");
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

    var worldChange = new ConversationRequest { PlayerText = "警察说这几天会多巡逻店附近。" };
    var worldResponse = new ConversationResponse { NpcReply = "那偷窃风险会低些。" };
    var worldNeedsGm = (bool)method.Invoke(null, new object[] { worldChange, worldResponse })!;
    Assert(worldNeedsGm, "涉及世界状态因果的自由聊天必须进入 GM 审核。");
}

void ThirdPartyTaskCanonicalizationFlow()
{
    var state = new WorldState();
    state.Characters["target-key"] = new CharacterState
    {
        CharacterKey = "target-key",
        DisplayName = "目标人物"
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
    Assert(Math.Clamp(proposal.DueInDays, 1, 7) == 7,
        "第三方任务期限没有应用 1 至 7 天边界。");
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
