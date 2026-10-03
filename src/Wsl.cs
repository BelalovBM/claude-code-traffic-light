using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Semaphore
{
    // Claude Code started inside WSL keeps its settings in the Linux home folder, so the hooks added on the
    // Windows side never run there and the traffic light stays silent. This only finds such an installation to
    // say so. It looks into distributions that are already running: opening \\wsl.localhost of a stopped one
    // would start its virtual machine.
    static class Wsl
    {
        static readonly object gate = new object();
        static List<string> found;
        static bool searching;

        // Distributions with a ~/.claude folder; null while the first search is still running.
        public static List<string> Found { get { lock (gate) return found; } }

        public static void Search(Action done)
        {
            lock (gate)
            {
                if (searching) return;
                searching = true;
            }
            new Thread(() =>
            {
                var result = new List<string>();
                try
                {
                    // A sandbox (tests, screenshots) leaves the real WSL alone and is told what to find.
                    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TRAFFICLIGHT_CLAUDE_DIR")))
                        result.AddRange((Environment.GetEnvironmentVariable("TRAFFICLIGHT_WSL_FOUND") ?? "")
                            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
                    else
                        foreach (string distro in Running())
                            if (HasClaude(distro)) result.Add(distro);
                }
                catch (Exception ex) { Log.Write("WSL check failed: " + ex.Message); }
                lock (gate) { found = result; searching = false; }
                if (result.Count > 0) Log.Write("Claude Code found in WSL: " + string.Join(", ", result));
                if (done != null) done();
            }) { IsBackground = true }.Start();
        }

        static IEnumerable<string> Running()
        {
            string wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
            if (!File.Exists(wsl)) return Enumerable.Empty<string>();
            var psi = new ProcessStartInfo(wsl, "--list --running --quiet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                // wsl.exe writes its own messages in UTF-16.
                StandardOutputEncoding = Encoding.Unicode,
            };
            using (Process p = Process.Start(psi))
            {
                string text = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { } return Enumerable.Empty<string>(); }
                if (p.ExitCode != 0) return Enumerable.Empty<string>();
                return text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim('\0', ' ')).Where(l => l.Length > 0).ToList();
            }
        }

        static bool HasClaude(string distro)
        {
            string root = @"\\wsl.localhost\" + distro;
            if (Directory.Exists(Path.Combine(root, @"root\.claude"))) return true;
            string home = Path.Combine(root, "home");
            if (!Directory.Exists(home)) return false;
            return Directory.GetDirectories(home).Any(d => Directory.Exists(Path.Combine(d, ".claude")));
        }
    }
}
