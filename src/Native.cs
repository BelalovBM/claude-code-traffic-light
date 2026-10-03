using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Semaphore
{
    static class Native
    {
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint attach, uint to, bool on);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);

        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
        static readonly IntPtr PerMonitorV2 = new IntPtr(-4), SystemAware = new IntPtr(-2);

        sealed class DpiContext : IDisposable
        {
            readonly IntPtr old;
            public DpiContext(IntPtr context)
            {
                try { old = SetThreadDpiAwarenessContext(context); }
                catch (EntryPointNotFoundException) { }   // older than Windows 10 1607: everything stays system-aware
            }
            public void Dispose()
            {
                if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old);
            }
        }

        // Top-level windows created inside are told about the scale of the monitor they are on and draw
        // themselves for it (the settings window); the rest of the program stays system-aware.
        public static IDisposable PerMonitorWindows() { return new DpiContext(PerMonitorV2); }

        // For a dialog opened from such a window: drawn for the system scale and stretched by Windows on another
        // monitor, rather than too small.
        public static IDisposable SystemAwareWindows() { return new DpiContext(SystemAware); }

        // The DPI of the monitor a window is on, for a per-monitor window; 0 where Windows cannot say.
        public static int WindowDpi(IntPtr h)
        {
            try { return (int)GetDpiForWindow(h); }
            catch (EntryPointNotFoundException) { return 0; }
        }

        // Brings a window forward on an explicit request of the user (the keyboard shortcut). Windows refuses that
        // to a program that did not get the last input, so for the moment of the switch this thread shares the
        // input state of the window that is in front.
        public static void Activate(IntPtr h)
        {
            if (SetForegroundWindow(h) && GetForegroundWindow() == h) return;
            uint front = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
            uint me = GetCurrentThreadId();
            bool attached = front != 0 && front != me && AttachThreadInput(me, front, true);
            try { BringWindowToTop(h); SetForegroundWindow(h); }
            finally { if (attached) AttachThreadInput(me, front, false); }
        }
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct NotifyIconData
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

        // A tray balloon with its own picture instead of the system warning/info sign, so the pop-up shows the
        // same lamp as the tray icon. WinForms has no way to ask for that, hence the direct call; the
        // window and id of the icon are taken from the NotifyIcon. Returns false if that did not work.
        public static bool Balloon(System.Windows.Forms.NotifyIcon icon, string title, string text, IntPtr largeIcon)
        {
            try
            {
                IntPtr hWnd; int id;
                if (!IconOwner(icon, out hWnd, out id)) return false;
                var data = new NotifyIconData
                {
                    hWnd = hWnd,
                    uID = id,
                    uFlags = 0x10,                      // NIF_INFO
                    szInfo = text.Length > 255 ? text.Substring(0, 254) + "…" : text,
                    szInfoTitle = title.Length > 63 ? title.Substring(0, 62) + "…" : title,
                    dwInfoFlags = 0x4 | 0x20,           // NIIF_USER | NIIF_LARGE_ICON
                    hBalloonIcon = largeIcon,
                };
                data.cbSize = Marshal.SizeOf(data);
                return Shell_NotifyIcon(1, ref data);   // NIM_MODIFY
            }
            catch { return false; }
        }

        // The window and id Windows knows the tray icon by; WinForms keeps them private.
        static bool IconOwner(System.Windows.Forms.NotifyIcon icon, out IntPtr hWnd, out int id)
        {
            hWnd = IntPtr.Zero; id = 0;
            if (icon == null) return false;
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var window = (System.Windows.Forms.NativeWindow)typeof(System.Windows.Forms.NotifyIcon).GetField("window", flags).GetValue(icon);
            id = (int)typeof(System.Windows.Forms.NotifyIcon).GetField("id", flags).GetValue(icon);
            if (window == null || window.Handle == IntPtr.Zero) return false;
            hWnd = window.Handle;
            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NotifyIconIdentifier
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public Guid guidItem;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Rect { public int Left, Top, Right, Bottom; }

        [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier id, out Rect rect);

        // The icon the panel and the windows keep close to; set once the tray icon exists.
        public static System.Windows.Forms.NotifyIcon TrayIcon;

        // Where the tray icon is on the screen; empty when it is hidden in the overflow or Windows does not say.
        public static System.Drawing.Rectangle TrayIconRect()
        {
            try
            {
                IntPtr hWnd; int id;
                if (IconOwner(TrayIcon, out hWnd, out id))
                {
                    var ident = new NotifyIconIdentifier { hWnd = hWnd, uID = id };
                    ident.cbSize = Marshal.SizeOf(ident);
                    Rect r;
                    if (Shell_NotifyIconGetRect(ref ident, out r) == 0 && r.Right > r.Left)
                        return System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                }
            }
            catch { }
            return System.Drawing.Rectangle.Empty;
        }

        // A menu opened from the keyboard gets the keys only while its program is in front; the hidden window of
        // the tray icon is what Windows itself brings forward for the icon's menu.
        public static void ActivateTrayWindow()
        {
            IntPtr hWnd; int id;
            try { if (IconOwner(TrayIcon, out hWnd, out id)) SetForegroundWindow(hWnd); }
            catch { }
        }

        // The monitor with the taskbar that holds the icon. When the icon is hidden in the overflow or Windows does
        // not say where it is, the monitor under the mouse: the user has just been there.
        public static System.Windows.Forms.Screen TrayScreen()
        {
            try
            {
                IntPtr hWnd; int id;
                if (IconOwner(TrayIcon, out hWnd, out id))
                {
                    var ident = new NotifyIconIdentifier { hWnd = hWnd, uID = id };
                    ident.cbSize = Marshal.SizeOf(ident);
                    Rect r;
                    if (Shell_NotifyIconGetRect(ref ident, out r) == 0 && r.Right > r.Left)
                        return System.Windows.Forms.Screen.FromRectangle(System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom));
                }
            }
            catch { }
            return System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        }
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);

        static readonly string[] Editors = { "code", "code - insiders", "cursor", "windsurf", "vscodium" };

        // Editors keep many windows in one process, so those are matched by the folder name in
        // the title; terminals are focused through the window found in the hook's process chain.
        public static bool FocusSession(Session s)
        {
            bool editor = s.HostName != null && Array.IndexOf(Editors, s.HostName.ToLowerInvariant()) >= 0;
            if (editor && FocusProject(s.Cwd)) return true;
            if (s.HostHwnd != 0)
            {
                IntPtr h = new IntPtr(s.HostHwnd);
                if (IsWindow(h))
                {
                    if (IsIconic(h)) ShowWindow(h, 9);
                    SetForegroundWindow(h);
                    return true;
                }
            }
            return FocusProject(s.Cwd);
        }

        // Best effort: bring forward the window whose title contains the project folder name
        // (VS Code shows it in the title).
        public static bool FocusProject(string cwd)
        {
            if (string.IsNullOrEmpty(cwd)) return false;
            string name = Path.GetFileName(cwd.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(name)) return false;

            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    IntPtr h = p.MainWindowHandle;
                    if (h == IntPtr.Zero) continue;
                    if (p.MainWindowTitle.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (IsIconic(h)) ShowWindow(h, 9);
                    SetForegroundWindow(h);
                    return true;
                }
                catch { }
                finally { p.Dispose(); }
            }
            return false;
        }

        public static void SetDarkTitleBar(IntPtr handle, bool dark)
        {
            try
            {
                int v = dark ? 1 : 0;
                if (DwmSetWindowAttribute(handle, 20, ref v, sizeof(int)) != 0)
                    DwmSetWindowAttribute(handle, 19, ref v, sizeof(int));
            }
            catch { }
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "ClaudeCodeTrafficLight";

        public static bool GetAutostart()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(RunValue) != null;
            }
            catch { return false; }
        }

        public static void SetAutostart(bool on)
        {
            // A test instance must never remove or redirect the autostart of the program the user runs.
            if (AppPaths.IsSandbox) return;
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(RunValue, "\"" + System.Windows.Forms.Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunValue, false);
                }
            }
            catch (Exception ex) { Log.Write("Autostart failed: " + ex.Message); }
        }
    }
}
