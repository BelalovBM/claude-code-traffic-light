# The settings window on a second monitor with a different scale. Opens the window in a sandbox, moves it onto
# every other monitor (and back) and takes it from the screen as it is shown there, so blur or wrong sizes are
# visible. Needs two monitors with different scales. Pictures: work\tests\monitor-move\<name>.png
param([string]$Name = 'shot')
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public class MM {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out R r, int size);
  public struct R { public int L, T, Rt, B; }
  public static R Visible(IntPtr h) { R r; if (DwmGetWindowAttribute(h, 9, out r, 16) != 0) GetWindowRect(h, out r); return r; }
}
'@
[void][MM]::SetProcessDpiAwarenessContext([IntPtr]-4)
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exe = Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'
$box = Join-Path $root 'work\tests\monitor-move'
Get-Process ClaudeCodeTrafficLight -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
if (Test-Path "$box\data") { Remove-Item "$box\data", "$box\claude" -Recurse -Force }
New-Item -ItemType Directory -Force "$box\data", "$box\claude" | Out-Null
[IO.File]::WriteAllText("$box\data\config.json", '{"FirstRunDone":true,"Language":"en","Theme":"light","StartupNotice":false}', (New-Object Text.UTF8Encoding $false))
$env:TRAFFICLIGHT_DATA_DIR = "$box\data"
$env:TRAFFICLIGHT_CLAUDE_DIR = "$box\claude"
$app = Start-Process $exe -PassThru
function Shot($h, $file) {
    $r = [MM]::Visible($h)
    $b = New-Object Drawing.Bitmap ($r.Rt - $r.L), ($r.B - $r.T)
    $g = [Drawing.Graphics]::FromImage($b); $g.CopyFromScreen($r.L, $r.T, 0, 0, $b.Size); $g.Dispose()
    $b.Save("$box\$file"); $b.Dispose()
    "{0}: window {1}x{2} at {3},{4}, window DPI {5}" -f $file, ($r.Rt - $r.L), ($r.B - $r.T), $r.L, $r.T, [MM]::GetDpiForWindow($h)
}
try {
    Start-Sleep 3
    & $exe --page 3 | Out-Null
    Start-Sleep 2
    $h = (Get-Process -Id $app.Id).MainWindowHandle
    $screens = [Windows.Forms.Screen]::AllScreens
    $i = 0
    $order = @($screens | Where-Object { -not $_.Primary }) + @($screens | Where-Object { $_.Primary })
    foreach ($s in $order) {
        $i++
        # On top of everything for the picture only (other programs' windows may lie there), then back to normal.
        [void][MM]::SetWindowPos($h, [IntPtr](-1), $s.WorkingArea.X + 40, $s.WorkingArea.Y + 30, 0, 0, 0x0001)
        Start-Sleep 2
        Shot $h ("$Name-$i-" + $s.DeviceName.Replace('\', '').Replace('.', '') + '.png')
        [void][MM]::SetWindowPos($h, [IntPtr](-2), 0, 0, 0, 0, 0x0001 -bor 0x0002)
    }
}
finally {
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    $env:TRAFFICLIGHT_DATA_DIR = $null
    $env:TRAFFICLIGHT_CLAUDE_DIR = $null
}
