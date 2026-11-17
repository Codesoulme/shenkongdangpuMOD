using System.Globalization;
using System.Text.RegularExpressions;

namespace AIWorldPawnshop.Compatibility;

// Capture before the trade removes event/client features or moves the item.
internal sealed class LedgerTradeDetails
{
    internal string Name = "物品信息不可用";
    internal string Identifier = string.Empty;
    internal string Specification = string.Empty;
    internal string PriceModifiers = string.Empty;

    internal static LedgerTradeDetails Capture(GameItem? item, bool wholesale = false)
    {
        var result = new LedgerTradeDetails();
        if (item is null) return result;
        try
        {
            // Keep the full container name, including size in parentheses.
            result.Name = Plain(item.GetDisplayName());
            result.Identifier = item.identifier ?? string.Empty;
            if (string.IsNullOrWhiteSpace(result.Name)) result.Name = result.Identifier;
            result.Specification = SpecificationOf(item);
            var modifiers = new List<string>();
            AddFeatures(item, modifiers, "", false);
            if (!item.IsTag("IGNORE_CHILD_VALUE"))
            {
                foreach (var child in item.FindAllChildItems())
                {
                    if (child.IsTag("ITEM_BEHIND_LOCK_TAG")) continue;
                    // Child pricing uses GetCurrentValue, which obtains event
                    // modifiers from active events, not child Event features.
                    AddFeatures(child, modifiers, $"内含 {Plain(child.GetDisplayName())}：", true);
                    var events = StoreStation.Instance?.storeEventManager?.GetActiveEvents();
                    if (events is null) continue;
                    foreach (var activeEvent in events)
                    foreach (var change in activeEvent.negociationDatas)
                        if (change.value != 0 && child.IsTag(change.itemType))
                            modifiers.Add($"内含 {Plain(child.GetDisplayName())}：事件·{Plain(change.reason)} {Percent(change.value)}");
                }
            }
            if (wholesale)
            {
                var client = LedgerGameAccess.GetClient(PlayerStore.Instance?.currentClientInstance);
                if (client is not null && client.isOfferWholesale && client.wholesaleDiscount != 0)
                    modifiers.Add($"批发折扣 {Math.Abs((long)client.wholesaleDiscount)}%");
            }
            result.PriceModifiers = string.Join("\n", modifiers);
        }
        catch (Exception ex) { UnityEngine.Debug.LogWarning($"[账本] 读取成交物品明细失败：{ex.Message}"); }
        return result;
    }

    private static string SpecificationOf(GameItem item)
    {
        if (item.IsTag("LIQUID_CONTAINER_TAG"))
        {
            if (item.IsTag("LIQUID_CONTAINER_CAPACITY"))
            {
                // Current WaterHelper stores microlitres. Preserve fractions
                // of a millilitre instead of truncating small containers.
                return $"{Millilitres(WaterHelper.GetCurrentCapacityUL(item))} / {Millilitres(WaterHelper.GetTotalCapacity(item))} ml";
            }
            if (item.IsTag("LIQUID_CONTAINER_MAX"))
                return $"{item.GetTagReadonly("LIQUID_CONTAINER_CURRENT").GetInt()} / {item.GetTagReadonly("LIQUID_CONTAINER_MAX").GetInt()} ml";
        }
        if (item.IsTag("power_source_item"))
            return $"{item.GetTagReadonly("power_source_item_energy").GetInt()} / {item.GetTagReadonly("power_source_item_max_energy").GetInt()} kJ";
        return item.unitCount > 1 ? $"数量 {item.unitCount}" : string.Empty;
    }

    private static string Millilitres(int microlitres) =>
        (microlitres / 1000m).ToString("0.###", CultureInfo.InvariantCulture);

    private static void AddFeatures(GameItem item, List<string> output, string prefix, bool child)
    {
        foreach (var feature in item.itemFeatures)
        {
            if (feature.isDisabled || (child && feature.featureType == ItemFeature.FeatureType.Event)) continue;
            var value = feature.GetCurrentValueMod();
            if (value == 0) continue;
            var source = feature.featureType == ItemFeature.FeatureType.Event ? "事件" :
                feature.GetValueStage() == ItemFeature.ValueStage.Final ? "议价" :
                feature.GetValueStage() == ItemFeature.ValueStage.Market ? "市场/顾客" : "物品状态";
            var label = Plain(feature.GetBaseCurrentDisplay());
            if (string.IsNullOrWhiteSpace(label)) label = "价格修正";
            output.Add($"{prefix}{source}·{label} {Percent(value)}");
        }
        if (child && item.IsApplyMarkUp() && item.FindItemFeatureByID("retailMarkUp") is null &&
            PlayerStore.Instance is not null && PlayerStore.Instance.retailMarkup != 0)
            output.Add($"{prefix}零售加价 {PlayerStore.Instance.retailMarkup}%");
    }

    private static string Percent(int value) => value > 0 ? $"加价 {value}%" : $"折扣 {Math.Abs((long)value)}%";
    private static string Plain(string? text) => Regex.Replace(text ?? string.Empty, "<[^>]*>", "").Trim();
}
