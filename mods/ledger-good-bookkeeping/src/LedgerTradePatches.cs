using AIWorldPawnshop.Compatibility;
using HarmonyLib;

namespace AIWorldPawnshop;

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.OnItemBought))]
internal static class PlayerStoreItemBoughtPatch
{
    private static void Prefix(GameItem __0, out LedgerTradeDetails? __state)
    {
        WholesaleCreditToastPatch.ObservePayment();
        __state = PlayerStoreBuyItemLedgerPatch.IsBuying ? null : LedgerTradeDetails.Capture(__0, wholesale: true);
    }

    // Wholesale pays once before invoking OnItemBought for each item.
    private static void Postfix(PlayerStore __instance, int __1, LedgerTradeDetails? __state)
    {
        if (__state is not null)
            AIWorldPawnshopMod.Instance?.OnNativeItemBought(__state, __1, __instance.playerCash);
    }
}

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.BuyItem))]
internal static class PlayerStoreBuyItemLedgerPatch
{
    [ThreadStatic] private static int _depth;
    internal static bool IsBuying => _depth > 0;
    internal sealed class State
    {
        internal int Cash;
        internal LedgerTradeDetails Item = null!;
    }
    private static void Prefix(PlayerStore __instance, GameItem __0, out State __state)
    {
        __state = new State { Cash = __instance.playerCash, Item = LedgerTradeDetails.Capture(__0) };
        _depth++;
    }
    private static Exception? Finalizer(PlayerStore __instance, Exception? __exception, State __state)
    {
        try
        {
            // Unlike OnItemBought, BuyItem has already deducted the payment.
            // Record real spending even if moving the paid item later failed.
            if (__state is not null)
            {
                var spent = (long)__state.Cash - __instance.playerCash;
                if (spent > 0 && spent <= int.MaxValue)
                    AIWorldPawnshopMod.Instance?.OnNativeItemBought(__state.Item, (int)spent, __instance.playerCash);
            }
        }
        catch (Exception ex) { UnityEngine.Debug.LogWarning($"[账本] 记录购入流水失败：{ex.Message}"); }
        finally { if (__state is not null && _depth > 0) _depth--; }
        return __exception;
    }
}

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.SellItem))]
internal static class PlayerStoreItemSoldLedgerPatch
{
    private static void Prefix(PlayerStore __instance, GameItem __0, out PlayerStoreBuyItemLedgerPatch.State __state) =>
        __state = new PlayerStoreBuyItemLedgerPatch.State { Cash = __instance.playerCash, Item = LedgerTradeDetails.Capture(__0) };

    private static void Postfix(PlayerStore __instance, PlayerStoreBuyItemLedgerPatch.State __state)
    {
        var income = (long)__instance.playerCash - __state.Cash;
        if (income > 0 && income <= int.MaxValue) AIWorldPawnshopMod.Instance?.OnNativeItemSold(__state.Item, (int)income, __instance.playerCash);
    }
}
