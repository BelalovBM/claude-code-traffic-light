Add-Type -AssemblyName System.Drawing,System.Windows.Forms
$sp = if ($args[0]) { $args[0] } else { Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'work\tests' }
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exe = Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'
$sandbox = "$sp\e2e"
Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$sandbox\data", "$sandbox\claude" | Out-Null
$env:TRAFFICLIGHT_DATA_DIR = "$sandbox\data"
$env:TRAFFICLIGHT_CLAUDE_DIR = "$sandbox\claude"
$server = "http://127.0.0.1:18080"
$mock = $null; $app = $null
try {
    $mock = Start-Process python -ArgumentList "$PSScriptRoot\mock_ntfy.py" -WindowStyle Hidden -PassThru
    Start-Sleep 2

    $asm = [Reflection.Assembly]::LoadFrom($exe)
    $secret = $asm.GetType("Semaphore.Secret")
    $prot = $secret.GetMethod("Protect")
    $ntfyTopic = $prot.Invoke($null, @("ccl-e2etopic0000000000000001"))
    $replyTopic = $prot.Invoke($null, @("ccr-e2ereply0000000000000001"))
    @{ FirstRunDone = $true; Language = "en"; NtfyEnabled = $true; NtfyServer = $server; NtfyTopicProtected = $ntfyTopic;
       RemoteApprove = $true; RemoteApproveMinutes = 1; ReplyTopicProtected = $replyTopic;
       MutedSessions = @("s-muted") } | ConvertTo-Json | Set-Content "$sandbox\data\config.json" -Encoding utf8

    $app = Start-Process $exe -PassThru
    Start-Sleep 3

    function RunHook($json) {
        $psi = New-Object Diagnostics.ProcessStartInfo $exe, "--hook trafficlight"
        $psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.CreateNoWindow = $true
        $p = [Diagnostics.Process]::Start($psi)
        $p.StandardInput.Write($json); $p.StandardInput.Close()
        $out = $p.StandardOutput.ReadToEndAsync()
        return @{ Proc = $p; Out = $out }
    }
    function PermissionPayload($sid) {
        '{"session_id":"' + $sid + '","cwd":"C:\\fake\\Proj","permission_mode":"default","hook_event_name":"PermissionRequest","tool_name":"Bash","tool_input":{"command":"rm -rf /tmp/build","description":"Clean"},"tool_use_id":"toolu_1"}'
    }
    function Published() { $o = (Invoke-WebRequest "$server/_published" -UseBasicParsing).Content | ConvertFrom-Json; @($o | ForEach-Object { $_ }) }
    function WaitPush($beforeCount) {
        for ($i = 0; $i -lt 40; $i++) {
            $all = @(Published)
            if ($all.Count -gt $beforeCount) { return $all[$all.Count - 1] }
            Start-Sleep -Milliseconds 250
        }
        return $null
    }
    function Tap($msg, $verb, $tokenOverride) {
        $a = $msg.actions | Where-Object { $_.body -like "$verb`:*" } | Select-Object -First 1
        $body = if ($tokenOverride) { "$verb`:$tokenOverride" } else { $a.body }
        Invoke-WebRequest $a.url -Method POST -Body $body -UseBasicParsing | Out-Null
        return $a
    }

    "=== A: allow from phone"
    $n0 = @(Published).Count
    $h = RunHook (PermissionPayload "s-allow-1111")
    $push = WaitPush $n0
    if (-not $push) { "NO PUSH RECEIVED" } else {
        "push title='$($push.title)' priority=$($push.priority) actions=" + (($push.actions | ForEach-Object { $_.label }) -join "/")
        "push body: " + ($push.message -replace "`n", " | ")
        $a = Tap $push "allow" $null
        $h.Proc.WaitForExit(10000) | Out-Null
        "hook exited=$($h.Proc.HasExited) stdout=" + $h.Out.Result
        "--- replay of the same token (must have no effect):"
        Tap $push "allow" $null | Out-Null
    }

    "=== B: deny from phone"
    $n0 = @(Published).Count
    $h = RunHook (PermissionPayload "s-deny-2222")
    $push = WaitPush $n0
    $null = Tap $push "deny" $null
    $h.Proc.WaitForExit(10000) | Out-Null
    "hook exited=$($h.Proc.HasExited) stdout=" + $h.Out.Result

    "=== C: wrong token is ignored, then timeout falls back to the normal prompt (about 60 s)"
    $n0 = @(Published).Count
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $h = RunHook (PermissionPayload "s-timeout-3333")
    $push = WaitPush $n0
    $null = Tap $push "allow" "00000000000000000000000000000000"
    Start-Sleep 3
    "after a wrong-token tap, hook still waiting: " + (-not $h.Proc.HasExited)
    $h.Proc.WaitForExit(90000) | Out-Null
    "hook exited=$($h.Proc.HasExited) after $([int]$sw.Elapsed.TotalSeconds) s, stdout=[" + $h.Out.Result + "]"

    "=== D: answered on the computer first (PostToolUse of the same session cancels the wait)"
    $n0 = @(Published).Count
    $h = RunHook (PermissionPayload "s-cancel-4444")
    $push = WaitPush $n0
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $post = RunHook '{"hook_event_name":"PostToolUse","session_id":"s-cancel-4444","cwd":"C:\\fake\\Proj","tool_name":"Bash"}'
    $h.Proc.WaitForExit(10000) | Out-Null
    "hook exited=$($h.Proc.HasExited) after $([int]$sw.ElapsedMilliseconds) ms, stdout=[" + $h.Out.Result + "]"

    "=== E: muted session is never asked"
    $n0 = @(Published).Count
    $h = RunHook (PermissionPayload "s-muted")
    $h.Proc.WaitForExit(10000) | Out-Null
    "hook exited=$($h.Proc.HasExited), stdout=[" + $h.Out.Result + "], new pushes=" + (@(Published).Count - $n0)

    "=== F: app not running -> hook exits at once with no output"
    Stop-Process -Id $app.Id -Force; Start-Sleep 1
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $h = RunHook (PermissionPayload "s-noapp-5555")
    $h.Proc.WaitForExit(10000) | Out-Null
    "hook exited=$($h.Proc.HasExited) after $([int]$sw.ElapsedMilliseconds) ms, stdout=[" + $h.Out.Result + "]"
}
finally {
    "--- sandbox log (approval lines):"
    Get-Content "$sandbox\data\trafficlight.log" -ErrorAction SilentlyContinue | Select-String -Pattern 'approval|Approval' | ForEach-Object { $_.Line }
    if ($app) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    if ($mock) { Stop-Process -Id $mock.Id -Force -ErrorAction SilentlyContinue }
}

