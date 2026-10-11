using AIWorldPawnshop.Capabilities;
using AIWorldPawnshop.Models;
using AIWorldPawnshop.Models.EchoWeave;
using AIWorldPawnshop.Orchestration;

var root = Path.Combine(Path.GetTempPath(), "AIWorldPawnshop-EchoWeaveSmoke", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

try
{
    RegistryContainsOnlyKnownCapabilities();
    DeduplicationKeyIsStable();
    LedgerRejectsDuplicateAndSurvivesCorruptTail();
    RuntimeRecoversUnfinishedConversation();
    StateRevisionIsMonotonic();
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
