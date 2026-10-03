# The panel for a question, then Claude Code's own "waiting" notification a moment later: the pop-up for it
# must stay away (the panel already says it). Takes a picture of the screen corner: work\tests\askpanel-shot\panel.png
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -Namespace TL -Name Dpi -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);'; [void][TL.Dpi]::SetProcessDpiAwarenessContext([IntPtr]-4)   # real pixels and coordinates at any display scale
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exe = Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'
$box = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'work\tests\askpanel-shot'
Get-Process ClaudeCodeTrafficLight -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
if (Test-Path $box) { Remove-Item $box -Recurse -Force }
New-Item -ItemType Directory -Force "$box\data", "$box\claude" | Out-Null
$utf8 = New-Object Text.UTF8Encoding $false
[IO.File]::WriteAllText("$box\data\config.json", '{"FirstRunDone":true,"Language":"en","StartupNotice":false,"LocalApprove":true,"PopupAsk":true,"ToastWaiting":true}', $utf8)
$env:TRAFFICLIGHT_DATA_DIR = "$box\data"
$env:TRAFFICLIGHT_CLAUDE_DIR = "$box\claude"
$app = Start-Process $exe -PassThru
Start-Sleep 3
'{"hook_event_name":"UserPromptSubmit","session_id":"pan-1111-2222","cwd":"C:/Projects/demo","prompt":"x"}' | & $exe --hook trafficlight | Out-Null
$q = '{"hook_event_name":"PermissionRequest","session_id":"pan-1111-2222","cwd":"C:/Projects/demo","tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"Which option do you choose?","header":"Choice","multiSelect":false,"options":[{"label":"First","description":"Option 1"},{"label":"Second","description":"Option 2"}]}]}}'
[IO.File]::WriteAllText("$box\q.json", $q, $utf8)
$waiter = Start-Process $exe -ArgumentList '--hook', 'trafficlight' -RedirectStandardInput "$box\q.json" -WindowStyle Hidden -PassThru
try {
    Start-Sleep 2
    '{"hook_event_name":"Notification","session_id":"pan-1111-2222","cwd":"C:/Projects/demo","notification_type":"permission_prompt","message":"Claude needs your permission to use AskUserQuestion"}' | & $exe --hook trafficlight | Out-Null
    Start-Sleep 2
    $s = [Windows.Forms.Screen]::PrimaryScreen.Bounds
    $w = 840; $h = 900   # room for the panel at 150% display scale
    $b = New-Object Drawing.Bitmap $w, $h
    $g = [Drawing.Graphics]::FromImage($b); $g.CopyFromScreen($s.Right - $w, $s.Bottom - $h, 0, 0, $b.Size); $g.Dispose()
    $b.Save("$box\panel.png"); $b.Dispose()
    "picture: $box\panel.png"
}
finally {
    Stop-Process -Id $waiter.Id -Force -ErrorAction SilentlyContinue
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    $env:TRAFFICLIGHT_DATA_DIR = $null
    $env:TRAFFICLIGHT_CLAUDE_DIR = $null
}
