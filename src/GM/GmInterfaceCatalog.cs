using AIWorldPawnshop.Capabilities;

namespace AIWorldPawnshop.GM;

public sealed class GmInterfaceDescriptor
{
    public string Key { get; init; } = string.Empty;
    public string Owner { get; init; } = string.Empty;
    public string Purpose { get; init; } = string.Empty;
    public string Status { get; init; } = "discovered";
    public bool CanModify { get; init; }
    public string ModificationBoundary { get; init; } = string.Empty;
    public string Executor { get; init; } = string.Empty;
    public List<string> Inputs { get; init; } = new();
    public List<string> Outputs { get; init; } = new();
    public List<string> Preconditions { get; init; } = new();
    public string Verification { get; init; } = string.Empty;
}

public static class GmInterfaceCatalog
{
    public sealed class GoalPath
    {
        public string Goal { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
        public int Priority { get; init; }
        public string Requires { get; init; } = string.Empty;
        public string Fallback { get; init; } = string.Empty;
    }

    public static IReadOnlyList<GoalPath> GoalPaths { get; } = new List<GoalPath>
    {
        new() { Goal = "reduce_store_loss", Path = "burglary_attempt_rate", Priority = 100, Requires = "native.PlayerStore.HandleInsurance write point verified", Fallback = "alarm_stop_rate" },
        new() { Goal = "reduce_store_loss", Path = "alarm_stop_rate", Priority = 80, Requires = "alarm exists and is active", Fallback = "insurance_coverage" },
        new() { Goal = "reduce_store_loss", Path = "alarm_performance", Priority = 70, Requires = "alarm exists and performance write point verified", Fallback = "protection_service" },
        new() { Goal = "reduce_store_loss", Path = "protection_service", Priority = 60, Requires = "existing protection service active", Fallback = "security_text_only" },
        new() { Goal = "increase_supply", Path = "supply_modifier", Priority = 80, Requires = "category is known", Fallback = "procurement_request" },
        new() { Goal = "change_prices", Path = "price_modifier", Priority = 70, Requires = "target category or global scope", Fallback = "demand_modifier" },
        new() { Goal = "change_prices", Path = "demand_modifier", Priority = 60, Requires = "known buyer demand target", Fallback = "none" },
        new() { Goal = "change_demand", Path = "demand_modifier", Priority = 80, Requires = "known buyer demand target", Fallback = "none" },
        new() { Goal = "change_supply", Path = "supply_modifier", Priority = 80, Requires = "known supply target", Fallback = "none" }
    };

    public static IReadOnlyList<GoalPath> FindGoalPaths(string goal) =>
        GoalPaths.Where(x => x.Goal.Equals(goal, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Priority).ToList();

    public static IReadOnlyList<GoalPath> FindExecutableGoalPaths(string goal) =>
        FindGoalPaths(goal).Where(x => IsExecutablePath(x.Path)).ToList();

    public static bool IsExecutablePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (path.Equals("price_modifier", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("demand_modifier", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("supply_modifier", StringComparison.OrdinalIgnoreCase))
        {
            var descriptor = All.FirstOrDefault(x =>
                x.Key.Equals("world.event.modifiers", StringComparison.OrdinalIgnoreCase));
            return descriptor is { Status: "implemented", CanModify: true };
        }

        return All.Any(x => x.CanModify &&
                            x.Status.Equals("implemented", StringComparison.OrdinalIgnoreCase) &&
                            (x.Key.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                             x.Key.EndsWith("." + path, StringComparison.OrdinalIgnoreCase)));
    }

    public static string DescribeExecutableGoalPaths(string goal) =>
        string.Join("；", FindExecutableGoalPaths(goal).Select(x =>
            $"{x.Path}(优先级{x.Priority},条件:{x.Requires})"));

    public static IReadOnlyList<GmInterfaceDescriptor> All { get; } = new List<GmInterfaceDescriptor>
    {
        new() { Key = "capability.trade.adjust", Owner = "NativeTradeAdapter", Purpose = "调用原版议价修改当前报价", Status = "implemented", CanModify = true, ModificationBoundary = "仅当前交易、1%至50%", Executor = "trade.adjust.selected", Inputs = new() { "current item", "percent", "scope" }, Outputs = new() { "native accept/reject", "refreshed quote" }, Preconditions = new() { "store scene", "current trade item" }, Verification = "读取原版议价结果" },
        new() { Key = "capability.procurement.request", Owner = "NativeProcurementAdapter", Purpose = "按原版渠道和掉落表登记找货", Status = "implemented", CanModify = true, ModificationBoundary = "只创建受限委托", Executor = "procurement.request.goods", Inputs = new() { "category", "due day", "premium" }, Outputs = new() { "request status", "resolved item" }, Preconditions = new() { "allowed category", "unique category" }, Verification = "委托状态和原版生成物品" },
        new() { Key = "capability.visit.request", Owner = "NativeReturnVisitAdapter", Purpose = "安排当前顾客未来回访", Status = "implemented", CanModify = true, ModificationBoundary = "使用原版来客队列", Executor = "visit.request.self", Inputs = new() { "character", "due day" }, Outputs = new() { "scheduled visit" }, Preconditions = new() { "character can return" }, Verification = "回访状态和来客队列" },
        new() { Key = "capability.inventory.reveal", Owner = "TemporaryClientInventoryAdapter", Purpose = "展示真实随身物品", Status = "implemented", CanModify = false, ModificationBoundary = "只读真实库存", Executor = "inventory.reveal.carried", Inputs = new() { "character" }, Outputs = new() { "runtime item keys" }, Preconditions = new() { "current client" }, Verification = "物品仍存在于顾客库存" },
        new() { Key = "context.counter.ownership", Owner = "CounterOwnershipSnapshot", Purpose = "区分顾客侧和玩家侧柜台物品归属", Status = "implemented", CanModify = false, ModificationBoundary = "只读 frontInvinvElement、backInvinvElementCounter 和 IsItemOwned", Executor = "conversation.context.trade", Inputs = new() { "current client", "counter areas" }, Outputs = new() { "owner=client/player/unknown" }, Preconditions = new() { "store scene" }, Verification = "顾客物品不会进入 player_offered_items" },
        new() { Key = "capability.barter.open", Owner = "NativeBarterAdapter", Purpose = "打开原版交换流程", Status = "implemented", CanModify = true, ModificationBoundary = "等待原版确认", Executor = "barter.open.native", Inputs = new() { "client item", "player item" }, Outputs = new() { "native confirmation" }, Preconditions = new() { "both real items" }, Verification = "原版交换窗口和确认结果" },
        new() { Key = "capability.wallet.transfer", Owner = "NativeWalletAdapter", Purpose = "从顾客原版预算补付玩家现金", Status = "implemented", CanModify = true, ModificationBoundary = "仅当前顾客、明确金额、预算余额充足", Executor = "wallet.transfer.native", Inputs = new() { "client budget", "amount" }, Outputs = new() { "budget deduction", "player cash increase" }, Preconditions = new() { "store scene", "client budget enabled", "explicit accepted transfer" }, Verification = "读取转账前后预算和玩家现金并支持回滚" },
        new() { Key = "capability.third_party.contact", Owner = "NativeReturnVisitAdapter", Purpose = "把已知人物联系任务接入原版未来来客队列并记录到场", Status = "implemented", CanModify = true, ModificationBoundary = "只使用已确认的原版人物标识，不覆盖剧情来客", Executor = "visit.request.third_party", Inputs = new() { "source character", "target native identifier", "purpose", "due day" }, Outputs = new() { "queued target", "arrival receipt", "target decision" }, Preconditions = new() { "target original identifier observed", "due day 1-7", "queue conflict checked" }, Verification = "原版队列、到场回执和目标 accept/reject 状态" },
        new() { Key = "world.event.modifiers", Owner = "GmModificationEngine", Purpose = "保存有限期限的供需/价格修正", Status = "implemented", CanModify = true, ModificationBoundary = "三个字段，-0.50至0.50，1至30天", Executor = "world.modify", Inputs = new() { "target", "field", "value", "duration", "evidence" }, Outputs = new() { "active modification", "news" }, Preconditions = new() { "confidence >= 0.60", "known field" }, Verification = "状态修订号、找货供给计算、到期移除、玩家页面" },
        new() { Key = "native.PlayerStore.HandleInsurance", Owner = "PlayerStore", Purpose = "夜间盗窃与保险结算入口", Status = "discovered", CanModify = false, ModificationBoundary = "尚未确认安全写入点" },
        new() { Key = "native.MachineAlarm.GetAlarmeStopRate", Owner = "MachineAlarm", Purpose = "读取警报器阻止率", Status = "readable", CanModify = false, ModificationBoundary = "可读取当前值，尚未确认写入方式", Executor = "security.probe" },
        new() { Key = "native.MachineAlarm.GetAlarmePerformance", Owner = "MachineAlarm", Purpose = "读取警报器性能", Status = "readable", CanModify = false, ModificationBoundary = "可读取当前值，尚未确认写入方式", Executor = "security.probe" },
        new() { Key = "native.RNG.Roll", Owner = "RNG", Purpose = "执行原版随机判定", Status = "discovered", CanModify = false, ModificationBoundary = "参数语义尚未确认" }
        ,new() { Key = "native.PlayerStore.GetRandomBurglaryEligibleItem", Owner = "PlayerStore", Purpose = "选择夜间可被盗物品", Status = "discovered", CanModify = false, ModificationBoundary = "只能确认读取结果，不能改变筛选规则" }
        ,new() { Key = "native.PlayerStore.isSecurityAlarmActive", Owner = "PlayerStore", Purpose = "表示商店警报器是否启用", Status = "discovered", CanModify = false, ModificationBoundary = "需确认原版安全写入时机" }
        ,new() { Key = "native.OnSleepEvent.IsAlarmActive", Owner = "OnSleepEvent", Purpose = "夜间事件中的警报器状态", Status = "discovered", CanModify = false, ModificationBoundary = "事件参数只读证据" }
        ,new() { Key = "native.ItemSpawner.SpawnFromTable", Owner = "ItemSpawner", Purpose = "从原版掉落表生成物品", Status = "discovered", CanModify = false, ModificationBoundary = "Mod 通过找货适配器间接使用" }
        ,new() { Key = "native.LootRegistry", Owner = "LootRegistry", Purpose = "提供原版掉落表和物品权重", Status = "discovered", CanModify = false, ModificationBoundary = "不能由 GM 改写掉落表" }
        ,new() { Key = "native.StoreClientList.CreateThief", Owner = "StoreClientList", Purpose = "创建偷窃顾客", Status = "discovered", CanModify = false, ModificationBoundary = "尚未接入 Mod" }
        ,new() { Key = "native.NewsUIManager.PopulateUI", Owner = "NewsUIManager", Purpose = "原版新闻界面填充入口", Status = "implemented", CanModify = false, ModificationBoundary = "只投影当前日期 Mod 新闻并与原版事件去重排序", Executor = "news.project.native", Verification = "原版新闻打开时不重复注入，原版事件和 Mod 新闻共同排序" }
    };

    public static IEnumerable<GmInterfaceDescriptor> Executable() => All.Where(x => x.Status == "implemented" && x.CanModify && !string.IsNullOrWhiteSpace(x.Executor));

    public static GmInterfaceDescriptor? Describe(string key) =>
        All.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<GmInterfaceDescriptor> Search(string? query)
    {
        var terms = (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return All.Where(x => terms.Length == 0 || terms.All(term =>
            x.Key.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            x.Owner.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            x.Purpose.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            x.TagsContain(term)))
            .ToList();
    }

    private static bool TagsContain(this GmInterfaceDescriptor descriptor, string term) =>
        descriptor.Inputs.Any(x => x.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
        descriptor.Outputs.Any(x => x.Contains(term, StringComparison.OrdinalIgnoreCase));
}
