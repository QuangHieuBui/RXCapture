# Builds res\app.ico (+ res\logo.png) from a logo image: removes the light background around the rounded
# icon (flood fill from the borders), crops to the icon shape and writes a multi-size PNG-compressed .ico.
# Usage: powershell -File tools\make-icon-from-image.ps1 -Source path\to\logo.jpg [-Preview out.png]
param([Parameter(Mandatory = $true)][string]$Source, [string]$Preview = '', [int]$Tolerance = 105, [int]$Erode = 2)
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent

$src = [System.Drawing.Bitmap]::FromFile((Resolve-Path $Source))
$w = $src.Width; $h = $src.Height
$bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g0 = [System.Drawing.Graphics]::FromImage($bmp); $g0.DrawImage($src, 0, 0, $w, $h); $g0.Dispose(); $src.Dispose()

$rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
$data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadWrite, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$stride = $data.Stride
$buf = New-Object byte[] ($stride * $h)
[System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buf, 0, $buf.Length)

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
public static class BgCut
{
    // returns bounding box [x0,y0,x1,y1] of the kept (non background) area; edits alpha in buf
    public static int[] Run(byte[] buf, int w, int h, int stride, int tol, int erode)
    {
        // background colour = average of the four corner 6x6 blocks
        long r = 0, g = 0, b = 0, n = 0;
        int[] cx = { 0, w - 6, 0, w - 6 }, cy = { 0, 0, h - 6, h - 6 };
        for (int k = 0; k < 4; k++)
            for (int y = cy[k]; y < cy[k] + 6; y++)
                for (int x = cx[k]; x < cx[k] + 6; x++) { int i = y * stride + x * 4; b += buf[i]; g += buf[i + 1]; r += buf[i + 2]; n++; }
        int br = (int)(r / n), bg = (int)(g / n), bb = (int)(b / n);
        bool[] bgm = new bool[w * h];
        Func<int, int, bool> near = delegate (int x, int y)
        {
            int i = y * stride + x * 4;
            int dr = buf[i + 2] - br, dg = buf[i + 1] - bg, db = buf[i] - bb;
            return buf[i + 2] > tol;   // the icon body is deep blue (low red); the light backdrop and its shadow have high red
        };
        Stack<int> st = new Stack<int>();
        for (int x = 0; x < w; x++) { st.Push(x); st.Push((h - 1) * w + x); }
        for (int y = 0; y < h; y++) { st.Push(y * w); st.Push(y * w + w - 1); }
        while (st.Count > 0)
        {
            int p = st.Pop(); int x = p % w, y = p / w;
            if (bgm[p] || !near(x, y)) continue;
            bgm[p] = true;
            if (x > 0) st.Push(p - 1); if (x < w - 1) st.Push(p + 1); if (y > 0) st.Push(p - w); if (y < h - 1) st.Push(p + w);
        }
        // grow the background by 'erode' pixels to remove the light fringe
        for (int it = 0; it < erode; it++)
        {
            bool[] nx = (bool[])bgm.Clone();
            for (int y = 1; y < h - 1; y++)
                for (int x = 1; x < w - 1; x++)
                {
                    int p = y * w + x;
                    if (!bgm[p] && (bgm[p - 1] || bgm[p + 1] || bgm[p - w] || bgm[p + w])) nx[p] = true;
                }
            bgm = nx;
        }
        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = y * w + x, i = y * stride + x * 4;
                if (bgm[p]) { buf[i] = buf[i + 1] = buf[i + 2] = 0; buf[i + 3] = 0; }
                else { buf[i + 3] = 255; if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
            }
        return new int[] { x0, y0, x1, y1 };
    }
}
"@
$bb = [BgCut]::Run($buf, $w, $h, $stride, $Tolerance, $Erode)
[System.Runtime.InteropServices.Marshal]::Copy($buf, 0, $data.Scan0, $buf.Length)
$bmp.UnlockBits($data)

$cw = $bb[2] - $bb[0] + 1; $ch = $bb[3] - $bb[1] + 1
$side = [Math]::Max($cw, $ch)
Write-Host "bbox $($bb -join ',')  size ${cw}x${ch}"
$sq = New-Object System.Drawing.Bitmap $side, $side, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$gs = [System.Drawing.Graphics]::FromImage($sq)
$gs.Clear([System.Drawing.Color]::Transparent)
$gs.DrawImage($bmp, [int](($side - $cw) / 2), [int](($side - $ch) / 2), (New-Object System.Drawing.Rectangle $bb[0], $bb[1], $cw, $ch), [System.Drawing.GraphicsUnit]::Pixel)
$gs.Dispose(); $bmp.Dispose()

function Resize([System.Drawing.Bitmap]$b, [int]$s) {
    $o = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($o)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.SmoothingMode = 'HighQuality'; $g.PixelOffsetMode = 'HighQuality'; $g.CompositingQuality = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $ia = New-Object System.Drawing.Imaging.ImageAttributes
    $ia.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
    $g.DrawImage($b, (New-Object System.Drawing.Rectangle 0, 0, $s, $s), 0, 0, $b.Width, $b.Height, [System.Drawing.GraphicsUnit]::Pixel, $ia)
    $g.Dispose(); return $o
}

$sizes = 256, 128, 64, 48, 32, 24, 16
$pngs = @()
foreach ($s in $sizes) {
    $r = Resize $sq $s
    $ms = New-Object System.IO.MemoryStream
    $r.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    if ($s -eq 256) { $r.Save("$root\res\logo.png", [System.Drawing.Imaging.ImageFormat]::Png) }
    $r.Dispose()
}

$icoPath = "$root\res\app.ico"
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close(); $fs.Close()
Write-Host "wrote $icoPath"

if ($Preview) {
    # preview on a checkerboard so transparency is visible
    $pv = New-Object System.Drawing.Bitmap 520, 300
    $g = [System.Drawing.Graphics]::FromImage($pv)
    for ($y = 0; $y -lt 300; $y += 20) { for ($x = 0; $x -lt 520; $x += 20) {
        $c = if ((($x + $y) / 20) % 2 -eq 0) { [System.Drawing.Color]::FromArgb(255, 200, 200, 200) } else { [System.Drawing.Color]::FromArgb(255, 120, 120, 120) }
        $br = New-Object System.Drawing.SolidBrush $c; $g.FillRectangle($br, $x, $y, 20, 20); $br.Dispose() } }
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.DrawImage((Resize $sq 260), 10, 20, 260, 260)
    $x = 290
    foreach ($s in 64, 48, 32, 24, 16) { $g.DrawImage((Resize $sq $s), $x, 30, $s, $s); $x += $s + 10 }
    $g.Dispose(); $pv.Save($Preview, [System.Drawing.Imaging.ImageFormat]::Png)
}
