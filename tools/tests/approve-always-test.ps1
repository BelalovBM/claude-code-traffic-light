# "Allow and don't ask again" and the end of the red light after an answer, in a sandbox with a mock ntfy server:
#  A  the phone gets three buttons; "Always" gives the hook Claude Code's own rules back as updatedPermissions;
#  B  the same answer from the panel near the tray (pressed through UI Automation, no mouse or keys);
#  C  "Allow" in the panel: the session is working again at once, not when the command ends;
#  D  the hook is stopped (as Claude Code does when the prompt is answered in its window): the wait ends too;
#  E  a prompt without suggestions keeps two buttons.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exe = Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'
$box = Join-Path $root 'work\tests\approve-always'
Get-Process ClaudeCodeTrafficLight -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
if (Test-Path $box) { Remove-Item $box -Recurse -Force }
New-Item -ItemType Directory -Force "$box\data", "$box\claude" | Out-Null
$env:TRAFFICLIGHT_DATA_DIR = "$box\data"
$env:TRAFFICLIGHT_CLAUDE_DIR = "$box\claude"
$server = "http://127.0.0.1:18080"
$mock = $null; $app = $null
try {
    $mock = Start-Process python -ArgumentList "$PSScriptRoot\mock_ntfy.py" -WindowStyle Hidden -PassThru
    Start-Sleep 2
    $secret = ([Reflection.Assembly]::LoadFrom($exe)).GetType("Semaphore.Secret").GetMethod("Protect")
    @{ FirstRunDone = $true; Language = "en"; StartupNotice = $false; NtfyEnabled = $true; NtfyOwn = $true; NtfyOwnServer = $server; NtfyServer = $server;
       NtfyTopicProtected = $secret.Invoke($null, @("ccl-alwaystopic00000000001")); ReplyTopicProtected = $secret.Invoke($null, @("ccr-alwaysreply00000000001"));
       RemoteApprove = $true; RemoteApproveMinutes = 2; LocalApprove = $true; PopupAsk = $true; PhoneOnlyIfIgnored = $false } |
        ConvertTo-Json | Set-Content "$box\data\config.json" -Encoding utf8
    $app = Start-Process $exe -PassThru
    Start-Sleep 3

    function RunHook($json) {
        $psi = New-Object Diagnostics.ProcessStartInfo $exe, "--hook trafficlight"
        $psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.CreateNoWindow = $true
        $p = [Diagnostics.Process]::Start($psi)
        $p.StandardInput.Write($json); $p.StandardInput.Close()
        @{ Proc = $p; Out = $p.StandardOutput.ReadToEndAsync() }
    }
    $suggest = ',"permission_suggestions":[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"npm test:*"}],"behavior":"allow","destination":"localSettings"}]'
    function Payload($sid, $extra) {
        '{"session_id":"' + $sid + '","cwd":"C:\\fake\\Proj","permission_mode":"default","hook_event_name":"PermissionRequest","tool_name":"Bash","tool_input":{"command":"npm test"},"tool_use_id":"toolu_1"' + $extra + '}'
    }
    function Published() { @(((Invoke-WebRequest "$server/_published" -UseBasicParsing).Content | ConvertFrom-Json) | ForEach-Object { $_ }) }
    function WaitPush($before) {
        for ($i = 0; $i -lt 40; $i++) { $all = @(Published); if ($all.Count -gt $before) { return $all[$all.Count - 1] }; Start-Sleep -Milliseconds 250 }
    }
    function Press($name) {
        $cond = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ProcessIdProperty), $app.Id
        for ($i = 0; $i -lt 20; $i++) {
            foreach ($w in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $cond)) {
                $b = $w.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::NameProperty), $name))
                if ($b) { $b.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true }
            }
            Start-Sleep -Milliseconds 250
        }
        $false
    }

    "=== A: three buttons on the phone, 'Always' returns the rules"
    $n0 = @(Published).Count
    $h = RunHook (Payload "aaaa-phone" $suggest)
    $push = WaitPush $n0
    "buttons: " + (($push.actions | ForEach-Object { $_.label }) -join " / ")
    "body: " + ($push.message -replace "`n", " | ")
    $a = $push.actions | Where-Object { $_.body -like "always:*" } | Select-Object -First 1
    Invoke-WebRequest $a.url -Method POST -Body $a.body -UseBasicParsing | Out-Null
    $h.Proc.WaitForExit(10000) | Out-Null
    "hook stdout: " + $h.Out.Result

    "=== B: 'Allow and don't ask again' in the panel"
    $h = RunHook (Payload "bbbb-panel" $suggest)
    Start-Sleep 2
    "pressed: " + (Press "Allow and don't ask again")
    $h.Proc.WaitForExit(10000) | Out-Null
    "hook stdout: " + $h.Out.Result

    "=== C: 'Allow' in the panel ends the red light at once"
    $h = RunHook (Payload "cccc-allow" "")
    Start-Sleep 2
    "pressed: " + (Press "Allow")
    $h.Proc.WaitForExit(10000) | Out-Null
    "hook stdout: " + $h.Out.Result

    "=== D: the hook is stopped"
    $h = RunHook (Payload "dddd-killed" "")
    Start-Sleep 2
    Stop-Process -Id $h.Proc.Id -Force
    Start-Sleep 3

    "=== E: no suggestions, two buttons"
    $n0 = @(Published).Count
    $h = RunHook (Payload "eeee-plain" "")
    $push = WaitPush $n0
    "buttons: " + (($push.actions | ForEach-Object { $_.label }) -join " / ")
    $d = $push.actions | Where-Object { $_.body -like "deny:*" } | Select-Object -First 1
    Invoke-WebRequest $d.url -Method POST -Body $d.body -UseBasicParsing | Out-Null
    $h.Proc.WaitForExit(10000) | Out-Null
    "hook stdout: " + $h.Out.Result
}
finally {
    Start-Sleep 1
    "--- sandbox log:"
    Get-Content "$box\data\trafficlight.log" -ErrorAction SilentlyContinue | Select-String -Pattern 'approval|answered|alert' | ForEach-Object { $_.Line }
    if ($app) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    if ($mock) { Stop-Process -Id $mock.Id -Force -ErrorAction SilentlyContinue }
    $env:TRAFFICLIGHT_DATA_DIR = $null; $env:TRAFFICLIGHT_CLAUDE_DIR = $null
}
