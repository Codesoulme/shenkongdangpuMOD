# 自由聊天响应时间诊断

日期：2026-10-12

## 结论

当前自由聊天的主要等待发生在已配置的第三方兼容服务及其上游，不在游戏界面或本地上下文构建。最小请求完全绕过 Mod，只要求模型回复 `OK`，仍出现 11.7 至 51.2 秒的成功耗时，并有一次在 61.2 秒后返回 HTTP 502。

Mod 原有请求会进一步放大等待：普通闲聊也携带完整交易说明并要求返回所有空提案；自动协议在临时上游故障或超时后还可能换协议再等一次；只凭世界话题关键词也可能触发串行 GM 复核。

## 实测数据

测试使用当前本机配置中的地址和模型，不记录或输出密钥。

| 场景 | 协议 | 请求量 | 结果 |
|---|---|---:|---:|
| 最小 `OK` | Responses | 94 B | 11.7 s、47.1 s；另一次 61.2 s 后 502 |
| 最小 `OK` | Chat Completions | 105 B | 14.1 s、25.3 s、51.2 s |
| 优化前代表人物上下文 | Responses | 上下文 6,776 字符 | 60.0 s 超时 |
| 优化前代表人物上下文 | Chat Completions | 上下文 6,776 字符 | 60.0 s 超时 |
| 优化后普通闲聊 | Responses | 6,871 B | 27.8 s 成功 |
| 优化后普通闲聊 | Chat Completions | 6,792 B | 25.0 s 成功 |
| 优化后混合交易 | Chat Completions | 12,320 B | 24.1 s 成功；服务波动时约 0.8 s 返回 502 |
| GM 单独复核 | Chat Completions | 15,541 B | 21.1 s 成功 |

这些数据不能作为服务等级承诺，只用于区分本地成本和上游等待。当前服务即使处理 94 B 的最小请求也常需十几至几十秒，因此更换低延迟直连地址才是根治方式。

## 已完成的优化

- 普通闲聊使用精简人物说明，移除无关人物目录、找货目录、完整物品清单和整套空行为对象。
- 普通闲聊输出预算从配置的 500 限制到 220；明确行为轮次限制到 360；GM 保持 280。
- 明确行为轮次按玩家意图只加载价格、找货、回访、随身物品、交换、补付、第三方联系、世界承诺或离场中的相关规则。
- 明确行为上下文按需携带交易、找货、人物目录和世界事件，保留物品归属及混合交易所需数据。
- HTTP 502、超时、限流和无效模型回复不再被当作协议不兼容而立即换协议重复等待。
- 只有人物对世界变化作出肯定承诺时才进入 GM；询问信息或明确拒绝保持单请求。
- `debug_log=true` 时写入 `[PERF-GMCHAT]` 分段数据，包括上下文构建、请求字节数、响应头、完整读取、人物步骤、GM 步骤和界面呈现耗时，不记录密钥或完整对话。

## 延迟预期

健康且无排队的直连接口下，按当前负载估计：普通闲聊约 2 至 6 秒；价格、交换或找货等完整结构化轮次约 4 至 12 秒；需要串行 GM 复核时约 8 至 20 秒。持续超过 20 秒通常意味着线路、服务排队或模型供应侧负载异常。

## 测量工具

```powershell
dotnet run --project .\tools\ModelLatencyHarness\ModelLatencyHarness.csproj -c Release -- <Config.json> chat_completions casual
dotnet run --project .\tools\ModelLatencyHarness\ModelLatencyHarness.csproj -c Release -- <Config.json> chat_completions full
dotnet run --project .\tools\ModelLatencyHarness\ModelLatencyHarness.csproj -c Release -- <Config.json> chat_completions gm
```

工具只输出地址无关的测量数据和模型结构化结果，不输出 API 密钥。
