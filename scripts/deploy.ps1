param(
    [string]$GameDir = "E:\SteamLibrary\steamapps\common\Probably Stolen",
    [string]$DataDir = "$env:USERPROFILE\AppData\LocalLow\Questing Goose Studio\Probably Stolen\AIWorldPawnshop",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$buildDir = Join-Path $projectRoot "bin\$Configuration\netstandard2.1"
$dll = Join-Path $buildDir "FreeChatBargainBarter.dll"
$modsDir = Join-Path $GameDir "Mods\FreeChatBargainBarter"
$dataDir = $DataDir

if (-not (Test-Path $dll)) {
    throw "找不到构建产物：$dll"
}

& (Join-Path $PSScriptRoot "validate-release.ps1") -BuildDir $buildDir

New-Item -ItemType Directory -Force -Path $modsDir, $dataDir, (Join-Path $dataDir "Logs") | Out-Null
Get-ChildItem -LiteralPath $buildDir -Filter "*.dll" -File |
    Copy-Item -Destination $modsDir -Force
Copy-Item -LiteralPath (Join-Path $projectRoot "manifest.xml") -Destination (Join-Path $modsDir "manifest.xml") -Force
if (Test-Path (Join-Path $projectRoot "Content\preview.png")) {
    Copy-Item -LiteralPath (Join-Path $projectRoot "Content\preview.png") -Destination (Join-Path $modsDir "preview.png") -Force
}
$configTarget = Join-Path $dataDir "Config.json"
$contentTarget = Join-Path $dataDir "LocalContent.json"
if (-not (Test-Path $configTarget)) {
    Copy-Item -LiteralPath (Join-Path $projectRoot "Content\Config.json") -Destination $configTarget
}
if (-not (Test-Path $contentTarget)) {
    Copy-Item -LiteralPath (Join-Path $projectRoot "Content\LocalContent.json") -Destination $contentTarget
}

Write-Host "已部署到 $modsDir"
