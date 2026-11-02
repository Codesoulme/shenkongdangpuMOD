$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$logPath = Join-Path $projectRoot ".deploy-staging\deploy-after-exit.log"
$logDir = Split-Path -Parent $logPath
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
Set-Content -LiteralPath $logPath -Value "Waiting for Probably Stolen to exit." -Encoding UTF8

try {
    $deadline = [DateTime]::UtcNow.AddHours(12)
    while ([DateTime]::UtcNow -lt $deadline) {
        $running = @(Get-Process -Name "Probably Stolen" -ErrorAction SilentlyContinue)
        if ($running.Count -eq 0) {
            Start-Sleep -Seconds 3
            $running = @(Get-Process -Name "Probably Stolen" -ErrorAction SilentlyContinue)
            if ($running.Count -eq 0) { break }
        }
        Start-Sleep -Seconds 5
    }
    if ([DateTime]::UtcNow -ge $deadline) { throw "Timed out waiting for game exit." }

    $deployOutput = & (Join-Path $PSScriptRoot "deploy.ps1") -GameDir "E:\SteamLibrary\steamapps\common\Probably Stolen" 2>&1
    $deployOutput | Add-Content -LiteralPath $logPath -Encoding UTF8
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "Deploy script failed with exit code $LASTEXITCODE." }

    $source = Join-Path $projectRoot "bin\Release\netstandard2.1\DeepSpacePawnshopLedger.dll"
    $target = "E:\SteamLibrary\steamapps\common\Probably Stolen\Mods\DeepSpacePawnshopLedger\DeepSpacePawnshopLedger.dll"
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) {
        throw "Installed DLL hash does not match the build."
    }
    "Deploy complete. SHA256: $((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash)" |
        Add-Content -LiteralPath $logPath -Encoding UTF8
}
catch {
    $_ | Out-String | Add-Content -LiteralPath $logPath -Encoding UTF8
}
