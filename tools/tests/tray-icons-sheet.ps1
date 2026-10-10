# Draws the four tray icons at several sizes into one picture, in normal vision, in grayscale and as seen
# with red-green colour blindness (deuteranopia), on a dark and a light background, so that the shapes can
# be judged without relying on colour.
param([string]$Out = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'work\tray-icons.png'))

Add-Type -AssemblyName System.Drawing
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$asm = [Reflection.Assembly]::LoadFrom((Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'))
$icons = $asm.GetType('Semaphore.TrayIcons')
$levelType = $asm.GetType('Semaphore.Level')
$draw = $icons.GetMethod('Draw')
$levels = 'Idle', 'Working', 'Background', 'Compacting', 'Waiting', 'None'

function Icon($name, $size) { $draw.Invoke($null, @([Enum]::Parse($levelType, $name), [int]$size)) }

# Vision filters applied to a copy of the bitmap
function Apply-Vision($src, $mode) {
    if ($mode -eq 'normal') { return $src.Clone() }
    $dst = New-Object Drawing.Bitmap $src.Width, $src.Height
    for ($y = 0; $y -lt $src.Height; $y++) { for ($x = 0; $x -lt $src.Width; $x++) {
        $c = $src.GetPixel($x, $y)
        if ($mode -eq 'gray') {
            $v = [int](0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B)
            $dst.SetPixel($x, $y, [Drawing.Color]::FromArgb($c.A, $v, $v, $v))
        } else { # deuteranopia (Machado et al. 2009, severity 1.0)
            $r = 0.367322 * $c.R + 0.860646 * $c.G - 0.227968 * $c.B
            $g = 0.280085 * $c.R + 0.672501 * $c.G + 0.047413 * $c.B
            $b = -0.011820 * $c.R + 0.042940 * $c.G + 0.968881 * $c.B
            $cl = { param($v) [int][math]::Max(0, [math]::Min(255, $v)) }
            $dst.SetPixel($x, $y, [Drawing.Color]::FromArgb($c.A, (& $cl $r), (& $cl $g), (& $cl $b)))
        }
    } }
    return $dst
}

$modes = 'normal', 'gray', 'deutan'
$sizes = 16, 24, 32
$cell = 104
$labelW = 70
$bgs = @(@('dark', [Drawing.Color]::FromArgb(32, 32, 32)), @('light', [Drawing.Color]::FromArgb(243, 243, 243)))
$cols = $modes.Count * $sizes.Count
$width = $labelW + $cols * $cell
$height = 26 + $bgs.Count * ($levels.Count * $cell + 30) + 120
$sheet = New-Object Drawing.Bitmap $width, $height
$g = [Drawing.Graphics]::FromImage($sheet)
$g.InterpolationMode = 'NearestNeighbor'; $g.PixelOffsetMode = 'Half'
$font = New-Object Drawing.Font 'Segoe UI', 9
$g.Clear([Drawing.Color]::FromArgb(70, 70, 70))
$y0 = 4
foreach ($bg in $bgs) {
    $g.FillRectangle((New-Object Drawing.SolidBrush $bg[1]), 0, $y0 + 18, $width, $levels.Count * $cell + 6)
    $fg = if ($bg[0] -eq 'dark') { [Drawing.Brushes]::White } else { [Drawing.Brushes]::Black }
    for ($m = 0; $m -lt $modes.Count; $m++) { for ($s = 0; $s -lt $sizes.Count; $s++) {
        $g.DrawString("$($modes[$m]) $($sizes[$s])px ($($bg[0]))", $font, [Drawing.Brushes]::White, $labelW + ($m * $sizes.Count + $s) * $cell, $y0)
    } }
    for ($l = 0; $l -lt $levels.Count; $l++) {
        $g.DrawString($levels[$l], $font, $fg, 4, $y0 + 20 + $l * $cell + 40)
        for ($m = 0; $m -lt $modes.Count; $m++) { for ($s = 0; $s -lt $sizes.Count; $s++) {
            $bmp = Icon $levels[$l] $sizes[$s]
            $f = Apply-Vision $bmp $modes[$m]
            $scale = [int]($cell - 12) / $sizes[$s]
            $zoom = [math]::Floor($scale)
            $g.DrawImage($f, $labelW + ($m * $sizes.Count + $s) * $cell + 6, $y0 + 22 + $l * $cell, $sizes[$s] * $zoom, $sizes[$s] * $zoom)
            $bmp.Dispose(); $f.Dispose()
        } }
    }
    $y0 += $levels.Count * $cell + 30
}
# real size strip, 1:1
$g.DrawString('1:1  (16, 24, 32 px; normal | deuteranopia)', $font, [Drawing.Brushes]::White, 4, $y0)
$x = 8
foreach ($bg in $bgs) { foreach ($mode in 'normal', 'deutan') {
    $g.FillRectangle((New-Object Drawing.SolidBrush $bg[1]), $x - 4, $y0 + 20, 4 * 3 * 40 + 12, 64)
    foreach ($lvl in $levels) { foreach ($sz in 16, 24, 32) {
        $b = Icon $lvl $sz; $f = Apply-Vision $b $mode
        $g.DrawImage($f, $x, $y0 + 28, $sz, $sz); $x += $sz + 6
        $b.Dispose(); $f.Dispose()
    } ; $x += 6 }
    $x += 18
} }
$sheet.Save($Out)
"saved $Out"
