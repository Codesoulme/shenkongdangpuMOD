using AIWorldPawnshop.Models;

namespace AIWorldPawnshop.Runtime;

/// <summary>
/// Pure selection rules for the native newspaper projection.  Keeping this
/// independent from Unity objects makes cross-day visibility deterministic
/// and directly regression-testable.
/// </summary>
public static class NativeNewsProjectionRules
{
    public static bool ShouldProject(WorldState state, NewsRecord news)
    {
        if (news.Day == state.CurrentDay)
            return true;

        if (state.ActiveEvents.Any(x =>
                x.EventKey.Equals(news.SourceEventKey, StringComparison.OrdinalIgnoreCase) &&
                IsActive(state.CurrentDay, x.StartDay, x.DurationDays)))
            return true;

        if (!news.SourceEventKey.StartsWith("gm:", StringComparison.OrdinalIgnoreCase))
            return false;

        var targetKey = news.SourceEventKey[3..];
        return state.ActiveModifications.Any(x =>
            x.TargetKey.Equals(targetKey, StringComparison.OrdinalIgnoreCase) &&
            IsActive(state.CurrentDay, x.StartDay, x.DurationDays));
    }

    private static bool IsActive(int day, int startDay, int durationDays) =>
        day >= startDay && (durationDays == 0 || day < startDay + durationDays);
}
