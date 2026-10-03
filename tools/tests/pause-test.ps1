# The pause of notifications on this computer, in a sandbox:
# a) not paused: the tray menu has "Pause notifications" with its three choices (picture menu-pause.png);
# b) paused for a while: the menu offers "Resume ... (paused until HH:mm)", the Notifications page says so and has
#    a button to end it (menu-resume.png, page-paused.png);
# c) switched off until switched on: the choices on the Notifications page are greyed out (page-off.png).
# Pictures in work\tests\pause\ .
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -Namespace TL -Name Dpi -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);'; [void][TL.Dpi]::SetProcessDpiAwarenessContext([IntPtr]-4)   # real pixels and coordinates at any display scale
Add-Type -TypeDefinition @'
using System; using System.Drawing; using System.Runtime.InteropServices;
public class PW {
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  public struct R { public int L, T, Rt, B; }
  public static Bitmap Grab(IntPtr h) {
    R w; GetWindowRect(h, out w);
    var b = new Bitmap(w.Rt - w.L, w.B - w.T);
    using (var g = Graphics.FromImage(b)) { IntPtr dc = g.GetHdc(); PrintWindow(h, dc, 2); g.ReleaseHdc(dc); }
    return b;
  }
}
'@ -ReferencedAssemblies System.Drawing
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exe = Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'
$box = Join-Path $root 'work\tests\pause'
Get-Process ClaudeCodeTrafficLight -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
if (Test-Path $box) { Remove-Item $box -Recurse -Force }
New-Item -ItemType Directory -Force $box | Out-Null
$utf8 = New-Object Text.UTF8Encoding $false

function Run($name, $extra, [scriptblock]$shots) {
    $d = "$box\$name"
    New-Item -ItemType Directory -Force "$d\data", "$d\claude" | Out-Null
    [IO.File]::WriteAllText("$d\data\config.json", '{"FirstRunDone":true,"Language":"en","Theme":"dark","StartupNotice":false' + $extra + '}', $utf8)
    $env:TRAFFICLIGHT_DATA_DIR = "$d\data"
    $env:TRAFFICLIGHT_CLAUDE_DIR = "$d\claude"
    $app = Start-Process $exe -PassThru
    try { Start-Sleep 3; & $shots $d $app }
    finally {
        Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
        $env:TRAFFICLIGHT_DATA_DIR = $null; $env:TRAFFICLIGHT_CLAUDE_DIR = $null
        Start-Sleep 1
    }
}
function MenuShot($d, $file) {
    & $exe --menu | Out-Null
    Start-Sleep 2
    $fs = [IO.File]::Open("$d\data\trafficlight.log", 'Open', 'Read', 'ReadWrite')
    $text = (New-Object IO.StreamReader($fs)).ReadToEnd(); $fs.Dispose()
    $m = [regex]::Matches($text, 'menu shot: (-?\d+),(-?\d+),(\d+),(\d+) sub: (-?\d+),(-?\d+),(\d+),(\d+)')
    $v = $m[$m.Count - 1].Groups
    $l = [Math]::Min([int]$v[1].Value, $(if ([int]$v[7].Value -gt 0) { [int]$v[5].Value } else { [int]$v[1].Value }))
    $t = [int]$v[2].Value
    $r = [Math]::Max([int]$v[1].Value + [int]$v[3].Value, [int]$v[5].Value + [int]$v[7].Value)
    $b = [Math]::Max([int]$v[2].Value + [int]$v[4].Value, [int]$v[6].Value + [int]$v[8].Value)
    $bmp = New-Object Drawing.Bitmap ($r - $l), ($b - $t)
    $g = [Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($l, $t, 0, 0, $bmp.Size); $g.Dispose()
    $bmp.Save("$box\$file"); $bmp.Dispose()
    # No key presses to close it: they would go to whatever window is active (a double Esc opens Rewind in
    # Claude Code). The menu is taken last and closes with the sandbox instance.
}
function PageShot($app, $file) {
    & $exe --page 1 | Out-Null
    Start-Sleep 2
    $p = Get-Process -Id $app.Id
    $bmp = [PW]::Grab($p.MainWindowHandle)
    $bmp.Save("$box\$file"); $bmp.Dispose()
}

Run 'a' '' { param($d, $app) MenuShot $d 'menu-pause.png' }
$later = (Get-Date).AddMinutes(45).Ticks
Run 'b' (',"DndUntilTicks":' + $later) { param($d, $app) PageShot $app 'page-paused.png'; MenuShot $d 'menu-resume.png' }
Run 'c' ',"NotificationsEnabled":false' { param($d, $app) PageShot $app 'page-off.png'; MenuShot $d 'menu-off.png' }
"pictures in $box"
