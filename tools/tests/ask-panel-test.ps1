# The panel near the tray: a question arrives through the hook, the panel shows it, a click on an answer
# (made through UI Automation, so no real mouse is needed) sends that answer back to the waiting hook.
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exe = Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'
$box = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'work\tests\askpanel-click'
Get-Process ClaudeCodeTrafficLight -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
if (Test-Path $box) { Remove-Item $box -Recurse -Force }
New-Item -ItemType Directory -Force "$box\data", "$box\claude" | Out-Null
$utf8 = New-Object Text.UTF8Encoding $false
[IO.File]::WriteAllText("$box\data\config.json", '{"FirstRunDone":true,"Language":"en","StartupNotice":false,"LocalApprove":true,"PopupAsk":true}', $utf8)
$env:TRAFFICLIGHT_DATA_DIR = "$box\data"
$env:TRAFFICLIGHT_CLAUDE_DIR = "$box\claude"
$app = Start-Process $exe -PassThru
Start-Sleep 3
$q = '{"hook_event_name":"PermissionRequest","session_id":"clk-1111-2222","cwd":"C:/fake/Proj","tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"Which way?","header":"Way","multiSelect":false,"options":[{"label":"Long","description":"safe"},{"label":"Short","description":"fast"}]}]}}'
[IO.File]::WriteAllText("$box\q.json", $q, $utf8)
$psi = New-Object Diagnostics.ProcessStartInfo $exe, '--hook trafficlight'
$psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.CreateNoWindow = $true
$hook = [Diagnostics.Process]::Start($psi)
$hook.StandardInput.Write($q); $hook.StandardInput.Close()
$out = $hook.StandardOutput.ReadToEndAsync()
try {
    Start-Sleep 3
    $cond = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ProcessIdProperty), $app.Id
    $win = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, $cond)
    if (-not $win) { 'NO PANEL WINDOW'; return }
    $btnCond = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::NameProperty), 'Short'
    $btn = $win.FindFirst([Windows.Automation.TreeScope]::Descendants, $btnCond)
    if (-not $btn) { 'NO BUTTON'; return }
    # A real click at the centre of the button, but only if the window under that point is the panel.
    Add-Type -ReferencedAssemblies System.Drawing -Namespace T -Name W -MemberDefinition '[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern System.IntPtr WindowFromPoint(System.Drawing.Point p); [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint pid); [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y); [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, System.UIntPtr e);'
    $r = $btn.Current.BoundingRectangle
    $cx = [int]($r.X + $r.Width / 2); $cy = [int]($r.Y + $r.Height / 2)
    $h = [T.W]::WindowFromPoint((New-Object Drawing.Point $cx, $cy))
    $owner = 0; [void][T.W]::GetWindowThreadProcessId($h, [ref]$owner)
    $ownerProc = (Get-CimInstance Win32_Process -Filter "ProcessId=$owner").ProcessId
    if ($ownerProc -ne $app.Id) { "the point $cx,$cy is not over the panel (process $ownerProc)"; return }
    [void][T.W]::SetCursorPos($cx, $cy)
    [T.W]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); [T.W]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    $hook.WaitForExit(10000) | Out-Null
    'hook exited=' + $hook.HasExited + ' stdout=' + $out.Result
}
finally {
    if (-not $hook.HasExited) { Stop-Process -Id $hook.Id -Force -ErrorAction SilentlyContinue }
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    $env:TRAFFICLIGHT_DATA_DIR = $null
    $env:TRAFFICLIGHT_CLAUDE_DIR = $null
}
