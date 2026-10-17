# 《AI 游戏世界模拟接入指导书 v1.1》当铺 Mod 接入方案

## 范围与原则

本方案依据 `C:\Users\14262\AIworld\docs\AI游戏世界模拟接入指导书_v1.1.docx`，只落实适用于 Probably Stolen Demo 0.46D 的机制。原版人物、物品、预算、交易和来客队列是事实来源；Mod 仅保存补充状态，并通过已经验证的原版适配器产生副作用。模型给出的文字或数值不能充当执行回执。指导书中的其他题材案例、研究阈值和未接通的原版接口不自动成为本 Mod 能力。

## 调整矩阵

| 指导书关注点 | 本项目落点 | 本轮调整与验收 |
|---|---|---|
| 原版状态权威、能力边界 | `NativeConversationContextBuilder`、`CapabilityRegistry`、原版适配器 | 能力目录区分 `implemented` 与 `catalog_only`；快照只列已接通能力；GM 修正必须提供目标、路径和证据，且目标与路径在目录中匹配。测试缺失路径和跨目标借用路径。 |
| 结构化提案、前置条件、回执 | 人物提案、GM 复核、`GmModificationEngine`、原版价格投影 | 保留既有本地校验和原版读回。世界经济回执区分当前来客已验证、尚无来客待投影、当前来客写入失败；失败时不把它加入可供人物引用的已完成事实。 |
| 修订号与幂等 | `WorldState.StateRevision`、`EchoWeaveRuntime`、`IntentLedger` | 既有过期回复拦截保持不变；同一 `world_id + conversation_id + turn_id + capability + arguments` 跨进程稳定去重。无对话编号时仍以当前会话为范围。账本写入失败不开放回合执行；仅登记未开始的回合可安全恢复。 |
| 故障恢复 | JSON 世界存储、追加账本 | 主文件损坏时读取备份；恢复后下一次保存不会用损坏主文件覆盖有效备份。世界保存返回成功/失败，回合落盘失败记为冲突而非完成；未闭合原版动作不自动重放。 |
| 可见性与长期记忆 | `FactRecord`、人物/阵营记忆、上下文读取 | 私有事实按人物稳定键读取，不再用名称子串猜归属。记忆保留人物、回合和原话来源；默认人物私有，阵营共享需要本轮原话证据及明确范围。读取时按重要度与时间衰减排序。旧版无归属的私有事实、无证据的阵营摘要默认不外泄。 |
| 人物与跨天变化 | 已有关系值、阵营声望、回访/第三方任务、每日事件 | 保留已有动态关系、期限、到期处理和原版队列回执，不凭通用指导书新增原版没有的角色数值或日程。三种说法的同一类请求进入离线回放。 |
| 可复现验证 | `EchoWeaveSmoke`、`BargainScopeHarness`、`PatchContractHarness` | 新增跨会话去重、账本写失败、备份恢复、事实隔离、记忆来源/衰减和目标路径绕过测试；既有议价、交换、电话、找货、结束对话及 Harmony 签名回归继续运行。 |

## 执行顺序

1. 从原版采集当前人物、物品归属、预算和交易阶段，生成带修订号的回合快照。
2. 人物模型给出话语和候选意图；必要时 GM 复核目标与可执行路径。本地规则再次校验身份、权限、参数、时机和原版对象。
3. 先持久化回合意图键，再交给已接通适配器按各自规则执行；无法登记回合时不执行。适配器目前仍在执行后写回结果，并未统一成每个动作都先登记的事务。原版副作用必须以读回结果判定，延期与失败不写成已完成。
4. 写入扩展状态和有限可见事实；后续人物只读取公开事实或以自身稳定键归属的私有内容。跨天过期和原版队列继续按已有确定性流程处理。

## 验收边界

离线检查能够验证确定性规则、序列化与恢复、模型协议、适配器调用契约和同义话术路径；不能证明原版画面、真实成交、顾客离店、新闻版面或存档长期兼容性。发布后的游戏内验证仍需确认：当前顾客价格和界面同步、重启后首次加载、连续跨天、交易和第三方来客队列。逐适配器的统一预登记与补偿仍是后续架构工作，不在本轮已完成项内。未验证的警报器、偷窃与保险写入点仍为目录候选，不能声称它们已由本轮接通。

## 本轮验收命令

```powershell
dotnet build AIWorldPawnshop.csproj -c Release
dotnet run --project tools/EchoWeaveSmoke/EchoWeaveSmoke.csproj -c Release
dotnet run --project tools/BargainScopeHarness/BargainScopeHarness.csproj -c Release
dotnet run --project tools/PatchContractHarness/PatchContractHarness.csproj -c Release -- "C:\Users\14262\AIWorldPawnshopMod\bin\Release\net6.0\AIWorldPawnshop.dll" "E:\SteamLibrary\steamapps\common\Probably Stolen Demo"
git diff --check
```
