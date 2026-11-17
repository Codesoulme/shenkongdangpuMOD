using HarmonyLib;
using UnityEngine;

namespace ScavengeExtraction;

public sealed class RaidMod : IMod
{
    private GameObject? host;
    private Harmony? patches;
    public void Init(ModManifest manifest) { }
    public void OnEnable()
    {
        if (host != null) return;
        ModHook.OnModItemDirectoryInit += RaidItems.Register;
        ModHook.OnCreateTooltipLate += ScavengingEffects.Tooltip;
        patches = new Harmony("YiWanWan.ScavengeExtraction");
        patches.PatchAll(typeof(RaidMod).Assembly);
        host = new GameObject("ScavengeExtraction.Runtime");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<RaidRuntime>().Install();
    }
    public void OnDisable()
    {
        ModHook.OnModItemDirectoryInit -= RaidItems.Register;
        ModHook.OnCreateTooltipLate -= ScavengingEffects.Tooltip;
        if (host != null) host.GetComponent<RaidRuntime>()?.Uninstall();
        if (patches != null) patches.UnpatchAll(patches.Id); patches = null;
        if (host != null) UnityEngine.Object.Destroy(host);
        host = null;
        RaidItems.Clear();
    }
}

[HarmonyPatch(typeof(MapUIManager), nameof(MapUIManager.ScavengeButtonClick))]
internal static class SearchPatch
{
    private static bool Prefix()
    {
        var raid = RaidRuntime.Instance;
        if (raid?.Active != true) return true;
        raid.Search(); return false;
    }
}

[HarmonyPatch(typeof(MapUIManager), nameof(MapUIManager.Return))]
internal static class ReturnPatch
{
    private static bool Prefix() => RaidRuntime.Instance?.PermitReturn() ?? true;
}

[HarmonyPatch(typeof(MapUIManager), nameof(MapUIManager.ReturnNow))]
internal static class ReturnNowPatch
{
    private static bool Prefix() => RaidRuntime.Instance?.PrepareReturn() ?? true;
}

[HarmonyPatch(typeof(ItemContextHandler), "SetButtonColor")]
internal static class CorpseContextPatch
{
    private static void Postfix(RichTextElement button, GameItem ___currentItem, RichTextElement ___buttonConfigure)
    {
        if (button != ___buttonConfigure || ___currentItem?.identifier != RaidItems.CorpseId) return;
        var ready = RaidRuntime.Instance?.HasBlade == true && !___currentItem.IsTag("RAID_DISMEMBERED");
        button.SetText(ready ? "肢解尸体" : "肢解尸体（需要随身刀具）", 10000,
            ready ? RenderHandler.ColorPalette.LightGray : RenderHandler.ColorPalette.DarkGray);
    }
}

// Route the existing backpack's consume action into the raid; skip vanilla's
// callback so the same unit is not consumed twice and one use costs one turn.
[HarmonyPatch(typeof(GameItem), nameof(GameItem.MayActivateSlotItem))]
internal static class MedicineAvailablePatch
{
    private static bool Prefix(GameItem __instance, ref bool __result)
    {
        if (!MedicineActivation.Applies(__instance)) return true;
        __result = true; return false;
    }
}
[HarmonyPatch(typeof(GameItem), nameof(GameItem.CanActivateSlotItem))]
internal static class MedicineEnabledPatch
{
    private static bool Prefix(GameItem __instance, ref bool __result)
    {
        if (!MedicineActivation.Applies(__instance)) return true;
        var raid = RaidRuntime.Instance!;
        __result = !raid.Busy && raid.State.Phase is Core.RaidPhase.Combat or Core.RaidPhase.Exploring;
        return false;
    }
}
[HarmonyPatch(typeof(GameItem), nameof(GameItem.ActivateSlotItem))]
internal static class MedicineUsePatch
{
    private static bool Prefix(GameItem __instance)
    {
        if (!MedicineActivation.Applies(__instance)) return true;
        RaidRuntime.Instance!.UseMedicine(__instance); return false;
    }
}
internal static class MedicineActivation
{
    internal static bool Applies(GameItem item) => RaidRuntime.Instance is { Active: true } raid
        && ScavengingEffects.Get(item) != null && raid.Carried().Contains(item);
}

// Preserve the native drag cleanup. Cancel native placement and only apply the
// single target dose AFTER its fake item, highlights and cursor have reset.
[HarmonyPatch(typeof(ItemMouseDragHandler), "EndDrag")]
internal static class EnemyDropPatch
{
    private static void Prefix(ref bool cancelled, GameItemElement ___currentItem,
        ref GameInventory ___lastInventory, ref GameItem ___lastItem, out EnemyDropContext? __state)
    {
        __state = null;
        var raid = RaidRuntime.Instance;
        if (cancelled || ___currentItem == null || raid?.Active != true) return;
        var target = raid.View?.EnemyDropTarget(Input.mousePosition, ___currentItem.fakeBackground);
        if (target == null) return;
        __state = new EnemyDropContext(___currentItem, target);
        cancelled = true;
        ___lastInventory = null!; ___lastItem = null!;
    }
    private static void Postfix(EnemyDropContext? __state)
    {
        if (__state == null) return;
        try { RaidRuntime.Instance?.UseEnemyItem(__state.Item, __state.Enemy); }
        catch (Exception error) { Debug.LogError("[ScavengeExtraction] 对人物使用物品失败：" + error); }
    }
    internal sealed class EnemyDropContext
    {
        internal readonly GameItem Item;
        internal readonly Core.EnemyState Enemy;
        internal EnemyDropContext(GameItem item, Core.EnemyState enemy) { Item = item; Enemy = enemy; }
    }
}
