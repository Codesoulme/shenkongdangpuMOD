namespace AIWorldPawnshop.Compatibility;

internal static class LedgerGameAccess
{
    internal static StoreClient? GetClient(StoreClientInstance? instance)
    {
        if (instance is null) return null;
        try { return instance.GetClientBlueprint(); }
        catch { return null; }
    }

    internal static bool GetMarkupApplied(BargainUIManager? manager) =>
        manager is not null && manager.MarkupOffered;

    internal static bool GetBuyingDiscountApplied(BargainUIManager? manager) =>
        manager is not null && manager.BuyingDiscountOffered;
}
