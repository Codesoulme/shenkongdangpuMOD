using System.Diagnostics;
using HarmonyLib;
using AIWorldPawnshop.Compatibility;
using AIWorldPawnshop.Runtime;
using AIWorldPawnshop.UI;

namespace AIWorldPawnshop;

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.EndNight))]
internal static class PlayerStoreEndNightPatch
{
    private static void Prefix(PlayerStore __instance, out int __state)
    {
        __state = __instance.playerCash;
        CreditLedgerPatchSupport.BeginNightSettlement();
    }

    private static void Postfix(PlayerStore __instance, int __state)
    {
        try { CreditLedgerPatchSupport.Record((long)__instance.playerCash - __state, "夜间结算", "夜间服务费及自动结算"); }
        finally { CreditLedgerPatchSupport.EndNightSettlement(); }
    }

    private static Exception? Finalizer(Exception? __exception)
    {
        if (__exception is not null) CreditLedgerPatchSupport.EndNightSettlement();
        return __exception;
    }
}

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.SaveGame))]
internal static class PlayerStoreSaveGamePatch
{
    private static void Postfix(PlayerStore __instance) => AIWorldPawnshopMod.Instance?.OnNativeSave(__instance.saveSlotId);
}

[HarmonyPatch(typeof(SaveFiles), nameof(SaveFiles.RotateIntoHistory))]
internal static class SaveFilesRotateLedgerSnapshotPatch
{
    private static void Postfix(int __0, string? __result)
    {
        if (!string.IsNullOrWhiteSpace(__result)) AIWorldPawnshopMod.Instance?.OnNativeSaveRotated(__0, __result);
    }
}

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.LoadGame))]
internal static class PlayerStoreLoadGameLedgerPatch
{
    private static void Postfix(PlayerStore __instance) =>
        AIWorldPawnshopMod.Instance?.OnNativeGameLoaded(__instance.saveSlotId, NewGameData.Instance?.saveFile);
}

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.OnItemBought))]
internal static class PlayerStoreItemBoughtPatch
{
    private static void Postfix(GameItem __0, int __1) => AIWorldPawnshopMod.Instance?.OnNativeItemBought(__0, __1);
}

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.SellItem))]
internal static class PlayerStoreItemSoldLedgerPatch
{
    private static void Prefix(PlayerStore __instance, out int __state) => __state = __instance.playerCash;

    private static void Postfix(PlayerStore __instance, GameItem __0, int __state)
    {
        var income = (long)__instance.playerCash - __state;
        if (income > 0 && income <= int.MaxValue) AIWorldPawnshopMod.Instance?.OnNativeItemSold(__0, (int)income);
    }
}

internal static class CreditLedgerPatchSupport
{
    [ThreadStatic] private static int _nightSettlementDepth;
    [ThreadStatic] private static int _observedActionDepth;

    internal static bool IsNightSettlement => _nightSettlementDepth > 0;
    internal static bool IsObservedAction => _observedActionDepth > 0;
    internal static void BeginNightSettlement() => _nightSettlementDepth++;
    internal static void EndNightSettlement() { if (_nightSettlementDepth > 0) _nightSettlementDepth--; }

    internal static void ObserveAction(Action action, string transactionType)
    {
        var before = PlayerStore.Instance?.playerCash ?? 0;
        _observedActionDepth++;
        try { action(); }
        finally
        {
            _observedActionDepth--;
            CreditLedgerRuntime.RecordFromPatch((long)(PlayerStore.Instance?.playerCash ?? before) - before,
                transactionType, "对话选项产生的信用点变动");
        }
    }

    internal static void Record(long delta, string transactionType, string detail) =>
        CreditLedgerRuntime.RecordFromPatch(delta, transactionType, detail);
}

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.ModCash))]
internal static class PlayerStoreModCashLedgerPatch
{
    private static void Prefix(PlayerStore __instance, out int __state) => __state = __instance.playerCash;

    private static void Postfix(PlayerStore __instance, int __state)
    {
        if (CreditLedgerPatchSupport.IsNightSettlement || CreditLedgerPatchSupport.IsObservedAction) return;
        var delta = (long)__instance.playerCash - __state;
        if (delta == 0) return;
        var source = CreditLedgerRuntime.DescribeModCashSource(delta).Split('|');
        CreditLedgerPatchSupport.Record(delta, source[0], source[1]);
    }
}

[HarmonyPatch(typeof(NegociationUIManager), nameof(NegociationUIManager.OnOfferAcceptClick))]
internal static class InformationPurchaseCreditLedgerPatch
{
    private static void Prefix(out int __state) => __state = PlayerStore.Instance?.playerCash ?? 0;

    private static void Postfix(int __state)
    {
        try
        {
            var store = PlayerStore.Instance;
            var client = LedgerGameAccess.GetClient(store?.currentClientInstance);
            var delta = (long)(store?.playerCash ?? __state) - __state;
            if (delta < 0 && client?.clientIntent == StoreClient.ClientIntent.INFORMATION_DEALER)
                AIWorldPawnshopMod.Instance?.OnNativeInformationPurchased(
                    (int)Math.Min(-delta, int.MaxValue),
                    string.IsNullOrWhiteSpace(client.realName) ? client.displayName : client.realName);
        }
        catch (Exception ex) { UnityEngine.Debug.LogWarning($"[账本] 情报购买流水记录失败：{ex.Message}"); }
    }
}

[HarmonyPatch(typeof(WildNetworkUI), "OnUseClicked")]
internal static class WildNetworkPurchaseCreditLedgerPatch
{
    private static void Prefix(out int __state) => __state = PlayerStore.Instance?.playerCash ?? 0;
    private static void Postfix(int __state) => CreditLedgerPatchSupport.Record(
        (long)(PlayerStore.Instance?.playerCash ?? __state) - __state, "野路子网络升级", "使用野路子人情不足部分以信用点支付");
}

[HarmonyPatch(typeof(WildUIManager), nameof(WildUIManager.OnUnlockClicked))]
internal static class WildUpgradePurchaseCreditLedgerPatch
{
    private static void Prefix(out int __state) => __state = PlayerStore.Instance?.playerCash ?? 0;
    private static void Postfix(int __state) => CreditLedgerPatchSupport.Record(
        (long)(PlayerStore.Instance?.playerCash ?? __state) - __state, "野路子升级", "人情不足部分以信用点支付");
}

[HarmonyPatch(typeof(Dialogue), nameof(Dialogue.SetStartAction))]
internal static class DialogueStartActionCreditLedgerPatch
{
    private static void Prefix(ref Action startAction)
    {
        if (startAction is null) return;
        var original = startAction;
        startAction = () => CreditLedgerPatchSupport.ObserveAction(original, "剧情选项结算");
    }
}

[HarmonyPatch(typeof(Dialogue), nameof(Dialogue.SetEndAction))]
internal static class DialogueEndActionCreditLedgerPatch
{
    private static void Prefix(ref Action endAction)
    {
        if (endAction is null) return;
        var original = endAction;
        endAction = () => CreditLedgerPatchSupport.ObserveAction(original, "剧情选项结算");
    }
}

internal static class DirectCashChangePatch
{
    internal static void Record(PlayerStore store, int before, string type, string detail) =>
        CreditLedgerPatchSupport.Record((long)store.playerCash - before, type, detail);
}

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.OnRentPaid))]
internal static class PlayerStoreRentLedgerPatch
{
    private static void Prefix(PlayerStore __instance, out int __state) => __state = __instance.playerCash;
    private static void Postfix(PlayerStore __instance, int __state) => DirectCashChangePatch.Record(__instance, __state, "支付房租", "房租及欠租");
}

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.PayWeeklyMortgage))]
internal static class PlayerStoreMortgagePaymentLedgerPatch
{
    private static void Prefix(PlayerStore __instance, out int __state) => __state = __instance.playerCash;
    private static void Postfix(PlayerStore __instance, int __state) => DirectCashChangePatch.Record(__instance, __state, "偿还房贷", "每周房贷");
}

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.BuyOutStore))]
internal static class PlayerStoreBuyOutLedgerPatch
{
    private static void Prefix(PlayerStore __instance, out int __state) => __state = __instance.playerCash;
    private static void Postfix(PlayerStore __instance, int __state) => DirectCashChangePatch.Record(__instance, __state, "购买房产", "一次性买断店铺");
}

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.TakeMortgage))]
internal static class PlayerStoreTakeMortgageLedgerPatch
{
    private static void Prefix(PlayerStore __instance, out int __state) => __state = __instance.playerCash;
    private static void Postfix(PlayerStore __instance, int __state) => DirectCashChangePatch.Record(__instance, __state, "办理房贷", "房贷首付款");
}

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.PayMoney))]
internal static class PlayerStorePayMoneyLedgerPatch
{
    [ThreadStatic] private static bool _insideForeclosure;
    internal static bool InsideForeclosure { get => _insideForeclosure; set => _insideForeclosure = value; }
    private static void Prefix(PlayerStore __instance, out int __state) => __state = __instance.playerCash;
    private static void Postfix(PlayerStore __instance, int __state)
    {
        if (!InsideForeclosure) DirectCashChangePatch.Record(__instance, __state, "罚款或贿赂", "现金付款");
    }
}

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.PaidOrForeclose))]
internal static class PlayerStoreForeclosureLedgerPatch
{
    private struct State { internal int Cash; internal bool WasInside; }
    private static void Prefix(PlayerStore __instance, out State __state)
    {
        __state = new State { Cash = __instance.playerCash, WasInside = PlayerStorePayMoneyLedgerPatch.InsideForeclosure };
        PlayerStorePayMoneyLedgerPatch.InsideForeclosure = true;
    }
    private static void Postfix(PlayerStore __instance, State __state)
    {
        Restore(__state);
        DirectCashChangePatch.Record(__instance, __state.Cash, "检查罚款", "缴费或没收现金");
    }
    private static Exception? Finalizer(Exception? __exception, State __state) { Restore(__state); return __exception; }
    private static void Restore(State state) => PlayerStorePayMoneyLedgerPatch.InsideForeclosure = state.WasInside;
}

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.BuyAllItemFromTable))]
internal static class PlayerStoreBuyAllLedgerPatch
{
    private static void Prefix(PlayerStore __instance, out int __state) => __state = __instance.playerCash;
    private static void Postfix(PlayerStore __instance, int __state) => DirectCashChangePatch.Record(__instance, __state, "批量购入", "批量购买顾客物品");
}

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.SellAllItemFromTable))]
internal static class PlayerStoreSellAllLedgerPatch
{
    private static void Prefix(PlayerStore __instance, out int __state) => __state = __instance.playerCash;
    private static void Postfix(PlayerStore __instance, int __state) => DirectCashChangePatch.Record(__instance, __state, "批量出售", "批量出售物品");
}

[HarmonyPatch(typeof(AppraisalServiceHelper), nameof(AppraisalServiceHelper.GiveAppraisalFee))]
internal static class AppraisalServiceCreditLedgerPatch
{
    private static void Prefix(out int __state) => __state = PlayerStore.Instance?.playerCash ?? 0;
    private static void Postfix(int __state) => CreditLedgerPatchSupport.Record(
        (long)(PlayerStore.Instance?.playerCash ?? __state) - __state, "鉴定服务收入", "提供物品鉴定");
}

[HarmonyPatch(typeof(CurrentConverterHelper), nameof(CurrentConverterHelper.Supply))]
internal static class CreditConverterLedgerPatch
{
    private static void Prefix(out int __state) => __state = PlayerStore.Instance?.playerCash ?? 0;
    private static void Postfix(int __1, int __state) => CreditLedgerPatchSupport.Record(
        (long)(PlayerStore.Instance?.playerCash ?? __state) - __state, "供能转换", $"能源转换消耗 {__1}");
}

[HarmonyPatch(typeof(StoreUIManager), nameof(StoreUIManager.OnGenericLeft))]
internal static class SecurityReportCreditLedgerPatch
{
    private static void Prefix(string __0, out int __state) => __state = __0 == "SEC" ? PlayerStore.Instance?.playerCash ?? 0 : int.MinValue;
    private static void Postfix(string __0, int __state)
    {
        if (__state == int.MinValue) return;
        CreditLedgerPatchSupport.Record((long)(PlayerStore.Instance?.playerCash ?? __state) - __state, "安保报告结算", "举报奖金或误报罚款");
    }
}

[HarmonyPatch(typeof(AdvCalendarUIManager), nameof(AdvCalendarUIManager.Init))]
internal static class CalendarLedgerTabPatch
{
    private static void Postfix(AdvCalendarUIManager __instance) => NativeLedgerTab.Attach(__instance);
}

[HarmonyPatch(typeof(AdvCalendarUIManager), nameof(AdvCalendarUIManager.OpenUI))]
internal static class CalendarLedgerOpenPatch
{
    private static void Postfix(AdvCalendarUIManager __instance) => NativeLedgerTab.Open(__instance);
}

[HarmonyPatch(typeof(AdvCalendarUIManager), nameof(AdvCalendarUIManager.OnCalendarButtonClicked))]
internal static class CalendarLedgerCalendarSelectedPatch
{
    private static void Postfix(AdvCalendarUIManager __instance) => NativeLedgerTab.ShowCalendar(__instance);
}

[HarmonyPatch(typeof(AdvCalendarUIManager), nameof(AdvCalendarUIManager.OnAnySectionButtonClicked))]
internal static class CalendarLedgerSectionResetPatch
{
    private static void Postfix(AdvCalendarUIManager __instance) => NativeLedgerTab.Hide(__instance);
}

[HarmonyPatch(typeof(AdvCalendarUIManager), nameof(AdvCalendarUIManager.CloseUI))]
internal static class CalendarLedgerClosePatch
{
    private static void Postfix(AdvCalendarUIManager __instance) => NativeLedgerTab.Hide(__instance);
}
