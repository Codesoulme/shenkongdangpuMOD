using Il2Cpp;
using MelonLoader;

namespace AIWorldPawnshop.Runtime;

internal sealed class PendingBargainClarification
{
    public string ClientKey { get; init; } = string.Empty;
    public string Direction { get; init; } = string.Empty;
    public string PlayerText { get; init; } = string.Empty;
    public TradeProposalSnapshot Proposal { get; init; } = new();
    public int CurrentItemId { get; init; }
    public List<int> SnapshotItemIds { get; init; } = new();
}

internal sealed class TradeProposalSnapshot
{
    public string Kind { get; init; } = "none";
    public int Percent { get; init; }
    public long? TargetAmount { get; init; }
    public bool RevokesPriorPriceChange { get; init; }
    public string Reason { get; init; } = string.Empty;
}

internal sealed class BargainAgreement
{
    public string ClientKey { get; init; } = string.Empty;
    public string Direction { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Scope { get; init; } = string.Empty;
    public string FeatureId { get; init; } = string.Empty;
    public string DisplayLine { get; init; } = string.Empty;
    public int Percent { get; init; }
    public bool IncludesFuture { get; init; }
    public HashSet<int> ItemIds { get; } = new();
}

internal static class BargainAgreementRegistry
{
    private static readonly List<BargainAgreement> Agreements = new();
    private static PendingBargainClarification? _pending;
    private static string _activeClientKey = string.Empty;

    public static void TrackClient(StoreClient? client)
    {
        var clientKey = ClientKey(client);
        if (string.Equals(_activeClientKey, clientKey, StringComparison.OrdinalIgnoreCase))
            return;

        Agreements.Clear();
        _pending = null;
        _activeClientKey = clientKey;
    }

    public static PendingBargainClarification? GetPending(string clientKey) =>
        _pending is not null &&
        _pending.ClientKey.Equals(clientKey, StringComparison.OrdinalIgnoreCase)
            ? _pending
            : null;

    public static void SetPending(PendingBargainClarification pending)
    {
        _pending = pending;
    }

    public static void ClearPending() => _pending = null;

    public static void Register(BargainAgreement agreement)
    {
        Agreements.RemoveAll(existing =>
            existing.ClientKey.Equals(agreement.ClientKey, StringComparison.OrdinalIgnoreCase) &&
            existing.Direction.Equals(agreement.Direction, StringComparison.OrdinalIgnoreCase) &&
            existing.Kind.Equals(agreement.Kind, StringComparison.OrdinalIgnoreCase));
        Agreements.Add(agreement);
        _pending = null;
    }

    public static IReadOnlyList<(string FeatureId, List<int> ItemIds)> Revoke(
        string clientKey,
        string direction,
        IReadOnlySet<int> targetIds,
        bool includeFuture)
    {
        var matches = Agreements
            .Where(agreement =>
                agreement.ClientKey.Equals(clientKey, StringComparison.OrdinalIgnoreCase) &&
                agreement.Direction.Equals(direction, StringComparison.OrdinalIgnoreCase) &&
                (includeFuture || agreement.ItemIds.Overlaps(targetIds)))
            .ToList();
        var revoked = new List<(string FeatureId, List<int> ItemIds)>();
        foreach (var agreement in matches)
        {
            var itemIds = includeFuture
                ? agreement.ItemIds.ToList()
                : agreement.ItemIds.Where(targetIds.Contains).ToList();
            revoked.Add((agreement.FeatureId, itemIds));
            if (includeFuture)
            {
                Agreements.Remove(agreement);
                continue;
            }

            agreement.ItemIds.RemoveWhere(targetIds.Contains);
            if (agreement.ItemIds.Count == 0)
                Agreements.Remove(agreement);
        }

        if (revoked.Count > 0)
            _pending = null;
        return revoked;
    }

    public static string BuildUiSection(GameItem? item)
    {
        if (item is null)
            return string.Empty;

        var lines = Agreements
            .Where(agreement => agreement.ItemIds.Contains(item.uniqueId))
            .Select(agreement =>
            {
                var signedPercent = IsDiscount(agreement.Kind)
                    ? $"(-{agreement.Percent}%)"
                    : $"(+{agreement.Percent}%)";
                return $"-{agreement.DisplayLine} {signedPercent}";
            })
            .Distinct()
            .ToList();
        return lines.Count == 0
            ? string.Empty
            : "\n\n对话约定：\n" + string.Join("\n", lines);
    }

    public static void ApplyToNewItem(GameItem? item, string direction)
    {
        if (item is null)
            return;

        try
        {
            var client = ResolveCurrentClient();
            var clientKey = ClientKey(client);
            var agreement = Agreements.LastOrDefault(candidate =>
                candidate.IncludesFuture &&
                candidate.ClientKey.Equals(clientKey, StringComparison.OrdinalIgnoreCase) &&
                candidate.Direction.Equals(direction, StringComparison.OrdinalIgnoreCase));
            if (agreement is null || agreement.ItemIds.Contains(item.uniqueId))
                return;
            if (!IsActiveTradeItem(item, direction))
                return;

            var feature = CreateFeature(agreement.Kind, agreement.Percent);
            if (feature is null)
                return;
            if (item.FindItemFeatureByID(agreement.FeatureId) is null)
                item.AddItemFeature(feature);
            agreement.ItemIds.Add(item.uniqueId);
            PlayerStore.Instance?.RefreshCounterItem();
            NegociationUIManager.Instance?.RefreshUI();
            MelonLogger.Msg(
                $"[当铺世界] 整单对话约定已应用到新商品 {item.uniqueId}");
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[当铺世界] 新商品继承对话约定失败：{ex.Message}");
        }
    }

    public static ItemFeature? CreateFeature(string kind, int percent) => kind switch
    {
        "offer_discount" => ItemFeatureList.BargainDiscount(percent),
        "ask_markup" => ItemFeatureList.BargainMarkup(percent),
        "offer_buying_markup" => ItemFeatureList.BargainBuyingMarkup(percent),
        "ask_buying_discount" => ItemFeatureList.BargainBuyingDiscount(percent),
        _ => null
    };

    public static string ClientKey(StoreClient? client) =>
        FirstNotBlank(client?.identifier, client?.displayName);

    private static StoreClient? ResolveCurrentClient()
    {
        var instance = PlayerStore.Instance?.currentClientInstance;
        return instance?.GetClientBlueprint() ?? instance?.storeClient;
    }

    private static bool IsActiveTradeItem(GameItem item, string direction)
    {
        var negotiation = NegociationUIManager.Instance;
        if (negotiation is null)
            return false;

        if (direction == "player_buys_from_client")
            return negotiation.currentNegociatedItem?.uniqueId == item.uniqueId;

        try
        {
            var sellable = negotiation.GetSellableItem();
            if (sellable is null)
                return false;
            for (var i = 0; i < sellable.Count; i++)
            {
                if (sellable[i]?.uniqueId == item.uniqueId)
                    return true;
            }
        }
        catch
        {
            return negotiation.currentNegociatedItem?.uniqueId == item.uniqueId;
        }

        return false;
    }

    private static bool IsDiscount(string kind) =>
        kind is "offer_discount" or "ask_buying_discount";

    private static string FirstNotBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
