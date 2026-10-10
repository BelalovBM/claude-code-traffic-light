using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Semaphore
{
    enum HooksStatus { Missing, Outdated, Installed }

    static class HookInstaller
    {
        const string Marker = "--hook trafficlight";
        static readonly string[] Events =
        {
            "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse",
            "Notification", "Stop", "SessionEnd", "PreCompact", "PostCompact",
            // A turn ended by an API error (a limit, overloaded servers, a lost sign-in) fires this instead of Stop.
            "StopFailure",
        };

        public static string SettingsPath
        {
            get
            {
                // TRAFFICLIGHT_CLAUDE_DIR stands in for ~/.claude (tests).
                string dir = Environment.GetEnvironmentVariable("TRAFFICLIGHT_CLAUDE_DIR");
                if (string.IsNullOrEmpty(dir))
                    dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
                return Path.Combine(dir, "settings.json");
            }
        }

        // Forward slashes: Claude Code runs hook commands through a POSIX shell on Windows,
        // where backslashes in an unquoted-escape path would be eaten.
        public static string Command
        {
            get { return "\"" + Application.ExecutablePath.Replace('\\', '/') + "\" " + Marker; }
        }

        // The permission hook only exists while answering from the phone is switched on, so with
        // the feature off Claude Code's permission flow is exactly what it was without this app.
        const string PermissionEvent = "PermissionRequest";
        public static bool Approve { get; private set; }
        public static int ApproveSeconds { get; private set; }

        public static void Configure(bool approve, int seconds)
        {
            Approve = approve;
            ApproveSeconds = seconds;
        }

        public static HooksStatus GetStatus()
        {
            try
            {
                var root = Load();
                var hooks = root.ContainsKey("hooks") ? root["hooks"] as IDictionary<string, object> : null;
                if (hooks == null) return HooksStatus.Missing;

                int found = 0, current = 0;
                foreach (string ev in Events)
                {
                    bool has = false, ok = false;
                    foreach (string cmd in OurCommands(hooks, ev))
                    {
                        has = true;
                        if (cmd == Command) ok = true;
                    }
                    if (has) found++;
                    if (ok) current++;
                }

                bool hasPermission = false, permissionOk = false;
                foreach (string cmd in OurCommands(hooks, PermissionEvent))
                {
                    hasPermission = true;
                    if (cmd == Command) permissionOk = true;
                }
                bool permissionFine = Approve ? permissionOk : !hasPermission;

                if (found == 0 && !hasPermission) return HooksStatus.Missing;
                return current == Events.Length && permissionFine ? HooksStatus.Installed : HooksStatus.Outdated;
            }
            catch { return HooksStatus.Missing; }
        }

        public static void Install()
        {
            var root = Load();
            var hooks = root.ContainsKey("hooks") ? root["hooks"] as Dictionary<string, object> : null;
            if (hooks == null)
            {
                hooks = new Dictionary<string, object>();
                root["hooks"] = hooks;
            }

            foreach (string ev in Events)
            {
                List<object> groups = StripOurs(hooks, ev);
                var group = new Dictionary<string, object>();
                if (ev == "PreToolUse" || ev == "PostToolUse")
                    group["matcher"] = "*";
                groups.Add(Group(group, 5));
                hooks[ev] = groups;
            }

            List<object> permissionGroups = StripOurs(hooks, PermissionEvent);
            if (Approve)
                permissionGroups.Add(Group(new Dictionary<string, object>(), ApproveSeconds));
            if (permissionGroups.Count == 0) hooks.Remove(PermissionEvent);
            else hooks[PermissionEvent] = permissionGroups;
            Save(root);
        }

        static Dictionary<string, object> Group(Dictionary<string, object> group, int timeoutSeconds)
        {
            var entry = new Dictionary<string, object>();
            entry["type"] = "command";
            entry["command"] = Command;
            entry["timeout"] = timeoutSeconds;
            group["hooks"] = new List<object> { entry };
            return group;
        }

        public static void Remove()
        {
            var root = Load();
            var hooks = root.ContainsKey("hooks") ? root["hooks"] as Dictionary<string, object> : null;
            if (hooks == null) return;

            var all = new List<string>(Events);
            all.Add(PermissionEvent);
            foreach (string ev in all)
            {
                if (!hooks.ContainsKey(ev)) continue;
                List<object> groups = StripOurs(hooks, ev);
                if (groups.Count == 0) hooks.Remove(ev);
                else hooks[ev] = groups;
            }
            if (hooks.Count == 0) root.Remove("hooks");
            Save(root);
        }

        static IEnumerable<string> OurCommands(IDictionary<string, object> hooks, string ev)
        {
            object groupsObj;
            if (!hooks.TryGetValue(ev, out groupsObj)) yield break;
            var groups = groupsObj as IEnumerable;
            if (groups == null) yield break;
            foreach (object g in groups)
            {
                var gd = g as IDictionary<string, object>;
                if (gd == null || !gd.ContainsKey("hooks")) continue;
                var inner = gd["hooks"] as IEnumerable;
                if (inner == null) continue;
                foreach (object h in inner)
                {
                    string cmd = Cmd(h);
                    if (IsOurs(cmd)) yield return cmd;
                }
            }
        }

        // Returns the event's groups with every entry of this app removed and
        // foreign hooks left untouched; groups that became empty are dropped.
        static List<object> StripOurs(IDictionary<string, object> hooks, string ev)
        {
            var result = new List<object>();
            object groupsObj;
            if (!hooks.TryGetValue(ev, out groupsObj)) return result;
            var groups = groupsObj as IEnumerable;
            if (groups == null) return result;

            foreach (object g in groups)
            {
                var gd = g as Dictionary<string, object>;
                if (gd == null || !gd.ContainsKey("hooks") || !(gd["hooks"] is IEnumerable))
                {
                    result.Add(g);
                    continue;
                }
                var kept = new List<object>();
                foreach (object h in (IEnumerable)gd["hooks"])
                    if (!IsOurs(Cmd(h))) kept.Add(h);
                if (kept.Count == 0) continue;
                gd["hooks"] = kept;
                result.Add(gd);
            }
            return result;
        }

        static string Cmd(object hook)
        {
            var d = hook as IDictionary<string, object>;
            object c;
            return d != null && d.TryGetValue("command", out c) ? c as string : null;
        }

        static bool IsOurs(string cmd)
        {
            return cmd != null && cmd.EndsWith(Marker, StringComparison.Ordinal);
        }

        static Dictionary<string, object> Load()
        {
            if (!File.Exists(SettingsPath)) return new Dictionary<string, object>();
            string text = File.ReadAllText(SettingsPath, Encoding.UTF8).TrimStart('﻿');
            if (text.Trim().Length == 0) return new Dictionary<string, object>();
            var root = Json.Parse(text) as Dictionary<string, object>;
            if (root == null) throw new InvalidDataException("settings.json is not a JSON object");
            return root;
        }

        static void Save(Dictionary<string, object> root)
        {
            string path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            // Keep the very first original so the user can always roll back.
            string backup = path + ".trafficlight.bak";
            if (File.Exists(path) && !File.Exists(backup))
                File.Copy(path, backup);

            string tmp = path + ".trafficlight.tmp";
            File.WriteAllText(tmp, Json.Pretty(root), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
