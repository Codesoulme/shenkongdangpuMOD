# 《信用点收支显示&账本：好好记账》

适用于 Probably Stolen 正式版 0.46D 的原生 Mod。它只负责记录信用点流水，并将账本加入游戏原生日历。

## 功能

- 打开日历保留原版默认页面，点击账本标签查看当天或昨天
- 显示当日收入、支出与净额
- 分列记录收支方向、顾客、交易类型、物品、明细、金额和成交后的剩余信用点
- 成交前快照记录实际生效的事件、顾客、物品状态、议价和批发价格修正
- 保留完整容器名称和规格；液体显示当前量／容量（ml），能源物品显示剩余／容量（kJ）
- 仅折叠相邻且同名、同规格、同单价、同价格修正的物品，支持分页和悬停查看完整文字
- 账本跟随当前游戏存档保存和读取
- 读取旧版账本记录并迁移到独立账本文件
- 信用点收支显示飘字；批发、批量购入与批量出售各显示一条订单总额，连续提示依次播放

账本不包含聊天、模型请求、游戏资源扫描或世界事件功能，只申请文件权限用于保存账本。

## English

# Keep Your Books Straight

Keep every credit movement easy to review. Select the Ledger tab in the in-game calendar to view today or yesterday. The calendar keeps its original default page.

### Ledger Features

- See daily totals for income, expenses, and net balance
- Separate columns show direction, customer, transaction type, item, details, amount, and the balance after settlement
- Review applied price modifiers, including events, customer effects, condition, bargaining, and wholesale discounts
- See full container names, liquid volumes and capacities, and energy storage levels
- Fold only adjacent items with matching names, specifications, unit prices, and price modifiers
- Browse paginated records; ledger data is saved and restored with the active game save

Customer trades and in-game credit settlements are recorded from their actual balance changes, helping you keep track of where your money went.

## 安装

将发布包中的 `DeepSpacePawnshopLedger.dll`、`manifest.xml` 和 `preview.png` 放入游戏 `Mods/DeepSpacePawnshopLedger` 目录，在游戏 Mod 页面启用即可。

## 兼容性

- 游戏：Probably Stolen 0.46D
- ModLoader：原生 ModLoader
- 当前版本：1.1.9

## 1.1.9 性能优化

日常交易改为仅追加本次新增流水，批量订单结束时追加一批记录，不再同步重写全部历史存档快照。游戏存档、读档和停用模组时归档完整账本，保留存档回滚和旧记录迁移。完整写入的流水可在异常退出后恢复，未完成的末尾写入会丢弃；归档采用代次标识，避免中断后重复回放或读档后混入上一条时间线。

原 `CreditLedger.json` 保留完整归档和存档快照；新增同目录 `CreditLedger.transactions.jsonl` 保存尚未归档的流水。手动备份时请备份整个 `DeepSpacePawnshopLedger` 数据目录。回退旧版前应先正常停用新版或正常退出游戏，确保增量流水已归档；旧版不能读取新的增量文件。

本机相同数据副本的独立基准：1.45 MB 历史账本，单笔约 57–65 ms 降至 0.42 ms，100 件批量约 60 ms 降至 2.40 ms；合成 14.7 MB 历史文件单笔约 312 ms 降至 0.36 ms。这里测量生产记录／存储代码，不包含真实游戏渲染；完整归档仍发生在存读档边界。账本页面和飘字样式保持原值，权限仍仅 `files`。

## 1.1.8 修复

批量交易原先每件物品都会同步序列化整本流水及全部存档快照并写盘，造成明显停顿。现在批量出售、购入和批发均在整单结束时统一保存，账本仍逐项记录；游戏存档及关闭模组时强制落盘，异常中止的订单保留已成交部分。整单飘字保持合计显示，不改变字号、样式和位置。

自动贩卖机提现记录设备名称和累计营业收入来源，市场委托奖励记录委托人、任务标题、物品及交付数量，并保留真实到账后的余额。修复通用来源识别误选账本自身函数的问题。旧流水已丢失的来源无法追溯，在显示时改为“历史记录未保存信用点来源”，不修改原始存储金额。

独立测试中，100 件出售、8 份各 100 条流水的存档快照，处理时间由 2561.0 ms 降至 46.2 ms（单次对比，非游戏帧率测试）。实际游戏画面及流畅度仍需复测。

## 1.1.7 修复

鉴定服务收入增加实际被鉴定物品的完整名称及规格。接受估价时保存传入物品的快照，在鉴定服务费到账时记录；不从桌面的鉴定单猜测对象。明细标注鉴定服务费，连续鉴定各自保留一条服务记录；原来没有保存物品信息的历史记录无法补造名称。

## 1.1.6 修复

补齐批发和批量购入的订单飘字合计范围，账本继续逐项记录。批发在第一笔商品回调前读取实际扣款，用于飘字总额，避免逐项折扣舍入相加与整单扣款不同。订单完成后产生的奖励或费用仍正常提示；失败或取消的订单不会遗留合计状态。收入方向重新验证批量出售，保持一批一条总收入提示。

## 1.1.5 修复

批量出售会逐件触发收入，旧版同时生成多条提示导致重叠。现在同一批出售合并为一条总额飘字，其他连续收支排队播放；同价物品收入正常累加，账本依然逐项记录。飘字样式、位置和动画参数保持原值。

本版扩充：日账拆分物品／明细列并增加剩余信用点；记录成交前的完整价格修正及物品规格；取消打开日历自动跳到账本。旧流水没有保存的余额、规格或事件来源不追溯补造，缺失余额显示“未记录”；折叠行余额取该组合最后一笔完成后的数值，批发一次付款的各项共享结算余额。

回归验证（链接实际生产源码，游戏字体和画面仍需实测）：

- `dotnet run --project tools/CreditToastRegression/CreditToastRegression.csproj -c Release`：29 项飘字金额合计、队列和生命周期检查，包含千件商品批量出售、购买和批发。
- `dotnet run --project tools/LedgerRegression/LedgerRegression.csproj -c Release`：46 项来源、批量保存、异常恢复、鉴定对象、扣款时序、价格来源、规格、折叠、余额和存读档检查。
- `dotnet run --project tools/LedgerRegression/LedgerRegression.csproj -c Release -- --benchmark`：批量写盘性能场景。
