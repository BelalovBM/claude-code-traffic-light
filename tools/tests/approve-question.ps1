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

    function QuestionPayload($sid, $questions) {
        '{"session_id":"' + $sid + '","cwd":"C:/fake/Proj","permission_mode":"default","hook_event_name":"PermissionRequest","tool_name":"AskUserQuestion","tool_input":{"questions":' + $questions + '},"tool_use_id":"toolu_q"}'
    }
    $three = '[{"question":"Which format?","header":"Format","multiSelect":false,"options":[{"label":"Summary","description":"short"},{"label":"Detailed","description":"long"},{"label":"Table","description":"rows"}]}]'
    $two = '[{"question":"A?","header":"A","multiSelect":false,"options":[{"label":"x","description":""},{"label":"y","description":""}]},{"question":"B?","header":"B","multiSelect":false,"options":[{"label":"x","description":""},{"label":"y","description":""}]}]'
    $multi = '[{"question":"Pick several","header":"P","multiSelect":true,"options":[{"label":"x","description":""},{"label":"y","description":""}]}]'

    "=== Q1: one question, three options -> three buttons, the second is tapped"
    $n0 = @(Published).Count
    $h = RunHook (QuestionPayload "s-q1-aaaa" $three)
    $push = WaitPush $n0
    if (-not $push) { "NO PUSH RECEIVED" } else {
        "push title='$($push.title)' tags=$($push.tags -join ',') body='$($push.message)' buttons=" + (($push.actions | ForEach-Object { $_.label }) -join "/")
        $a = $push.actions | Where-Object { $_.label -eq '2) Detailed' } | Select-Object -First 1
        Invoke-WebRequest $a.url -Method POST -Body $a.body -UseBasicParsing | Out-Null
        $h.Proc.WaitForExit(15000) | Out-Null
        "hook exited=$($h.Proc.HasExited) stdout=" + $h.Out.Result
        "--- tapping a second time has no effect:"
        Invoke-WebRequest $a.url -Method POST -Body $a.body -UseBasicParsing | Out-Null
    }
    "=== Q2: two questions -> no push, normal prompt"
    $n0 = @(Published).Count
    $h = RunHook (QuestionPayload "s-q2-bbbb" $two)
    $h.Proc.WaitForExit(8000) | Out-Null
    "hook exited=$($h.Proc.HasExited), stdout=[" + $h.Out.Result + "], new pushes=" + (@(Published).Count - $n0)
    "=== Q3: multi-select -> no push, normal prompt"
    $n0 = @(Published).Count
    $h = RunHook (QuestionPayload "s-q3-cccc" $multi)
    $h.Proc.WaitForExit(8000) | Out-Null
    "hook exited=$($h.Proc.HasExited), stdout=[" + $h.Out.Result + "], new pushes=" + (@(Published).Count - $n0)
    "=== Q4: five options -> text with all of them, a note, and buttons for the first three only"
    $five = '[{"question":"Which one?","header":"Q","multiSelect":false,"options":[{"label":"A","description":"a"},{"label":"B","description":"b"},{"label":"C","description":"c"},{"label":"D","description":"d"},{"label":"E","description":"e"}]}]'
    $n0 = @(Published).Count
    $h = RunHook (QuestionPayload "s-five-eeee" $five)
    $push = WaitPush $n0
    if (-not $push) { "NO PUSH RECEIVED" } else {
        "buttons=" + (($push.actions | ForEach-Object { $_.label }) -join "/")
        "lists D and E: " + (($push.message -match '4\) D') -and ($push.message -match '5\) E'))
        "has the note about more than three: " + ($push.message -match '5 answers')
    }
    Stop-Process -Id $h.Proc.Id -Force -ErrorAction SilentlyContinue
}
finally {
    "--- sandbox log (question lines):"
    Get-Content "$sandbox\data	rafficlight.log" -ErrorAction SilentlyContinue | Select-String -Pattern 'approval|question' | ForEach-Object { $_.Line }
    if ($app) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    if ($mock) { Stop-Process -Id $mock.Id -Force -ErrorAction SilentlyContinue }
}
