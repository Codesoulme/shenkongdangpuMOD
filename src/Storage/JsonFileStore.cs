using System.Text.Json;
using System.Text.Json.Serialization;
using MelonLoader;
using AIWorldPawnshop.Models;

namespace AIWorldPawnshop.Storage;

public sealed class JsonFileStore
{
    private readonly string _root;
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public JsonFileStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "Worlds"));
        Directory.CreateDirectory(Path.Combine(_root, "Logs"));
    }

    public ModConfig LoadConfig()
    {
        var path = Path.Combine(_root, "Config.json");
        if (!File.Exists(path))
        {
            var defaults = new ModConfig();
            defaults.Normalize();
            defaults.SyncLegacyFieldsFromActive();
            Save(path, defaults);
            return defaults;
        }

        var config = Read<ModConfig>(path) ?? new ModConfig();
        config.Normalize();
        config.SyncLegacyFieldsFromActive();
        return config;
    }

    public void SaveConfig(ModConfig config)
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
        NormalizeWorld(state, worldId);
        return state;
    }

    public void SaveWorld(WorldState state)
    {
        Save(GetWorldPath(state.WorldId), state);
    }

    public void Log(string message, Exception? exception = null)
    {
        var line = $"{DateTime.UtcNow:O} {message}";
        if (exception is not null)
            line += Environment.NewLine + exception;

        File.AppendAllText(Path.Combine(_root, "Logs", "mod.log"), line + Environment.NewLine);
    }

    private string GetWorldPath(string worldId) =>
        Path.Combine(_root, "Worlds", $"{worldId}.json");

    private static void NormalizeWorld(WorldState state, string worldId)
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
        state.Facts ??= new List<FactRecord>();
        state.News ??= new List<NewsRecord>();
        state.ProcurementRequests ??= new List<ProcurementRequestState>();
        state.ReturnVisits ??= new List<ReturnVisitState>();
        state.CarriedInventories ??= new List<CarriedInventoryState>();

        foreach (var faction in state.Factions.Values.Where(x => x is not null))
        {
            faction.FactionKey ??= string.Empty;
            faction.RecentChanges ??= new List<int>();
            faction.Memories ??= new List<MemoryRecord>();
        }

        foreach (var character in state.Characters.Values.Where(x => x is not null))
        {
            character.CharacterKey ??= string.Empty;
            character.DisplayName ??= string.Empty;
            character.FactionKey ??= string.Empty;
            character.Memories ??= new List<MemoryRecord>();
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
    }

    private T? Read<T>(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), _options);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[当铺世界] 读取文件失败：{path}");
            Log($"Read failed: {path}", ex);
            return default;
        }
    }

    private void Save<T>(string path, T value)
    {
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, _options));
            if (File.Exists(path))
                File.Copy(path, path + ".bak", true);
            File.Move(temp, path, true);
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[当铺世界] 保存文件失败：{path}");
            Log($"Write failed: {path}", ex);
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }
}
