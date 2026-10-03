$sp = $args[0]; $uiRoot = $args[1]; $lang = $args[2]; $theme = $args[3]
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -Namespace TL -Name Dpi -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);'; [void][TL.Dpi]::SetProcessDpiAwarenessContext([IntPtr]-4)   # real pixels and coordinates at any display scale
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System; using System.Drawing; using System.Runtime.InteropServices;
public class W16 {
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out R r, int size);
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; }
  static R Visible(IntPtr h) {
    R r;
    if (DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf(typeof(R))) != 0) GetWindowRect(h, out r);
    return r;
  }
  // The window's own picture (PrintWindow), independent of what lies on top of it on screen.
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
$out = "$uiRoot\$lang-$theme"
New-Item -ItemType Directory -Force $out | Out-Null
$asm = [Reflection.Assembly]::LoadFrom($exe)
$asm.GetType('Semaphore.Loc').GetMethod('Load').Invoke($null, @($lang)) | Out-Null
$asm.GetType('Semaphore.Theme').GetMethod('Update').Invoke($null, @($theme)) | Out-Null
$failed = @()

function Snap($form, $file) {
    $form.TopMost = $true
    $form.Show()
    for ($i = 0; $i -lt 8; $i++) { [Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 150 }
    $bmp = [W16]::Grab($form.Handle)
    $c = $bmp.GetPixel([int]($bmp.Width / 2), [int]($bmp.Height / 2))
    if (($c.R + $c.G + $c.B) -gt 0) {
        $round = Round $bmp 8
        $round.Save("$out\$file")
        $round.Dispose()
    } else { $script:failed += $file }
    $bmp.Dispose()
    $form.Close()
}

$cfg = [Activator]::CreateInstance($asm.GetType('Semaphore.Config'))
$cfg.Language = $lang
$welcome = [Activator]::CreateInstance($asm.GetType('Semaphore.WelcomeForm'), @($cfg, $null, [Action]{ }))
Snap $welcome '10-welcome.png'

$demoTopic = $asm.GetType('Semaphore.Secret').GetMethod('RandomTopic').Invoke($null, @('ccl-'))
$dlg = [Activator]::CreateInstance($asm.GetType('Semaphore.TopicDialog'), @($demoTopic, $null))
Snap $dlg '11-topic-dialog.png'
"dialogs $lang-$theme done; failed: [" + ($failed -join ', ') + "]"
