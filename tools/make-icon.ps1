# Generates res\app.ico (camera-lens style icon) using System.Drawing. No external assets.
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$sizes = 256, 48, 32, 16
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $u = $s / 256.0
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush ((New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 0, $s), [System.Drawing.Color]::FromArgb(255, 34, 197, 94), [System.Drawing.Color]::FromArgb(255, 21, 128, 61))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = 52 * $u; $m = 8 * $u; $w = $s - 2 * $m
    $path.AddArc($m, $m, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($m + $w - 2 * $r, $m, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($m + $w - 2 * $r, $m + $w - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc($m, $m + $w - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $g.FillPath($bg, $path)
    # selection-corner brackets (capture symbol)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), (16 * $u)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $a = 66 * $u; $b = 190 * $u; $l = 44 * $u
    $g.DrawLines($pen, @((New-Object System.Drawing.PointF $a, ($a + $l)), (New-Object System.Drawing.PointF $a, $a), (New-Object System.Drawing.PointF ($a + $l), $a)))
    $g.DrawLines($pen, @((New-Object System.Drawing.PointF ($b - $l), $a), (New-Object System.Drawing.PointF $b, $a), (New-Object System.Drawing.PointF $b, ($a + $l))))
    $g.DrawLines($pen, @((New-Object System.Drawing.PointF $b, ($b - $l)), (New-Object System.Drawing.PointF $b, $b), (New-Object System.Drawing.PointF ($b - $l), $b)))
    $g.DrawLines($pen, @((New-Object System.Drawing.PointF ($a + $l), $b), (New-Object System.Drawing.PointF $a, $b), (New-Object System.Drawing.PointF $a, ($b - $l))))
    $g.FillEllipse([System.Drawing.Brushes]::White, 108 * $u, 108 * $u, 40 * $u, 40 * $u)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    $bmp.Dispose()
}
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $root 'res\app.ico'), $out.ToArray())
Write-Host "app.ico written"
