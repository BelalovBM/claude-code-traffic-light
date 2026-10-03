# The panel switched off, a question and then Claude Code's "waiting" notification: the toast with the Answer button
# must appear. Takes a picture of the screen corner: work\tests\toast-shot\toast.png
param([switch]$NoNotification)
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -Namespace TL -Name Dpi -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);'; [void][TL.Dpi]::SetProcessDpiAwarenessContext([IntPtr]-4)   # real pixels and coordinates at any display scale
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exe = Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'
$box = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'work\tests\toast-shot'
Get-Process ClaudeCodeTrafficLight -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
if (Test-Path $box) { Remove-Item $box -Recurse -Force }
New-Item -ItemType Directory -Force "$box\data", "$box\claude" | Out-Null
$utf8 = New-Object Text.UTF8Encoding $false
[IO.File]::WriteAllText("$box\data\config.json", '{"FirstRunDone":true,"Language":"en","StartupNotice":false,"LocalApprove":true,"PopupAsk":false,"ToastWaiting":true}', $utf8)
$env:TRAFFICLIGHT_DATA_DIR = "$box\data"
$env:TRAFFICLIGHT_CLAUDE_DIR = "$box\claude"
$app = Start-Process $exe -PassThru
Start-Sleep 3
'{"hook_event_name":"UserPromptSubmit","session_id":"tst-1111-2222","cwd":"C:/Projects/demo","prompt":"x"}' | & $exe --hook trafficlight | Out-Null
$q = '{"hook_event_name":"PermissionRequest","session_id":"tst-1111-2222","cwd":"C:/Projects/demo","tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"Which option do you choose?","header":"Choice","multiSelect":false,"options":[{"label":"First","description":"Option 1"},{"label":"Second","description":"Option 2"}]}]}}'
[IO.File]::WriteAllText("$box\q.json", $q, $utf8)
$waiter = Start-Process $exe -ArgumentList '--hook', 'trafficlight' -RedirectStandardInput "$box\q.json" -WindowStyle Hidden -PassThru
try {
    Start-Sleep 2
    if (-not $NoNotification) { '{"hook_event_name":"Notification","session_id":"tst-1111-2222","cwd":"C:/Projects/demo","notification_type":"permission_prompt","message":"Claude needs your permission to use AskUserQuestion"}' | & $exe --hook trafficlight | Out-Null }
    Start-Sleep 2
    $s = [Windows.Forms.Screen]::PrimaryScreen.Bounds
    $b = New-Object Drawing.Bitmap 560, 300
    $g = [Drawing.Graphics]::FromImage($b); $g.CopyFromScreen($s.Right - 560, $s.Bottom - 360, 0, 0, $b.Size); $g.Dispose()
    $b.Save("$box\toast.png"); $b.Dispose()
    Get-Content "$box\data\trafficlight.log" | Select-String 'Toast' | ForEach-Object { $_.Line }
    "picture: $box\toast.png"
}
finally {
    Stop-Process -Id $waiter.Id -Force -ErrorAction SilentlyContinue
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    $env:TRAFFICLIGHT_DATA_DIR = $null
    $env:TRAFFICLIGHT_CLAUDE_DIR = $null
}
