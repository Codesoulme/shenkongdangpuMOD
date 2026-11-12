# 0.4.6 正式版更新兼容报告

## 核对版本

- 游戏显示版本：0.46D；Steam Build：26525552；AppID：4348910。
- 本机 `Assembly-CSharp.dll` SHA-256：`725CB999D019DFF1FF31BED353C7F9BB8D81A7605A15DE1F80F5A3A6CF7B1D05`。
- 原生 ModLoader 扫描器版本：4。
- Mod 版本：0.4.6；名称及工坊条目保持不变。

## 调整

1. 私有柜台、顾客、电话与议价界面字段改经原生 `ModReflect.Get` 读取；掉落表刷新改用 `ModReflect.CallStatic`，移除通用反射调用。
2. 文件指纹任务改用 `ModHelper.RunInBackground`。任务在首次逐帧回调启动，避开加载器尚未注册 Mod 的 `OnEnable` 阶段；禁用时使用加载器取消信号，文件哈希每 64 KiB 检查取消。
3. 清单只保留实际需要的 `files` 和 `network`，移除 `dynamic-code` 与 `reflection-external`。发布脚本拒绝重新加入停用中的权限。
4. 正式版 `Managed/Assembly-CSharp.dll`、`Assembly-CSharp-firstpass.dll` 和 `globalgamemanagers` 加入资料指纹。仅代码更新也能触发目录重建，内容相同则继续复用原目录。
5. 扩展 `FormalPatchHarness`，直接调用当前游戏的扫描器，检查未声明权限、拒绝项和待执行的新规则，并核对 8 个私有字段及掉落表刷新方法。

## 控制台验证

在本 Mod 目录运行：

```powershell
dotnet build AIWorldPawnshop.csproj -c Release
dotnet run --project tools/FormalPatchHarness -c Release -- bin/Release/netstandard2.1/FreeChatBargainBarter.dll "E:\SteamLibrary\steamapps\common\Probably Stolen" manifest.xml
dotnet run --project tools/EchoWeaveSmoke -c Release
dotnet run --project tools/BarterDirectionHarness -c Release
dotnet run --project tools/CounterPlacementHarness -c Release
dotnet run --project tools/JsonCompatibilityHarness -c Release
pwsh -NoProfile -File scripts/validate-release.ps1
```

结果：Release 编译无警告、无错误；25 个补丁类对应的 25 个原版方法通过检查；原生扫描只检出 `files, network`，无拒绝项或待执行规则。核心流程、混合交换补款方向、顾客柜台实物识别、配置及模型请求格式回归通过。新增正式版仅代码更新、内容未变及取消检查通过。

这些检查覆盖程序集接口、补丁声明、原生静态扫描和现有可在控制台运行的逻辑；游戏内画面、按钮布局、真实成交和联网回复仍由玩家实机验证。

## 加载与工坊

最新游戏日志中，0.4.5 被登记为禁用状态，工坊副本因本地存在相同 Mod ID 而跳过。更新本地文件后，需要在游戏模组列表确认启用；部署脚本不改玩家的启用选择或信任记录。

新版加载器对来自工坊、使用文件或网络的模组还有开发者验证要求。本地适配和扫描通过不代表已经取得该验证；发布到工坊后若出现相应提示，需要按游戏官方流程申请验证。
