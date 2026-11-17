namespace ScavengeExtraction;

public sealed class RaidEnemyItemEffect
{
    public int Healing;
    public int Damage;
}

// A single unit dropped onto a living NPC. Future explosive/chemical items can
// register target effects here without changing inventory or UI drag handling.
public static class EnemyItemEffects
{
    private static readonly Dictionary<string, RaidEnemyItemEffect> effects = new();
    public static event Func<GameItem, RaidEnemyItemEffect?>? Resolve;
    public static void Register(string itemId, RaidEnemyItemEffect effect) => effects[itemId] = effect;
    public static RaidEnemyItemEffect? Get(GameItem item)
    {
        if (effects.TryGetValue(item.identifier, out var effect)) return effect;
        if (Resolve != null)
            foreach (Func<GameItem, RaidEnemyItemEffect?> resolver in Resolve.GetInvocationList())
            {
                try { var result = resolver(item); if (result != null) return result; }
                catch (Exception error) { UnityEngine.Debug.LogWarning("[ScavengeExtraction] 对人物使用物品的扩展失败：" + error.Message); }
            }
        var medicine = ScavengingEffects.Get(item);
        return medicine?.Healing > 0 ? new RaidEnemyItemEffect { Healing = medicine.Healing } : null;
    }
}
