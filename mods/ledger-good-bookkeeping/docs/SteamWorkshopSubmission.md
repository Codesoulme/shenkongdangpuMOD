# Steam 创意工坊提交流程

本文记录《信用点收支显示&账本：好好记账》的实际发布流程，后续更新应复用现有工坊条目。

## 当前条目

- 游戏 AppID：`4348910`
- Mod 名称：`信用点收支显示&账本：好好记账`
- 当前本地及线上已发布版本：`1.1.8`
- 工坊条目 ID：`3832110492`
- 条目链接：https://steamcommunity.com/sharedfiles/filedetails/?id=3832110492
- 首次公开提交：2026-11-02
- 最近更新：以文末成功提交记录为准
- 封面：图1标题图（512×512）
- 附加预览：图1标题图和图2账本界面图

## 上传工具

上传工具由用户通过 Steam 订阅，位于 Steam 库目录下：

`steamapps\workshop\content\4348910\<上传工具条目ID>`

工具目录会因用户的 Steam 库位置和上传工具条目 ID 而异。找不到时，在 `steamapps\workshop\content\4348910` 下搜索 `启动上传.bat`，并先阅读同目录 `README.txt`。

## 发布文件

建议把发布文件暂存于本模组目录下的 `.workshop-submission\DeepSpacePawnshopLedger`。该目录是本机生成内容，已由 `.gitignore` 排除，不应提交到版本库。

目录内放清单、Release DLL 和**主封面**。上传工具的 `preview=` 只设置工坊主封面，需为 512×512 PNG/JPG；不要把这个字段当作截图预览组。

账本版本不包含联网、聊天或模型代码，只声明 `files` 用于持久化流水。

工坊更新器位于 `tools\WorkshopPreviewUpdater`，用 `GameDir` 指向当前游戏安装目录即可构建。它提供 `--verify <publishedFileId>` 查询附加预览，也提供 `--update-item` 更新现有条目的标题、说明、内容目录、主封面和可见性。更新模式不新建条目，也不触碰已有附加预览。

Steam 描述正文单独保存在上传器目录 `description.txt`，采用中英双语和 Steam BBCode。

## 首次提交步骤

1. 构建 Release，并运行 `scripts\validate-release.ps1`，确认清单版本、程序集版本、游戏版本和程序集列表一致。
2. 将最新 `manifest.xml`、`bin\Release\netstandard2.1\DeepSpacePawnshopLedger.dll` 和 512×512 主封面 `preview.png` 复制到暂存目录。
3. 更新工具目录的 `description.txt`，确认中英文功能介绍与清单相符。
4. 配置工具目录的 `upload.ini`：AppID `4348910`、标题、说明文件、内容目录、预览路径、可见性。首次创建时 `publishedfileid=0`。
5. 确认 Steam 客户端正在运行且用户已登录；运行 `检查配置.bat`。此模式只检查，不会上传。
6. 在工具目录运行 `启动上传.bat`，或执行 `WorkshopUploader.exe --yes`。
7. 等待工具显示 `[5/5] 完成！`，核对返回链接和条目 ID。
8. 如果上传器缺少 `WorkshopUploader.exe`，在工具目录启动 Steam API 更新模式：先构建 `WorkshopPreviewUpdater`，再运行 `--update-item <publishedFileId> <content-folder> <cover.png> <description.txt> <changeNote> <visibility>`。例如 visibility `0` 为公开。运行时工作目录应包含 `steam_appid.txt` 和 `steam_api64.dll`。
9. 运行 `WorkshopPreviewUpdater --verify <publishedFileId>`，确认附加预览数量和文件名，再打开条目页面核对标题、说明、封面及公开状态。

## 后续更新

- **不要把 `publishedfileid` 改回 `0`**；保留 `upload.ini` 中的 `3832110492`，这样会更新现有条目，不会另建重复条目。
- 更新 Mod 时同步递增工程、程序集和清单版本，更新暂存 DLL、manifest、预览图和更新说明，再运行检查与上传流程。
- 1.1.0 移除了旧聊天模组运行时、模型联网、世界事件和资源扫描；首次启动时会从旧世界文件迁移流水和存档快照。
- 1.1.1 增大信用点收支飘字，并让所有已记录的收支统一显示提示。
- 1.1.2 恢复为仅声明 `files` 权限，移除不必要的动态代码和外部反射权限。
- 1.1.3 正式发布：飘字字号为原生日历字号的 1.5 倍，采用加粗字形和深色描边；显示阶段不透明，末尾淡出。固定画布起点 125、终点 275，动画时长 2.4 秒。
- 历史本地测试曾使用 1.1.4–1.1.8；当时正式发布版本为 1.1.4。
- 1.1.4 尝试按同帧同金额去重，但未覆盖多件商品的真实收入同时显示，同价物品还可能丢失提示金额。
- 1.1.5 在原生 `NegociationUIManager.SellMultiple` 外包裹批次，将同批出售合并为总收入提示，账本仍逐项记录；取消按金额去重，其他提示依次播放，读档或关闭模组时清空待播提示。16 项源码回归测试通过，实际游戏画面待玩家复测。
- 上传返回成功后再次核对条目链接及 `upload.ini` 中的 ID；附加图更新后再用预览检查模式确认数量。
- 如果工具提示需要同意工坊条款，先按提示完成 Steam 页面上的同意步骤再重试。遇到 `AccessDenied` 时停止，不尝试绕过权限限制。

## 历史成功提交结果（1.1.4）

- 2026-11-13 02:31:56（北京时间）工坊更新成功，条目 ID：`3832110492`，版本：`1.1.4`
- 更新说明：修复同一笔信用点变动触发重复飘字的问题，保持收支提示样式和位置不变。
- 工坊链接：https://steamcommunity.com/sharedfiles/filedetails/?id=3832110492
- 可见性配置：公开
- 主封面：512×512 PNG；Steam 返回的内容包大小为 342,983 字节
- 附加预览：图1标题图和图2账本界面图；Steamworks 更新后查询仍为 2 张
- 线上双语说明已核对，与 `Content/WorkshopDescription.txt` 完全一致。
- 本次使用项目独立暂存目录 `LedgerUploader` 中保留的 `WorkshopUploader.exe --yes` 上传成功，订阅目录缺少该可执行文件。
- 失败排查：若返回 `k_EResultFail`，先检查 Steam 安装目录下 `logs/workshop_log.txt`。本次实际原因是 `Timeout uploading manifest`；核对线上更新时间确认未生效后，间隔重试成功。更换上传器不能解决同一网络超时，不应连续盲目提交。
- 本地安装目录已同步部署 1.1.4，仅声明 `files` 权限。
- 开发者模式属于游戏原生 ModLoader，仅用于本地模组开发时免重复审核，不属于本模组功能；创意工坊版本不会显示此开关。

## 2026-11-15 本地测试阶段记录（随后已发布）

- 此阶段用户要求仅部署本地测试，当时未运行上传器，工坊仍为 1.1.4；随后用户明确要求更新，发布结果见下节。
- 本地版本 1.1.5 同时包含批量出售飘字合计修复，以及本轮日账扩充。
- 原生日历默认页面恢复；账本仅通过标签进入。七列表格分别显示收支、顾客、交易类型、物品、明细、收支金额、剩余信用点；每页四条，使用原生悬停提示查看长文字。
- 成交前读取完整物品名称、容量及实际生效的价格特征，包括事件、市场／顾客、状态和议价；批发额外记录实际批发折扣。普通购买在 BuyItem 结束后记录真实扣款余额，避免 OnItemBought 在扣款前触发以及事件特征被清除的时序问题。
- 同名但成交单价、规格或价格修正不同的商品分行。相邻一致的商品允许折叠，余额保留该组最后一次完成后的数值；批发一次支付的各项共享结算余额。旧记录缺失的余额显示“未记录”，不反推。
- 液体采用原版微升数据换算为 ml（保留小数），显示当前量／总容量；能源显示当前量／总容量（kJ），其余多单位物品保留数量。
- 25 项账本源码回归、16 项飘字源码回归及 Release 构建通过；安装清单校验通过，仅声明 files。实际游戏画面、悬停交互和扫描结果待玩家验证。
- 已部署至 `E:\SteamLibrary\steamapps\common\Probably Stolen\Mods\DeepSpacePawnshopLedger`；安装 DLL 与 Release 一致，SHA256：`ECEF9A0EC2DBFC9A4CCE7B561BA6109DF8BE6D9A5BB14B5F36E199646FF7DC3A`。
- 替换前的本地 DLL、清单和封面保存在 `.workshop-submission/LocalBackup-20261115-214702`；未改动账本数据。

实现依据：官方 MOD-06/10 不提供原生日历嵌入或逐笔成交价格／结算余额钩子，沿用最小 Harmony 介入；价格、容量与显示名称调用游戏公开成员，不引入额外反射或权限。新增 `LedgerTradePatches` 集中处理普通购买及批发回调时序；今后游戏更新应复核 BuyItem、OnItemBought、SellItem 的顺序。

## 历史成功提交结果（1.1.5）

- Steam 确认更新时间：2026-11-15 21:52:04（北京时间），条目 `3832110492`，公开。
- 更新内容：增加成交后剩余信用点，拆分物品和明细列；扩充实际价格修正来源和容量规格；同名不同单价分行；恢复原生日历默认页；修复批量出售收入飘字重叠。
- Release 构建及清单校验通过；发布包 DLL 与本地安装一致，SHA256 为 `ECEF9A0EC2DBFC9A4CCE7B561BA6109DF8BE6D9A5BB14B5F36E199646FF7DC3A`。
- 独立暂存目录 `LedgerUploader` 中运行检查模式后，以 `WorkshopUploader.exe --yes` 更新已有条目；返回 `[5/5] 完成！` 和 `publishedFileId = 3832110492`。
- Steam 公共接口返回成功、更新时间已变化、内容包 351,094 字节；线上中英说明与 `Content/WorkshopDescription.txt` 一致。
- `WorkshopPreviewUpdater --verify 3832110492` 确认保留两张附加预览：`preview-01-logo.png`、`preview-02-ledger.jpg`。
- 上传日志：`.workshop-submission/LedgerUploader/upload-1.1.5-result.txt`。本次未再次覆盖游戏安装文件。

## 历史成功提交结果（1.1.6）

- 用户要求修复后直接部署和更新工坊；Steam 确认更新时间为 2026-11-15 22:20:26（北京时间），条目 `3832110492`，公开。
- 原因：1.1.5 仅对 SellMultiple 设置飘字合计范围，OnWholesaleClick 与 BuyMultipleItem 缺失。批发先扣整单金额，再逐项回调，逐件折扣舍入之和还可能与整单扣款不同。
- 修复：补齐批发及批量购入的合计范围；在批发第一笔物品回调前保存真实总扣款，逐项流水保存后仅收集商品扣款提示，显示一次实际扣款总额。批量出售仍显示合计收入；订单完成的其他奖励或费用照常处理。未改动账本页面、逐项账本金额和飘字样式。
- 回归：修改前新用例复现 `[-100,-250] != [-350]`；修改后 26 项飘字及 26 项账本测试通过，覆盖实际批发扣款、同价商品、舍入差额、额外收入／支出、失败释放、不同订单分开及批量出售。Release 构建零警告、零错误，清单校验通过，仍仅 files 权限；游戏画面未在本轮实测。
- 本地部署完成；构建、安装及上传包 DLL 哈希一致：`0C121B91BEB1108C494DBC7DBFAE482F2A6B5EE8AA8527F668E5725B2AC9C2D4`。替换前本地文件保存在 `.workshop-submission/LocalBackup-1.1.5-20261115-221950`，未改动玩家账本数据。
- 上传器检查通过后以 `WorkshopUploader.exe --yes` 更新，返回 `[5/5] 完成！` 与 `publishedFileId = 3832110492`；日志为 `.workshop-submission/LedgerUploader/upload-1.1.6-result.txt`。
- 线上接口确认更新成功，内容包为 352,118 字节，说明与 `Content/WorkshopDescription.txt` 一致；附加预览查询仍为原有两张。

## 历史成功提交结果（1.1.7）

- 沿用用户“修好直接部署和更新工坊”的要求完成；Steam 确认更新时间：2026-11-15 23:02:15（北京时间），条目 `3832110492`，公开。
- 原因：旧鉴定服务费补丁仅保存信用点差额，未关联鉴定对象。原版 GiveAppraisalFee 还会生成鉴定单，因此不能事后从桌面选择猜测物品。
- 修复：在 GetAcceptEstimateDialog(GameItem, int) 保存传入对象的名称、标识和规格，在 GiveAppraisalFee 实际入账时关联快照；作用域通过 finalizer 恢复，避免连续或嵌套鉴定串名。明细标注“鉴定服务费”，服务记录不折叠为商品出售；不把物品市场修正误标为服务费折扣。缺少物品上下文的外部直接服务费调用仍记录收入，但不猜测对象；历史缺失信息不补造。
- 验证：新增用例在修复前报 `appraised item missing`；修复后 33 项账本、26 项飘字回归通过。覆盖单据替换、对象变化、存读档、连续鉴定、嵌套／异常恢复、无付款及无对象的直接调用。Release 构建零警告、零错误，清单校验通过，权限仍仅 files；未在游戏内实测画面。
- 实现沿用官方 MOD-06/20 的最小补丁路径，新增一个接受估价上下文补丁，不干预原版鉴定估值、结算或物品销毁。
- 本地部署、构建及上传包 DLL 一致，SHA256：`66516C2EC5D8672954B1822D51536719DCD67FD2CC67EB951EBFF2EAD0F5916D`；替换前备份 `.workshop-submission/LocalBackup-1.1.6-20261115-230127`，未改玩家流水。
- 检查模式通过后执行 `WorkshopUploader.exe --yes`，返回 `[5/5] 完成！`、`publishedFileId = 3832110492`；日志为 `.workshop-submission/LedgerUploader/upload-1.1.7-result.txt`。
- 线上接口确认更新时间变化，内容包 353,142 字节，中英说明一致；原有两张附加预览仍保留。

## 历史成功提交结果（1.1.8）

- 沿用用户“修好直接部署和更新工坊”的授权完成；Steam 确认更新时间：2026-11-16 14:07:14（北京时间），条目 `3832110492`，公开。
- 批量交易卡顿的已复现原因是每件物品都同步序列化整本账和所有存档快照并替换磁盘文件。批量出售、购入和批发现在分别设置保存批次，整单结束统一落盘；保留逐项流水，实际存档及关闭模组强制保存，订单异常仍保存已完成部分。每个入口使用独立 Harmony 补丁类，避免多条目标属性合并后只作用于一个方法。
- 来源显示内部函数名的原因是旧筛选只排除根命名空间，误选 Runtime 子命名空间的自身函数。现在排除本程序集，并为 VendingMachineHelper.TransferCashToPlayer 与 StoreRequest.DispenseReward 保存明确上下文。贩卖机显示设备名称、累计营业收入提现说明；委托显示委托人、任务、交付物品和数量。提现仅在实际转入玩家余额时记录，不把设备暂存收入重复计入。历史已丢失的来源只显示缺失说明，不补造数据。
- 回归：46 项账本测试、29 项飘字测试通过，含千件商品批量出售／购买／批发各只产生一条总额提示，异常与嵌套批次、真实余额、零收入、未知物品定义、存读档及来源隔离。独立相同场景（100 件出售、8 份各 100 条流水的存档快照）修复前 2561.0 ms，修复后 46.2 ms；这是单次处理耗时对比，实际游戏帧率和画面未实测。
- Release 构建零警告、零错误，清单校验通过，权限仍仅 files；账本布局和已确认的飘字外观参数保持原值。
- 本地部署、构建与上传包 DLL 一致，SHA256：`AFE71D9919424389CD3754612470AB4533CEBC10C14F70A75A4B95FA163653DB`；原本地 1.1.7 文件备份于 `.workshop-submission/LocalBackup-1.1.7-20261116-140638`，未修改玩家账本数据。
- 独立 `LedgerUploader` 检查模式通过后执行 `WorkshopUploader.exe --yes`，返回 `[5/5] 完成！` 及 `publishedFileId = 3832110492`；日志 `.workshop-submission/LedgerUploader/upload-1.1.8-result.txt`。
- Steam 接口返回成功，更新时间戳 `1794809234`，内容包 355,702 字节，线上中英文说明与源文件一致；`WorkshopPreviewUpdater --verify 3832110492` 确认仍有 `preview-01-logo.png` 和 `preview-02-ledger.jpg` 两张附加预览。

## 最新成功提交结果（1.1.9）

- 用户授权优化后直接推送，工坊更新成功。Steam 公共接口确认更新时间：2026-11-17 14:51:43（北京时间），时间戳 `1794898303`，条目 `3832110492`，公开，内容包 357,750 字节。线上中英文说明与 `Content/WorkshopDescription.txt` 一致。
- 修复：日常交易只追加新增流水；批量订单一次追加，避免同步重写旧存档快照。存档、轮转、读档和停用时原子归档；通过代次标识隔离不同时间线，处理未完成尾部和失败写入重试，保留旧记录迁移及存档回滚。详见 `TransactionPerformance-20261117.md`。
- 56 项账本、29 项飘字回归通过；Release 零警告、零错误。相同 1.45 MB 数据模板，单笔中位数约 56.93 ms 降到 0.42 ms，100 件批量约 60.27 ms 降到 2.40 ms；这是生产记录／存储代码的独立基准，真实 Unity 帧耗时未测量。
- 当前官方扫描器 v4 检测能力仍只有 files，无 HardFlags、无错误；既有存档轮转补丁的 files 待生效发现已由清单覆盖，原始报告在 `.workshop-submission/scan-1.1.9.json`。未改动账本布局、飘字外观及动画参数。
- 本地部署、Release 与上传包 DLL SHA256 一致：`7829CBF6FD1A15DFE1A60238A0128D272B7F24C7CA52402F767442380D1BE101`。部署前旧文件及玩家账本目录备份于 `.workshop-submission/LocalBackup-1.1.8-20261117-145109`；玩家原 `CreditLedger.json` 哈希未变，未提前运行数据迁移。
- 本次上传器从项目独立 `LedgerUploader` 目录复用，先检查再 `-Publish`，返回 `[5/5] 完成！` 及 `publishedFileId = 3832110492`；日志 `.workshop-submission/LedgerUploader/upload-1.1.9-result.txt`。脚本允许来源与暂存上传器为同一路径，避免文件复制到自身。
- `WorkshopPreviewUpdater --verify 3832110492` 确认原有 `preview-01-logo.png`、`preview-02-ledger.jpg` 两张预览仍保留。
- 手动备份需同时包含 `CreditLedger.json` 和 `CreditLedger.transactions.jsonl`，推荐备份整个数据目录。退回旧版前正常停用新版／正常退出，让增量流水归档；旧版不会读取增量文件。
