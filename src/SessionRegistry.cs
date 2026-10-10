using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Semaphore
{
    sealed class RegistryEntry
    {
        public int Pid;
        public string SessionId, Cwd, Status, Kind, Version;
        public long ProcStart;
        public DateTime Updated;
    }

    // Claude Code keeps a small file per running process in ~/.claude/sessions (session id, folder,
    // busy/idle status). It is not a documented interface, so it is only a fallback: it lets the tray
    // app find sessions that started before it did and notice an interrupted task; the hooks stay
    // the main source of truth.
    static class SessionRegistry
    {
        internal static string ClaudeDir
        {
            get
            {
                string dir = Environment.GetEnvironmentVariable("TRAFFICLIGHT_CLAUDE_DIR");
                return string.IsNullOrEmpty(dir)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
                    : dir;
            }
        }

        // Set while files of running Claude Code processes exist but none of them has the fields this program reads:
        // the format has changed, and running sessions are then found through the hooks only.
        public static volatile bool FormatUnknown;
        static string loggedVersion;

        public static List<RegistryEntry> Scan()
        {
            var result = new List<RegistryEntry>();
            int unreadable = 0;
            string dir = Path.Combine(ClaudeDir, "sessions");
            if (!Directory.Exists(dir)) return result;

            foreach (string file in Directory.GetFiles(dir, "*.json"))
            {
                int pid;
                if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out pid)) continue;
                try
                {
                    // Files of dead processes stay behind; skip them before reading anything.
                    using (Process proc = Process.GetProcessById(pid))
                    {
                        var d = ReadJson(file);
                        if (d == null) continue;
                        if (Str(d, "sessionId") == null || Str(d, "status") == null) { unreadable++; continue; }

                        var e = new RegistryEntry
                        {
                            Pid = pid,
                            SessionId = Str(d, "sessionId"),
                            Cwd = Str(d, "cwd"),
                            Status = Str(d, "status"),
                            Kind = Str(d, "kind"),
                            Version = Str(d, "version"),
                            ProcStart = Num(d, "procStart"),
                        };
                        long updated = Num(d, "statusUpdatedAt");
                        if (updated == 0) updated = Num(d, "updatedAt");
                        e.Updated = updated == 0
                            ? File.GetLastWriteTime(file)
                            : new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(updated).ToLocalTime();

                        // The process id may have been reused by another program since the file was written.
                        if (e.ProcStart != 0)
                        {
                            if (proc.StartTime.ToFileTimeUtc() != e.ProcStart) continue;
                        }
                        else if (proc.StartTime.ToUniversalTime() > File.GetLastWriteTimeUtc(file).AddSeconds(5))
                        {
                            continue;
                        }
                        result.Add(e);
                    }
                }
                catch { }
            }
            FormatUnknown = unreadable > 0 && result.Count == 0;
            if (FormatUnknown) Log.Write("The files in ~/.claude/sessions have an unknown format: running sessions are found through the hooks only");
            if (result.Count > 0 && result[0].Version != null && result[0].Version != loggedVersion)
            {
                loggedVersion = result[0].Version;
                Log.Write("Claude Code version " + loggedVersion);
            }
            return result;
        }

        // Transcripts live in ~/.claude/projects/<encoded folder>/<session id>.jsonl.
        public static string FindTranscript(string sessionId)
        {
            try
            {
                string projects = Path.Combine(ClaudeDir, "projects");
                if (!Directory.Exists(projects) || string.IsNullOrEmpty(sessionId)) return null;
                foreach (string folder in Directory.GetDirectories(projects))
                {
                    string path = Path.Combine(folder, sessionId + ".jsonl");
                    if (File.Exists(path)) return path;
                }
            }
            catch { }
            return null;
        }

        static IDictionary<string, object> ReadJson(string file)
        {
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(fs, Encoding.UTF8))
                    return Json.Parse(r.ReadToEnd()) as IDictionary<string, object>;
            }
            catch { return null; }
        }

        static string Str(IDictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) ? v as string : null;
        }

        static long Num(IDictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return 0;
            try { return Convert.ToInt64(v); }
            catch { return 0; }
        }
    }
}
