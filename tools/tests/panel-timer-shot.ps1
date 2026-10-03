# The panel counts down the time left to answer: red in the last 15 seconds, and when the time is up it stays with a
# message instead of vanishing. Three pictures of the screen corner (after 5 s, 50 s and 66 s) in
# work\tests\panel-timer\ . The request lives one minute (RemoteApproveMinutes = 1). About 70 seconds.
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -Namespace TL -Name Dpi -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);'; [void][TL.Dpi]::SetProcessDpiAwarenessContext([IntPtr]-4)   # real pixels and coordinates at any display scale
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exe = Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'
$box = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'work\tests\panel-timer'
Get-Process ClaudeCodeTrafficLight -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
if (Test-Path $box) { Remove-Item $box -Recurse -Force }
New-Item -ItemType Directory -Force "$box\data", "$box\claude" | Out-Null
$utf8 = New-Object Text.UTF8Encoding $false
[IO.File]::WriteAllText("$box\data\config.json", '{"FirstRunDone":true,"Language":"en","StartupNotice":false,"LocalApprove":true,"PopupAsk":true,"RemoteApproveMinutes":1}', $utf8)
$env:TRAFFICLIGHT_DATA_DIR = "$box\data"
$env:TRAFFICLIGHT_CLAUDE_DIR = "$box\claude"
$app = Start-Process $exe -PassThru
Start-Sleep 3
'{"hook_event_name":"UserPromptSubmit","session_id":"tmr-1111-2222","cwd":"C:/Projects/demo","prompt":"x"}' | & $exe --hook trafficlight | Out-Null
$q = '{"hook_event_name":"PermissionRequest","session_id":"tmr-1111-2222","cwd":"C:/Projects/demo","tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"Which option do you choose?","header":"Choice","multiSelect":false,"options":[{"label":"First","description":"Option 1"},{"label":"Second","description":"Option 2"}]}]}}'
[IO.File]::WriteAllText("$box\q.json", $q, $utf8)
$waiter = Start-Process $exe -ArgumentList '--hook', 'trafficlight' -RedirectStandardInput "$box\q.json" -WindowStyle Hidden -PassThru
function Shot($name) {
    $s = [Windows.Forms.Screen]::PrimaryScreen.Bounds
    $b = New-Object Drawing.Bitmap 560, 520
    $g = [Drawing.Graphics]::FromImage($b); $g.CopyFromScreen($s.Right - 560, $s.Bottom - 580, 0, 0, $b.Size); $g.Dispose()
    $b.Save("$box\$name.png"); $b.Dispose()
}
try {
    Start-Sleep 5;  Shot 'a-start'
    Start-Sleep 45; Shot 'b-last-seconds'
    Start-Sleep 16; Shot 'c-expired'
    "pictures in $box"
}
finally {
    Stop-Process -Id $waiter.Id -Force -ErrorAction SilentlyContinue
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    $env:TRAFFICLIGHT_DATA_DIR = $null
    $env:TRAFFICLIGHT_CLAUDE_DIR = $null
}
