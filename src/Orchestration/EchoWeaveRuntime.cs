using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using AIWorldPawnshop.Capabilities;
using AIWorldPawnshop.Models;
using AIWorldPawnshop.Models.EchoWeave;

namespace AIWorldPawnshop.Orchestration;

/// <summary>
/// Small deterministic boundary around the first-stage persistence pieces.
/// It does not execute game actions; existing adapters remain the authority for
/// native effects, while this class records intent and recovery evidence.
/// </summary>
public sealed class EchoWeaveRuntime
{
    private readonly object _sync = new();
    private readonly string _snapshotPath;
    private readonly IntentLedger _ledger;

    public EchoWeaveRuntime(string dataRoot, string worldId)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
            throw new ArgumentException("Data root cannot be empty.", nameof(dataRoot));
        if (string.IsNullOrWhiteSpace(worldId))
            throw new ArgumentException("World id cannot be empty.", nameof(worldId));

        System.IO.Directory.CreateDirectory(dataRoot);
        _snapshotPath = Path.Combine(dataRoot, "EchoWeaveSnapshot.json");
        _ledger = new IntentLedger(dataRoot, worldId);
        WorldId = worldId;
        Capabilities = BuiltInCapabilityCatalog.Create();
        Snapshot = LoadSnapshot() ?? new EchoWeaveSnapshot { WorldId = worldId };
        if (!Snapshot.WorldId.Equals(worldId, StringComparison.OrdinalIgnoreCase))
            Snapshot = new EchoWeaveSnapshot { WorldId = worldId };
    }

    public string WorldId { get; }
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public CapabilityRegistry Capabilities { get; }
    public EchoWeaveSnapshot Snapshot { get; private set; }
    public IntentLedger Ledger => _ledger;

    public EchoWeaveSnapshot Capture(ConversationRequest request, WorldState state)
    {
        RuntimeCompat.Require(request, nameof(request));
        RuntimeCompat.Require(state, nameof(state));

        var gameContext = request.GameContext ?? new GameConversationContext();
        var character = gameContext.Character ?? new NativeCharacterContext();
        var snapshot = new EchoWeaveSnapshot
        {
            WorldId = state.WorldId,
            SessionId = SessionId,
            ConversationId = request.ConversationId ?? string.Empty,
            TurnId = request.TurnId,
            StateRevision = state.StateRevision,
            CurrentDay = state.CurrentDay,
            Scene = request.Scene ?? string.Empty,
            CharacterKey = FirstNotBlank(character.Key, request.CharacterKey),
            CharacterDisplayName = FirstNotBlank(character.DisplayName, request.CharacterDisplayName),
            FactionKey = FirstNotBlank(character.FactionKey, request.FactionKey),
            CharacterOriginalIdentifier = character.OriginalIdentifier ?? string.Empty,
            PlayerText = Clip(request.PlayerText, 500),
            CapturedAtUtc = DateTime.UtcNow,
            OriginalStoryFacts = gameContext.OriginalStoryFacts
                ?.Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => Clip(x, 240))
                .Take(24)
                .ToList() ?? new List<string>(),
            ActiveCapabilities = Capabilities.Search(null, request.Scene)
                .Where(x => x.ImplementationStatus.Equals("implemented", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.CapabilityKey)
                .ToList()
        };

        lock (_sync)
        {
            Snapshot = snapshot;
            SaveSnapshot(snapshot);
        }

        return snapshot;
    }

    public string CreateDeduplicationKey(
        EchoWeaveSnapshot snapshot,
        string capabilityKey,
        IReadOnlyDictionary<string, string>? arguments = null)
    {
        return EchoWeaveKeys.Create(
            snapshot.WorldId,
            string.IsNullOrWhiteSpace(snapshot.ConversationId)
                ? snapshot.SessionId
                : "conversation:" + snapshot.ConversationId.Trim(),
            snapshot.TurnId,
            capabilityKey,
            arguments);
    }

    public bool IsCurrent(EchoWeaveSnapshot snapshot, WorldState state) =>
        snapshot.WorldId.Equals(state.WorldId, StringComparison.OrdinalIgnoreCase) &&
        snapshot.StateRevision == state.StateRevision;

    public bool TryRecordConversation(EchoWeaveSnapshot snapshot, string summary)
    {
        var arguments = ConversationArguments(snapshot);
        var key = CreateDeduplicationKey(snapshot, "conversation.record", arguments);
        var proposed = _ledger.TryPropose(new IntentRecord
        {
            WorldId = snapshot.WorldId,
            SessionId = snapshot.SessionId,
            TurnId = snapshot.TurnId,
            CapabilityKey = "conversation.record",
            CapabilityVersion = "1.0",
            SnapshotRevision = snapshot.StateRevision.ToString(),
            Status = EchoWeaveIntentStatus.Approved,
            DeduplicationKey = key,
            IntentSummary = summary,
            Arguments = arguments,
            CreatedDay = snapshot.CurrentDay
        }, "intent_approved", summary);
        if (!proposed &&
            (!_ledger.TryGetByDeduplicationKey(key, out var pending) ||
             pending.Status != EchoWeaveIntentStatus.Approved ||
             pending.SnapshotRevision != snapshot.StateRevision.ToString()))
        {
            return false;
        }

        return _ledger.TryBeginExecution(key, snapshot.CurrentDay, out _);
    }

    public bool CompleteConversation(
        EchoWeaveSnapshot snapshot,
        EchoWeaveIntentStatus status,
        string summary)
    {
        var key = CreateDeduplicationKey(
            snapshot,
            "conversation.record",
            ConversationArguments(snapshot));
        return _ledger.Complete(key, status, snapshot.CurrentDay, summary);
    }

    public bool TryRecordAdapterOutcome(
        EchoWeaveSnapshot snapshot,
        string capabilityKey,
        string summary,
        EchoWeaveIntentStatus status,
        IReadOnlyDictionary<string, string>? arguments = null)
    {
        var key = CreateDeduplicationKey(snapshot, capabilityKey, arguments);

        // A live action is normally pre-registered through
        // TryBeginCapability. Complete that same ledger record instead of
        // appending a second proposal after the native call. The fallback
        // proposal path keeps compatibility with older callers and with
        // cross-day records that have no live execution phase.
        if (_ledger.TryGetByDeduplicationKey(key, out var existing))
        {
            if (existing.Status is not EchoWeaveIntentStatus.Fulfilled and
                not EchoWeaveIntentStatus.Rejected and
                not EchoWeaveIntentStatus.Conflict and
                not EchoWeaveIntentStatus.PermanentFailure and
                not EchoWeaveIntentStatus.Expired and
                not EchoWeaveIntentStatus.Scheduled)
            {
                return _ledger.Complete(key, status, snapshot.CurrentDay, summary);
            }

            return false;
        }

        return _ledger.TryPropose(new IntentRecord
        {
            WorldId = snapshot.WorldId,
            SessionId = snapshot.SessionId,
            TurnId = snapshot.TurnId,
            CapabilityKey = capabilityKey,
            CapabilityVersion = Capabilities.Describe(capabilityKey)?.Descriptor.Version ?? "1.0",
            SnapshotRevision = snapshot.StateRevision.ToString(),
            Status = status,
            DeduplicationKey = key,
            IntentSummary = summary,
            Arguments = arguments is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(arguments, StringComparer.OrdinalIgnoreCase),
            CreatedDay = snapshot.CurrentDay
        }, "adapter_outcome", summary);
    }

    /// <summary>
    /// Opens the single commit gate used by native adapters. A capability must
    /// be present in the directory, marked implemented, pass its local check,
    /// and be durably recorded before the caller is allowed to touch the game.
    /// </summary>
    public bool TryBeginCapability(
        EchoWeaveSnapshot snapshot,
        string capabilityKey,
        string summary,
        out string reason,
        IReadOnlyDictionary<string, string>? arguments = null,
        EchoWeaveSafeTiming timing = EchoWeaveSafeTiming.AfterDialogue)
    {
        reason = string.Empty;
        if (!Capabilities.TryGet(capabilityKey, out var capability) ||
            !capability.Descriptor.ImplementationStatus.Equals(
                "implemented", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"能力未接通：{capabilityKey}";
            return false;
        }

        if (capability.Descriptor.SupportedScenes.Count > 0 &&
            !capability.Descriptor.SupportedScenes.Any(scene =>
                scene.Equals(snapshot.Scene, StringComparison.OrdinalIgnoreCase)))
        {
            reason = $"当前场景不支持能力：{capabilityKey}";
            return false;
        }

        var invocation = new CapabilityInvocation
        {
            CapabilityKey = capabilityKey,
            CapabilityVersion = capability.Descriptor.Version,
            Arguments = arguments is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(arguments, StringComparer.OrdinalIgnoreCase),
            DeduplicationKey = CreateDeduplicationKey(snapshot, capabilityKey, arguments)
        };
        var check = capability.Check(invocation, snapshot);
        if (!check.Allowed)
        {
            reason = string.IsNullOrWhiteSpace(check.Reason)
                ? "能力前置条件未满足"
                : check.Reason;
            return false;
        }

        if (_ledger.TryGetByDeduplicationKey(invocation.DeduplicationKey, out var existing))
        {
            if (existing.Status is EchoWeaveIntentStatus.Approved or EchoWeaveIntentStatus.Ready)
            {
                if (_ledger.TryBeginExecution(
                        invocation.DeduplicationKey, snapshot.CurrentDay, out _))
                    return true;
            }

            reason = existing.Status is EchoWeaveIntentStatus.Fulfilled
                or EchoWeaveIntentStatus.Scheduled
                ? "同一动作已经完成"
                : "同一动作已经登记，等待原版回执";
            return false;
        }

        var proposed = _ledger.TryPropose(new IntentRecord
        {
            WorldId = snapshot.WorldId,
            SessionId = snapshot.SessionId,
            TurnId = snapshot.TurnId,
            CapabilityKey = capabilityKey,
            CapabilityVersion = capability.Descriptor.Version,
            SnapshotRevision = snapshot.StateRevision.ToString(),
            Status = EchoWeaveIntentStatus.Approved,
            RiskLevel = capability.Descriptor.RiskLevel,
            SafeTiming = check.RequiredTiming ?? timing,
            DeduplicationKey = invocation.DeduplicationKey,
            IntentSummary = summary,
            Arguments = invocation.Arguments,
            CreatedDay = snapshot.CurrentDay
        }, "capability_approved", summary);
        if (!proposed || !_ledger.TryBeginExecution(
                invocation.DeduplicationKey, snapshot.CurrentDay, out _))
        {
            reason = "动作登记失败，未调用原版接口";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Records deterministic cross-day work without pretending that it came
    /// from a live conversation turn.  The stable world/day/subject tuple
    /// makes retries idempotent and keeps events, expirations and arrivals in
    /// the same append-only ledger as dialogue-triggered capabilities.
    /// </summary>
    public bool TryRecordWorldOutcome(
        int day,
        string capabilityKey,
        string subjectKey,
        string summary,
        EchoWeaveIntentStatus status,
        IReadOnlyDictionary<string, string>? outputs = null)
    {
        var arguments = outputs is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(outputs, StringComparer.OrdinalIgnoreCase);
        arguments["subject_key"] = subjectKey ?? string.Empty;
        var key = EchoWeaveKeys.Create(
            WorldId,
            "world",
            day,
            capabilityKey,
            arguments);
        return _ledger.TryPropose(new IntentRecord
        {
            WorldId = WorldId,
            SessionId = "world",
            TurnId = day,
            CapabilityKey = capabilityKey,
            CapabilityVersion = Capabilities.Describe(capabilityKey)?.Descriptor.Version ?? "1.0",
            SnapshotRevision = Snapshot.StateRevision.ToString(),
            Status = status,
            DeduplicationKey = key,
            IntentSummary = summary,
            Arguments = arguments,
            CreatedDay = day
        }, "world_outcome", summary);
    }

    public EchoWeaveRecoveryReport Recover(WorldState state)
    {
        RuntimeCompat.Require(state, nameof(state));
        var report = _ledger.Recover();
        if (report.OpenIntents.Count == 0)
            return report;

        // A native action cannot be safely replayed from a process restart.
        // Keep it visible as a conflict and let the next conversation decide.
        foreach (var intent in report.OpenIntents)
        {
            if (_ledger.Append(new IntentLedgerEntry
            {
                IntentId = intent.IntentId,
                WorldId = intent.WorldId,
                Kind = "execution_recovered",
                Status = EchoWeaveIntentStatus.Conflict,
                DeduplicationKey = intent.DeduplicationKey,
                CapabilityKey = intent.CapabilityKey,
                SnapshotRevision = intent.SnapshotRevision,
                Day = state.CurrentDay,
                Summary = "进程退出时动作未闭合，已标记为待重新核对，不自动重放。"
            }))
                report.RecoveredCount++;
        }
        return report;
    }

    public void Save()
    {
        lock (_sync)
            SaveSnapshot(Snapshot);
    }

    private EchoWeaveSnapshot? LoadSnapshot()
    {
        try
        {
            if (!File.Exists(_snapshotPath))
                return null;
            return JsonConvert.DeserializeObject<EchoWeaveSnapshot>(
                File.ReadAllText(_snapshotPath), JsonOptions());
        }
        catch
        {
            // A corrupt extension snapshot must never block the original game.
            return null;
        }
    }

    private static Dictionary<string, string> ConversationArguments(EchoWeaveSnapshot snapshot) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["conversation_id"] = snapshot.ConversationId,
            ["character_key"] = snapshot.CharacterKey,
            ["scene"] = snapshot.Scene
        };

    private void SaveSnapshot(EchoWeaveSnapshot snapshot)
    {
        var temp = _snapshotPath + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonConvert.SerializeObject(snapshot, JsonOptions()), Encoding.UTF8);
            if (File.Exists(_snapshotPath))
                File.Copy(_snapshotPath, _snapshotPath + ".bak", true);
            RuntimeCompat.MoveReplace(temp, _snapshotPath);
        }
        catch
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch
            {
                // Persistence failures are intentionally non-fatal to gameplay.
            }
        }
    }

    internal static JsonSerializerSettings JsonOptions(bool writeIndented = true) => new()
    {
        Formatting = writeIndented ? Formatting.Indented : Formatting.None,
        Converters = { new StringEnumConverter() }
    };

    private static string FirstNotBlank(params string?[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;

    private static string Clip(string? value, int max)
    {
        var text = value?.Trim() ?? string.Empty;
        return text.Length <= max ? text : text[..max];
    }
}

public static class EchoWeaveKeys
{
    public static string Create(
        string worldId,
        string sessionId,
        int turnId,
        string capabilityKey,
        IReadOnlyDictionary<string, string>? arguments = null)
    {
        var builder = new StringBuilder();
        Append(builder, worldId);
        Append(builder, sessionId);
        Append(builder, turnId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(builder, capabilityKey);
        if (arguments is not null)
        {
            foreach (var pair in arguments.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                Append(builder, pair.Key.Trim().ToLowerInvariant());
                Append(builder, pair.Value?.Trim() ?? string.Empty);
            }
        }

        var bytes = RuntimeCompat.HashSha256(Encoding.UTF8.GetBytes(builder.ToString()));
        return RuntimeCompat.ToHex(bytes).ToLowerInvariant();
    }

    private static void Append(StringBuilder builder, string value)
    {
        builder.Append(value.Length).Append(':').Append(value).Append('|');
    }
}
