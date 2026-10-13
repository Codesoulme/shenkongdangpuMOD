using AIWorldPawnshop.Models;

namespace AIWorldPawnshop.Runtime;

internal sealed class BarterValueCheckResult
{
    public bool Allowed { get; init; }
    public bool ValuesKnown { get; init; }
    public long ClientTotalValue { get; init; }
    public long PlayerTotalValue { get; init; }
    public double PlayerToClientRatio { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Checks the economic side of a conversation barter before it reaches the
/// native offer. The model may account for need or urgency, but it cannot
/// bypass missing values or an extreme imbalance.
/// </summary>
internal static class BarterValuePolicy
{
    private const double HardMinimumRatio = 0.5d;
    private const double SoftMinimumRatio = 0.75d;

    public static BarterValueCheckResult Evaluate(
        ConversationRequest request,
        ConversationResponse response)
    {
        var proposal = response.BarterProposal;
        if (!proposal.Explicit || !proposal.Accepted)
        {
            return new BarterValueCheckResult
            {
                Allowed = true,
                ValuesKnown = false,
                Message = "交换提案未被接受"
            };
        }

        var clientItems = ResolveClientItems(request.GameContext.Trade, proposal);
        var playerItems = ResolvePlayerItems(request.GameContext.Trade, proposal);
        var clientValue = SumValues(clientItems);
        var playerValue = SumValues(playerItems);
        if (clientValue is null || playerValue is null || clientValue <= 0 || playerValue <= 0)
        {
            return Reject("交换双方的原版价值无法完整确认");
        }

        var cash = response.WalletTransfer.Explicit &&
                   response.WalletTransfer.Accepted
            ? Math.Max(0, response.WalletTransfer.Amount)
            : 0;
        var clientTotal = SaturatingAdd(clientValue.Value, cash);
        if (clientTotal <= 0)
            return Reject("顾客一侧的交换价值无法确认");

        var ratio = playerValue.Value / (double)clientTotal;
        var assessment = NormalizeDecision(proposal.ValueAssessment.Decision);
        var reason = proposal.ValueAssessment.Reason?.Trim() ?? string.Empty;

        if (assessment is "reject" or "adjust")
            return Reject("人物或 GM 认为交换条件需要拒绝或调整", clientTotal, playerValue.Value, ratio);

        if (ratio < HardMinimumRatio)
            return Reject("玩家提供的价值明显低于顾客给出的物品和补付款", clientTotal, playerValue.Value, ratio);

        if (ratio < SoftMinimumRatio &&
            (assessment != "accept" || string.IsNullOrWhiteSpace(reason)))
        {
            return Reject("交换价值偏低，且没有明确的顾客需求或其他合理理由", clientTotal, playerValue.Value, ratio);
        }

        return new BarterValueCheckResult
        {
            Allowed = true,
            ValuesKnown = true,
            ClientTotalValue = clientTotal,
            PlayerTotalValue = playerValue.Value,
            PlayerToClientRatio = ratio,
            Message = $"交换价值复核通过：顾客一侧 {clientTotal}，玩家一侧 {playerValue.Value}"
        };
    }

    private static BarterValueCheckResult Reject(
        string message,
        long clientTotal = 0,
        long playerTotal = 0,
        double ratio = 0)
    {
        return new BarterValueCheckResult
        {
            Allowed = false,
            ValuesKnown = clientTotal > 0 && playerTotal > 0,
            ClientTotalValue = clientTotal,
            PlayerTotalValue = playerTotal,
            PlayerToClientRatio = ratio,
            Message = message
        };
    }

    private static string NormalizeDecision(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "accept" or "accepted" or "approve" or "approved" or "fair" => "accept",
            "reject" or "rejected" or "decline" or "declined" => "reject",
            "adjust" or "counter" or "counteroffer" or "counter_offer" => "adjust",
            _ => "unknown"
        };

    private static List<NativeItemContext> ResolveClientItems(
        NativeTradeContext trade,
        BarterProposal proposal)
    {
        var candidates = trade.ClientCarriedItems
            .Concat(trade.Item is { Owner: "client" } ? new[] { trade.Item } : Array.Empty<NativeItemContext>())
            .Where(item => item is not null)
            .GroupBy(RuntimeKey)
            .Select(group => group.First())
            .ToList();

        if (proposal.ClientItemRuntimeKey is > 0)
        {
            var match = candidates.FirstOrDefault(item =>
                item.RuntimeKey == proposal.ClientItemRuntimeKey.Value);
            return match is null ? new List<NativeItemContext>() : new List<NativeItemContext> { match };
        }

        var current = trade.Item;
        if (current is { Owner: "client" })
            return new List<NativeItemContext> { current };

        return candidates.Count == 1 ? new List<NativeItemContext> { candidates[0] } : new List<NativeItemContext>();
    }

    private static List<NativeItemContext> ResolvePlayerItems(
        NativeTradeContext trade,
        BarterProposal proposal)
    {
        var candidates = trade.PlayerOfferedItems
            .Concat(trade.Item is { Owner: "player" } ? new[] { trade.Item } : Array.Empty<NativeItemContext>())
            .Where(item => item is not null)
            .GroupBy(RuntimeKey)
            .Select(group => group.First())
            .ToList();
        var runtimeKeys = proposal.RequiredPlayerItemRuntimeKeys
            .Where(key => key > 0)
            .Append(proposal.RequiredPlayerItemRuntimeKey ?? 0)
            .Where(key => key > 0)
            .Distinct()
            .ToList();
        if (runtimeKeys.Count > 0)
            return runtimeKeys
                .Select(key => candidates.FirstOrDefault(item => item.RuntimeKey == key))
                .Where(item => item is not null)
                .Cast<NativeItemContext>()
                .ToList();

        var current = trade.Item;
        if (current is { Owner: "player" })
            return new List<NativeItemContext> { current };

        return candidates.Count == 1 ? new List<NativeItemContext> { candidates[0] } : new List<NativeItemContext>();
    }

    private static long? SumValues(IEnumerable<NativeItemContext> items)
    {
        var list = items.ToList();
        if (list.Count == 0)
            return null;

        long total = 0;
        foreach (var item in list)
        {
            var unitValue = item.NegotiatedValue > 0
                ? item.NegotiatedValue
                : item.CurrentValue > 0
                    ? item.CurrentValue
                    : item.BaseValue;
            if (unitValue <= 0)
                return null;

            var quantity = Math.Max(1, item.Quantity);
            total = SaturatingAdd(total, SaturatingMultiply(unitValue, quantity));
        }
        return total > 0 ? total : null;
    }

    private static int RuntimeKey(NativeItemContext item) => item.RuntimeKey;

    private static long SaturatingMultiply(long left, int right)
    {
        if (left <= 0 || right <= 0)
            return 0;
        return left > long.MaxValue / right
            ? long.MaxValue
            : left * right;
    }

    private static long SaturatingAdd(long left, long right)
    {
        if (right <= 0)
            return left;
        return left > long.MaxValue - right
            ? long.MaxValue
            : left + right;
    }
}
