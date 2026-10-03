using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Semaphore
{
    // "--selfcheck": shows the program with made-up sessions on a computer that may have no Claude Code at all,
    // photographs every window and writes what the screen and the system are like, so the look can be checked
    // there (display scaling, several monitors, high contrast). Everything stays in a folder of its own next to
    // the program; hooks, autostart and the shortcut of an installed copy are not touched (it is a sandbox).
    static class SelfCheckSetup
    {
        // This process is a self-check: nothing may go out over the network.
        public static bool Active { get; private set; }

        // Runs before anything reads the folders: they must point into the self-check folder from the start.
        public static string Prepare()
        {
            Active = true;
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string root = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "selfcheck-" + stamp);
            try { Directory.CreateDirectory(root); }
            catch { root = Path.Combine(Path.GetTempPath(), "ClaudeCodeTrafficLight-selfcheck-" + stamp); Directory.CreateDirectory(root); }
            string data = Path.Combine(root, "data"), claude = Path.Combine(root, "claude");
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(Path.Combine(claude, "projects", "demo"));
            Environment.SetEnvironmentVariable("TRAFFICLIGHT_DATA_DIR", data);
            Environment.SetEnvironmentVariable("TRAFFICLIGHT_CLAUDE_DIR", claude);

            var utf8 = new UTF8Encoding(false);
            // Chat titles for the made-up sessions, as Claude Code writes them into its transcripts.
            for (int i = 0; i < SelfCheckDemo.Ids.Length; i++)
                File.WriteAllText(Path.Combine(claude, "projects", "demo", SelfCheckDemo.Ids[i] + ".jsonl"),
                    "{\"type\":\"ai-title\",\"aiTitle\":\"" + SelfCheckDemo.Titles[i] + "\",\"sessionId\":\"" + SelfCheckDemo.Ids[i] + "\"}\n", utf8);
            // Claude Code's settings with this program's hooks, so the connection page shows the connected state.
            string cmd = "\\\"" + Application.ExecutablePath.Replace('\\', '/') + "\\\" --hook trafficlight";
            var hooks = new StringBuilder("{\"hooks\":{");
            string[] events = { "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "Notification", "Stop", "SessionEnd", "PreCompact", "PostCompact" };
            for (int i = 0; i < events.Length; i++)
            {
                if (i > 0) hooks.Append(',');
                hooks.Append("\"").Append(events[i]).Append("\":[{");
                if (events[i] == "PreToolUse" || events[i] == "PostToolUse") hooks.Append("\"matcher\":\"*\",");
                hooks.Append("\"hooks\":[{\"type\":\"command\",\"command\":\"").Append(cmd).Append("\",\"timeout\":5}]}]");
            }
            hooks.Append("}}");
            File.WriteAllText(Path.Combine(claude, "settings.json"), hooks.ToString(), utf8);
            File.WriteAllText(Path.Combine(data, "config.json"),
                "{\"FirstRunDone\":true,\"StartupNotice\":false,\"LocalApprove\":true,\"PopupAsk\":false,\"NtfyEnabled\":true,"
                + "\"SoundWaiting\":false,\"SoundDone\":false,\"ToastWaiting\":false,\"ToastDone\":false,\"Hotkey\":\"\"}", utf8);
            return root;
        }
    }

    static class SelfCheckDemo
    {
        public static readonly string[] Ids = { "11111111-aaaa-4aaa-8aaa-111111111111", "22222222-bbbb-4bbb-8bbb-222222222222", "33333333-cccc-4ccc-8ccc-333333333333" };
        public static readonly string[] Titles = { "Refactor the export module", "Write unit tests for login", "Update the changelog" };
        public static readonly string[] Folders = { @"C:\Projects\my-app", @"C:\Projects\my-app", @"C:\Projects\docs-site" };
    }

    sealed partial class TrayApp
    {
        public void StartSelfCheck(string folder)
        {
            new SelfCheckRun(this, folder).Start();
        }

        // The steps, one after another on the UI thread, with time for each window to appear before it is taken.
        sealed class SelfCheckRun
        {
            [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
            [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
            [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
            [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
            [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, int flags);
            [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
            [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

            readonly TrayApp app;
            readonly string folder;
            readonly List<KeyValuePair<int, Action>> steps = new List<KeyValuePair<int, Action>>();
            readonly List<string> results = new List<string>();
            readonly Timer timer = new Timer();
            int next;

            public SelfCheckRun(TrayApp app, string folder)
            {
                this.app = app;
                this.folder = folder;
            }

            void Step(int waitMs, string name, Action action)
            {
                steps.Add(new KeyValuePair<int, Action>(waitMs, () =>
                {
                    try { action(); results.Add("ok      " + name); }
                    catch (Exception ex) { results.Add("FAILED  " + name + ": " + ex.Message); }
                }));
            }

            public void Start()
            {
                Step(2500, "made-up sessions", Seed);
                foreach (string theme in new[] { "light", "dark" })
                {
                    string t = theme;
                    Step(500, "theme " + t, () => { app.Config.Theme = t; app.ApplyConfig(); });
                    for (int page = 0; page < 6; page++)
                    {
                        int p = page;
                        Step(300, "open page " + p + " (" + t + ")", () => app.ShowSettings(p));
                        Step(1500, "picture page " + p + " (" + t + ")", () => SaveWindow(app.settings, "page" + p + "-" + t + ".png"));
                    }
                    Step(300, "close settings (" + t + ")", () => app.settings.Close());
                    Step(300, "open menu (" + t + ")", () => app.ShowMenuAt(MenuPoint()));
                    Step(1500, "picture menu (" + t + ")", () => SaveMenu("menu-" + t + ".png"));
                    Step(300, "close menu (" + t + ")", () => app.menu.Close());
                }
                Step(300, "open request panel", ShowPanel);
                Step(1500, "picture request panel", () => SaveScreenRect(app.askPanel.Bounds, "panel.png"));
                Step(300, "close request panel", () => { app.askPanel.Close(); app.askTimer.Start(); });
                Step(300, "show a notification", () => app.ShowTestNotification());
                Step(2500, "picture notification", () => SaveScreenRect(CornerOfTray(), "notification.png"));
                Step(300, "open welcome window", () => { welcome = new WelcomeForm(app.Config, app.icons[Level.Idle], () => { }); welcome.Show(); });
                Step(1500, "picture welcome window", () => SaveWindow(welcome, "welcome.png"));
                Step(300, "close welcome window", () => welcome.Close());
                Step(300, "open topic window", () => { topic = new TopicDialog("ccl-selfcheck00000000000001", app.icons[Level.Idle]); topic.Show(); });
                Step(1500, "picture topic window", () => SaveWindow(topic, "topic.png"));
                Step(300, "close topic window", () => topic.Close());
                steps.Add(new KeyValuePair<int, Action>(300, Finish));

                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    if (next >= steps.Count) return;
                    KeyValuePair<int, Action> step = steps[next++];
                    step.Value();
                    if (next < steps.Count) { timer.Interval = steps[next].Key; timer.Start(); }
                };
                timer.Interval = steps[0].Key;
                timer.Start();
            }

            Form welcome, topic;

            void Seed()
            {
                string claude = Environment.GetEnvironmentVariable("TRAFFICLIGHT_CLAUDE_DIR");
                for (int i = 0; i < SelfCheckDemo.Ids.Length; i++)
                {
                    string tp = Path.Combine(claude, "projects", "demo", SelfCheckDemo.Ids[i] + ".jsonl").Replace("\\", "\\\\");
                    string b = "\"session_id\":\"" + SelfCheckDemo.Ids[i] + "\",\"cwd\":\"" + SelfCheckDemo.Folders[i].Replace("\\", "\\\\") + "\",\"transcript_path\":\"" + tp + "\"";
                    app.OnMessage("{\"hook_event_name\":\"UserPromptSubmit\"," + b + ",\"prompt\":\"x\"}");
                    if (i == 1) app.OnMessage("{\"hook_event_name\":\"Stop\"," + b + "}");
                    if (i == 2) app.OnMessage("{\"hook_event_name\":\"Notification\"," + b + ",\"notification_type\":\"permission_prompt\",\"message\":\"Claude needs your permission to use Bash\"}");
                }
            }

            void ShowPanel()
            {
                var r = new Approvals.Request
                {
                    Token = "selfcheck",
                    SessionId = SelfCheckDemo.Ids[2],
                    Tool = "AskUserQuestion",
                    Question = "Which format should the release notes use?",
                    Options = new List<string> { "Short summary", "Detailed list", "Table" },
                    Descriptions = new List<string> { "A few lines for the announcement.", "Every change with its reason.", "Change, area and impact in columns." },
                    Deadline = DateTime.Now.AddMinutes(5),
                };
                // The panel of a request the program does not know is closed by the regular check; it waits meanwhile.
                app.askTimer.Stop();
                app.ShowAsk(r);
            }

            Point MenuPoint()
            {
                Rectangle area = Native.TrayScreen().WorkingArea;
                return new Point(area.Left + Ui.S(260), area.Top + Ui.S(160));
            }

            Rectangle CornerOfTray()
            {
                Rectangle all = Native.TrayScreen().Bounds;
                int w = Math.Min(all.Width, Ui.S(560)), h = Math.Min(all.Height, Ui.S(360));
                return new Rectangle(all.Right - w, all.Bottom - h, w, h);
            }

            void SaveWindow(Form form, string file)
            {
                RECT r;
                GetWindowRect(form.Handle, out r);
                using (var bmp = new Bitmap(Math.Max(1, r.R - r.L), Math.Max(1, r.B - r.T)))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        IntPtr dc = g.GetHdc();
                        PrintWindow(form.Handle, dc, 2);
                        g.ReleaseHdc(dc);
                    }
                    bmp.Save(Path.Combine(folder, file), System.Drawing.Imaging.ImageFormat.Png);
                }
            }

            void SaveMenu(string file)
            {
                Rectangle box = app.menu.Bounds;
                foreach (ToolStripItem item in app.menu.Items)
                {
                    var m = item as ToolStripMenuItem;
                    if (m != null && m.DropDown.Visible) box = Rectangle.Union(box, m.DropDown.Bounds);
                }
                SaveScreenRect(box, file);
            }

            void SaveScreenRect(Rectangle r, string file)
            {
                using (var bmp = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height)))
                {
                    using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                    bmp.Save(Path.Combine(folder, file), System.Drawing.Imaging.ImageFormat.Png);
                }
            }

            void Finish()
            {
                try { File.WriteAllText(Path.Combine(folder, "report.txt"), Report(), new UTF8Encoding(true)); }
                catch (Exception ex) { Log.Write("Self-check report failed: " + ex.Message); }
                // Nothing more happens to the made-up sessions while the last window waits for the user.
                app.tick.Stop();
                app.askTimer.Stop();
                // The author's automated runs need no window to close at the end.
                if (Environment.GetEnvironmentVariable("TRAFFICLIGHT_SELFCHECK_QUIET") != "1")
                {
                    try { System.Diagnostics.Process.Start("explorer.exe", "\"" + folder + "\""); }
                    catch { }
                    MessageBox.Show(Loc.T("selfcheck.done", folder), Loc.T("app.name"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                // The made-up Claude Code folder is of no use to whoever reads the result; the log stays.
                try { Directory.Delete(Environment.GetEnvironmentVariable("TRAFFICLIGHT_CLAUDE_DIR"), true); }
                catch { }
                app.Quit();
            }

            string Report()
            {
                var sb = new StringBuilder();
                sb.AppendLine("Claude Code Traffic Light self-check");
                sb.AppendLine("version   " + AppInfo.Version + "   " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("windows   " + WindowsName() + "   (" + Environment.OSVersion.VersionString + ")");
                sb.AppendLine(".net      " + Environment.Version + "   64-bit process: " + Environment.Is64BitProcess);
                sb.AppendLine("language  program: " + Loc.Code + ", system: " + System.Globalization.CultureInfo.CurrentUICulture.Name);
                sb.AppendLine("theme     dark: " + Theme.Dark + ", high contrast: " + Theme.HighContrast);
                sb.AppendLine("scale     program draws at " + (int)Math.Round(Ui.Scale * 100) + "%  (system DPI " + (int)Math.Round(Ui.Scale * 96) + ")");
                sb.AppendLine("notifications  Windows notifications available: " + Toasts.Ready);
                sb.AppendLine();
                sb.AppendLine("monitors");
                foreach (Screen s in Screen.AllScreens)
                    sb.AppendLine("  " + s.DeviceName + (s.Primary ? " (primary)" : "") + "  bounds " + Box(s.Bounds) + "  working area " + Box(s.WorkingArea) + "  scale " + MonitorScale(s));
                Rectangle icon = Native.TrayIconRect();
                sb.AppendLine("tray icon " + (icon.IsEmpty ? "hidden in the overflow or unknown" : Box(icon)) + ", on " + Native.TrayScreen().DeviceName);
                if (app.settings != null) sb.AppendLine("settings window " + app.settings.Width + "x" + app.settings.Height);
                sb.AppendLine();
                sb.AppendLine("steps");
                foreach (string r in results) sb.AppendLine("  " + r);
                return sb.ToString();
            }

            static string Box(Rectangle r) { return r.Width + "x" + r.Height + " at " + r.X + "," + r.Y; }

            // The real scale of each monitor: this process only knows the system one, so it is asked with the
            // thread switched to per-monitor awareness for the moment.
            static string MonitorScale(Screen s)
            {
                IntPtr old = IntPtr.Zero;
                try
                {
                    old = SetThreadDpiAwarenessContext(new IntPtr(-4));
                    var p = new POINT { X = s.Bounds.X + s.Bounds.Width / 2, Y = s.Bounds.Y + s.Bounds.Height / 2 };
                    uint x, y;
                    if (GetDpiForMonitor(MonitorFromPoint(p, 2), 0, out x, out y) == 0) return (x * 100 / 96) + "%";
                }
                catch { }
                finally { if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old); }
                return "unknown";
            }

            static string WindowsName()
            {
                try
                {
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    {
                        string name = (k.GetValue("ProductName") ?? "").ToString();
                        int build;
                        // Windows 11 still calls itself "Windows 10" there; the build number tells them apart.
                        if (int.TryParse((k.GetValue("CurrentBuild") ?? "").ToString(), out build) && build >= 22000)
                            name = name.Replace("Windows 10", "Windows 11");
                        return name + " " + k.GetValue("DisplayVersion") + " build " + k.GetValue("CurrentBuild");
                    }
                }
                catch { return "?"; }
            }
        }
    }
}
