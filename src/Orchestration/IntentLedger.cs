using System.Text;
using System.Text.Json;
using AIWorldPawnshop.Models.EchoWeave;

namespace AIWorldPawnshop.Orchestration;

/// <summary>
/// Append-only intent log with tolerant tail recovery. Each line is complete
/// JSON, so a process exit during a write can only lose the incomplete tail.
/// </summary>
public sealed class IntentLedger
{
    private readonly object _sync = new();
    private readonly string _path;
    private readonly string _worldId;
    private readonly List<IntentLedgerEntry> _entries = new();
    private readonly Dictionary<string, IntentRecord> _intentsByDedup =
        new(StringComparer.OrdinalIgnoreCase);
    private long _sequence;

    public IntentLedger(string dataRoot, string worldId)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
            throw new ArgumentException("Data root cannot be empty.", nameof(dataRoot));
        if (string.IsNullOrWhiteSpace(worldId))
            throw new ArgumentException("World id cannot be empty.", nameof(worldId));

        Directory.CreateDirectory(dataRoot);
        _path = Path.Combine(dataRoot, "EchoWeaveLedger.jsonl");
        _worldId = worldId;
        Load();
    }

    public IReadOnlyList<IntentLedgerEntry> Entries
    {
        get
        {
            lock (_sync)
                return _entries.ToList();
        }
    }

    public bool TryGetByDeduplicationKey(string key, out IntentRecord intent)
    {
        lock (_sync)
            return _intentsByDedup.TryGetValue(key ?? string.Empty, out intent!);
    }

    public bool TryPropose(IntentRecord intent, string kind, string summary)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (string.IsNullOrWhiteSpace(intent.DeduplicationKey))
            throw new ArgumentException("An intent must have a deduplication key.", nameof(intent));

        lock (_sync)
        {
            if (_intentsByDedup.ContainsKey(intent.DeduplicationKey))
                return false;

            intent.WorldId = string.IsNullOrWhiteSpace(intent.WorldId) ? _worldId : intent.WorldId;
            intent.CreatedAtUtc = intent.CreatedAtUtc == default ? DateTime.UtcNow : intent.CreatedAtUtc;
            intent.UpdatedAtUtc = intent.CreatedAtUtc;
            _intentsByDedup[intent.DeduplicationKey] = Clone(intent);
            AppendLocked(new IntentLedgerEntry
            {
                IntentId = intent.IntentId,
                WorldId = intent.WorldId,
                Kind = kind,
                Status = intent.Status,
                DeduplicationKey = intent.DeduplicationKey,
                CapabilityKey = intent.CapabilityKey,
                SnapshotRevision = intent.SnapshotRevision,
                Day = intent.CreatedDay,
                Summary = Clip(summary, 240),
                Data = new Dictionary<string, string>(intent.Arguments, StringComparer.OrdinalIgnoreCase)
            });
            return true;
        }
    }

    public bool TryBeginExecution(string deduplicationKey, int day, out IntentRecord? intent)
    {
        lock (_sync)
        {
            if (!_intentsByDedup.TryGetValue(deduplicationKey ?? string.Empty, out var current))
            {
                intent = null;
                return false;
            }
            if (current.Status == EchoWeaveIntentStatus.Executing ||
                current.Status == EchoWeaveIntentStatus.Fulfilled)
            {
                intent = Clone(current);
                return false;
            }

            current.Status = EchoWeaveIntentStatus.Executing;
            current.UpdatedAtUtc = DateTime.UtcNow;
            AppendLocked(new IntentLedgerEntry
            {
                IntentId = current.IntentId,
                WorldId = current.WorldId,
                Kind = "execution_started",
                Status = EchoWeaveIntentStatus.Executing,
                DeduplicationKey = current.DeduplicationKey,
                CapabilityKey = current.CapabilityKey,
                SnapshotRevision = current.SnapshotRevision,
                Day = day,
                Summary = "开始执行已登记动作。"
            });
            intent = Clone(current);
            return true;
        }
    }

    public bool Complete(
        string deduplicationKey,
        EchoWeaveIntentStatus status,
        int day,
        string summary,
        IReadOnlyDictionary<string, string>? data = null)
    {
        lock (_sync)
        {
            if (!_intentsByDedup.TryGetValue(deduplicationKey ?? string.Empty, out var intent))
                return false;
            intent.Status = status;
            intent.LastError = status is EchoWeaveIntentStatus.PermanentFailure or EchoWeaveIntentStatus.Conflict
                ? Clip(summary, 240)
                : string.Empty;
            intent.UpdatedAtUtc = DateTime.UtcNow;
            AppendLocked(new IntentLedgerEntry
            {
                IntentId = intent.IntentId,
                WorldId = intent.WorldId,
                Kind = KindForStatus(status),
                Status = status,
                DeduplicationKey = intent.DeduplicationKey,
                CapabilityKey = intent.CapabilityKey,
                SnapshotRevision = intent.SnapshotRevision,
                Day = day,
                Summary = Clip(summary, 240),
                Data = data is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(data, StringComparer.OrdinalIgnoreCase)
            });
            return true;
        }
    }

    public void Append(IntentLedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_sync)
            AppendLocked(entry);
    }

    public EchoWeaveRecoveryReport Recover()
    {
        lock (_sync)
        {
            var report = new EchoWeaveRecoveryReport
            {
                LastSequence = _sequence,
                ValidEntryCount = _entries.Count
            };
            foreach (var intent in _intentsByDedup.Values)
            {
                if (intent.Status is EchoWeaveIntentStatus.Executing or EchoWeaveIntentStatus.Compensating)
                    report.OpenIntents.Add(Clone(intent));
            }
            return report;
        }
    }

    private void Load()
    {
        if (!File.Exists(_path))
            return;

        foreach (var line in File.ReadLines(_path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                var entry = JsonSerializer.Deserialize<IntentLedgerEntry>(
                    line, EchoWeaveRuntime.JsonOptions(false));
                if (entry is null || !entry.WorldId.Equals(_worldId, StringComparison.OrdinalIgnoreCase))
                    continue;

                _entries.Add(entry);
                _sequence = Math.Max(_sequence, entry.Sequence);
                ApplyEntry(entry);
            }
            catch (JsonException)
            {
                // Ignore a corrupt tail and all malformed extension records.
                // The original save remains independent of this file.
            }
            catch (IOException)
            {
                break;
            }
        }
    }

    private void AppendLocked(IntentLedgerEntry entry)
    {
        entry.WorldId = string.IsNullOrWhiteSpace(entry.WorldId) ? _worldId : entry.WorldId;
        entry.Sequence = ++_sequence;
        entry.CreatedAtUtc = entry.CreatedAtUtc == default ? DateTime.UtcNow : entry.CreatedAtUtc;
        entry.Data ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var line = JsonSerializer.Serialize(entry, EchoWeaveRuntime.JsonOptions(false));
            File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            _entries.Add(entry);
            ApplyEntry(entry);
        }
        catch
        {
            // Keep the in-memory game path alive if extension storage is unavailable.
        }
    }

    private void ApplyEntry(IntentLedgerEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.DeduplicationKey))
            return;

        if (!_intentsByDedup.TryGetValue(entry.DeduplicationKey, out var intent))
        {
            intent = new IntentRecord
            {
                IntentId = entry.IntentId,
                WorldId = entry.WorldId,
                DeduplicationKey = entry.DeduplicationKey,
                CapabilityKey = entry.CapabilityKey,
                SnapshotRevision = entry.SnapshotRevision,
                CreatedDay = entry.Day,
                CreatedAtUtc = entry.CreatedAtUtc,
                UpdatedAtUtc = entry.CreatedAtUtc,
                Arguments = new Dictionary<string, string>(entry.Data, StringComparer.OrdinalIgnoreCase)
            };
            _intentsByDedup[entry.DeduplicationKey] = intent;
        }

        intent.Status = entry.Status;
        intent.UpdatedAtUtc = entry.CreatedAtUtc;
        if (entry.Status is EchoWeaveIntentStatus.Conflict or EchoWeaveIntentStatus.PermanentFailure)
            intent.LastError = entry.Summary;
    }

    private static string KindForStatus(EchoWeaveIntentStatus status) => status switch
    {
        EchoWeaveIntentStatus.Fulfilled => "intent_fulfilled",
        EchoWeaveIntentStatus.RetryableFailure => "execution_failed",
        EchoWeaveIntentStatus.Conflict => "intent_conflict",
        EchoWeaveIntentStatus.Compensated => "compensation_committed",
        EchoWeaveIntentStatus.Expired => "intent_expired",
        _ => "intent_status_changed"
    };

    private static IntentRecord Clone(IntentRecord intent) => new()
    {
        IntentId = intent.IntentId,
        DeduplicationKey = intent.DeduplicationKey,
        WorldId = intent.WorldId,
        SessionId = intent.SessionId,
        TurnId = intent.TurnId,
        CapabilityKey = intent.CapabilityKey,
        CapabilityVersion = intent.CapabilityVersion,
        SnapshotRevision = intent.SnapshotRevision,
        Status = intent.Status,
        RiskLevel = intent.RiskLevel,
        SafeTiming = intent.SafeTiming,
        IntentSummary = intent.IntentSummary,
        Arguments = new Dictionary<string, string>(intent.Arguments, StringComparer.OrdinalIgnoreCase),
        CreatedDay = intent.CreatedDay,
        CreatedAtUtc = intent.CreatedAtUtc,
        UpdatedAtUtc = intent.UpdatedAtUtc,
        LastError = intent.LastError
    };

    private static string Clip(string? value, int max)
    {
        var text = value?.Trim() ?? string.Empty;
        return text.Length <= max ? text : text[..max];
    }
}

public sealed class EchoWeaveRecoveryReport
{
    public long LastSequence { get; init; }
    public int ValidEntryCount { get; init; }
    public int RecoveredCount { get; set; }
    public List<IntentRecord> OpenIntents { get; } = new();
}
