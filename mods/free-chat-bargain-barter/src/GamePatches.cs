using HarmonyLib;
using AIWorldPawnshop.Compatibility;
using AIWorldPawnshop.Runtime;
using System.Diagnostics;

namespace AIWorldPawnshop;

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.EndNight))]
internal static class PlayerStoreEndNightPatch
{
    private static void Postfix()
    {
        try
        {
            AIWorldPawnshopMod.Instance?.OnNativeDayEnded();
        }
        catch (System.Exception ex)
        {
            MelonLoader.MelonLogger.Error($"[当铺世界] EndNight 钩子失败：{ex.Message}");
        }
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
    private static void Postfix()
    {
        try
        {
            AIWorldPawnshopMod.Instance?.OnNativeSave();
        }
        catch (System.Exception ex)
        {
            MelonLoader.MelonLogger.Error($"[当铺世界] SaveGame 钩子失败：{ex.Message}");
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
