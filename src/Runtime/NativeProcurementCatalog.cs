using System.Security.Cryptography;
using System.Text;
using AIWorldPawnshop.Models;
using Il2Cpp;

namespace AIWorldPawnshop.Runtime;

internal sealed class ProcurementItemPlan
{
    public bool Available { get; init; }
    public bool SpecificRequest { get; init; }
    public string ItemKey { get; init; } = string.Empty;
    public string ItemName { get; init; } = string.Empty;
    public float LootWeight { get; init; }
    public int RarityTier { get; init; }
    public string FailureReason { get; init; } = string.Empty;
}

internal sealed class ProcurementItemCandidate
{
    public string ItemKey { get; init; } = string.Empty;
    public string ItemName { get; init; } = string.Empty;
    public float Weight { get; init; }
    public int RarityTier { get; init; }
}

internal static class NativeProcurementCatalog
{
    public static ProcurementItemPlan Plan(
        ProcurementRequestState request,
        int worldSeed)
    {
        try
        {
            var candidates = ReadCandidates(request.Category);
            if (candidates.Count == 0)
            {
                return Failure(
                    "原版物品表当前没有可用条目，未生成替代物品");
            }

            var query = string.IsNullOrWhiteSpace(request.PlayerRequestText)
                ? request.RequestedDescription
                : request.PlayerRequestText;
            var specific = FindSpecific(candidates, query);
            if (specific is not null)
            {
                if (specific.RarityTier > request.MaxRarityTier)
                {
                    return Failure(
                        $"指定的{specific.ItemName}属于{ProcurementRarity.Label(specific.RarityTier)}货，" +
                        "超出了这名顾客能够接触的货源",
                        true,
                        specific);
                }

                return Success(specific, true);
            }

            var accessible = candidates
                .Where(x => x.RarityTier <= request.MaxRarityTier)
                .ToList();
            if (accessible.Count == 0)
            {
                return Failure(
                    "这一类别目前没有符合该顾客渠道层级的原版物品");
            }

            var selected = PickWeighted(
                accessible,
                $"{worldSeed}:{request.RequestKey}:{request.DueDay}:{request.Category}:item");
            return selected is null
                ? Failure("原版物品表无法完成有效抽取")
                : Success(selected, false);
        }
        catch (Exception ex)
        {
            return Failure($"读取原版物品权重失败：{ex.Message}");
        }
    }

    public static GameItem? CreateItem(ProcurementRequestState request)
    {
        if (!string.IsNullOrWhiteSpace(request.ResolvedItemKey))
        {
            var exact = Safe(
                () => DirectoryMaster.Item(request.ResolvedItemKey, false),
                null as GameItem);
            if (exact is not null && !string.IsNullOrWhiteSpace(exact.identifier))
                return exact;

            return Safe(
                () => ItemSpawner.Spawn(request.ResolvedItemKey),
                null as GameItem);
        }

        var tableId = GetTableId(request.Category);
        if (string.IsNullOrWhiteSpace(tableId))
            return null;

        var itemId = Safe(() => TableMaster.Roll(tableId), string.Empty);
        if (!string.IsNullOrWhiteSpace(itemId))
        {
            var item = Safe(() => DirectoryMaster.Item(itemId, false), null as GameItem);
            if (item is not null && !string.IsNullOrWhiteSpace(item.identifier))
                return item;
        }

        return Safe(() => ItemSpawner.SpawnFromTable(tableId), null as GameItem);
    }

    private static List<ProcurementItemCandidate> ReadCandidates(string category)
    {
        var tableId = GetTableId(category);
        if (string.IsNullOrWhiteSpace(tableId))
            return new List<ProcurementItemCandidate>();

        Safe(() =>
        {
            LootRegistry.RebuildIfDirty();
            return true;
        }, false);

        Il2CppSystem.Collections.Generic.List<LootEntry>? entries = null;
        var tables = LootRegistry.tables;
        if (tables is not null)
            tables.TryGetValue(tableId, out entries);
        if (entries is null)
        {
            var vanillaTables = LootRegistry.vanillaTables;
            if (vanillaTables is not null)
                vanillaTables.TryGetValue(tableId, out entries);
        }
        if (entries is null)
            return new List<ProcurementItemCandidate>();

        var weights = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry is null || string.IsNullOrWhiteSpace(entry.id) || entry.weight <= 0)
                continue;
            weights[entry.id] = weights.TryGetValue(entry.id, out var current)
                ? current + entry.weight
                : entry.weight;
        }

        if (weights.Count == 0)
            return new List<ProcurementItemCandidate>();

        var maxWeight = weights.Values.Max();
        return weights
            .Select(pair => new ProcurementItemCandidate
            {
                ItemKey = pair.Key,
                ItemName = ResolveDisplayName(pair.Key),
                Weight = pair.Value,
                RarityTier = ProcurementRarity.FromRelativeWeight(pair.Value, maxWeight)
            })
            .ToList();
    }

    private static ProcurementItemCandidate? FindSpecific(
        IReadOnlyList<ProcurementItemCandidate> candidates,
        string query)
    {
        var normalizedQuery = Normalize(query);
        if (normalizedQuery.Length < 2)
            return null;

        return candidates
            .Select(candidate => new
            {
                Candidate = candidate,
                Name = Normalize(candidate.ItemName),
                Key = Normalize(candidate.ItemKey)
            })
            .Where(x =>
                (x.Name.Length >= 2 && normalizedQuery.Contains(x.Name, StringComparison.Ordinal)) ||
                (x.Key.Length >= 4 && normalizedQuery.Contains(x.Key, StringComparison.Ordinal)))
            .OrderByDescending(x => Math.Max(x.Name.Length, x.Key.Length))
            .Select(x => x.Candidate)
            .FirstOrDefault();
    }

    private static ProcurementItemCandidate? PickWeighted(
        IReadOnlyList<ProcurementItemCandidate> candidates,
        string seed)
    {
        var total = candidates.Sum(x => Math.Max(0.0001d, x.Weight));
        if (total <= 0)
            return null;

        var target = StableFraction(seed) * total;
        var cumulative = 0d;
        foreach (var candidate in candidates.OrderBy(x => x.ItemKey, StringComparer.OrdinalIgnoreCase))
        {
            cumulative += Math.Max(0.0001d, candidate.Weight);
            if (target <= cumulative)
                return candidate;
        }
        return candidates[^1];
    }

    private static ProcurementItemPlan Success(
        ProcurementItemCandidate candidate,
        bool specific) => new()
    {
        Available = true,
        SpecificRequest = specific,
        ItemKey = candidate.ItemKey,
        ItemName = candidate.ItemName,
        LootWeight = candidate.Weight,
        RarityTier = candidate.RarityTier
    };

    private static ProcurementItemPlan Failure(
        string reason,
        bool specific = false,
        ProcurementItemCandidate? candidate = null) => new()
    {
        Available = false,
        SpecificRequest = specific,
        ItemKey = candidate?.ItemKey ?? string.Empty,
        ItemName = candidate?.ItemName ?? string.Empty,
        LootWeight = candidate?.Weight ?? 0,
        RarityTier = candidate?.RarityTier ?? 0,
        FailureReason = reason
    };

    private static string GetTableId(string category) => category switch
    {
        "food" => TableMaster.packedFoodTable,
        "medical" => TableMaster.medicalTable,
        "material" => TableMaster.materialTable,
        "household" => TableMaster.householdTable,
        "weapon" => TableMaster.makeshiftWeaponTable,
        "tool" => TableMaster.toolTable,
        "module" => TableMaster.allModuleTable,
        "access_card" => TableMaster.accessCardTable,
        "junk" => TableMaster.junkTable,
        _ => string.Empty
    };

    private static string ResolveDisplayName(string itemKey)
    {
        var localized = Safe(() => LocHelper.GetLocalizedName(itemKey), string.Empty);
        return string.IsNullOrWhiteSpace(localized) ? itemKey : localized.Trim();
    }

    private static double StableFraction(string seed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        var value = ((ulong)hash[0] << 56) |
                    ((ulong)hash[1] << 48) |
                    ((ulong)hash[2] << 40) |
                    ((ulong)hash[3] << 32) |
                    ((ulong)hash[4] << 24) |
                    ((ulong)hash[5] << 16) |
                    ((ulong)hash[6] << 8) |
                    hash[7];
        return value / ((double)ulong.MaxValue + 1d);
    }

    private static string Normalize(string? value) =>
        string.Concat((value ?? string.Empty)
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit));

    private static T Safe<T>(Func<T> getter, T fallback)
    {
        try { return getter(); }
        catch { return fallback; }
    }
}
