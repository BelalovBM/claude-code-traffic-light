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
            Group("transcripts", Transcripts);
            Group("questions", Questions);
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

            File.AppendAllText(tp, reported + "\n", Utf8);
            Check("a reported task is no longer counted", TitleReader.PendingBackgroundTasks(tp) == 0);
            Apply(store, "{\"hook_event_name\":\"UserPromptSubmit\"," + b + ",\"prompt\":\"<task-notification>\"}");
            Check("the next turn ends the background wait", !s.InBackground);
            c = Apply(store, "{\"hook_event_name\":\"Stop\"," + b + "}");
            Check("the stop after it is \"finished\"", c.New == State.Idle && c.Old == State.Working);

            File.WriteAllText(tp, started + "\n", Utf8);
            Apply(store, "{\"hook_event_name\":\"UserPromptSubmit\"," + b + ",\"prompt\":\"serve\"}");
            Apply(store, "{\"hook_event_name\":\"Stop\"," + b + "}");
            s.BackgroundUntil = DateTime.Now.AddSeconds(-1);
            List<Change> expired = store.ExpireBackground();
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
