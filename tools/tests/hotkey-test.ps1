# The global shortcut, without pressing any keys (keys would go to whatever window is active):
# 1) a sandbox instance leaves the shortcut alone unless TRAFFICLIGHT_ALLOW_HOTKEY=1;
# 2) when allowed it registers Win+Alt+C: this script then cannot register the same combination;
# 3) "--hotkey" does what the shortcut does: with a request waiting, the panel opens and becomes the
#    foreground window (for a moment it takes the focus from whatever you are typing in);
# 4) with nothing waiting, the tray menu opens instead.
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Namespace TL -Name Dpi -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);'; [void][TL.Dpi]::SetProcessDpiAwarenessContext([IntPtr]-4)   # real pixels and coordinates at any display scale
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Text;
public class HK {
  [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint key);
  [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  public static bool Free() { bool ok = RegisterHotKey(IntPtr.Zero, 77, 0x1 | 0x8, 0x43); if (ok) UnregisterHotKey(IntPtr.Zero, 77); return ok; }
  public static string Foreground(out uint pid) { IntPtr h = GetForegroundWindow(); GetWindowThreadProcessId(h, out pid); var s = new StringBuilder(256); GetWindowText(h, s, 256); return s.ToString(); }
}
'@
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exe = Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'
$box = Join-Path $root 'work\tests\hotkey'
Get-Process ClaudeCodeTrafficLight -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
if (Test-Path $box) { Remove-Item $box -Recurse -Force }
New-Item -ItemType Directory -Force "$box\data", "$box\claude" | Out-Null
$utf8 = New-Object Text.UTF8Encoding $false
[IO.File]::WriteAllText("$box\data\config.json", '{"FirstRunDone":true,"Language":"en","StartupNotice":false,"LocalApprove":true,"PopupAsk":false}', $utf8)
$env:TRAFFICLIGHT_DATA_DIR = "$box\data"
$env:TRAFFICLIGHT_CLAUDE_DIR = "$box\claude"
$before = [HK]::Free()
"Win+Alt+C free before the test: $before" + $(if (-not $before) { ' (held by the copy you run: steps 1-2 cannot be judged)' } else { '' })

$app = Start-Process $exe -PassThru
Start-Sleep 3
"1) sandbox without permission leaves it free: " + [HK]::Free()
Stop-Process -Id $app.Id -Force; Start-Sleep 1

$env:TRAFFICLIGHT_ALLOW_HOTKEY = '1'
$app = Start-Process $exe -PassThru
$waiter = $null
try {
    Start-Sleep 3
    "2) sandbox with permission holds it: " + (-not [HK]::Free())

    & $exe --hotkey | Out-Null
    Start-Sleep 1.5
    $line = Select-String -Path "$box\data\trafficlight.log" -Pattern 'Menu opened from the keyboard at (\d+),(\d+)' | Select-Object -Last 1
    $scr = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $nearTray = $line -and [int]$line.Matches[0].Groups[1].Value -gt $scr.Width / 2 -and [int]$line.Matches[0].Groups[2].Value -gt $scr.Height / 2
    "4) nothing waiting -> menu opened next to the tray (bottom right), not at the mouse: " + [bool]$nearTray + "  ($($line.Line))"
    & $exe --hotkey | Out-Null   # a second call is harmless; the menu closes when the panel takes the focus below

    '{"hook_event_name":"UserPromptSubmit","session_id":"hk-1111","cwd":"C:/fake/Proj","prompt":"x"}' | & $exe --hook trafficlight | Out-Null
    $q = '{"hook_event_name":"PermissionRequest","session_id":"hk-1111","cwd":"C:/fake/Proj","tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"Which one?","header":"Pick","multiSelect":false,"options":[{"label":"First","description":"1"},{"label":"Second","description":"2"}]}]}}'
    [IO.File]::WriteAllText("$box\q.json", $q, $utf8)
    $waiter = Start-Process $exe -ArgumentList '--hook', 'trafficlight' -RedirectStandardInput "$box\q.json" -WindowStyle Hidden -PassThru
    Start-Sleep 2
    & $exe --hotkey | Out-Null
    Start-Sleep 1.5
    $fgPid = 0
    $title = [HK]::Foreground([ref]$fgPid)
    "3) request waiting -> panel in front: title='$title', belongs to the sandbox: " + ($fgPid -eq $app.Id)
}
finally {
    if ($waiter) { Stop-Process -Id $waiter.Id -Force -ErrorAction SilentlyContinue }
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    $env:TRAFFICLIGHT_ALLOW_HOTKEY = $null
    $env:TRAFFICLIGHT_DATA_DIR = $null
    $env:TRAFFICLIGHT_CLAUDE_DIR = $null
}
"Win+Alt+C free after the test: " + [HK]::Free()
