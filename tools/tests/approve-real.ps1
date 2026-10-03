Add-Type -AssemblyName System.Drawing,System.Windows.Forms
$sp = if ($args[0]) { $args[0] } else { Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'work\tests' }; $mode = $args[1]; $token = $args[2]
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exe = Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'
$sandbox = "$sp\real-$mode"
Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$sandbox\data", "$sandbox\claude" | Out-Null
$env:TRAFFICLIGHT_DATA_DIR = "$sandbox\data"
$env:TRAFFICLIGHT_CLAUDE_DIR = "$sandbox\claude"
$server = "http://127.0.0.1:18081"
$pushTopic = "ccl-real$mode" + "0000000000000001"
$replyTopic = "ccr-real$mode" + "0000000000000001"
$phoneAuth = @{ Authorization = "Basic " + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("bob:bobs-secret-pw")) }
$app = $null
function Code($scriptBlock) { try { & $scriptBlock; return 200 } catch { if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode } else { return $_.Exception.Message } } }
try {
    "=== server access rules (as seen by an anonymous client)"
    "anonymous publish to push topic -> " + (Code { Invoke-WebRequest "$server/$pushTopic" -Method POST -Body x -UseBasicParsing | Out-Null })
    "anonymous read of push topic    -> " + (Code { Invoke-WebRequest "$server/$pushTopic/json?poll=1" -UseBasicParsing | Out-Null })
    "anonymous write to reply topic  -> " + (Code { Invoke-WebRequest "$server/$replyTopic" -Method POST -Body probe -UseBasicParsing | Out-Null })
    "anonymous read of reply topic   -> " + (Code { Invoke-WebRequest "$server/$replyTopic/json?poll=1" -UseBasicParsing | Out-Null })

    $asm = [Reflection.Assembly]::LoadFrom($exe)
    $prot = $asm.GetType("Semaphore.Secret").GetMethod("Protect")
    $cfg = @{ FirstRunDone = $true; Language = "en"; NtfyEnabled = $true; NtfyServer = $server
              NtfyTopicProtected = $prot.Invoke($null, @($pushTopic))
              RemoteApprove = $true; RemoteApproveMinutes = 1; ReplyTopicProtected = $prot.Invoke($null, @($replyTopic)) }
    if ($mode -eq "basic") { $cfg.NtfyUser = "bob"; $cfg.NtfySecretProtected = $prot.Invoke($null, @("bobs-secret-pw")) }
    else { $cfg.NtfyUser = ""; $cfg.NtfySecretProtected = $prot.Invoke($null, @($token)) }
    $cfg | ConvertTo-Json | Set-Content "$sandbox\data\config.json" -Encoding utf8
    $app = Start-Process $exe -PassThru
    Start-Sleep 3

    function RunHook($json) {
        $psi = New-Object Diagnostics.ProcessStartInfo $exe, "--hook trafficlight"
        $psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.CreateNoWindow = $true
        $p = [Diagnostics.Process]::Start($psi)
        $p.StandardInput.Write($json); $p.StandardInput.Close()
        return @{ Proc = $p; Out = $p.StandardOutput.ReadToEndAsync() }
    }
    function PermissionPayload($sid) {
        '{"session_id":"' + $sid + '","cwd":"C:\\fake\\Proj","permission_mode":"default","hook_event_name":"PermissionRequest","tool_name":"Bash","tool_input":{"command":"rm -rf /tmp/build"},"tool_use_id":"toolu_1"}'
    }
    function Published() {
        $r = Invoke-WebRequest "$server/$pushTopic/json?poll=1&since=all" -Headers $phoneAuth -UseBasicParsing
        $msgs = @()
        $text = if ($r.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($r.Content) } else { [string]$r.Content }
        foreach ($line in ($text -split "`n")) { if ($line.Trim()) { $o = $line | ConvertFrom-Json; if ($o.event -eq "message") { $msgs += $o } } }
        return $msgs
    }
    function WaitPush([int]$before) {
        for ($i = 0; $i -lt 40; $i++) { $all = @(Published); if ($all.Count -gt $before) { return $all[$all.Count - 1] }; Start-Sleep -Milliseconds 250 }
        return $null
    }
    function Tap($msg, $verb) {
        $a = $msg.actions | Where-Object { $_.body -like "$verb*" } | Select-Object -First 1
        Invoke-WebRequest $a.url -Method POST -Body $a.body -UseBasicParsing | Out-Null
    }

    foreach ($case in 'allow', 'deny') {
        "=== [$mode] $case from the phone"
        $n0 = @(Published).Count
        $h = RunHook (PermissionPayload "s-$case-$mode")
        $push = WaitPush $n0
        if (-not $push) { "NO PUSH RECEIVED"; continue }
        "push: title='$($push.title)' body='$($push.message)' buttons=" + (($push.actions | ForEach-Object { $_.label }) -join "/")
        Tap $push $case
        $h.Proc.WaitForExit(10000) | Out-Null
        "hook stdout: " + $h.Out.Result
    }
}
finally {
    "--- app log (approval/ntfy lines):"
    Get-Content "$sandbox\data\trafficlight.log" -ErrorAction SilentlyContinue | Select-String -Pattern 'approval|ntfy|listener' | ForEach-Object { $_.Line }
    if ($app) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
}

