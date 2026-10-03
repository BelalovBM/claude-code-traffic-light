using System;
using System.Collections.Generic;
using System.IO;

namespace Semaphore
{
    // Compacting: Claude is condensing the conversation, which can take minutes and sends no other event.
    enum State { Idle = 0, Working = 1, Compacting = 2, Waiting = 3 }

    sealed class HookEvent
    {
        public string Name, SessionId, Cwd, Tool, ToolDetail, Message, NotificationType, Prompt, TranscriptPath, HostName, ToolInputText, Trigger;
        public long HostHwnd;
        public int ProcessId;
        public long ProcessStart;
        public IDictionary<string, object> ToolInput;
        public string ToolFile;

        public static HookEvent Parse(string json)
        {
            var d = Json.Parse(json) as IDictionary<string, object>;
            if (d == null) return null;
            var e = new HookEvent
            {
                Name = Str(d, "hook_event_name"),
                SessionId = Str(d, "session_id"),
                Cwd = Str(d, "cwd"),
                Tool = Str(d, "tool_name"),
                Message = Str(d, "message"),
                NotificationType = Str(d, "notification_type"),
                Prompt = Str(d, "prompt"),
                TranscriptPath = Str(d, "transcript_path"),
                HostName = Str(d, "tl_host"),
                Trigger = Str(d, "trigger"),
            };
            object hwnd, pid, started;
            if (d.TryGetValue("tl_hwnd", out hwnd) && hwnd != null)
            {
                try { e.HostHwnd = Convert.ToInt64(hwnd); }
                catch { }
            }
            if (d.TryGetValue("tl_pid", out pid) && pid != null && d.TryGetValue("tl_pstart", out started) && started != null)
            {
                try
                {
                    e.ProcessId = Convert.ToInt32(pid);
                    e.ProcessStart = Convert.ToInt64(started);
                }
                catch { }
            }
            var input = d.ContainsKey("tool_input") ? d["tool_input"] as IDictionary<string, object> : null;
            if (input != null)
            {
                string file = Str(input, "file_path");
                if (!string.IsNullOrEmpty(file))
                {
                    e.ToolDetail = Path.GetFileName(file);
                    e.ToolFile = file;
                }
                e.ToolInput = input;
                e.ToolInputText = Describe(input);
            }
            return e;
        }

        // What the tool is about to do, in a form a person can judge: the command, the file...
        internal static string Describe(IDictionary<string, object> input)
        {
            foreach (string key in new[] { "command", "file_path", "path", "url", "pattern", "query", "description" })
            {
                string v = Str(input, key);
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            try { return new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(input); }
            catch { return ""; }
        }

        static string Str(IDictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) ? v as string : null;
        }
    }

    sealed class Session
    {
        public string Id;
        public string Cwd;
        public State State;
        public DateTime Since;
        public DateTime LastEvent;
        public string Tool;
        public string Message;
        public string Prompt;
        public DateTime TaskStart;
        public bool WaitingNotified;
        public string Title;
        // "manual" (/compact) or "auto" of the compaction that is running or has just run.
        public string CompactTrigger;
        // The folder of the file the session touched last: the real place of the work, which can differ
        // from the folder the session was started in.
        public string WorkDir;
        // Whether the user reacted to the notification on the computer (clicked, closed or answered it), and until
        // when that notification is on the screen: the phone waits for it.
        public bool Reacted;
        public DateTime GraceUntil;
        // What the session is waiting for, read from its transcript when no hook told about it.
        public Approvals.Request Orphan;
        // The wait (identified by when it began) for which the program has had the request itself; its question
        // is not looked up in the transcript again once the request has expired or was dismissed.
        public DateTime AskedSince;
        public DateTime OrphanChecked;
        public string TranscriptPath;
        // Claude ended its turn only to wait for a background task it started (a build, tests): the work goes on,
        // so this is not "finished". Until when that is believed; DateTime.MinValue when not waiting.
        public DateTime BackgroundUntil;
        public bool InBackground { get { return BackgroundUntil > DateTime.Now && State == State.Working; } }
        public string HostName;
        public long HostHwnd;
        public int ProcessId;
        public long ProcessStart;
        public DateTime TitleChecked;

        public string ShortId
        {
            get { return string.IsNullOrEmpty(Id) ? "" : Id.Substring(0, Math.Min(4, Id.Length)); }
        }

        // Label that tells apart sessions of the same project: the start of the last prompt,
        // or a short id when no prompt was seen.
        public string Hint
        {
            get
            {
                if (string.IsNullOrEmpty(Prompt)) return ShortId;
                string flat = System.Text.RegularExpressions.Regex.Replace(Prompt, @"\s+", " ").Trim();
                return flat.Length <= 32 ? flat : flat.Substring(0, 31) + "…";
            }
        }

        public string Project
        {
            get
            {
                if (string.IsNullOrEmpty(Cwd)) return Id ?? "?";
                string n = Path.GetFileName(Cwd.TrimEnd('\\', '/'));
                return string.IsNullOrEmpty(n) ? Cwd : n;
            }
        }
    }

    sealed class Change
    {
        public Session Session;
        public State? Old;
        public State? New;
        // How long the session had been in the old state.
        public TimeSpan Elapsed;
    }

    enum Level { None = 0, Idle = 1, Working = 2, Compacting = 3, Waiting = 4 }

    sealed class SessionStore
    {
        readonly Dictionary<string, Session> map = new Dictionary<string, Session>();
        // Sessions that already ended must not come back from the process registry.
        readonly HashSet<string> ended = new HashSet<string>();

        public IEnumerable<Session> All { get { return map.Values; } }

        // How long a stop with an unfinished background task still counts as working.
        public const int BackgroundLimitMinutes = 30;

        public Session Find(string id)
        {
            Session s;
            return !string.IsNullOrEmpty(id) && map.TryGetValue(id, out s) ? s : null;
        }

        // A session found in the process registry that no hook has reported yet (the tray app was
        // started after it). Returns null when it is already known or has ended.
        public Session Adopt(RegistryEntry r)
        {
            if (string.IsNullOrEmpty(r.SessionId) || map.ContainsKey(r.SessionId) || ended.Contains(r.SessionId))
                return null;
            DateTime now = DateTime.Now;
            DateTime since = r.Updated > now ? now : r.Updated;
            var s = new Session
            {
                Id = r.SessionId,
                Cwd = r.Cwd,
                State = r.Status == "busy" ? State.Working : r.Status == "waiting" ? State.Waiting : State.Idle,
                Since = since,
                TaskStart = since,
                LastEvent = now,
                ProcessId = r.Pid,
                ProcessStart = r.ProcStart,
            };
            map[s.Id] = s;
            return s;
        }

        // An interrupted task fires no Stop hook, so the session would stay "working". The registry
        // saying idle for a while after the last hook event is the proof that it is not.
        public bool SettleIdle(RegistryEntry r)
        {
            Session s;
            if (string.IsNullOrEmpty(r.SessionId) || !map.TryGetValue(r.SessionId, out s)) return false;
            if (s.State != State.Working || r.Status != "idle") return false;
            // Idle in the registry is expected while Claude waits for its own background task.
            if (s.InBackground) return false;
            if (r.Updated <= s.LastEvent.AddSeconds(3)) return false;
            s.State = State.Idle;
            s.Since = DateTime.Now;
            s.Tool = null;
            return true;
        }

        // The registry saying "waiting" after the last hook event is the proof that the session asks for something
        // (the hook may have been missed, for instance while this program was not running).
        public bool SettleWaiting(RegistryEntry r)
        {
            Session s;
            if (string.IsNullOrEmpty(r.SessionId) || !map.TryGetValue(r.SessionId, out s)) return false;
            if (s.State == State.Waiting || r.Status != "waiting") return false;
            if (r.Updated <= s.LastEvent.AddSeconds(3)) return false;
            s.State = State.Waiting;
            s.Since = DateTime.Now;
            s.WaitingNotified = false;
            return true;
        }

        public Level Overall()
        {
            if (map.Count == 0) return Level.None;
            Level level = Level.Idle;
            foreach (var s in map.Values)
            {
                if (s.State == State.Waiting) return Level.Waiting;
                if (s.State == State.Compacting) level = Level.Compacting;
                else if (s.State == State.Working && level != Level.Compacting) level = Level.Working;
            }
            return level;
        }

        public Change Apply(HookEvent e)
        {
            if (e == null || string.IsNullOrEmpty(e.Name)) return null;
            string id = e.SessionId ?? "";
            DateTime now = DateTime.Now;

            if (e.Name == "SessionEnd")
            {
                ended.Add(id);
                Session gone;
                if (map.TryGetValue(id, out gone))
                {
                    map.Remove(id);
                    return new Change { Session = gone, Old = gone.State, New = null };
                }
                return null;
            }

            State? target = null;
            switch (e.Name)
            {
                case "SessionStart":
                case "Stop":
                    target = State.Idle;
                    break;
                case "UserPromptSubmit":
                case "PreToolUse":
                case "PostToolUse":
                case "PostCompact":
                    target = State.Working;
                    break;
                case "PreCompact":
                    target = State.Compacting;
                    break;
                case "Notification":
                    target = NotificationState(e);
                    break;
            }
            if (target == null) return null;

            Session s;
            State? old = null;
            if (!map.TryGetValue(id, out s))
            {
                s = new Session { Id = id, Since = now };
                map[id] = s;
            }
            else old = s.State;

            if (!string.IsNullOrEmpty(e.Cwd)) s.Cwd = e.Cwd;
            s.LastEvent = now;

            if (!string.IsNullOrEmpty(e.TranscriptPath)) s.TranscriptPath = e.TranscriptPath;
            // Any event but Stop means the session acts again: whatever background wait there was is over.
            if (e.Name != "Stop") s.BackgroundUntil = DateTime.MinValue;
            else if (old == State.Working || old == State.Compacting)
            {
                // A stop while a task it started in the background still runs: the session keeps working. A task that
                // never reports (a server left running) must not keep it yellow for ever, hence the limit.
                int pending = TitleReader.PendingBackgroundTasks(s.TranscriptPath);
                if (pending > 0)
                {
                    target = State.Working;
                    s.BackgroundUntil = now.AddMinutes(BackgroundLimitMinutes);
                    Log.Write("[" + s.ShortId + "] stopped to wait for " + pending + " background task(s), still working");
                }
            }

            if (e.Name == "PreCompact") s.CompactTrigger = e.Trigger;
            // After a /compact nothing follows, so the session is ready; an automatic one continues the task.
            if (e.Name == "PostCompact" && s.CompactTrigger == "manual") target = State.Idle;

            if (e.Name == "PreToolUse")
            {
                s.Tool = string.IsNullOrEmpty(e.ToolDetail) ? e.Tool : e.Tool + ": " + e.ToolDetail;
                if (!string.IsNullOrEmpty(e.ToolFile))
                {
                    try { s.WorkDir = Path.GetDirectoryName(e.ToolFile); }
                    catch { }
                }
            }
            else if (target == State.Idle)
                s.Tool = null;

            s.Message = target == State.Waiting ? e.Message : null;
            if (e.Name == "UserPromptSubmit" && !string.IsNullOrEmpty(e.Prompt))
                s.Prompt = e.Prompt;

            if (!string.IsNullOrEmpty(e.TranscriptPath)) s.TranscriptPath = e.TranscriptPath;
            if (e.HostHwnd != 0)
            {
                s.HostHwnd = e.HostHwnd;
                s.HostName = e.HostName;
            }
            if (e.ProcessId != 0)
            {
                s.ProcessId = e.ProcessId;
                s.ProcessStart = e.ProcessStart;
            }
            // Titles appear after the first reply; a session met mid-way (app restarted) is
            // looked up on any event until one is found.
            bool titleMoment = e.Name == "SessionStart" || e.Name == "UserPromptSubmit" || e.Name == "Stop"
                || s.Title == null;
            if (titleMoment && (now - s.TitleChecked).TotalSeconds > 20)
            {
                s.TitleChecked = now;
                string title = TitleReader.Read(s.TranscriptPath);
                if (title != null) s.Title = title;
                if (s.WorkDir == null) s.WorkDir = TitleReader.ReadWorkDir(s.TranscriptPath);
            }

            if (target == State.Working && (old == null || old == State.Idle))
                s.TaskStart = now;

            TimeSpan elapsed = now - s.Since;
            if (old != target)
            {
                s.State = target.Value;
                s.Since = now;
                s.WaitingNotified = false;
            }
            return new Change { Session = s, Old = old, New = target, Elapsed = elapsed };
        }

        static State? NotificationState(HookEvent e)
        {
            string type = e.NotificationType;
            if (type == "permission_prompt" || type == "elicitation_dialog")
                return State.Waiting;
            if (!string.IsNullOrEmpty(type))
                return null;
            string m = e.Message ?? "";
            if (m.IndexOf("permission", StringComparison.OrdinalIgnoreCase) >= 0)
                return State.Waiting;
            return null;
        }

        // A closed terminal kills Claude Code without a SessionEnd hook; the process is the proof
        // that the session still exists. Unknown (never reported, or not readable) counts as alive.
        static bool ProcessAlive(Session s)
        {
            if (s.ProcessId == 0) return true;
            try
            {
                using (var p = System.Diagnostics.Process.GetProcessById(s.ProcessId))
                    return !p.HasExited && p.StartTime.ToFileTimeUtc() == s.ProcessStart;
            }
            catch (ArgumentException) { return false; }
            catch { return true; }
        }

        // Sessions whose background wait ran out without the task reporting back: Claude handed the turn over after
        // all (a server left running), so they are finished now, with the usual notification.
        public List<Change> ExpireBackground()
        {
            var changes = new List<Change>();
            DateTime now = DateTime.Now;
            foreach (Session s in map.Values)
            {
                if (s.BackgroundUntil == DateTime.MinValue || now < s.BackgroundUntil) continue;
                s.BackgroundUntil = DateTime.MinValue;
                if (s.State != State.Working) continue;
                changes.Add(new Change { Session = s, Old = State.Working, New = State.Idle, Elapsed = now - s.Since });
                s.State = State.Idle;
                s.Since = now;
                s.Tool = null;
            }
            return changes;
        }

        // Working sessions that stopped reporting (for instance after an interrupt,
        // which fires no Stop hook) fall back to Idle; sessions whose process is gone or that
        // have been idle for long are forgotten. removed receives the ids that were dropped.
        public bool Sweep(int staleMinutes, List<string> removed)
        {
            bool changed = false;
            DateTime now = DateTime.Now;
            var drop = new List<string>();
            foreach (var kv in map)
            {
                Session s = kv.Value;
                double idle = (now - s.LastEvent).TotalMinutes;
                if (!ProcessAlive(s))
                {
                    drop.Add(kv.Key);
                    changed = true;
                }
                else if (s.State == State.Working && idle > staleMinutes && s.BackgroundUntil == DateTime.MinValue)
                {
                    s.State = State.Idle;
                    s.Since = now;
                    s.Tool = null;
                    changed = true;
                }
                else if (s.State == State.Compacting && idle > 20)
                {
                    // Compaction takes minutes; twenty without PostCompact means it was interrupted.
                    s.State = State.Idle;
                    s.Since = now;
                    changed = true;
                }
                else if (s.State == State.Idle && idle > 12 * 60)
                {
                    drop.Add(kv.Key);
                    changed = true;
                }
            }
            foreach (string k in drop)
            {
                map.Remove(k);
                ended.Add(k);
                if (removed != null) removed.Add(k);
            }
            return changed;
        }
    }
}
