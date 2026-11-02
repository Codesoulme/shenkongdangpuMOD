# Steam 创意工坊提交流程

本文记录《账本：好好记账》的实际发布流程，后续更新应复用现有工坊条目。

## 当前条目

- 游戏 AppID：`4348910`
- Mod 名称：`账本：好好记账`
- 当前 Mod 版本：`1.0.1`
- 工坊条目 ID：`3832110492`
- 条目链接：https://steamcommunity.com/sharedfiles/filedetails/?id=3832110492
- 首次公开提交：2026-11-02
- 封面：图1标题图（512×512）
- 附加预览：图1标题图和图2账本界面图

## 上传工具

上传工具由用户通过 Steam 订阅，位于 Steam 库目录下：

`steamapps\workshop\content\4348910\<上传工具条目ID>`

工具目录会因用户的 Steam 库位置和上传工具条目 ID 而异。找不到时，在 `steamapps\workshop\content\4348910` 下搜索 `启动上传.bat`，并先阅读同目录 `README.txt`。

## 发布文件

建议把发布文件暂存于本模组目录下的 `.workshop-submission\DeepSpacePawnshopLedger`。该目录是本机生成内容，已由 `.gitignore` 排除，不应提交到版本库。

目录内放清单、Release DLL 和**主封面**。上传工具的 `preview=` 只设置工坊主封面，需为 512×512 PNG/JPG；不要把这个字段当作截图预览组。

附加预览通过 Steamworks UGC 的 `AddItemPreviewFile` 上传。可复用工具在 `tools\WorkshopPreviewUpdater`：传入 512×512 主封面和附加预览图片。工具提供 `--verify <publishedFileId>`，会查询工坊并列出附加预览数量和 URL。

Steam 描述正文单独保存在上传器目录 `description.txt`，采用中英双语和 Steam BBCode。

## 首次提交步骤

1. 构建 Release，并运行 `scripts\validate-release.ps1`，确认清单版本、程序集版本、游戏版本和程序集列表一致。
2. 将最新 `manifest.xml`、`bin\Release\netstandard2.1\DeepSpacePawnshopLedger.dll` 和 512×512 主封面 `preview.png` 复制到暂存目录。
3. 更新工具目录的 `description.txt`，确认中英文功能介绍与清单相符。
4. 配置工具目录的 `upload.ini`：AppID `4348910`、标题、说明文件、内容目录、预览路径、可见性。首次创建时 `publishedfileid=0`。
5. 确认 Steam 客户端正在运行且用户已登录；运行 `检查配置.bat`。此模式只检查，不会上传。
6. 在工具目录运行 `启动上传.bat`，或执行 `WorkshopUploader.exe --yes`。
7. 等待工具显示 `[5/5] 完成！`，记录 `publishedFileId` 和返回链接，并确认工具已将 ID 写入 `upload.ini`。
8. 若需要附加预览图，运行 `WorkshopPreviewUpdater`，将封面和附加图片路径传给工具；它会设置封面并调用 `AddItemPreviewFile` 添加图片。
9. 运行 `WorkshopPreviewUpdater --verify <publishedFileId>`，确认附加预览数量和文件名，再打开返回链接核对标题、说明、封面及公开状态。

## 后续更新

- **不要把 `publishedfileid` 改回 `0`**；保留 `upload.ini` 中的 `3832110492`，这样会更新现有条目，不会另建重复条目。
- 更新 Mod 时同步递增工程、程序集和清单版本，更新暂存 DLL、manifest、预览图和更新说明，再运行检查与上传流程。
- 上传返回成功后再次核对条目链接及 `upload.ini` 中的 ID；附加图更新后再用预览检查模式确认数量。
- 如果工具提示需要同意工坊条款，先按提示完成 Steam 页面上的同意步骤再重试。遇到 `AccessDenied` 时停止，不尝试绕过权限限制。

## 本次提交结果

- 工具上传完成，返回 ID：`3832110492`
- 上传返回链接：https://steamcommunity.com/sharedfiles/filedetails/?id=3832110492
- 可见性配置：公开
- 主封面：图1标题图的 512×512 版本
- 附加预览：图1标题图和图2账本界面图；Steamworks 查询确认共 2 张
- 更新说明：修正封面图并新增两张工坊预览图
- 内容、标题与中英文描述由上传器读取暂存目录和配置文件。
