namespace ScavengeExtraction.Core;

public enum RaidPhase { Exploring, Searching, Combat, Victory, Defeated, Ended }
public enum EnemyDisposition { Neutral, Hostile, Gone }
public enum VictoryChoice { Kill, KnockOut, Release }

public sealed class PlayerCondition
{
    public int Hp = 100;
    public int MaxHp = 100;
    public int Bleeding;
    public int StimTurns;
    public int WeaponUid;
    public int ArmorUid;
    public void Normalize()
    {
        MaxHp = 100;
        Hp = Rules.Clamp(Hp, 0, MaxHp);
        Bleeding = Rules.Clamp(Bleeding, 0, 3);
        StimTurns = Rules.Clamp(StimTurns, 0, 8);
    }
    public int Heal(int amount, bool stopBleeding)
    {
        if (Hp <= 0) return 0;
        var old = Hp;
        Hp = Rules.Clamp(Hp + System.Math.Max(0, amount), 0, MaxHp);
        if (stopBleeding) Bleeding = 0;
        return Hp - old;
    }
}

public sealed class EnemyState
{
    public string Name = "";
    public string Faction = "";
    public string Sprite = "";
    public string WeaponId = "";
    public int Hp = 55;
    public int MaxHp = 55;
    public int Attack = 12;
    public int Accuracy = 70;
    public int Armor = 2;
    public EnemyDisposition Disposition;
    public bool Settled;
    public bool LootCreated;
    public bool TheftAttempted;
    public bool SupplyStolen;
    public bool HealingReputationGranted;
    public bool TryBeginTheft()
    {
        if (Settled || Hp <= 0 || TheftAttempted) return false;
        TheftAttempted = true;
        return true;
    }
    public bool RewardHealing(int healed)
    {
        if (healed <= 0 || HealingReputationGranted) return false;
        HealingReputationGranted = true;
        return true;
    }
    public int Heal(int amount)
    {
        if (Settled || Hp <= 0 || amount <= 0) return 0;
        int healed = System.Math.Min(amount, System.Math.Max(0, MaxHp - Hp));
        Hp += healed;
        return healed;
    }
}

public sealed class NodeState
{
    public bool Visited;
    public bool EncounterRolled;
    public int Searches;
    public bool LootQueued;
    // Keep the original field so existing prototype saves remain readable.
    public EnemyState? Enemy;
    public List<EnemyState> AdditionalEnemies = new();
    public IEnumerable<EnemyState> GetEnemies()
    {
        if (Enemy != null) yield return Enemy;
        if (AdditionalEnemies != null)
            foreach (var enemy in AdditionalEnemies)
                if (enemy != null && !ReferenceEquals(enemy, Enemy)) yield return enemy;
    }
    public void AddEnemy(EnemyState enemy)
    {
        if (Enemy == null) Enemy = enemy;
        else (AdditionalEnemies ??= new()).Add(enemy);
    }
}

public sealed class RaidState
{
    public int Schema = 1;
    public int Day = -1;
    public int Node;
    public int SelectedEnemy;
    public RaidPhase Phase;
    public PlayerCondition Player = new();
    public NodeState[] Nodes = CreateNodes();
    public string LastThreat = "拾荒中的意外";
    public bool DefeatSettled;
    public static NodeState[] CreateNodes() => System.Linq.Enumerable.Range(0, Map.Nodes.Length).Select(_ => new NodeState()).ToArray();
    public void BeginNight(int day)
    {
        if (Day != day)
        {
            Day = day;
            Nodes = CreateNodes();
            Player.StimTurns = 0;
        }
        Node = 0;
        SelectedEnemy = 0;
        Phase = Player.Hp <= 0 ? RaidPhase.Defeated : RaidPhase.Exploring;
        DefeatSettled = false;
        Nodes[0].Visited = true;
    }
    public bool CanMove(int target) => Phase == RaidPhase.Exploring && target >= 0 && target < Nodes.Length
        && Map.Nodes[Node].Links.Contains(target);
    public bool Move(int target)
    {
        if (!CanMove(target)) return false;
        Node = target;
        SelectFirstLivingEnemy();
        Nodes[target].Visited = true;
        return true;
    }
    public EnemyState? Enemy => Nodes[Node].GetEnemies().ElementAtOrDefault(SelectedEnemy);
    public bool SelectEnemy(EnemyState enemy)
    {
        if (Phase != RaidPhase.Exploring || enemy.Settled || enemy.Hp <= 0) return false;
        int index = Nodes[Node].GetEnemies().ToList().IndexOf(enemy);
        if (index < 0) return false;
        SelectedEnemy = index;
        return true;
    }
    public void SelectFirstLivingEnemy()
    {
        int first = Nodes[Node].GetEnemies().ToList().FindIndex(e => !e.Settled && e.Hp > 0);
        SelectedEnemy = System.Math.Max(0, first);
    }
    public bool MayUseOnEnemy(EnemyState enemy) => !enemy.Settled && enemy.Hp > 0
        && Nodes[Node].GetEnemies().Contains(enemy)
        && (Phase == RaidPhase.Exploring || (Phase == RaidPhase.Combat && ReferenceEquals(Enemy, enemy)));
    public bool StartCombat()
    {
        if (Phase != RaidPhase.Exploring && Phase != RaidPhase.Searching) return false;
        if (Enemy == null || Enemy.Settled || Enemy.Hp <= 0) return false;
        Enemy.Disposition = EnemyDisposition.Hostile;
        LastThreat = Enemy.Name;
        Phase = RaidPhase.Combat;
        return true;
    }
    public bool DamageEnemy(int amount)
    {
        if (Phase != RaidPhase.Combat || Enemy == null) return false;
        Enemy.Hp = System.Math.Max(0, Enemy.Hp - System.Math.Max(0, amount));
        if (Enemy.Hp == 0) Phase = RaidPhase.Victory;
        return true;
    }
    public int DamagePlayer(int amount)
    {
        if (Phase == RaidPhase.Ended || Phase == RaidPhase.Defeated || Player.Hp <= 0) return 0;
        int taken = System.Math.Min(Player.Hp, System.Math.Max(0, amount));
        Player.Hp -= taken;
        if (Player.Hp <= 0) Phase = RaidPhase.Defeated;
        return taken;
    }
    public bool SettleEnemy(VictoryChoice choice)
    {
        if (Phase != RaidPhase.Victory || Enemy == null || Enemy.Settled) return false;
        Enemy.Settled = true;
        Enemy.Disposition = EnemyDisposition.Gone;
        Phase = RaidPhase.Exploring;
        SelectFirstLivingEnemy();
        return true;
    }
    public bool CanExtract => Phase == RaidPhase.Exploring && Map.Nodes[Node].IsExit;
}

public sealed class MapNode
{
    public readonly string Name, Description;
    public readonly float X, Y;
    public readonly int[] Links;
    public readonly bool IsExit;
    public readonly int Risk;
    public MapNode(string name, string description, float x, float y, int risk, bool exit, params int[] links)
    { Name = name; Description = description; X = x; Y = y; Risk = risk; IsExit = exit; Links = links; }
}

public static class Map
{
    public static readonly MapNode[] Nodes =
    {
        new("入口 / 撤离", "出发前整理随身装备。背包和口袋里的物品才能带回去。", .09f, .49f, 0, true, 1, 2),
        new("废料堆", "低风险杂物区；有人在附近翻找。", .31f, .25f, 1, false, 0, 3, 4),
        new("废车通道", "通向内区的狭窄通道，容易遭遇争夺。", .31f, .76f, 2, false, 0, 4, 5),
        new("遗弃货舱", "残留工具和货物，争夺者更危险。", .55f, .16f, 3, false, 1, 6),
        new("医疗废站", "可能找到原版医疗用品。", .54f, .49f, 2, false, 1, 2, 6),
        new("散货角落", "黑市人员时常出没。留意你的战利品。", .55f, .83f, 3, false, 2, 6),
        new("昏暗走廊", "内区汇合处；保持撤离路线畅通。", .77f, .49f, 4, false, 3, 4, 5, 7),
        new("应急撤离口", "从这里回店；未装进随身容器的物品会留下。", .91f, .49f, 0, true, 6)
    };
}

public static class Rules
{
    public static int Clamp(int n, int lo, int hi) => System.Math.Max(lo, System.Math.Min(hi, n));
    public static double Hostility(int reputation, int risk, bool searching) => System.Math.Max(.04,
        System.Math.Min(.90, .28 + .065 * risk - Clamp(reputation, -100, 100) * .003 + (searching ? .13 : 0)));
    public static double BegChance(int reputation) => System.Math.Max(.12, System.Math.Min(.85, .48 + Clamp(reputation, -100, 100) * .0035));
    public static int Damage(int power, int armor, bool defending) => System.Math.Max(1,
        (int)System.Math.Round(System.Math.Max(1, power - System.Math.Max(0, armor)) * (defending ? .4 : 1)));
    public static int GunDamage(int firepower) => Clamp(firepower, 5, 90);
    public static int GunAccuracy(int accuracy) => Clamp(55 + accuracy / 3, 55, 95);
}
