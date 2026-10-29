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
            Group("new versions", NewVersions);
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
            c = Apply(store, "{\"hook_event_name\":\"PreCompact\"," + b + ",\"trigger\":\"auto\"}");
            Check("a compaction prepared in the background of an idle session changes nothing",
                c == null && store.Find("st1").State == State.Idle && store.Overall() == Level.Idle);
            c = Apply(store, "{\"hook_event_name\":\"PreCompact\"," + b + ",\"trigger\":\"manual\"}");
            Check("a /compact in an idle session is shown", c.New == State.Compacting);
            Apply(store, "{\"hook_event_name\":\"PostCompact\"," + b + ",\"trigger\":\"manual\"}");
            // A summary prepared in the background is put to use on the next prompt: the session restarts and works on.
            Apply(store, "{\"hook_event_name\":\"UserPromptSubmit\"," + b + ",\"prompt\":\"go on\"}");
            c = Apply(store, "{\"hook_event_name\":\"SessionStart\"," + b + ",\"source\":\"compact\"}");
            Check("the restart after a compaction does not finish the turn", c == null || c.New == State.Working, c == null ? "" : c.New.ToString());
            c = Apply(store, "{\"hook_event_name\":\"PostCompact\"," + b + ",\"trigger\":\"auto\"}");
            Check("and the turn goes on after it", c.New == State.Working);
            Apply(store, "{\"hook_event_name\":\"Stop\"," + b + "}");

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
            Change c = Apply(store, "{\"hook_event_name\":\"StopFailure\"," + b + ",\"error\":\"rate_limit\",\"last_assistant_message\":\"You've hit your limit В· resets 9pm\"}");
            Session s = store.Find("sf1");
            Check("an API error is not \"finished\": the session waits for the user", c.New == State.Waiting && store.Overall() == Level.Waiting);
            Check("the kind of error and Claude's words are kept", s.Failure == "rate_limit" && s.FailureText.Contains("resets 9pm"));
            Apply(store, "{\"hook_event_name\":\"UserPromptSubmit\"," + b + ",\"prompt\":\"again\"}");
            Check("the next prompt clears the error", s.Failure == null && s.State == State.Working);

            // What Claude said when it finished, for the "done" notice: the first line, without Markdown marks.
            Check("the summary is the first line without a heading mark", TrayApp.SummaryLine("## Done\n\n**Added** tests") == "Done");
            Check("list and code marks are dropped", TrayApp.SummaryLine("\n- `npm test` passes, **13** of 13") == "npm test passes, 13 of 13");
            Check("no message, no summary", TrayApp.SummaryLine(null) == "");
            Check("Claude Code's permission message gives its tool, to be shown in the program's language",
                TrayApp.PermissionTool("Claude needs your permission to use AskUserQuestion") == "AskUserQuestion"
                && TrayApp.PermissionTool("Claude needs your permission to use Bash.") == "Bash"
                && TrayApp.PermissionTool("Claude is waiting for your input") == null);

            // How full the context is: the last reply of the main conversation, not one of a subagent.
            string tp = Transcript("ctx",
                "{\"isSidechain\":false,\"message\":{\"model\":\"claude-opus-5-5\",\"id\":\"m1\",\"role\":\"assistant\",\"content\":[],\"usage\":{\"input_tokens\":10,\"cache_creation_input_tokens\":2000,\"cache_read_input_tokens\":400000,\"output_tokens\":500}},\"type\":\"assistant\"}",
                "{\"isSidechain\":true,\"message\":{\"model\":\"claude-haiku-4-5\",\"id\":\"m2\",\"role\":\"assistant\",\"content\":[],\"usage\":{\"input_tokens\":5,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":9000,\"output_tokens\":50}},\"type\":\"assistant\"}");
            long tokens; string model;
            Check("the context is the input of the last main reply", TitleReader.ReadContext(tp, out tokens, out model) && tokens == 402010 && model == "claude-opus-5-5",
                tokens + " " + model);
            string compacted = "{\"isSidechain\":false,\"type\":\"system\",\"subtype\":\"compact_boundary\",\"compactMetadata\":{\"trigger\":\"manual\",\"preTokens\":402010}}";
            tp = Transcript("ctx2", File.ReadAllText(tp).TrimEnd('\n', '\r'), compacted);
            Check("after a compaction the old size is not shown", TitleReader.ReadContext(tp, out tokens, out model) && tokens == 0, tokens.ToString());
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
            string stopped = Transcript("bg-stopped", started,
                "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"id\":\"t2\",\"name\":\"TaskStop\",\"input\":{\"task_id\":\"job7\"}}]}}");
            Check("a task Claude stopped itself is no longer counted", TitleReader.PendingBackgroundTasks(stopped) == 0);
            Check("Claude Code's own list of background tasks is read from Stop",
                HookEvent.Parse("{\"hook_event_name\":\"Stop\",\"session_id\":\"x\",\"background_tasks\":[]}").BackgroundTasks == 0
                && HookEvent.Parse("{\"hook_event_name\":\"Stop\",\"session_id\":\"x\",\"background_tasks\":[{\"id\":\"a\"},{\"id\":\"b\"}]}").BackgroundTasks == 2
                && HookEvent.Parse("{\"hook_event_name\":\"Stop\",\"session_id\":\"x\"}").BackgroundTasks == null);
            HookEvent listed = HookEvent.Parse("{\"hook_event_name\":\"Stop\",\"session_id\":\"x\",\"background_tasks\":[{\"id\":\"a\",\"type\":\"shell\",\"status\":\"completed\",\"description\":\"tests\"},{\"id\":\"b\",\"type\":\"monitor\",\"status\":\"running\",\"description\":\"watch\"}]}");
            Check("a task the list shows as ended does not count, and the list is kept for the log",
                listed.BackgroundTasks == 1 && listed.BackgroundTaskText == "shell completed: tests; monitor running: watch", listed.BackgroundTaskText);
            Check("the watch on a published artifact is no background work",
                HookEvent.Parse("{\"hook_event_name\":\"Stop\",\"session_id\":\"x\",\"background_tasks\":[{\"id\":\"a\",\"type\":\"monitor\",\"status\":\"running\",\"description\":\"live updates for artifact https://claude.ai/artifact/x (re-publishes)\"}]}").BackgroundTasks == 0);
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
            Check("the reply weighs most, reading the cache least", UsageMath.Cost(0, 0, 0, 1) == 5 && UsageMath.Cost(0, 0, 40, 0) == 1);

            // At API prices: Opus 5.5 is $4 in, $20 out, $0.20 for reading the cache, writing it 1.25 or 2 times the input.
            double? usd = ApiPrices.Usd("claude-opus-5-5", 1000000, 1000000, 1000000, 1000000, 1000000, 0, false);
            Check("a reply at API prices counts every kind of token, an hour's cache write dearer", usd.HasValue && Math.Abs(usd.Value - (4 + 5 + 8 + 0.2 + 20)) < 1e-9, usd.ToString());
            usd = ApiPrices.Usd("claude-haiku-4-5-20251001", 1000000, 0, 0, 1000000, 0, 3, false);
            Check("a dated model takes its base price, reading at a tenth, web searches on top", usd.HasValue && Math.Abs(usd.Value - (1 + 0.1 + 0.03)) < 1e-9, usd.ToString());
            Check("fast mode costs twice as much", ApiPrices.Usd("claude-opus-5-5", 0, 0, 0, 0, 1000000, 0, true) == 40);
            Check("a model of no known price is not guessed", ApiPrices.Usd("claude-opus-6", 1, 0, 0, 0, 1, 0, false) == null && ApiPrices.Usd("<synthetic>", 1, 0, 0, 0, 1, 0, false) == null);
            Check("a long prompt is dearer where the price says so",
                ApiPrices.Usd("claude-haiku-5-5", 50000, 0, 0, 0, 0, 0, false) == 0.005 && ApiPrices.Usd("claude-haiku-5-5", 200000, 0, 0, 0, 0, 0, false) == 0.1);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            Loc.Load("en");
            Check("token counts are short", ApiPrices.Tokens(740, inv) == "740" && ApiPrices.Tokens(35400, inv) == "35k"
                && ApiPrices.Tokens(1250000, inv) == "1.3M" && ApiPrices.Tokens(120400000, inv) == "120M" && ApiPrices.Tokens(3160000000, inv) == "3.2B",
                ApiPrices.Tokens(1250000, inv));
            Check("dollars have cents only below ten", ApiPrices.Dollars(0.4249, inv) == "$0.42" && ApiPrices.Dollars(1089.4, inv) == "$1,089");
            File.WriteAllText(ApiPrices.FilePath, "{\"claude-opus-6\":{\"Input\":3,\"Output\":15}}", Utf8);
            ApiPrices.Reload();
            Check("a price added in api-prices.json counts, the built-in ones stay",
                ApiPrices.Usd("claude-opus-6", 1000000, 0, 0, 1000000, 0, 0, false) == 3.3 && ApiPrices.For("claude-opus-5-5") != null);
            File.Delete(ApiPrices.FilePath);
            ApiPrices.Reload();

            // LiteLLM's price file: only Anthropic's own Claude models, in dollars per million, with a long-prompt price.
            string lite = "{\"sample_spec\":{\"input_cost_per_token\":1},"
                + "\"claude-sonnet-5-5\":{\"litellm_provider\":\"anthropic\",\"input_cost_per_token\":2e-06,\"output_cost_per_token\":1e-05,\"cache_read_input_token_cost\":2e-07},"
                + "\"claude-haiku-5-5\":{\"litellm_provider\":\"anthropic\",\"input_cost_per_token\":1e-07,\"output_cost_per_token\":5e-07,\"input_cost_per_token_above_100k_tokens\":5e-07,\"output_cost_per_token_above_100k_tokens\":2.5e-06},"
                + "\"claude-opus-6\":{\"litellm_provider\":\"anthropic\",\"input_cost_per_token\":5e-06,\"output_cost_per_token\":2.5e-05},"
                + "\"bedrock/claude-x\":{\"litellm_provider\":\"bedrock\",\"input_cost_per_token\":1},"
                + "\"claude-on-vertex\":{\"litellm_provider\":\"vertex_ai\",\"input_cost_per_token\":1,\"output_cost_per_token\":1}}";
            var loaded = ApiPrices.ParseLiteLlm(lite);
            Check("LiteLLM's file gives only Anthropic's Claude models", loaded.Count == 3 && loaded.ContainsKey("claude-opus-6") && !loaded.ContainsKey("claude-on-vertex"));
            Check("prices are per million tokens, with the long-prompt price",
                loaded["claude-sonnet-5-5"].Input == 2 && loaded["claude-sonnet-5-5"].CacheRead == 0.2
                && loaded["claude-haiku-5-5"].LongFrom == 100000 && loaded["claude-haiku-5-5"].LongOutput == 2.5);
            Check("a file without Claude models gives nothing", ApiPrices.ParseLiteLlm("{\"gpt\":{\"litellm_provider\":\"openai\"}}").Count == 0);
            ApiPrices.SetOwn("claude-opus-6", 9, 45, null);
            var changes = ApiPrices.Compare(loaded);
            Check("the comparison names a changed price and a price typed in, not an unchanged one",
                changes.Count == 2 && changes.Any(x => x.Model == "claude-sonnet-5-5" && x.Old.CacheRead == 0.10) && changes.Any(x => x.Model == "claude-opus-6" && x.Own));
            ApiPrices.Apply(loaded, new string[0], new DateTime(2026, 10, 29, 1, 40, 0));
            Check("applying keeps a price typed in unless chosen, and notes where the others came from",
                ApiPrices.For("claude-opus-6").Input == 9 && ApiPrices.For("claude-sonnet-5-5").Source == ApiPrices.FromLiteLlm
                && ApiPrices.For("claude-sonnet-5-5").Date == "2026-10-29 01:40" && ApiPrices.For("claude-sonnet-5-5").CacheRead == 0.2);
            ApiPrices.Apply(loaded, new[] { "claude-opus-6" }, DateTime.Now);
            Check("a price typed in is replaced when chosen", ApiPrices.For("claude-opus-6").Input == 5);
            ApiPrices.Price same = loaded["claude-opus-6"];
            ApiPrices.SetOwn("claude-opus-6", same.Input, same.Output, same.CacheRead);
            ApiPrices.Apply(loaded, new string[0], DateTime.Now);
            Check("a price typed in that is the same as the loaded one goes over to it",
                ApiPrices.For("claude-opus-6").Source == ApiPrices.FromLiteLlm);
            ApiPrices.Clear();
            Check("default prices drop what was typed and loaded", !File.Exists(ApiPrices.FilePath) && ApiPrices.For("claude-opus-6") == null
                && ApiPrices.For("claude-sonnet-5-5").CacheRead == 0.10 && ApiPrices.For("claude-sonnet-5-5").Source == null);

            // The statistics of a period: two sessions, one with a subagent and a pause of two hours, one with a large
            // context; Opus 5.5 at $4 in, $20 out, $0.20 for reading the cache; one percent of the week per 100k units.
            DateTime st0 = DateTime.UtcNow.AddHours(-5);
            var sw = new UsageMath.Weights { Common = 1e-5 };
            var steps = new List<UsageStep>
            {
                new UsageStep { At = st0, Model = "claude-opus-5-5", Session = "a", Path = "a.jsonl", Write1h = 100000, Output = 1000 },
                new UsageStep { At = st0.AddMinutes(1), Model = "claude-opus-5-5", Session = "a", Path = "a/subagents/x.jsonl", Sidechain = true, Write1h = 200000 },
                new UsageStep { At = st0.AddHours(2), Model = "claude-opus-5-5", Session = "a", Path = "a.jsonl", Read = 100000, Write1h = 100000 },
                new UsageStep { At = st0.AddHours(3), Model = "claude-opus-5-5", Session = "b", Path = "b.jsonl", Read = 700000, Output = 2000 },
                new UsageStep { At = st0.AddHours(3), Model = "claude-opus-6", Session = "b", Path = "b.jsonl", Input = 10 },
                new UsageStep { At = st0.AddDays(-40), Model = "claude-opus-5-5", Session = "old", Path = "old.jsonl", Output = 1000 },
            };
            var comps = new List<UsageCompaction> { new UsageCompaction { At = st0.AddHours(1), Session = "a" }, new UsageCompaction { At = st0.AddDays(-40), Session = "old" } };
            UsageReport rep = UsageStats.Build(steps, comps, st0.AddDays(-30), DateTime.UtcNow, sw);
            double total = 0.8 + 0.02 + 1.6 + 0.02 + 0.8 + 0.14 + 0.04;
            Check("the total of a period is the sum of its steps, older ones left out", Math.Abs(rep.Total.Usd - total) < 1e-9 && rep.Total.Count == 5, rep.Total.Usd.ToString());
            Check("the parts add up to the total", Math.Abs(rep.Reading.Usd + rep.Writing.Usd + rep.Replies.Usd - total) < 1e-9
                && Math.Abs(rep.Reading.Usd - 0.16) < 1e-9 && Math.Abs(rep.Replies.Usd - 0.06) < 1e-9);
            Check("the limit is counted by the weights of the week", rep.LimitKnown && Math.Abs(rep.Total.Weeks.Value - steps.Take(5).Sum(s => s.Cost) * 1e-7) < 1e-12);
            Check("a large write after a pause longer than the cache lasts is a cold start, its write part only",
                rep.ColdStarts.Count == 1 && Math.Abs(rep.ColdStarts.Usd - 0.8) < 1e-9);
            Check("a step with a large context counts as one", rep.LargeContext.Count == 1 && Math.Abs(rep.LargeContext.Usd - 0.18) < 1e-9);
            Check("compactions of the period are counted", rep.Compactions == 1);
            Check("a subagent counts with its session, named by the session's own transcript",
                rep.Sessions.Count == 2 && rep.Sessions[0].Session == "a" && rep.Sessions[0].Steps == 3 && rep.Sessions[0].Path == "a.jsonl" && rep.Sessions[0].Model == "claude-opus-5-5");
            Check("the largest context of a session is kept", rep.Sessions[1].MaxContext == 700000);
            Check("a model without a price is named", rep.Unpriced.SequenceEqual(new[] { "claude-opus-6" }));
            Check("the days add up to the total", Math.Abs(rep.Days.Sum(d => d.Value.Usd) - total) < 1e-9);
            Check("without a subscription there is no limit", !UsageStats.Build(steps, comps, st0.AddDays(-30), DateTime.UtcNow, null).Total.Weeks.HasValue);

            // The replies the self-check writes are read like real ones: the statistics window has something to show.
            string demo = Path.Combine(box, "claude", "projects", "demo");
            Directory.CreateDirectory(demo);
            for (int i = 0; i < SelfCheckDemo.Ids.Length; i++)
                File.WriteAllText(Path.Combine(demo, SelfCheckDemo.Ids[i] + ".jsonl"), SelfCheckSetup.DemoReplies(i), Utf8);
            // The context limit comes from a compaction made at the limit, not from a summary prepared in the background.
            string reply = "{\"timestamp\":\"" + DateTime.UtcNow.AddHours(-2).ToString("o") + "\",\"isSidechain\":false,\"message\":{\"model\":\"claude-test-ctx\",\"id\":\"MID\",\"role\":\"assistant\",\"content\":[],\"usage\":{\"input_tokens\":10,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":1000,\"output_tokens\":5}},\"type\":\"assistant\"}";
            string ctxDir = Path.Combine(box, "claude", "projects", "ctxtest");
            Directory.CreateDirectory(ctxDir);
            File.WriteAllText(Path.Combine(ctxDir, "ctx-limit.jsonl"), string.Join("\n",
                reply.Replace("MID", "c1"),
                "{\"type\":\"system\",\"subtype\":\"compact_boundary\",\"compactMetadata\":{\"trigger\":\"auto\",\"preTokens\":968000,\"postTokens\":10000}}",
                reply.Replace("MID", "c2"),
                "{\"type\":\"system\",\"subtype\":\"compact_boundary\",\"compactMetadata\":{\"trigger\":\"auto\",\"preTokens\":481000,\"postTokens\":15000,\"durationMs\":47,\"precomputed\":true}}") + "\n", Utf8);
            var tracker = new UsageTracker();
            tracker.Start();
            UsageReport demoReport = null;
            for (int i = 0; i < 40 && (demoReport == null || demoReport.Total.Count == 0); i++)
            {
                System.Threading.Thread.Sleep(250);
                demoReport = tracker.Report(DateTime.UtcNow.AddDays(-30), DateTime.UtcNow);
            }
            long ctxLimit = tracker.ContextLimit("claude-test-ctx");
            tracker.Stop();
            Directory.Delete(demo, true);
            Directory.Delete(ctxDir, true);
            Check("a summary prepared in the background does not lower the context limit", ctxLimit == 968000, ctxLimit.ToString());
            Check("the self-check's replies are read: sessions, cold starts after its breaks, dollars",
                demoReport.Total.Count > 50 && demoReport.Sessions.Count(x => x.Session != "ctx-limit") == SelfCheckDemo.Ids.Length && demoReport.ColdStarts.Count > 0 && demoReport.Total.Usd > 0,
                demoReport.Total.Count + " steps, " + demoReport.Sessions.Count + " sessions, " + demoReport.ColdStarts.Count + " cold");

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
            var named = UsageMath.Fit(new List<UsageMath.Sample>
            {
                new UsageMath.Sample { Cost = Use("claude-sonnet-5-5", 1000000), Percent = 10, At = now.AddHours(-3) },
                new UsageMath.Sample { Cost = Use("claude-opus-5-5", 1000000), Percent = 15, At = now.AddHours(-2) },
            }, now);
            Check("a new model of a known family takes that family's weight",
                named.For("claude-opus-6") == named.For("claude-opus-5-5") && UsageMath.Family("claude-opus-5-5") == "claude-opus");
            Check("a model of an unknown family takes the common weight", named.For("claude-lyra-1") == named.Common);
            Check("the plan is read from ~/.claude.json", UsageTracker.ParsePlan("{\"oauthAccount\":{\"organizationType\": \"claude_max\"}}") == "claude_max");
            Check("no anchors, no weights", UsageMath.Fit(new List<UsageMath.Sample>(), now) == null);
            // A model used less, on top of the main one: its figures show it counts half, and the fit follows them.
            var minor = UsageMath.Fit(new List<UsageMath.Sample>
            {
                new UsageMath.Sample { Cost = Use("opus", 3000000), Percent = 45, At = now.AddHours(-6) },
                new UsageMath.Sample { Cost = Use("opus", 5700000, "sonnet", 800000), Percent = 92, At = now.AddHours(-4) },
                new UsageMath.Sample { Cost = Use("opus", 3200000, "sonnet", 2000000), Percent = 63, At = now.AddHours(-1) },
            }, now);
            double half = minor.For("sonnet") / minor.For("opus");
            Check("a model used less still gets its own weight", half > 0.4 && half < 0.65, half.ToString("0.00"));
            // /usage in the background: only after work, when the estimate may have drifted, and never past the brake.
            DateTime t0 = new DateTime(2026, 10, 13, 15, 0, 0, DateTimeKind.Utc);
            Func<double, double, double, LimitView> view = (exact, estimate, minutesAgo) => new LimitView
            {
                Known = true, HasExact = true, ExactPercent = exact, Percent = estimate, ExactAt = t0.AddMinutes(-minutesAgo),
            };
            LimitView calm = view(40, 44, 10);
            Check("/usage is not asked while nothing worked",
                UsageRefresh.Reason(view(40, 60, 10), calm, t0.AddMinutes(-1), t0.AddMinutes(-2), t0) == null);
            Check("a small rise needs no /usage, ten points do",
                UsageRefresh.Reason(calm, calm, t0.AddMinutes(-9), t0, t0) == null
                && UsageRefresh.Reason(view(40, 50.5, 10), calm, t0.AddMinutes(-9), t0, t0) != null);
            Check("from 90% on two points are enough, and the exact figure may be a quarter of an hour old at most",
                UsageRefresh.Reason(view(90, 92.2, 3), calm, t0.AddMinutes(-9), t0, t0) != null
                && UsageRefresh.Reason(view(91, 91.5, 16), calm, t0.AddMinutes(-9), t0, t0) != null
                && UsageRefresh.Reason(view(91, 91.5, 5), calm, t0.AddMinutes(-9), t0, t0) == null);
            Check("an exact figure from Claude Code itself needs nothing",
                UsageRefresh.Reason(new LimitView { Known = true, Exact = true, HasExact = true, Percent = 97 }, calm, t0.AddMinutes(-9), t0, t0) == null);
            Check("the brake: five minutes at least, twice as long after each failure up to an hour",
                !UsageRefresh.Allowed(t0.AddMinutes(-4), 0, 0, t0) && UsageRefresh.Allowed(t0.AddMinutes(-5), 0, 0, t0)
                && !UsageRefresh.Allowed(t0.AddMinutes(-19), 2, 0, t0) && UsageRefresh.Allowed(t0.AddMinutes(-20), 2, 0, t0)
                && UsageRefresh.Gap(50) == TimeSpan.FromMinutes(60));
            Check("the brake: the daily limit lies above what five minutes let through, and a clock put back cannot open it",
                UsageRefresh.PerDay > 24 * 60 / 5
                && !UsageRefresh.Allowed(t0.AddHours(-3), 0, UsageRefresh.PerDay, t0)
                && UsageRefresh.Allowed(t0.AddHours(-3), 0, UsageRefresh.PerDay - 1, t0)
                && !UsageRefresh.Allowed(t0.AddHours(2), 0, 0, t0));
            Check("the threshold comes down as the estimate misses more, never below two points",
                UsageRefresh.Rise(10, 2, 0.03) == 10 && UsageRefresh.Rise(10, 2, 0.1) == 5
                && UsageRefresh.Rise(10, 2, 0.2) == 2.5 && UsageRefresh.Rise(10, 2, 0.6) == 2);
            LimitView shown = new LimitView { Known = true, HasExact = true, ExactPercent = 40, Percent = 50, Reset = t0.AddHours(3) };
            double missed = UsageRefresh.Learn(0.03, shown, 48, t0.AddHours(3));
            Check("a miss of two points on ten added is learnt, smoothed with the earlier ones",
                Math.Abs(missed - (0.5 * 0.03 + 0.5 * 0.2)) < 1e-9, missed.ToString());
            Check("a small addition, another window or an exact figure teach nothing",
                UsageRefresh.Learn(0.03, new LimitView { Known = true, HasExact = true, ExactPercent = 40, Percent = 41, Reset = t0.AddHours(3) }, 45, t0.AddHours(3)) == 0.03
                && UsageRefresh.Learn(0.03, shown, 48, t0.AddHours(8)) == 0.03
                && UsageRefresh.Learn(0.03, new LimitView { Known = true, Exact = true, HasExact = true, Percent = 50, Reset = t0.AddHours(3) }, 48, t0.AddHours(3)) == 0.03);
            Check("with a missing estimate, two points of rise are enough to ask",
                UsageRefresh.Reason(view(40, 43, 10), calm, 0.25, 0.03, t0.AddMinutes(-9), t0, t0) != null
                && UsageRefresh.Reason(view(40, 43, 10), calm, 0.03, 0.03, t0.AddMinutes(-9), t0, t0) == null);
            Check("a sign-in counts as without a subscription only with neither a plan nor figures of /usage",
                UsageTracker.NoSubscriptionIn("{\"oauthAccount\":{\"emailAddress\":\"x\"}}")
                && !UsageTracker.NoSubscriptionIn("{\"oauthAccount\":{\"organizationType\":\"claude_pro\"}}")
                && !UsageTracker.NoSubscriptionIn("{\"cachedUsageUtilization\":{\"fetchedAtMs\":1}}"));
            string head = "You are currently using your subscription to power your Claude Code usage\n\n";
            Check("the figures of /usage are read from either line",
                UsageRefresh.Figures(head + "Current session: 30% used · resets 10pm\nCurrent week (all models): 23% used · resets Oct 21") == "5 hours 30%, week 23%"
                && UsageRefresh.Figures(head + "Current week (all models): 23% used · resets Oct 21") == "week 23%"
                && UsageRefresh.Figures(head + "What's contributing to your limits usage?") == null);
            Check("an answer with only the header is no breakdown, anything else without figures is",
                UsageRefresh.OnlyHeader(head) && !UsageRefresh.OnlyHeader("Error: not logged in") && !UsageRefresh.OnlyHeader(head + "Current session: 3% used"));
            Check("the same figures again replace the last one instead of piling up",
                UsageTracker.SameFigures(new UsageAnchor { Five = 40, Week = 20, FiveResetMs = 1000000, WeekResetMs = 5000000 },
                                         new UsageAnchor { Five = 40, Week = 20, FiveResetMs = 1000000 + 60000, WeekResetMs = 5000000 })
                && !UsageTracker.SameFigures(new UsageAnchor { Five = 40, Week = 20 }, new UsageAnchor { Five = 41, Week = 20 }));
            // An early reset: the figure fell within the same window, so the window starts at the figure before the fall.
            DateTime w0 = new DateTime(2026, 10, 7, 19, 0, 0, DateTimeKind.Utc);
            var figures = new[]
            {
                new KeyValuePair<DateTime, double>(w0.AddDays(5), 85),
                new KeyValuePair<DateTime, double>(w0.AddDays(5).AddMinutes(5), 0),
                new KeyValuePair<DateTime, double>(w0.AddDays(5).AddHours(14), 12),
            };
            Check("an early reset moves the start of the window",
                UsageMath.WindowStart(figures, w0, w0.AddDays(6)) == w0.AddDays(5)
                && UsageMath.WindowStart(figures, w0, w0.AddDays(4)) == w0
                && UsageMath.WindowStart(figures.Take(1), w0, w0.AddDays(6)) == w0);

            string cache = "{\"x\":{\"D:/a\":1,\"d:/a\":2},\"cachedUsageUtilization\":{\"fetchedAtMs\":1791635591572,\"accountUuid\":\"u\",\"utilization\":{"
                + "\"five_hour\":{\"utilization\":27,\"resets_at\":\"2026-10-10T16:49:59.771589+00:00\",\"note\":\"a } in text\"},"
                + "\"seven_day\":{\"utilization\":47,\"resets_at\":\"2026-10-14T13:59:59.771613+00:00\"},\"seven_day_opus\":null}},\"after\":3}";
            UsageAnchor a = UsageTracker.ParseCache(cache);
            Check("the cached figures are read from ~/.claude.json", a != null && a.Five == 27 && a.Week == 47
                && a.FiveReset == new DateTime(2026, 10, 10, 16, 49, 59, 771, DateTimeKind.Utc), a == null ? "null" : a.FiveReset.ToString("o"));
            Check("a file without them gives nothing", UsageTracker.ParseCache("{\"a\":1}") == null);
        }

        static void NewVersions()
        {
            Check("a later version is newer", Updates.IsNewer("0.9.6", "0.9.5"));
            Check("0.10.0 is newer than 0.9.5 (numbers, not text)", Updates.IsNewer("0.10.0", "0.9.5"));
            Check("the same version is not newer", !Updates.IsNewer("0.9.5", "0.9.5"));
            Check("an older version is not newer", !Updates.IsNewer("0.9.4", "0.9.5"));
            Check("a -beta ending is ignored", Updates.IsNewer("1.0.0-beta", "0.9.5") && !Updates.IsNewer("0.9.5-beta", "0.9.5"));
            Check("nonsense is never newer", !Updates.IsNewer("latest", "0.9.5"));
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
