# Opens the settings window of the built program on a page in a sandbox, opens every "More" on it through the
# accessibility interface (no mouse, no keys) and says whether a part of the window then has a horizontal scroll bar.
#   powershell -File settings-width-check.ps1 [page] [language]
param([int]$Page = 1, [string]$Lang = 'ru', [int]$Narrower = 0)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -Namespace TL -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
[DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
'@
# The default action of a control through MSAA, as a screen reader does it: a link opens without a click.
Add-Type -ReferencedAssemblies Accessibility, System.Drawing -TypeDefinition @'
using System; using System.Runtime.InteropServices; using Accessibility;
public static class Msaa {
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
  // A picture of the window itself (not of the screen), saved as PNG.
  public static void Shot(IntPtr h, string path) {
    RECT r; GetWindowRect(h, out r);
    using (var b = new System.Drawing.Bitmap(r.R - r.L, r.B - r.T)) {
      using (var g = System.Drawing.Graphics.FromImage(b)) { IntPtr dc = g.GetHdc(); PrintWindow(h, dc, 2); g.ReleaseHdc(dc); }
      b.Save(path);
    }
  }
  [DllImport("oleacc.dll")] static extern int AccessibleObjectFromWindow(IntPtr h, uint id, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object o);
  public static bool Act(IntPtr h) {
    Guid iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
    object o;
    if (AccessibleObjectFromWindow(h, 0xFFFFFFFC, ref iid, out o) != 0) return false;
    var a = (IAccessible)o;
    for (int child = 1; child <= Math.Max(1, a.accChildCount); child++) {
      try { a.accDoDefaultAction(child); return true; } catch { }
    }
    try { a.accDoDefaultAction(0); return true; } catch { return false; }
  }
}
'@
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exe = Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'
$box = Join-Path $root 'work\tests\settings-width'
Get-Process ClaudeCodeTrafficLight -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
if (Test-Path $box) { Remove-Item $box -Recurse -Force }
New-Item -ItemType Directory -Force "$box\data", "$box\home\.claude" | Out-Null
[IO.File]::WriteAllText("$box\home\.claude.json", '{"oauthAccount":{"organizationType":"claude_pro"}}')
# CHECK_CLAUDE_PATH: a Claude Code path to put in the settings, to see how the page tells a missing file.
$claudePath = if ($env:CHECK_CLAUDE_PATH) { ',"LimitsAutoRefresh":true,"ClaudePath":"' + ($env:CHECK_CLAUDE_PATH -replace '\\', '\\') + '"' } else { '' }
[IO.File]::WriteAllText("$box\data\config.json", '{"FirstRunDone":true,"Language":"' + $Lang + '","StartupNotice":false,"Theme":"dark"' + $claudePath + '}')
$env:TRAFFICLIGHT_DATA_DIR = "$box\data"
$env:TRAFFICLIGHT_CLAUDE_DIR = "$box\home\.claude"
$app = Start-Process $exe -PassThru
$A = [Windows.Automation.AutomationElement]
function Bars($win) {
    $h = 0; $v = 0
    foreach ($e in $win.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
        $hwnd = [IntPtr]$e.Current.NativeWindowHandle
        if ($hwnd -eq [IntPtr]::Zero) { continue }
        $style = [TL.Win]::GetWindowLong($hwnd, -16)
        if ($style -band 0x00100000) { $h++ }
        if ($style -band 0x00200000) { $v++ }
    }
    "horizontal bars: $h, vertical bars: $v"
}
try {
    Start-Sleep 3
    & $exe --page $Page | Out-Null
    Start-Sleep 3
    $win = $A::RootElement.FindFirst([Windows.Automation.TreeScope]::Children,
        ([Windows.Automation.PropertyCondition]::new($A::ProcessIdProperty, $app.Id)))
    if (-not $win) { 'NO WINDOW'; return }
    if ($Narrower -gt 0) {
        # As if the window was dragged a few pixels narrower: the move, then the end of the drag, which rebuilds the page.
        $h = [IntPtr]$win.Current.NativeWindowHandle
        $r = New-Object TL.Win+RECT; [void][TL.Win]::GetWindowRect($h, [ref]$r)
        [void][TL.Win]::MoveWindow($h, $r.L, $r.T, $r.R - $r.L - $Narrower, $r.B - $r.T, $true)
        [void][TL.Win]::SendMessage($h, 0x0232, [IntPtr]::Zero, [IntPtr]::Zero)
        Start-Sleep 3
        # Then lower, without the end of a drag: the page keeps its width, and "More" makes it scroll.
        [void][TL.Win]::GetWindowRect($h, [ref]$r)
        [void][TL.Win]::MoveWindow($h, $r.L, $r.T, $r.R - $r.L, 640, $true)
        Start-Sleep 1
        "window made $Narrower px narrower, the page rebuilt, then the window lowered"
    }
    "page $Page closed: " + (Bars $win)
    $opened = 0
    foreach ($e in $win.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
        if (-not $e.Current.Name.Contains([string][char]0x25B8)) { continue }
        if ([Msaa]::Act([IntPtr]$e.Current.NativeWindowHandle)) { $opened++; Start-Sleep -Milliseconds 500 }
    }
    Start-Sleep 1
    "page $Page with $opened 'More' open: " + (Bars $win)
    # How far the widest control reaches against the right edge of the scrolling page, in pixels.
    $all = $win.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
    $scroller = $all | Where-Object { $_.Current.NativeWindowHandle -ne 0 -and ([TL.Win]::GetWindowLong([IntPtr]$_.Current.NativeWindowHandle, -16) -band 0x00200000) } | Select-Object -First 1
    if ($scroller) {
        $edge = $scroller.Current.BoundingRectangle.Right - 17
        $inside = $scroller.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
        $widest = ($inside | ForEach-Object { $_.Current.BoundingRectangle.Right } | Measure-Object -Maximum).Maximum
        "  room left at the right of the page: {0} px" -f [int]($edge - $widest)
    }
    [Msaa]::Shot([IntPtr]$win.Current.NativeWindowHandle, "$box\page$Page.png")
}
finally {
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    $env:TRAFFICLIGHT_DATA_DIR = $null
    $env:TRAFFICLIGHT_CLAUDE_DIR = $null
}

