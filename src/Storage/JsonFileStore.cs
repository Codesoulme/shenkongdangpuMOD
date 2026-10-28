using System.Text.Json;
using System.Text.Json.Serialization;
using MelonLoader;
using AIWorldPawnshop.Models;

namespace AIWorldPawnshop.Storage;

public sealed class JsonFileStore
{
    private readonly string _root;
    private readonly HashSet<string> _recoveredPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public JsonFileStore(string root)
    {
        _root = root;
        System.IO.Directory.CreateDirectory(_root);
        System.IO.Directory.CreateDirectory(Path.Combine(_root, "Worlds"));
        System.IO.Directory.CreateDirectory(Path.Combine(_root, "Logs"));
    }

    public WorldModConfig LoadConfig()
    {
        var path = Path.Combine(_root, "Config.json");
        if (!File.Exists(path))
        {
            var defaults = new WorldModConfig();
            defaults.Normalize();
            defaults.SyncLegacyFieldsFromActive();
            Save(path, defaults);
            return defaults;
        }

        var config = Read<WorldModConfig>(path) ?? new WorldModConfig();
        config.Normalize();
        config.SyncLegacyFieldsFromActive();
        return config;
    }

    public void SaveConfig(WorldModConfig config)
    {
        config.Normalize();
        config.SyncLegacyFieldsFromActive();
        Save(Path.Combine(_root, "Config.json"), config);
    }

    public LocalContent LoadLocalContent()
    {
        var path = Path.Combine(_root, "LocalContent.json");
        return File.Exists(path)
            ? Read<LocalContent>(path) ?? LocalContent.CreateDefaults()
            : LocalContent.CreateDefaults();
    }

    public WorldState LoadWorld(string worldId)
    {
        var path = GetWorldPath(worldId);
        var state = File.Exists(path)
            ? Read<WorldState>(path) ?? new WorldState { WorldId = worldId }
            : new WorldState { WorldId = worldId };
        NormalizeLoadedWorld(state, worldId);
        return state;
    }

    public bool SaveWorld(WorldState state)
    {
        return Save(GetWorldPath(state.WorldId), state);
    }

    public void Log(string message, Exception? exception = null)
    {
        var line = $"{DateTime.UtcNow:O} {message}";
        if (exception is not null)
            line += Environment.NewLine + exception;

        try
        {
            File.AppendAllText(Path.Combine(_root, "Logs", "mod.log"), line + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string GetWorldPath(string worldId) =>
        Path.Combine(_root, "Worlds", $"{worldId}.json");

    public static void NormalizeLoadedWorld(WorldState state, string worldId)
    {
        state.SchemaVersion = ModConstants.SchemaVersion;
        if (string.IsNullOrWhiteSpace(state.WorldId))
            state.WorldId = worldId;
        if (state.StateRevision < 0)
            state.StateRevision = 0;

        state.Factions = state.Factions is null
            ? new Dictionary<string, FactionState>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, FactionState>(state.Factions, StringComparer.OrdinalIgnoreCase);
        state.Characters = state.Characters is null
            ? new Dictionary<string, CharacterState>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, CharacterState>(state.Characters, StringComparer.OrdinalIgnoreCase);
        state.ActiveEvents ??= new List<WorldEventState>();
        state.ActiveModifications ??= new List<WorldModificationState>();
        state.Facts ??= new List<FactRecord>();
        state.News ??= new List<NewsRecord>();
        state.ProcurementRequests ??= new List<ProcurementRequestState>();
        state.ReturnVisits ??= new List<ReturnVisitState>();
        state.CarriedInventories ??= new List<CarriedInventoryState>();
        state.ThirdPartyTasks ??= new List<ThirdPartyTaskState>();

        foreach (var faction in state.Factions.Values.Where(x => x is not null))
        {
            faction.FactionKey ??= string.Empty;
            faction.RecentChanges ??= new List<int>();
            faction.Memories ??= new List<MemoryRecord>();
            NormalizeMemories(faction.Memories);
        }

        foreach (var character in state.Characters.Values.Where(x => x is not null))
        {
            character.CharacterKey ??= string.Empty;
            character.DisplayName ??= string.Empty;
            character.OriginalIdentifier ??= string.Empty;
            character.OriginalRealName ??= string.Empty;
            character.FactionKey ??= string.Empty;
            character.Memories ??= new List<MemoryRecord>();
            NormalizeMemories(character.Memories);
        }

        foreach (var worldEvent in state.ActiveEvents.Where(x => x is not null))
        {
            worldEvent.EventKey ??= string.Empty;
            worldEvent.Title ??= string.Empty;
            worldEvent.Summary ??= string.Empty;
            worldEvent.ItemGroup ??= string.Empty;
        }

        foreach (var modification in state.ActiveModifications.Where(x => x is not null))
        {
            modification.ModificationId ??= Guid.NewGuid().ToString("N");
            modification.TargetKey ??= string.Empty;
            modification.Field ??= string.Empty;
            modification.Title ??= string.Empty;
            modification.Summary ??= string.Empty;
            modification.Evidence ??= string.Empty;
            modification.Source ??= "gm";
            modification.ExecutorKey ??= "world.modify";
        }

        foreach (var news in state.News.Where(x => x is not null))
        {
            news.NewsId ??= Guid.NewGuid().ToString("N");
            news.Title ??= string.Empty;
            news.Body ??= string.Empty;
            news.SourceEventKey ??= string.Empty;
        }

        foreach (var request in state.ProcurementRequests.Where(x => x is not null))
        {
            request.RequestKey ??= Guid.NewGuid().ToString("N");
            request.CharacterKey ??= string.Empty;
            request.CharacterDisplayName ??= string.Empty;
            request.OriginalClientIdentifier ??= string.Empty;
            request.FactionKey ??= string.Empty;
            request.OriginalIntent ??= string.Empty;
            request.Category ??= string.Empty;
            request.RequestedDescription ??= string.Empty;
            request.PlayerRequestText ??= string.Empty;
            request.AccessReason ??= string.Empty;
            request.Status ??= "pending";
            request.ResolvedItemKey ??= string.Empty;
            request.ResolvedItemName ??= string.Empty;
            request.ResolvedRarityLabel ??= string.Empty;
            request.OutcomeSummary ??= string.Empty;
        }

        foreach (var task in state.ThirdPartyTasks.Where(x => x is not null))
        {
            task.TaskKey ??= Guid.NewGuid().ToString("N");
            task.SourceCharacterKey ??= string.Empty;
            task.TargetCharacterKey ??= string.Empty;
            task.TargetNativeIdentifier ??= string.Empty;
            task.TargetDisplayName ??= string.Empty;
            task.Purpose ??= string.Empty;
            task.Status ??= "proposed";
            task.OutcomeSummary ??= string.Empty;
        }

        foreach (var fact in state.Facts.Where(x => x is not null))
        {
            fact.Visibility ??= "private";
            fact.SubjectCharacterKey ??= string.Empty;
        }
    }

    private static void NormalizeMemories(IEnumerable<MemoryRecord> memories)
    {
        foreach (var memory in memories.Where(x => x is not null))
        {
            memory.SourceCharacterKey ??= string.Empty;
            memory.SourceConversationId ??= string.Empty;
            memory.Evidence ??= string.Empty;
        }
    }

    private T? Read<T>(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), _options);
        }
        catch (Exception ex)
        {
            TryLogWarning($"[当铺世界] 读取文件失败：{path}");
            Log($"Read failed: {path}", ex);
            var backup = path + ".bak";
            if (!File.Exists(backup))
                return default;
            try
            {
                var recovered = JsonSerializer.Deserialize<T>(File.ReadAllText(backup), _options);
                if (recovered is not null)
                {
                    _recoveredPaths.Add(path);
                    Log($"Recovered from backup: {backup}");
                }
                return recovered;
            }
            catch (Exception backupError)
            {
                Log($"Backup read failed: {backup}", backupError);
                return default;
            }
        }
    }

    private bool Save<T>(string path, T value)
    {
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, _options));
            if (File.Exists(path) && !_recoveredPaths.Contains(path))
                File.Copy(path, path + ".bak", true);
            RuntimeCompat.MoveReplace(temp, path);
            _recoveredPaths.Remove(path);
            return true;
        }
        catch (Exception ex)
        {
            TryLogError($"[当铺世界] 保存文件失败：{path}");
            Log($"Write failed: {path}", ex);
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            return false;
        }
    }

    private static void TryLogWarning(string message)
    {
        try { MelonLogger.Warning(message); }
        catch { }
    }

    private static void TryLogError(string message)
    {
        try { MelonLogger.Error(message); }
        catch { }
    }
}
