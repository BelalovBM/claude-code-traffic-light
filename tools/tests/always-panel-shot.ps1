# The panel for a permission prompt with Claude Code's "don't ask again" rule (in Russian). Takes a picture of the
# panel window only, nothing else on the screen: work\tests\always-shot\panel.png
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type -Namespace TL -Name Dpi -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);'; [void][TL.Dpi]::SetProcessDpiAwarenessContext([IntPtr]-4)   # real pixels and coordinates at any display scale
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exe = Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'
$box = Join-Path $root 'work\tests\always-shot'
Get-Process ClaudeCodeTrafficLight -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
if (Test-Path $box) { Remove-Item $box -Recurse -Force }
New-Item -ItemType Directory -Force "$box\data", "$box\claude" | Out-Null
$utf8 = New-Object Text.UTF8Encoding $false
[IO.File]::WriteAllText("$box\data\config.json", '{"FirstRunDone":true,"Language":"ru","StartupNotice":false,"LocalApprove":true,"PopupAsk":true,"ToastWaiting":true}', $utf8)
$env:TRAFFICLIGHT_DATA_DIR = "$box\data"
$env:TRAFFICLIGHT_CLAUDE_DIR = "$box\claude"
$app = Start-Process $exe -PassThru
Start-Sleep 3
'{"hook_event_name":"UserPromptSubmit","session_id":"pan-1111-2222","cwd":"C:/Projects/demo","prompt":"x"}' | & $exe --hook trafficlight | Out-Null
$q = '{"hook_event_name":"PermissionRequest","session_id":"pan-1111-2222","cwd":"C:/Projects/demo","tool_name":"Bash","tool_input":{"command":"./.venv/Scripts/python.exe -c \"import pypdf\""},"permission_suggestions":[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"./.venv/Scripts/python.exe:*"}],"behavior":"allow","destination":"projectSettings"}]}'
[IO.File]::WriteAllText("$box\q.json", $q, $utf8)
$waiter = Start-Process $exe -ArgumentList '--hook', 'trafficlight' -RedirectStandardInput "$box\q.json" -WindowStyle Hidden -PassThru
try {
    Start-Sleep 3
    $cond = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ProcessIdProperty), $app.Id
    $win = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, $cond)
    if (-not $win) { 'NO PANEL WINDOW'; return }
    $r = $win.Current.BoundingRectangle
    $b = New-Object Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
    $g = [Drawing.Graphics]::FromImage($b); $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $b.Size); $g.Dispose()
    $b.Save("$box\panel.png"); $b.Dispose()
    "picture: $box\panel.png"
}
finally {
    Stop-Process -Id $waiter.Id -Force -ErrorAction SilentlyContinue
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    $env:TRAFFICLIGHT_DATA_DIR = $null
    $env:TRAFFICLIGHT_CLAUDE_DIR = $null
}
