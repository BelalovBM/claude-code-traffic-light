# An open tray menu must follow changes: a new session started while the menu is open appears in it
# without reopening. Screenshots of the same screen area before and after must differ, and the
# second one must be taller (one more session).
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -Namespace TL -Name Dpi -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);'; [void][TL.Dpi]::SetProcessDpiAwarenessContext([IntPtr]-4)   # real pixels and coordinates at any display scale
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exe = Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'
$box = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'work\tests\menulive'
Get-Process ClaudeCodeTrafficLight -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
if (Test-Path $box) { Remove-Item $box -Recurse -Force }
New-Item -ItemType Directory -Force "$box\data", "$box\claude" | Out-Null
$utf8 = New-Object Text.UTF8Encoding $false
[IO.File]::WriteAllText("$box\data\config.json", '{"FirstRunDone":true,"Language":"en","StartupNotice":false}', $utf8)
$env:TRAFFICLIGHT_DATA_DIR = "$box\data"
$env:TRAFFICLIGHT_CLAUDE_DIR = "$box\claude"
$app = Start-Process $exe -PassThru
Start-Sleep 3
function Hook($json) { $json | & $exe --hook trafficlight | Out-Null }
function Shot($name) {
    $b = New-Object Drawing.Bitmap 700, 700
    $g = [Drawing.Graphics]::FromImage($b)
    $g.CopyFromScreen(0, 0, 0, 0, $b.Size)
    $g.Dispose()
    $b.Save("$box\$name.png")
    $b.Dispose()
}
try {
    Hook '{"hook_event_name":"UserPromptSubmit","session_id":"live-aaaa","cwd":"C:/fake/One","prompt":"x"}'
    & $exe --menu | Out-Null
    Start-Sleep 2
    Shot 'before'
    Hook '{"hook_event_name":"UserPromptSubmit","session_id":"live-bbbb","cwd":"C:/fake/Two","prompt":"y"}'
    Start-Sleep 3
    Shot 'after'
    $a = (Get-FileHash "$box\before.png").Hash
    $b = (Get-FileHash "$box\after.png").Hash
    'screens differ after a session started while the menu was open: ' + ($a -ne $b)
    Get-Content "$box\data\trafficlight.log" | Select-String 'menu shot' | ForEach-Object { $_.Line }
}
finally {
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    $env:TRAFFICLIGHT_DATA_DIR = $null
    $env:TRAFFICLIGHT_CLAUDE_DIR = $null
}
