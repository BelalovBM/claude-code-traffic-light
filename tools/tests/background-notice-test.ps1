# A turn that ends only to wait for its own background task: the lamp is the clock one, and after the set minutes
# (1 here) one notice goes to the computer and one push to the phone (a mock ntfy server), not more.
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exe = Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'
$box = Join-Path $root 'work\tests\background-notice'
Get-Process ClaudeCodeTrafficLight -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
if (Test-Path $box) { Remove-Item $box -Recurse -Force }
New-Item -ItemType Directory -Force "$box\data", "$box\claude\projects\p" | Out-Null
$env:TRAFFICLIGHT_DATA_DIR = "$box\data"
$env:TRAFFICLIGHT_CLAUDE_DIR = "$box\claude"
$server = "http://127.0.0.1:18080"
$mock = $null; $app = $null
try {
    $mock = Start-Process python -ArgumentList "$PSScriptRoot\mock_ntfy.py" -WindowStyle Hidden -PassThru
    Start-Sleep 2
    $secret = ([Reflection.Assembly]::LoadFrom($exe)).GetType("Semaphore.Secret").GetMethod("Protect")
    @{ FirstRunDone = $true; Language = "en"; StartupNotice = $false; NtfyEnabled = $true; NtfyOwn = $true; NtfyOwnServer = $server; NtfyServer = $server;
       NtfyTopicProtected = $secret.Invoke($null, @("ccl-bgnotice0000000000001")); PhoneOnlyIfIgnored = $false; BackgroundNotifyMinutes = 1 } |
        ConvertTo-Json | Set-Content "$box\data\config.json" -Encoding utf8
    $tp = "$box\claude\projects\p\bg-1111.jsonl"
    $started = '{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"Command running in background with ID: job7."}]},"toolUseResult":{"backgroundTaskId":"job7"}}'
    [IO.File]::WriteAllText($tp, $started + "`n", (New-Object Text.UTF8Encoding $false))
    $app = Start-Process $exe -PassThru
    Start-Sleep 3
    $b = '"session_id":"bg-1111","cwd":"C:\\fake\\Proj","transcript_path":"' + $tp.Replace('\', '\\') + '"'
    '{"hook_event_name":"UserPromptSubmit",' + $b + ',"prompt":"run the benchmark"}' | & $exe --hook trafficlight | Out-Null
    '{"hook_event_name":"Stop",' + $b + '}' | & $exe --hook trafficlight | Out-Null
    "waiting 75 s for the notice..."
    Start-Sleep 75
    $pushes = @(((Invoke-WebRequest "$server/_published" -UseBasicParsing).Content | ConvertFrom-Json) | ForEach-Object { $_ })
    "pushes: $($pushes.Count)"
    $pushes | ForEach-Object { "  [$($_.title)] " + ($_.message -replace "`n", " | ") }
    Start-Sleep 15
    "pushes 15 s later (still one): " + @(((Invoke-WebRequest "$server/_published" -UseBasicParsing).Content | ConvertFrom-Json) | ForEach-Object { $_ }).Count
}
finally {
    "--- log:"
    Get-Content "$box\data\trafficlight.log" -ErrorAction SilentlyContinue | Select-String 'background|ntfy' | ForEach-Object { $_.Line }
    if ($app) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    if ($mock) { Stop-Process -Id $mock.Id -Force -ErrorAction SilentlyContinue }
    $env:TRAFFICLIGHT_DATA_DIR = $null; $env:TRAFFICLIGHT_CLAUDE_DIR = $null
}
