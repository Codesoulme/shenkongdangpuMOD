using ScavengeExtraction.Core;
using Newtonsoft.Json;

int assertions = 0;
void Check(bool condition, string message)
{
    assertions++;
    if (!condition) throw new Exception(message);
}
var state = new RaidState();
state.Player.Hp = 43; state.Player.Bleeding = 2; state.Player.WeaponUid = 179;
state.BeginNight(7);
Check(state.Player.Hp == 43 && state.Player.Bleeding == 2, "Entering night must not heal or stop bleeding");
Check(state.Player.WeaponUid == 179, "Equipment selection persists across nights");
Check(state.CanExtract, "Entrance must be a safe exit");
Check(!state.Move(7), "Cannot teleport to a distant exit");
Check(state.Move(1) && !state.CanExtract, "Travel along a connected edge, no extraction from interior");
state.Nodes[1].Searches = 1;
state.BeginNight(7);
Check(state.Nodes[1].Searches == 1, "Same-night return cannot reroll spent searches");
state.BeginNight(8);
Check(state.Nodes[1].Searches == 0 && state.Player.Hp == 43, "New night refreshes map without healing");

state.Move(1);
state.Nodes[1].Enemy = new EnemyState { Name = "警惕的拾荒客", Hp = 25, MaxHp = 25 };
Check(state.StartCombat(), "Enemy combat can start");
Check(!state.Move(0) && !state.CanExtract, "Combat locks travel and return");
Check(!state.SettleEnemy(VictoryChoice.Kill), "Cannot settle a living enemy mid-fight");
state.DamageEnemy(100);
Check(state.Phase == RaidPhase.Victory, "Zero enemy HP enters decision, not automatic death");
Check(!state.Move(0), "Pending victory decision blocks travel");
Check(state.SettleEnemy(VictoryChoice.KnockOut), "Knockout resolves victory");
Check(!state.SettleEnemy(VictoryChoice.Kill), "Repeated click cannot settle or duplicate loot");
Check(state.Enemy!.Settled && state.Enemy.Disposition == EnemyDisposition.Gone, "Resolved enemy cannot fight again");
Check(!state.StartCombat(), "No resurrection of settled opponents");
state.DamagePlayer(200);
Check(state.Player.Hp == 0 && state.Phase == RaidPhase.Defeated, "Fatal damage clamps health and opens defeat choice");
Check(state.Player.Heal(40, true) == 0, "Ordinary healing cannot bypass defeat");
Check(!state.CanExtract && !state.Move(0), "Defeat cannot be bypassed through navigation");

var morning = new RaidState(); morning.Player.Hp = 63; morning.Player.WeaponUid = 400;
morning.BeginNight(12); morning.Move(1); morning.Nodes[1].Enemy = new EnemyState { Name = "沉默的巡逻员", Hp = 35 };
string morningData = JsonConvert.SerializeObject(morning);
morning.Player.Hp = 1; morning.Player.WeaponUid = 999; morning.Nodes[1].Searches = 2;
var loaded = JsonConvert.DeserializeObject<RaidState>(morningData)!;
Check(loaded.Player.Hp == 63 && loaded.Player.WeaponUid == 400 && loaded.Nodes[1].Searches == 0,
    "Loading an earlier snapshot discards later health, equipment and exploration");
Check(loaded.Nodes[1].Enemy!.Name == "沉默的巡逻员" && loaded.Nodes[1].Enemy!.Hp == 35, "Enemy state roundtrips");
var secondSlot = JsonConvert.DeserializeObject<RaidState>(JsonConvert.SerializeObject(new RaidState()))!;
Check(secondSlot.Player.Hp == 100 && secondSlot.Player.WeaponUid == 0 && secondSlot.Nodes[1].Enemy == null,
    "Separate save data cannot inherit the prior slot's encounter or injury");

var player = new PlayerCondition { Hp = 90, Bleeding = 3 };
Check(player.Heal(30, true) == 10 && player.Hp == 100 && player.Bleeding == 0, "Medical healing caps and applies hemostasis");
player.Hp = 28; player.Bleeding = 2;
Check(player.Heal(15, false) == 15 && player.Bleeding == 2, "Non-hemostatic salve does not remove bleeding");
Check(Rules.Damage(20, 5, true) == 6, "Defend reduces post-armor damage by sixty percent");
Check(Rules.Damage(5, 1000, false) == 1, "Defense never creates negative damage or healing");

double previousHostility = 1, previousBeg = 0;
for (int reputation = -100; reputation <= 100; reputation++)
{
    double hostility = Rules.Hostility(reputation, 3, false), beg = Rules.BegChance(reputation);
    Check(hostility <= previousHostility && beg >= previousBeg, "Better reputation must not worsen hostility or begging");
    Check(beg is >= .12 and <= .85 && hostility is >= .04 and <= .90, "Probabilities remain bounded");
    Check(Rules.Hostility(reputation, 3, true) >= hostility, "Search increases the provocation risk");
    previousHostility = hostility; previousBeg = beg;
}
for (int i = 0; i < Map.Nodes.Length; i++)
{
    foreach (int neighbor in Map.Nodes[i].Links)
        Check(neighbor >= 0 && neighbor < Map.Nodes.Length && Map.Nodes[neighbor].Links.Contains(i), "Map edges are bidirectional");
    Check(Map.Nodes[i].X is >= .077f and <= .923f, "Node buttons fit within map width");
}
var reachable = new HashSet<int> { 0 };
for (int pass = 0; pass < Map.Nodes.Length; pass++)
    foreach (int node in reachable.ToArray()) foreach (int neighbor in Map.Nodes[node].Links) reachable.Add(neighbor);
Check(reachable.Count == Map.Nodes.Length, "Every area and extraction point is reachable");

// A native PixelWindow root may be 0x0 or 100x100 while its machinery
// children are hundreds of pixels wide. Fit the logical content dimensions.
foreach (var screen in new[] { (1280d, 720d), (1920d, 1080d), (1920d, 1200d), (2560d, 1080d), (3840d, 2160d), (900d, 1600d) })
foreach (double canvasScale in new[] { 1d, 2d, 3d, 4d })
foreach (var upper in new[] { (484d, 410d), (690d, 390d) })
{
    var (w, h) = screen;
    double bottomScale = WindowLayout.FitScale(970 * canvasScale, 252 * canvasScale, .94 * w, .35 * h);
    double bottomWidth = 970 * canvasScale * bottomScale, bottomHeight = 252 * canvasScale * bottomScale;
    double left = WindowLayout.CenterLeft(.03 * w, .94 * w, bottomWidth);
    Check(left >= .03 * w - .001 && left + bottomWidth <= .97 * w + .001, "All pockets and appended equipment stay inside horizontal safe bounds");
    Check(Math.Abs(left + bottomWidth / 2 - w / 2) < .001, "The full rendered bottom row is centred, including appended panels");
    Check(bottomHeight <= .35 * h + .001, "The backpack leaves room for the enlarged map");
    double topBottom = .02 * h + bottomHeight + .012 * h;
    double topScale = WindowLayout.FitScale(upper.Item1 * canvasScale, upper.Item2 * canvasScale, .94 * w, .98 * h - topBottom);
    double topWidth = upper.Item1 * canvasScale * topScale, topHeight = upper.Item2 * canvasScale * topScale;
    double topLeft = WindowLayout.CenterLeft(.03 * w, .94 * w, topWidth);
    Check(Math.Abs(topLeft + topWidth / 2 - w / 2) < .001, "Top map and bottom inventories share the same screen centre");
    Check(topLeft >= .03 * w - .001 && topLeft + topWidth <= .97 * w + .001, "The enlarged map stays inside horizontal bounds");
    Check(.98 * h - topHeight >= topBottom - .001, "The map and backpack cannot overlap vertically");
}
Check(WindowLayout.FitScale(3000, 800, 1800, 600) == .6, "Use content width, not a zero/default root rect, for oversized rows");

var target = new EnemyState { Hp = 30, MaxHp = 60 };
Check(target.Heal(20) == 20 && target.Hp == 50, "NPC treatment heals the target, not the player");
Check(target.RewardHealing(20), "Effective treatment can award reputation");
Check(!target.RewardHealing(20), "Repeated treatment cannot farm reputation from one NPC");
Check(target.Heal(20) == 10 && target.Hp == 60, "NPC healing caps at actual missing health");
Check(target.Heal(20) == 0 && !target.RewardHealing(0), "Full-health NPCs yield neither healing nor reputation");
Check(target.TryBeginTheft() && !target.TryBeginTheft(), "A single NPC cannot be repeatedly pickpocketed");
target.SupplyStolen = true;
var targetReloaded = JsonConvert.DeserializeObject<EnemyState>(JsonConvert.SerializeObject(target))!;
Check(targetReloaded.SupplyStolen && !targetReloaded.TryBeginTheft() && !targetReloaded.RewardHealing(10),
    "Reloading preserves stolen loot and reputation limits");
target.Hp = 0;
Check(target.Heal(40) == 0, "Dragging medicine cannot resurrect a defeated NPC or bypass settlement");
target.Hp = 20; target.Settled = true;
Check(target.Heal(40) == 0, "A released or resolved NPC cannot be healed through a stale portrait");

foreach (int node in Enumerable.Range(0, Map.Nodes.Length))
foreach (RaidPhase phase in Enum.GetValues<RaidPhase>())
{
    var exitState = new RaidState { Node = node, Phase = phase };
    Check(exitState.CanExtract == (phase == RaidPhase.Exploring && (node == 0 || node == 7)),
        "Extraction visibility only allows the two exit nodes while exploring");
}
var group = new RaidState(); group.BeginNight(20); group.Move(1);
var firstNpc = new EnemyState { Name = "警惕的拾荒客", Hp = 40, MaxHp = 60 };
var secondNpc = new EnemyState { Name = "沉默的巡逻员", Hp = 50, MaxHp = 70 };
var thirdNpc = new EnemyState { Name = "落魄的教徒", Hp = 25, MaxHp = 50 };
group.Nodes[1].AddEnemy(firstNpc); group.Nodes[1].AddEnemy(secondNpc); group.Nodes[1].AddEnemy(thirdNpc);
Check(group.Nodes[1].GetEnemies().Count() == 3, "One node can hold several independently addressable NPCs");
Check(group.SelectEnemy(secondNpc) && ReferenceEquals(group.Enemy, secondNpc), "Clicking a second portrait selects that exact NPC");
Check(group.MayUseOnEnemy(firstNpc) && group.MayUseOnEnemy(thirdNpc), "Peaceful item drops can address any living NPC in this node");
Check(!group.SelectEnemy(new EnemyState()) && ReferenceEquals(group.Enemy, secondNpc), "A stale or remote portrait cannot change the current target");
Check(group.StartCombat(), "Combat starts against the selected NPC");
Check(!group.SelectEnemy(firstNpc) && !group.MayUseOnEnemy(firstNpc), "Combat locks selection and item drops to the current opponent");
Check(group.MayUseOnEnemy(secondNpc), "The current opponent remains a valid item target");
group.DamageEnemy(20);
Check(secondNpc.Hp == 30 && firstNpc.Hp == 40 && thirdNpc.Hp == 25, "Attacks damage only the selected opponent");
group.DamageEnemy(500);
Check(group.SettleEnemy(VictoryChoice.KnockOut), "A selected opponent can be settled independently");
Check(secondNpc.Settled && !firstNpc.Settled && !thirdNpc.Settled, "Resolving one NPC preserves everyone else");
Check(ReferenceEquals(group.Enemy, firstNpc) && !group.MayUseOnEnemy(secondNpc), "Settlement selects a living NPC without reviving the downed one");
thirdNpc.SupplyStolen = true; thirdNpc.TheftAttempted = true;
var loadedGroup = JsonConvert.DeserializeObject<RaidState>(JsonConvert.SerializeObject(group))!;
var loadedPeople = loadedGroup.Nodes[1].GetEnemies().ToArray();
Check(loadedPeople.Length == 3 && loadedPeople[0].Hp == 40 && loadedPeople[1].Settled && loadedPeople[2].SupplyStolen,
    "All NPC health, settlement and theft data survives saving independently");
Check(group.Move(0) && group.Enemy == null && !group.MayUseOnEnemy(firstNpc), "Leaving a node invalidates its portrait targets");
Check(group.Move(1) && ReferenceEquals(group.Enemy, firstNpc), "Returning restores living inhabitants without generating copies");
var oldNode = JsonConvert.DeserializeObject<NodeState>("{\"Enemy\":{\"Name\":\"旧版拾荒客\",\"Hp\":33}}")!;
Check(oldNode.GetEnemies().Count() == 1 && oldNode.GetEnemies().Single().Hp == 33, "Legacy single-NPC saves retain their original encounter");
Console.WriteLine($"PASS: {assertions} checks (health, saves, combat, reputation, map, sidebar bounds, NPC targeting/treatment/theft, exit visibility).");
