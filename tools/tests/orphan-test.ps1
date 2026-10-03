# A session found already waiting (the program was not running when it asked) must turn red, and the
# question it waits for is read from its transcript and shown in the read-only panel.
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -Namespace TL -Name Dpi -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);'; [void][TL.Dpi]::SetProcessDpiAwarenessContext([IntPtr]-4)   # real pixels and coordinates at any display scale
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exe = Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'
$box = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'work\tests\orphan'
Get-Process ClaudeCodeTrafficLight -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
if (Test-Path $box) { Remove-Item $box -Recurse -Force }
New-Item -ItemType Directory -Force "$box\data", "$box\claude\sessions", "$box\claude\projects\demo" | Out-Null
$utf8 = New-Object Text.UTF8Encoding $false
[IO.File]::WriteAllText("$box\data\config.json", '{"FirstRunDone":true,"Language":"en","StartupNotice":false,"LocalApprove":true,"PopupAsk":true}', $utf8)

# a live process stands in for Claude Code
$me = Get-Process -Id $PID
$now = [long]([DateTime]::UtcNow - (New-Object DateTime 1970, 1, 1)).TotalMilliseconds
$sid = 'orph-1111-2222-3333-444455556666'
$reg = '{"pid":' + $PID + ',"sessionId":"' + $sid + '","cwd":"C:\\fake\\Proj","procStart":"' + $me.StartTime.ToFileTimeUtc() + '","kind":"interactive","status":"waiting","statusUpdatedAt":' + $now + ',"updatedAt":' + $now + '}'
[IO.File]::WriteAllText("$box\claude\sessions\$PID.json", $reg, $utf8)
$ask = '{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_orph","name":"AskUserQuestion","input":{"questions":[{"question":"Which way do we go?","header":"Way","multiSelect":false,"options":[{"label":"The long way","description":"Safer, takes more time."},{"label":"The short way","description":"Faster, riskier."}]}]}}]}}'
[IO.File]::WriteAllText("$box\claude\projects\demo\$sid.jsonl", $ask + "`n", $utf8)

$env:TRAFFICLIGHT_DATA_DIR = "$box\data"
$env:TRAFFICLIGHT_CLAUDE_DIR = "$box\claude"
$app = Start-Process $exe -PassThru
try {
    Start-Sleep 9
    $s = [Windows.Forms.Screen]::PrimaryScreen.Bounds
    $b = New-Object Drawing.Bitmap 560, 500
    $g = [Drawing.Graphics]::FromImage($b); $g.CopyFromScreen($s.Right - 560, $s.Bottom - 560, 0, 0, $b.Size); $g.Dispose()
    $b.Save("$box\panel.png"); $b.Dispose()
    Get-Content "$box\data\trafficlight.log" | Select-String 'found a running|transcript|waiting' | ForEach-Object { $_.Line }
}
finally {
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    $env:TRAFFICLIGHT_DATA_DIR = $null
    $env:TRAFFICLIGHT_CLAUDE_DIR = $null
}
