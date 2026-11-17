using ScavengeExtraction.Core;

namespace ScavengeExtraction;

internal static class RaidItems
{
    internal const string ModId = "ScavengeExtraction";
    internal const string CacheId = "raid.unsearched";
    internal const string CorpseId = "raid.corpse";
    internal const string PackId = "raid.defeated_pack";
    internal const string PartId = "raid.body_part";
    internal const string NodeTag = "RAID_NODE";
    internal static readonly Dictionary<GameItem, GameGridInventory> Cargo = new();
    internal static readonly Dictionary<GameItem, GameSlotInventory> Weapons = new();
    internal static readonly Dictionary<GameItem, GameSlotInventory> Armor = new();

    internal static void Register(ModItemDirectory dir)
    {
        dir.Add(CacheId, MakeCache);
        dir.Add(CorpseId, () => MakeRemains(true));
        dir.Add(PackId, () => MakeRemains(false));
        dir.Add(PartId, () => Make(PartId, "肢解残骸", "拾荒原型的身体残骸；没有食物或医疗效果。", "part"));
    }

    private static GameItem Make(string id, string name, string description, string sprite)
    {
        var item = ItemDirectory.CreateEmptyItem(id);
        item.name = name;
        item.shortDescription = description;
        item.unitValue = 0;
        item.SetSpriteAndShapeFromMod(ModId, sprite);
        return item;
    }

    private static GameItem MakeCache()
    {
        // Deserialization compatibility only; new searches never create caches.
        var item = Make(CacheId, "旧版拾荒物品", "旧版拾荒留下的物品，打开后可取出。", "pack");
        var cargo = new GameGridInventory(16, 12);
        Cargo[item] = cargo;
        item.EnableTag("IS_UNLOAD_DISABLED_TAG");
        item.SetShape(new GridShapeBuilder(2, 2).SetDataFill((byte)1));
        item.SetContentWindow(MachineHelper.CreateMachineryWindow(true, MachineHelper.Panel("拾荒物品", cargo, "legacy")));
        item.contentWindow.isLocked = false;
        item.mayActivateSlotItemFunc = (_, _, _) => true;
        item.canActivateSlotItemFunc = (_, _, _) => RaidRuntime.Instance?.Active == true;
        item.onActivateSlotItemFunc = (_, _, _) => RaidRuntime.Instance?.RecoverLegacyCaches();
        // Retain this old container on the ground; contents can be retrieved.
        item.mayInventoryAddItemFunc = (_, inv) => inv == EmporiumEntry.Instance?.afterhourInventory;
        return item;
    }

    private static GameItem MakeRemains(bool corpse)
    {
        var item = Make(corpse ? CorpseId : PackId, corpse ? "拾荒者的尸体" : "缴获的装备包",
            corpse ? "双击搜索尸体。右键“肢解尸体”需要随身刀具；请先取走装备和物品。" : "对手失去行动能力后留下的装备。双击搜刮。", corpse ? "corpse" : "pack");
        if (corpse) item.SetShape(new GridShapeBuilder(2, 3).SetDataFill((byte)1));
        var weapon = new GameSlotInventory(4, 3);
        var armor = new GameSlotInventory(4, 3);
        var cargo = new GameGridInventory(8, 6);
        Weapons[item] = weapon; Armor[item] = armor; Cargo[item] = cargo;
        item.SetContentWindow(MachineHelper.CreateMachineryWindow(true,
            MachineHelper.Panel("武器", weapon, "weapon"), MachineHelper.Panel("防具", armor, "armor"),
            MachineHelper.Panel("随身物品", cargo, "cargo")));
        if (corpse)
        {
            item.EnableTag("CONFIGURE_TAG");
            item.onConfigureSlotItemFunc = (s, _, _) => RaidRuntime.Instance?.Dismember(s);
            item.mayItemTargetThisFunc = (blade, self) => blade?.IsTag(HusbandryHelper.BLADE_ITEM_TAG) == true && self.identifier == CorpseId;
            item.canItemTargetThisFunc = item.mayItemTargetThisFunc;
            item.onItemTargetThisFunc = (blade, self) => RaidRuntime.Instance?.Dismember(self);
        }
        return item;
    }

    internal static GameItem Spawn(string id) => ModHelper.SetItemOwnerShip(ModHelper.SpawnItem(id), true);
    internal static bool Put(GraphNodeStorage? inventory, GameItem item) => inventory != null && GraphUtils.TryAcceptAll(inventory, item) == 0;
    internal static void ConsumeOne(GameItem item)
    {
        if (item.unitCount <= 1) item.Destroy();
        else item.SetUnitCount(item.unitCount - 1);
    }
    internal static void Clear() { Cargo.Clear(); Weapons.Clear(); Armor.Clear(); }
    internal static void PruneDetached()
    {
        foreach (var item in Cargo.Keys.Where(i => i.parentInventory == null).ToArray())
        {
            if (item is GameItemElement element && !element.IsDestroyed()) item.Destroy();
            Cargo.Remove(item); Weapons.Remove(item); Armor.Remove(item);
        }
    }

    internal static bool IsWeapon(GameItem item) => item.IsGameItemType("FIREARM") || item.IsGameItemType("MELEE_WEAPON")
        || item.IsTag(HusbandryHelper.BLADE_ITEM_TAG) || item.identifier == "stun_gun";

    internal static (int damage, int accuracy) WeaponStats(GameItem? item)
    {
        if (item == null) return (9, 82);
        if (item.IsTag(GunHelper.ADV_GUN_TAG))
            return (Rules.GunDamage(item.GetTagReadonly(GunHelper.GUN_CURRENT_FIREPOWER_TAG).GetInt()),
                Rules.GunAccuracy(item.GetTagReadonly(GunHelper.GUN_CURRENT_ACCURACY_TAG).GetInt()));
        return item.identifier switch
        {
            "combat_machete" => (28, 88), "hatchet" => (25, 82), "combat_knife" => (22, 92),
            "kitchen_cleaver" => (23, 80), "crowbar" => (21, 78), "pipe_weapon" => (18, 78),
            "stun_baton" => (19, 88), "stun_gun" => (22, 85), "glass_shard_shiv" => (12, 80),
            _ => (16, 88)
        };
    }
}

public sealed class RaidConsumableEffect
{
    public int Healing;
    public bool StopsBleeding;
    public int StimActions;
    public string Description = "";
}

// Deliberately public extension seam for future native chemical-dose integration.
// A resolver describes a single consumed unit, never an entire chemical container.
public static class ScavengingEffects
{
    private static readonly Dictionary<string, RaidConsumableEffect> effects = new()
    {
        ["bandage_item"] = new() { Healing = 20, StopsBleeding = true, Description = "恢复20点生命。\n停止流血。\n每次消耗1份。" },
        ["hemostatic_bandage_item"] = new() { Healing = 30, StopsBleeding = true, Description = "恢复30点生命。\n停止流血。\n每次消耗1份。" },
        ["topical_bandage_item"] = new() { Healing = 25, StopsBleeding = true, Description = "恢复25点生命。\n停止流血。\n每次消耗1份。" },
        ["salve"] = new() { Healing = 15, Description = "使用1份恢复15生命。" },
        ["blood_bag"] = new() { Healing = 40, Description = "使用1袋恢复40生命。" },
        ["caffeine_pill"] = new() { StimActions = 6, Description = "使用1粒，强化持续6次行动。\n伤害 +4\n命中 +8%" },
        ["smelling_salt"] = new() { StimActions = 3, Description = "使用1份，强化持续3次行动。\n伤害 +4\n命中 +8%" }
    };
    public static event Func<GameItem, RaidConsumableEffect?>? ResolveChemicalEffect;
    public static void Register(string itemId, RaidConsumableEffect effect) => effects[itemId] = effect;
    public static RaidConsumableEffect? Get(GameItem item)
    {
        if (effects.TryGetValue(item.identifier, out var effect)) return effect;
        if (!item.IsTag(ChemicalHelper.CHEM_ITEM_TAG) || ResolveChemicalEffect == null) return null;
        foreach (Func<GameItem, RaidConsumableEffect?> resolver in ResolveChemicalEffect.GetInvocationList())
        {
            try { var resolved = resolver(item); if (resolved != null) return resolved; }
            catch (Exception error) { UnityEngine.Debug.LogWarning("[ScavengeExtraction] 化学效果扩展失败：" + error.Message); }
        }
        return null;
    }
    internal static void Tooltip(RichTextBuilder builder, GameItem item)
    {
        var effect = Get(item);
        string? text = effect?.Description;
        if (text == null && RaidItems.IsWeapon(item))
        {
            var stats = RaidItems.WeaponStats(item);
            text = $"放入武器槽后生效。\n伤害　{stats.damage}\n命中　{stats.accuracy}%\n"
                + (item.IsGameItemType("FIREARM") ? "每次攻击消耗1发同口径散装子弹。" : "近战攻击不消耗弹药。");
        }
        if (text == null && item.IsTag("armor_item"))
            text = $"放入防具槽后生效。\n护甲值　{ArmorHelper.GetArmorItemValue(item)}\n受击时损耗护甲。";
        if (text == null) return;
        builder.NextLine().AddLine("——").AddLine("拾荒", bold: true);
        foreach (string paragraph in text.Replace("\r", "").Split('\n'))
            foreach (string line in Core.TooltipLayout.Wrap(paragraph, 20)) builder.AddLine(line);
    }
}
