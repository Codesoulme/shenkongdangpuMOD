# Probably Stolen Mods

本仓库按模组分别存放源码、清单、资源和说明。每个模组可独立构建、部署和发布；新增模组时，请在 `mods/` 下新建目录并更新本页索引。

## 模组目录

| 模组 | 目录 | Mod ID | 当前版本 | 简介 | Steam 创意工坊 |
|---|---|---|---|---|---|
| 自由聊天&议价&交换 | [`mods/free-chat-bargain-barter/`](mods/free-chat-bargain-barter/) | `FreeChatBargainBarter` | 0.4.5 | 顾客自由交谈、议价与物品交换 | [订阅](https://steamcommunity.com/sharedfiles/filedetails/?id=3829066775) |
| 账本：好好记账 | [`mods/ledger-good-bookkeeping/`](mods/ledger-good-bookkeeping/) | `DeepSpacePawnshopLedger` | 1.1.0 | 在原生日历查看当天与昨天的信用点流水 | [订阅](https://steamcommunity.com/sharedfiles/filedetails/?id=3832110492) |

## 仓库结构

```text
mods/
├── free-chat-bargain-barter/   # 自由聊天、议价与交换
└── ledger-good-bookkeeping/    # 信用点账本
```

每个模组目录均包含自己的 `.csproj`、`manifest.xml`、`src/`、`Content/` 和 `README.md`。提交改动时，请确认修改落在对应模组目录；共享目录仅用于仓库级索引和协作说明。

## 开发与构建

选择对应目录中的 README 和 `.csproj`，按该模组文档独立构建。不要把某个模组的 DLL、清单或资源放进另一个模组目录。

新增模组时：

1. 在 `mods/<mod-id>/` 创建独立目录。
2. 放入该模组自己的工程、清单、源码、资源和 README。
3. 在本页“模组目录”表格中添加名称、目录、Mod ID、版本与简介。
