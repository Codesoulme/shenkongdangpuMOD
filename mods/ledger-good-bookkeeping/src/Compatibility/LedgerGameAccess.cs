using System.Reflection;

namespace AIWorldPawnshop.Compatibility;

internal static class LedgerGameAccess
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static StoreClient? GetClient(StoreClientInstance? instance)
    {
        if (instance is null) return null;
        try
        {
            var client = instance.GetClientBlueprint();
            if (client is not null) return client;
        }
        catch { }
        return GetField<StoreClient>(instance, "storeClient");
    }

    internal static bool GetMarkupApplied(BargainUIManager? manager) =>
        manager is not null && GetField<bool>(manager, "markupApplied");

    internal static bool GetBuyingDiscountApplied(BargainUIManager? manager) =>
        manager is not null && GetField<bool>(manager, "buyingDiscountApplied");

    private static T GetField<T>(object instance, string name)
    {
        try
        {
            var value = instance.GetType().GetField(name, InstanceFlags)?.GetValue(instance);
            if (value is T typed) return typed;
        }
        catch { }
        return default!;
    }
}
