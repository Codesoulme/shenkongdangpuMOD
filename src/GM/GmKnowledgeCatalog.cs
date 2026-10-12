using AIWorldPawnshop.Models;

namespace AIWorldPawnshop.GM;

public static class GmKnowledgeCatalog
{
    public static List<string> Build(WorldState state) => new()
    {
        "身份：GM 系统负责把第二 AI 的世界影响提案转换为受限、可回滚的扩展状态；不能直接调用原版方法。",
        "物品来源：找货由原版 LootRegistry/TableMaster/DirectoryMaster/ItemSpawner 的加权表生成；类别包括 food、medical、material、household、weapon、tool、module、access_card、junk。",
        "柜台归属：frontInvinvElement 是顾客侧、backInvinvElementCounter 是玩家侧；GetCountersItems() 会合并两侧，不能把合并结果当成玩家物品。原版 IsItemOwned=false 或顾客侧物品属于当前顾客，IsItemOwned=true 且在玩家侧才可作为玩家交换物。上下文中的 item.owner 是唯一归属结论：client 就是顾客带来的物品，player 才是玩家物品，unknown 必须保持不确定。identifier、internal_name、display_name、tags、condition_identifier 只用于识别物品，不得覆盖 owner；不得因显示名、议价方向、顾客说“没带东西”或物品出现在合并柜台而改判归属。",
        "物品上下文：identifier 是原版物品标识，internal_name 是原版内部名称，tags 是实际 itemTypes，condition_identifier 只在原版物品特征提供当前条件标识时出现；空值表示原版没有可读值。不要把 runtime_key 当物品种类或归属证据，也不要把缺少条件标识当成玩家物品。",
        "已接能力：议价、找货委托、回访、随身货物、交换、顾客预算补付；这些能力有各自的原版事实校验。",
        "接口状态：" + string.Join("；", GmInterfaceCatalog.All.Select(x => $"{x.Key}={x.Status}{(x.CanModify ? ",可修改" : ",只读或未接入")}")),
        "目标路径：" + string.Join("；", GmInterfaceCatalog.GoalPaths.Select(x => $"{x.Goal}->{x.Path}(优先级{x.Priority},条件:{x.Requires},备用:{x.Fallback})")),
        "可修改字段：GM 事件的 price_modifier、demand_modifier、supply_modifier；供给会进入找货成功率，价格和供需会在当前及后续原版来客生成时投影到 buyPriceModifier/sellPriceModifier；数值必须在 -0.50 到 0.50，duration_days=0 表示按对话语义永久生效，正数表示临时期限。",
        "不可修改：原版人物身份、物品表权重、任意库存数量、剧情变量、任意未知字段；金钱只能通过 wallet.transfer.native 从当前顾客已启用且余额足够的原版预算扣除并加入玩家现金；没有已验证接口的偷窃概率不能声称已改变。",
        "提案必须包含 target_key、field、operation(add/replace)、value、duration_days、title、summary、evidence、confidence；证据不足时拒绝。",
        "第三方联系：只有已经从原版 StoreClient 观察到的 OriginalIdentifier 才能写入 futurStoreClientIdQueue；Mod 的 canonical character key 不能直接当作原版来客标识。队列冲突时保持 proposed，不伪称已安排；目标到店后状态为 arrived，目标人物明确接受或拒绝后才变成 fulfilled 或 declined。",
        "已知人物目录：" + string.Join("；", state.Characters.Values
            .Where(x => !string.IsNullOrWhiteSpace(x.CharacterKey) ||
                        !string.IsNullOrWhiteSpace(x.DisplayName))
            .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(48)
            .Select(x =>
                $"key={x.CharacterKey}, name={x.DisplayName}, native={x.OriginalIdentifier}, faction={x.FactionKey}, can_return={x.CanReturn}")),
        $"当前世界第 {state.CurrentDay} 天；现有事件 {state.ActiveEvents.Count} 个，现有 GM 修正 {state.ActiveModifications.Count} 个。",
        "当前安全状态：" + GmSecurityProbe.Capture().Summary
    };
}
