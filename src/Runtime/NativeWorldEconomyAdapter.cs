using AIWorldPawnshop.Models;
using Il2Cpp;
using MelonLoader;

namespace AIWorldPawnshop.Runtime;

internal sealed record NativeEconomyApplication(
    bool Applied,
    bool Verified,
    string Message);

/// <summary>
/// Projects already accepted world modifiers onto the live native customer's
/// existing price fields. The original values are retained per native client
/// and restored when that client leaves, so the Mod never compounds its own
/// adjustment across refreshes or visitors.
/// </summary>
internal static class NativeWorldEconomyAdapter
{
    private sealed record Baseline(int BuyPriceModifier, int SellPriceModifier);

    private static readonly Dictionary<string, Baseline> Baselines =
        new(StringComparer.OrdinalIgnoreCase);
    private static StoreClient? _activeClient;

    public static NativeEconomyApplication ApplyCurrentClient(WorldState state)
    {
        try
        {
            var instance = PlayerStore.Instance?.currentClientInstance;
            var client = instance?.GetClientBlueprint() ?? instance?.storeClient;
            if (client is null)
                return new(false, false, "当前没有原版来客");

            var key = FirstNotBlank(client.identifier, client.displayName);
            if (string.IsNullOrWhiteSpace(key))
                return new(false, false, "当前来客没有可验证的原版标识");

            if (_activeClient is not null && !ReferenceEquals(_activeClient, client))
                RestoreClient(_activeClient);
            _activeClient = client;

            if (!Baselines.TryGetValue(key, out var baseline))
            {
                baseline = new Baseline(client.buyPriceModifier, client.sellPriceModifier);
                Baselines[key] = baseline;
            }

            var buyDelta = CalculateDelta(state, client, instance, forClientBuying: true);
            var sellDelta = CalculateDelta(state, client, instance, forClientBuying: false);
            var expectedBuy = ClampModifier(baseline.BuyPriceModifier + buyDelta);
            var expectedSell = ClampModifier(baseline.SellPriceModifier + sellDelta);

            client.buyPriceModifier = expectedBuy;
            client.sellPriceModifier = expectedSell;
            var verified = client.buyPriceModifier == expectedBuy &&
                           client.sellPriceModifier == expectedSell;
            if (!verified)
                return new(true, false, "原版顾客价格字段写入后校验失败");

            var message = buyDelta == 0 && sellDelta == 0
                ? "当前来客没有匹配的世界经济修正"
                : $"已应用原版顾客价格修正：买入 {buyDelta:+0;-0;0}，卖出 {sellDelta:+0;-0;0} 个百分点";
            return new(true, true, message);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[当铺世界] 原版经济修正写入失败：{ex.Message}");
            return new(false, false, "原版顾客价格字段不可用");
        }
    }

    public static bool RestoreClient(StoreClient? client)
    {
        if (client is null)
            return false;

        try
        {
            var key = FirstNotBlank(client.identifier, client.displayName);
            if (string.IsNullOrWhiteSpace(key) || !Baselines.Remove(key, out var baseline))
            {
                if (ReferenceEquals(_activeClient, client))
                    _activeClient = null;
                return false;
            }

            client.buyPriceModifier = baseline.BuyPriceModifier;
            client.sellPriceModifier = baseline.SellPriceModifier;
            var restored = client.buyPriceModifier == baseline.BuyPriceModifier &&
                           client.sellPriceModifier == baseline.SellPriceModifier;
            if (!restored)
                MelonLogger.Warning($"[当铺世界] 原版顾客价格修正恢复后校验失败：{key}");
            if (ReferenceEquals(_activeClient, client))
                _activeClient = null;
            return restored;
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[当铺世界] 原版顾客价格修正恢复失败：{ex.Message}");
            return false;
        }
    }

    public static bool RefreshCurrentClient(WorldState state) =>
        ApplyCurrentClient(state) is { Applied: true, Verified: true };

    private static int CalculateDelta(
        WorldState state,
        StoreClient client,
        StoreClientInstance? instance,
        bool forClientBuying)
    {
        var total = 0d;
        foreach (var modification in state.ActiveModifications)
        {
            if (!IsActive(state.CurrentDay, modification.StartDay, modification.DurationDays) ||
                !MatchesTarget(client, instance, modification.TargetKey, forClientBuying))
                continue;

            total += modification.Field.ToLowerInvariant() switch
            {
                "price_modifier" => modification.Value,
                "demand_modifier" when forClientBuying => modification.Value,
                "supply_modifier" when !forClientBuying => -modification.Value,
                _ => 0d
            };
        }

        foreach (var worldEvent in state.ActiveEvents)
        {
            if (!IsActive(state.CurrentDay, worldEvent.StartDay, worldEvent.DurationDays) ||
                !MatchesTarget(client, instance, worldEvent.ItemGroup, forClientBuying))
                continue;

            total += worldEvent.PriceModifier;
            total += forClientBuying ? worldEvent.DemandModifier : -worldEvent.SupplyModifier;
        }

        return (int)Math.Round(total * 100d, MidpointRounding.AwayFromZero);
    }

    private static bool MatchesTarget(
        StoreClient client,
        StoreClientInstance? instance,
        string? targetKey,
        bool forClientBuying)
    {
        if (string.IsNullOrWhiteSpace(targetKey) ||
            targetKey.Equals("global", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            if (forClientBuying)
            {
                var tags = client.clientBuyingTagList;
                if (tags is not null)
                {
                    for (var i = 0; i < tags.Count; i++)
                    {
                        if (tags[i].Equals(targetKey, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }

                var ids = client.clientBuyingIdList;
                if (ids is not null)
                {
                    for (var i = 0; i < ids.Count; i++)
                    {
                        if (ids[i].Equals(targetKey, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }

                return false;
            }

            var seen = new HashSet<int>();
            var carried = instance?.gridInv?.items;
            if (carried is not null)
            {
                for (var i = 0; i < carried.Count; i++)
                {
                    var item = carried[i];
                    if (item is null || (item.uniqueId > 0 && !seen.Add(item.uniqueId)))
                        continue;
                    if (MatchesItem(item, targetKey))
                        return true;
                }
            }

            // Customer goods leave gridInv while sitting on the counter.  The
            // ownership snapshot prevents a player-owned counter item from
            // making a supply-side event match the wrong direction.
            var ownership = CounterOwnershipSnapshot.Capture(PlayerStore.Instance, instance);
            foreach (var item in ownership.ClientCounterItems)
            {
                if (item is null || (item.uniqueId > 0 && !seen.Add(item.uniqueId)))
                    continue;
                if (MatchesItem(item, targetKey))
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool MatchesItem(GameItem item, string targetKey)
    {
        if (targetKey.Equals(item.identifier, StringComparison.OrdinalIgnoreCase) ||
            targetKey.Equals(item.name, StringComparison.OrdinalIgnoreCase))
            return true;

        var types = item.itemTypes;
        if (types is null)
            return false;
        for (var i = 0; i < types.Count; i++)
        {
            if (types[i].Equals(targetKey, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsActive(int day, int startDay, int durationDays) =>
        day >= startDay && (durationDays == 0 || day < startDay + durationDays);

    private static int ClampModifier(int value) => Math.Clamp(value, -100, 100);

    private static string FirstNotBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
