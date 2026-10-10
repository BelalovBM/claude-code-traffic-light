using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Media;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace Semaphore
{
    sealed partial class TrayApp : ApplicationContext
    {
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly SessionStore store = new SessionStore();
        readonly HookServer server = new HookServer();
        readonly Approvals approvals;
        readonly SynchronizationContext ui;
        readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer menuLive = new System.Windows.Forms.Timer { Interval = 1000 };
        string menuSignature;
        readonly Dictionary<Level, Icon> icons = new Dictionary<Level, Icon>();

        public Config Config { get; private set; }
        SettingsForm settings;
        Level shown = (Level)(-1);

        public TrayApp()
        {
            ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            Config = Config.Load();
            Config.Autostart = Native.GetAutostart();
            Loc.Load(Config.Language);
            Theme.Update(Config.Theme);

            icons[Level.None] = TrayIcons.Create(Level.None);
            icons[Level.Idle] = TrayIcons.Create(Level.Idle);
            icons[Level.Working] = TrayIcons.Create(Level.Working);
            icons[Level.Compacting] = TrayIcons.Create(Level.Compacting);
            icons[Level.Waiting] = TrayIcons.Create(Level.Waiting);
            icons[Level.Background] = TrayIcons.Create(Level.Background);

            menu.Opening += (s, e) =>
            {
                BuildMenu();
                foreach (Session waiting in store.All) if (waiting.State == State.Waiting) waiting.Reacted = true;
            };
            // An open menu follows the sessions and pending questions instead of showing the moment it was opened.
            menu.Opened += (s, e) =>
            {
                MenuGuard.OpenedAt = Environment.TickCount;
                KeepCursorOffItems();
                menuSignature = MenuSignature();
                menuLive.Start();
                OpenAskingSession();
            };
            menu.Closed += (s, e) => menuLive.Stop();
            menuLive.Tick += (s, e) => RefreshOpenMenu();
            Toasts.Prepare(Loc.T("app.name"), LampFile(Level.Idle));
            askTimer.Tick += (s, e) => CheckAsk();
            Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
            tray.BalloonTipClicked += (s, e) =>
            {
                Action click = toastClick;
                toastClick = null;
                MarkReacted(toastSession);
                if (click != null) click();
            };
            tray.BalloonTipClosed += (s, e) => toastClick = null;
            askTimer.Start();
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            Native.TrayIcon = tray;
            hotkey.Pressed += OnHotkey;
            hotkey.Set(Config.Hotkey);
            tray.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) ShowMenu();
            };
            tray.DoubleClick += (s, e) =>
            {
                // The first click of a double click opened the menu; the double click means "settings", not the menu.
                menu.Close();
                ShowSettings();
            };

            tick.Interval = 5000;
            tick.Tick += (s, e) =>
            {
                foreach (Change done in store.ExpireBackground(s => ProcessTree.RunningToolShells(s.ProcessId)))
                {
                    Log.Write("[" + done.Session.ShortId + "] background wait ran out (no task running for "
                        + SessionStore.BackgroundLimitMinutes + " min), the session is finished");
                    Notify(done);
                }
                var gone = new List<string>();
                store.Sweep(Config.StaleMinutes, gone);
                foreach (string id in gone)
                {
                    // Same clean-up as for a normal SessionEnd.
                    Log.Write("[" + id.Substring(0, Math.Min(4, id.Length)) + "] session ended (process gone)");
                    SetMuted(id, false);
                    approvals.OnSessionEvent(new HookEvent { Name = "SessionEnd", SessionId = id });
                }
                DiscoverSessions();
                CheckLongWaiting();
                CheckLongBackground();
                CheckLimits();
                Refresh();
            };
            tick.Start();
            StartLimits();

            server.Received += text => ui.Post(_ => OnMessage(text), null);
            server.Start();

            approvals = new Approvals(this);
            HookInstaller.Configure(ApproveActive, ApproveSeconds);
            approvals.Start();
            approvals.Sync();

            Refresh();
            // Building the menu once up front pays the one-time JIT/rendering cost at start-up
            // instead of on the first click.
            ui.Post(_ => BuildMenu(), null);
            ui.Post(_ => { DiscoverSessions(); Refresh(); }, null);
            ui.Post(_ =>
            {
                bool first = !Config.FirstRunDone;
                FirstRun();
                if (!first) StartupNotice();
            }, null);
        }

        // The icon appears in the tray without any sign, so say that the program is there and how many sessions it sees.
        void StartupNotice()
        {
            if (!Config.StartupNotice) return;
            int count = store.All.Count();
            // With no sessions there is nothing to count.
            Balloon(Loc.T("app.name"), count > 0 ? Loc.T("startup.text", count) : Loc.T("startup.text.none"), store.Overall(), 3000);
        }

        readonly Dictionary<Level, Icon> balloonIcons = new Dictionary<Level, Icon>();

        // The lamp as a picture file for the toast (a toast can only show a picture from a file).
        static string LampFile(Level level)
        {
            try
            {
                // A test or self-check instance keeps its pictures in its own folder, away from the installed program's.
                string dir = AppPaths.IsSandbox ? System.IO.Path.Combine(AppPaths.DataDir, "lamps")
                    : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClaudeCodeTrafficLight");
                System.IO.Directory.CreateDirectory(dir);
                string file = System.IO.Path.Combine(dir, "lamp-" + level + ".png");
                if (!System.IO.File.Exists(file))
                    using (Bitmap bmp = TrayIcons.Draw(level, 128)) bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                return file;
            }
            catch { return null; }
        }

        // A pop-up that carries the lamp of the state it is about (red for waiting, green for done...), so it
        // agrees with the tray icon. Falls back to the system signs if the direct call is not possible.
        // What a click on the pop-up that is on the screen does (open the request, bring the window forward).
        Action toastClick;

        Session toastSession;

        void Toast(string title, string text, Level level, ToolTipIcon fallback, Action onClick = null, Session session = null)
        {
            toastClick = onClick;
            toastSession = session;
            Icon big;
            if (!balloonIcons.TryGetValue(level, out big))
                balloonIcons[level] = big = TrayIcons.CreateLarge(level, 48);
            if (!Native.Balloon(tray, title, text, big.Handle))
                tray.ShowBalloonTip(5000, title, text, fallback);
        }

        // A pop-up that closes itself: a Windows notification, which takes the time it is given. Only where those
        // cannot be shown, the balloon: Windows ignores the time given to it, and taking the icon off and putting
        // it back (a short blink in the tray) is the only way to close it.
        void Balloon(string title, string text, Level level, int milliseconds, Action onClick = null)
        {
            if (Toasts.Show("info", title, text, LampFile(level), null, milliseconds,
                    args => { if (onClick != null) ui.Post(_ => onClick(), null); }, null))
                return;
            Toast(title, text, level, ToolTipIcon.Info, onClick);
            var close = new System.Windows.Forms.Timer { Interval = milliseconds };
            close.Tick += (s, e) =>
            {
                close.Stop();
                close.Dispose();
                tray.Visible = false;
                tray.Visible = true;
            };
            close.Start();
        }

        // Re-applies language, theme and autostart after the settings changed.
        public void ApplyConfig()
        {
            Loc.Load(Config.Language);
            Theme.Update(Config.Theme);
            Native.SetAutostart(Config.Autostart);
            Config.Save();
            if ((Config.Hotkey ?? "") != (hotkey.Current ?? "")) hotkey.Set(Config.Hotkey);
            SyncApproval();
            Refresh();
        }

        // Picks up sessions that were already running when the tray app started, and settles a task
        // that was interrupted (no hook tells about that). Hooks remain the main source of truth.
        void DiscoverSessions()
        {
            try
            {
                foreach (RegistryEntry r in SessionRegistry.Scan())
                {
                    if (!string.IsNullOrEmpty(r.Kind) && r.Kind != "interactive") continue;

                    Session s = store.Adopt(r);
                    if (s == null)
                    {
                        if (store.SettleWaiting(r))
                            Log.Write("[" + r.SessionId.Substring(0, Math.Min(4, r.SessionId.Length)) + "] the session is waiting (registry), no hook told about it");
                        if (store.SettleIdle(r))
                            Log.Write("[" + r.SessionId.Substring(0, Math.Min(4, r.SessionId.Length)) + "] task ended without a Stop hook (interrupted)");

                        // Tool-call hooks no longer carry the process and window, so a session first seen
                        // through them gets both from the registry here.
                        Session known = store.Find(r.SessionId);
                        if (known != null)
                        {
                            if (known.ProcessId == 0) { known.ProcessId = r.Pid; known.ProcessStart = r.ProcStart; }
                            long hostHwnd;
                            string hostName;
                            if (known.HostHwnd == 0 && ProcessTree.FindHostWindowFrom(r.Pid, out hostHwnd, out hostName))
                            {
                                known.HostHwnd = hostHwnd;
                                known.HostName = hostName;
                            }
                        }
                        continue;
                    }

                    long hwnd;
                    string host;
                    if (ProcessTree.FindHostWindowFrom(r.Pid, out hwnd, out host))
                    {
                        s.HostHwnd = hwnd;
                        s.HostName = host;
                    }
                    s.TranscriptPath = SessionRegistry.FindTranscript(s.Id);
                    s.TitleChecked = DateTime.Now;
                    s.Title = TitleReader.Read(s.TranscriptPath);
                    s.WorkDir = TitleReader.ReadWorkDir(s.TranscriptPath);
                    Log.Write("[" + s.ShortId + "] found a running session (" + r.Status + ") host=" + s.HostName);
                }
            }
            catch (Exception ex) { Log.Write("Session discovery failed: " + ex.Message); }
            RefreshOrphans();
        }

        // A waiting session that no hook reported (the program was not running when it asked) still has the
        // question in its transcript; it is found there so the request can at least be shown.
        void RefreshOrphans()
        {
            DateTime now = DateTime.Now;
            List<Approvals.Request> live = approvals.Waiting();
            foreach (Session s in store.All)
            {
                if (s.State != State.Waiting || live.Any(r => r.SessionId == s.Id)) { s.Orphan = null; continue; }
                // A wait the program saw as a live request is not offered again as a request found afterwards.
                if (s.AskedSince == s.Since) { s.Orphan = null; continue; }
                if (s.Orphan != null || (now - s.OrphanChecked).TotalSeconds < 10) continue;
                s.OrphanChecked = now;
                Approvals.Request found = TitleReader.ReadPendingRequest(s.TranscriptPath);
                if (found == null) continue;
                found.SessionId = s.Id;
                found.Token = "orphan:" + s.Id + ":" + s.Since.Ticks;
                s.Orphan = found;
                Log.Write("[" + s.ShortId + "] the request it waits for was read from the transcript: " + found.Tool);
            }
        }

        // Answering permission prompts from the phone needs ntfy and its own secret reply topic.
        bool ApproveActive
        {
            get
            {
                return Config.RemoteApprove && Config.NtfyEnabled
                    && !string.IsNullOrEmpty(Config.NtfyTopic) && !string.IsNullOrEmpty(Config.ReplyTopic);
            }
        }

        // The permission hook is needed when prompts can be answered from the phone or from the menu.
        bool HookNeeded { get { return ApproveActive || Config.LocalApprove; } }

        int ApproveSeconds { get { return Config.RemoteApproveMinutes * 60 + 30; } }

        public Approvals ApprovalService { get { return approvals; } }

        // Keeps Claude Code's settings and the listener in line with the approval settings: the
        // permission hook is added only while the feature is on, and removed when it is turned off.
        void SyncApproval()
        {
            bool active = HookNeeded;
            if (active != HookInstaller.Approve || ApproveSeconds != HookInstaller.ApproveSeconds)
            {
                HookInstaller.Configure(active, ApproveSeconds);
                if (HookInstaller.GetStatus() != HooksStatus.Missing)
                {
                    try { HookInstaller.Install(); }
                    catch (Exception ex) { Log.Write("Could not update the permission hook: " + ex.Message); }
                }
            }
            approvals.Sync();
        }

        public sealed class ApprovalContext
        {
            public bool Phone, Local;
            public string Label = "";
        }

        // Called from the approval threads; the session data belongs to the UI thread.
        public ApprovalContext GetApprovalContext(string sessionId)
        {
            var result = new ApprovalContext();
            ui.Send(_ =>
            {
                Session s = store.All.FirstOrDefault(x => x.Id == sessionId);
                bool muted = Config.MutedSessions.Contains(sessionId ?? "");
                // A paused phone gets nothing, answers to prompts included.
                // The switch for notifications on this computer does not reach the phone: it has its own.
                result.Phone = ApproveActive && !muted && !Config.NtfyPaused;
                result.Local = Config.LocalApprove;
                if (s != null) result.Label = SessionName(s);
            }, null);
            return result;
        }

        void FirstRun()
        {
            if (Config.FirstRunDone) return;
            Config.FirstRunDone = true;
            Config.Save();

            using (var wizard = new WelcomeForm(Config, icons[Level.Idle], ApplyConfig))
            {
                if (wizard.ShowDialog() != DialogResult.OK) return;

                Config.Autostart = wizard.Autostart;
                if (wizard.Phone)
                {
                    Config.NtfyEnabled = true;
                    if (string.IsNullOrEmpty(Config.NtfyTopic)) Config.NtfyTopic = Secret.RandomTopic();
                }
                ApplyConfig();

                if (wizard.Connect)
                {
                    try
                    {
                        HookInstaller.Install();
                        Toast(Loc.T("app.name"), Loc.T("msg.hooks.installed"), Level.Idle, ToolTipIcon.Info);
                    }
                    catch (Exception ex)
                    {
                        Log.Write("Hook install failed: " + ex);
                        MessageBox.Show(Loc.T("msg.hooks.error", ex.Message), Loc.T("app.name"),
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
                if (wizard.Phone) ShowSettings(2);
            }
        }

        public void Quit()
        {
            ExitThread();
        }

        public void MarkWaitingNotified(string sessionId)
        {
            ui.Post(_ =>
            {
                Session s = store.Find(sessionId);
                if (s != null) s.WaitingNotified = true;
            }, null);
        }

        // A permission prompt or a question reached the approval service: the session is waiting from this moment.
        public void RequestArrived(string sessionId, string cwd)
        {
            ui.Send(_ =>
            {
                try
                {
                    var ev = new HookEvent { Name = "Notification", NotificationType = "permission_prompt", SessionId = sessionId, Cwd = cwd };
                    Change change = store.Apply(ev);
                    Session asking = store.Find(sessionId);
                    if (asking != null) asking.AskedSince = asking.Since;
                    if (change != null) Notify(change);
                    Refresh();
                }
                catch (Exception ex) { Log.Write("Early waiting failed: " + ex.Message); }
            }, null);
        }

        // The request was answered (here, on the phone or in the window of Claude Code): Claude goes on at once, so the
        // session works again, unless it still asks something else.
        public void RequestSettled(string sessionId)
        {
            ui.Post(_ =>
            {
                Session s = store.Find(sessionId);
                if (s == null || s.State != State.Waiting) return;
                if (approvals.Waiting().Any(r => r.SessionId == sessionId)) return;
                Change change = store.Apply(new HookEvent { Name = "PostToolUse", SessionId = sessionId });
                Log.Write("[" + s.ShortId + "] answered, working again");
                if (change != null) Notify(change);
                Refresh();
            }, null);
        }

        void OnMessage(string text)
        {
            try
            {
                if (text.IndexOf("trafficlight_cmd", StringComparison.Ordinal) >= 0)
                {
                    int page = 0;
                    var cmd = Json.Parse(text) as IDictionary<string, object>;
                    object p, what = null;
                    if (cmd != null && cmd.TryGetValue("page", out p) && p is int) page = (int)p;
                    if (cmd != null && cmd.TryGetValue("trafficlight_cmd", out what) && "menu".Equals(what))
                        ShowMenuAt(new Point(260, 160));
                    else if ("hotkey".Equals(what))
                        OnHotkey();
                    else
                        ShowSettings(page);
                    return;
                }
                var ev = HookEvent.Parse(text);
                Change change = store.Apply(ev);
                approvals.OnSessionEvent(ev);
                // A finished session never comes back, so its mute entry would only pile up.
                if (change != null && change.New == null) SetMuted(change.Session.Id, false);
                if (ev != null)
                    Log.Write(string.Format("[{0}] {1} {2} {3} host={4}", change == null ? "-" : change.Session.ShortId, ev.Name, ev.Tool, ev.NotificationType, ev.HostName).TrimEnd());
                if (change != null) Notify(change);
                Refresh();
            }
            catch (Exception ex) { Log.Write("Event error: " + ex.Message); }
        }

        void Notify(Change c)
        {
            if (c.New == null) return;
            if (IsMuted(c.Session))
            {
                Log.Write("[" + c.Session.ShortId + "] muted, no notification");
                return;
            }
            string label = SessionLabel(c.Session);
            // Sounds and pop-ups on this computer; the pushes to the phone below do not depend on it.
            bool quiet = Paused;

            // Compaction can take minutes and nothing else reports it: say when it starts and when it ends.
            if (c.New == State.Compacting && c.Old != State.Compacting)
            {
                Log.Write("[" + c.Session.ShortId + "] compacting started (" + (c.Session.CompactTrigger ?? "?") + ")");
                if (Config.ToastCompact && !quiet)
                    Balloon(Loc.T("toast.compact.start.title"), WithLabel(label, Loc.T("toast.compact.start.text")), Level.Compacting, 4000);
            }
            else if (c.Old == State.Compacting && c.New != State.Compacting)
            {
                Log.Write("[" + c.Session.ShortId + "] compacting finished after " + Duration(c.Elapsed));
                if (Config.ToastCompact && !quiet)
                    Balloon(Loc.T("toast.compact.end.title"), WithLabel(label, Loc.T("toast.compact.end.text", Duration(c.Elapsed))), Level.Compacting, 4000);
            }

            // How long the notification on the computer stays on the screen; the phone waits until then and is told
            // only if the user did not react (when the setting asks for it).
            bool doneToast = c.New == State.Idle && c.Old == State.Working && Config.ToastDone && !quiet;
            if (c.New == State.Idle && c.Old != null && c.Old != State.Idle) StartGrace(c.Session, doneToast);

            if (c.New == State.Waiting && c.Old != State.Waiting)
            {
                Log.Write("[" + c.Session.ShortId + "] alert: waiting");
                if (Config.SoundWaiting && !quiet) SystemSounds.Exclamation.Play();
                Approvals.Request ask = approvals.Waiting().FirstOrDefault(r => r.SessionId == c.Session.Id);
                // The panel carries what this pop-up would say (the lamp, the title, the request itself); both together
                // would only cover each other.
                bool inPanel = Config.LocalApprove && Config.PopupAsk && !Paused && (ask != null || c.Session.Orphan != null);
                StartGrace(c.Session, inPanel || (Config.ToastWaiting && !quiet), inPanel);
                if (Config.ToastWaiting && !inPanel && !quiet)
                {
                    string msg = string.IsNullOrEmpty(c.Session.Message) ? Loc.T("toast.waiting.default") : c.Session.Message;
                    // A question or prompt that can be answered from the menu says what it is about.
                    if (ask != null) msg = ask.Options != null ? ask.Question : ask.Tool + (string.IsNullOrEmpty(ask.Summary) ? "" : ": " + Shorten(ask.Summary, 120));
                    Session asking = c.Session;
                    Approvals.Request toShow = ask ?? asking.Orphan;
                    // A toast with an "Answer" button when there is something to answer; the click on the rest of it just
                    // closes it, like any toast. Without that (or if it cannot be shown) the balloon is used.
                    bool shown = toShow != null && Config.LocalApprove
                        && Toasts.Show("waiting-" + asking.Id, Loc.T("toast.waiting.title"), WithLabel(label, msg), LampFile(Level.Waiting),
                            Loc.T("toast.waiting.answer"), 7000,
                            args => ui.Post(_ => { MarkReacted(asking); if (args == "answer") ShowAsk(toShow); }, null),
                            () => ui.Post(_ => MarkReacted(asking), null));
                    if (!shown)
                        Toast(Loc.T("toast.waiting.title"), WithLabel(label, msg), Level.Waiting, ToolTipIcon.Warning,
                            () => { if (toShow != null && Config.LocalApprove) ShowAsk(toShow); else Native.FocusSession(asking); }, asking);
                }

                // Threshold 0 means "push right away"; otherwise CheckLongWaiting sends it later.
                if (Config.RemoteWaiting && Config.RemoteWaitingAfterMinutes == 0 && !c.Session.WaitingNotified && !PhoneAlreadyAsked(c.Session) && !(ask != null && ApproveActive))
                {
                    c.Session.WaitingNotified = true;
                    Session waiting = c.Session;
                    AfterGrace(waiting, () => waiting.State == State.Waiting,
                        () => Notifier.Dispatch(Config, Loc.T("toast.waiting.title"), WaitingBody(waiting), true));
                }
            }
            else if (c.New == State.Idle && c.Old == State.Working)
            {
                if (Config.SoundDone && !quiet) SystemSounds.Asterisk.Play();
                if (Config.ToastDone && !quiet)
                {
                    Session finished = c.Session;
                    string doneTitle = Loc.T("toast.done.title");
                    string doneText = label.Length > 0 ? Loc.T("toast.done.body", label) : Loc.T("toast.done.plain");
                    bool shownDone = Toasts.Show("done-" + finished.Id, doneTitle, doneText, LampFile(Level.Idle), null, 7000,
                        args => ui.Post(_ => { MarkReacted(finished); Native.FocusSession(finished); }, null),
                        () => ui.Post(_ => MarkReacted(finished), null));
                    if (!shownDone)
                        Toast(doneTitle, doneText, Level.Idle, ToolTipIcon.Info, () => Native.FocusSession(finished), finished);
                }
            }

            // Remote: a finished task is only worth a push if it ran long enough that
            // the user probably looked away.
            if (c.New == State.Idle && c.Old != null && c.Old != State.Idle && c.Old != State.Compacting && Config.RemoteDone)
            {
                TimeSpan took = DateTime.Now - c.Session.TaskStart;
                if (took.TotalMinutes >= Config.RemoteDoneMinMinutes)
                {
                    string remote = RemoteLabel(c.Session);
                    string body = remote.Length > 0
                        ? Loc.T("remote.done.body", remote, Duration(took))
                        : Loc.T("remote.done.plain", Duration(took));
                    AfterGrace(c.Session, () => true, () => Notifier.Dispatch(Config, Loc.T("toast.done.title"), body, false));
                }
            }
        }

        const int GraceMs = 7000;

        bool Dnd { get { return Config.DndUntilTicks > DateTime.Now.Ticks; } }

        // Nothing on this computer: paused for a while from the menu, or switched off until switched on again.
        bool Paused { get { return Dnd || !Config.NotificationsEnabled; } }

        GuardedItem PauseFor(string text, DateTime until)
        {
            var item = new GuardedItem(text);
            item.ForeColor = Theme.Fore;
            item.Click += (o, e) =>
            {
                Config.DndUntilTicks = until.Ticks;
                Config.Save();
            };
            return item;
        }

        // A pop-up to show how notifications look (the "Check" block on the General page).
        public void ShowTestNotification()
        {
            string title = Loc.T("app.name");
            string text = Loc.T("toast.test.text");
            if (!Toasts.Show("test", title, text, LampFile(Level.Idle), null, 5000, null, null))
                Toast(title, text, Level.Idle, ToolTipIcon.Info);
        }

        void MarkReacted(Session s)
        {
            if (s != null) s.Reacted = true;
        }

        // The panel stays until it is dealt with, so it gets a longer time than a toast: the "keeps waiting" minutes
        // from the settings (at least one), renewed while the mouse moves over it.
        // The "keeps waiting" minutes only count while that push is switched on; otherwise it is one minute.
        TimeSpan PanelGrace { get { return TimeSpan.FromMinutes(Config.RemoteWaiting ? Math.Max(1, Config.RemoteWaitingAfterMinutes) : 1); } }

        // The period in which the notification on the computer can be seen; nothing shown means no waiting.
        void StartGrace(Session s, bool shown, bool panel = false)
        {
            s.Reacted = false;
            s.GraceUntil = !shown ? DateTime.Now
                : panel ? DateTime.Now + PanelGrace
                : DateTime.Now.AddMilliseconds(GraceMs + 500);
        }

        // The mouse is over the panel: the user is reading, the phone waits another stretch.
        void ExtendGrace(Session s)
        {
            if (s == null || s.Reacted) return;
            DateTime until = DateTime.Now + PanelGrace;
            if (until > s.GraceUntil) s.GraceUntil = until;
        }

        // A locked screen means the user has left: nothing waits for a reaction any more.
        volatile bool screenLocked;
        public bool ScreenLocked { get { return screenLocked; } }
        readonly List<Action> graceWaiters = new List<Action>();

        // The system theme or high contrast was switched in Windows: the windows and the menu follow without a restart.
        void OnPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            if (e.Category != Microsoft.Win32.UserPreferenceCategory.General && e.Category != Microsoft.Win32.UserPreferenceCategory.Color) return;
            ui.Post(_ =>
            {
                bool wasDark = Theme.Dark, wasContrast = Theme.HighContrast;
                Theme.Update(Config.Theme);
                if (wasDark == Theme.Dark && wasContrast == Theme.HighContrast) return;
                BuildMenu();
                if (settings != null && !settings.IsDisposed) settings.ReapplyTheme();
            }, null);
        }

        void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
        {
            if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock)
            {
                screenLocked = true;
                Log.Write("The screen was locked");
                ui.Post(_ => { foreach (Action fire in graceWaiters.ToArray()) fire(); }, null);
            }
            else if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock)
                screenLocked = false;
        }

        // Runs send once the notification has had its time without a reaction (or the screen is locked); at once
        // when the setting is off.
        void AfterGrace(Session s, Func<bool> stillRelevant, Action send)
        {
            if (!Config.PhoneOnlyIfIgnored) { send(); return; }
            int wait = (int)Math.Max(1, (s.GraceUntil - DateTime.Now).TotalMilliseconds);
            var timer = new System.Windows.Forms.Timer { Interval = Math.Min(wait, 10 * 60 * 1000) };
            Action fire = null;
            fire = () =>
            {
                timer.Stop();
                timer.Dispose();
                graceWaiters.Remove(fire);
                if ((screenLocked || !s.Reacted) && stillRelevant()) send();
                else Log.Write("[" + s.ShortId + "] reacted on the computer or settled, no push to the phone");
            };
            // The mouse can renew the time while it runs: check again when it is over.
            timer.Tick += (a, b) =>
            {
                if (!screenLocked && !s.Reacted && DateTime.Now < s.GraceUntil)
                {
                    timer.Interval = (int)Math.Max(1, Math.Min((s.GraceUntil - DateTime.Now).TotalMilliseconds, 10 * 60 * 1000));
                    return;
                }
                fire();
            };
            graceWaiters.Add(fire);
            timer.Start();
        }

        // For the approval service (a worker thread): true when the phone may be told now, false when the user
        // reacted on the computer or the request is settled. Waits while the notification is still on the screen.
        public bool PhoneTurn(string sessionId, WaitHandle settled, DateTime deadline)
        {
            if (!Config.PhoneOnlyIfIgnored) return true;
            while (true)
            {
                bool? verdict = null;
                ui.Send(_ =>
                {
                    Session s = store.Find(sessionId);
                    if (s == null || screenLocked) verdict = true;
                    else if (s.Reacted) verdict = false;
                    else if (DateTime.Now >= s.GraceUntil) verdict = true;
                    // The wait for the user may not eat the whole time of the request: the phone needs time to answer.
                    else if (DateTime.Now >= deadline - TimeSpan.FromSeconds(20)) verdict = true;
                }, null);
                if (verdict.HasValue) return verdict.Value;
                if (settled.WaitOne(250)) return false;
            }
        }
        // A permission prompt that stays unanswered is the moment a push is useful.
        // The push with Allow/Deny or the answers is already on the phone; "waiting" on top of it is noise.
        bool PhoneAlreadyAsked(Session s)
        {
            return approvals.Waiting().Any(r => r.SessionId == s.Id && r.Pushed);
        }

        // Claude has been waiting long for its own background task: a server left running or a stuck task looks like work
        // for ever, and Claude will not go on by itself. Said once per wait, here and on the phone.
        void CheckLongBackground()
        {
            int minutes = Config.BackgroundNotifyMinutes;
            if (minutes <= 0) return;
            DateTime now = DateTime.Now;
            foreach (Session s in store.All)
            {
                if (!s.InBackground || s.BackgroundNotified || (now - s.BackgroundSince).TotalMinutes < minutes) continue;
                s.BackgroundNotified = true;
                Log.Write("[" + s.ShortId + "] waiting for its background task for " + Duration(now - s.BackgroundSince));
                if (IsMuted(s)) continue;
                string title = Loc.T("toast.background.title");
                string text = Loc.T("toast.background.text", Duration(now - s.BackgroundSince));
                Session waiting = s;
                if (!Paused) Balloon(title, WithLabel(SessionLabel(s), text), Level.Background, 7000, () => Native.FocusSession(waiting));
                Notifier.Dispatch(Config, title, WithLabel(RemoteLabel(s), text), false);
            }
        }

        void CheckLongWaiting()
        {
            if (!Config.RemoteWaiting) return;
            DateTime now = DateTime.Now;
            foreach (Session s in store.All)
            {
                if (s.State != State.Waiting || s.WaitingNotified || IsMuted(s) || PhoneAlreadyAsked(s)) continue;
                if ((now - s.Since).TotalMinutes < Config.RemoteWaitingAfterMinutes) continue;
                s.WaitingNotified = true;
                Notifier.Dispatch(Config, Loc.T("toast.waiting.title"), WaitingBody(s), true);
            }
        }

        // Chat titles summarise the conversation, so they only leave the computer when the
        // user opted in to details.
        string RemoteLabel(Session s)
        {
            return Config.RemoteDetails ? SessionLabel(s) : StripProductName(s.Project);
        }

        string WaitingBody(Session s)
        {
            return WithLabel(RemoteLabel(s), Config.RemoteDetails && !string.IsNullOrEmpty(s.Message)
                ? s.Message : Loc.T("toast.waiting.default"));
        }

        void Refresh()
        {
            Level level = store.Overall();
            if (level != shown)
            {
                tray.Icon = icons[level];
                shown = level;
            }
            // The limits go on a second line; if anything is cut, it is the end of the state line.
            string limits = LimitsTip();
            SetTip(limits == null || limits.Length > 60
                ? Truncate(Tooltip(level), TipMax)
                : Truncate(Tooltip(level), TipMax - limits.Length - 1) + "\n" + limits);
        }

        // Windows takes 127 characters for a tray tooltip; .NET Framework refuses more than 63, so a longer text is put
        // in its place and handed over the way .NET itself does it. If that is not possible, the text is cut to 63.
        const int TipMax = 127;
        string tipShown;

        void SetTip(string text)
        {
            if (text == tipShown) return;
            tipShown = text;
            if (text.Length <= 63) { tray.Text = text; return; }
            try
            {
                const System.Reflection.BindingFlags inner = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var field = typeof(NotifyIcon).GetField("text", inner);
                var added = typeof(NotifyIcon).GetField("added", inner);
                var update = typeof(NotifyIcon).GetMethod("UpdateIcon", inner);
                if (field != null && added != null && update != null)
                {
                    field.SetValue(tray, text);
                    if ((bool)added.GetValue(tray)) update.Invoke(tray, new object[] { tray.Visible });
                    return;
                }
            }
            catch { }
            tray.Text = Truncate(text, 63);
        }

        string Tooltip(Level level)
        {
            if (level == Level.None) return Loc.T("tip.none");

            // The state of the most urgent session and how many there are; names do not fit a tooltip.
            // A session that only waits for its background task comes after one that is really working.
            Session top = store.All
                .OrderByDescending(s => (int)s.State * 2 - (s.InBackground ? 1 : 0))
                .ThenBy(s => s.Since)
                .First();
            return Loc.T("tip.state", StateLabel(top), store.All.Count());
        }

        static string Truncate(string s, int max)
        {
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        // Working that is waiting for its own background task is said so: nothing to do for the user yet.
        static string StateLabel(Session s)
        {
            return s.InBackground ? Loc.T("status.background") : StateLabel(s.State);
        }

        static string StateLabel(State s)
        {
            switch (s)
            {
                case State.Waiting: return Loc.T("status.waiting");
                case State.Working: return Loc.T("status.working");
                case State.Compacting: return Loc.T("status.compacting");
                default: return Loc.T("status.idle");
            }
        }

        static string Duration(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return Loc.T("dur.s", (int)t.TotalSeconds);
            if (t.TotalHours < 1) return Loc.T("dur.m", (int)t.TotalMinutes);
            return Loc.T("dur.h", (int)t.TotalHours, t.Minutes);
        }

        string MenuSignature()
        {
            var parts = store.All.OrderBy(s => s.Id).Select(s => s.Id + ":" + s.State + ":" + s.InBackground + ":" + SessionName(s) + ":" + IsMuted(s)).ToList();
            parts.AddRange(approvals.Waiting().Select(r => r.Token).OrderBy(t => t));
            // A timed pause that runs out while the menu is open turns "Resume" back into "Pause".
            parts.Add("paused:" + Paused);
            parts.AddRange(store.All.Where(s => s.Orphan != null).Select(s => s.Orphan.Token));
            return string.Join("|", parts);
        }

        // The menu opens with the mouse at its edge, over the last item (Exit): the second click of a double click
        // on the icon would press it. If the pointer is over the menu, the menu is moved away from it.
        void KeepCursorOffItems()
        {
            Point pointer = Cursor.Position;
            Rectangle b = menu.Bounds;
            if (!b.Contains(pointer)) return;
            Rectangle area = Screen.FromPoint(pointer).WorkingArea;
            int gap = Ui.S(14);
            int top = pointer.Y > b.Top + b.Height / 2 ? pointer.Y - b.Height - gap : pointer.Y + gap;
            menu.Top = Math.Max(area.Top, Math.Min(area.Bottom - b.Height, top));
        }

        void RefreshOpenMenu()
        {
            if (!menu.Visible) { menuLive.Stop(); return; }
            string signature = MenuSignature();
            if (signature == menuSignature) return;
            menuSignature = signature;
            BuildMenu();
            OpenAskingSession();
            // A menu that opened upwards from the taskbar grows downwards: keep it on the screen.
            Rectangle area = Screen.FromPoint(menu.Location).WorkingArea;
            if (menu.Bottom > area.Bottom) menu.Top = Math.Max(area.Top, area.Bottom - menu.Height);
        }

        void BuildMenu()
        {
            foreach (ToolStripItem old in menu.Items.Cast<ToolStripItem>().ToList())
            {
                menu.Items.Remove(old);
                if (old.Image != null) old.Image.Dispose();
                old.Dispose();
            }
            menu.Renderer = Theme.MenuRenderer();
            menu.BackColor = Theme.Surface;
            menu.ForeColor = Theme.Fore;

            AddLimitsToMenu(menu.Items);

            var header = new ToolStripLabel(Loc.T("menu.sessions"));
            header.ForeColor = Theme.Muted;
            menu.Items.Add(header);

            var pending = Config.LocalApprove ? approvals.Waiting() : new List<Approvals.Request>();
            AddUnmatchedApprovals(pending);

            var sessions = store.All.OrderByDescending(s => (int)s.State).ThenBy(s => s.Project).ToList();
            if (sessions.Count == 0)
            {
                var none = new ToolStripLabel(Loc.T("menu.sessions.empty"));
                none.ForeColor = Theme.Muted;
                menu.Items.Add(none);
            }
            foreach (Session s in sessions)
            {
                Session captured = s;
                bool muted = IsMuted(s);
                string text = string.Format("{0} — {1} · {2}", SessionName(s), StateLabel(s), Duration(DateTime.Now - s.Since));
                List<Approvals.Request> asks = pending.Where(r => r.SessionId == s.Id).ToList();
                if (asks.Count > 0) text += " · ❓ " + Loc.T("menu.session.asking");
                else if (Config.LocalApprove && s.State == State.Waiting && s.Orphan != null)
                {
                    asks.Add(s.Orphan);
                    text += " · ❓ " + Loc.T("menu.session.waitwin");
                }
                if (muted) text += " 🔕";
                // '&' would otherwise be taken as a menu mnemonic marker.
                var item = new GuardedItem(text.Replace("&", "&&"));
                item.ForeColor = Theme.Fore;
                item.Image = TrayIcons.Draw(s.InBackground ? Level.Background : ToLevel(s.State), Ui.S(16));

                var show = new GuardedItem(Loc.T("menu.session.show"));
                show.ForeColor = Theme.Fore;
                show.Click += (o, e) => Native.FocusSession(captured);

                var notify = new GuardedItem(Loc.T("menu.session.notify"));
                notify.ForeColor = Theme.Fore;
                notify.Checked = !muted;
                notify.Click += (o, e) => SetMuted(captured.Id, !IsMuted(captured));

                if (asks.Count > 0) item.Tag = "asking";

                // What identifies the session, so it can be matched with a window or a chat.
                var info = new ToolStripLabel(Loc.T("menu.session.id",
                    (s.Id ?? "").Substring(0, Math.Min(8, (s.Id ?? "").Length)),
                    s.ProcessId > 0 ? s.ProcessId.ToString() : "?"));
                info.ForeColor = Theme.Muted;
                FlushLeft(info);
                item.DropDownItems.Add(info);
                // Claude Code reports one folder for a session; the files it touches show where it works.
                if (!string.IsNullOrEmpty(s.WorkDir))
                {
                    var work = new ToolStripLabel(Loc.T("menu.session.workdir", ShortPath(s.WorkDir, 34).Replace("&", "&&")));
                    work.ToolTipText = s.WorkDir;
                    work.ForeColor = Theme.Muted;
                    FlushLeft(work);
                    item.DropDownItems.Add(work);
                }
                if (!string.IsNullOrEmpty(s.Cwd))
                {
                    var start = new ToolStripLabel(Loc.T("menu.session.cwd", ShortPath(s.Cwd, 34).Replace("&", "&&")));
                    start.ToolTipText = s.Cwd;
                    start.ForeColor = Theme.Muted;
                    FlushLeft(start);
                    item.DropDownItems.Add(start);
                }
                item.DropDownItems.Add(new ToolStripSeparator());
                // The request comes after the lines that say which session it is.
                foreach (Approvals.Request r in asks) AddRequest(item.DropDownItems, r, null, captured);
                item.DropDownItems.Add(show);
                item.DropDownItems.Add(notify);
                FitLabels(item);
                item.DropDown.BackColor = Theme.Surface;
                item.DropDown.ForeColor = Theme.Fore;
                menu.Items.Add(item);
            }

            menu.Items.Add(new ToolStripSeparator());

            var settingsItem = new GuardedItem(Loc.T("menu.settings"));
            settingsItem.ForeColor = Theme.Fore;
            settingsItem.Click += (o, e) => ShowSettings();
            menu.Items.Add(settingsItem);

            // One pause for everything on this computer (sounds, pop-ups, the panel): for a while, or until it is
            // switched back on. The phone is not affected. While it is on, the item turns into "Resume".
            if (Paused)
            {
                var resume = new GuardedItem(Config.NotificationsEnabled
                    ? Loc.T("menu.pause.resume.until", new DateTime(Config.DndUntilTicks).ToString("HH:mm"))
                    : Loc.T("menu.pause.resume"));
                resume.ForeColor = Theme.Fore;
                resume.Click += (o, e) =>
                {
                    Config.NotificationsEnabled = true;
                    Config.DndUntilTicks = 0;
                    Config.Save();
                };
                menu.Items.Add(resume);
            }
            else
            {
                var pause = new GuardedItem(Loc.T("menu.pause"));
                pause.ForeColor = Theme.Fore;
                pause.DropDownItems.Add(PauseFor(Loc.T("menu.pause.hour"), DateTime.Now.AddHours(1)));
                pause.DropDownItems.Add(PauseFor(Loc.T("menu.pause.today"), DateTime.Today.AddDays(1)));
                var forever = new GuardedItem(Loc.T("menu.pause.forever"));
                forever.ForeColor = Theme.Fore;
                forever.Click += (o, e) =>
                {
                    Config.NotificationsEnabled = false;
                    Config.Save();
                };
                pause.DropDownItems.Add(forever);
                pause.DropDown.BackColor = Theme.Surface;
                pause.DropDown.ForeColor = Theme.Fore;
                menu.Items.Add(pause);
            }

            if (Config.NtfyEnabled && !string.IsNullOrEmpty(Config.NtfyTopic))
            {
                var push = new GuardedItem(Loc.T("menu.ntfy"));
                push.ForeColor = Theme.Fore;
                push.Checked = !Config.NtfyPaused;
                push.Click += (o, e) =>
                {
                    Config.NtfyPaused = !Config.NtfyPaused;
                    Config.Save();
                };
                menu.Items.Add(push);
            }

            menu.Items.Add(new ToolStripSeparator());

            var exit = new GuardedItem(Loc.T("menu.exit"));
            exit.ForeColor = Theme.Fore;
            exit.Click += (o, e) => ExitThread();
            menu.Items.Add(exit);
        }

        public IEnumerable<Session> Sessions
        {
            get { return store.All.OrderByDescending(s => (int)s.State).ThenBy(s => s.Project).ToList(); }
        }

        public bool IsMuted(Session s)
        {
            return Config.MutedSessions.Contains(s.Id);
        }

        public void SetMuted(string id, bool muted)
        {
            bool has = Config.MutedSessions.Contains(id);
            if (muted == has) return;
            if (muted) Config.MutedSessions.Add(id);
            else Config.MutedSessions.Remove(id);
            Config.Save();
        }

        // A request whose session the menu does not list still gets a place, at the top.
        void AddUnmatchedApprovals(List<Approvals.Request> requests)
        {
            foreach (Approvals.Request r in requests)
            {
                if (store.All.Any(x => x.Id == r.SessionId)) continue;
                AddRequest(menu.Items, r, "?", null);
            }
        }

        // The request itself is too long for a menu: the menu only points to the panel where it is shown and answered.
        void AddRequest(ToolStripItemCollection items, Approvals.Request r, string headerSession, Session session)
        {
            Approvals.Request request = r;
            if (headerSession != null)
            {
                var title = new ToolStripLabel(Loc.T("menu.approve.header", headerSession));
                title.ForeColor = Palette.StatusWarn;
                title.Font = new Font(title.Font, FontStyle.Bold);
                items.Add(title);
            }
            var open = new GuardedItem(Loc.T("menu.approve.open"));
            open.ForeColor = Theme.Fore;
            open.Font = new Font(open.Font, FontStyle.Bold);
            open.Click += (o, e) => ShowAsk(request);
            items.Add(open);
            items.Add(new ToolStripSeparator());
        }

        AskPanel askPanel;
        readonly HashSet<string> askDismissed = new HashSet<string>();
        readonly System.Windows.Forms.Timer askTimer = new System.Windows.Forms.Timer { Interval = 1000 };

        // Shows a new request in the panel on its own and closes the panel when the request is settled
        // (answered here, on the phone or in the window).
        void CheckAsk()
        {
            List<Approvals.Request> pending = Config.LocalApprove ? approvals.Waiting() : new List<Approvals.Request>();
            if (askPanel != null && !askPanel.IsDisposed)
            {
                Approvals.Request current = pending.FirstOrDefault(r => r.Token == askPanel.Token);
                if (current != null)
                {
                    // Once the request went to the phone, the panel says so: it can be answered there too.
                    if (current.Pushed) askPanel.ShowPhoneSent(current.PushedAt);
                    return;
                }
                if (askPanel.Token.StartsWith("orphan:") && Config.LocalApprove
                    && store.All.Any(s => s.State == State.Waiting && s.Orphan != null && s.Orphan.Token == askPanel.Token)) return;
                // The request is gone. If its time ran out, the panel stays (its buttons off) and says so, until the
                // session stops waiting, the user closes it, or another request needs the place; if it was answered, it goes.
                if (askPanel.Deadline > DateTime.MinValue && DateTime.Now >= askPanel.Deadline.AddSeconds(-3))
                {
                    askPanel.ExpireNow();
                    Session waiting = store.Find(askPanel.SessionId);
                    bool another = pending.Any(r => !askDismissed.Contains(r.Token));
                    if (waiting != null && waiting.State == State.Waiting && !another) return;
                }
                askPanel.Close();
            }
            askPanel = null;
            var tokens = new HashSet<string>(pending.Select(r => r.Token));
            if (Config.LocalApprove)
                foreach (Session s in store.All)
                    if (s.State == State.Waiting && s.Orphan != null) tokens.Add(s.Orphan.Token);
            askDismissed.RemoveWhere(t => !tokens.Contains(t));
            if (!Config.PopupAsk || !Config.LocalApprove || Paused) return;
            Approvals.Request next = pending.FirstOrDefault(r => !askDismissed.Contains(r.Token));
            if (next == null)
                next = store.All.Where(s => s.State == State.Waiting && s.Orphan != null && !askDismissed.Contains(s.Orphan.Token))
                    .Select(s => s.Orphan).FirstOrDefault();
            if (next != null) ShowAsk(next);
        }

        void ShowAsk(Approvals.Request r)
        {
            if (askPanel != null && !askPanel.IsDisposed) askPanel.Close();
            Session s = store.All.FirstOrDefault(x => x.Id == r.SessionId);
            string token = r.Token;
            askDismissed.Remove(token);
            var who = new AskSession
            {
                Name = s == null ? "?" : SessionName(s),
                Id = s == null ? "" : s.Id.Substring(0, Math.Min(8, s.Id.Length)),
                Pid = s == null ? 0 : s.ProcessId,
                WorkDir = s == null ? null : s.WorkDir,
                Cwd = s == null ? null : s.Cwd,
            };
            askPanel = new AskPanel(r, who,
                index => { MarkReacted(s); approvals.AnswerOption(token, index); },
                verb => { MarkReacted(s); approvals.Answer(token, verb); },
                () => { MarkReacted(s); askDismissed.Add(token); if (s != null) Native.FocusSession(s); },
                () => { MarkReacted(s); askDismissed.Add(token); },
                token.StartsWith("orphan:"));
            askPanel.Hovered += () => ExtendGrace(s);
            askPanel.Show();
        }
        // The submenu of a session that asks is opened at once, so the question is in front of the user.
        void OpenAskingSession()
        {
            foreach (ToolStripItem item in menu.Items)
            {
                var m = item as ToolStripMenuItem;
                if (m != null && "asking".Equals(m.Tag)) { m.ShowDropDown(); return; }
            }
        }
        // Breaks a long text into lines for a menu label; the end is cut when it runs past maxLines.
        static string Wrap(string text, int width, int maxLines)
        {
            var lines = new List<string>();
            foreach (string paragraph in text.Replace("\r", "").Split('\n'))
            {
                string rest = paragraph;
                do
                {
                    if (rest.Length <= width) { lines.Add(rest); break; }
                    int cut = rest.LastIndexOf(' ', width);
                    if (cut < width / 2) cut = width;
                    lines.Add(rest.Substring(0, cut).TrimEnd());
                    rest = rest.Substring(cut).TrimStart();
                } while (rest.Length > 0);
            }
            if (lines.Count > maxLines)
            {
                lines = lines.Take(maxLines).ToList();
                lines[maxLines - 1] = Shorten(lines[maxLines - 1], width - 1) + "…";
            }
            return string.Join("\n", lines);
        }

        // The chat title identifies a session best; without one, sessions of the same project
        // are told apart by the start of their last prompt.
        public string SessionName(Session s)
        {
            string label = SessionLabel(s);
            return label.Length > 0 ? label : s.Project;
        }

        // Like SessionName, but empty when nothing is left after dropping the product name;
        // messages then go without a "label:" prefix instead of repeating the folder name.
        string SessionLabel(Session s)
        {
            string project = StripProductName(s.Project);
            string detail = StripProductName(s.Title);
            if (detail.Length > 0) detail = Shorten(detail, 40);
            else if (store.All.Count(o => o.Project == s.Project) > 1)
                // A chat that has no title and no prompt yet is a new, still empty one.
                detail = string.IsNullOrEmpty(s.Prompt) ? Loc.T("session.new") + " " + s.ShortId : s.Hint ?? "";

            return project.Length > 0 && detail.Length > 0 ? project + " · " + detail
                 : project.Length > 0 ? project
                 : detail;
        }

        static string WithLabel(string label, string text)
        {
            return label.Length > 0 ? label + ": " + text : text;
        }

        // The menu group is already called "Claude Code sessions", so the product name at the
        // start of a project folder or a generated chat title only repeats it.
        static string StripProductName(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return System.Text.RegularExpressions.Regex.Replace(
                text, @"^\s*Claude\s*Code\s*[:\-–—·]*\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        }

        // Plain text lines in a drop-down start where the icon column ends; pulling them left lines
        // them up with the edge instead of floating next to the commands.
        static void FlushLeft(ToolStripItem item)
        {
            item.Margin = new Padding(-Ui.S(24), 0, 0, 0);
        }

        // A drop-down takes its width from its commands only; plain text lines (a folder path) longer than every
        // command would be cut off, so the drop-down is made at least as wide as they are.
        static void FitLabels(ToolStripMenuItem parent)
        {
            int need = 0;
            foreach (ToolStripItem i in parent.DropDownItems)
                if (i is ToolStripLabel)
                    need = Math.Max(need, TextRenderer.MeasureText(i.Text, i.Font).Width + Ui.S(16));
            if (need > 0) parent.DropDown.MinimumSize = new Size(need, 0);
        }

        // A long folder path keeps its end, which is the part that tells folders apart: "…\features\recommendations".
        internal static string ShortPath(string path, int max)
        {
            if (path.Length <= max) return path;
            string[] parts = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            string result = "";
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                string candidate = parts[i] + (result.Length > 0 ? "\\" + result : "");
                if (candidate.Length + 2 > max) break;
                result = candidate;
            }
            return result.Length == 0 ? "…" + path.Substring(path.Length - (max - 1)) : "…\\" + result;
        }

        static string Shorten(string text, int max)
        {
            return text.Length <= max ? text : text.Substring(0, max - 1) + "…";
        }

        // Shows the tray menu at fixed screen coordinates with the first session's submenu open;
        // used to take documentation screenshots without moving the mouse.
        void ShowMenuAt(Point screen)
        {
            menu.Show(screen);
            Rectangle sub = Rectangle.Empty;
            foreach (ToolStripItem item in menu.Items)
            {
                var m = item as ToolStripMenuItem;
                if (m != null && m.HasDropDownItems)
                {
                    m.ShowDropDown();
                    sub = m.DropDown.Bounds;
                    break;
                }
            }
            // The bounds let a screenshot cut out exactly the menu windows.
            Log.Write(string.Format("menu shot: {0},{1},{2},{3} sub: {4},{5},{6},{7}",
                menu.Left, menu.Top, menu.Width, menu.Height, sub.Left, sub.Top, sub.Width, sub.Height));
        }

        // The global shortcut: the latest waiting request opens with the keyboard focus in it, so it can be answered
        // without the mouse; with nothing waiting, the tray menu opens.
        readonly Hotkey hotkey = new Hotkey();
        public Hotkey Shortcut { get { return hotkey; } }

        void OnHotkey()
        {
            Approvals.Request ask = Config.LocalApprove ? approvals.Waiting().LastOrDefault() : null;
            if (ask == null && Config.LocalApprove)
                ask = store.All.Where(s => s.State == State.Waiting && s.Orphan != null).Select(s => s.Orphan).FirstOrDefault();
            if (ask != null)
            {
                if (askPanel == null || askPanel.IsDisposed || askPanel.Token != ask.Token) ShowAsk(ask);
                askPanel.TakeFocus();
                return;
            }
            ShowMenuFromKeyboard();
        }

        // From the keyboard the menu belongs next to the tray icon, not wherever the mouse happens to be, with the
        // first command selected so the arrows and Enter work at once.
        void ShowMenuFromKeyboard()
        {
            Rectangle icon = Native.TrayIconRect();
            Rectangle area = Native.TrayScreen().WorkingArea;
            Point at = icon.IsEmpty
                ? new Point(area.Right - Ui.S(8), area.Bottom - Ui.S(8))
                : new Point(icon.Left + icon.Width / 2, icon.Top);
            Native.ActivateTrayWindow();
            menu.Show(at, ToolStripDropDownDirection.AboveLeft);
            ToolStripItem first = menu.Items.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Enabled && i.Available);
            if (first != null) first.Select();
            Log.Write("Menu opened from the keyboard at " + at.X + "," + at.Y);
        }

        void ShowMenu()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            // WinForms has no public call for "the menu of this icon, placed like Windows does"; if a future version
            // of the framework drops the private one, the menu opens at the mouse instead of failing.
            var show = typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
            if (show != null) show.Invoke(tray, null);
            else menu.Show(Cursor.Position);
            Log.Write("Menu opened in " + watch.ElapsedMilliseconds + " ms");
        }

        void ShowSettings(int page = 0)
        {
            if (settings != null && !settings.IsDisposed)
            {
                settings.GoTo(page);
                settings.Activate();
                return;
            }
            // Per-monitor aware: on a second monitor with another scale the window draws itself for that monitor
            // instead of being stretched (and blurred) by Windows. The window is created inside the block.
            using (Native.PerMonitorWindows())
            {
                settings = new SettingsForm(this, icons[Level.Idle]);
                settings.GoTo(page);
                settings.Show();
            }
            settings.Activate();
        }

        static Level ToLevel(State state)
        {
            switch (state)
            {
                case State.Waiting: return Level.Waiting;
                case State.Working: return Level.Working;
                case State.Compacting: return Level.Compacting;
                default: return Level.Idle;
            }
        }
        protected override void ExitThreadCore()
        {
            Log.Write("Exiting (closed from the menu or by the system)");
            server.Stop();
            approvals.Stop();
            usage.Stop();
            tick.Stop();
            askTimer.Stop();
            hotkey.Dispose();
            Toasts.Release();
            Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
            if (askPanel != null && !askPanel.IsDisposed) askPanel.Close();
            tray.Visible = false;
            tray.Dispose();
            base.ExitThreadCore();
        }
    }

    // A click that follows the opening of the menu by less than a double click is the second click of a double
    // click on the icon, not a choice: the items ignore it.
    static class MenuGuard
    {
        public static int OpenedAt;

        public static bool Blocked
        {
            get { return unchecked(Environment.TickCount - OpenedAt) < SystemInformation.DoubleClickTime + 100; }
        }
    }

    sealed class GuardedItem : ToolStripMenuItem
    {
        public GuardedItem(string text) : base(text) { }

        protected override void OnClick(EventArgs e)
        {
            if (MenuGuard.Blocked) return;
            base.OnClick(e);
        }
    }
}