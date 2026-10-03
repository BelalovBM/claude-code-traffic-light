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
        '{"session_id":"' + $sid + '","cwd":"C:/fake/Proj","permission_mode":"default","hook_event_name":"PermissionRequest","tool_name":"Bash","tool_input":{"command":"rm -rf /tmp/build","description":"Clean"},"tool_use_id":"toolu_1"}'
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

    function QuestionPayload($sid) {
        '{"session_id":"' + $sid + '","cwd":"C:/fake/Proj","permission_mode":"default","hook_event_name":"PermissionRequest","tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"Which?","header":"Q","multiSelect":false,"options":[{"label":"A","description":"a"},{"label":"B","description":"b"}]}]},"tool_use_id":"toolu_q"}'
    }
    "=== F1: nobody answers -> after the time ran out the phone is told"
    $n0 = @(Published).Count
    $h = RunHook (QuestionPayload "s-expire-aa")
    $first = WaitPush $n0
    "first push: " + $first.title
    $n1 = @(Published).Count
    $second = $null
    for ($i = 0; $i -lt 90 -and -not $second; $i++) { Start-Sleep 1; if (@(Published).Count -gt $n1) { $second = (Published)[-1] } }
    "follow-up after about " + (8 + $i) + " s: title='" + $second.title + "' body='" + ($second.message -replace "`n", ' | ') + "'"
    Stop-Process -Id $h.Proc.Id -Force -ErrorAction SilentlyContinue
}
finally {
    "--- sandbox log:"
    Get-Content "$sandbox\data\trafficlight.log" -ErrorAction SilentlyContinue | Select-String -Pattern 'approval|reacted' | ForEach-Object { $_.Line }
    if ($app) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    if ($mock) { Stop-Process -Id $mock.Id -Force -ErrorAction SilentlyContinue }
}
