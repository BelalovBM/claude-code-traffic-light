# Compaction states in a sandbox: PreCompact makes the session "compacting", PostCompact releases it
# (to working after an automatic one, to ready after /compact). The log shows each step.
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exe = Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'
$box = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'work\tests\compact'
Get-Process ClaudeCodeTrafficLight -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
if (Test-Path $box) { Remove-Item $box -Recurse -Force }
New-Item -ItemType Directory -Force "$box\data", "$box\claude" | Out-Null
$utf8 = New-Object Text.UTF8Encoding $false
[IO.File]::WriteAllText("$box\data\config.json", '{"FirstRunDone":true,"Language":"en","StartupNotice":false,"ToastCompact":false}', $utf8)
$env:TRAFFICLIGHT_DATA_DIR = "$box\data"
$env:TRAFFICLIGHT_CLAUDE_DIR = "$box\claude"
$app = Start-Process $exe -PassThru
Start-Sleep 3
function Hook($json) { $json | & $exe --hook trafficlight | Out-Null; Start-Sleep -Milliseconds 400 }
$base = '"session_id":"cmp-1111","cwd":"C:/fake/Proj"'
try {
    Hook ('{"hook_event_name":"UserPromptSubmit",' + $base + ',"prompt":"x"}')
    Hook ('{"hook_event_name":"PreCompact",' + $base + ',"trigger":"auto"}')
    Start-Sleep 1
    Hook ('{"hook_event_name":"PostCompact",' + $base + ',"trigger":"auto"}')
    Hook ('{"hook_event_name":"Stop",' + $base + '}')
    Hook ('{"hook_event_name":"PreCompact",' + $base + ',"trigger":"manual"}')
    Hook ('{"hook_event_name":"PostCompact",' + $base + ',"trigger":"manual"}')
}
finally {
    Get-Content "$box\data\trafficlight.log" | Select-String 'compact|\[cmp' | ForEach-Object { $_.Line }
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    $env:TRAFFICLIGHT_DATA_DIR = $null
    $env:TRAFFICLIGHT_CLAUDE_DIR = $null
}
