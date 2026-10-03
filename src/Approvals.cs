using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace Semaphore
{
    // Answering Claude Code's permission prompts from the phone.
    //
    // A PermissionRequest hook (this exe, started with --hook) asks this class for a decision over a
    // dedicated pipe and waits. A push with Allow/Deny buttons goes to the phone; each button posts a
    // one-time token to a separate secret "reply" topic that this class listens on. Whatever goes wrong
    // (disabled, muted, no answer in time, answered on the computer first), the hook gets an empty
    // reply and Claude Code shows its normal prompt: nothing is ever approved without a valid answer.
    sealed class Approvals
    {
        public static readonly string PipeName = "ClaudeCodeTrafficLight.Approve" + AppPaths.InstanceSuffix;

        sealed class Pending
        {
            public string Token, SessionId, Tool, Summary;
            // A multiple-choice question from Claude: the options and the original tool input.
            public string Question;
            public List<string> Options, Descriptions;
            public IDictionary<string, object> Input;
            // The push with the buttons went out, so a second "waiting" push would only repeat it.
            public bool Pushed;
            public DateTime PushedAt;
            // The answer (if any) was given on this computer, not on the phone.
            public bool ByComputer;
            public DateTime Deadline;
            public string Result; // "allow", "deny", "pick:<n>", or null for "ask on the computer"
            public bool Used;
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
        }

        readonly TrayApp app;
        readonly object gate = new object();
        readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>();
        volatile bool stopped;
        int generation;
        string listenKey;
        HttpWebRequest currentRequest;

        public volatile string Status = "";

        public Approvals(TrayApp app)
        {
            this.app = app;
        }

        public void Start()
        {
            new Thread(PipeLoop) { IsBackground = true, Name = "ApprovalPipe" }.Start();
        }

        public void Stop()
        {
            stopped = true;
            lock (gate)
            {
                generation++;
                if (currentRequest != null) try { currentRequest.Abort(); } catch { }
            }
        }

        // ---- the hook side: one blocking request per permission prompt ----

        void PipeLoop()
        {
            NamedPipeServerStream server = null;
            while (!stopped)
            {
                try
                {
                    if (server == null) server = NewServer();
                    server.WaitForConnection();
                    NamedPipeServerStream connected = server;
                    server = NewServer();
                    new Thread(() => Serve(connected)) { IsBackground = true, Name = "ApprovalRequest" }.Start();
                }
                catch (Exception ex)
                {
                    Log.Write("Approval pipe error: " + ex.Message);
                    if (server != null) { server.Dispose(); server = null; }
                    Thread.Sleep(250);
                }
            }
        }

        static NamedPipeServerStream NewServer()
        {
            return new NamedPipeServerStream(PipeName, PipeDirection.InOut, 16, PipeTransmissionMode.Byte);
        }

        void Serve(NamedPipeServerStream pipe)
        {
            try
            {
                using (pipe)
                {
                    string payload = Encoding.UTF8.GetString(ReadMessage(pipe));
                    WriteMessage(pipe, Decide(payload));
                }
            }
            catch (Exception ex) { Log.Write("Approval request failed: " + ex.Message); }
        }

        // What became of every request goes to requests.log next to the program (see "Claude Code" > "Where things are
        // kept"): when, which session, the start of what was asked, how it ended. The file is cut in half when it
        // reaches about 200 KB, so it never grows without end.
        static readonly object fileGate = new object();

        void Record(Pending p, string session, string result, bool timedOut)
        {
            string outcome;
            if (result == "allow") outcome = Loc.T("journal.allowed") + " " + Loc.T(p.ByComputer ? "journal.pc" : "journal.phone");
            else if (result == "deny") outcome = Loc.T("journal.denied") + " " + Loc.T(p.ByComputer ? "journal.pc" : "journal.phone");
            else if (result != null && result.StartsWith("pick:", StringComparison.Ordinal) && p.Options != null)
            {
                int index = int.Parse(result.Substring(5), CultureInfo.InvariantCulture);
                outcome = Loc.T("journal.picked", p.Options[index]) + " " + Loc.T(p.ByComputer ? "journal.pc" : "journal.phone");
            }
            else if (timedOut) outcome = Loc.T("journal.expired");
            else outcome = Loc.T("journal.screen");

            string what = p.Options != null ? p.Question : p.Tool + (string.IsNullOrEmpty(p.Summary) ? "" : ": " + Clip(p.Summary, 80));
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + session + " · " + Clip(what, 120) + " -> " + outcome;
            if (AppPaths.Disabled) return;
            try
            {
                string path = Path.Combine(AppPaths.DataDir, "requests.log");
                lock (fileGate)
                {
                    Log.TrimToLastHalf(path, 200 * 1024);
                    File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch (Exception ex) { Log.Write("Requests journal write failed: " + ex.Message); }
        }
        public sealed class Request
        {
            public string Token, SessionId, Tool, Summary, Question;
            public List<string> Options, Descriptions;
            public bool Pushed;
            public DateTime PushedAt;
            // When the program stops waiting for an answer (default: no deadline known).
            public DateTime Deadline;
        }

        // A question with one question and 2-10 single-choice options can be answered from the panel and (the first
        // three) by buttons on the phone; anything else (several questions, multi-select) is left to the normal prompt.
        internal static bool ReadQuestion(IDictionary<string, object> input, out string question, out List<string> options, out List<string> descriptions)
        {
            question = null;
            options = null;
            descriptions = null;
            try
            {
                var questions = input == null || !input.ContainsKey("questions") ? null : input["questions"] as System.Collections.IList;
                if (questions == null || questions.Count != 1) return false;
                var q = questions[0] as IDictionary<string, object>;
                if (q == null) return false;
                object multi;
                if (q.TryGetValue("multiSelect", out multi) && multi is bool && (bool)multi) return false;
                question = q.ContainsKey("question") ? q["question"] as string : null;
                var list = q.ContainsKey("options") ? q["options"] as System.Collections.IList : null;
                if (string.IsNullOrEmpty(question) || list == null || list.Count < 2 || list.Count > 10) return false;
                options = new List<string>();
                descriptions = new List<string>();
                foreach (object o in list)
                {
                    var option = o as IDictionary<string, object>;
                    string label = option != null && option.ContainsKey("label") ? option["label"] as string : null;
                    if (string.IsNullOrEmpty(label)) return false;
                    options.Add(label);
                    descriptions.Add(option.ContainsKey("description") ? option["description"] as string ?? "" : "");
                }
                return true;
            }
            catch { return false; }
        }

        // What goes back to Claude Code: the question with the chosen answer filled in.
        static string AnswerJson(Pending p, int index)
        {
            var updated = new Dictionary<string, object>(p.Input);
            var answers = new Dictionary<string, object>();
            answers[p.Question] = p.Options[index];
            updated["answers"] = answers;
            return new JavaScriptSerializer().Serialize(updated);
        }

        // The prompts that wait for an answer right now (for the tray menu).
        public List<Request> Waiting()
        {
            var list = new List<Request>();
            lock (gate)
                foreach (Pending p in pending.Values)
                    if (p.SessionId != "test" && p.Result == null && !p.Used)
                        list.Add(new Request
                        {
                            Token = p.Token, SessionId = p.SessionId, Tool = p.Tool, Summary = p.Summary,
                            Question = p.Question, Options = p.Options, Descriptions = p.Descriptions, Pushed = p.Pushed, PushedAt = p.PushedAt, Deadline = p.Deadline,
                        });
            return list;
        }

        // An answer given in the tray menu; works like a button on the phone, once.
        public void Answer(string token, bool allow)
        {
            MarkByComputer(token);
            Resolve((allow ? "allow:" : "deny:") + token);
        }

        public void AnswerOption(string token, int index)
        {
            MarkByComputer(token);
            Resolve("pick:" + token + ":" + index);
        }

        void MarkByComputer(string token)
        {
            lock (gate)
            {
                Pending p;
                if (pending.TryGetValue(token, out p)) p.ByComputer = true;
            }
        }

        // The phone got buttons that no longer work: say why, and what is left to do. Silence would look like a
        // broken program.
        void FollowUp(Pending p, string result, bool timedOut, string label, int minutes)
        {
            string title, text;
            if (result == null && timedOut)
            {
                title = Loc.T("ask.expired.title");
                text = Loc.T("ask.expired.text", label, minutes);
            }
            else if (p.ByComputer || result == null)
            {
                title = Loc.T("ask.settled.title");
                text = Loc.T("ask.settled.text", label);
            }
            else return;
            // No session label: no blank first line.
            text = text.TrimStart('\n', ' ');
            Config cfg = app.Config;
            // Red while the question still waits in the window, green when it is settled.
            Notifier.SendNtfy(cfg.NtfyServer, Notifier.Authorization(cfg), cfg.NtfyTopic, title, text, false, null, null,
                result == null && timedOut ? "red_circle" : "green_circle");
        }

        // The reply the hook turns into JSON: "allow", "deny|message" or "" (ask on the computer).
        string Decide(string payload)
        {
            HookEvent e = HookEvent.Parse(payload);
            if (e == null || e.Name != "PermissionRequest") return "";

            TrayApp.ApprovalContext ctx = app.GetApprovalContext(e.SessionId);
            if (!ctx.Phone && !ctx.Local) return "";

            // One deadline for everything below, so waiting for the phone's turn does not stretch the hook; the panel shows
            // the time that is left.
            DateTime deadline = DateTime.Now.AddMinutes(Math.Max(1, app.Config.RemoteApproveMinutes));
            var p = new Pending
            {
                Token = Secret.RandomToken(),
                SessionId = e.SessionId,
                Tool = e.Tool ?? "?",
                Summary = Clip(e.ToolInputText, 700),
                Deadline = deadline,
            };
            if (p.Tool == "AskUserQuestion")
            {
                // "Allow" would let the question through without an answer, so a question gets the
                // answers as buttons, or none at all when its shape does not fit.
                if (!ReadQuestion(e.ToolInput, out p.Question, out p.Options, out p.Descriptions))
                {
                    Log.Write("[" + Short(e.SessionId) + "] question: not a single choice, asking on the computer");
                    return "";
                }
                p.Input = e.ToolInput;
                p.Summary = p.Question;
            }
            lock (gate) pending[p.Token] = p;
            // Claude Code reports "waiting" a few seconds after it asks; the request itself is known at once, so the
            // session turns red and the pop-up appears now, not after that delay.
            app.RequestArrived(e.SessionId, e.Cwd);

            string id = Short(e.SessionId);
            Log.Write("[" + id + "] approval requested: " + p.Tool);
            bool pushed = false;
            try
            {
                if (ctx.Phone)
                {
                    // The phone is told only if the notification on the computer was ignored (when that is asked for).
                    if (!app.PhoneTurn(e.SessionId, p.Done, deadline))
                        Log.Write("[" + id + "] approval: reacted on the computer, no push to the phone");
                    else
                    {
                        pushed = true;
                        if (!PushSync(p, ctx.Label))
                        {
                            Log.Write("[" + id + "] approval: push failed" + (ctx.Local ? ", waiting for the tray menu" : ", asking on the computer"));
                            if (!ctx.Local) return "";
                        }
                    }
                }
                // The wait. A screen that gets locked sends the push that was held back: the user has left.
                while (true)
                {
                    TimeSpan left = deadline - DateTime.Now;
                    if (left <= TimeSpan.Zero) break;
                    if (p.Done.WaitOne((int)Math.Min(1000, left.TotalMilliseconds))) break;
                    bool open;
                    lock (gate) open = p.Result == null;
                    if (ctx.Phone && !pushed && open && app.ScreenLocked)
                    {
                        pushed = true;
                        Log.Write("[" + id + "] approval: the screen is locked, pushing to the phone now");
                        if (!PushSync(p, ctx.Label)) Log.Write("[" + id + "] approval: push failed");
                    }
                }
            }
            finally
            {
                lock (gate) pending.Remove(p.Token);
            }

            string result;
            lock (gate) result = p.Result;
            Record(p, string.IsNullOrEmpty(ctx.Label) ? "?" : ctx.Label, result, result == null && DateTime.Now >= deadline);
            if (p.Pushed && ctx.Phone)
                FollowUp(p, result, result == null && DateTime.Now >= deadline, string.IsNullOrEmpty(ctx.Label) ? "" : ctx.Label,
                    Math.Max(1, app.Config.RemoteApproveMinutes));
            Log.Write("[" + id + "] approval " + (result ?? "not answered, asking on the computer"));
            if (result != null && result.StartsWith("pick:", StringComparison.Ordinal) && p.Options != null)
                return "answer|" + AnswerJson(p, int.Parse(result.Substring(5), CultureInfo.InvariantCulture));
            if (result == "allow") return "allow";
            if (result == "deny") return "deny|" + Loc.T("approve.denied.message");
            return "";
        }

        // The prompt was answered on the computer (or the session moved on): stop waiting.
        public void OnSessionEvent(HookEvent e)
        {
            if (e == null || string.IsNullOrEmpty(e.SessionId)) return;
            switch (e.Name)
            {
                case "PostToolUse":
                case "Stop":
                case "UserPromptSubmit":
                case "SessionEnd":
                    break;
                default:
                    return;
            }
            lock (gate)
            {
                foreach (Pending p in pending.Values)
                    if (p.SessionId == e.SessionId && p.Result == null)
                        p.Done.Set();
            }
        }

        // ---- the phone side ----

        bool PushSync(Pending p, string label)
        {
            Config cfg = app.Config;
            string replyUrl = cfg.NtfyServer.Trim().TrimEnd('/') + "/" + cfg.ReplyTopic;
            string summary = Clip(p.Summary, 300);
            string body = (string.IsNullOrEmpty(label) ? "" : label + "\n")
                + (p.Options != null ? summary + OptionList(p) : p.Tool + (summary.Length > 0 ? ": " + summary : ""));

            object[] actions;
            if (p.Options != null)
            {
                // ntfy shows at most three buttons; a fourth option stays for the tray menu.
                var buttons = new List<object>();
                for (int i = 0; i < Math.Min(3, p.Options.Count); i++)
                    buttons.Add(Button(Clip((i + 1) + ") " + p.Options[i], 30), replyUrl, "pick:" + p.Token + ":" + i));
                actions = buttons.ToArray();
            }
            else
            {
                actions = new object[]
                {
                    Button(Loc.T("approve.push.allow"), replyUrl, "allow:" + p.Token),
                    Button(Loc.T("approve.push.deny"), replyUrl, "deny:" + p.Token),
                };
            }

            string error = "pending";
            var sent = new ManualResetEvent(false);
            Notifier.SendNtfy(cfg.NtfyServer, Notifier.Authorization(cfg), cfg.NtfyTopic, Loc.T(p.Options != null ? "ask.push.title" : "approve.push.title"), body, true, actions, err =>
            {
                error = err;
                sent.Set();
            });
            bool ok = sent.WaitOne(15000) && error == null;
            if (ok)
            {
                p.Pushed = true;
                p.PushedAt = DateTime.Now;
                // The phone knows about this wait now; a plain "waiting" push on top of it would only repeat it.
                app.MarkWaitingNotified(p.SessionId);
            }
            return ok;
        }

        // The buttons can only carry the start of an answer, so the push also lists every answer in full.
        static string OptionList(Pending p)
        {
            var sb = new StringBuilder("\n");
            for (int i = 0; i < p.Options.Count; i++)
            {
                sb.Append("\n").Append(i + 1).Append(") ").Append(p.Options[i]);
                string d = Clip(p.Descriptions[i], 120);
                if (d.Length > 0) sb.Append(" — ").Append(d);
            }
            if (p.Options.Count > 3) sb.Append("\n\n").Append(Loc.T("ask.more.note", p.Options.Count));
            sb.Append("\n\n").Append(Loc.T("ask.other.note"));
            return sb.ToString();
        }

        static Dictionary<string, object> Button(string label, string url, string body)
        {
            var b = new Dictionary<string, object>();
            b["action"] = "http";
            b["label"] = label;
            b["url"] = url;
            b["method"] = "POST";
            b["body"] = body;
            b["clear"] = true;
            return b;
        }

        // Sends a request nobody is waiting for and reports the answer, to check the buttons end to end.
        public void RunTest(Action<string> done)
        {
            new Thread(() =>
            {
                var p = new Pending
                {
                    Token = Secret.RandomToken(),
                    SessionId = "test",
                    Tool = Loc.T("approve.test.tool"),
                    Summary = "",
                };
                lock (gate) pending[p.Token] = p;
                string result = null;
                try
                {
                    if (!PushSync(p, ""))
                    {
                        done("error");
                        return;
                    }
                    p.Done.WaitOne(90000);
                    lock (gate) result = p.Result;
                }
                finally
                {
                    lock (gate) pending.Remove(p.Token);
                }
                done(result);
            }) { IsBackground = true, Name = "ApprovalTest" }.Start();
        }

        // Starts, restarts or stops the listener for answers, depending on the current settings.
        public void Sync()
        {
            Config cfg = app.Config;
            bool on = cfg.RemoteApprove && cfg.NtfyEnabled && !string.IsNullOrEmpty(cfg.NtfyTopic) && !string.IsNullOrEmpty(cfg.ReplyTopic);
            string auth = Notifier.Authorization(cfg);
            string key = on ? cfg.NtfyServer.Trim() + "|" + cfg.ReplyTopic + "|" + auth : null;
            int gen;
            lock (gate)
            {
                if (key == listenKey) return;
                listenKey = key;
                gen = ++generation;
                if (currentRequest != null) try { currentRequest.Abort(); } catch { }
            }
            Status = "";
            if (key == null) return;

            string server = cfg.NtfyServer.Trim().TrimEnd('/');
            string topic = cfg.ReplyTopic;
            new Thread(() => Listen(gen, server, auth, topic)) { IsBackground = true, Name = "ApprovalListener" }.Start();
        }

        // Follows the reply topic (newline-delimited JSON over a long-lived response).
        void Listen(int gen, string server, string auth, string topic)
        {
            string since = ((long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds).ToString(CultureInfo.InvariantCulture);
            int backoff = 2;
            while (gen == generation && !stopped)
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(server + "/" + topic + "/json?since=" + Uri.EscapeDataString(since));
                    req.Timeout = 30000;
                    req.ReadWriteTimeout = 120000;
                    req.UserAgent = "ClaudeCodeTrafficLight";
                    if (!string.IsNullOrEmpty(auth)) req.Headers["Authorization"] = auth;
                    lock (gate)
                    {
                        if (gen != generation) return;
                        currentRequest = req;
                    }
                    using (var resp = req.GetResponse())
                    using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        Status = "ok";
                        backoff = 2;
                        string line;
                        while (gen == generation && (line = reader.ReadLine()) != null)
                            since = Handle(line, since);
                    }
                }
                catch (Exception ex)
                {
                    if (gen != generation || stopped) return;
                    Status = ex.Message;
                    Log.Write("Approval listener: " + ex.Message);
                    Thread.Sleep(backoff * 1000);
                    backoff = Math.Min(backoff * 2, 30);
                }
            }
        }

        string Handle(string line, string since)
        {
            try
            {
                var m = new JavaScriptSerializer().DeserializeObject(line) as IDictionary<string, object>;
                if (m == null) return since;
                object id;
                if (m.TryGetValue("id", out id) && id is string) since = (string)id;
                object ev, text;
                if (!m.TryGetValue("event", out ev) || !"message".Equals(ev)) return since;
                if (!m.TryGetValue("message", out text) || !(text is string)) return since;
                Resolve(((string)text).Trim());
            }
            catch (Exception ex) { Log.Write("Approval reply ignored: " + ex.Message); }
            return since;
        }

        // "allow:<token>" or "deny:<token>"; a token works once and only for a request still waiting.
        void Resolve(string message)
        {
            if (message.Length > 100) return;
            int colon = message.IndexOf(':');
            if (colon <= 0) return;
            string verb = message.Substring(0, colon);
            string token = message.Substring(colon + 1);
            if (verb != "allow" && verb != "deny" && verb != "pick") return;

            // "pick:<token>:<option number>"
            int option = -1;
            if (verb == "pick")
            {
                int last = token.LastIndexOf(':');
                if (last <= 0 || !int.TryParse(token.Substring(last + 1), NumberStyles.None, CultureInfo.InvariantCulture, out option)) return;
                token = token.Substring(0, last);
            }

            lock (gate)
            {
                Pending p;
                if (!pending.TryGetValue(token, out p) || p.Used) return;
                // A question takes only a chosen option, a permission prompt only allow or deny.
                if (verb == "pick" ? p.Options == null || option < 0 || option >= p.Options.Count : p.Options != null) return;
                p.Used = true;
                p.Result = verb == "pick" ? "pick:" + option : verb;
                p.Done.Set();
            }
        }

        // ---- helpers ----

        static string Clip(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string flat = Regex.Replace(text, @"\s+", " ").Trim();
            return flat.Length <= max ? flat : flat.Substring(0, max - 1) + "…";
        }

        static string Short(string id)
        {
            return string.IsNullOrEmpty(id) ? "-" : id.Substring(0, Math.Min(4, id.Length));
        }

        public static byte[] ReadMessage(Stream s)
        {
            var head = new byte[4];
            ReadFully(s, head, 4);
            int length = BitConverter.ToInt32(head, 0);
            if (length < 0 || length > 4 * 1024 * 1024) throw new InvalidDataException("bad length");
            var data = new byte[length];
            ReadFully(s, data, length);
            return data;
        }

        public static void WriteMessage(Stream s, string text)
        {
            byte[] data = new UTF8Encoding(false).GetBytes(text ?? "");
            s.Write(BitConverter.GetBytes(data.Length), 0, 4);
            s.Write(data, 0, data.Length);
            s.Flush();
        }

        static void ReadFully(Stream s, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = s.Read(buffer, read, count - read);
                if (n <= 0) throw new EndOfStreamException();
                read += n;
            }
        }
    }
}
