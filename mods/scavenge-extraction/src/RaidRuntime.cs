using Newtonsoft.Json;
using ScavengeExtraction.Core;
using UnityEngine;

namespace ScavengeExtraction;

public sealed class RaidRuntime : MonoBehaviour
{
    internal static RaidRuntime? Instance { get; private set; }
    internal RaidState State { get; private set; } = new();
    internal bool Active { get; private set; }
    internal bool AllowReturn;
    internal RaidView? View;
    internal NativeRaidPanels? Panels;
    internal readonly Queue<string> Messages = new();
    private readonly System.Random random = new();
    private float nextRefresh;
    private bool animationBusy, installed, saveHealthy = true;
    private GameObject? nativePanel;
    private bool nativePanelWasActive;
    internal bool Busy => animationBusy;
    internal bool CanSearch => Active && State.Phase == RaidPhase.Exploring && State.Player.Hp > 0
        && !animationBusy && ScavHelper.CanScavenge();
    internal bool HasBlade => Carried().Any(i => i.IsTag(HusbandryHelper.BLADE_ITEM_TAG));
    internal GameItem? Weapon => Panels?.WeaponSlot.childItem;
    internal GameItem? EquippedArmor => Panels?.ArmorSlot.childItem;
    internal int Defense => EquippedArmor is GameItem a && ArmorHelper.GetArmorItemValue(a) > 0
        ? Rules.Clamp(ArmorHelper.GetArmorItemValue(a) / 10, 1, 22) : 0;
    internal (int damage, int accuracy) AttackStats
    {
        get { var s = RaidItems.WeaponStats(Weapon); return (s.damage + (State.Player.StimTurns > 0 ? 4 : 0),
            Rules.Clamp(s.accuracy + (State.Player.StimTurns > 0 ? 8 : 0), 5, 98)); }
    }

    internal void Install()
    {
        if (installed) return;
        installed = true; Instance = this;
        ModHook.OnGameLoadedInit += BeforeLoad;
        ModHook.OnGameLoadedLate += Load;
        ModHook.OnAfterhourArriveLate += Arrive;
        ModHook.OnAfterhourLeaveEarly += Leave;
        ModHook.OnAfterhourLeaveLate += AfterLeave;
        ModHook.OnNewGameStarted += NewGame;
    }
    internal void Uninstall()
    {
        if (!installed) return;
        installed = false;
        ModHook.OnGameLoadedInit -= BeforeLoad;
        ModHook.OnGameLoadedLate -= Load;
        ModHook.OnAfterhourArriveLate -= Arrive;
        ModHook.OnAfterhourLeaveEarly -= Leave;
        ModHook.OnAfterhourLeaveLate -= AfterLeave;
        ModHook.OnNewGameStarted -= NewGame;
        StopVisuals(); Instance = null;
    }
    private void NewGame(string scenario) { State = new RaidState(); saveHealthy = true; Messages.Clear(); }
    private void BeforeLoad() { StopVisuals(); RaidItems.Clear(); Messages.Clear(); State = new RaidState(); }
    private void Load()
    {
        try
        {
            saveHealthy = true;
            string saved = ModHelper.GetModData(RaidItems.ModId, "state.v1", "");
            State = string.IsNullOrWhiteSpace(saved) ? new RaidState() : JsonConvert.DeserializeObject<RaidState>(saved) ?? new RaidState();
            if (State.Schema != 1) throw new InvalidOperationException("无法读取该拾荒存档版本。");
            State.Player ??= new PlayerCondition(); State.Player.Normalize();
            if (State.Nodes == null || State.Nodes.Length != Map.Nodes.Length || State.Nodes.Any(n => n == null)) State.Nodes = RaidState.CreateNodes();
            State.Node = Rules.Clamp(State.Node, 0, Map.Nodes.Length - 1);
        }
        catch (Exception error)
        {
            // Preserve unreadable data; do not silently replace an injured character with full HP.
            Debug.LogError("[ScavengeExtraction] 存档读取失败，已暂停本次扩展：" + error);
            saveHealthy = false;
        }
        if (ModHelper.GetCurrentAfterhourLocationId() == AfterhourLocation.DUMPING_GROUND) Arrive(AfterhourLocation.DUMPING_GROUND);
    }
    internal void Save()
    {
        if (Panels != null)
        {
            State.Player.WeaponUid = Weapon?.GetUniqueID() ?? 0;
            State.Player.ArmorUid = EquippedArmor?.GetUniqueID() ?? 0;
        }
        if (saveHealthy && PlayerStore.Instance != null) ModHelper.SetModData(RaidItems.ModId, "state.v1", JsonConvert.SerializeObject(State));
    }
    private void Arrive(string location)
    {
        if (location != AfterhourLocation.DUMPING_GROUND || Active) return;
        if (!saveHealthy) { StoreUIManager.Instance.Notify("拾荒扩展存档无法读取，已保留数据并暂停扩展。请查看日志。"); return; }
        Active = true; AllowReturn = false; animationBusy = false;
        State.BeginNight(StoreStation.Instance.dayCounter);
        RecoverLegacyCaches();
        nativePanel = MapUIManager.Instance.UIScavengingPanel;
        if (nativePanel != null) { nativePanelWasActive = nativePanel.activeSelf; nativePanel.SetActive(false); }
        Panels = new NativeRaidPanels(this);
        View = RaidView.Create(this);
        Log("进入拾荒区。将武器、防具放入装备槽，选择相连地点探索。");
        if (State.Player.Hp <= 0) Log("伤势过重，无法继续行动。请作出选择。");
        Save();
    }
    private void Leave(string location)
    {
        if (location != AfterhourLocation.DUMPING_GROUND) return;
        if (State.Phase == RaidPhase.Searching) State.Phase = RaidPhase.Exploring;
        foreach (var n in State.Nodes) n.LootQueued = false;
        Save(); StopVisuals();
    }
    private void AfterLeave(string location)
    {
        if (location == AfterhourLocation.DUMPING_GROUND) RaidItems.PruneDetached();
    }
    private void StopVisuals()
    {
        Panels?.Detach(); Panels = null;
        Active = false; animationBusy = false;
        if (View != null) View.Dispose(); View = null;
        if (nativePanel != null) nativePanel.SetActive(nativePanelWasActive);
        nativePanel = null;
    }
    private void Update()
    {
        if (!Active) return;
        if (PlayerStore.Instance == null || MapUIManager.Instance == null || ModHelper.GetCurrentAfterhourLocationId() != AfterhourLocation.DUMPING_GROUND)
        { StopVisuals(); return; }
        if (Time.unscaledTime >= nextRefresh)
        {
            nextRefresh = Time.unscaledTime + .35f;
            View?.Refresh();
        }
    }
    internal void Log(string text)
    {
        Messages.Enqueue(text);
        while (Messages.Count > 12) Messages.Dequeue();
        View?.Refresh();
    }
    internal List<GameItem> Carried()
    {
        var e = EmporiumEntry.Instance;
        if (e == null) return new();
        var result = new List<GameItem>();
        foreach (var root in new GameInventory?[] { e.afterhourPocketSlotInvLeft, e.afterhourPocketSlotInvBackpack, e.afterhourPocketSlotInvRight,
            Panels?.WeaponSlot, Panels?.ArmorSlot })
        {
            if (root == null) continue;
            foreach (var i in root.childItems)
            {
                result.Add(i); result.AddRange(i.FindAllChildItems());
            }
        }
        return result.Where(i => i != null && GeneralHelper.IsItemEffectivelyOwned(i)).Distinct().ToList();
    }
    internal void Move(int node)
    {
        if (animationBusy || !State.Move(node)) return;
        Log("抵达「" + Map.Nodes[node].Name + "」。" + Map.Nodes[node].Description);
        TickCondition();
        if (State.Phase == RaidPhase.Defeated) { Save(); return; }
        RollEncounter();
        Save(); View?.Refresh();
    }
    private void RollEncounter()
    {
        var node = State.Nodes[State.Node];
        if (node.EncounterRolled)
        {
            var hostile = node.GetEnemies().FirstOrDefault(e => !e.Settled && e.Hp > 0 && e.Disposition == EnemyDisposition.Hostile);
            if (hostile != null && State.SelectEnemy(hostile) && State.StartCombat())
            { Log("敌对者仍守在这里，再次向你进攻！"); EnemyTurn(false); View?.ShowBattle(); }
            return;
        }
        if (Map.Nodes[State.Node].IsExit) return;
        node.EncounterRolled = true;
        if (random.NextDouble() > .76) { Log("附近暂时没有其他人。"); return; }
        int count = random.NextDouble() < .5 ? random.Next(2, 4) : 1;
        for (int i = 0; i < count; i++)
        {
            var enemy = CreateEncounterEnemy();
            node.AddEnemy(enemy);
            if (random.NextDouble() < Rules.Hostility(Reputation(enemy.Faction), Map.Nodes[State.Node].Risk, false))
                enemy.Disposition = EnemyDisposition.Hostile;
            Log(enemy.Name + "（" + FactionName(enemy.Faction) + "）出现在附近。");
        }
        var attacker = node.GetEnemies().FirstOrDefault(e => e.Disposition == EnemyDisposition.Hostile && !e.Settled && e.Hp > 0);
        if (attacker != null && State.SelectEnemy(attacker) && State.StartCombat())
        {
            Log(attacker.Name + "主动发起攻击！"); EnemyTurn(false); View?.ShowBattle();
        }
        else Log("周围的人暂时没有敌意，但可能争抢物品。");
    }
    private EnemyState CreateEncounterEnemy()
    {
        string[] factions = { "FACTION_LOWER_LEVEL", "FACTION_BLACK_MARKET", "FACTION_SECURITY", "FACTION_REVOLUTION", "FACTION_CHURCH", "FACTION_CARTEL" };
        int index = State.Node == 5 ? 1 : random.Next(factions.Length);
        string[] prefixes = { "暴虐的", "卑鄙的", "警惕的", "贪婪的", "落魄的", "沉默的", "凶悍的" };
        string[] roles = { "拾荒客", "走私客", "巡逻员", "游击队员", "教徒", "打手" };
        var availablePrefixes = prefixes.Where(p => !State.Nodes[State.Node].GetEnemies().Any(e => e.Name == p + roles[index])).ToArray();
        string[] weapons = { "kitchen_knife", "pipe_weapon", "handmade_pistol", "combat_knife", "crowbar", "heavy_handmade_pistol" };
        string sprite = index switch
        {
            1 => SpriteDict.GetRandomShadySpriteName(), 2 => SpriteDict.GetRandomSecSpriteName(),
            3 => SpriteDict.GetRandomRevSpriteName(), 5 => SpriteDict.GetRandomMercSpriteName(),
            _ => SpriteDict.GetRandomScavSpriteName()
        };
        var nativeWeapon = RaidItems.Spawn(weapons[index]);
        var stats = RaidItems.WeaponStats(nativeWeapon);
        nativeWeapon.Destroy();
        int risk = Map.Nodes[State.Node].Risk;
        return new EnemyState
        {
            Name = availablePrefixes[random.Next(availablePrefixes.Length)] + roles[index], Faction = factions[index], Sprite = sprite,
            WeaponId = weapons[index], Attack = Rules.Clamp(stats.damage / 2 + risk, 7, 24),
            Accuracy = Rules.Clamp(stats.accuracy - 12, 55, 85), Hp = 40 + risk * 10, MaxHp = 40 + risk * 10, Armor = risk
        };
    }
    internal static int Reputation(string faction) => StoreReputation.GetStoreReputation(faction)?.GetReputation() ?? 0;
    internal static string FactionName(string faction) => StoreReputation.GetFactionNameByID(faction);
    internal void RecoverLegacyCaches()
    {
        // Only an older prototype can leave one of these containers behind.
        // Reveal the existing objects without rolling or charging another search.
        var ground = EmporiumEntry.Instance.afterhourInventory;
        foreach (var cache in ground.childItems.Where(i => i.identifier == RaidItems.CacheId).ToArray())
        {
            if (!RaidItems.Cargo.TryGetValue(cache, out var cargo)) continue;
            foreach (var item in cargo.childItems.ToArray()) RaidItems.Put(ground, item);
            if (cargo.childItems.Count == 0) cache.Destroy();
            else
            {
                cache.contentWindow.isLocked = false;
                GeneralHelper.SetCustomName(cache, "旧版拾荒物品");
            }
        }
        foreach (var node in State.Nodes) node.LootQueued = false;
    }
    internal void Search()
    {
        if (!CanSearch) { Log("当前无法拾荒：请处理战斗或伤势，并检查今夜剩余次数。"); return; }
        foreach (var enemy in State.Nodes[State.Node].GetEnemies().Where(e => !e.Settled && e.Hp > 0).ToArray())
        {
            if (random.NextDouble() < Rules.Hostility(Reputation(enemy.Faction), Map.Nodes[State.Node].Risk, true))
            {
                State.SelectEnemy(enemy); State.StartCombat(); Log("翻找声引起了注意，" + enemy.Name + "向你发起攻击！");
                EnemyTurn(false); Save(); View?.ShowBattle(); return;
            }
            if (random.NextDouble() < .25) StealGroundItem(enemy);
        }
        var ground = EmporiumEntry.Instance.afterhourInventory;
        var before = ground.childItems.ToDictionary(i => i, i => i.unitCount);
        int attempts = PlayerStore.Instance.scavengingAttempts;
        // Vanilla owns loot rolls, scanner/perk/event bonuses, injury/luck,
        // backpack discoveries, run statistics and the single attempt charge.
        ScavHelper.ScavengeDumpingGrounds();
        MapUIManager.Instance.UpdateScavUI();
        var found = ground.childItems.Where(i => !before.TryGetValue(i, out int units) || i.unitCount > units).ToArray();
        foreach (var item in found)
        {
            item.EnableTag("RAID_FOUND"); item.ModifyTag(RaidItems.NodeTag, s => s.Enable().SetInt(State.Node));
        }
        if (PlayerStore.Instance.scavengingAttempts > attempts)
        {
            State.Nodes[State.Node].Searches++;
            TickCondition();
            Log(found.Length > 0 ? "发现：" + string.Join("、", found.Select(i => i.GetDisplayName(true))) + "。"
                : "本次拾荒没有找到可拾取的物品。");
        }
        Save(); View?.Refresh();
    }
    private void StealGroundItem(EnemyState enemy)
    {
        var loot = EmporiumEntry.Instance.afterhourInventory.childItems.FirstOrDefault(i => i.IsTag("RAID_FOUND")
            && i.GetTagReadonly(RaidItems.NodeTag).GetInt() == State.Node && i.identifier != RaidItems.CacheId);
        if (loot == null) return;
        var name = loot.GetDisplayName(true); loot.Destroy(); Log(enemy.Name + "抢走了地上的「" + name + "」。");
    }
    internal void Provoke()
    {
        if (animationBusy || State.Phase != RaidPhase.Exploring || !State.StartCombat()) return;
        Log("你向对手发起了战斗。"); Save(); View?.ShowBattle();
    }
    private bool CanInteract => Active && !animationBusy && State.Phase == RaidPhase.Exploring
        && State.Enemy is { Settled: false, Hp: > 0 };
    internal void Talk()
    {
        if (!CanInteract) return;
        var enemy = State.Enemy!;
        string words = Reputation(enemy.Faction) < -15 ? "离我远点。别碰我盯上的东西。"
            : Reputation(enemy.Faction) > 15 ? "原来是你。这里不太平，留点药，记住回去的路。"
            : "我只是来找点东西。你找你的，别挡我的路。";
        Log("你与" + enemy.Name + "交谈。");
        GenericConfirmationUIManager.Instance.OpenPanelSingleButton(enemy.Name + "（" + FactionName(enemy.Faction) + "）\n\n“" + words + "”", "结束对话", () => { });
    }
    internal void Steal()
    {
        if (!CanInteract) return;
        var enemy = State.Enemy!;
        if (enemy.TheftAttempted) { Log("对方已经提高警惕，无法再次偷窃。"); return; }
        if (random.NextDouble() < .45)
        {
            var supply = RaidItems.Spawn("bandage_item");
            bool placed = false;
            // Place one real item before marking the enemy's matching supply
            // as gone, so full inventories never silently destroy the reward.
            foreach (var inv in new GraphNodeStorage?[] { EmporiumEntry.Instance.afterhourPocketSlotInvBackpack.childItem,
                EmporiumEntry.Instance.afterhourPocketSlotInvLeft, EmporiumEntry.Instance.afterhourPocketSlotInvRight })
                if (inv != null && RaidItems.Put(inv, supply)) { placed = true; break; }
            if (!placed) placed = RaidItems.Put(EmporiumEntry.Instance.afterhourInventory, supply);
            if (!placed) { supply.Destroy(); Log("随身物品栏和地面都没有空位，暂时无法偷取物品。"); return; }
            enemy.TryBeginTheft(); enemy.SupplyStolen = true;
            Log("你偷到了" + supply.GetDisplayName(true) + "。对方已经有所警觉。");
            TickCondition();
        }
        else
        {
            enemy.TryBeginTheft(); State.StartCombat();
            StoreReputation.ModReputation(enemy.Faction, -1, true);
            Log("偷窃被发现，" + enemy.Name + "向你发动攻击！");
            EnemyTurn(false); View?.ShowBattle();
        }
        Save(); View?.Refresh();
    }
    internal void UseEnemyItem(GameItem item, EnemyState enemy)
    {
        if (!Active || animationBusy || !State.MayUseOnEnemy(enemy)) return;
        if (!Carried().Contains(item) || item.unitCount <= 0 || !item.MayRemove())
        { Log("只能对人物使用随身携带、可取出的物品。"); return; }
        var effect = EnemyItemEffects.Get(item);
        if (effect == null || (effect.Healing <= 0 && effect.Damage <= 0))
        { Log("这件物品目前不能对人物使用。"); return; }
        if (effect.Damage <= 0 && enemy.Hp >= enemy.MaxHp)
        { Log("对方没有需要治疗的伤势，不会消耗药品。"); return; }
        if (effect.Damage > 0 && State.Phase == RaidPhase.Exploring
            && (!State.SelectEnemy(enemy) || !State.StartCombat())) return;
        string name = item.GetDisplayName(true);
        TrySound(item.soundUse);
        RaidItems.ConsumeOne(item);
        if (effect.Damage > 0)
        {
            int damage = Math.Min(enemy.Hp, Rules.Damage(effect.Damage, enemy.Armor, false));
            State.DamageEnemy(damage);
            Log("对" + enemy.Name + "使用" + name + $"，造成 {damage} 点伤害。");
            View?.ShowEnemyDamage(damage);
        }
        else
        {
            int healed = enemy.Heal(effect.Healing);
            if (enemy.RewardHealing(healed)) StoreReputation.ModReputation(enemy.Faction, 1, true);
            Log("为" + enemy.Name + "使用" + name + $"，恢复 {healed} 生命。");
        }
        if (State.Phase == RaidPhase.Combat) EnemyTurn(false);
        TickBuff(); Save(); View?.Refresh();
    }
    internal void Attack()
    {
        if (!CanAct()) return;
        var weapon = Weapon;
        if (weapon != null && weapon.IsGameItemType("FIREARM"))
        {
            int caliber = weapon.GetTagReadonly(GunHelper.GUN_CALIBER_TAG).GetInt();
            var ammo = Carried().FirstOrDefault(i => GunHelper.IsAmmunitionOfCaliber(i, caliber)
                && !i.IsTag("AMMO_BOX_CONTENT_CALIBER_TAG") && i.unitCount > 0);
            if (ammo == null) { Log("缺少同口径散装子弹，请将子弹放入随身背包。"); return; }
            RaidItems.ConsumeOne(ammo);
        }
        var stats = AttackStats;
        bool hit = random.Next(100) < stats.accuracy;
        int damage = hit ? Math.Min(State.Enemy!.Hp, Rules.Damage(stats.damage + random.Next(-2, 4), State.Enemy.Armor, false)) : 0;
        State.DamageEnemy(damage);
        Log(hit ? $"你命中对方，造成 {damage} 点伤害。" : "你的攻击没有命中。");
        animationBusy = true;
        View!.AnimateAttack(weapon, damage, State.Phase == RaidPhase.Victory, () =>
        {
            animationBusy = false;
            if (!Active) return;
            if (State.Phase == RaidPhase.Combat) EnemyTurn(false);
            else Log("对方已失去战斗能力。选择击杀、击晕或放走。");
            TickBuff(); Save(); View?.Refresh();
        });
        Save();
    }
    internal void Defend()
    {
        if (!CanAct()) return;
        Log("你集中防御，本回合受到的伤害降低60%。");
        EnemyTurn(true); TickBuff(); Save();
    }
    internal void Flee()
    {
        if (!CanAct()) return;
        if (random.NextDouble() < (State.Player.StimTurns > 0 ? .70 : .52))
        {
            State.Phase = RaidPhase.Exploring;
            State.Node = 0; State.Nodes[0].Visited = true;
            State.SelectFirstLivingEnemy();
            Log("你甩开追击，逃回入口。地面的东西仍留在拾荒区。");
            View?.HideBattle(); TickCondition();
        }
        else { Log("逃跑失败，对方追了上来！"); EnemyTurn(false); TickBuff(); }
        Save();
    }
    private bool CanAct() => Active && State.Phase == RaidPhase.Combat && !animationBusy;
    private void EnemyTurn(bool defending)
    {
        if (State.Phase != RaidPhase.Combat || State.Enemy == null) return;
        var enemy = State.Enemy;
        if (random.Next(100) >= enemy.Accuracy) Log(enemy.Name + "的攻击落空。");
        else
        {
            var armor = EquippedArmor;
            int damage = Rules.Damage(enemy.Attack + random.Next(-2, 4), Defense, defending);
            damage = State.DamagePlayer(damage);
            if (armor != null) ArmorHelper.ModifyArmor(armor, Math.Max(1, damage / 3), 0);
            if (State.Player.Hp > 0 && !defending && random.NextDouble() < .16) State.Player.Bleeding = Math.Min(3, State.Player.Bleeding + 1);
            Log($"{enemy.Name}造成 {damage} 点伤害。" + (State.Player.Bleeding > 0 ? "你正在流血！" : ""));
            View?.FlashDamage();
            View?.ShowPlayerDamage(damage);
        }
        if (State.Phase == RaidPhase.Combat) TickBleeding();
        if (State.Phase == RaidPhase.Defeated) Log("你被击败了。求饶可能失去所有随身物品，或者认命。");
    }
    private void TickBuff() { if (State.Player.StimTurns > 0) State.Player.StimTurns--; }
    private void TickCondition() { TickBuff(); TickBleeding(); }
    private void TickBleeding()
    {
        if (State.Player.Bleeding <= 0 || State.Phase is RaidPhase.Defeated or RaidPhase.Ended) return;
        int taken = State.DamagePlayer(State.Player.Bleeding);
        View?.ShowPlayerDamage(taken);
        if (State.Phase == RaidPhase.Defeated) State.LastThreat = State.Enemy?.Name ?? "未处理的伤口";
    }
    internal void UseMedicine(GameItem medicine)
    {
        if (!Active || animationBusy || State.Phase is not (RaidPhase.Combat or RaidPhase.Exploring)) return;
        if (!Carried().Contains(medicine) || medicine.unitCount <= 0 || !medicine.MayRemove()) { Log("这件物品已不在随身背包，或无法使用。"); return; }
        var effect = ScavengingEffects.Get(medicine); if (effect == null) return;
        if (effect.Healing > 0 && State.Player.Hp >= State.Player.MaxHp && (!effect.StopsBleeding || State.Player.Bleeding == 0))
        { Log("生命已满，不会浪费药品。"); return; }
        string name = medicine.GetDisplayName(true);
        RaidItems.ConsumeOne(medicine);
        int healed = State.Player.Heal(effect.Healing, effect.StopsBleeding);
        State.Player.StimTurns = Math.Max(State.Player.StimTurns, Rules.Clamp(effect.StimActions, 0, 8));
        Log($"使用{name}，恢复 {healed} 生命。" + (effect.StopsBleeding ? "伤口已止血。" : ""));
        if (State.Phase == RaidPhase.Combat) EnemyTurn(false);
        Save();
    }
    internal void ResolveVictory(VictoryChoice choice)
    {
        if (animationBusy || State.Phase != RaidPhase.Victory || State.Enemy == null) return;
        var enemy = State.Enemy;
        if (choice != VictoryChoice.Release && !enemy.LootCreated)
        {
            if (!SpawnRemains(enemy, choice == VictoryChoice.Kill)) { Log("地面放不下战利品，请先腾出位置再选择处置。"); return; }
            enemy.LootCreated = true;
        }
        if (!State.SettleEnemy(choice)) return;
        StoreReputation.ModReputation(enemy.Faction, choice == VictoryChoice.Kill ? -3 : choice == VictoryChoice.Release ? 2 : -1, true);
        Log(choice switch { VictoryChoice.Kill => "对方已死亡。地面出现2×3尸体，双击搜刮；随身有刀可右键肢解。",
            VictoryChoice.KnockOut => "你击晕了对方，留下的装备包可以搜刮。", _ => "你放走了对方，与其阵营的关系有所改善。" });
        View?.HideBattle(); Save();
    }
    private bool SpawnRemains(EnemyState enemy, bool corpse)
    {
        var item = RaidItems.Spawn(corpse ? RaidItems.CorpseId : RaidItems.PackId);
        bool placed = false;
        try
        {
            GeneralHelper.SetCustomName(item, corpse ? enemy.Name + "的尸体" : enemy.Name + "的装备包");
            var weapon = RaidItems.Spawn(enemy.WeaponId);
            if (!RaidItems.Put(RaidItems.Weapons[item], weapon) && !RaidItems.Put(RaidItems.Cargo[item], weapon)) weapon.Destroy();
            if (!enemy.SupplyStolen)
            {
                var med = RaidItems.Spawn("bandage_item");
                if (!RaidItems.Put(RaidItems.Cargo[item], med)) med.Destroy();
            }
            if (enemy.WeaponId.Contains("pistol"))
            {
                var ammo = RaidItems.Spawn(enemy.WeaponId == "heavy_handmade_pistol" ? "heavy_pistol_ammo" : "small_pistol_ammo");
                ammo.SetUnitCount(3);
                if (!RaidItems.Put(RaidItems.Cargo[item], ammo)) ammo.Destroy();
            }
            placed = RaidItems.Put(EmporiumEntry.Instance.afterhourInventory, item);
            return placed;
        }
        finally { if (!placed) item.Destroy(); }
    }
    internal void Dismember(GameItem corpse)
    {
        if (!Active || State.Phase != RaidPhase.Exploring || animationBusy || corpse.identifier != RaidItems.CorpseId) return;
        if (!HasBlade) { Log("肢解需要随身携带刀类武器。"); return; }
        if (corpse.IsTag("RAID_DISMEMBERED")) { Log("这具尸体已经肢解过。"); return; }
        if (corpse.FindAllChildItems().Count > 0) { Log("请先取走尸体内的全部装备和物品，再肢解。"); return; }
        if (!RaidItems.Cargo.TryGetValue(corpse, out var cargo)) return;
        // Mark first: repeated context clicks must never duplicate remains.
        corpse.EnableTag("RAID_DISMEMBERED");
        foreach (var part in new[] { "头部残骸", "躯干残骸", "手臂残骸", "腿部残骸" })
        {
            var i = RaidItems.Spawn(RaidItems.PartId); GeneralHelper.SetCustomName(i, part);
            if (!RaidItems.Put(cargo, i)) i.Destroy();
        }
        GeneralHelper.SetCustomName(corpse, "已肢解的残骸");
        TrySound("body_collapse_floor"); Log("肢解完成，残骸位于尸体的物品栏内。");
        corpse.contentWindow.Show(Input.mousePosition, true); Save();
    }
    internal void Beg()
    {
        if (!Active || State.Phase != RaidPhase.Defeated || State.DefeatSettled) return;
        State.DefeatSettled = true;
        double chance = Rules.BegChance(State.Enemy == null ? -40 : Reputation(State.Enemy.Faction));
        if (random.NextDouble() >= chance) { Log("对方拒绝了你的请求。"); Die(); return; }
        // Snapshot roots only; Destroy recursively removes contents exactly once.
        var e = EmporiumEntry.Instance;
        var roots = new[] { e.afterhourPocketSlotInvLeft, e.afterhourPocketSlotInvBackpack, e.afterhourPocketSlotInvRight }
            .SelectMany(inv => inv.childItems).Concat(Panels?.EquippedRoots ?? Enumerable.Empty<GameItem>()).Distinct().ToArray();
        foreach (var item in roots) item.Destroy();
        State.Player.WeaponUid = State.Player.ArmorUid = 0;
        State.Player.Hp = 1; State.Player.Bleeding = 0; State.Player.StimTurns = 0;
        State.Node = 0; State.Phase = RaidPhase.Exploring;
        AllowReturn = true; Save(); View?.HideBattle();
        GenericConfirmationUIManager.Instance.OpenPanelSingleButton("你用身上所有的物品，换取了活命的机会。", "返回店铺", () =>
        { if (Active) ModHelper.ReturnFromAfterhour(false); });
    }
    internal void Die()
    {
        if (!Active || State.Phase == RaidPhase.Ended) return;
        State.DefeatSettled = true; State.Player.Hp = 0; State.Phase = RaidPhase.Ended; Save();
        View?.SetVisible(false);
        var store = PlayerStore.Instance;
        RunStats.OnRunEnded(StoreStation.Instance.dayCounter, store.GetTotalEstimatedValue(), store.playerCash);
        GameOverUIManager.Instance.SetEnding("ScavengeExtraction.defeat");
        GameOverUIManager.Instance.DisplayUI("拾荒失败", $"第 {StoreStation.Instance.dayCounter + 1} 天，你在拾荒时被{State.LastThreat}击败了。你身上的所有东西、随身物品，以及你的身体，都永远属于他了。", false);
    }
    internal bool PermitReturn()
    {
        if (!Active || AllowReturn) return true;
        if (!State.CanExtract || animationBusy)
        { Log(State.Phase == RaidPhase.Exploring ? "需要沿路线回到入口或应急撤离口才能撤离。" : "请先处理当前搜索、战斗或战败选择。"); return false; }
        return true;
    }
    internal bool PrepareReturn()
    {
        if (!PermitReturn()) return false;
        if (!Active) return true;
        try { Panels?.ReturnEquipment(); return true; }
        catch (InvalidOperationException)
        {
            Log("随身背包和店内空间不足，请腾出位置后撤离。未能转移的装备仍保留在槽内。");
            return false;
        }
    }
    internal void Extract()
    {
        if (!PermitReturn()) return;
        ModHelper.ReturnFromAfterhour(true);
    }
    internal static void TrySound(string? id) { if (!string.IsNullOrEmpty(id) && AudioManager.Instance != null) AudioManager.Instance.Play2(id); }
}
