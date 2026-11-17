using AIWorldPawnshop.Compatibility;
using AIWorldPawnshop.Runtime;
using HarmonyLib;

namespace AIWorldPawnshop;

[HarmonyPatch(typeof(VendingMachineHelper), nameof(VendingMachineHelper.TransferCashToPlayer))]
internal static class VendingIncomeSourcePatch
{
    private static void Prefix(GameItem __0, out LedgerCashSource? __state)
    {
        __state = LedgerCashSource.Current;
        LedgerCashSource.Current = new LedgerCashSource
        {
            Type = "自动贩卖机收入", Customer = "自动贩卖机",
            Detail = "提取累计营业收入（商品售出时暂存于设备，本次转入信用点余额）",
            Item = __0 is null ? null : LedgerTradeDetails.Capture(__0)
        };
    }

    private static Exception? Finalizer(Exception? __exception, LedgerCashSource? __state)
    {
        LedgerCashSource.Current = __state;
        return __exception;
    }
}

[HarmonyPatch(typeof(StoreRequest), nameof(StoreRequest.DispenseReward))]
internal static class MarketplaceIncomeSourcePatch
{
    private static void Prefix(StoreRequest __instance, out LedgerCashSource? __state)
    {
        __state = LedgerCashSource.Current;
        var itemName = __instance.useCustomItemName ? __instance.requestedItem : string.Empty;
        if (string.IsNullOrWhiteSpace(itemName) && !string.IsNullOrWhiteSpace(__instance.requiredItem))
        {
            // A missing definition (for example from a removed mod) must not
            // prevent the original game from paying the reward.
            try { itemName = GeneralHelper.GetDisplayNameFromID(__instance.requiredItem); }
            catch { itemName = __instance.requiredItem; }
        }
        LedgerCashSource.Current = new LedgerCashSource
        {
            Type = "市场委托收入",
            Customer = string.IsNullOrWhiteSpace(__instance.requesterName) ? "市场委托" : __instance.requesterName,
            Detail = string.IsNullOrWhiteSpace(__instance.title) ? "领取市场委托信用点奖励" : $"委托：{__instance.title}\n领取信用点奖励",
            Item = string.IsNullOrWhiteSpace(itemName) ? null : new LedgerTradeDetails
            {
                Name = itemName, Identifier = __instance.requiredItem ?? string.Empty,
                Specification = $"交付数量 {__instance.requiredItemCount}"
            }
        };
    }

    private static Exception? Finalizer(Exception? __exception, LedgerCashSource? __state)
    {
        LedgerCashSource.Current = __state;
        return __exception;
    }
}
