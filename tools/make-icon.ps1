# Generates src\app.ico: a traffic light with three lit lamps, in several sizes (PNG-compressed entries).
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$sizes = 16, 24, 32, 48, 64, 128, 256

function Draw([int]$size) {
    $bmp = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([Drawing.Color]::Transparent)
    $s = $size / 256.0

    # housing
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $x = 62 * $s; $y = 8 * $s; $w = 132 * $s; $h = 240 * $s; $r = 44 * $s
    $path.AddArc($x, $y, $r, $r, 180, 90)
    $path.AddArc($x + $w - $r, $y, $r, $r, 270, 90)
    $path.AddArc($x + $w - $r, $y + $h - $r, $r, $r, 0, 90)
    $path.AddArc($x, $y + $h - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $body = New-Object Drawing.Drawing2D.LinearGradientBrush ([Drawing.RectangleF]::new($x, $y, $w, $h)), ([Drawing.Color]::FromArgb(70, 74, 82)), ([Drawing.Color]::FromArgb(28, 30, 34)), 90
    $g.FillPath($body, $path)
    $edge = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(150, 170, 178, 190)), ([Math]::Max(1.0, 7 * $s))
    $g.DrawPath($edge, $path)

    # lamps
    $lamps = @(
        @{ y = 24;  c = [Drawing.Color]::FromArgb(239, 68, 68) },
        @{ y = 94;  c = [Drawing.Color]::FromArgb(250, 204, 21) },
        @{ y = 164; c = [Drawing.Color]::FromArgb(34, 197, 94) }
    )
    foreach ($l in $lamps) {
        $d = 68 * $s; $lx = 94 * $s; $ly = $l.y * $s
        $brush = New-Object Drawing.SolidBrush $l.c
        $g.FillEllipse($brush, $lx, $ly, $d, $d)
        $shine = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(95, 255, 255, 255))
        $g.FillEllipse($shine, $lx + 10 * $s, $ly + 7 * $s, 34 * $s, 24 * $s)
    }
    $g.Dispose()
    return $bmp
}

$images = foreach ($sz in $sizes) {
    $bmp = Draw $sz
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    [pscustomobject]@{ Size = $sz; Data = $ms.ToArray() }
}

$out = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($i in $images) {
    $dim = if ($i.Size -ge 256) { 0 } else { $i.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$i.Data.Length); $w.Write([uint32]$offset)
    $offset += $i.Data.Length
}
foreach ($i in $images) { $w.Write($i.Data) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $root 'src\app.ico'), $out.ToArray())
Write-Host "Wrote src\app.ico"
