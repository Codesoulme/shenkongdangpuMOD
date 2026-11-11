# 《账本：好好记账》

适用于 Probably Stolen 正式版 0.46D 的原生 Mod。它只负责记录信用点流水，并将账本加入游戏原生日历。

## 功能

- 打开日历默认显示当天账本，可切换查看昨天
- 显示当日收入、支出与净额
- 逐笔记录收支方向、顾客、交易类型、物品明细和信用点金额
- 标注成交中实际生效的折扣或加价
- 自动折叠同名物品并显示数量，支持分页查看
- 账本跟随当前游戏存档保存和读取
- 读取旧版账本记录并迁移到独立账本文件

账本不包含聊天、模型请求、游戏资源扫描或世界事件功能，不申请联网权限。
文件权限用于保存账本；动态代码和外部反射权限用于原版收支与日历补丁，以及读取交易顾客和议价状态。

## English

# Keep Your Books Straight

Keep every credit movement easy to review. This mod adds a ledger page to the in-game calendar. It opens on today's transactions, and you can switch to yesterday whenever you need to look back.

### Ledger Features

- See daily totals for income, expenses, and net balance
- Review each entry by direction, customer when available, transaction type, item details, and amount
- See discounts or markups that were actually applied to completed trades
- Fold entries for items with the same name and see the total quantity
- Browse paginated records; ledger data is saved and restored with the active game save

Customer trades and in-game credit settlements are recorded from their actual balance changes, helping you keep track of where your money went.

## 安装

将发布包中的 `DeepSpacePawnshopLedger.dll`、`manifest.xml` 和 `preview.png` 放入游戏 `Mods/DeepSpacePawnshopLedger` 目录，在游戏 Mod 页面启用即可。

## 兼容性

- 游戏：Probably Stolen 0.46D
- ModLoader：原生 ModLoader
- 当前版本：1.1.0
