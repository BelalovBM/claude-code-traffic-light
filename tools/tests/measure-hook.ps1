# Measures how long a hook takes: with the tray app running and without it, for an ordinary event and for
# a tool event (the one Claude fires twice per tool call). Fails (exit code 1) when a run stalls (> 1 s),
# which is how the 2 s shutdown stall of the hook process showed up, or when a median is above its limit.
# Restores the tray app afterwards.
param([int]$Runs = 30)

$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exe = Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'
# A sandbox of its own: the program you use keeps running and gets none of the probe events.
$box = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'work\tests\measure'
New-Item -ItemType Directory -Force "$box\data", "$box\claude" | Out-Null
[IO.File]::WriteAllText("$box\data\config.json", '{"FirstRunDone":true,"StartupNotice":false}', (New-Object Text.UTF8Encoding $false))
$env:TRAFFICLIGHT_DATA_DIR = "$box\data"
$env:TRAFFICLIGHT_CLAUDE_DIR = "$box\claude"

# The parent waits for the exit right after closing stdin: that is the way the stall used to appear.
function Measure-Hook([string]$payload, [int]$n) {
    $times = @()
    for ($i = 0; $i -lt $n; $i++) {
        $psi = New-Object Diagnostics.ProcessStartInfo $exe, '--hook trafficlight'
        $psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.CreateNoWindow = $true
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $p = [Diagnostics.Process]::Start($psi)
        if ($payload) { $p.StandardInput.Write($payload) }
        $p.StandardInput.Close()
        $p.WaitForExit()
        $sw.Stop()
        $times += $sw.ElapsedMilliseconds
    }
    $sorted = $times | Sort-Object
    [pscustomobject]@{ Median = $sorted[[int]($sorted.Count / 2)]; Min = $sorted[0]; Max = $sorted[-1] }
}

$failed = 0
function Check([string]$label, [string]$payload, [int]$medianLimit) {
    $r = Measure-Hook $payload $Runs
    $stall = $r.Max -gt 1000
    $slow = $r.Median -gt $medianLimit
    if ($stall -or $slow) { $script:failed++ }
    '{0,-44} median {1,4} ms  min {2,4}  max {3,4}   {4}' -f $label, $r.Median, $r.Min, $r.Max,
        $(if ($stall) { 'FAIL: stalled run' } elseif ($slow) { "FAIL: median above $medianLimit ms" } else { 'ok' })
}

# "other" is an event the app ignores (no session appears); "tool" is what Claude fires twice per tool call.
$other = '{"hook_event_name":"MeasureProbe","session_id":"measure-probe","cwd":"C:\\x"}'
$tool = '{"hook_event_name":"PostToolUse","session_id":"measure-probe","cwd":"C:\\x","tool_name":"Bash"}'
$end = '{"hook_event_name":"SessionEnd","session_id":"measure-probe","cwd":"C:\\x"}'
Get-Process ClaudeCodeTrafficLight -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
Start-Process $exe; Start-Sleep 3
Check 'tray app running,     tool event' $tool 150
Check 'tray app running,     other event' $other 200
[void](Measure-Hook $end 1)   # removes the probe session the tool events created

Get-Process ClaudeCodeTrafficLight -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
Start-Sleep 1
Check 'tray app not running, tool event' $tool 150
Check 'tray app not running, other event' $other 250

$env:TRAFFICLIGHT_DATA_DIR = $null
$env:TRAFFICLIGHT_CLAUDE_DIR = $null
"$failed check(s) failed"
exit $(if ($failed -gt 0) { 1 } else { 0 })
