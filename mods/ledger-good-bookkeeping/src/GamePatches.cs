using HarmonyLib;
using AIWorldPawnshop.Compatibility;
using AIWorldPawnshop.Runtime;
using AIWorldPawnshop.UI;
using System.Diagnostics;

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
        try
        {
            CreditLedgerPatchSupport.Record(
                (long)__instance.playerCash - __state, "夜间结算", "夜间服务费及自动结算");
            AIWorldPawnshopMod.Instance?.OnNativeDayEnded();
        }
        catch (System.Exception ex)
        {
            MelonLoader.MelonLogger.Error($"[当铺世界] EndNight 钩子失败：{ex.Message}");
        }
        finally
        {
            CreditLedgerPatchSupport.EndNightSettlement();
        }
    }

    private static System.Exception? Finalizer(System.Exception? __exception)
    {
        if (__exception is not null)
            CreditLedgerPatchSupport.EndNightSettlement();
        return __exception;
    }
}

/// <summary>
/// Projects today's private world-news records into the existing newspaper UI
/// list for this read only call. It does not register or activate native events.
/// </summary>
[HarmonyPatch(typeof(StoreEventManager), nameof(StoreEventManager.GetActiveEvents))]
internal static class StoreEventManagerNewsProjectionPatch
{
    private static void Postfix(
        ref System.Collections.Generic.List<StoreEvent> __result)
    {
        try
        {
            if (!NativeNewsProjectionScope.IsActive &&
                !new StackTrace().ToString().Contains("NewsUIManager", StringComparison.Ordinal))
                return;
            var runtime = AIWorldPawnshopMod.Instance?.Runtime;
            if (runtime is null || __result is null)
                return;

            var knownIdentifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var merged = new List<StoreEvent>();
            foreach (var nativeEvent in __result)
            {
                if (nativeEvent is null)
                    continue;
                var identifier = nativeEvent.identifier ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(identifier) && !knownIdentifiers.Add(identifier))
                    continue;
                merged.Add(nativeEvent);
            }

            foreach (var news in runtime.State.News
                         .Where(x => NativeNewsProjectionRules.ShouldProject(runtime.State, x))
                         .OrderByDescending(x => x.Importance)
                         .ThenByDescending(x => x.NewsId, StringComparer.Ordinal))
            {
                var identifier = "aiworld.news." + news.NewsId;
                if (!knownIdentifiers.Add(identifier))
                    continue;

                merged.Add(new StoreEvent
                {
                    identifier = identifier,
                    newsName = news.Title,
                    displayName = news.Title,
                    newsDescription = news.Body,
                    importance = news.Importance,
                    startDay = runtime.State.CurrentDay,
                    currentDay = runtime.State.CurrentDay,
                    duration = 1,
                    totalDuration = 1,
                    isDurationVisible = false,
                    IsKnown = true,
                    isHidden = false,
                    isHiddenFromNewsPaper = false
                });
            }

            var ordered = merged
                .OrderByDescending(x => x.importance)
                .ThenByDescending(x => x.startDay)
                .ThenBy(x => x.identifier, StringComparer.OrdinalIgnoreCase)
                .ToList();
            __result.Clear();
            foreach (var item in ordered)
                __result.Add(item);
        }
        catch (System.Exception ex)
        {
            MelonLoader.MelonLogger.Warning($"[当铺世界] 原版新闻投影失败：{ex.Message}");
        }
    }
}

internal static class NativeNewsProjectionScope
{
    [ThreadStatic]
    private static int _depth;

    public static bool IsActive => _depth > 0;

    public static void Enter() => _depth++;

    public static void Exit()
    {
        if (_depth > 0)
            _depth--;
    }
}

[HarmonyPatch(typeof(NewsUIManager), nameof(NewsUIManager.PopulateUI))]
internal static class NewsUiManagerPopulatePatch
{
    private static void Prefix(out bool __state)
    {
        NativeNewsProjectionScope.Enter();
        __state = true;
    }

    private static void Postfix(ref bool __state) => ExitScope(ref __state);

    // Harmony finalizers also run when the original method or another patch
    // throws.  Keeping an invocation-local flag makes the cleanup idempotent:
    // the normal Postfix exits once, while an exceptional call is recovered
    // here without corrupting an outer/nested projection scope.
    private static System.Exception? Finalizer(
        System.Exception? __exception,
        ref bool __state)
    {
        ExitScope(ref __state);
        return __exception;
    }

    private static void ExitScope(ref bool entered)
    {
        if (!entered)
            return;
        NativeNewsProjectionScope.Exit();
        entered = false;
    }
}

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.SaveGame))]
internal static class PlayerStoreSaveGamePatch
{
    private static void Postfix(PlayerStore __instance)
    {
        try
        {
            AIWorldPawnshopMod.Instance?.OnNativeSave(__instance.saveSlotId);
        }
        catch (System.Exception ex)
        {
            MelonLoader.MelonLogger.Error($"[当铺世界] SaveGame 钩子失败：{ex.Message}");
        }
    }
}

[HarmonyPatch(typeof(SaveFiles), nameof(SaveFiles.RotateIntoHistory))]
internal static class SaveFilesRotateLedgerSnapshotPatch
{
    private static void Postfix(int __0, string? __result)
    {
        if (!string.IsNullOrWhiteSpace(__result))
            AIWorldPawnshopMod.Instance?.OnNativeSaveRotated(__0, __result);
    }
}

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.LoadGame))]
internal static class PlayerStoreLoadGameLedgerPatch
{
    private static void Postfix(PlayerStore __instance)
    {
        try
        {
            AIWorldPawnshopMod.Instance?.OnNativeGameLoaded(
                __instance.saveSlotId,
                NewGameData.Instance?.saveFile);
        }
        catch (Exception ex)
        {
            MelonLoader.MelonLogger.Warning($"[深空当铺] 读取存档账本快照失败：{ex.Message}");
        }
    }
}

[HarmonyPatch(typeof(StoreClientInstance), nameof(StoreClientInstance.CreateClientInstance))]
internal static class StoreClientInstanceCreatePatch
{
    private static void Prefix(StoreClient __0, out string __state)
    {
        __state = string.Empty;
        try
        {
            __state = AIWorldPawnshopMod.Instance?.PrepareNativeClient(__0) ?? string.Empty;
        }
        catch (System.Exception ex)
        {
            MelonLoader.MelonLogger.Error(
                $"[当铺世界] CreateClientInstance 前置钩子失败：{ex.Message}");
        }
    }

    private static void Postfix(
        StoreClient __0,
        StoreClientInstance __result,
        string __state)
    {
        try
        {
            BargainAgreementRegistry.TrackClient(__0);
            AIWorldPawnshopMod.Instance?.CompleteNativeClientCreation(
                __state,
                __0,
                __result);
        }
        catch (System.Exception ex)
        {
            MelonLoader.MelonLogger.Error(
                $"[当铺世界] CreateClientInstance 后置钩子失败：{ex.Message}");
        }
    }
}

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.OnItemBought))]
internal static class PlayerStoreItemBoughtPatch
{
    private static void Prefix(GameItem __0)
    {
        try
        {
            AIWorldPawnshopMod.Instance?.PrepareNativeItemBought(__0);
        }
        catch (System.Exception ex)
        {
            MelonLoader.MelonLogger.Error(
                $"[当铺世界] OnItemBought 前置钩子失败：{ex.Message}");
        }
    }

    private static void Postfix(GameItem __0, int __1)
    {
        try
        {
            AIWorldPawnshopMod.Instance?.OnNativeItemBought(__0, __1);
        }
        catch (System.Exception ex)
        {
            MelonLoader.MelonLogger.Error(
                $"[当铺世界] OnItemBought 钩子失败：{ex.Message}");
        }
    }
}

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.SellItem))]
internal static class PlayerStoreItemSoldLedgerPatch
{
    private static void Prefix(PlayerStore __instance, out int __state)
    {
        __state = __instance.playerCash;
    }

    private static void Postfix(PlayerStore __instance, GameItem __0, int __state)
    {
        var actualIncome = (long)__instance.playerCash - __state;
        if (actualIncome > 0 && actualIncome <= int.MaxValue)
            AIWorldPawnshopMod.Instance?.OnNativeItemSold(__0, (int)actualIncome);
    }
}

internal static class CreditLedgerPatchSupport
{
    [ThreadStatic] private static int _nightSettlementDepth;
    [ThreadStatic] private static int _observedActionDepth;

    internal static bool IsNightSettlement => _nightSettlementDepth > 0;
    internal static bool IsObservedAction => _observedActionDepth > 0;
    internal static void BeginNightSettlement() => _nightSettlementDepth++;
    internal static void EndNightSettlement()
    {
        if (_nightSettlementDepth > 0)
            _nightSettlementDepth--;
    }

    internal static void ObserveAction(System.Action action, string transactionType)
    {
        var store = PlayerStore.Instance;
        var before = store?.playerCash ?? 0;
        _observedActionDepth++;
        try
        {
            action();
        }
        finally
        {
            _observedActionDepth--;
            var delta = (long)(PlayerStore.Instance?.playerCash ?? before) - before;
            Record(delta, transactionType, "对话选项产生的信用点变动");
        }
    }

    internal static void Record(long delta, string transactionType, string detail) =>
        AIWorldPawnshopMod.Instance?.OnNativeCreditChanged(delta, transactionType, detail);

    internal static string DescribeModCashSource(long delta)
    {
        var caller = new StackTrace().GetFrames()?
            .Select(frame => frame.GetMethod())
            .FirstOrDefault(method => method?.DeclaringType != typeof(PlayerStore) &&
                method?.DeclaringType != typeof(CreditLedgerPatchSupport) &&
                method?.DeclaringType?.Namespace != typeof(AIWorldPawnshopMod).Namespace);
        var type = caller?.DeclaringType?.Name ?? string.Empty;
        var methodName = caller?.Name ?? string.Empty;
        var category = type switch
        {
            "AugHelper" => "改造体结算",
            "CommissaryUIManager" => "兑换信用点",
            "GunOrderData" => "枪械订单结算",
            "LotteryHelper" => "彩票结算",
            "StartingPerkContent" => "特质结算",
            "StoreClientListAppraisal" => "鉴定服务结算",
            "StoreClientListSecurity" => "安保结算",
            "StoreClientListWanted" => "悬赏结算",
            "StoreClientListCartel" => "势力交易结算",
            "StoreClientListMinor" or "StoreClientListSpec" => "特殊事件结算",
            "StoreClientUniqueList" => "特殊顾客结算",
            "StoreRequest" => "委托结算",
            "VendingMachineHelper" => "售货机结算",
            "StoreClientList" => "顾客事件结算",
            _ => delta < 0 ? "其他支出" : "其他收入"
        };
        var detail = string.IsNullOrWhiteSpace(type)
            ? "系统信用点变动"
            : $"{type} · {methodName}";
        return category + "|" + detail;
    }
}

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.ModCash))]
internal static class PlayerStoreModCashLedgerPatch
{
    private static void Prefix(PlayerStore __instance, out int __state) => __state = __instance.playerCash;

    private static void Postfix(PlayerStore __instance, int __state)
    {
        // EndNight records one aggregate delta because that method mixes
        // direct field writes and ModCash calls during the same settlement.
        if (CreditLedgerPatchSupport.IsNightSettlement || CreditLedgerPatchSupport.IsObservedAction) return;
        var delta = (long)__instance.playerCash - __state;
        if (delta == 0) return;
        var source = CreditLedgerPatchSupport.DescribeModCashSource(delta).Split('|');
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
            var client = FormalGameAccess.GetClient(store?.currentClientInstance);
            var delta = (long)(store?.playerCash ?? __state) - __state;
            if (delta < 0 && client?.clientIntent == StoreClient.ClientIntent.INFORMATION_DEALER)
                AIWorldPawnshopMod.Instance?.OnNativeInformationPurchased(
                    (int)Math.Min(-delta, int.MaxValue),
                    string.IsNullOrWhiteSpace(client.realName) ? client.displayName : client.realName);
        }
        catch (Exception ex)
        {
            MelonLoader.MelonLogger.Warning($"[深空当铺] 情报购买流水记录失败：{ex.Message}");
        }
    }
}

[HarmonyPatch(typeof(WildNetworkUI), "OnUseClicked")]
internal static class WildNetworkPurchaseCreditLedgerPatch
{
    private static void Prefix(out int __state) => __state = PlayerStore.Instance?.playerCash ?? 0;
    private static void Postfix(int __state)
    {
        var delta = (long)(PlayerStore.Instance?.playerCash ?? __state) - __state;
        CreditLedgerPatchSupport.Record(delta, "野路子网络升级", "使用野路子人情不足部分以信用点支付");
    }
}

[HarmonyPatch(typeof(WildUIManager), nameof(WildUIManager.OnUnlockClicked))]
internal static class WildUpgradePurchaseCreditLedgerPatch
{
    private static void Prefix(out int __state) => __state = PlayerStore.Instance?.playerCash ?? 0;
    private static void Postfix(int __state)
    {
        var delta = (long)(PlayerStore.Instance?.playerCash ?? __state) - __state;
        CreditLedgerPatchSupport.Record(delta, "野路子升级", "人情不足部分以信用点支付");
    }
}

[HarmonyPatch(typeof(Dialogue), nameof(Dialogue.SetStartAction))]
internal static class DialogueStartActionCreditLedgerPatch
{
    private static void Prefix(ref System.Action startAction)
    {
        if (startAction is null) return;
        var original = startAction;
        startAction = () => CreditLedgerPatchSupport.ObserveAction(original, "剧情选项结算");
    }
}

[HarmonyPatch(typeof(Dialogue), nameof(Dialogue.SetEndAction))]
internal static class DialogueEndActionCreditLedgerPatch
{
    private static void Prefix(ref System.Action endAction)
    {
        if (endAction is null) return;
        var original = endAction;
        endAction = () => CreditLedgerPatchSupport.ObserveAction(original, "剧情选项结算");
    }
}

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.OnRentPaid))]
internal static class PlayerStoreRentLedgerPatch
{
    private static void Prefix(PlayerStore __instance, out int __state) => __state = __instance.playerCash;
    private static void Postfix(PlayerStore __instance, int __state) =>
        CreditLedgerPatchSupport.Record((long)__instance.playerCash - __state, "支付房租", "房租及欠租");
}

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.PayWeeklyMortgage))]
internal static class PlayerStoreMortgagePaymentLedgerPatch
{
    private static void Prefix(PlayerStore __instance, out int __state) => __state = __instance.playerCash;
    private static void Postfix(PlayerStore __instance, int __state) =>
        CreditLedgerPatchSupport.Record((long)__instance.playerCash - __state, "偿还房贷", "每周房贷");
}

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.BuyOutStore))]
internal static class PlayerStoreBuyOutLedgerPatch
{
    private static void Prefix(PlayerStore __instance, out int __state) => __state = __instance.playerCash;
    private static void Postfix(PlayerStore __instance, int __state) =>
        CreditLedgerPatchSupport.Record((long)__instance.playerCash - __state, "购买房产", "一次性买断店铺");
}

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.TakeMortgage))]
internal static class PlayerStoreTakeMortgageLedgerPatch
{
    private static void Prefix(PlayerStore __instance, out int __state) => __state = __instance.playerCash;
    private static void Postfix(PlayerStore __instance, int __state) =>
        CreditLedgerPatchSupport.Record((long)__instance.playerCash - __state, "办理房贷", "房贷首付款");
}

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.PayMoney))]
internal static class PlayerStorePayMoneyLedgerPatch
{
    [ThreadStatic] private static bool _insideForeclosure;
    internal static bool InsideForeclosure { get => _insideForeclosure; set => _insideForeclosure = value; }
    private static void Prefix(PlayerStore __instance, out int __state) => __state = __instance.playerCash;
    private static void Postfix(PlayerStore __instance, int __state)
    {
        if (!InsideForeclosure)
            CreditLedgerPatchSupport.Record((long)__instance.playerCash - __state, "罚款或贿赂", "现金付款");
    }
}

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.PaidOrForeclose))]
internal static class PlayerStoreForeclosureLedgerPatch
{
    private struct ForeclosureState
    {
        internal int Cash;
        internal bool WasInsideForeclosure;
    }

    private static void Prefix(PlayerStore __instance, out ForeclosureState __state)
    {
        __state = new ForeclosureState
        {
            Cash = __instance.playerCash,
            WasInsideForeclosure = PlayerStorePayMoneyLedgerPatch.InsideForeclosure
        };
        PlayerStorePayMoneyLedgerPatch.InsideForeclosure = true;
    }

    private static void Postfix(PlayerStore __instance, ForeclosureState __state)
    {
        RestoreScope(__state);
        CreditLedgerPatchSupport.Record(
            (long)__instance.playerCash - __state.Cash, "检查罚款", "缴费或没收现金");
    }

    private static Exception? Finalizer(Exception? __exception, ForeclosureState __state)
    {
        RestoreScope(__state);
        return __exception;
    }

    private static void RestoreScope(ForeclosureState state) =>
        PlayerStorePayMoneyLedgerPatch.InsideForeclosure = state.WasInsideForeclosure;
}

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.BuyAllItemFromTable))]
internal static class PlayerStoreBuyAllLedgerPatch
{
    private static void Prefix(PlayerStore __instance, out int __state) => __state = __instance.playerCash;
    private static void Postfix(PlayerStore __instance, int __state) =>
        CreditLedgerPatchSupport.Record((long)__instance.playerCash - __state, "批量购入", "批量购买顾客物品");
}

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.SellAllItemFromTable))]
internal static class PlayerStoreSellAllLedgerPatch
{
    private static void Prefix(PlayerStore __instance, out int __state) => __state = __instance.playerCash;
    private static void Postfix(PlayerStore __instance, int __state) =>
        CreditLedgerPatchSupport.Record((long)__instance.playerCash - __state, "批量出售", "批量出售物品");
}

[HarmonyPatch(typeof(AppraisalServiceHelper), nameof(AppraisalServiceHelper.GiveAppraisalFee))]
internal static class AppraisalServiceCreditLedgerPatch
{
    private static void Prefix(out int __state) => __state = PlayerStore.Instance?.playerCash ?? 0;
    private static void Postfix(int __state)
    {
        var cash = PlayerStore.Instance?.playerCash ?? __state;
        CreditLedgerPatchSupport.Record((long)cash - __state, "鑑定服務收入", "提供物品鑑定");
    }
}

[HarmonyPatch(typeof(CurrentConverterHelper), nameof(CurrentConverterHelper.Supply))]
internal static class CreditConverterLedgerPatch
{
    private static void Prefix(out int __state) => __state = PlayerStore.Instance?.playerCash ?? 0;
    private static void Postfix(GameItem __0, int __1, int __state)
    {
        var cash = PlayerStore.Instance?.playerCash ?? __state;
        CreditLedgerPatchSupport.Record((long)cash - __state, "供能转换", $"能源转换消耗 {__1}");
    }
}

[HarmonyPatch(typeof(StoreUIManager), nameof(StoreUIManager.OnGenericLeft))]
internal static class SecurityReportCreditLedgerPatch
{
    private static void Prefix(string __0, out int __state) =>
        __state = __0 == "SEC" ? PlayerStore.Instance?.playerCash ?? 0 : int.MinValue;

    private static void Postfix(string __0, int __state)
    {
        if (__state == int.MinValue) return;
        var cash = PlayerStore.Instance?.playerCash ?? __state;
        CreditLedgerPatchSupport.Record((long)cash - __state, "安保报告结算", "举报奖金或误报罚款");
    }
}

[HarmonyPatch(typeof(AdvCalendarUIManager), nameof(AdvCalendarUIManager.Init))]
internal static class CalendarLedgerTabPatch
{
    private static void Postfix(AdvCalendarUIManager __instance)
    {
        try
        {
            NativeLedgerTab.Attach(__instance);
        }
        catch (Exception ex)
        {
            MelonLoader.MelonLogger.Warning($"[深空当铺] 注入原生日历标签失败：{ex.Message}");
        }
    }
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

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.DismissCurrentClient))]
internal static class PlayerStoreDismissClientPatch
{
    private static void Prefix(PlayerStore __instance, out StoreClient? __state)
    {
        __state = null;
        try
        {
            __state = FormalGameAccess.GetClient(__instance.currentClientInstance);
        }
        catch { }
    }

    private static void Postfix(StoreClient? __state)
    {
        try
        {
            NativeBarterAdapter.RestoreOriginalIntent(__state);
            AIWorldPawnshopMod.Instance?.OnNativeClientDismissed(__state);
            BargainAgreementRegistry.TrackClient(null);
        }
        catch (System.Exception ex)
        {
            MelonLoader.MelonLogger.Error(
                $"[当铺世界] DismissCurrentClient 钩子失败：{ex.Message}");
        }
    }
}

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.AcceptBarter))]
internal static class PlayerStoreAcceptBarterPatch
{
    private static bool Prefix()
    {
        try
        {
            // A false result cancels the native confirmation.  This is
            // important because the unpatched method intentionally accepts
            // every object in the two counter grids.
            return NativeBarterAdapter.ValidateBeforeAccept();
        }
        catch (System.Exception ex)
        {
            MelonLoader.MelonLogger.Error($"[当铺世界] AcceptBarter 校验失败：{ex.Message}");
            NativeBarterAdapter.RestoreOriginalIntent();
            return false;
        }
    }

    private static void Postfix()
    {
        try
        {
            NativeBarterAdapter.CompleteAcceptedTransaction();
            NativeBarterAdapter.RestoreOriginalIntent();
        }
        catch (System.Exception ex)
        {
            MelonLoader.MelonLogger.Error($"[当铺世界] AcceptBarter 恢复钩子失败：{ex.Message}");
        }
    }
}

// The native barter panel lists every unowned counter item. Restrict that
// visual list to the exact customer item only while a Mod offer is rendered.
[HarmonyPatch(typeof(NegociationUIManager), "BuildBarter")]
internal static class NegotiationBuildBarterUiScopePatch
{
    [ThreadStatic] private static int _depth;

    internal static bool IsActive => _depth > 0;

    private static void Prefix(StoreClient __0, out bool __state)
    {
        __state = NativeBarterAdapter.IsActiveUiOffer(__0);
        if (__state)
            _depth++;
    }

    private static void Postfix(ref bool __state) => Exit(ref __state);

    private static Exception? Finalizer(Exception? __exception, ref bool __state)
    {
        Exit(ref __state);
        return __exception;
    }

    private static void Exit(ref bool entered)
    {
        if (!entered)
            return;
        _depth--;
        entered = false;
    }
}

[HarmonyPatch(typeof(EmporiumEntry), nameof(EmporiumEntry.GetAllNonOwnedItem))]
internal static class EmporiumEntryBarterDisplayItemsPatch
{
    private static void Postfix(ref List<GameItem> __result)
    {
        if (NegotiationBuildBarterUiScopePatch.IsActive && __result is not null)
            __result = NativeBarterAdapter.FilterDisplayedClientItems(__result);
    }
}

[HarmonyPatch(typeof(BarterOffer), nameof(BarterOffer.OnBarterAccepted))]
internal static class BarterOfferOnBarterAcceptedPatch
{
    private static bool Prefix(BarterOffer __instance)
    {
        try
        {
            // Ordinary native offers keep the game's original behaviour.  A
            // Mod-created offer is narrowed to the runtime keys captured by
            // NativeBarterAdapter, so duplicate identifiers cannot be sold as
            // a batch.
            return !NativeBarterAdapter.SkipNativeBarterAccepted(__instance);
        }
        catch (System.Exception ex)
        {
            MelonLoader.MelonLogger.Error(
                $"[当铺世界] 精确交换成交记录钩子失败：{ex.Message}");
            // Do not let a recognised Mod snapshot fall through to the native
            // all-items loop after an exception.  The AcceptBarter validation
            // and transfer hooks will cancel the rest of the transaction.
            return !NativeBarterAdapter.HasActiveExecution;
        }
    }
}

[HarmonyPatch(typeof(BarterOffer), nameof(BarterOffer.GetItemPlacedResponse))]
internal static class BarterOfferGetItemPlacedResponsePatch
{
    private static void Postfix(BarterOffer __instance, ref string __result)
    {
        try
        {
            if (NativeBarterAdapter.TryOverridePlacedItemResponse(__instance, out var response))
                __result = response;
        }
        catch (System.Exception ex)
        {
            MelonLoader.MelonLogger.Warning(
                $"[当铺世界] 交换放置物回复覆盖失败：{ex.Message}");
        }
    }
}

[HarmonyPatch(typeof(EmporiumEntry), nameof(EmporiumEntry.TransferOwnershipFrontInv))]
internal static class EmporiumEntryTransferOwnershipFrontInvPatch
{
    private static bool Prefix(EmporiumEntry __instance)
    {
        try
        {
            return !NativeBarterAdapter.SkipNativeTransferOwnershipFront(__instance);
        }
        catch (System.Exception ex)
        {
            // Never fall through to the native all-items loop after a Mod
            // snapshot has been created.
            MelonLoader.MelonLogger.Error(
                $"[当铺世界] 精确交换顾客归属钩子失败：{ex.Message}");
            return false;
        }
    }
}

[HarmonyPatch(typeof(EmporiumEntry), nameof(EmporiumEntry.TransferOwnershipBackInv))]
internal static class EmporiumEntryTransferOwnershipBackInvPatch
{
    private static bool Prefix(EmporiumEntry __instance)
    {
        try
        {
            return !NativeBarterAdapter.SkipNativeTransferOwnershipBack(__instance);
        }
        catch (System.Exception ex)
        {
            MelonLoader.MelonLogger.Error(
                $"[当铺世界] 精确交换玩家归属钩子失败：{ex.Message}");
            return false;
        }
    }
}

[HarmonyPatch(typeof(EmporiumEntry), nameof(EmporiumEntry.SwapInv))]
internal static class EmporiumEntrySwapInvPatch
{
    private static bool Prefix(EmporiumEntry __instance)
    {
        try
        {
            return !NativeBarterAdapter.SkipNativeSwap(__instance);
        }
        catch (System.Exception ex)
        {
            MelonLoader.MelonLogger.Error(
                $"[当铺世界] 精确交换转移钩子失败：{ex.Message}");
            return false;
        }
    }
}
