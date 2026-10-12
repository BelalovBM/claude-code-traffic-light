# Pictures of the program's notices for the readme and the project page (English, made-up project "demo-app"):
# the panel with a permission prompt, and the Windows notifications "finished" (with what was done), "needs you" (with
# the Answer button) and "stopped because of an error". Each notification is taken by the bounds of its own window,
# found through UI Automation, so nothing else on the screen gets into the picture. Output: work\notices\*.png
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type -Namespace TL -Name Dpi -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);'; [void][TL.Dpi]::SetProcessDpiAwarenessContext([IntPtr]-4)
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exe = Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'
$out = Join-Path $root 'work\notices'
$utf8 = New-Object Text.UTF8Encoding $false
if (Test-Path $out) { [IO.Directory]::Delete($out, $true) }
New-Item -ItemType Directory -Force $out | Out-Null
$sid = '44444444-dddd-4ddd-8ddd-444444444444'

function Start-Box($name, $config) {
    Get-Process ClaudeCodeTrafficLight -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
    $d = "$out\box-$name"
    New-Item -ItemType Directory -Force "$d\data", "$d\claude\projects\demo" | Out-Null
    [IO.File]::WriteAllText("$d\claude\projects\demo\$sid.jsonl", '{"type":"ai-title","aiTitle":"Add tests for the export","sessionId":"' + $sid + '"}' + "`n", $utf8)
    [IO.File]::WriteAllText("$d\data\config.json", $config, $utf8)
    $env:TRAFFICLIGHT_DATA_DIR = "$d\data"; $env:TRAFFICLIGHT_CLAUDE_DIR = "$d\claude"
    $script:app = Start-Process $exe -PassThru
    Start-Sleep 3
    return $d
}
function Stop-Box() { Stop-Process -Id $script:app.Id -Force -ErrorAction SilentlyContinue; $env:TRAFFICLIGHT_DATA_DIR = $null; $env:TRAFFICLIGHT_CLAUDE_DIR = $null; Start-Sleep 1 }
function Hook($d, $rest) {
    $tp = "$d\claude\projects\demo\$sid.jsonl".Replace('\', '\\')
    ('{' + $rest + ',"session_id":"' + $sid + '","cwd":"C:\\Projects\\demo-app","transcript_path":"' + $tp + '"}') | & $exe --hook trafficlight | Out-Null
}
function Grab($rect, $file) {
    $b = New-Object Drawing.Bitmap ([int]$rect.Width), ([int]$rect.Height)
    $g = [Drawing.Graphics]::FromImage($b); $g.CopyFromScreen([int]$rect.X, [int]$rect.Y, 0, 0, $b.Size); $g.Dispose()
    $b.Save("$out\$file"); $b.Dispose(); "picture: $file"
}
# The Windows notification on the screen: a shell window of its own, found by its name in the language of Windows.
function Shot-Toast($file) {
    for ($i = 0; $i -lt 30; $i++) {
        foreach ($w in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)) {
            $n = $w.Current.Name
            if ($w.Current.ClassName -eq 'Windows.UI.Core.CoreWindow' -and ($n -eq 'New notification' -or $n -eq 'Новое уведомление')) {
                Start-Sleep -Milliseconds 700   # the slide-in animation
                Grab $w.Current.BoundingRectangle $file
                return
            }
        }
        Start-Sleep -Milliseconds 300
    }
    "no notification window found for ${file}: take it by hand now (Win+Shift+S)"; Start-Sleep 14
}
$quiet = '"FirstRunDone":true,"Language":"en","Theme":"dark","StartupNotice":false,"SoundWaiting":false,"SoundDone":false'

try {
    # 1. finished, with the first line of what Claude said
    $d = Start-Box 'done' ('{' + $quiet + ',"ToastDone":true,"ToastWaiting":true}')
    Hook $d '"hook_event_name":"UserPromptSubmit","prompt":"x"'
    Start-Sleep 2
    Hook $d '"hook_event_name":"Stop","last_assistant_message":"Added tests for the CSV and JSON export; all 13 pass.\n\nDetails below."'
    Shot-Toast 'toast-done.png'
    Stop-Box
    Start-Sleep 6

    # 2. needs you: a permission prompt with the Answer button (the panel switched off)
    $d = Start-Box 'waiting' ('{' + $quiet + ',"ToastWaiting":true,"LocalApprove":true,"PopupAsk":false}')
    Hook $d '"hook_event_name":"UserPromptSubmit","prompt":"x"'
    $q = '{"hook_event_name":"PermissionRequest","session_id":"' + $sid + '","cwd":"C:\\Projects\\demo-app","tool_name":"Bash","tool_input":{"command":"npm test -- --coverage"}}'
    [IO.File]::WriteAllText("$d\q.json", $q, $utf8)
    $waiter = Start-Process $exe -ArgumentList '--hook', 'trafficlight' -RedirectStandardInput "$d\q.json" -WindowStyle Hidden -PassThru
    Shot-Toast 'toast-waiting.png'
    Stop-Process -Id $waiter.Id -Force -ErrorAction SilentlyContinue
    Stop-Box
    Start-Sleep 6

    # 3. stopped because of an error, with Claude's own words
    $d = Start-Box 'error' ('{' + $quiet + ',"ToastWaiting":true}')
    Hook $d '"hook_event_name":"UserPromptSubmit","prompt":"x"'
    Hook $d '"hook_event_name":"StopFailure","error":"rate_limit","last_assistant_message":"You have hit your limit, resets 9pm"'
    Shot-Toast 'toast-error.png'
    Stop-Box
    Start-Sleep 6

    # 4. the panel near the tray with a permission prompt and Claude Code's own "don't ask again" rule
    $d = Start-Box 'panel' ('{' + $quiet + ',"ToastWaiting":true,"LocalApprove":true,"PopupAsk":true}')
    Hook $d '"hook_event_name":"UserPromptSubmit","prompt":"x"'
    $q = '{"hook_event_name":"PermissionRequest","session_id":"' + $sid + '","cwd":"C:\\Projects\\demo-app","tool_name":"Bash","tool_input":{"command":"npm test -- --coverage"},"permission_suggestions":[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"npm test:*"}],"behavior":"allow","destination":"localSettings"}]}'
    [IO.File]::WriteAllText("$d\q.json", $q, $utf8)
    $waiter = Start-Process $exe -ArgumentList '--hook', 'trafficlight' -RedirectStandardInput "$d\q.json" -WindowStyle Hidden -PassThru
    Start-Sleep 3
    $cond = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ProcessIdProperty), $script:app.Id
    $win = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, $cond)
    if ($win) { Grab $win.Current.BoundingRectangle 'panel-permission.png' } else { 'no panel' }
    Stop-Process -Id $waiter.Id -Force -ErrorAction SilentlyContinue
    Stop-Box
}
finally {
    if ($script:app) { Stop-Process -Id $script:app.Id -Force -ErrorAction SilentlyContinue }
    $env:TRAFFICLIGHT_DATA_DIR = $null; $env:TRAFFICLIGHT_CLAUDE_DIR = $null
}
