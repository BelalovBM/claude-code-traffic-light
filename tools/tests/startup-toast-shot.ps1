# The startup notice and the compaction notices are Windows notifications that close by themselves, not tray
# balloons (closing a balloon early makes the tray icon blink). Starts a sandbox instance with the startup notice
# on and takes a picture of the screen corner: work\tests\startup-toast\toast.png. The log must not say
# "Toast failed".
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -Namespace TL -Name Dpi -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);'; [void][TL.Dpi]::SetProcessDpiAwarenessContext([IntPtr]-4)   # real pixels and coordinates at any display scale
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exe = Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'
$box = Join-Path $root 'work\tests\startup-toast'
Get-Process ClaudeCodeTrafficLight -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
if (Test-Path $box) { Remove-Item $box -Recurse -Force }
New-Item -ItemType Directory -Force "$box\data", "$box\claude" | Out-Null
[IO.File]::WriteAllText("$box\data\config.json", '{"FirstRunDone":true,"Language":"en","StartupNotice":true}', (New-Object Text.UTF8Encoding $false))
$env:TRAFFICLIGHT_DATA_DIR = "$box\data"
$env:TRAFFICLIGHT_CLAUDE_DIR = "$box\claude"
$app = Start-Process $exe -PassThru
try {
    Start-Sleep 3
    $s = [Windows.Forms.Screen]::PrimaryScreen.Bounds
    $b = New-Object Drawing.Bitmap 520, 300
    $g = [Drawing.Graphics]::FromImage($b); $g.CopyFromScreen($s.Right - 520, $s.Bottom - 300, 0, 0, $b.Size); $g.Dispose()
    $b.Save("$box\toast.png"); $b.Dispose()
    # An empty sandbox may write no log at all; then nothing failed either.
    $log = "$box\data\trafficlight.log"
    'balloon fallback used: ' + ((Test-Path $log) -and [bool](Select-String -Path $log -Pattern 'Toast failed' -Quiet))
}
finally {
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    $env:TRAFFICLIGHT_DATA_DIR = $null
    $env:TRAFFICLIGHT_CLAUDE_DIR = $null
}
