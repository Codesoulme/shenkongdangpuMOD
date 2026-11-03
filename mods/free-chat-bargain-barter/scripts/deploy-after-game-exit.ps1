$ErrorActionPreference = "Stop"
if ($PSVersionTable.PSEdition -ne "Core") {
    $pwsh = (Get-Command pwsh.exe -ErrorAction Stop).Source
    & $pwsh -NoProfile -File $PSCommandPath
    exit $LASTEXITCODE
}
$projectRoot = Split-Path -Parent $PSScriptRoot
$logDir = Join-Path $projectRoot ".deploy-staging"
$logPath = Join-Path $logDir "deploy-after-exit.log"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
Set-Content -LiteralPath $logPath -Value "Waiting for Probably Stolen to exit." -Encoding UTF8

try {
    $deadline = [DateTime]::UtcNow.AddHours(24)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (-not (Get-Process -Name "Probably Stolen" -ErrorAction SilentlyContinue)) {
            Start-Sleep -Seconds 3
            if (-not (Get-Process -Name "Probably Stolen" -ErrorAction SilentlyContinue)) {
                break
            }
        }
        Start-Sleep -Seconds 5
    }
    if ([DateTime]::UtcNow -ge $deadline) {
        throw "Timed out waiting for game exit."
    }

    $deployOutput = & (Join-Path $PSScriptRoot "deploy.ps1") 2>&1
    $deployOutput | Add-Content -LiteralPath $logPath -Encoding UTF8

    $source = Join-Path $projectRoot "bin\Release\netstandard2.1\FreeChatBargainBarter.dll"
    $target = "E:\SteamLibrary\steamapps\common\Probably Stolen\Mods\FreeChatBargainBarter\FreeChatBargainBarter.dll"
    $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    $targetHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
    if ($sourceHash -ne $targetHash) {
        throw "Installed DLL hash does not match the Release build."
    }
    "Deploy complete. SHA256: $targetHash" | Add-Content -LiteralPath $logPath -Encoding UTF8
}
catch {
    $_ | Out-String | Add-Content -LiteralPath $logPath -Encoding UTF8
}
