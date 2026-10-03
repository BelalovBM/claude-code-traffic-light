$sp = $args[0]; $uiRoot = $args[1]; $lang = $args[2]; $theme = $args[3]; $only = $args[4]
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -Namespace TL -Name Dpi -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);'; [void][TL.Dpi]::SetProcessDpiAwarenessContext([IntPtr]-4)   # real pixels and coordinates at any display scale
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System; using System.Drawing; using System.Runtime.InteropServices;
public class W15 {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out R r, int size);
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; }
  // The visible frame, without the invisible resize border that GetWindowRect includes.
  public static R Visible(IntPtr h) {
    R r;
    if (DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf(typeof(R))) != 0) GetWindowRect(h, out r);
    return r;
  }
  // The window's own picture (PrintWindow), so whatever lies on top of it on the screen is irrelevant.
  public static Bitmap Grab(IntPtr h) {
    R w; GetWindowRect(h, out w);
    R v = Visible(h);
    var full = new Bitmap(w.Rt - w.L, w.B - w.T);
    using (var g = Graphics.FromImage(full)) {
      IntPtr hdc = g.GetHdc();
      PrintWindow(h, hdc, 2);
      g.ReleaseHdc(hdc);
    }
    var rect = new Rectangle(v.L - w.L, v.T - w.T, v.Rt - v.L, v.B - v.T);
    var cropped = full.Clone(rect, full.PixelFormat);
    full.Dispose();
    return cropped;
  }
}
'@

function Round($bmp, $radius) {
    $res = New-Object Drawing.Bitmap $bmp.Width, $bmp.Height, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($res)
    $g.SmoothingMode = 'AntiAlias'
    $d = 2 * $radius
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($bmp.Width - $d - 1, 0, $d, $d, 270, 90)
    $path.AddArc($bmp.Width - $d - 1, $bmp.Height - $d - 1, $d, $d, 0, 90)
    $path.AddArc(0, $bmp.Height - $d - 1, $d, $d, 90, 90)
    $path.CloseFigure()
    $g.SetClip($path)
    $g.DrawImage($bmp, 0, 0)
    $g.Dispose()
    return $res
}
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exe = Join-Path $root 'bin\ClaudeCodeTrafficLight.exe'
$set = "$lang-$theme"
$out = "$uiRoot\$set"
$box = "$sp\shots\$set"
New-Item -ItemType Directory -Force $out | Out-Null
if (Test-Path $box) { Remove-Item $box -Recurse -Force }
$titles = (Get-Content "$PSScriptRoot\titles.json" -Raw -Encoding UTF8 | ConvertFrom-Json).$lang
$sids = @('11111111-aaaa-4aaa-8aaa-111111111111', '22222222-bbbb-4bbb-8bbb-222222222222', '33333333-cccc-4ccc-8ccc-333333333333')
$cwds = @('C:\Projects\my-app', 'C:\Projects\my-app', 'C:\Projects\docs-site')
$utf8 = New-Object Text.UTF8Encoding $false
$bgColor = if ($theme -eq 'dark') { [Drawing.Color]::FromArgb(32, 32, 32) } else { [Drawing.Color]::FromArgb(243, 243, 243) }
$failed = @()
$script:app = $null
$script:mock = $null

function Prepare($name) {
    $d = "$box\$name"
    New-Item -ItemType Directory -Force "$d\data", "$d\claude\projects\demo" | Out-Null
    for ($i = 0; $i -lt 3; $i++) {
        $line = (@{ type = 'ai-title'; aiTitle = $titles[$i]; sessionId = $sids[$i] } | ConvertTo-Json -Compress)
        [IO.File]::WriteAllText("$d\claude\projects\demo\$($sids[$i]).jsonl", $line + "`n", $utf8)
    }
    # settings.json with this app's hooks, so the connection page shows "connected"
    $cmd = '"' + $exe.Replace('\', '/') + '" --hook trafficlight'
    $hooks = @{}
    foreach ($ev in 'SessionStart', 'UserPromptSubmit', 'PreToolUse', 'PostToolUse', 'Notification', 'Stop', 'SessionEnd', 'PreCompact', 'PostCompact') {
        $g = @{ hooks = @(@{ type = 'command'; command = $cmd; timeout = 5 }) }
        if ($ev -eq 'PreToolUse' -or $ev -eq 'PostToolUse') { $g.matcher = '*' }
        $hooks[$ev] = @($g)
    }
    [IO.File]::WriteAllText("$d\claude\settings.json", (@{ hooks = $hooks } | ConvertTo-Json -Depth 8), $utf8)
    return $d
}

function BaseConfig() {
    return @{ FirstRunDone = $true; Language = $lang; Theme = $theme; NtfyEnabled = $true
              SoundWaiting = $false; ToastWaiting = $false; SoundDone = $false; ToastDone = $false
              MutedSessions = @($sids[1]) }
}

function StartApp($d, $cfg) {
    [IO.File]::WriteAllText("$d\data\config.json", ($cfg | ConvertTo-Json), $utf8)
    # No other instance may be running: it would answer the --page and --menu commands instead.
    Get-Process ClaudeCodeTrafficLight -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
    Start-Sleep 1
    $env:TRAFFICLIGHT_DATA_DIR = "$d\data"
    $env:TRAFFICLIGHT_CLAUDE_DIR = "$d\claude"
    $script:app = Start-Process $exe -PassThru
    Start-Sleep 3
    if ($script:app.HasExited) { throw "the sandbox instance did not start" }
}

function StopApp() {
    if ($script:app) { Stop-Process -Id $script:app.Id -Force -ErrorAction SilentlyContinue; $script:app = $null }
    Start-Sleep 1
}

function Hook($json) { $json | & $exe --hook trafficlight | Out-Null }

function Seed($d) {
    $claude = "$d\claude"
    for ($i = 0; $i -lt 3; $i++) {
        $tp = ("$claude\projects\demo\$($sids[$i]).jsonl").Replace('\', '\\')
        $cw = $cwds[$i].Replace('\', '\\')
        $base = '"session_id":"' + $sids[$i] + '","cwd":"' + $cw + '","transcript_path":"' + $tp + '"'
        Hook ('{"hook_event_name":"UserPromptSubmit",' + $base + ',"prompt":"x"}')
        if ($i -eq 0) { Hook ('{"hook_event_name":"PreToolUse",' + $base + ',"tool_name":"Edit","tool_input":{"file_path":"C:\\Projects\\docs-site\\content\\en\\changelog.md"}}') }
        if ($i -eq 1) { Hook ('{"hook_event_name":"Stop",' + $base + '}') }
        if ($i -eq 2) { Hook ('{"hook_event_name":"Notification",' + $base + ',"notification_type":"permission_prompt","message":"Claude needs your permission to use Bash"}') }
    }
    Start-Sleep 1.5
}

function IsBlank($bmp) {
    $seen = 0
    foreach ($fx in 0.1, 0.3, 0.5, 0.7, 0.9) { foreach ($fy in 0.2, 0.5, 0.8) {
        $c = $bmp.GetPixel([int]($bmp.Width * $fx), [int]($bmp.Height * $fy))
        if (($c.R + $c.G + $c.B) -gt 0) { $seen++ }
    } }
    return ($seen -eq 0)
}

function CaptureRect($l, $t, $w, $h) {
    $bmp = New-Object Drawing.Bitmap $w, $h
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($l, $t, 0, 0, $bmp.Size)
    $g.Dispose()
    return $bmp
}

function ShotPage($n, $file) {
    for ($try = 0; $try -lt 4; $try++) {
        & $exe --page $n | Out-Null
        Start-Sleep 2
        $p = Get-Process -Id $script:app.Id -ErrorAction SilentlyContinue
        if (-not $p -or $p.MainWindowHandle -eq [IntPtr]::Zero) { continue }
        $bmp = [W15]::Grab($p.MainWindowHandle)
        if (-not (IsBlank $bmp)) {
            $round = Round $bmp 8
            $round.Save("$out\$file")
            $round.Dispose(); $bmp.Dispose()
            return
        }
        $bmp.Dispose()
    }
    $script:failed += $file
}

function ShotMenu($d, $file) {
    for ($try = 0; $try -lt 3; $try++) {
        & $exe --menu | Out-Null
        Start-Sleep 2
        $logPath = "$d\data\trafficlight.log"
        $fs = [IO.File]::Open($logPath, 'Open', 'Read', 'ReadWrite')
        $text = (New-Object IO.StreamReader($fs)).ReadToEnd(); $fs.Dispose()
        $m = [regex]::Matches($text, 'menu shot: (-?\d+),(-?\d+),(\d+),(\d+) sub: (-?\d+),(-?\d+),(\d+),(\d+)')
        if ($m.Count -eq 0) { continue }
        $v = $m[$m.Count - 1].Groups
        $a = @([int]$v[1].Value, [int]$v[2].Value, [int]$v[3].Value, [int]$v[4].Value)
        $b = @([int]$v[5].Value, [int]$v[6].Value, [int]$v[7].Value, [int]$v[8].Value)
        $minX = $a[0]; $minY = $a[1]; $maxX = $a[0] + $a[2]; $maxY = $a[1] + $a[3]
        if ($b[2] -gt 0) {
            $minX = [Math]::Min($minX, $b[0]); $minY = [Math]::Min($minY, $b[1])
            $maxX = [Math]::Max($maxX, $b[0] + $b[2]); $maxY = [Math]::Max($maxY, $b[1] + $b[3])
        }
        $pad = 14
        $canvas = New-Object Drawing.Bitmap ($maxX - $minX + 2 * $pad), ($maxY - $minY + 2 * $pad)
        $g = [Drawing.Graphics]::FromImage($canvas)
        $g.Clear($bgColor)
        # each menu window is captured on its own, so nothing from the desktop behind leaks into the picture
        $rects = @(,$a)
        if ($b[2] -gt 0) { $rects += ,$b }
        $blank = $false
        foreach ($rc in $rects) {
            $piece = CaptureRect $rc[0] $rc[1] $rc[2] $rc[3]
            if (IsBlank $piece) { $blank = $true }
            $g.DrawImage($piece, $rc[0] - $minX + $pad, $rc[1] - $minY + $pad)
            $piece.Dispose()
        }
        $g.Dispose()
        if (-not $blank) { $canvas.Save("$out\$file"); $canvas.Dispose(); return }
        $canvas.Dispose()
    }
    $script:failed += $file
}

try {
    # --- main variant: every page, the tray menu
    if (-not $only) {
    $d = Prepare 'main'
    StartApp $d (BaseConfig)
    Seed $d
    ShotPage 0 '01-general.png'
    ShotPage 1 '02-notifications.png'
    ShotPage 2 '03-phone.png'
    ShotPage 3 '05-remote-approval.png'
    ShotPage 4 '07-claude-code.png'
    ShotPage 5 '08-about.png'
    ShotMenu $d '09-tray-menu.png'
    StopApp
    }

    # --- phone not set up yet: the first visit shows one button
    $d = Prepare 'fresh'
    $cfg = BaseConfig; $cfg.NtfyEnabled = $false
    StartApp $d $cfg
    ShotPage 2 '03b-phone-not-set-up.png'
    StopApp

    # --- a permission prompt waiting in the tray menu (answered from the menu, no phone)
    $d = Prepare 'localapp'
    $cfg = BaseConfig; $cfg.LocalApprove = $true
    StartApp $d $cfg
    Seed $d
    $tp = ("$d\claude\projects\demo\$($sids[2]).jsonl").Replace('\', '\\')
    $cw = $cwds[2].Replace('\', '\\')
    $req = '{"hook_event_name":"PermissionRequest","session_id":"' + $sids[2] + '","cwd":"' + $cw + '","transcript_path":"' + $tp + '","tool_name":"Bash","tool_input":{"command":"git push origin main --force-with-lease && npm run deploy -- --env production"}}'
    [IO.File]::WriteAllText("$d\permission.json", $req, $utf8)
    $waiter = Start-Process $exe -ArgumentList '--hook', 'trafficlight' -RedirectStandardInput "$d\permission.json" -WindowStyle Hidden -PassThru
    Start-Sleep 2
    ShotMenu $d '12-tray-menu-approval.png'
    StopApp
    Stop-Process -Id $waiter.Id -Force -ErrorAction SilentlyContinue

    # --- own server (advanced section open)
    $d = Prepare 'own'
    $cfg = BaseConfig; $cfg.NtfyServer = 'https://ntfy.example.org'; $cfg.NtfyUser = 'alice'
    StartApp $d $cfg
    ShotPage 2 '04-phone-own-server.png'
    StopApp

    # --- Claude Code also installed inside WSL: the connection page says those sessions are out of reach
    $d = Prepare 'wsl'
    $env:TRAFFICLIGHT_WSL_FOUND = 'Ubuntu'
    StartApp $d (BaseConfig)
    ShotPage 4 '07b-claude-code-wsl.png'
    StopApp
    $env:TRAFFICLIGHT_WSL_FOUND = $null

    # --- the phone already confirmed by a test: the steps and the QR code are folded away
    $d = Prepare 'tested'
    $asm = [Reflection.Assembly]::LoadFrom($exe)
    $prot = $asm.GetType('Semaphore.Secret').GetMethod('Protect')
    $topic = 'ccl-demotopic00000000000001'
    $sha = [Security.Cryptography.SHA256]::Create()
    $hash = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes("https://ntfy.sh|$topic")), 0, 12)).Replace('-', '')
    $cfg = BaseConfig; $cfg.NtfyTopicProtected = $prot.Invoke($null, @($topic)); $cfg.NtfyTestedHash = $hash
    StartApp $d $cfg
    ShotPage 2 '03c-phone-connected.png'
    StopApp

    if ($only) { "set $set -> only the own-server page"; return }
    # --- remote approval switched on (a local stand-in server makes the status "listening")
    $script:mock = Start-Process python -ArgumentList "$root\tools\tests\mock_ntfy.py" -WindowStyle Hidden -PassThru
    Start-Sleep 2
    $d = Prepare 'approve'
    $cfg = BaseConfig
    $cfg.NtfyServer = 'http://127.0.0.1:18080'
    $cfg.RemoteApprove = $true
    $cfg.RemoteApproveMinutes = 5
    $cfg.ReplyTopicProtected = $prot.Invoke($null, @('ccr-demoreply000000000000001'))
    StartApp $d $cfg
    Start-Sleep 3
    ShotPage 3 '06-remote-approval-enabled.png'
    StopApp
}
finally {
    StopApp
    if ($script:mock) { Stop-Process -Id $script:mock.Id -Force -ErrorAction SilentlyContinue }
    $env:TRAFFICLIGHT_DATA_DIR = $null
    $env:TRAFFICLIGHT_CLAUDE_DIR = $null
}
"set $set -> app pages done; failed: [" + ($failed -join ', ') + "]"
