param(
    [string]$Source = "$PSScriptRoot\..\Content\preview-cover.png",
    [string]$Output = "$PSScriptRoot\..\Content\preview.png"
)

$ErrorActionPreference = "Stop"
$sourcePath = (Resolve-Path -LiteralPath $Source).Path
$outputPath = [System.IO.Path]::GetFullPath($Output)
if (-not [string]::Equals($sourcePath, $outputPath, [System.StringComparison]::OrdinalIgnoreCase)) {
    $outputDirectory = Split-Path -Parent $outputPath
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    Copy-Item -LiteralPath $sourcePath -Destination $outputPath -Force
}
Write-Output $outputPath
