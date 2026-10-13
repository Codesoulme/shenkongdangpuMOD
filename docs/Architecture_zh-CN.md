# 当铺世界 Mod 架构图

适用版本：Mod 0.2.2 / Probably Stolen Demo 0.46D。本图描述当前代码的运行路径，不是未来功能规划。

```mermaid
flowchart TD
    GAME["原版游戏：顾客、电话、物品、交易、跨天状态"]
    HOOK["MelonLoader / Harmony 钩子"]
    UI["自由聊天、原版对话气泡、设置与世界页面"]
    CONTEXT["原版上下文：人物、双方物品、原版背景、关系"]
    INDEX["原版资料目录：资源指纹、分帧扫描、已见资料"]
    CACHE["GameKnowledgeCatalog.json：本地缓存"]
    RUNTIME["WorldRuntime：回合、状态修订号与协调"]
    PROFILE["同一份当前模型接口配置"]
    PERSON_CALL["人物模型调用"]
    GM_CALL["GM 模型调用"]
    MODEL["外部模型服务"]
    CHARACTER["人物回复与行为提案"]
    DECISION{"需要 GM 复核？"}
    GM["GM 复核结果：世界修正与特定提案"]
    FALLBACK["接口失败时的本地回复"]
    CHECK["身份、快照时效、提案范围与能力校验"]
    ADAPTER["原版适配器：议价、交换、预算、找货、回访等"]
    WORLD["GM 受限修正：价格、需求、供给"]
    STORE["配置与世界状态 JSON；意图账本 JSONL"]
    DAY["跨天结算：到期恢复、事件与新闻投影"]

    GAME --> HOOK --> UI --> CONTEXT --> RUNTIME
    GAME -- 原版资源与当前可见内容 --> INDEX --> CONTEXT
    INDEX <--> CACHE
    RUNTIME --> PERSON_CALL
    PROFILE --> PERSON_CALL
    PROFILE --> GM_CALL
    PERSON_CALL <--> MODEL
    GM_CALL <--> MODEL
    PERSON_CALL --> CHARACTER --> DECISION
    PERSON_CALL -- 请求失败 --> FALLBACK --> CHECK
    DECISION -- 是，追加调用 --> GM_CALL --> GM --> CHECK
    DECISION -- 否 --> CHECK
    CHECK --> ADAPTER --> GAME
    CHECK --> WORLD --> GAME
    CHECK --> STORE
    HOOK -- 换日与存档 --> DAY --> STORE
    DAY --> GAME
    CHECK -- 人物回复与结果提示 --> UI
```

人物回复与 GM 审核是两种职责，但使用同一份当前接口配置；GM 仅在符合触发条件时串行调用。模型只输出候选回复和提案，不能直接调用原版函数或任意改写字段。实际改变由运行时核对当前人物、交易现场、执行器白名单和快照时效后完成。请求失败或人物身份冲突会转入本地回复；过期回合的文字可以显示，但其副作用会被移除。

主要实现位置：

- `src/AIWorldPawnshopMod.cs`、`src/GamePatches.cs`：加载和原版事件入口。
- `src/UI/ChatUiController.cs`、`src/Runtime/NativeConversationContextBuilder.cs`：聊天交互和当前游戏状态快照。
- `src/Runtime/GameKnowledgeCatalog.cs`：原版资源变更检测、本地缓存、分帧物品扫描与可见资料检索。
- `src/Runtime/WorldRuntime.cs`、`src/Model/OpenAiCompatibleProvider.cs`：人物回复、条件 GM 复核与回合协调。
- `src/GM/GmInterfaceCatalog.cs`、`src/GM/GmModificationEngine.cs`、`src/Runtime/Native*Adapter.cs`：可执行接口目录、受限修改与原版适配器。
- `src/Orchestration/EchoWeaveRuntime.cs`、`src/Orchestration/IntentLedger.cs`、`src/Storage/JsonFileStore.cs`：去重、状态恢复和持久化。旧类名保留用于存档兼容，对外统一称 GM 系统。

桌面分享包只包含 `AIWorldPawnshop.dll` 与使用说明，不包含本开发架构文档。
