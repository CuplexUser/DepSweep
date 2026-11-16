<#
.SYNOPSIS
  Regenerates the DepSweep application icons (multi-size .ico with PNG frames).

.DESCRIPTION
  Draws both icons with System.Drawing at every size (not downscaled from one bitmap, so small sizes stay crisp):
    - src/DepSweep.App/Assets/DepSweep.ico     GUI: blue tile, white shield with a green check mark
    - src/DepSweep.Cli/Assets/depsweep-cli.ico CLI: dark tile, blue shield with a ">_" prompt
  Run with Windows PowerShell 5.1:  powershell -ExecutionPolicy Bypass -File tools\make-icons.ps1
#>
param([string]$RepoRoot = (Split-Path -Parent $PSScriptRoot))

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# Shield on a 256-unit canvas.
function New-Shield {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.StartFigure()
    $p.AddBezier(128, 40, 150, 56, 172, 64, 200, 66)          # top edge, center -> right shoulder
    $p.AddLine(200, 66, 200, 124)                             # right side
    $p.AddBezier(200, 124, 200, 172, 170, 204, 128, 222)      # right side -> bottom point
    $p.AddBezier(128, 222, 86, 204, 56, 172, 56, 124)         # bottom point -> left side
    $p.AddLine(56, 124, 56, 66)                               # left side
    $p.AddBezier(56, 66, 84, 64, 106, 56, 128, 40)            # left shoulder -> center
    $p.CloseFigure()
    return $p
}

function New-Pen([System.Drawing.Color]$color, [float]$width) {
    $pen = New-Object System.Drawing.Pen $color, $width
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    return $pen
}

function Draw-Icon([int]$size, [string]$variant) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform($size / 256.0, $size / 256.0)

    # Small icons get a tighter margin and heavier strokes so the motif stays readable.
    $small = $size -le 24
    $margin = if ($small) { 4 } else { 10 }
    $tile = New-RoundedRect $margin $margin (256 - 2 * $margin) (256 - 2 * $margin) 52
    $rect = New-Object System.Drawing.RectangleF 0, 0, 256, 256

    if ($variant -eq 'gui') {
        $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 56, 139, 253)), ([System.Drawing.Color]::FromArgb(255, 21, 72, 171)), 45
        $g.FillPath($bg, $tile)
        $shield = New-Shield
        $g.FillPath([System.Drawing.Brushes]::White, $shield)
        $check = New-Pen ([System.Drawing.Color]::FromArgb(255, 31, 157, 85)) $(if ($small) { 30 } else { 24 })
        $g.DrawLines($check, [System.Drawing.PointF[]]@(
            (New-Object System.Drawing.PointF 94, 132),
            (New-Object System.Drawing.PointF 120, 160),
            (New-Object System.Drawing.PointF 164, 106)))
    }
    else {
        $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 52, 58, 68)), ([System.Drawing.Color]::FromArgb(255, 18, 20, 25)), 45
        $g.FillPath($bg, $tile)
        $shield = New-Shield
        $shieldBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 56, 139, 253)), ([System.Drawing.Color]::FromArgb(255, 21, 72, 171)), 90
        $g.FillPath($shieldBrush, $shield)
        $prompt = New-Pen ([System.Drawing.Color]::White) $(if ($small) { 26 } else { 18 })
        $g.DrawLines($prompt, [System.Drawing.PointF[]]@(
            (New-Object System.Drawing.PointF 92, 102),
            (New-Object System.Drawing.PointF 120, 128),
            (New-Object System.Drawing.PointF 92, 154)))
        $g.DrawLine($prompt, 132, 160, 166, 160)
    }

    $g.Dispose()
    return $bmp
}

function Write-Ico([string]$path, [string]$variant) {
    $frames = foreach ($s in $sizes) {
        $bmp = Draw-Icon $s $variant
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        , @($s, $ms.ToArray())
    }

    New-Item -ItemType Directory -Force (Split-Path -Parent $path) | Out-Null
    $out = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $out
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$frames.Count)   # ICONDIR
    $offset = 6 + 16 * $frames.Count
    foreach ($f in $frames) {                                                    # ICONDIRENTRY
        $dim = if ($f[0] -ge 256) { 0 } else { $f[0] }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32)
        $w.Write([uint32]$f[1].Length); $w.Write([uint32]$offset)
        $offset += $f[1].Length
    }
    foreach ($f in $frames) { $w.Write([byte[]]$f[1]) }
    $w.Flush()
    [System.IO.File]::WriteAllBytes($path, $out.ToArray())

    # Also keep a 256 px PNG preview next to the icon.
    [System.IO.File]::WriteAllBytes([System.IO.Path]::ChangeExtension($path, '.png'), $frames[-1][1])
    "Wrote $path ($($frames.Count) sizes)"
}

Write-Ico (Join-Path $RepoRoot 'src\DepSweep.App\Assets\DepSweep.ico') 'gui'
Write-Ico (Join-Path $RepoRoot 'src\DepSweep.Cli\Assets\depsweep-cli.ico') 'cli'
