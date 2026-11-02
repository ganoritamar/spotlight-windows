# Draws the app icon (dark rounded square + magnifier) and the tray icon (white magnifier)
# and writes them as multi-size .ico files into src\Spotlight\Assets.
param([string]$OutDir = (Join-Path $PSScriptRoot '..\src\Spotlight\Assets'))

Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-Frame([int]$Size, [bool]$Badge) {
    $bmp = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    if ($Badge) {
        $r = $Size * 0.45
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $w = $Size - 1
        $path.AddArc(0, 0, $r, $r, 180, 90)
        $path.AddArc($w - $r, 0, $r, $r, 270, 90)
        $path.AddArc($w - $r, $w - $r, $r, $r, 0, 90)
        $path.AddArc(0, $w - $r, $r, $r, 90, 90)
        $path.CloseFigure()
        $rect = New-Object System.Drawing.RectangleF 0, 0, $Size, $Size
        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 74, 78, 92)), ([System.Drawing.Color]::FromArgb(255, 24, 25, 30)), 90.0
        $g.FillPath($brush, $path)
        $box = $Size * 0.64; $off = $Size * 0.18
    } else {
        $box = $Size * 1.0; $off = 0
    }

    $stroke = [Math]::Max(1.6, $box * 0.13)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $stroke
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $rad = $box * 0.27
    $cx = $off + $box * 0.42; $cy = $off + $box * 0.42
    $g.DrawEllipse($pen, [float]($cx - $rad), [float]($cy - $rad), [float](2 * $rad), [float](2 * $rad))
    $h1 = $rad * 0.75
    $g.DrawLine($pen, [float]($cx + $h1), [float]($cy + $h1), [float]($off + $box * 0.88), [float]($off + $box * 0.88))
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

function Write-Ico([string]$Path, [int[]]$Sizes, [bool]$Badge) {
    $frames = @()
    foreach ($s in $Sizes) { $frames += , (New-Frame $s $Badge) }
    $fs = [System.IO.File]::Create($Path)
    $bw = New-Object System.IO.BinaryWriter $fs
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$Sizes.Count)
    $offset = 6 + 16 * $Sizes.Count
    for ($i = 0; $i -lt $Sizes.Count; $i++) {
        $s = $Sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
        $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$frames[$i].Length); $bw.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($f in $frames) { $bw.Write([byte[]]$f) }
    $bw.Close()
}

Write-Ico (Join-Path $OutDir 'app.ico') @(16, 20, 24, 32, 40, 48, 64, 256) $true
Write-Ico (Join-Path $OutDir 'tray.ico') @(16, 20, 24, 32, 40, 48) $false
Write-Output "Icons written to $OutDir"
