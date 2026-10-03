# WCAG contrast ratios of the interface palette, read from the compiled program so the test always matches
# the code. Normal text needs 4.5:1, graphical parts (borders, the selected-page bar, icon glyphs) 3:1.
# Exit code 1 when any pair fails.
Add-Type -AssemblyName System.Drawing
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$asm = [Reflection.Assembly]::LoadFrom((Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'))
$theme = $asm.GetType('Semaphore.Theme')
$palette = $asm.GetType('Semaphore.Palette')
$flags = [Reflection.BindingFlags]'Public,Static,NonPublic'

function Lum($c) {
    $f = { param($v) $v = $v / 255; if ($v -le 0.03928) { $v / 12.92 } else { [math]::Pow(($v + 0.055) / 1.055, 2.4) } }
    0.2126 * (& $f $c.R) + 0.7152 * (& $f $c.G) + 0.0722 * (& $f $c.B)
}
function Ratio($a, $b) {
    $la = Lum $a; $lb = Lum $b
    [math]::Round(([math]::Max($la, $lb) + 0.05) / ([math]::Min($la, $lb) + 0.05), 2)
}
function ThemeColor($name) { $theme.GetField($name, $flags).GetValue($null) }
function PaletteColor($name) {
    $p = $palette.GetProperty($name, $flags)
    if ($p) { return $p.GetValue($null) }
    $palette.GetField($name, $flags).GetValue($null)
}

$failed = 0
function Check($label, $fg, $bg, $need) {
    $r = Ratio $fg $bg
    $ok = $r -ge $need
    if (-not $ok) { $script:failed++ }
    '{0,-46} {1,6}  {2,4}  {3}' -f $label, $r, $need, $(if ($ok) { 'ok' } else { 'FAIL' })
}

'{0,-46} {1,6}  {2,4}  result' -f 'pair', 'ratio', 'need'
foreach ($mode in 'dark', 'light') {
    [void]$theme.GetMethod('Update').Invoke($null, @($mode))
    $page = ThemeColor 'Back'; $card = ThemeColor 'Surface'; $text = ThemeColor 'Fore'; $muted = ThemeColor 'Muted'
    $m = $mode.PadRight(5)
    Check "$m body text on page" $text $page 4.5
    Check "$m muted hint on page" $muted $page 4.5
    Check "$m muted hint on card" $muted $card 4.5
    # Switched-off controls are exempt from 4.5:1, but must stay readable.
    Check "$m switched-off text on page" (ThemeColor 'Disabled') $page 3.0
    Check "$m switched-off text on card" (ThemeColor 'Disabled') $card 3.0
    Check "$m control border on page" (PaletteColor 'ControlBorder') $page 3.0
    Check "$m control border on card" (PaletteColor 'ControlBorder') $card 3.0
    Check "$m link on page" (PaletteColor 'Link') $page 4.5
    Check "$m link (active) on page" (PaletteColor 'LinkActive') $page 4.5
    Check "$m status ok on page" (PaletteColor 'StatusOk') $page 4.5
    Check "$m status warning on page" (PaletteColor 'StatusWarn') $page 4.5
    Check "$m status error on page" (PaletteColor 'StatusError') $page 4.5
    Check "$m accent text (step number) on page" (PaletteColor 'AccentText') $page 4.5
    Check "$m accent bar on navigation" (PaletteColor 'AccentBar') $card 3.0
}

# The tray lamps are told apart by the shape on them (check, dots, exclamation), not by colour,
# so what must hold is the glyph against its own lamp.
$dark = PaletteColor 'GlyphDark'; $light = PaletteColor 'GlyphLight'
Check 'tray  dark check on green lamp' $dark (PaletteColor 'Green') 3.0
Check 'tray  dark dots on yellow lamp' $dark (PaletteColor 'Yellow') 3.0
Check 'tray  white exclamation on red lamp' $light (PaletteColor 'Red') 3.0
Check 'tray  white chevrons on blue lamp' $light (PaletteColor 'Blue') 3.0

"$failed pair(s) below the required ratio"
exit $(if ($failed -gt 0) { 1 } else { 0 })
