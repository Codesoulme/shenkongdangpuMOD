# GM 系统规则与第二 AI 知识

GM 系统是第二 AI 与游戏状态之间的受限边界。第二 AI 只提出事实依据和数据变化，不能直接调用 C#、原版对象或任意字段。GM 对提案做确定性校验后才写入世界状态。

## 当前可用知识

- 找货物品由原版 `LootRegistry`、`TableMaster`、`DirectoryMaster` 和 `ItemSpawner` 生成；类别为 `food`、`medical`、`material`、`household`、`weapon`、`tool`、`module`、`access_card`、`junk`。
- 已接入能力：议价、找货委托、回访、随身货物、交换、顾客预算补付，以及对话中的原版离店和电话挂断。原版身份、渠道、库存和交易规则仍是事实来源。
- 对话上下文的 `gm_knowledge` 会携带这些知识和当前世界摘要。
- `GmInterfaceCatalog` 是接口目录：`implemented` 且 `CanModify=true` 的项目才允许规划执行；`discovered` 只代表从原版转储确认存在，不能当作已接入能力。
- 每个可修改接口还必须绑定 `IGmValueExecutor` 的 `Read`、`Write`、`Verify` 三步。写入后验证失败会撤销本次修正，不生成新闻或玩家提示；没有这三步的接口只能供 GM AI 分析，不能承诺执行。
- 已建立的执行器映射：`trade.adjust.selected` → `NativeTradeAdapter`；`procurement.request.goods` → `NativeProcurementAdapter`；`visit.request.self` → `NativeReturnVisitAdapter`；`inventory.reveal.carried` → `TemporaryClientInventoryAdapter`；`barter.open.native` → `NativeBarterAdapter`；`wallet.transfer.native` → `NativeWalletAdapter`；`world.modify` → `GmModificationEngine`。
- 每日事件在换日时按世界种子和日期确定性选择一至两个候选并写入新闻流；原版 `NewsUIManager.PopulateUI` 已登记但尚未确认安全注入点，因此当前通过 Mod 世界页面呈现。
- 第三方联系任务第一版会记录来源人物、目标人物、目的和到期日，跨天保存在 `ThirdPartyTasks`；它是私人任务，只在联系安排页面显示，不自动写入世界新闻。目标人物在后续对话中可通过 `decision=accept/reject` 作出二次决定，超期自动标记为 expired。原版来客队列接入仍需后续完成。

## 提案格式

模型响应可选 `world_modifications` 数组，每项包含：

`target_key`、`field`、`operation`（`add` 或 `replace`）、`value`、`duration_days`、`title`、`summary`、`evidence`、`confidence`。

## 裁决规则

1. 只允许 `price_modifier`、`demand_modifier`、`supply_modifier` 三个字段，幅度限制为 -0.50 至 0.50。
2. `duration_days=0` 表示对话明确要求长期或永久生效；正数表示临时期限，最多 30 天。没有明确期限时，GM AI 必须根据语义选择或拒绝，不能擅自永久化。
3. 同一目标、字段、当天的提案幂等合并，不重复叠加同一请求。
4. 每日换日自动移除到期修正。每次生效都会生成玩家可见新闻，并在 F7 世界页面显示原因、数据影响和到期日。
5. 不得声称改变未接入的原版机制（例如尚未发现的偷窃概率、剧情变量、物品掉落表权重）。第二 AI 在对话中就应拒绝无法落地的承诺；GM 不向玩家显示内部拒绝原因。顾客补付只能使用 `wallet.transfer.native`，并且必须在当前顾客启用原版预算且余额校验通过后才算生效。

警察承诺、天气、供应链中断等只是证据来源，不是硬编码特例；只要它们能合理映射到上述已接入字段，就按同一规则处理，否则提示暂不可影响游戏数据。
