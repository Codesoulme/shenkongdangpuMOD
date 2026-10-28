param(
    [string]$OutputDir = "$env:USERPROFILE\Desktop\FreeChatBargainBarter-Workshop",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$buildDir = Join-Path $projectRoot "bin\$Configuration\netstandard2.1"
$required = @("FreeChatBargainBarter.dll", "System.Text.Json.dll")
foreach ($name in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $buildDir $name))) {
        throw "缺少构建产物：$name。请先构建 Release 版本。"
    }
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$outputPath = (Resolve-Path -LiteralPath $OutputDir).Path
$assemblies = Get-ChildItem -LiteralPath $buildDir -Filter "*.dll" -File
$expected = @($assemblies.Name) + @("manifest.xml", "preview.png")
$unexpected = Get-ChildItem -LiteralPath $outputPath -File |
    Where-Object { $_.Name -notin $expected }
if ($unexpected) {
    throw "上传目录含有非发布文件，请先检查：$($unexpected.Name -join ', ')"
}

$assemblies | Copy-Item -Destination $outputPath -Force
Copy-Item -LiteralPath (Join-Path $projectRoot "manifest.xml") -Destination $outputPath -Force
Copy-Item -LiteralPath (Join-Path $projectRoot "Content\preview.png") -Destination $outputPath -Force
Write-Host "工坊上传目录已准备：$outputPath"
