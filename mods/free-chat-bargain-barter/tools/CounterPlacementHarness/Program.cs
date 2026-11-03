using AIWorldPawnshop.Runtime;

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

Assert(!CounterPlacementRules.IsVisibleClientCounterItem(
        inClientArea: false, isClientOwned: true),
    "顾客仍随身携带的物品不能算作已摆上柜台");
Assert(CounterPlacementRules.IsVisibleClientCounterItem(
        inClientArea: true, isClientOwned: true),
    "顾客物品进入顾客侧格子后才能算作已展示");
Assert(!CounterPlacementRules.IsVisibleClientCounterItem(
        inClientArea: true, isClientOwned: false),
    "非顾客物品即使位于顾客侧格子也不能算作已展示的顾客货物");

Console.WriteLine("Counter placement regression passed.");
