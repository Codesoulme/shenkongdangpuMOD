using ScavengeExtraction.Core;
using TMPro;
using UnityEngine;

namespace ScavengeExtraction;

// Use the same machinery panels and real inventories as the vanilla pockets.
internal sealed class NativeRaidPanels
{
    private readonly RaidRuntime raid;
    private readonly MachineryPanelRow row;
    private readonly MachineryPanel attributes, weaponPanel, armorPanel;
    private readonly MachineryText health, bleeding, damage, accuracy, defense, stimulant;
    private readonly PixelWindow window;
    private readonly Vector3 originalScale;
    private readonly Vector2 originalPosition;
    internal readonly GameSlotInventory WeaponSlot = new(3, 3);
    internal readonly GameSlotInventory ArmorSlot = new(3, 3);
    private bool restored, returning;

    internal NativeRaidPanels(RaidRuntime runtime)
    {
        raid = runtime;
        window = EmporiumEntry.Instance.afterhourWindow;
        row = window.childElement as MachineryPanelRow ?? throw new InvalidOperationException("原版夜间背包布局不可用。");
        originalScale = window.rectTransform.localScale;
        originalPosition = window.rectTransform.anchoredPosition;
        health = Label(); bleeding = Label(); damage = Label(); accuracy = Label(); defense = Label(); stimulant = Label();
        var stats = MachineryStack.Vertical(health, bleeding, damage, accuracy, defense, stimulant);
        stats.gap = 4;
        attributes = MachineHelper.Panel("战斗属性", stats, "raid.stats").SetMinWidth(82);
        Configure(WeaponSlot, RaidItems.IsWeapon);
        Configure(ArmorSlot, i => i.IsTag("armor_item"));
        WeaponSlot.SetTooltipName("[武器]"); ArmorSlot.SetTooltipName("[防具]");
        weaponPanel = MachineHelper.Panel("武器", WeaponSlot, "raid.weapon");
        armorPanel = MachineHelper.Panel("防具", ArmorSlot, "raid.armor");
        row.Add(attributes).Add(weaponPanel).Add(armorPanel);
        window.Validate();
        // Upgrade the former selection references by moving the original items.
        var carried = raid.Carried();
        var oldWeapon = carried.FirstOrDefault(i => i.GetUniqueID() == raid.State.Player.WeaponUid);
        var oldArmor = carried.FirstOrDefault(i => i.GetUniqueID() == raid.State.Player.ArmorUid);
        if (oldWeapon != null && RaidItems.IsWeapon(oldWeapon)) RaidItems.Put(WeaponSlot, oldWeapon);
        if (oldArmor?.IsTag("armor_item") == true) RaidItems.Put(ArmorSlot, oldArmor);
        Refresh();
    }
    private static MachineryText Label() => new("", 7, HorizontalAlignmentOptions.Left, false);
    private void Configure(GameSlotInventory slot, Func<GameItem, bool> accepts)
    {
        slot.mayInventoryAddItemFunc = (item, _) => CanChangeEquipment && GeneralHelper.IsItemEffectivelyOwned(item)
            && item.unitCount == 1 && accepts(item);
        slot.mayInventoryRemoveItemFunc = (_, _) => CanChangeEquipment;
        slot.onSlotAddItemLateFunc += (_, _, _) => raid.Save();
        slot.onSlotRemoveItemLateFunc += (_, _, _) => raid.Save();
    }
    private bool CanChangeEquipment => returning || (raid.Active && !raid.Busy && raid.State.Phase == RaidPhase.Exploring);
    internal IEnumerable<GameItem> EquippedRoots => WeaponSlot.childItems.Concat(ArmorSlot.childItems);
    internal void Refresh()
    {
        var p = raid.State.Player; var attack = raid.AttackStats;
        Set(health, $"生命　{p.Hp} / {p.MaxHp}");
        Set(bleeding, p.Bleeding > 0 ? $"流血　-{p.Bleeding} / 行动" : "伤势　稳定");
        Set(damage, $"伤害　{attack.damage}"); Set(accuracy, $"命中　{attack.accuracy}%");
        Set(defense, $"防御　{raid.Defense}"); Set(stimulant, p.StimTurns > 0 ? $"强化　剩余{p.StimTurns}次" : "强化　无");
    }
    private static void Set(MachineryText label, string text) { if (label.GetText() != text) label.SetText(text); }
    internal RectTransform HealthAnchor => health.rectTransform;
    internal Vector3 HealthNumberPosition => health.rectTransform.TransformPoint(new Vector3(
        health.rectTransform.rect.xMin + health.widthPixels * .5f, health.rectTransform.rect.yMax));
    internal void FitToScreen()
    {
        if (restored || !window.IsVisible()) return;
        NativeWindowLayout.Place(window,
            new Rect(Screen.width * .03f, Screen.height * .02f, Screen.width * .94f, Screen.height * .35f),
            originalScale, false);
    }
    internal void ReturnEquipment()
    {
        returning = true;
        try
        {
            foreach (var item in EquippedRoots.ToArray())
            {
                var e = EmporiumEntry.Instance;
                // Prefer the carried bag/pockets: the equipped items often came
                // from them and must take the same vanilla return path.
                var bag = e.afterhourPocketSlotInvBackpack.childItem;
                bool moved = bag != null && RaidItems.Put(bag, item);
                if (!moved) moved = RaidItems.Put(e.afterhourPocketSlotInvLeft, item);
                if (!moved) moved = RaidItems.Put(e.afterhourPocketSlotInvRight, item);
                if (!moved) e.TryAddToPlayerInv(item);
                if (item.parentInventory == WeaponSlot || item.parentInventory == ArmorSlot)
                    RaidItems.Put(e.frontInvinvElement, item);
                if (item.parentInventory == WeaponSlot || item.parentInventory == ArmorSlot)
                    throw new InvalidOperationException("装备尚未转移，已保留装备槽以防物品丢失。");
            }
        }
        finally { returning = false; }
    }
    internal void Detach()
    {
        if (restored) return;
        ReturnEquipment();
        restored = true;
        foreach (var panel in new[] { attributes, weaponPanel, armorPanel })
        {
            row.Detach(panel);
            UnityEngine.Object.Destroy(panel.handler.gameObject);
        }
        window.Validate();
        window.rectTransform.localScale = originalScale;
        window.rectTransform.anchoredPosition = originalPosition;
    }
}

// Fixed-size native content slot for the interactive map and first-person view.
internal sealed class RaidSurface : PixelElement
{
    public bool canResize => false;
    public int widthPixels { get; }
    public int heightPixels { get; }
    public TreeNodeRender handler { get; }
    public RectTransform rectTransform => handler.rectTransform;
    public TreeNodeRender inner => handler;
    public RaidSurface(int width = 460, int height = 340)
    {
        widthPixels = width; heightPixels = height;
        handler = TreeNodeRender.Instantiate(this, "拾荒地图画面");
        handler.image.enabled = false;
        Validate();
    }
    public int XClamp(int x) => 0;
    public int YClamp(int y) => 0;
    public int WidthPixelsClamp(int width) => widthPixels;
    public int HeightPixelsClamp(int height) => heightPixels;
    public void ResizePixels(int width, int height) => Validate();
    public void Validate() => rectTransform.sizeDelta = new Vector2(widthPixels, heightPixels);
    public bool Attach(PixelElement element) => false;
    public bool Detach(PixelElement element) => false;
}
