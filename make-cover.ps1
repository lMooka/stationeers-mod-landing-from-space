# Monta as imagens de capa da Workshop (Preview.png 1280x720 e thumb.png 512x512) a partir de uma captura do jogo.
# Uso:  powershell -ExecutionPolicy Bypass -File LandingFromSpace\make-cover.ps1 -Source "<captura 1920x1080>.png" [-CenterX 960 -CenterY 520]
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [int]$CenterX = 960,
    [int]$CenterY = 520
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $here "ModPackage\About"

$title = "Landing From Space"
$subtitle = "choose where you land, then fly down from space"
$accent = [System.Drawing.Color]::FromArgb(255, 255, 192, 77)
$subColor = [System.Drawing.Color]::FromArgb(255, 205, 214, 224)

function New-Cover([int]$width, [int]$height, [int]$cropSize, [float]$titleSize, [float]$subSize, [string]$file, [bool]$twoLines, [float]$capsuleAt) {
    $src = [System.Drawing.Image]::FromFile($Source)
    try {
        $cropW = if ($width -eq $height) { $cropSize } else { $width }
        $cropH = if ($width -eq $height) { $cropSize } else { $height }
        # A capsula fica abaixo do centro ($capsuleAt da altura), deixando o ceu em cima para o titulo.
        $x = [Math]::Max(0, [Math]::Min($src.Width - $cropW, $CenterX - [int]($cropW / 2)))
        $y = [Math]::Max(0, [Math]::Min($src.Height - $cropH, $CenterY - [int]($cropH * $capsuleAt)))
        $bmp = New-Object System.Drawing.Bitmap $width, $height
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.TextRenderingHint = 'AntiAliasGridFit'
        $g.DrawImage($src, (New-Object System.Drawing.Rectangle 0, 0, $width, $height), (New-Object System.Drawing.Rectangle $x, $y, $cropW, $cropH), 'Pixel')

        # Sombra no topo para o texto ficar legivel.
        $band = [int]($height * 0.42)
        $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Rectangle 0, 0, $width, $band), ([System.Drawing.Color]::FromArgb(200, 10, 14, 22)), ([System.Drawing.Color]::FromArgb(0, 10, 14, 22)), 90
        $g.FillRectangle($grad, 0, 0, $width, $band)

        $fmt = New-Object System.Drawing.StringFormat
        $fmt.Alignment = 'Center'
        $titleFont = New-Object System.Drawing.Font "Segoe UI", $titleSize, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
        $subFont = New-Object System.Drawing.Font "Segoe UI", ([Math]::Max(1, $subSize)), ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
        $text = if ($twoLines) { "Landing From`nSpace" } else { $title }
        $top = [int]($height * 0.05)
        $titleHeight = $g.MeasureString($text, $titleFont, $width).Height
        $g.DrawString($text, $titleFont, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(160, 0, 0, 0))), (New-Object System.Drawing.RectangleF 3, ($top + 3), $width, $titleHeight), $fmt)
        $g.DrawString($text, $titleFont, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, $top, $width, $titleHeight), $fmt)
        # Traco amarelo entre o titulo e a frase.
        $lineY = $top + $titleHeight + [int]($height * 0.012)
        $lineW = [int]($width * 0.12)
        if ($subSize -gt 0) {
            $g.FillRectangle((New-Object System.Drawing.SolidBrush $accent), [int](($width - $lineW) / 2), $lineY, $lineW, [Math]::Max(3, [int]($height * 0.007)))
            $subTop = $lineY + [int]($height * 0.03)
            $g.DrawString($subtitle, $subFont, (New-Object System.Drawing.SolidBrush $subColor), (New-Object System.Drawing.RectangleF 0, $subTop, $width, ($subSize * 2.6)), $fmt)
        }

        $path = Join-Path $out $file
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
        Write-Output "$path ($width x $height)"
    }
    finally {
        $src.Dispose()
    }
}

New-Cover 1280 720 0 78 30 "Preview.png" $false 0.66
New-Cover 512 512 980 58 0 "thumb.png" $true 0.70
