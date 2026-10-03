# The short animation for the README (docs/demo.gif): two sessions work, one asks a question in the panel, after the
# answer it goes on and finishes. Made from pictures of the program's own windows in a sandbox with made-up sessions
# (no desktop, no taskbar), joined by tools\screenshots\make-gif.py (Python with Pillow).
Add-Type -AssemblyName System.Drawing
Add-Type -Namespace TL -Name Dpi -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);'; [void][TL.Dpi]::SetProcessDpiAwarenessContext([IntPtr]-4)   # real pixels and coordinates at any display scale
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System; using System.Drawing; using System.Runtime.InteropServices; using System.Text;
public class GW {
  delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  public struct R { public int L, T, Rt, B; }
  public static IntPtr Find(int pid, string title) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); var s = new StringBuilder(256); GetWindowText(h, s, 256);
      if (p == pid && IsWindowVisible(h) && s.ToString() == title) { found = h; return false; } return true; }, IntPtr.Zero);
    return found;
  }
  public static Bitmap Grab(IntPtr h) {
    R r; GetWindowRect(h, out r);
    var b = new Bitmap(r.Rt - r.L, r.B - r.T);
    using (var g = Graphics.FromImage(b)) { IntPtr dc = g.GetHdc(); PrintWindow(h, dc, 2); g.ReleaseHdc(dc); }
    return b;
  }
}
'@
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exe = Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'
$work = Join-Path $root 'work\gif'
if (Test-Path $work) { [IO.Directory]::Delete($work, $true) }
New-Item -ItemType Directory -Force $work | Out-Null
$utf8 = New-Object Text.UTF8Encoding $false
$sids = @('11111111-aaaa-4aaa-8aaa-111111111111', '33333333-cccc-4ccc-8ccc-333333333333')
$titles = @('Refactor the export module', 'Update the changelog')
$cwds = @('C:\Projects\my-app', 'C:\Projects\docs-site')

function Start-Sandbox($name) {
    $d = "$work\$name"
    New-Item -ItemType Directory -Force "$d\data", "$d\claude\projects\demo" | Out-Null
    for ($i = 0; $i -lt 2; $i++) {
        [IO.File]::WriteAllText("$d\claude\projects\demo\$($sids[$i]).jsonl", (@{ type = 'ai-title'; aiTitle = $titles[$i]; sessionId = $sids[$i] } | ConvertTo-Json -Compress) + "`n", $utf8)
    }
    [IO.File]::WriteAllText("$d\data\config.json", '{"FirstRunDone":true,"Language":"en","Theme":"dark","StartupNotice":false,"LocalApprove":true,"PopupAsk":true,"SoundWaiting":false,"SoundDone":false,"ToastWaiting":false,"ToastDone":false}', $utf8)
    $env:TRAFFICLIGHT_DATA_DIR = "$d\data"
    $env:TRAFFICLIGHT_CLAUDE_DIR = "$d\claude"
    $script:app = Start-Process $exe -PassThru
    Start-Sleep 3
    return $d
}
function Stop-Sandbox() {
    Stop-Process -Id $script:app.Id -Force -ErrorAction SilentlyContinue
    $env:TRAFFICLIGHT_DATA_DIR = $null; $env:TRAFFICLIGHT_CLAUDE_DIR = $null
    Start-Sleep 1
}
function Hook($d, $i, $rest) {
    $tp = ("$d\claude\projects\demo\$($sids[$i]).jsonl").Replace('\', '\\')
    $json = '{' + $rest + ',"session_id":"' + $sids[$i] + '","cwd":"' + $cwds[$i].Replace('\', '\\') + '","transcript_path":"' + $tp + '"}'
    $json | & $exe --hook trafficlight | Out-Null
}
function Menu-Shot($d, $file) {
    & $exe --menu | Out-Null
    Start-Sleep 2
    $text = [IO.File]::ReadAllText("$d\data\trafficlight.log")
    $m = [regex]::Matches($text, 'menu shot: (-?\d+),(-?\d+),(\d+),(\d+) sub: (-?\d+),(-?\d+),(\d+),(\d+)')
    $v = $m[$m.Count - 1].Groups
    # the menu alone, without its submenu
    $b = New-Object Drawing.Bitmap ([int]$v[3].Value), ([int]$v[4].Value)
    $g = [Drawing.Graphics]::FromImage($b); $g.CopyFromScreen([int]$v[1].Value, [int]$v[2].Value, 0, 0, $b.Size); $g.Dispose()
    $b.Save("$work\$file"); $b.Dispose()
}

try {
    # 1. two sessions at work
    $d = Start-Sandbox 'a'
    Hook $d 0 '"hook_event_name":"UserPromptSubmit","prompt":"x"'
    Hook $d 1 '"hook_event_name":"UserPromptSubmit","prompt":"x"'
    Start-Sleep 1.5
    Menu-Shot $d '1-working.png'
    Stop-Sandbox

    # 2. one of them asks: the panel opens by itself
    $d = Start-Sandbox 'b'
    Hook $d 0 '"hook_event_name":"UserPromptSubmit","prompt":"x"'
    Hook $d 1 '"hook_event_name":"UserPromptSubmit","prompt":"x"'
    $q = '{"hook_event_name":"PermissionRequest","session_id":"' + $sids[1] + '","cwd":"' + $cwds[1].Replace('\', '\\') + '","tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"Which format should the release notes use?","header":"Format","multiSelect":false,"options":[{"label":"Short summary","description":"A few lines for the announcement."},{"label":"Detailed list","description":"Every change with its reason."},{"label":"Table","description":"Change, area and impact in columns."}]}]}}'
    [IO.File]::WriteAllText("$d\q.json", $q, $utf8)
    $waiter = Start-Process $exe -ArgumentList '--hook', 'trafficlight' -RedirectStandardInput "$d\q.json" -WindowStyle Hidden -PassThru
    Start-Sleep 2.5
    $h = [GW]::Find($script:app.Id, 'Claude Code needs you')
    if ($h -eq [IntPtr]::Zero) { throw 'the panel did not open' }
    $p = [GW]::Grab($h); $p.Save("$work\2-panel.png"); $p.Dispose()
    Menu-Shot $d '2-menu-waiting.png'
    Stop-Process -Id $waiter.Id -Force -ErrorAction SilentlyContinue
    Stop-Sandbox

    # 3. answered: the session goes on, and later it is done
    $d = Start-Sandbox 'c'
    Hook $d 0 '"hook_event_name":"UserPromptSubmit","prompt":"x"'
    Hook $d 1 '"hook_event_name":"UserPromptSubmit","prompt":"x"'
    Hook $d 1 '"hook_event_name":"Stop"'
    Start-Sleep 1.5
    Menu-Shot $d '3-done.png'
    Stop-Sandbox

    # the lamps for the title picture, drawn by the program itself
    $asm = [Reflection.Assembly]::LoadFrom($exe)
    $draw = $asm.GetType('Semaphore.TrayIcons').GetMethod('Draw', [Reflection.BindingFlags]'Static,Public,NonPublic')
    $level = $asm.GetType('Semaphore.Level')
    foreach ($l in 'Working', 'Compacting', 'Waiting', 'Idle') {
        $bmp = $draw.Invoke($null, @([Enum]::Parse($level, $l), 64)); $bmp.Save("$work\lamp-$l.png"); $bmp.Dispose()
    }

    python (Join-Path $PSScriptRoot 'make-gif.py') $work (Join-Path $root 'docs\demo.gif')
}
finally {
    if ($script:app) { Stop-Process -Id $script:app.Id -Force -ErrorAction SilentlyContinue }
    $env:TRAFFICLIGHT_DATA_DIR = $null; $env:TRAFFICLIGHT_CLAUDE_DIR = $null
}
