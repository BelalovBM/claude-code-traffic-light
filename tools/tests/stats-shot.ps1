# Pictures of the statistics window and of the settings page with the prices, from the built program in a sandbox
# (its own settings: no hooks, no phone, no background /usage). With -Real it reads the real transcripts and limits
# of this computer, read only; the pictures then show real session titles and stay on this computer.
# With -Load it also presses "Update prices from LiteLLM" (one request to GitHub), pictures the dialog and applies it.
#   powershell -File stats-shot.ps1 [-Real] [-Load] [-Lang ru] [-Theme dark]
param([switch]$Real, [switch]$Load, [string]$Lang = 'ru', [string]$Theme = 'dark')
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class Shot {
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
  [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
  public static void Save(IntPtr h, string path) {
    RECT r; GetWindowRect(h, out r);
    using (var b = new System.Drawing.Bitmap(r.R - r.L, r.B - r.T)) {
      using (var g = System.Drawing.Graphics.FromImage(b)) { IntPtr dc = g.GetHdc(); PrintWindow(h, dc, 2); g.ReleaseHdc(dc); }
      b.Save(path);
    }
  }
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
  delegate bool Cb(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr p, Cb cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
  // A button of the window, found by its text, pressed with a message to it (no mouse, no keys); posted, so that a
  // dialog it opens does not hold the script.
  public static bool Press(IntPtr top, string text) {
    IntPtr found = IntPtr.Zero;
    EnumChildWindows(top, (h, l) => { var t = new System.Text.StringBuilder(200); GetWindowText(h, t, 200);
      if (t.ToString() == text) { found = h; return false; } return true; }, IntPtr.Zero);
    return found != IntPtr.Zero && PostMessage(found, 0x00F5, IntPtr.Zero, IntPtr.Zero);
  }
  [DllImport("user32.dll")] static extern bool EnumWindows(Cb cb, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  // A visible window of the process by its title. Not through UI Automation: there a dialog is listed under the
  // window that owns it, not among the top-level ones.
  public static IntPtr Find(uint pid, string title) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p);
      if (p == pid && IsWindowVisible(h)) { var t = new System.Text.StringBuilder(200); GetWindowText(h, t, 200);
        if (t.ToString() == title) { found = h; return false; } }
      return true; }, IntPtr.Zero);
    return found;
  }
  public static void Size(IntPtr h, int w, int hh) { RECT r; GetWindowRect(h, out r); MoveWindow(h, r.L, 0, w, hh, true); }
}
'@
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exe = Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'
$box = Join-Path $root 'work\tests\stats-shot'
$out = Join-Path $root "UI\stats-shots"
Get-Process ClaudeCodeTrafficLight -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
if (Test-Path $box) { Remove-Item $box -Recurse -Force }
New-Item -ItemType Directory -Force "$box\data", $out | Out-Null
[IO.File]::WriteAllText("$box\data\config.json", '{"FirstRunDone":true,"Language":"' + $Lang + '","StartupNotice":false,"Theme":"' + $Theme + '"}')
if ($Real) {
    # The exact figures seen so far, so that the share of the limit is known.
    $installed = '$env:LOCALAPPDATA\ClaudeCodeTrafficLight\usage-limits.json'
    if (Test-Path $installed) { Copy-Item $installed "$box\data\" }
} else {
    New-Item -ItemType Directory -Force "$box\home\.claude" | Out-Null
    $env:TRAFFICLIGHT_CLAUDE_DIR = "$box\home\.claude"
}
$env:TRAFFICLIGHT_DATA_DIR = "$box\data"
$app = Start-Process $exe -PassThru
$A = [Windows.Automation.AutomationElement]
function Window($title, $seconds = 10) {
    for ($i = 0; $i -lt $seconds * 2; $i++) {
        $h = [Shot]::Find([uint32]$app.Id, $title)
        if ($h -ne [IntPtr]::Zero) { return $h }
        Start-Sleep -Milliseconds 500
    }
    return [IntPtr]::Zero
}
try {
    # The first reading of a month of transcripts takes a while.
    Start-Sleep 25
    & $exe --stats | Out-Null
    $titles = @{ ru = 'Статистика расхода'; en = 'Usage statistics' }
    $h = Window $titles[$Lang]
    if ($h -eq [IntPtr]::Zero) { 'NO STATS WINDOW'; return }
    Start-Sleep 2
    [Shot]::Size($h, 1000, 1400)
    Start-Sleep 2
    [Shot]::Save($h, "$out\stats-$Lang-$Theme.png")
    "stats: $out\stats-$Lang-$Theme.png"
    & $exe --page 1 | Out-Null
    Start-Sleep 12
    $s = Window ($(if ($Lang -eq 'ru') { 'Claude Code Traffic Light — Настройки' } else { 'Claude Code Traffic Light — Settings' }))
    if ($s -ne [IntPtr]::Zero) {
        Start-Sleep 2
        # The page scrolled to its end, where the card of the prices is, as the mouse wheel does it (a scroll
        # message from outside moves the bar but not the page). The window keeps its size.
        $win = $A::RootElement.FindFirst([Windows.Automation.TreeScope]::Children,
            [Windows.Automation.PropertyCondition]::new($A::NativeWindowHandleProperty, [int]$s))
        foreach ($e in $win.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
            $hw = [IntPtr]$e.Current.NativeWindowHandle
            if ($hw -ne [IntPtr]::Zero -and ([Shot]::GetWindowLong($hw, -16) -band 0x00200000)) {
                for ($i = 0; $i -lt 30; $i++) { [void][Shot]::SendMessage($hw, 0x020A, [IntPtr](-120 * 65536), [IntPtr]::Zero) }
            }
        }
        Start-Sleep 2
        [Shot]::Save($s, "$out\settings-$Lang-$Theme.png")
        "settings: $out\settings-$Lang-$Theme.png"
        if ($Load) {
            # The button and the dialog are pressed with messages to them: no mouse, no keys.
            if (-not [Shot]::Press($s, $(if ($Lang -eq 'ru') { 'Обновить цены из LiteLLM' } else { 'Update prices from LiteLLM' }))) { 'NO LOAD BUTTON' }
            $d = Window ($(if ($Lang -eq 'ru') { 'Цены из LiteLLM' } else { 'Prices from LiteLLM' })) 120
            if ($d -ne [IntPtr]::Zero) {
                Start-Sleep 1
                [Shot]::Save($d, "$out\prices-dialog-$Lang-$Theme.png")
                "dialog: $out\prices-dialog-$Lang-$Theme.png"
                if (-not [Shot]::Press($d, $(if ($Lang -eq 'ru') { 'Применить' } else { 'Apply' }))) { 'NO APPLY BUTTON' }
                Start-Sleep 2
                [Shot]::Save($s, "$out\settings-loaded-$Lang-$Theme.png")
                "after loading: $out\settings-loaded-$Lang-$Theme.png"
            } else { 'NO PRICES DIALOG (see the result line on the page)'; [Shot]::Save($s, "$out\settings-loaded-$Lang-$Theme.png") }
        }
    } else { 'NO SETTINGS WINDOW' }
}
finally {
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    Remove-Item Env:TRAFFICLIGHT_DATA_DIR, Env:TRAFFICLIGHT_CLAUDE_DIR -ErrorAction SilentlyContinue
}
