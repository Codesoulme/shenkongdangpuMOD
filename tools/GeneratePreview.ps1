param(
    [string]$Source = "$PSScriptRoot\..\Content\preview-source.jpg",
    [string]$Output = "$PSScriptRoot\..\Content\preview.png"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$sourceImage = [System.Drawing.Image]::FromFile((Resolve-Path -LiteralPath $Source).Path)
$bitmap = [System.Drawing.Bitmap]::new(1920, 1080)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
$graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

try {
    $graphics.DrawImage($sourceImage, 0, 0, 1920, 1080)

    $band = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(228, 11, 15, 29))
    $graphics.FillRectangle($band, 0, 0, 1920, 238)
    $band.Dispose()

    $accent = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 97, 214, 217))
    $graphics.FillRectangle($accent, 0, 238, 1920, 5)
    $graphics.FillRectangle($accent, 84, 59, 10, 132)
    $accent.Dispose()

    $titleFont = [System.Drawing.Font]::new('SimHei', 78, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $subtitleFont = [System.Drawing.Font]::new('Arial', 31, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $titleBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 244, 247, 249))
    $subtitleBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 131, 216, 222))

    try {
        $graphics.DrawString('自由聊天&议价&交换', $titleFont, $titleBrush, 121, 55)
        $graphics.DrawString('PROBABLY STOLEN  /  FREE CHAT + BARGAIN + BARTER', $subtitleFont, $subtitleBrush, 128, 157)
    }
    finally {
        $titleFont.Dispose()
        $subtitleFont.Dispose()
        $titleBrush.Dispose()
        $subtitleBrush.Dispose()
    }

    $directory = Split-Path -Parent $Output
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $bitmap.Save($Output, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Output (Resolve-Path -LiteralPath $Output).Path
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
    $sourceImage.Dispose()
}
