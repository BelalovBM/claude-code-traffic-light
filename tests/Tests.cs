using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

// Unit tests of the program's logic, without windows: built against the program itself (InternalsVisibleTo) by
// tools\run-tests.ps1, locally and on every GitHub build. The exit code is the number of failed checks.
namespace Semaphore.Tests
{
    static class Program
    {
        static int passed, failed;
        static string box, repo;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        static int Main(string[] args)
        {
            repo = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
            box = Path.Combine(repo, "work", "unit-tests", "sandbox");
            if (Directory.Exists(box)) Directory.Delete(box, true);
            Directory.CreateDirectory(Path.Combine(box, "data"));
            Directory.CreateDirectory(Path.Combine(box, "claude"));
            // Before anything reads them: the program's files go to the sandbox, never next to an installed copy.
            Environment.SetEnvironmentVariable("TRAFFICLIGHT_DATA_DIR", Path.Combine(box, "data"));
            Environment.SetEnvironmentVariable("TRAFFICLIGHT_CLAUDE_DIR", Path.Combine(box, "claude"));

            Group("hook events", HookEvents);
            Group("session states", SessionStates);
            Group("background wait", BackgroundWait);
            Group("stop on an error", StopFailure);
            Group("transcripts", Transcripts);
            Group("questions", Questions);
            Group("usage limits", UsageLimits);
            Group("settings", Settings);
            Group("log", LogFile);
            Group("shortcut", Shortcut);
            Group("languages", Languages);

            Console.WriteLine();
            Console.WriteLine(passed + " passed, " + failed + " failed");
            return failed;
        }

        static void Group(string name, Action body)
        {
            Console.WriteLine("== " + name);
            try { body(); }
            catch (Exception ex) { Fail(name + " crashed: " + ex); }
        }

        static void Check(string what, bool ok, string detail = null)
        {
            if (ok) { passed++; Console.WriteLine("  ok    " + what); }
            else Fail(what + (detail == null ? "" : "  (" + detail + ")"));
        }

        static void Fail(string what)
        {
            failed++;
            Console.WriteLine("  FAIL  " + what);
        }

        static string Transcript(string name, params string[] lines)
        {
            string path = Path.Combine(box, "claude", name + ".jsonl");
            File.WriteAllText(path, string.Join("\n", lines) + "\n", Utf8);
            return path;
        }

        static string Esc(string path) { return path.Replace("\\", "\\\\"); }

        // ---- hook events ----

        static void HookEvents()
        {
            HookEvent e = HookEvent.Parse("{\"hook_event_name\":\"PreToolUse\",\"session_id\":\"s1\",\"cwd\":\"C:\\\\Proj\",\"tool_name\":\"Edit\",\"tool_input\":{\"file_path\":\"C:\\\\Proj\\\\src\\\\a.cs\"}}");
            Check("PreToolUse is read", e != null && e.Name == "PreToolUse" && e.SessionId == "s1" && e.Cwd == @"C:\Proj");
            Check("the edited file gives the tool detail", e.Tool == "Edit" && e.ToolDetail == "a.cs" && e.ToolFile == @"C:\Proj\src\a.cs");
            Check("a broken payload gives nothing", HookEvent.Parse("{not json") == null);

            Check("the event name is found without a parser", HookClient.EventName("{\"session_id\":\"x\", \"hook_event_name\" : \"Stop\"}") == "Stop");
            Check("an escaped copy of the key inside a string is not the event name",
                HookClient.EventName("{\"prompt\":\"say \\\"hook_event_name\\\"\",\"hook_event_name\":\"UserPromptSubmit\"}") == "UserPromptSubmit");
            Check("no event name gives null", HookClient.EventName("{\"a\":1}") == null);
        }

        // ---- session states ----

        static Change Apply(SessionStore store, string json) { return store.Apply(HookEvent.Parse(json)); }

        static void SessionStates()
        {
            var store = new SessionStore();
            string b = "\"session_id\":\"st1\",\"cwd\":\"C:\\\\Proj\"";
            Change c = Apply(store, "{\"hook_event_name\":\"UserPromptSubmit\"," + b + ",\"prompt\":\"go\"}");
            Check("a prompt makes the session working", c.New == State.Working && c.Old == null);
            c = Apply(store, "{\"hook_event_name\":\"Notification\"," + b + ",\"notification_type\":\"permission_prompt\",\"message\":\"Claude needs your permission\"}");
            Check("a permission prompt makes it waiting", c.New == State.Waiting && c.Old == State.Working);
            Check("the overall light is red while one waits", store.Overall() == Level.Waiting);
            c = Apply(store, "{\"hook_event_name\":\"Notification\"," + b + ",\"notification_type\":\"idle_prompt\",\"message\":\"Claude is waiting for your input\"}");
            Check("other notifications change nothing", c == null);
            Apply(store, "{\"hook_event_name\":\"PostToolUse\"," + b + ",\"tool_name\":\"Bash\"}");
            c = Apply(store, "{\"hook_event_name\":\"Stop\"," + b + "}");
            Check("stop makes it idle (finished)", c.New == State.Idle && c.Old == State.Working);

            Apply(store, "{\"hook_event_name\":\"UserPromptSubmit\"," + b + ",\"prompt\":\"more\"}");
            c = Apply(store, "{\"hook_event_name\":\"PreCompact\"," + b + ",\"trigger\":\"auto\"}");
            Check("compaction is its own state", c.New == State.Compacting && store.Overall() == Level.Compacting);
            c = Apply(store, "{\"hook_event_name\":\"PostCompact\"," + b + ",\"trigger\":\"auto\"}");
            Check("an automatic compaction continues the task", c.New == State.Working);
            Apply(store, "{\"hook_event_name\":\"PreCompact\"," + b + ",\"trigger\":\"manual\"}");
            c = Apply(store, "{\"hook_event_name\":\"PostCompact\"," + b + ",\"trigger\":\"manual\"}");
            Check("after /compact the session is ready", c.New == State.Idle);

            c = Apply(store, "{\"hook_event_name\":\"SessionEnd\"," + b + "}");
            Check("session end removes it", c.New == null && store.Find("st1") == null && store.Overall() == Level.None);
            c = Apply(store, "{\"hook_event_name\":\"Stop\",\"session_id\":\"\",\"cwd\":\"C:\\\\X\",\"unknown\":1}");
            Check("an event without a known name of its own is still handled safely", c == null || c.Session != null);
        }

        // ---- a turn ended by an API error ----

        static void StopFailure()
        {
            var store = new SessionStore();
            string b = "\"session_id\":\"sf1\",\"cwd\":\"C:\\\\Proj\"";
            Apply(store, "{\"hook_event_name\":\"UserPromptSubmit\"," + b + ",\"prompt\":\"go\"}");
            Change c = Apply(store, "{\"hook_event_name\":\"StopFailure\"," + b + ",\"error\":\"rate_limit\",\"last_assistant_message\":\"You've hit your limit · resets 9pm\"}");
            Session s = store.Find("sf1");
            Check("an API error is not \"finished\": the session waits for the user", c.New == State.Waiting && store.Overall() == Level.Waiting);
            Check("the kind of error and Claude's words are kept", s.Failure == "rate_limit" && s.FailureText.Contains("resets 9pm"));
            Apply(store, "{\"hook_event_name\":\"UserPromptSubmit\"," + b + ",\"prompt\":\"again\"}");
            Check("the next prompt clears the error", s.Failure == null && s.State == State.Working);
        }

        // ---- a stop that only waits for a background task ----

        static void BackgroundWait()
        {
            string started = "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"t1\",\"content\":\"Command running in background with ID: job7.\"}]},\"toolUseResult\":{\"backgroundTaskId\":\"job7\"}}";
            string reported = "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"<task-notification>\\n<task-id>job7</task-id>\\n<status>completed</status>\\n</task-notification>\"}}";
            string tp = Transcript("bg", started);
            Check("an unreported background task is counted", TitleReader.PendingBackgroundTasks(tp) == 1);

            var store = new SessionStore();
            string b = "\"session_id\":\"bg1\",\"cwd\":\"C:\\\\Proj\",\"transcript_path\":\"" + Esc(tp) + "\"";
            Apply(store, "{\"hook_event_name\":\"UserPromptSubmit\"," + b + ",\"prompt\":\"build\"}");
            Change c = Apply(store, "{\"hook_event_name\":\"Stop\"," + b + "}");
            Session s = store.Find("bg1");
            Check("a stop while it runs is not \"finished\"", c.New == State.Working && s.InBackground);
            Check("nothing ends the wait early", store.ExpireBackground().Count == 0);
            Check("only waiting for a background task has its own lamp", store.Overall() == Level.Background);
            Check("the wait remembers when it began", (DateTime.Now - s.BackgroundSince).TotalSeconds < 5 && !s.BackgroundNotified);
            Apply(store, "{\"hook_event_name\":\"UserPromptSubmit\",\"session_id\":\"bg2\",\"cwd\":\"C:\\\\Other\",\"prompt\":\"x\"}");
            Check("a session that really works comes first", store.Overall() == Level.Working);
            Apply(store, "{\"hook_event_name\":\"SessionEnd\",\"session_id\":\"bg2\"}");

            File.AppendAllText(tp, reported + "\n", Utf8);
            Check("a reported task is no longer counted", TitleReader.PendingBackgroundTasks(tp) == 0);
            Apply(store, "{\"hook_event_name\":\"UserPromptSubmit\"," + b + ",\"prompt\":\"<task-notification>\"}");
            Check("the next turn ends the background wait", !s.InBackground);
            c = Apply(store, "{\"hook_event_name\":\"Stop\"," + b + "}");
            Check("the stop after it is \"finished\"", c.New == State.Idle && c.Old == State.Working);

            File.WriteAllText(tp, started + "\n", Utf8);
            Apply(store, "{\"hook_event_name\":\"UserPromptSubmit\"," + b + ",\"prompt\":\"serve\"}");
            Apply(store, "{\"hook_event_name\":\"Stop\"," + b + "}");
            // A long task (a two-hour benchmark): while its process runs, the limit does not end the wait.
            s.BackgroundUntil = DateTime.Now.AddSeconds(-1);
            Check("a task seen running keeps the session working past the limit",
                store.ExpireBackground(x => 1).Count == 0 && s.InBackground && s.State == State.Working);
            Check("an unknown process count changes nothing before the limit", store.ExpireBackground(x => null).Count == 0);
            s.BackgroundUntil = DateTime.Now.AddSeconds(-1);
            List<Change> expired = store.ExpireBackground(x => 0);
            Check("a task that never reports ends the wait at the limit, as finished",
                expired.Count == 1 && expired[0].New == State.Idle && s.State == State.Idle);
        }

        // ---- transcripts ----

        static void Transcripts()
        {
            string tp = Transcript("title",
                "{\"type\":\"ai-title\",\"aiTitle\":\"First title\",\"sessionId\":\"x\"}",
                "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"a\",\"name\":\"Edit\",\"input\":{\"file_path\":\"C:\\\\Work\\\\app\\\\main.cs\"}}]}}",
                "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"a\",\"content\":\"ok\"}]}}",
                "{\"type\":\"ai-title\",\"aiTitle\":\"Later \\\"quoted\\\" title\",\"sessionId\":\"x\"}");
            Check("the latest chat title wins", TitleReader.Read(tp) == "Later \"quoted\" title", TitleReader.Read(tp));
            Check("the folder of the last edited file", TitleReader.ReadWorkDir(tp) == @"C:\Work\app", TitleReader.ReadWorkDir(tp));
            Check("an answered tool call is not pending", TitleReader.ReadPendingRequest(tp) == null);

            string ask = Transcript("ask",
                "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"q1\",\"name\":\"AskUserQuestion\",\"input\":{\"questions\":[{\"question\":\"Which way?\",\"header\":\"Way\",\"multiSelect\":false,\"options\":[{\"label\":\"Long\",\"description\":\"Safer\"},{\"label\":\"Short\",\"description\":\"Faster\"}]}]}}]}}");
            Approvals.Request r = TitleReader.ReadPendingRequest(ask);
            Check("an unanswered question is found with its options",
                r != null && r.Question == "Which way?" && r.Options != null && r.Options.SequenceEqual(new[] { "Long", "Short" }) && r.Descriptions[1] == "Faster");
            Check("a missing transcript gives nothing", TitleReader.Read(Path.Combine(box, "none.jsonl")) == null);
        }

        // ---- questions that can be answered by buttons ----

        static IDictionary<string, object> Input(string json) { return (IDictionary<string, object>)Json.Parse(json); }

        static string Options(int count)
        {
            return string.Join(",", Enumerable.Range(1, count).Select(i => "{\"label\":\"O" + i + "\",\"description\":\"d\"}"));
        }

        static bool Readable(string json)
        {
            string q; List<string> o, d;
            return Approvals.ReadQuestion(Input(json), out q, out o, out d);
        }

        static void Questions()
        {
            string q; List<string> o, d;
            bool ok = Approvals.ReadQuestion(Input("{\"questions\":[{\"question\":\"Pick\",\"multiSelect\":false,\"options\":[" + Options(3) + "]}]}"), out q, out o, out d);
            Check("one question with three options", ok && q == "Pick" && o.Count == 3 && d.Count == 3);
            Check("ten options are still buttons", Readable("{\"questions\":[{\"question\":\"P\",\"multiSelect\":false,\"options\":[" + Options(10) + "]}]}"));
            Check("eleven options are left to the window", !Readable("{\"questions\":[{\"question\":\"P\",\"multiSelect\":false,\"options\":[" + Options(11) + "]}]}"));
            Check("one option is not a choice", !Readable("{\"questions\":[{\"question\":\"P\",\"multiSelect\":false,\"options\":[" + Options(1) + "]}]}"));
            Check("multi-select is left to the window", !Readable("{\"questions\":[{\"question\":\"P\",\"multiSelect\":true,\"options\":[" + Options(3) + "]}]}"));
            Check("two questions are left to the window",
                !Readable("{\"questions\":[{\"question\":\"A\",\"multiSelect\":false,\"options\":[" + Options(2) + "]},{\"question\":\"B\",\"multiSelect\":false,\"options\":[" + Options(2) + "]}]}"));

            // "Allow and don't ask again": Claude Code's suggestions are offered only when every one can be put in words.
            HookEvent e = HookEvent.Parse("{\"hook_event_name\":\"PermissionRequest\",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"npm test\"},"
                + "\"permission_suggestions\":[{\"type\":\"addRules\",\"rules\":[{\"toolName\":\"Bash\",\"ruleContent\":\"npm test:*\"}],\"behavior\":\"allow\",\"destination\":\"localSettings\"}]}");
            string rules = Approvals.DescribeRules(e.PermissionSuggestions);
            Check("a suggested rule is described", rules != null && rules.Contains("Bash(npm test:*)"), rules);
            Check("a folder is described", Rules("[{\"type\":\"addDirectories\",\"directories\":[\"D:\\\\Work\"],\"destination\":\"session\"}]") != null);
            Check("accepting edits is described", Rules("[{\"type\":\"setMode\",\"mode\":\"acceptEdits\",\"destination\":\"session\"}]") != null);
            Check("another mode is not offered", Rules("[{\"type\":\"setMode\",\"mode\":\"bypassPermissions\",\"destination\":\"session\"}]") == null);
            Check("a deny rule is not offered", Rules("[{\"type\":\"addRules\",\"rules\":[{\"toolName\":\"Bash\"}],\"behavior\":\"deny\",\"destination\":\"session\"}]") == null);
            Check("an unknown kind among known ones is not offered",
                Rules("[{\"type\":\"setMode\",\"mode\":\"acceptEdits\",\"destination\":\"session\"},{\"type\":\"replaceRules\",\"destination\":\"session\"}]") == null);
            Check("no suggestions, nothing offered", Rules("[]") == null && Approvals.DescribeRules(null) == null);
        }

        // ---- usage limits ----

        static Dictionary<string, double> Use(params object[] modelCost)
        {
            var d = new Dictionary<string, double>();
            for (int i = 0; i < modelCost.Length; i += 2) d[(string)modelCost[i]] = Convert.ToDouble(modelCost[i + 1]);
            return d;
        }

        static void UsageLimits()
        {
            Check("the reply weighs most, reading the cache least", UsageMath.Cost(0, 0, 0, 1) == 5 && UsageMath.Cost(0, 0, 10, 0) == 1);

            // Two models, the dearer one counted 1.5 times: the fit finds that from the anchors alone.
            DateTime now = DateTime.UtcNow;
            var samples = new List<UsageMath.Sample>
            {
                new UsageMath.Sample { Cost = Use("sonnet", 1000000), Percent = 10, At = now.AddHours(-30) },
                new UsageMath.Sample { Cost = Use("opus", 1000000), Percent = 15, At = now.AddHours(-20) },
                new UsageMath.Sample { Cost = Use("sonnet", 2000000, "opus", 1000000), Percent = 35, At = now.AddHours(-10) },
                new UsageMath.Sample { Cost = Use("opus", 2000000), Percent = 30, At = now.AddHours(-2) },
            };
            UsageMath.Weights w = UsageMath.Fit(samples, now);
            double ratio = w.For("opus") / w.For("sonnet");
            Check("each model gets its own weight from the facts", ratio > 1.35 && ratio < 1.65, ratio.ToString("0.00"));
            Check("the estimate follows the anchors", Math.Abs(UsageMath.Apply(w, Use("sonnet", 1000000, "opus", 1000000)) - 25) < 2.5);
            Check("a model never seen gets the common weight", w.For("haiku") == w.Common);
            Check("no anchors, no weights", UsageMath.Fit(new List<UsageMath.Sample>(), now) == null);

            string cache = "{\"x\":{\"D:/a\":1,\"d:/a\":2},\"cachedUsageUtilization\":{\"fetchedAtMs\":1791635591572,\"accountUuid\":\"u\",\"utilization\":{"
                + "\"five_hour\":{\"utilization\":27,\"resets_at\":\"2026-10-10T16:49:59.771589+00:00\",\"note\":\"a } in text\"},"
                + "\"seven_day\":{\"utilization\":47,\"resets_at\":\"2026-10-14T13:59:59.771613+00:00\"},\"seven_day_opus\":null}},\"after\":3}";
            UsageAnchor a = UsageTracker.ParseCache(cache);
            Check("the cached figures are read from ~/.claude.json", a != null && a.Five == 27 && a.Week == 47
                && a.FiveReset == new DateTime(2026, 10, 10, 16, 49, 59, 771, DateTimeKind.Utc), a == null ? "null" : a.FiveReset.ToString("o"));
            Check("a file without them gives nothing", UsageTracker.ParseCache("{\"a\":1}") == null);
        }

        static string Rules(string json)
        {
            return Approvals.DescribeRules(Json.Parse(json) as System.Collections.IList);
        }

        // ---- settings ----

        static Config LoadConfig(string json)
        {
            string dir = Path.Combine(box, "data");
            foreach (string f in Directory.GetFiles(dir, "config.json*")) File.Delete(f);
            if (json != null) File.WriteAllText(Path.Combine(dir, "config.json"), json, Utf8);
            return Config.Load();
        }

        static void Settings()
        {
            string secret = Secret.Protect("s3cret");
            Check("secrets come back after encryption", Secret.Unprotect(secret) == "s3cret" && secret != "s3cret");

            Config c = LoadConfig("{\"NtfyServer\":\"http://192.168.0.10:8080\",\"NtfyUser\":\"phone\",\"NtfySecretProtected\":\"" + secret + "\"}");
            Check("old settings with another address are an own server", c.NtfyOwn && c.NtfyOwnServer == "http://192.168.0.10:8080" && c.NtfyServer == c.NtfyOwnServer);
            Check("the own server gets the sign-in", Notifier.Authorization(c) != null);
            c.NtfyOwn = false;
            c.ApplyNtfyServer();
            Check("switching to the public server keeps the own address", c.NtfyServer == Config.PublicServer && c.NtfyOwnServer == "http://192.168.0.10:8080");
            Check("the public server never gets the own sign-in", Notifier.Authorization(c) == null);

            c = LoadConfig("{\"NtfyServer\":\"https://ntfy.sh\",\"NtfyUser\":\"phone\",\"NtfySecretProtected\":\"" + secret + "\"}");
            Check("a leftover password on the public server is not sent", !c.NtfyOwn && Notifier.Authorization(c) == null);

            c = LoadConfig(null);
            Check("no settings file gives the defaults", c.Hotkey == Hotkey.Default && c.PhoneOnlyIfIgnored && c.NtfyServer == Config.PublicServer);

            c = LoadConfig("{\"Language\":\"de\",\"FirstRunDone\":true}");
            c.Save();
            c.Language = "fr";
            c.Save();
            string main = File.ReadAllText(Config.FilePath), backup = File.ReadAllText(Config.BackupPath);
            Check("a save keeps the previous file as the backup", main.Contains("\"fr\"") && backup.Contains("\"de\""));
            File.WriteAllText(Config.FilePath, "{\"Language\":\"fr\",\"Ntfy", Utf8);
            Check("a cut-off settings file is restored from the backup", Config.Load().Language == "de");
        }

        // ---- the log ----

        static void LogFile()
        {
            string path = Path.Combine(box, "big.log");
            File.WriteAllLines(path, Enumerable.Range(0, 1000).Select(i => "line " + i.ToString("D4") + new string('x', 80)), Utf8);
            Log.TrimToLastHalf(path, 50 * 1024);
            string[] kept = File.ReadAllLines(path);
            Check("an overgrown log keeps its newer half", kept.Length == 500 && kept[0].StartsWith("line 0500") && kept[499].StartsWith("line 0999"));
            long size = new FileInfo(path).Length;
            Log.TrimToLastHalf(path, 10 * 1024 * 1024);
            Check("a log under the limit is left alone", new FileInfo(path).Length == size);
        }

        // ---- the keyboard shortcut ----

        static void Shortcut()
        {
            uint mods, key;
            Check("Win+Alt+C", Hotkey.Parse("Win+Alt+C", out mods, out key) && mods == (0x8 | 0x1) && key == 0x43);
            Check("Ctrl+Shift+F12", Hotkey.Parse("Ctrl+Shift+F12", out mods, out key) && mods == (0x2 | 0x4) && key == 0x7B);
            Check("a key without a modifier is refused", !Hotkey.Parse("C", out mods, out key));
            Check("off is refused", !Hotkey.Parse("", out mods, out key));
            Check("an unknown modifier is refused", !Hotkey.Parse("Hyper+C", out mods, out key));
            Check("every offered choice is valid or off", Hotkey.Choices.All(h => h.Length == 0 || Hotkey.Parse(h, out mods, out key)));
        }

        // ---- language files ----

        static Dictionary<string, string> ReadLang(string file)
        {
            var map = new Dictionary<string, string>();
            foreach (string raw in File.ReadAllLines(file, Encoding.UTF8))
            {
                string line = raw.Trim('\uFEFF');
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq > 0) map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1);
            }
            return map;
        }

        static string Placeholders(string text)
        {
            return string.Join(",", Regex.Matches(text, @"\{(\d+)\}").Cast<Match>().Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x));
        }

        static void Languages()
        {
            string dir = Path.Combine(repo, "lang");
            Dictionary<string, string> en = ReadLang(Path.Combine(dir, "en.txt"));
            Check("the English file has the texts", en.Count > 200, en.Count + " keys");
            foreach (Loc.Lang l in Loc.Languages)
            {
                string file = Path.Combine(dir, l.Code + ".txt");
                if (!File.Exists(file)) { Fail(l.Code + ".txt is missing"); continue; }
                Dictionary<string, string> other = ReadLang(file);
                var missing = en.Keys.Where(k => !other.ContainsKey(k)).ToList();
                var extra = other.Keys.Where(k => !en.ContainsKey(k)).ToList();
                var badArgs = en.Keys.Where(k => other.ContainsKey(k) && Placeholders(other[k]) != Placeholders(en[k])).ToList();
                Check(l.Code + ": the same keys as English", missing.Count == 0 && extra.Count == 0,
                    "missing: " + string.Join(" ", missing.Take(5)) + "; extra: " + string.Join(" ", extra.Take(5)));
                Check(l.Code + ": the same {0} placeholders", badArgs.Count == 0, string.Join(" ", badArgs.Take(5)));
            }

            // Every text the program asks for by a fixed key must exist, or the key itself would be shown.
            var used = new SortedSet<string>();
            foreach (string cs in Directory.GetFiles(Path.Combine(repo, "src"), "*.cs"))
                foreach (Match m in Regex.Matches(File.ReadAllText(cs), "Loc\\.T\\(\"([^\"]+)\""))
                    used.Add(m.Groups[1].Value);
            // A key built in the code ("usage." + i) appears here as its beginning: some text must start with it.
            var unknown = used.Where(k => k.EndsWith(".") ? !en.Keys.Any(e => e.StartsWith(k)) : !en.ContainsKey(k)).ToList();
            Check("every key used in the code exists (" + used.Count + " keys)", unknown.Count == 0, string.Join(" ", unknown));
        }
    }
}
