# Draws the trophy app icon and writes app.ico (PNG-compressed, 16-256 px).
param([string]$Out = (Join-Path $PSScriptRoot '..\app.ico'))
Add-Type -AssemblyName System.Drawing

function Draw-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0

    # rounded blue tile
    $r = 56 * $s; $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $w = $size - 1
    $path.AddArc(0, 0, $r, $r, 180, 90); $path.AddArc($w - $r, 0, $r, $r, 270, 90)
    $path.AddArc($w - $r, $w - $r, $r, $r, 0, 90); $path.AddArc(0, $w - $r, $r, $r, 90, 90); $path.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new(0, $size)), ([System.Drawing.Color]::FromArgb(255, 59, 111, 216)), ([System.Drawing.Color]::FromArgb(255, 33, 62, 140))
    $g.FillPath($bg, $path)

    $gold = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 50 * $s)), ([System.Drawing.PointF]::new(0, 210 * $s)), ([System.Drawing.Color]::FromArgb(255, 255, 214, 92)), ([System.Drawing.Color]::FromArgb(255, 230, 160, 30))
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 255, 200, 70)), ([float](14 * $s))

    # handles
    $g.DrawArc($pen, 46 * $s, 66 * $s, 52 * $s, 60 * $s, 90, 180)
    $g.DrawArc($pen, 158 * $s, 66 * $s, 52 * $s, 60 * $s, 270, 180)
    # cup
    $cup = New-Object System.Drawing.Drawing2D.GraphicsPath
    $cup.AddLine(72 * $s, 56 * $s, 184 * $s, 56 * $s)
    $cup.AddBezier(184 * $s, 56 * $s, 184 * $s, 130 * $s, 160 * $s, 150 * $s, 128 * $s, 152 * $s)
    $cup.AddBezier(128 * $s, 152 * $s, 96 * $s, 150 * $s, 72 * $s, 130 * $s, 72 * $s, 56 * $s)
    $g.FillPath($gold, $cup)
    # stem and base
    $g.FillRectangle($gold, 116 * $s, 148 * $s, 24 * $s, 34 * $s)
    $g.FillRectangle($gold, 84 * $s, 180 * $s, 88 * $s, 22 * $s)

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 256, 64, 48, 32, 24, 16
$images = foreach ($sz in $sizes) { , (Draw-Icon $sz) }

$ico = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ico
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $img = $images[$i]
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$img.Length); $bw.Write([uint32]$offset)
    $offset += $img.Length
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Flush()
[IO.File]::WriteAllBytes([IO.Path]::GetFullPath($Out), $ico.ToArray())
# 256 px PNG used as the logo inside the app
[IO.File]::WriteAllBytes(([IO.Path]::Combine([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Out)), 'assets', 'logo.png')), $images[0])
"wrote $Out"

