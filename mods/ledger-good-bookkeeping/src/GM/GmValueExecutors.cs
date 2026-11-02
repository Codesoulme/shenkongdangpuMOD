using AIWorldPawnshop.Models;

namespace AIWorldPawnshop.GM;

public sealed record GmValueReadResult(bool Found, double Value, string Message);
public sealed record GmValueWriteResult(bool Applied, string Message);

public interface IGmValueExecutor
{
    string Key { get; }
    GmValueReadResult Read(WorldState state, string targetKey, string field);
    GmValueWriteResult Write(WorldState state, string targetKey, string field, double value);
    bool Verify(WorldState state, string targetKey, string field, double expected);
    GmValueWriteResult Restore(WorldState state, string targetKey, string field, double original);
}

public sealed class WorldEventModifierExecutor : IGmValueExecutor
{
    public string Key => "world.modify";

    public GmValueReadResult Read(WorldState state, string targetKey, string field)
    {
        var item = state.ActiveModifications.LastOrDefault(x =>
            x.TargetKey.Equals(targetKey, StringComparison.OrdinalIgnoreCase) &&
            x.Field.Equals(field, StringComparison.OrdinalIgnoreCase));
        return item is null
            ? new(false, 0, "没有现存修正")
            : new(true, item.Value, "已读取现存 GM 修正");
    }

    public GmValueWriteResult Write(WorldState state, string targetKey, string field, double value)
    {
        if (field is not ("price_modifier" or "demand_modifier" or "supply_modifier"))
            return new(false, "字段不在 GM 白名单");
        if (value is < -0.50 or > 0.50)
            return new(false, "数值超出 GM 边界");
        var item = state.ActiveModifications.LastOrDefault(x =>
            x.TargetKey.Equals(targetKey, StringComparison.OrdinalIgnoreCase) &&
            x.Field.Equals(field, StringComparison.OrdinalIgnoreCase));
        if (item is null)
            return new(false, "没有可写入的活动修正");
        item.Value = value;
        state.TouchRevision();
        return new(true, "已写入 GM 修正");
    }

    public bool Verify(WorldState state, string targetKey, string field, double expected) =>
        Read(state, targetKey, field) is { Found: true, Value: var actual } &&
        Math.Abs(actual - expected) < 0.0001;

    public GmValueWriteResult Restore(WorldState state, string targetKey, string field, double original) =>
        Write(state, targetKey, field, original);
}

public sealed class GmExecutorRegistry
{
    private readonly Dictionary<string, IGmValueExecutor> _executors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["world.modify"] = new WorldEventModifierExecutor()
    };

    public bool TryGet(string key, out IGmValueExecutor executor) => _executors.TryGetValue(key, out executor!);
}
