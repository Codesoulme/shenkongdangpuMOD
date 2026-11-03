namespace AIWorldPawnshop.Runtime;

internal static class CounterPlacementRules
{
    // Ownership alone does not prove that an item was moved from the
    // customer's carried inventory onto the visible customer-side grid.
    internal static bool IsVisibleClientCounterItem(bool inClientArea, bool isClientOwned) =>
        inClientArea && isClientOwned;
}
