using HarmonyLib;
using Il2Cpp;
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
        ref Il2CppSystem.Collections.Generic.List<StoreEvent> __result)
    {
        try
        {
            if (!new StackTrace().ToString().Contains("NewsUIManager", StringComparison.Ordinal))
                return;
            var runtime = AIWorldPawnshopMod.Instance?.Runtime;
            if (runtime is null)
                return;

            foreach (var news in runtime.State.News
                         .Where(x => x.Day == runtime.State.CurrentDay)
                         .OrderByDescending(x => x.Importance)
                         .Take(4))
            {
                __result.Add(new StoreEvent
                {
                    identifier = "aiworld.news." + news.NewsId,
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
        }
        catch (System.Exception ex)
        {
            MelonLoader.MelonLogger.Warning($"[当铺世界] 原版新闻投影失败：{ex.Message}");
        }
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
            __state = __instance.currentClientInstance?.GetClientBlueprint();
        }
        catch
        {
            __state = __instance.currentClientInstance?.storeClient;
        }
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
