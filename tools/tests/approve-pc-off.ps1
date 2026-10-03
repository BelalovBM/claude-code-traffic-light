# With notifications on this computer switched off, a permission prompt must still reach the phone with its
# buttons, and the answer from there must come back to Claude Code. Uses the mock ntfy server (mock_ntfy.py).
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exe = Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'
$box = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'work\tests\pc-off'
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
    $asm = [Reflection.Assembly]::LoadFrom($exe)
    $prot = $asm.GetType("Semaphore.Secret").GetMethod("Protect")
    $ntfyTopic = $prot.Invoke($null, @("ccl-pcofftopic000000000000001"))
    $replyTopic = $prot.Invoke($null, @("ccr-pcoffreply000000000000001"))
    @{ FirstRunDone = $true; Language = "en"; StartupNotice = $false; NotificationsEnabled = $false;
       NtfyEnabled = $true; NtfyServer = $server; NtfyTopicProtected = $ntfyTopic;
       RemoteApprove = $true; RemoteApproveMinutes = 1; ReplyTopicProtected = $replyTopic; LocalApprove = $false } |
        ConvertTo-Json | Set-Content "$box\data\config.json" -Encoding utf8
    $app = Start-Process $exe -PassThru
    Start-Sleep 3

    $psi = New-Object Diagnostics.ProcessStartInfo $exe, "--hook trafficlight"
    $psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.CreateNoWindow = $true
    $before = @(((Invoke-WebRequest "$server/_published" -UseBasicParsing).Content | ConvertFrom-Json) | ForEach-Object { $_ }).Count
    $p = [Diagnostics.Process]::Start($psi)
    $p.StandardInput.Write('{"session_id":"pcoff-1111","cwd":"C:\\fake\\Proj","hook_event_name":"PermissionRequest","tool_name":"Bash","tool_input":{"command":"dir"},"tool_use_id":"toolu_1"}')
    $p.StandardInput.Close()
    $out = $p.StandardOutput.ReadToEndAsync()
    $push = $null
    for ($i = 0; $i -lt 40 -and -not $push; $i++) {
        $all = @(((Invoke-WebRequest "$server/_published" -UseBasicParsing).Content | ConvertFrom-Json) | ForEach-Object { $_ })
        if ($all.Count -gt $before) { $push = $all[$all.Count - 1] } else { Start-Sleep -Milliseconds 250 }
    }
    if (-not $push) { "FAIL: no push while notifications on this computer are off" }
    else {
        "push received, buttons: " + (($push.actions | ForEach-Object { $_.label }) -join "/")
        $a = $push.actions | Where-Object { $_.body -like "allow:*" } | Select-Object -First 1
        Invoke-WebRequest $a.url -Method POST -Body $a.body -UseBasicParsing | Out-Null
        $p.WaitForExit(10000) | Out-Null
        "hook answered: " + ($out.Result -like '*allow*') + "  stdout=" + $out.Result
    }
}
finally {
    if ($app) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    if ($mock) { Stop-Process -Id $mock.Id -Force -ErrorAction SilentlyContinue }
    $env:TRAFFICLIGHT_DATA_DIR = $null
    $env:TRAFFICLIGHT_CLAUDE_DIR = $null
}
