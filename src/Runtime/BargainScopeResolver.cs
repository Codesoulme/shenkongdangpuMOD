using System.Text.RegularExpressions;

namespace AIWorldPawnshop.Runtime;

internal static class BargainScopes
{
    public const string Unspecified = "unspecified";
    public const string CurrentItem = "current_item";
    public const string CurrentSnapshot = "current_snapshot";
    public const string AllCurrentAndFuture = "all_current_and_future";
    public const string Ambiguous = "ambiguous";
}

internal sealed record BargainScopeDecision(
    string Scope,
    bool NeedsClarification,
    int? RequestedCount,
    string Reason);

internal static class BargainScopeResolver
{
    private static readonly Regex CountRegex = new(
        @"(?:这|那|眼前|当前|本次)?\s*(?<count>[一二两三四五六七八九十\d]+)\s*(?:个|件|瓶|盒|包|把|支|块|桶|罐|份|组|套)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static BargainScopeDecision Resolve(string playerText, int currentItemCount)
    {
        var text = playerText?.Trim() ?? string.Empty;
        if (ContainsAny(
                text,
                "后面加的也算", "后来加的也算", "新增的也算", "之后加的也算",
                "全部都算", "所有都算", "整单", "全部商品", "所有商品",
                "全部打折", "全部加价", "全部", "所有", "全都"))
        {
            return new BargainScopeDecision(
                BargainScopes.AllCurrentAndFuture,
                false,
                null,
                "玩家明确约定当前与后续加入的全部商品");
        }

        var requestedCount = ReadRequestedCount(text);
        if (ContainsAny(
                text,
                "这些", "这批", "眼前这些", "当前这些", "本次这些",
                "现在这些", "桌上这些", "柜台这些") || requestedCount is > 1)
        {
            return new BargainScopeDecision(
                BargainScopes.CurrentSnapshot,
                false,
                requestedCount,
                "玩家明确指向说话当刻的一组商品");
        }

        if (ContainsAny(
                text,
                "这一个", "这个", "这一件", "这件", "这一瓶", "这瓶",
                "当前这个", "当前这件", "眼前这个", "眼前这件") || requestedCount == 1)
        {
            return new BargainScopeDecision(
                BargainScopes.CurrentItem,
                false,
                1,
                "玩家明确指向当前商品");
        }

        if (currentItemCount <= 1)
        {
            return new BargainScopeDecision(
                BargainScopes.CurrentItem,
                false,
                1,
                "柜台当前只有一件相关商品");
        }

        return new BargainScopeDecision(
            BargainScopes.Ambiguous,
            true,
            null,
            "柜台有多件商品，但玩家没有说明价格约定的范围");
    }

    public static string? ResolveClarification(string playerText)
    {
        var text = playerText?.Trim() ?? string.Empty;
        if (ContainsAny(
                text,
                "后面也算", "新增也算", "新加的也算", "之后也算",
                "全部都算", "全都算", "整单都算", "都打折", "都加价"))
            return BargainScopes.AllCurrentAndFuture;

        if (ContainsAny(
                text,
                "只算现在", "只算当前", "只算这些", "新加的不算",
                "后面的不算", "新增不算", "就这些", "仅限这些"))
            return BargainScopes.CurrentSnapshot;

        return null;
    }

    public static ISet<int> SelectTargetIds(
        string scope,
        IReadOnlyCollection<int> snapshotIds,
        int currentItemId,
        IReadOnlyCollection<int>? matchingIds = null,
        int? requestedCount = null)
    {
        if (scope == BargainScopes.CurrentItem)
            return new HashSet<int> { currentItemId };

        var source = matchingIds is { Count: > 0 } ? matchingIds : snapshotIds;
        var ordered = source
            .Distinct()
            .OrderByDescending(id => id == currentItemId)
            .ThenBy(id => id)
            .ToList();
        if (requestedCount is > 0 && ordered.Count > requestedCount.Value)
            ordered = ordered.Take(requestedCount.Value).ToList();
        return new HashSet<int>(ordered);
    }

    private static int? ReadRequestedCount(string text)
    {
        var match = CountRegex.Match(text);
        if (!match.Success)
            return null;

        var value = match.Groups["count"].Value;
        if (int.TryParse(value, out var number))
            return number;
        return value switch
        {
            "一" => 1,
            "二" or "两" => 2,
            "三" => 3,
            "四" => 4,
            "五" => 5,
            "六" => 6,
            "七" => 7,
            "八" => 8,
            "九" => 9,
            "十" => 10,
            _ => null
        };
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));
}
