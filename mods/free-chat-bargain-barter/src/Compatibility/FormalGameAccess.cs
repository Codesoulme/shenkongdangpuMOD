using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AIWorldPawnshop.Compatibility;

/// <summary>
/// Version boundary for the formal game's private backing fields.  The
/// public API is preferred wherever the release exposes one; reflection is
/// kept here so version-specific access does not leak into the adapters.
/// </summary>
internal static class FormalGameAccess
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static StoreClient? GetClient(StoreClientInstance? instance)
    {
        if (instance is null)
            return null;
        try
        {
            var client = instance.GetClientBlueprint();
            if (client is not null)
                return client;
        }
        catch { }
        return GetField<StoreClient>(instance, "storeClient");
    }

    public static GameGridInventory? GetInventory(StoreClientInstance? instance) =>
        instance is null ? null : GetField<GameGridInventory>(instance, "gridInv");

    public static IReadOnlyList<GameItem> GetItems(GameGridInventory? inventory)
    {
        if (inventory is null)
            return Array.Empty<GameItem>();
        try
        {
            var items = inventory.childItems;
            if (items is not null)
                return items;
        }
        catch { }
        return GetField<List<GameItem>>(inventory, "items") ?? new List<GameItem>();
    }

    public static Button? GetDialogButton(DialogChoiceBubble? bubble)
    {
        if (bubble is null)
            return null;
        return GetField<Button>(bubble, "button") ?? bubble.GetComponent<Button>();
    }

    public static TextMeshProUGUI? GetNegotiationSubtitle(NegociationUIManager? manager)
    {
        var frame = manager is null ? null : GetField<NegotiationTabletFrame>(manager, "frame");
        return frame?.clientSubtitle;
    }

    public static string GetPhoneNumber(PhoneUIManager? manager)
    {
        return manager is null
            ? string.Empty
            : GetField<long>(manager, "currentNumber").ToString();
    }

    public static StoreClient? GetTrackedClient(BargainUIManager? manager) =>
        manager is null ? null : GetField<StoreClient>(manager, "trackedClient");

    public static bool GetMarkupApplied(BargainUIManager? manager) =>
        manager is not null && GetField<bool>(manager, "markupApplied");

    public static bool GetBuyingDiscountApplied(BargainUIManager? manager) =>
        manager is not null && GetField<bool>(manager, "buyingDiscountApplied");

    public static int GetClientBudget(StoreClient? client)
    {
        if (client is null)
            return 0;
        try { return client.GetBudget(); }
        catch { return GetField<int>(client, "clientBudget"); }
    }

    public static IReadOnlyList<StoreEvent> GetActiveEvents(StoreEventManager? manager)
    {
        if (manager is null)
            return Array.Empty<StoreEvent>();
        try { return manager.GetActiveEvents() ?? new List<StoreEvent>(); }
        catch { return GetField<List<StoreEvent>>(manager, "activeEvents") ?? new List<StoreEvent>(); }
    }

    public static bool IsFeatureExposed(ItemFeature? feature)
    {
        if (feature is null)
            return false;
        try { return feature.IsExposed(); }
        catch { return GetField<bool>(feature, "isFeatureExposed"); }
    }

    public static bool IsFeatureDiscovered(ItemFeature? feature)
    {
        if (feature is null)
            return false;
        try { return feature.IsFeatureDiscovered(); }
        catch { return GetField<bool>(feature, "isFeatureDiscovered"); }
    }

    public static string GetPublicDisplay(ItemFeature? feature)
    {
        if (feature is null)
            return string.Empty;
        try { return feature.GetPublicDisplay() ?? string.Empty; }
        catch { return GetField<string>(feature, "publicDisplay") ?? string.Empty; }
    }

    public static string GetActualDisplay(ItemFeature? feature)
    {
        if (feature is null)
            return string.Empty;
        try { return feature.GetActualDisplay() ?? string.Empty; }
        catch { return GetField<string>(feature, "actualDisplay") ?? string.Empty; }
    }

    public static void RebuildLootRegistry()
    {
        try
        {
            var method = typeof(LootRegistry).GetMethod("RebuildIfDirty", StaticFlags);
            method?.Invoke(null, null);
        }
        catch { }
    }

    private static T GetField<T>(object instance, string name)
    {
        try
        {
            var field = instance.GetType().GetField(name, InstanceFlags);
            var value = field?.GetValue(instance);
            if (value is T typed)
                return typed;
        }
        catch { }
        return default!;
    }

}
