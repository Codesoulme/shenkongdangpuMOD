# 《自由聊天&议价&交换》工坊更新

现有创意工坊条目：[3829066775](https://steamcommunity.com/sharedfiles/filedetails/?id=3829066775)。订阅的“深空当铺 · 创意工坊上传工具”位于 `E:\SteamLibrary\steamapps\workshop\content\4348910\3831507852`。

其他 Codex 会话可直接调用个人 Skill：`$probably-stolen-workshop-upload`。

在本 Mod 目录运行 PowerShell 7：

```powershell
pwsh -NoProfile -File .\scripts\upload-workshop.ps1
```

上面只构建、打包并调用工具的检查模式。核对版本、条目 ID、标题和说明后，再运行：

```powershell
pwsh -NoProfile -File .\scripts\upload-workshop.ps1 -Publish -ChangeNote '本次更新内容'
```

脚本只把 `FreeChatBargainBarter.dll`、`manifest.xml` 和 `preview.png` 放入工坊内容目录。上传说明来自 `Content\WorkshopDescription.txt`，中文与英文之间使用 Steam 的 `[hr][/hr]` 分割线。上传工具在 `.workshop-submission\FreeChatUploader` 中单独运行，不修改订阅目录现有的 `upload.ini`；那个配置当前属于其他 Mod。

订阅目录缺少 `WorkshopUploader.exe` 时，脚本可复用仓库根目录 `.workshop-submission\FreeChatUploader` 中上次保存的工具文件，也可通过 `-UploaderDirectory` 指定完整工具目录。发布参数固定为字符串数组，确保 `--yes` 整体传入，避免被拆成字符而误进入检查模式。

每次更新先检查 `manifest.xml`、项目版本和 `src\AssemblyInfo.cs` 一致，更新说明应准确描述本次变更。工具输出“上传内容 100%”和 `publishedFileId = 3829066775` 才算上传完成；出现法律条款、AccessDenied 或其他错误时按实际结果报告，不把检查模式当成已发布。
