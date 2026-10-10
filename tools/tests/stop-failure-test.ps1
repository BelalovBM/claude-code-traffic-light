# A turn ended by an API error (StopFailure): the session turns red with "stopped by an error", a push with the
# kind of error and Claude's own words goes to a mock ntfy server; a connection made without StopFailure counts as
# outdated, and Connect adds it. Everything runs in a sandbox.
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exe = Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'
$box = Join-Path $root 'work\tests\stop-failure'
Get-Process ClaudeCodeTrafficLight -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
if (Test-Path $box) { Remove-Item $box -Recurse -Force }
New-Item -ItemType Directory -Force "$box\data", "$box\claude" | Out-Null
$env:TRAFFICLIGHT_DATA_DIR = "$box\data"
$env:TRAFFICLIGHT_CLAUDE_DIR = "$box\claude"
$server = "http://127.0.0.1:18080"
$mock = $null; $app = $null
try {
    # The connection as an older version made it: every event but StopFailure.
    $cmd = '"' + $exe.Replace('\', '/') + '" --hook trafficlight'
    $old = @{}
    foreach ($ev in 'SessionStart', 'UserPromptSubmit', 'PreToolUse', 'PostToolUse', 'Notification', 'Stop', 'SessionEnd', 'PreCompact', 'PostCompact') {
        $old[$ev] = @(@{ hooks = @(@{ type = 'command'; command = $cmd; timeout = 5 }) })
    }
    @{ hooks = $old } | ConvertTo-Json -Depth 6 | Set-Content "$box\claude\settings.json" -Encoding utf8
    $asm = [Reflection.Assembly]::LoadFrom($exe)
    $installer = $asm.GetType('Semaphore.HookInstaller')
    "status of an old connection: " + $installer.GetMethod('GetStatus').Invoke($null, @())
    $installer.GetMethod('Install').Invoke($null, @())
    "after Connect: " + $installer.GetMethod('GetStatus').Invoke($null, @()) + ", StopFailure in settings: " + ((Get-Content "$box\claude\settings.json" -Raw) -match '"StopFailure"')

    $mock = Start-Process python -ArgumentList "$PSScriptRoot\mock_ntfy.py" -WindowStyle Hidden -PassThru
    Start-Sleep 2
    $secret = $asm.GetType("Semaphore.Secret").GetMethod("Protect")
    @{ FirstRunDone = $true; Language = "en"; StartupNotice = $false; NtfyEnabled = $true; NtfyOwn = $true; NtfyOwnServer = $server; NtfyServer = $server;
       NtfyTopicProtected = $secret.Invoke($null, @("ccl-stopfail0000000000001")); PhoneOnlyIfIgnored = $false } |
        ConvertTo-Json | Set-Content "$box\data\config.json" -Encoding utf8
    $app = Start-Process $exe -PassThru
    Start-Sleep 3
    $b = '"session_id":"sf-2222","cwd":"C:\\fake\\Proj"'
    '{"hook_event_name":"UserPromptSubmit",' + $b + ',"prompt":"refactor"}' | & $exe --hook trafficlight | Out-Null
    '{"hook_event_name":"StopFailure",' + $b + ',"error":"rate_limit","error_details":"429","last_assistant_message":"You''ve hit your limit · resets 9pm"}' | & $exe --hook trafficlight | Out-Null
    Start-Sleep 3
    $pushes = @(((Invoke-WebRequest "$server/_published" -UseBasicParsing).Content | ConvertFrom-Json) | ForEach-Object { $_ })
    "pushes: $($pushes.Count)"
    $pushes | ForEach-Object { "  [$($_.title)] priority=$($_.priority) " + ($_.message -replace "`n", " | ") }
}
finally {
    "--- log:"
    Get-Content "$box\data\trafficlight.log" -ErrorAction SilentlyContinue | Select-String 'StopFailure|error|ntfy' | ForEach-Object { $_.Line }
    if ($app) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    if ($mock) { Stop-Process -Id $mock.Id -Force -ErrorAction SilentlyContinue }
    $env:TRAFFICLIGHT_DATA_DIR = $null; $env:TRAFFICLIGHT_CLAUDE_DIR = $null
}
