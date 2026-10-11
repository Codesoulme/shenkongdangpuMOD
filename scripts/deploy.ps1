param(
    [string]$GameDir = "E:\SteamLibrary\steamapps\common\Probably Stolen Demo",
    [string]$DataDir = "$env:USERPROFILE\AppData\LocalLow\Questing Goose Studio\Probably Stolen\AIWorldPawnshop",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$dll = Join-Path $projectRoot "bin\$Configuration\net6.0\AIWorldPawnshop.dll"
$modsDir = Join-Path $GameDir "Mods"
$dataDir = $DataDir

if (-not (Test-Path $dll)) {
    throw "找不到构建产物：$dll"
}

New-Item -ItemType Directory -Force -Path $modsDir, $dataDir, (Join-Path $dataDir "Logs") | Out-Null
Copy-Item -LiteralPath $dll -Destination (Join-Path $modsDir "AIWorldPawnshop.dll") -Force
$configTarget = Join-Path $dataDir "Config.json"
$contentTarget = Join-Path $dataDir "LocalContent.json"
if (-not (Test-Path $configTarget)) {
    Copy-Item -LiteralPath (Join-Path $projectRoot "Content\Config.json") -Destination $configTarget
}
if (-not (Test-Path $contentTarget)) {
    Copy-Item -LiteralPath (Join-Path $projectRoot "Content\LocalContent.json") -Destination $contentTarget
}

Write-Host "已部署到 $modsDir"
