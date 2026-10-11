using System.Text.Json.Serialization;

namespace AIWorldPawnshop.Models.EchoWeave;

/// <summary>
/// Risk classes used by the local validator. The numeric order is intentional:
/// a lower value is less capable of changing the game.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EchoWeaveRiskLevel
{
    R0ReadOnly,
    R1ExtensionState,
    R2RecoverableNative,
    R3SensitiveNative,
    R4Forbidden
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EchoWeaveSafeTiming
{
    AfterDialogue,
    BeforeClientLeaves,
    TradeUiReady,
    PhoneOptionsReady,
    EndOfDay,
    BeforeStoreOpens,
    ClientCreated,
    NativeTransactionConfirmed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EchoWeaveIntentStatus
{
    Proposed,
    ClarificationRequired,
    Rejected,
    CapabilityPending,
    Approved,
    Scheduled,
    Ready,
    Executing,
    Fulfilled,
    RetryableFailure,
    Conflict,
    PermanentFailure,
    Compensating,
    Compensated,
    Expired,
    Superseded
}

public sealed class EchoWeaveSnapshot
{
    public string WorldId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string ConversationId { get; set; } = string.Empty;
    public int TurnId { get; set; }
    public long StateRevision { get; set; }
    public int CurrentDay { get; set; }
    public string Scene { get; set; } = string.Empty;
    public string CharacterKey { get; set; } = string.Empty;
    public string CharacterDisplayName { get; set; } = string.Empty;
    public string FactionKey { get; set; } = string.Empty;
    public string CharacterOriginalIdentifier { get; set; } = string.Empty;
    public string PlayerText { get; set; } = string.Empty;
    public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
    public List<string> OriginalStoryFacts { get; set; } = new();
    public List<string> ActiveCapabilities { get; set; } = new();
}

public sealed class IntentRecord
{
    public string IntentId { get; set; } = Guid.NewGuid().ToString("N");
    public string DeduplicationKey { get; set; } = string.Empty;
    public string WorldId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public int TurnId { get; set; }
    public string CapabilityKey { get; set; } = string.Empty;
    public string CapabilityVersion { get; set; } = string.Empty;
    public string SnapshotRevision { get; set; } = string.Empty;
    public EchoWeaveIntentStatus Status { get; set; } = EchoWeaveIntentStatus.Proposed;
    public EchoWeaveRiskLevel RiskLevel { get; set; } = EchoWeaveRiskLevel.R0ReadOnly;
    public EchoWeaveSafeTiming SafeTiming { get; set; } = EchoWeaveSafeTiming.AfterDialogue;
    public string IntentSummary { get; set; } = string.Empty;
    public Dictionary<string, string> Arguments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int CreatedDay { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public string LastError { get; set; } = string.Empty;
}

public sealed class IntentLedgerEntry
{
    public long Sequence { get; set; }
    public string EventId { get; set; } = Guid.NewGuid().ToString("N");
    public string IntentId { get; set; } = string.Empty;
    public string WorldId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public EchoWeaveIntentStatus Status { get; set; } = EchoWeaveIntentStatus.Proposed;
    public string DeduplicationKey { get; set; } = string.Empty;
    public string CapabilityKey { get; set; } = string.Empty;
    public string SnapshotRevision { get; set; } = string.Empty;
    public int Day { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string Summary { get; set; } = string.Empty;
    public Dictionary<string, string> Data { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class WorldActionPlan
{
    public string PlanKey { get; set; } = string.Empty;
    public string IntentSummary { get; set; } = string.Empty;
    public string SnapshotRevision { get; set; } = string.Empty;
    public List<WorldActionStep> Steps { get; set; } = new();
    public string ExecutionPolicy { get; set; } = "ordered";
}

public sealed class WorldActionStep
{
    public string StepKey { get; set; } = string.Empty;
    public string CapabilityKey { get; set; } = string.Empty;
    public Dictionary<string, string> Arguments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> DependsOn { get; set; } = new();
    public string FailurePolicy { get; set; } = "stop_and_explain";
}
