using System;
using System.IO;

namespace Semaphore
{
    // Where Claude Code keeps its own files: settings.json (the hooks), sessions, projects (transcripts), and
    // .claude.json (the plan and the last limit figures). By default that is ~/.claude and ~/.claude.json; with
    // CLAUDE_CONFIG_DIR set, Claude Code uses that folder for all of it, .claude.json included.
    //
    // The variable is read from the registry (the user's, then the system's) before this process's own copy:
    // the tray app usually starts with Windows and lives for days, so a variable set or changed after it started
    // is only seen there. The process copy still counts when neither has it, for a start from a shell that set it.
    static class ClaudeHome
    {
        const string Name = "CLAUDE_CONFIG_DIR";
        static readonly object gate = new object();
        static DateTime readAt;
        static string dir, json, logged;
        static bool custom;

        // The folder (~/.claude unless moved).
        public static string Dir { get { Refresh(); return dir; } }

        // The file Claude Code calls its global config.
        public static string JsonPath { get { Refresh(); return json; } }

        // True when CLAUDE_CONFIG_DIR moved the folder.
        public static bool Custom { get { Refresh(); return custom; } }

        // The settings file as the settings page shows it: the short ~ form for the usual place.
        public static string SettingsShown
        {
            get { return Custom ? Path.Combine(Dir, "settings.json") : "~/.claude/settings.json"; }
        }

        // The value to hand to a Claude Code started from here, null to leave its environment as it is.
        public static string ConfigDirForChild { get { return Custom ? Dir : null; } }

        static void Refresh()
        {
            lock (gate)
            {
                // Read at most every few seconds: the folder is asked for on every scan of the sessions.
                DateTime now = DateTime.UtcNow;
                if (dir != null && now - readAt < TimeSpan.FromSeconds(5) && now >= readAt) return;
                readAt = now;
                string config = Variable();
                string d, j;
                bool c;
                d = Resolve(Environment.GetEnvironmentVariable("TRAFFICLIGHT_CLAUDE_DIR"), config,
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), out j, out c);
                dir = d; json = j; custom = c;
                string said = c ? d : "";
                if (logged != said)
                {
                    if (c) Log.Write("Claude Code's folder is " + d + " (CLAUDE_CONFIG_DIR)");
                    else if (logged != null) Log.Write("Claude Code's folder is ~/.claude again (CLAUDE_CONFIG_DIR is gone)");
                    logged = said;
                }
            }
        }

        static string Variable()
        {
            foreach (var target in new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            {
                try
                {
                    string v = Environment.GetEnvironmentVariable(Name, target);
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
                catch (Exception) { }
            }
            return Environment.GetEnvironmentVariable(Name);
        }

        // test: the folder a sandbox stands in for ~/.claude (its .claude.json beside it, as in a profile);
        // config: CLAUDE_CONFIG_DIR; home: the user's profile. A value Claude Code would refuse (blank, or not
        // an absolute path) is ignored, as Claude Code then falls back to the usual place too.
        internal static string Resolve(string test, string config, string home, out string jsonPath, out bool moved)
        {
            moved = false;
            if (!string.IsNullOrEmpty(test))
            {
                jsonPath = Path.Combine(Path.GetDirectoryName(test.TrimEnd('\\', '/')), ".claude.json");
                return test;
            }
            string d = Clean(config);
            if (d != null)
            {
                moved = true;
                jsonPath = Path.Combine(d, ".claude.json");
                return d;
            }
            jsonPath = Path.Combine(home, ".claude.json");
            return Path.Combine(home, ".claude");
        }

        internal static string Clean(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string v = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"').Trim());
            bool drive = v.Length >= 3 && char.IsLetter(v[0]) && v[1] == ':' && (v[2] == '\\' || v[2] == '/');
            bool unc = v.StartsWith(@"\\", StringComparison.Ordinal) || v.StartsWith("//", StringComparison.Ordinal);
            if (!drive && !unc) return null;
            try
            {
                v = Path.GetFullPath(v);
                if (v.Length > 3) v = v.TrimEnd('\\', '/');
                return v;
            }
            catch (Exception) { return null; }
        }
    }
}
