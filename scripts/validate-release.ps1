param(
    [string]$BuildDir = "$PSScriptRoot\..\bin\Release\netstandard2.1",
    [string]$ManifestPath = "$PSScriptRoot\..\manifest.xml",
    [string]$ProjectPath = "$PSScriptRoot\..\AIWorldPawnshop.csproj",
    [string]$GameVersion = "0.46D"
)

$ErrorActionPreference = "Stop"
$manifest = [xml](Get-Content -LiteralPath $ManifestPath -Raw)
$project = [xml](Get-Content -LiteralPath $ProjectPath -Raw)
$declared = @($manifest.ModManifest.Assemblies.Assembly) |
    ForEach-Object { [string]$_ }
$actual = @(Get-ChildItem -LiteralPath $BuildDir -Filter "*.dll" -File |
    ForEach-Object { $_.Name })

if ($declared.Count -eq 0 -or $actual.Count -eq 0) {
    throw "清单或构建目录中没有 DLL。"
}

$unlisted = @($actual | Where-Object { $_ -notin $declared })
$missing = @($declared | Where-Object { $_ -notin $actual })
$duplicates = @($declared | Group-Object | Where-Object Count -gt 1 | ForEach-Object Name)
if ($unlisted.Count -gt 0 -or $missing.Count -gt 0 -or $duplicates.Count -gt 0) {
    throw "程序集清单与构建文件不一致。未列入：$($unlisted -join ', ')；缺少：$($missing -join ', ')；重复：$($duplicates -join ', ')"
}

$versions = @($manifest.ModManifest.GameVersions.Version) |
    ForEach-Object { [string]$_ }
if ($GameVersion -notin $versions) {
    throw "GameVersions 必须包含游戏版本 $GameVersion；当前为 $($versions -join ', ')。"
}

$modVersion = [string]$manifest.ModManifest.ModVersion
$projectVersion = [string]$project.Project.PropertyGroup.Version
if ($modVersion -ne $projectVersion) {
    throw "ModVersion $modVersion 与工程版本 $projectVersion 不一致。"
}

$mainAssembly = Join-Path $BuildDir "FreeChatBargainBarter.dll"
$assemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($mainAssembly).Version.ToString(3)
if ($modVersion -ne $assemblyVersion) {
    throw "ModVersion $modVersion 与构建 DLL 版本 $assemblyVersion 不一致。"
}

Write-Host "发布清单校验通过：$modVersion / 游戏 $GameVersion / $($actual.Count) 个 DLL"
