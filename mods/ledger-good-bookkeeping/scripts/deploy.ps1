param(
    [string]$GameDir = "E:\SteamLibrary\steamapps\common\Probably Stolen",
    [string]$DataDir = "$env:USERPROFILE\AppData\LocalLow\Questing Goose Studio\Probably Stolen\DeepSpacePawnshopLedger",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$buildDir = Join-Path $projectRoot "bin\$Configuration\netstandard2.1"
$dll = Join-Path $buildDir "DeepSpacePawnshopLedger.dll"
$modsDir = Join-Path $GameDir "Mods\DeepSpacePawnshopLedger"
$dataDir = $DataDir

if (-not (Test-Path $dll)) {
    throw "找不到构建产物：$dll"
}

& (Join-Path $PSScriptRoot "validate-release.ps1") -BuildDir $buildDir

New-Item -ItemType Directory -Force -Path $modsDir, $dataDir | Out-Null
$obsolete = @(
    "Microsoft.Bcl.AsyncInterfaces.dll", "System.Buffers.dll", "System.Memory.dll",
    "System.Numerics.Vectors.dll", "System.Runtime.CompilerServices.Unsafe.dll",
    "System.Text.Encodings.Web.dll", "System.Text.Json.dll",
    "System.Threading.Tasks.Extensions.dll"
)
foreach ($name in $obsolete) {
    $oldPath = Join-Path $modsDir $name
    if (Test-Path -LiteralPath $oldPath) {
        Remove-Item -LiteralPath $oldPath -Force
    }
}
Get-ChildItem -LiteralPath $buildDir -Filter "*.dll" -File |
    Copy-Item -Destination $modsDir -Force
Copy-Item -LiteralPath (Join-Path $projectRoot "manifest.xml") -Destination (Join-Path $modsDir "manifest.xml") -Force
if (Test-Path (Join-Path $projectRoot "Content\preview.png")) {
    Copy-Item -LiteralPath (Join-Path $projectRoot "Content\preview.png") -Destination (Join-Path $modsDir "preview.png") -Force
}
Write-Host "已部署到 $modsDir"
