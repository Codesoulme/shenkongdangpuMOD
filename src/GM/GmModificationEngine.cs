using AIWorldPawnshop.Models;

namespace AIWorldPawnshop.GM;

public sealed record GmModificationResult(bool Applied, string Message);

public static class GmModificationEngine
{
    private static readonly GmExecutorRegistry Executors = new();
    private static readonly HashSet<string> Fields = new(StringComparer.OrdinalIgnoreCase)
    { "price_modifier", "demand_modifier", "supply_modifier" };

    public static GmModificationResult Apply(WorldState state, IEnumerable<WorldModificationProposal>? proposals)
    {
        var applied = 0;
        foreach (var proposal in proposals ?? Enumerable.Empty<WorldModificationProposal>())
        {
            if (!TryValidate(proposal, out var reason)) continue;
            var field = proposal.Field.Trim().ToLowerInvariant();
            var value = Math.Clamp(proposal.Value, -0.50, 0.50);
            var key = string.IsNullOrWhiteSpace(proposal.TargetKey) ? "global" : proposal.TargetKey.Trim();
            var existing = state.ActiveModifications.FirstOrDefault(x =>
                x.TargetKey.Equals(key, StringComparison.OrdinalIgnoreCase) &&
                x.Field.Equals(field, StringComparison.OrdinalIgnoreCase) &&
                x.StartDay == state.CurrentDay);
            if (existing is not null)
            {
                existing.Value = proposal.Operation.Equals("replace", StringComparison.OrdinalIgnoreCase)
                    ? value : Math.Clamp(existing.Value + value, -0.50, 0.50);
                existing.DurationDays = Math.Max(existing.DurationDays, proposal.DurationDays);
                continue;
            }
            state.ActiveModifications.Add(new WorldModificationState
            {
                TargetKey = key, Field = field, Value = value, StartDay = state.CurrentDay,
                DurationDays = proposal.DurationDays, Title = Clip(proposal.Title, "世界变化"),
                Summary = Clip(proposal.Summary, "一项经 GM 裁决的世界变化已生效。"),
                Evidence = Clip(proposal.Evidence, "对话中的可验证线索")
            });
            if (!Executors.TryGet("world.modify", out var executor) ||
                !executor.Verify(state, key, field, value))
            {
                state.ActiveModifications.RemoveAll(x => x.TargetKey.Equals(key, StringComparison.OrdinalIgnoreCase) && x.Field.Equals(field, StringComparison.OrdinalIgnoreCase) && x.StartDay == state.CurrentDay);
                continue;
            }
            state.News.Add(new NewsRecord { Day = state.CurrentDay, Title = Clip(proposal.Title, "世界变化"),
                Body = $"{Clip(proposal.Summary, "世界状态已调整。")}（影响：{field} {value:+0.##;-0.##;0}，至第 {state.CurrentDay + proposal.DurationDays - 1} 天）",
                Importance = 2, SourceEventKey = "gm:" + key });
            applied++;
        }
        if (applied == 0) return new(false, "没有通过 GM 边界校验的世界修正。");
        state.TouchRevision();
        return new(true, $"已应用 {applied} 项 GM 世界修正。");
    }

    private static bool TryValidate(WorldModificationProposal p, out string reason)
    {
        reason = string.Empty;
        if (!string.IsNullOrWhiteSpace(p.Goal) && !string.IsNullOrWhiteSpace(p.Path) &&
            !GmInterfaceCatalog.FindGoalPaths(p.Goal).Any(x => x.Path.Equals(p.Path, StringComparison.OrdinalIgnoreCase)))
        { reason = "目标路径不在 GM 目录"; return false; }
        if (!Fields.Contains(p.Field) || !p.Operation.Equals("add", StringComparison.OrdinalIgnoreCase) && !p.Operation.Equals("replace", StringComparison.OrdinalIgnoreCase)) { reason = "字段或操作不在白名单"; return false; }
        if (p.Confidence < 0.60 || p.DurationDays is < 0 or > 30 || Math.Abs(p.Value) > 0.50) { reason = "置信度、期限或幅度越界"; return false; }
        return true;
    }

    private static string Clip(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim()[..Math.Min(value.Trim().Length, 180)];
}
