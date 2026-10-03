# The second click of a double click on the tray icon must not press an item of the menu that has just opened
# (Exit was the usual victim). The menu is opened with --menu, "Exit" is pressed at once through UI Automation (the
# program has to stay), and again after a second (now it has to quit).
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exe = Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'
$box = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'work\tests\menuguard'
Get-Process ClaudeCodeTrafficLight -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
if (Test-Path $box) { Remove-Item $box -Recurse -Force }
New-Item -ItemType Directory -Force "$box\data", "$box\claude" | Out-Null
[IO.File]::WriteAllText("$box\data\config.json", '{"FirstRunDone":true,"Language":"en","StartupNotice":false}', (New-Object Text.UTF8Encoding $false))
$env:TRAFFICLIGHT_DATA_DIR = "$box\data"
$env:TRAFFICLIGHT_CLAUDE_DIR = "$box\claude"
$app = Start-Process $exe -PassThru
Start-Sleep 3
function PressExit {
    $cond = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ProcessIdProperty), $app.Id
    foreach ($w in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $cond)) {
        $nameCond = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::NameProperty), 'Exit'
        $item = $w.FindFirst([Windows.Automation.TreeScope]::Descendants, $nameCond)
        if ($item) {
            try { $item.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true } catch { return $false }
        }
    }
    return $false
}
try {
    & $exe --menu | Out-Null
    Start-Sleep -Milliseconds 150
    $pressed = PressExit
    Start-Sleep 1
    'Exit pressed right after opening (found: ' + $pressed + '): program still running = ' + (-not $app.HasExited)
    # the click closed the menu; a fresh program, the menu open for a while, then Exit
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep 1
    $app = Start-Process $exe -PassThru
    Start-Sleep 3
    & $exe --menu | Out-Null
    Start-Sleep -Milliseconds 1200
    $pressed2 = PressExit
    Start-Sleep 2
    'Exit pressed 1.2 s after opening (found: ' + $pressed2 + '): program quit = ' + $app.HasExited
}
finally {
    if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    $env:TRAFFICLIGHT_DATA_DIR = $null
    $env:TRAFFICLIGHT_CLAUDE_DIR = $null
}
