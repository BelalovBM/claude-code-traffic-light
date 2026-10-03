# The toast with the Answer button: pressing the button must open the panel with the question.
# The button is found through UI Automation (by its name, inside the shell's notification window) and
# pressed with the real mouse at its own centre.
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type -Namespace T -Name M -MemberDefinition '[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y); [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, System.UIntPtr e);'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exe = Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'
$box = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'work\tests\toastclick'
Get-Process ClaudeCodeTrafficLight -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
if (Test-Path $box) { Remove-Item $box -Recurse -Force }
New-Item -ItemType Directory -Force "$box\data", "$box\claude" | Out-Null
$utf8 = New-Object Text.UTF8Encoding $false
[IO.File]::WriteAllText("$box\data\config.json", '{"FirstRunDone":true,"Language":"en","StartupNotice":false,"LocalApprove":true,"PopupAsk":false,"ToastWaiting":true}', $utf8)
$env:TRAFFICLIGHT_DATA_DIR = "$box\data"
$env:TRAFFICLIGHT_CLAUDE_DIR = "$box\claude"
$app = Start-Process $exe -PassThru
Start-Sleep 3
'{"hook_event_name":"UserPromptSubmit","session_id":"tck-1111-2222","cwd":"C:/Projects/demo","prompt":"x"}' | & $exe --hook trafficlight | Out-Null
$q = '{"hook_event_name":"PermissionRequest","session_id":"tck-1111-2222","cwd":"C:/Projects/demo","tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"Click test question?","header":"Q","multiSelect":false,"options":[{"label":"A","description":"a"},{"label":"B","description":"b"}]}]}}'
[IO.File]::WriteAllText("$box\q.json", $q, $utf8)
$waiter = Start-Process $exe -ArgumentList '--hook', 'trafficlight' -RedirectStandardInput "$box\q.json" -WindowStyle Hidden -PassThru
try {
    Start-Sleep 2
    '{"hook_event_name":"Notification","session_id":"tck-1111-2222","cwd":"C:/Projects/demo","notification_type":"permission_prompt","message":"Claude needs your permission to use AskUserQuestion"}' | & $exe --hook trafficlight | Out-Null
    Start-Sleep 2
    # The toast has a fixed place in the corner (picture from toast-shot.ps1 taken earlier, 560x300 at the screen's
    # bottom right). The Answer button is at 362,267 in that picture; before clicking, the pixels there must look like
    # the button in the reference picture, so a click can never land on something else.
    $refFile = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'work\tests\toast-shot\toast.png'
    if (-not (Test-Path $refFile)) { 'run toast-shot.ps1 first (reference picture)'; return }
    $ref = New-Object Drawing.Bitmap $refFile
    $screen = [Windows.Forms.Screen]::PrimaryScreen.Bounds
    $now = New-Object Drawing.Bitmap 560, 300
    $g = [Drawing.Graphics]::FromImage($now); $g.CopyFromScreen($screen.Right - 560, $screen.Bottom - 360, 0, 0, $now.Size); $g.Dispose()
    $same = $true
    foreach ($pt in @(@(300, 267), @(362, 267), @(440, 267), @(250, 250), @(480, 285), @(240, 215))) {
        $c1 = $ref.GetPixel($pt[0], $pt[1]); $c2 = $now.GetPixel($pt[0], $pt[1])
        if ([Math]::Abs($c1.R - $c2.R) + [Math]::Abs($c1.G - $c2.G) + [Math]::Abs($c1.B - $c2.B) -gt 24) { $same = $false }
    }
    if (-not $same) { 'the toast is not where it was in the reference picture, no click'; return }
    $cx = $screen.Right - 560 + 362; $cy = $screen.Bottom - 360 + 267
    [void][T.M]::SetCursorPos($cx, $cy)
    [T.M]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); [T.M]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep 2
    $pc = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ProcessIdProperty), $app.Id
    $panel = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, $pc)
    'panel opened by the button: ' + [bool]$panel
}
finally {
    Stop-Process -Id $waiter.Id -Force -ErrorAction SilentlyContinue
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    $env:TRAFFICLIGHT_DATA_DIR = $null
    $env:TRAFFICLIGHT_CLAUDE_DIR = $null
}
