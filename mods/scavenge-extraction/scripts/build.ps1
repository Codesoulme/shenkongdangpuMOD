param([string]$GameDir = 'E:\SteamLibrary\steamapps\common\Probably Stolen')
$ErrorActionPreference = 'Stop'
$modRoot = Split-Path -Parent $PSScriptRoot
dotnet build (Join-Path $modRoot 'ScavengeExtraction.csproj') -c Release --nologo "-p:GameDir=$GameDir"
if ($LASTEXITCODE -ne 0) { throw 'Mod build failed' }
dotnet run --project (Join-Path $modRoot 'tools\RaidRegression\RaidRegression.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Gameplay regressions failed' }
$release = Join-Path $modRoot 'dist\ScavengeExtraction'
New-Item -ItemType Directory -Path $release -Force | Out-Null
foreach ($name in @('manifest.xml', 'README.md')) {
    Copy-Item -LiteralPath (Join-Path $modRoot $name) -Destination $release -Force
}
Copy-Item -LiteralPath (Join-Path $modRoot 'bin\Release\netstandard2.1\ScavengeExtraction.dll') -Destination $release -Force
Copy-Item -LiteralPath (Join-Path $modRoot 'Content\Textures') -Destination $release -Recurse -Force
$allow = @('ScavengeExtraction.dll', 'manifest.xml', 'README.md', 'Textures/Items/unknown.png', 'Textures/Items/corpse.png', 'Textures/Items/pack.png', 'Textures/Items/part.png')
$actual = @(Get-ChildItem -LiteralPath $release -Recurse -File | ForEach-Object { $_.FullName.Substring($release.Length + 1).Replace('\', '/') })
if (@(Compare-Object $allow $actual).Count -gt 0) { throw 'Unexpected or missing package file; inspect dist.' }
dotnet run --project (Join-Path $modRoot 'tools\OfficialScan\OfficialScan.csproj') -c Release -- (Join-Path $GameDir 'Probably Stolen_Data\Managed') $release (Join-Path $modRoot 'docs\validation.json')
if ($LASTEXITCODE -ne 0) { throw 'Game scanner did not confirm a zero-capability package.' }
Compress-Archive -LiteralPath $release -DestinationPath (Join-Path $modRoot 'dist\ScavengeExtraction-0.1.0.zip') -Force
Write-Output "Ready: $release"
