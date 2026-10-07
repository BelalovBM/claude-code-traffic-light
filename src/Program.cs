using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Windows.Forms;

namespace Semaphore
{
    static class Program
    {
        // A property, not a field: --selfcheck must choose its own folders before the name is first needed.
        public static string PipeName { get { return "ClaudeCodeTrafficLight.Hook" + AppPaths.InstanceSuffix; } }

        // A hook ends with TerminateProcess instead of returning from Main: the normal shutdown of this
        // process sometimes took 2 s (measured), which held up Claude Code on every tool call. By then
        // everything the hook had to send or print is already written and flushed.
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool TerminateProcess(IntPtr process, uint exitCode);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        [STAThread]
        static int Main(string[] args)
        {
            // Hook mode is handled before any UI type is touched to keep start-up fast
            // and to guarantee that a hook can never disturb Claude Code.
            if (args.Length > 0 && args[0] == "--hook")
            {
                int code = HookClient.Run();
                TerminateProcess(GetCurrentProcess(), (uint)code);
                return code;
            }

            // A run that only shows and photographs the program with made-up sessions, in a folder of its own.
            string selfCheck = args.Length > 0 && args[0] == "--selfcheck" ? SelfCheckSetup.Prepare() : null;

            bool created;
            using (var mutex = new Mutex(true, @"Local\ClaudeCodeTrafficLight.SingleInstance" + AppPaths.InstanceSuffix, out created))
            {
                if (!created)
                {
                    // A second start asks the running instance to show its settings, a given
                    // settings page, or (for documentation screenshots) the tray menu.
                    int page = 0;
                    if (args.Length > 1 && args[0] == "--page") int.TryParse(args[1], out page);
                    // --hotkey does what the keyboard shortcut does, so a test needs no simulated key presses.
                    string command = args.Length > 0 && args[0] == "--menu" ? "menu"
                        : args.Length > 0 && args[0] == "--hotkey" ? "hotkey" : "settings";
                    HookClient.Send("{\"trafficlight_cmd\":\"" + command + "\",\"page\":" + page + "}");
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => Log.Write("UI error: " + e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("Fatal: " + e.ExceptionObject);
                var app = new TrayApp();
                if (selfCheck != null) app.StartSelfCheck(selfCheck);
                Application.Run(app);
            }
            return 0;
        }
    }

    static class HookClient
    {
        public static int Run()
        {
            try
            {
                string payload = ReadStdin(2000);
                if (string.IsNullOrWhiteSpace(payload)) return 0;
                // One hook is one short process: build the process snapshot once and reuse it.
                ProcessTree.ReuseSnapshot = true;
                if (IsPermissionRequest(payload))
                    return Approve(payload);
                Send(payload, AddHost);
            }
            catch { }
            return 0;
        }

        static bool IsPermissionRequest(string payload)
        {
            return EventName(payload) == "PermissionRequest";
        }

        // Reads the value of the top-level "hook_event_name" without a JSON parser or a regular
        // expression: both cost noticeable start-up time in a process that lives for a few milliseconds.
        // An escaped copy of the key inside a string (\"hook_event_name\") does not match.
        internal static string EventName(string payload)
        {
            int key = payload.IndexOf("\"hook_event_name\"", StringComparison.Ordinal);
            if (key < 0) return null;
            int i = key + "\"hook_event_name\"".Length;
            while (i < payload.Length && (payload[i] == ' ' || payload[i] == ':' || payload[i] == '\t' || payload[i] == '\r' || payload[i] == '\n')) i++;
            if (i >= payload.Length || payload[i] != '"') return null;
            int end = payload.IndexOf('"', i + 1);
            return end < 0 ? null : payload.Substring(i + 1, end - i - 1);
        }

        // Waits for the tray app's decision on a permission prompt. Printing nothing leaves the
        // decision to Claude Code's normal prompt, which is what happens on any failure.
        static int Approve(string payload)
        {
            string reply = null;
            try
            {
                using (var c = new NamedPipeClientStream(".", Approvals.PipeName, PipeDirection.InOut))
                {
                    c.Connect(60);   // the server keeps a listening instance ready; do not make Claude wait for an absent app
                    Approvals.WriteMessage(c, payload);
                    reply = System.Text.Encoding.UTF8.GetString(Approvals.ReadMessage(c));
                }
            }
            catch { return 0; }

            string decision;
            if (reply == "allow")
                decision = "{\"behavior\":\"allow\"}";
            else if (reply != null && reply.StartsWith("always|", StringComparison.Ordinal))
                decision = "{\"behavior\":\"allow\",\"updatedPermissions\":" + reply.Substring(7) + "}";
            else if (reply != null && reply.StartsWith("answer|", StringComparison.Ordinal))
                decision = "{\"behavior\":\"allow\",\"updatedInput\":" + reply.Substring(7) + "}";
            else if (reply != null && reply.StartsWith("deny|", StringComparison.Ordinal))
                decision = "{\"behavior\":\"deny\",\"message\":" + JsonString(reply.Substring(5)) + "}";
            else
                return 0;

            string json = "{\"hookSpecificOutput\":{\"hookEventName\":\"PermissionRequest\",\"decision\":" + decision + "}}";
            try
            {
                byte[] bytes = new System.Text.UTF8Encoding(false).GetBytes(json);
                using (var stdout = Console.OpenStandardOutput())
                {
                    stdout.Write(bytes, 0, bytes.Length);
                    stdout.Flush();
                }
            }
            catch { }
            return 0;
        }

        static string JsonString(string s)
        {
            var sb = new System.Text.StringBuilder("\"");
            foreach (char ch in s)
            {
                if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
                else if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                else sb.Append(ch);
            }
            return sb.Append('"').ToString();
        }

        // Adds what the tray app needs to know about the session's surroundings to the hook's JSON:
        // the window that hosts it and the Claude Code process (to notice when it is gone).
        static string AddHost(string payload)
        {
            // Tool events fire twice per tool call and the tray app already knows the window and the
            // process from the session's other events, so looking them up again would only cost time.
            string eventName = EventName(payload);
            if (eventName == "PreToolUse" || eventName == "PostToolUse")
                return payload;

            var extra = new System.Text.StringBuilder();
            long hwnd;
            string name;
            if (ProcessTree.FindHostWindow(out hwnd, out name))
                extra.Append("\"tl_hwnd\":").Append(hwnd)
                     .Append(",\"tl_host\":\"").Append(name.Replace("\\", "").Replace("\"", "")).Append("\"");
            int pid;
            long started;
            if (ProcessTree.FindClaudeProcess(out pid, out started))
            {
                if (extra.Length > 0) extra.Append(',');
                extra.Append("\"tl_pid\":").Append(pid).Append(",\"tl_pstart\":").Append(started);
            }
            if (extra.Length == 0) return payload;

            int end = payload.LastIndexOf('}');
            if (end < 0) return payload;
            bool empty = payload.Substring(0, end).TrimEnd().EndsWith("{", StringComparison.Ordinal);
            return payload.Substring(0, end) + (empty ? "" : ",") + extra + payload.Substring(end);
        }

        // Reads the hook's JSON from stdin. It stops as soon as the object is complete instead of
        // waiting for the end of the stream: a parent that is slow to close stdin would otherwise
        // hold every hook (and with it Claude Code) until the timeout.
        static string ReadStdin(int timeoutMs)
        {
            var ms = new MemoryStream();
            var finished = new ManualResetEvent(false);
            var t = new Thread(() =>
            {
                try
                {
                    using (var stdin = Console.OpenStandardInput())
                    {
                        var buffer = new byte[8192];
                        var scan = new JsonEnd();
                        int n;
                        while ((n = stdin.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            lock (ms) ms.Write(buffer, 0, n);
                            if (scan.Feed(buffer, n)) break;
                        }
                    }
                }
                catch { }
                finally { finished.Set(); }
            });
            t.IsBackground = true;
            t.Start();
            finished.WaitOne(timeoutMs);
            byte[] data;
            lock (ms) data = ms.ToArray();
            return System.Text.Encoding.UTF8.GetString(data).TrimStart('\uFEFF');
        }

        // Tells when the first top-level JSON object has been closed. Structural characters are ASCII,
        // and bytes of multi-byte UTF-8 characters are never ASCII, so scanning bytes is safe.
        sealed class JsonEnd
        {
            int depth;
            bool started, inString, escaped;

            public bool Feed(byte[] bytes, int count)
            {
                for (int i = 0; i < count; i++)
                {
                    byte c = bytes[i];
                    if (inString)
                    {
                        if (escaped) escaped = false;
                        else if (c == (byte)'\\') escaped = true;
                        else if (c == (byte)'"') inString = false;
                    }
                    else if (c == (byte)'"') inString = true;
                    else if (c == (byte)'{') { depth++; started = true; }
                    else if (c == (byte)'}')
                    {
                        depth--;
                        if (started && depth == 0) return true;
                    }
                }
                return false;
            }
        }

        public static void Send(string payload)
        {
            Send(payload, null);
        }

        // enrich runs only after the connection is made: when the tray app is not running there is
        // nobody to tell, so the look-around (about 50 ms) is skipped.
        public static void Send(string payload, Func<string, string> enrich)
        {
            try
            {
                using (var c = new NamedPipeClientStream(".", Program.PipeName, PipeDirection.Out))
                {
                    // Short on purpose: when the tray app is not running, every event of every tool call
                    // would otherwise wait for the full timeout (it was 300 ms) before giving up.
                    c.Connect(40);
                    if (enrich != null) payload = enrich(payload);
                    byte[] bytes = new System.Text.UTF8Encoding(false).GetBytes(payload);
                    c.Write(bytes, 0, bytes.Length);
                    c.Flush();
                }
            }
            catch { }
        }
    }
}
