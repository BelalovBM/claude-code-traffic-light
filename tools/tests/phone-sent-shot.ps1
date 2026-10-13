# The panel after its request went to the phone (a local mock ntfy server, nothing leaves the computer), in Russian.
# Takes a picture of the panel window only: work\tests\phone-sent\panel.png
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type -Namespace TL -Name Dpi -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);'; [void][TL.Dpi]::SetProcessDpiAwarenessContext([IntPtr]-4)
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exe = Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'
$box = Join-Path $root 'work\tests\phone-sent'
Get-Process ClaudeCodeTrafficLight -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
if (Test-Path $box) { Remove-Item $box -Recurse -Force }
New-Item -ItemType Directory -Force "$box\data", "$box\claude" | Out-Null
$env:TRAFFICLIGHT_DATA_DIR = "$box\data"
$env:TRAFFICLIGHT_CLAUDE_DIR = "$box\claude"
$server = "http://127.0.0.1:18080"
$mock = Start-Process python -ArgumentList "$PSScriptRoot\mock_ntfy.py" -WindowStyle Hidden -PassThru
$app = $null; $waiter = $null
try {
    Start-Sleep 2
    $secret = [Reflection.Assembly]::LoadFrom($exe).GetType("Semaphore.Secret").GetMethod("Protect")
    @{ FirstRunDone = $true; Language = "ru"; StartupNotice = $false; NtfyEnabled = $true; NtfyServer = $server;
       NtfyTopicProtected = $secret.Invoke($null, @("ccl-shot000000000000000000001")); ReplyTopicProtected = $secret.Invoke($null, @("ccr-shot000000000000000000001"));
       RemoteApprove = $true; LocalApprove = $true; PopupAsk = $true; RemoteWaitingAfterMinutes = 1; RemoteApproveMinutes = 5 } |
        ConvertTo-Json | Set-Content "$box\data\config.json" -Encoding utf8
    $app = Start-Process $exe -PassThru
    Start-Sleep 3
    '{"hook_event_name":"UserPromptSubmit","session_id":"ps-1111-2222","cwd":"C:/Projects/demo-app","prompt":"x"}' | & $exe --hook trafficlight | Out-Null
    $q = '{"hook_event_name":"PermissionRequest","session_id":"ps-1111-2222","cwd":"C:/Projects/demo-app","tool_name":"Bash","tool_input":{"command":"npm test -- --coverage"},"tool_use_id":"toolu_1"}'
    [IO.File]::WriteAllText("$box\q.json", $q, (New-Object Text.UTF8Encoding $false))
    $waiter = Start-Process $exe -ArgumentList '--hook', 'trafficlight' -RedirectStandardInput "$box\q.json" -WindowStyle Hidden -PassThru
    # The panel left untouched for about a minute: then the request goes to the phone and the panel says so.
    Start-Sleep ([int]($env:SHOT_WAIT + 0) + 75)
    $cond = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ProcessIdProperty), $app.Id
    $win = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, $cond)
    if (-not $win) { 'NO PANEL WINDOW'; return }
    $r = $win.Current.BoundingRectangle
    $b = New-Object Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
    $g = [Drawing.Graphics]::FromImage($b); $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $b.Size); $g.Dispose()
    $b.Save("$box\panel.png"); $b.Dispose()
    if ($env:SHOT_AGAIN) { Start-Sleep 5; $r = $win.Current.BoundingRectangle; $b = New-Object Drawing.Bitmap ([int]$r.Width), ([int]$r.Height); $g = [Drawing.Graphics]::FromImage($b); $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $b.Size); $g.Dispose(); $b.Save("$box\panel2.png"); $b.Dispose() }
    "picture: $box\panel.png"
}
finally {
    if ($waiter) { Stop-Process -Id $waiter.Id -Force -ErrorAction SilentlyContinue }
    if ($app) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    Stop-Process -Id $mock.Id -Force -ErrorAction SilentlyContinue
    $env:TRAFFICLIGHT_DATA_DIR = $null
    $env:TRAFFICLIGHT_CLAUDE_DIR = $null
}
