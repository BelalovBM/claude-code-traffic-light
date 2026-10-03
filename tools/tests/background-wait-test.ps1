# Claude stops its turn while a background task it started still runs (a build, tests) and continues when the
# task reports back. That stop is not "finished": no done notification, no push, the session stays working.
# 1) transcript with an unfinished background task + Stop -> still working, no push to the phone;
# 2) the task reports back, a new turn runs and stops -> finished, one push.
# Uses the mock ntfy server (mock_ntfy.py) and a sandbox.
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exe = Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'
$box = Join-Path $root 'work\tests\background-wait'
Get-Process ClaudeCodeTrafficLight -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
if (Test-Path $box) { Remove-Item $box -Recurse -Force }
New-Item -ItemType Directory -Force "$box\data", "$box\claude\projects\demo" | Out-Null
$utf8 = New-Object Text.UTF8Encoding $false
$server = 'http://127.0.0.1:18080'
$env:TRAFFICLIGHT_DATA_DIR = "$box\data"
$env:TRAFFICLIGHT_CLAUDE_DIR = "$box\claude"
$mock = $null; $app = $null
try {
    $mock = Start-Process python -ArgumentList "$PSScriptRoot\mock_ntfy.py" -WindowStyle Hidden -PassThru
    Start-Sleep 2
    $asm = [Reflection.Assembly]::LoadFrom($exe)
    $topic = $asm.GetType('Semaphore.Secret').GetMethod('Protect').Invoke($null, @('ccl-bgtest0000000000000001'))
    @{ FirstRunDone = $true; Language = 'en'; StartupNotice = $false; NtfyEnabled = $true; NtfyServer = $server; NtfyTopicProtected = $topic
       RemoteDone = $true; RemoteDoneMinMinutes = 0; PhoneOnlyIfIgnored = $false; ToastDone = $false; SoundDone = $false } |
        ConvertTo-Json | Set-Content "$box\data\config.json" -Encoding utf8
    $app = Start-Process $exe -PassThru
    Start-Sleep 3
    function Pushes() { @(((Invoke-WebRequest "$server/_published" -UseBasicParsing).Content | ConvertFrom-Json) | ForEach-Object { $_ }).Count }
    $tp = "$box\claude\projects\demo\bg-1111.jsonl"
    $base = '"session_id":"bg-1111","cwd":"C:\\fake\\Proj","transcript_path":"' + $tp.Replace('\', '\\') + '"'
    function Hook($json) { $json | & $exe --hook trafficlight | Out-Null; Start-Sleep -Milliseconds 600 }

    $started = '{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_x","content":"Command running in background with ID: task42."}]},"toolUseResult":{"backgroundTaskId":"task42"}}'
    [IO.File]::WriteAllText($tp, $started + "`n", $utf8)
    $n0 = Pushes
    Hook ('{"hook_event_name":"UserPromptSubmit",' + $base + ',"prompt":"build it"}')
    Hook ('{"hook_event_name":"Stop",' + $base + '}')
    Start-Sleep 2
    $log = Get-Content "$box\data\trafficlight.log"
    "1) waits for the background task: " + [bool]($log -match 'stopped to wait for 1 background') + ", pushes sent: " + ((Pushes) - $n0)

    $done = '{"type":"user","message":{"role":"user","content":"<task-notification>\n<task-id>task42</task-id>\n<status>completed</status>\n</task-notification>"}}'
    [IO.File]::AppendAllText($tp, $done + "`n", $utf8)
    Hook ('{"hook_event_name":"UserPromptSubmit",' + $base + ',"prompt":"<task-notification>"}')
    Hook ('{"hook_event_name":"Stop",' + $base + '}')
    Start-Sleep 2
    "2) after the task reported back and the turn ended: pushes sent: " + ((Pushes) - $n0)

    # 3) the limit: a task that never reports (a server left running) ends the wait, as "finished".
    [IO.File]::WriteAllText($tp, $started + "`n", $utf8)
    $parse = $asm.GetType('Semaphore.HookEvent').GetMethod('Parse')
    $store = [Activator]::CreateInstance($asm.GetType('Semaphore.SessionStore'))
    $apply = $store.GetType().GetMethod('Apply')
    [void]$apply.Invoke($store, @($parse.Invoke($null, @('{"hook_event_name":"UserPromptSubmit",' + $base + ',"prompt":"x"}'))))
    [void]$apply.Invoke($store, @($parse.Invoke($null, @('{"hook_event_name":"Stop",' + $base + '}'))))
    $s = $store.GetType().GetMethod('Find').Invoke($store, @('bg-1111'))
    $before = $store.GetType().GetMethod('ExpireBackground').Invoke($store, @()).Count
    $s.GetType().GetField('BackgroundUntil').SetValue($s, [DateTime]::Now.AddSeconds(-1))
    $changes = $store.GetType().GetMethod('ExpireBackground').Invoke($store, @())
    "3) limit: nothing before it ($before change(s)); after it: $($changes.Count) change(s), working -> " + $(if ($changes.Count) { $changes[0].New } else { '-' })
}
finally {
    if ($app) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    if ($mock) { Stop-Process -Id $mock.Id -Force -ErrorAction SilentlyContinue }
    $env:TRAFFICLIGHT_DATA_DIR = $null
    $env:TRAFFICLIGHT_CLAUDE_DIR = $null
}
