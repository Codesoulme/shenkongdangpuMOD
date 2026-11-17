param([string]$GameDir = 'E:\SteamLibrary\steamapps\common\Probably Stolen')
$ErrorActionPreference = 'Stop'
$modRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'build.ps1') -GameDir $GameDir
$source = Join-Path $modRoot 'dist\ScavengeExtraction'
$mods = Join-Path $GameDir 'Mods'
$destination = Join-Path $mods 'ScavengeExtraction'
New-Item -ItemType Directory -Path $mods -Force | Out-Null
if (Test-Path -LiteralPath $destination) {
    $existing = [xml](Get-Content -LiteralPath (Join-Path $destination 'manifest.xml') -Raw -Encoding UTF8)
    if ($existing.ModManifest.ID -ne 'ScavengeExtraction') { throw 'Destination belongs to a different mod.' }
    $backup = Join-Path $modRoot ('.deploy-backups\ScavengeExtraction-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path (Split-Path -Parent $backup) -Force | Out-Null
    Copy-Item -LiteralPath $destination -Destination $backup -Recurse
}
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Get-ChildItem -LiteralPath $source | Copy-Item -Destination $destination -Recurse -Force
foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File) {
    $relative = $file.FullName.Substring($source.Length + 1)
    $installed = Join-Path $destination $relative
    if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $installed).Hash) { throw "Deployment mismatch: $relative" }
}
Write-Output "Deployed and verified: $destination"
if (Get-Process 'Probably Stolen' -ErrorAction SilentlyContinue) { Write-Output 'Game is running. Restart normally to discover this new mod.' }
