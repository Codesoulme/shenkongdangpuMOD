using JsonPropertyName = Newtonsoft.Json.JsonPropertyAttribute;

namespace AIWorldPawnshop.Models;

public sealed class WorldModConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "openai-compatible";
    [JsonPropertyName("base_url")]
    public string BaseUrl { get; set; } = "http://127.0.0.1:11434/v1";
    [JsonPropertyName("model")]
    public string Model { get; set; } = "local-model";
    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = "auto";
    [JsonPropertyName("api_key_env")]
    public string ApiKeyEnv { get; set; } = "PAWN_SHOP_MODEL_KEY";
    [JsonPropertyName("timeout_ms")]
    public int TimeoutMs { get; set; } = 60000;
    [JsonPropertyName("max_output_tokens")]
    public int MaxOutputTokens { get; set; } = 500;
    [JsonPropertyName("allow_network")]
    public bool AllowNetwork { get; set; }
    [JsonPropertyName("debug_log")]
    public bool DebugLog { get; set; }
    [JsonPropertyName("show_exact_relationship_values")]
    public bool ShowExactRelationshipValues { get; set; }

    // The legacy fields above remain readable so existing Config.json files do
    // not need a manual migration. New installs and the settings window use
    // the profile list below.
    [JsonPropertyName("active_profile_id")]
    public string ActiveProfileId { get; set; } = string.Empty;

    [JsonPropertyName("profiles")]
    public List<ModelProfile> Profiles { get; set; } = new();

    public void Normalize()
    {
        Profiles ??= new List<ModelProfile>();
        Profiles = Profiles.Where(profile => profile is not null).ToList();

        if (Profiles.Count == 0)
            Profiles.Add(ModelProfile.FromLegacy(this));

        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in Profiles)
        {
            profile.Normalize();
            if (!usedIds.Add(profile.Id))
                profile.Id = Guid.NewGuid().ToString("N");
        }

        if (string.IsNullOrWhiteSpace(ActiveProfileId) ||
            Profiles.All(profile => !profile.Id.Equals(ActiveProfileId, StringComparison.OrdinalIgnoreCase)))
        {
            ActiveProfileId = Profiles[0].Id;
        }
    }

    public ModelProfile GetActiveProfile()
    {
        Normalize();
        return Profiles.First(profile =>
            profile.Id.Equals(ActiveProfileId, StringComparison.OrdinalIgnoreCase));
    }

    public void SyncLegacyFieldsFromActive()
    {
        var profile = GetActiveProfile();
        Provider = profile.Provider;
        BaseUrl = profile.BaseUrl;
        Model = profile.Model;
        Protocol = profile.Protocol;
        ApiKeyEnv = profile.ApiKeyEnv;
        TimeoutMs = profile.TimeoutMs;
        MaxOutputTokens = profile.MaxOutputTokens;
        AllowNetwork = profile.AllowNetwork;
    }

    public WorldModConfig Clone()
    {
        var clone = new WorldModConfig
        {
            Enabled = Enabled,
            Provider = Provider,
            BaseUrl = BaseUrl,
            Model = Model,
            Protocol = Protocol,
            ApiKeyEnv = ApiKeyEnv,
            TimeoutMs = TimeoutMs,
            MaxOutputTokens = MaxOutputTokens,
            AllowNetwork = AllowNetwork,
            DebugLog = DebugLog,
            ShowExactRelationshipValues = ShowExactRelationshipValues,
            ActiveProfileId = ActiveProfileId,
            Profiles = Profiles?
                .Where(profile => profile is not null)
                .Select(profile => profile.Clone())
                .ToList() ?? new List<ModelProfile>()
        };
        clone.Normalize();
        return clone;
    }
}

public sealed class ModelProfile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("name")]
    public string Name { get; set; } = "默认接口";

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "openai-compatible";

    [JsonPropertyName("base_url")]
    public string BaseUrl { get; set; } = "http://127.0.0.1:11434/v1";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "local-model";

    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = "auto";

    [JsonPropertyName("api_key")]
    public string ApiKey { get; set; } = string.Empty;

    [JsonPropertyName("api_key_env")]
    public string ApiKeyEnv { get; set; } = "PAWN_SHOP_MODEL_KEY";

    [JsonPropertyName("timeout_ms")]
    public int TimeoutMs { get; set; } = 60000;

    [JsonPropertyName("max_output_tokens")]
    public int MaxOutputTokens { get; set; } = 500;

    [JsonPropertyName("allow_network")]
    public bool AllowNetwork { get; set; }

    public static ModelProfile FromLegacy(WorldModConfig config)
    {
        return new ModelProfile
        {
            Name = "默认接口",
            Provider = config.Provider,
            BaseUrl = config.BaseUrl,
            Model = config.Model,
            Protocol = config.Protocol,
            ApiKeyEnv = config.ApiKeyEnv,
            TimeoutMs = config.TimeoutMs,
            MaxOutputTokens = config.MaxOutputTokens,
            AllowNetwork = config.AllowNetwork
        };
    }

    public void Normalize()
    {
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id.Trim();
        Name = string.IsNullOrWhiteSpace(Name) ? "未命名接口" : Name.Trim();
        Provider = string.IsNullOrWhiteSpace(Provider) ? "openai-compatible" : Provider.Trim();
        BaseUrl = (BaseUrl ?? string.Empty).Trim();
        Model = (Model ?? string.Empty).Trim();
        Protocol = NormalizeProtocol(Protocol);
        ApiKey = ApiKey?.Trim() ?? string.Empty;
        ApiKeyEnv = ApiKeyEnv?.Trim() ?? string.Empty;
        TimeoutMs = Math.Clamp(TimeoutMs, 1000, 120000);
        MaxOutputTokens = Math.Clamp(MaxOutputTokens, 32, 8000);
    }

    public ModelProfile Clone()
    {
        return new ModelProfile
        {
            Id = Id,
            Name = Name,
            Provider = Provider,
            BaseUrl = BaseUrl,
            Model = Model,
            Protocol = Protocol,
            ApiKey = ApiKey,
            ApiKeyEnv = ApiKeyEnv,
            TimeoutMs = TimeoutMs,
            MaxOutputTokens = MaxOutputTokens,
            AllowNetwork = AllowNetwork
        };
    }

    private static string NormalizeProtocol(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "responses" => "responses",
            "chat" or "chat-completions" or "chat_completions" => "chat_completions",
            _ => "auto"
        };
    }
}
