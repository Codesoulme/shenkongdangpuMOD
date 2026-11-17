#requires -Version 7.0
param(
    [switch]$Publish,
    [string]$ChangeNote = "",
    [string]$UploaderDirectory = "$PSScriptRoot\..\..\..\.workshop-submission\FreeChatUploader"
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$stagingRoot = Join-Path $projectRoot ".workshop-submission"
$packageDir = Join-Path $stagingRoot "DeepSpacePawnshopLedger"
$uploaderDir = Join-Path $stagingRoot "LedgerUploader"
$manifestPath = Join-Path $projectRoot "manifest.xml"
$descriptionPath = Join-Path $projectRoot "Content\WorkshopDescription.txt"
$publishedFileId = "3832110492"

if (-not (Test-Path -LiteralPath $descriptionPath)) {
    throw "缺少工坊说明：$descriptionPath"
}

& dotnet build (Join-Path $projectRoot "AIWorldPawnshop.csproj") -c Release -v q
if ($LASTEXITCODE -ne 0) { throw "Release 构建失败。" }
& (Join-Path $PSScriptRoot "package-workshop.ps1") -OutputDir $packageDir

$manifest = [xml](Get-Content -LiteralPath $manifestPath -Raw)
if ($manifest.ModManifest.ID -ne "DeepSpacePawnshopLedger") {
    throw "当前清单不是账本模组，已停止上传。"
}
$version = [string]$manifest.ModManifest.ModVersion
$note = if ([string]::IsNullOrWhiteSpace($ChangeNote)) { "$version：增大信用点收支飘字" } else { $ChangeNote.Trim() }
$note = $note -replace '[\r\n]', ' '

New-Item -ItemType Directory -Path $uploaderDir -Force | Out-Null
foreach ($name in @(
    "WorkshopUploader.exe",
    "com.rlabrecque.steamworks.net.dll",
    "steam_api64.dll",
    "steam_appid.txt"
)) {
    $source = Join-Path $UploaderDirectory $name
    if (-not (Test-Path -LiteralPath $source)) {
        throw "上传工具缺少 $name：$UploaderDirectory"
    }
    $destination = Join-Path $uploaderDir $name
    if ([IO.Path]::GetFullPath($source) -ne [IO.Path]::GetFullPath($destination)) {
        Copy-Item -LiteralPath $source -Destination $destination -Force
    }
}
Copy-Item -LiteralPath $descriptionPath -Destination (Join-Path $uploaderDir "description.txt") -Force

$config = @(
    "appid=4348910",
    "publishedfileid=$publishedFileId",
    "content=$packageDir",
    "preview=$(Join-Path $packageDir 'preview.png')",
    "visibility=0",
    "title=信用点收支显示&账本：好好记账",
    "description=",
    "descriptionfile=description.txt",
    "changenote=$note",
    "requiremanifest=1",
    "gamedir=E:\SteamLibrary\steamapps\common\Probably Stolen"
)
Set-Content -LiteralPath (Join-Path $uploaderDir "upload.ini") -Value $config -Encoding utf8

Push-Location $uploaderDir
try {
    $arguments = if ($Publish) { @("--yes") } else { @() }
    $outputPath = Join-Path $uploaderDir "upload-output.txt"
    $errorPath = Join-Path $uploaderDir "upload-error.txt"
    Remove-Item -LiteralPath $outputPath, $errorPath -Force -ErrorAction SilentlyContinue
    $process = Start-Process -FilePath (Join-Path $uploaderDir "WorkshopUploader.exe") `
        -ArgumentList $arguments -WorkingDirectory $uploaderDir -Wait -PassThru -NoNewWindow `
        -RedirectStandardOutput $outputPath -RedirectStandardError $errorPath
    $uploadOutput = @()
    if (Test-Path -LiteralPath $outputPath) { $uploadOutput += Get-Content -LiteralPath $outputPath }
    if (Test-Path -LiteralPath $errorPath) { $uploadOutput += Get-Content -LiteralPath $errorPath }
    $uploadOutput | ForEach-Object { Write-Host $_ }
    if ($process.ExitCode -ne 0) { throw "上传工具退出码：$($process.ExitCode)" }
    $resultText = $uploadOutput -join "`n"
    if ($Publish -and
        (-not $resultText.Contains("[5/5] 完成！", [StringComparison]::Ordinal) -or
         -not $resultText.Contains("publishedFileId = $publishedFileId", [StringComparison]::Ordinal))) {
        throw "上传工具未明确确认目标条目完成；请核对条目状态，避免重复提交。"
    }
}
finally {
    Pop-Location
}

$savedId = Get-Content -LiteralPath (Join-Path $uploaderDir "upload.ini") |
    Where-Object { $_ -like "publishedfileid=*" } |
    Select-Object -First 1
if ($savedId -ne "publishedfileid=$publishedFileId") {
    throw "上传工具写回的条目 ID 与预期不符：$savedId"
}

$mode = if ($Publish) { "上传完成" } else { "检查完成，尚未上传" }
Write-Host "$mode：版本 $version；条目 https://steamcommunity.com/sharedfiles/filedetails/?id=$publishedFileId"
