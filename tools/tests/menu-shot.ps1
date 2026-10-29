# A picture of the tray menu with the made-up sessions of the self-check, for the site: the program in a sandbox,
# the sessions sent to it as hook events, the menu opened by --menu and pictured as its own window (PrintWindow), so
# nothing else of the screen gets in.
#   powershell -File menu-shot.ps1 [-Lang en] [-Theme dark]
param([string]$Lang = 'en', [string]$Theme = 'dark')
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class MenuShot {
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
  delegate bool Cb(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(Cb cb, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
  // The drop-down of the program: the largest visible window of its process (the menu is the only one shown).
  public static IntPtr Find(uint pid) {
    IntPtr found = IntPtr.Zero; int best = 0;
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p);
      if (p == pid && IsWindowVisible(h)) {
        var c = new System.Text.StringBuilder(200); GetClassName(h, c, 200);
        RECT r; GetWindowRect(h, out r);
        int area = (r.R - r.L) * (r.B - r.T);
        if (c.ToString().StartsWith("WindowsForms") && area > best && r.B - r.T > 100) { best = area; found = h; } }
      return true; }, IntPtr.Zero);
    return found;
  }
  public static void Save(IntPtr h, string path) {
    RECT r; GetWindowRect(h, out r);
    using (var b = new System.Drawing.Bitmap(r.R - r.L, r.B - r.T)) {
      using (var g = System.Drawing.Graphics.FromImage(b)) { IntPtr dc = g.GetHdc(); PrintWindow(h, dc, 2); g.ReleaseHdc(dc); }
      b.Save(path);
    }
  }
}
'@
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exe = Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'
$box = Join-Path $root 'work\tests\menu-shot'
$out = Join-Path $root 'UI\stats-shots'
Get-Process ClaudeCodeTrafficLight -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
if (Test-Path $box) { Remove-Item $box -Recurse -Force }
New-Item -ItemType Directory -Force "$box\data", "$box\home\.claude\projects\demo", $out | Out-Null
[IO.File]::WriteAllText("$box\data\config.json", '{"FirstRunDone":true,"Language":"' + $Lang + '","StartupNotice":false,"Theme":"' + $Theme + '"}')
[IO.File]::WriteAllText("$box\home\.claude.json", '{"oauthAccount":{"organizationType":"claude_pro"}}')
# The made-up sessions of the self-check, written by the program's own code (loaded as a library).
$asm = [Reflection.Assembly]::LoadFrom($exe)
$flags = [Reflection.BindingFlags]'NonPublic,Public,Static'
$demo = $asm.GetType('Semaphore.SelfCheckDemo')
$ids = $demo.GetField('Ids', $flags).GetValue($null)
$names = $demo.GetField('Titles', $flags).GetValue($null)
$folders = $demo.GetField('Folders', $flags).GetValue($null)
$replies = $asm.GetType('Semaphore.SelfCheckSetup').GetMethod('DemoReplies', $flags)
$texts = @()
for ($i = 0; $i -lt $ids.Length; $i++) {
    $texts += '{"type":"ai-title","aiTitle":"' + $names[$i] + '","sessionId":"' + $ids[$i] + '"}' + "`n" + $replies.Invoke($null, @($i))
}
# The replies moved in time so that the last of them was an hour ago: the menu has a "today" to show.
$stamp = '"timestamp":"([^"]+)"'
$last = ($texts | ForEach-Object { [regex]::Matches($_, $stamp) } | ForEach-Object { [DateTime]::Parse($_.Groups[1].Value, $null, 'RoundtripKind').ToUniversalTime() } | Measure-Object -Maximum).Maximum
$shift = [DateTime]::UtcNow.AddHours(-1) - $last
for ($i = 0; $i -lt $ids.Length; $i++) {
    $text = [regex]::Replace($texts[$i], $stamp, { param($m) '"timestamp":"' + [DateTime]::Parse($m.Groups[1].Value, $null, 'RoundtripKind').ToUniversalTime().Add($shift).ToString('yyyy-MM-ddTHH:mm:ss.fffZ') + '"' })
    [IO.File]::WriteAllText("$box\home\.claude\projects\demo\$($ids[$i]).jsonl", $text, (New-Object Text.UTF8Encoding($false)))
}
# One exact figure of each limit, as if /usage had been run an hour ago.
$now = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
[IO.File]::WriteAllText("$box\data\usage-limits.json", '[{"AtMs":' + ($now - 3600000) + ',"Five":42,"FiveResetMs":' + ($now + 3 * 3600000) + ',"Week":63,"WeekResetMs":' + ($now + 3 * 86400000) + ',"CostVersion":3,"Plan":"claude_pro"}]')
$env:TRAFFICLIGHT_DATA_DIR = "$box\data"
$env:TRAFFICLIGHT_CLAUDE_DIR = "$box\home\.claude"
$app = Start-Process $exe -PassThru
try {
    Start-Sleep 8
    # The sessions as Claude Code's hooks would report them: one working, one done, one waiting for a permission.
    for ($i = 0; $i -lt $ids.Length; $i++) {
        $tp = "$box\home\.claude\projects\demo\$($ids[$i]).jsonl".Replace('\', '\\')
        $b = '"session_id":"' + $ids[$i] + '","cwd":"' + $folders[$i].Replace('\', '\\') + '","transcript_path":"' + $tp + '"'
        $events = @('{"hook_event_name":"UserPromptSubmit",' + $b + ',"prompt":"x"}')
        if ($i -eq 1) { $events += '{"hook_event_name":"Stop",' + $b + '}' }
        if ($i -eq 2) { $events += '{"hook_event_name":"Notification",' + $b + ',"notification_type":"permission_prompt","message":"Claude needs your permission to use Bash"}' }
        foreach ($e in $events) { $e | & $exe --hook | Out-Null }
    }
    Start-Sleep 4
    & $exe --menu | Out-Null
    Start-Sleep 2
    $h = [MenuShot]::Find([uint32]$app.Id)
    if ($h -eq [IntPtr]::Zero) { 'NO MENU'; return }
    [MenuShot]::Save($h, "$out\menu-$Lang-$Theme.png")
    "menu: $out\menu-$Lang-$Theme.png"
}
finally {
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    Remove-Item Env:TRAFFICLIGHT_DATA_DIR, Env:TRAFFICLIGHT_CLAUDE_DIR -ErrorAction SilentlyContinue
}
