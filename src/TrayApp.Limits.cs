using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace Semaphore
{
    // Claude's usage limits in the tray: a block at the top of the menu, the start of the tooltip, a line at the end of
    // every push, and notices as the use grows, runs out and comes back (see Limits.cs for where the figures come from).
    sealed partial class TrayApp
    {
        readonly UsageTracker usage = new UsageTracker();

        // What was last seen of one limit: its window, the last step that was pushed, whether it ran out.
        sealed class LimitWatch
        {
            public DateTime Reset = DateTime.MinValue;
            public int Step = -1;
            public bool Out;
            // The use of the window being watched, as last shown: what the notice of its end reports.
            public double Percent;
            public string PercentText;
        }

        readonly LimitWatch fiveWatch = new LimitWatch(), weekWatch = new LimitWatch();

        void StartLimits()
        {
            usage.Start();
            Notifier.Footer = LimitsFooter;
            refreshTimer.Tick += (o, e) => RefreshLimits();
            refreshTimer.Start();
        }

        // When switched on: Claude Code's /usage, run hidden when the estimate may have drifted (see UsageRefresh).
        readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer { Interval = 60 * 1000 };
        int refreshing, refreshFailures, refreshBreakdowns, capNoticeDay;
        bool refreshTold;

        // The limits are shown and watched at all: switched on, and the sign-in has a subscription with such limits.
        bool LimitsOn(LimitsSnapshot s)
        {
            return Config.LimitsShow && !s.NoSubscription;
        }

        void RefreshLimits()
        {
            if (!Config.LimitsAutoRefresh || AppPaths.IsSandbox || SelfCheckSetup.Active) return;
            LimitsSnapshot snap = usage.Snapshot;
            if (!LimitsOn(snap)) return;
            DateTime nowUtc = DateTime.UtcNow;
            DateTime lastRun = new DateTime(Math.Max(0, Math.Min(Config.LimitsAutoLastUtcTicks, DateTime.MaxValue.Ticks)), DateTimeKind.Utc);
            DateTime activity = store.All.Select(s => s.LastEvent.ToUniversalTime()).DefaultIfEmpty(DateTime.MinValue).Max();
            string reason = UsageRefresh.Reason(snap.Five, snap.Week, snap.FiveMiss, snap.WeekMiss, lastRun, activity, nowUtc);
            if (reason == null) return;
            // The day of the count is the local one: it starts again at midnight.
            DateTime local = DateTime.Now;
            int today = local.Year * 10000 + local.Month * 100 + local.Day;
            int runsToday = Config.LimitsAutoDay == today ? Config.LimitsAutoCount : 0;
            if (runsToday >= UsageRefresh.PerDay)
            {
                // Only if the brake itself failed; the limits are then estimated until midnight, and the user is told once.
                if (capNoticeDay != today)
                {
                    capNoticeDay = today;
                    Log.Write("Usage limits: " + UsageRefresh.PerDay + " runs of /usage today, no more until midnight");
                    if (!Paused) Balloon(Loc.T("limits.auto.cap.title"), Loc.T("limits.auto.cap.text"), Level.Waiting, 12000);
                }
                return;
            }
            if (!UsageRefresh.Allowed(lastRun, refreshFailures, runsToday, nowUtc)) return;
            if (System.Threading.Interlocked.Exchange(ref refreshing, 1) == 1) return;
            // Counted before the run, whatever comes of it: a run that hangs or fails still waits out the brake.
            Config.LimitsAutoLastUtcTicks = nowUtc.Ticks;
            Config.LimitsAutoDay = today;
            Config.LimitsAutoCount = runsToday + 1;
            Config.Save();
            Log.Write("Usage limits: /usage in the background (" + reason + ")");
            string chosen = Config.ClaudePath;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string error = null;
                bool passing = false;
                try { error = UsageRefresh.Run(chosen, out passing); }
                finally { System.Threading.Interlocked.Exchange(ref refreshing, 0); }
                ui.Post(__ => RefreshDone(error, passing), null);
            });
        }

        // A run that fails now and then (the network, a slow start) is no news: the estimate stays, and the next run
        // waits twice as long. Three breakdowns in a row say something is wrong (no Claude Code, a sign-in to renew, no
        // way to Anthropic), once, until a run works again. An answer without figures (Claude Code worked, Anthropic
        // sent none this time) is told only when it lasts: six in a row, with the waits growing to an hour.
        void RefreshDone(string error, bool passing)
        {
            if (error == null) { refreshFailures = 0; refreshBreakdowns = 0; refreshTold = false; return; }
            refreshFailures++;
            if (!passing) refreshBreakdowns++;
            if (refreshTold || (refreshBreakdowns < 3 && refreshFailures < 6)) return;
            refreshTold = true;
            if (!Paused) Balloon(Loc.T("limits.auto.failed.title"), Loc.T("limits.auto.failed.text", error), Level.Waiting, 12000);
        }

        // "27%" when it is Claude Code's own figure, "≈38%" when estimated since.
        static string LimitPercent(LimitView v)
        {
            if (!v.Known) return "?";
            return (v.Exact || v.NotStarted ? "" : "≈") + Math.Round(v.Percent).ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        static CultureInfo LimitCulture()
        {
            try { return CultureInfo.GetCultureInfo(Loc.Code ?? "en"); }
            catch { return CultureInfo.InvariantCulture; }
        }

        // "at 21:50" today, "Wed 19:00" on another day.
        static string LimitReset(LimitView v)
        {
            if (v.Reset == DateTime.MinValue) return "";
            DateTime local = v.Reset.ToLocalTime();
            string when = local.Date == DateTime.Today
                ? Loc.T("limits.reset.at", local.ToString("HH:mm"))
                : Loc.T("limits.reset.on", local.ToString("ddd HH:mm", LimitCulture()));
            return v.ResetApprox ? "≈ " + when : when;
        }

        bool LimitsVisible(LimitsSnapshot s)
        {
            return s.Five.Known || s.Week.Known;
        }

        string LimitsTip()
        {
            LimitsSnapshot s = usage.Snapshot;
            if (!LimitsOn(s) || !LimitsVisible(s)) return null;
            return Loc.T("tip.limits", LimitPercent(s.Five), LimitPercent(s.Week));
        }

        // Called for every push (on a pool thread); null adds nothing.
        string LimitsFooter()
        {
            LimitsSnapshot s = usage.Snapshot;
            if (!LimitsOn(s) || !Config.LimitsInPush || !LimitsVisible(s)) return null;
            return Loc.T("limits.push.footer", LimitPercent(s.Five), LimitReset(s.Five), LimitPercent(s.Week));
        }

        void AddLimitsToMenu(ToolStripItemCollection items)
        {
            LimitsSnapshot s = usage.Snapshot;
            bool cost = Config.CostShow && s.Month.Tokens > 0;
            if (!LimitsOn(s))
            {
                // Without the limits (a sign-in by API key, or switched off) the use at API prices has a block of its own.
                if (!cost) return;
                var own = new ToolStripLabel(Loc.T("cost.menu.title"));
                own.ForeColor = Theme.Muted;
                items.Add(own);
                AddCostLines(items, s);
                items.Add(new ToolStripSeparator());
                return;
            }
            var title = new ToolStripLabel(Loc.T("limits.menu.title"));
            title.ForeColor = Theme.Muted;
            items.Add(title);
            if (!s.AnyAnchor)
            {
                items.Add(MutedLine(Loc.T("limits.menu.setup")));
                if (cost) AddCostLines(items, s);
                items.Add(new ToolStripSeparator());
                return;
            }
            items.Add(LimitLine(Loc.T("limits.five"), s.Five));
            items.Add(LimitLine(Loc.T("limits.week"), s.Week));
            LimitView exact = s.Five.HasExact ? s.Five : s.Week.HasExact ? s.Week : null;
            if (exact != null && !(s.Five.Exact && s.Week.Exact))
                items.Add(MutedLine(Loc.T("limits.menu.exact", exact.ExactAt.ToLocalTime().ToString("HH:mm"),
                    (s.Five.HasExact ? Math.Round(s.Five.ExactPercent) + "%" : "?") + " / " + (s.Week.HasExact ? Math.Round(s.Week.ExactPercent) + "%" : "?"))));
            else if (exact == null)
                items.Add(MutedLine(Loc.T("limits.menu.old")));
            if (cost) AddCostLines(items, s);
            items.Add(new ToolStripSeparator());
        }

        // "Today: 120M tokens · ≈ $38 at API prices" and the same for 30 days: what this computer's use of Claude Code
        // would cost by the token (see ApiPrices).
        static void AddCostLines(ToolStripItemCollection items, LimitsSnapshot s)
        {
            CultureInfo c = LimitCulture();
            items.Add(MutedLine(Loc.T("cost.menu.today", ApiPrices.Tokens(s.Today.Tokens, c), ApiPrices.Dollars(s.Today.Usd, c))));
            items.Add(MutedLine(Loc.T("cost.menu.month", ApiPrices.Tokens(s.Month.Tokens, c), ApiPrices.Dollars(s.Month.Usd, c))));
            if (s.Month.Unpriced.Count > 0)
                items.Add(MutedLine(Loc.T("cost.menu.unpriced", string.Join(", ", s.Month.Unpriced))));
        }

        static ToolStripLabel MutedLine(string text)
        {
            var l = new ToolStripLabel(text.Replace("&", "&&"));
            l.ForeColor = Theme.Muted;
            return l;
        }

        // "▰▰▰▰▱▱▱▱▱▱  5 hours: ≈38% · resets at 21:50", in the warning colours as it fills up.
        static ToolStripLabel LimitLine(string name, LimitView v)
        {
            string text;
            if (!v.Known) text = name + ": " + Loc.T("limits.unknown");
            else if (v.NotStarted) text = name + ": " + Loc.T("limits.notstarted");
            else
            {
                int filled = (int)Math.Round(Math.Max(0, Math.Min(100, v.Percent)) / 10);
                string reset = LimitReset(v);
                text = new string('▰', filled) + new string('▱', 10 - filled) + "  " + name + ": " + LimitPercent(v)
                    + (reset.Length > 0 ? " · " + reset : "");
            }
            var l = new ToolStripLabel(text);
            l.ForeColor = !v.Known || v.NotStarted ? Theme.Muted
                : v.Percent >= 90 ? Palette.StatusError
                : v.Percent >= 70 ? Palette.StatusWarn
                : Theme.Fore;
            l.Font = new Font(l.Font, FontStyle.Bold);
            return l;
        }

        // ---- the context of each session ----

        // On the tray timer: how full each session's context is, read again after the session reported something. Near
        // the point where Claude compacts it on its own, one notice on this computer: finishing the step first, or a
        // /compact chosen by the user, keeps the details that an automatic one may lose.
        void CheckContexts()
        {
            foreach (Session s in store.All)
            {
                if (s.LastEvent > s.ContextChecked)
                {
                    s.ContextChecked = DateTime.Now;
                    long tokens;
                    string model;
                    if (TitleReader.ReadContext(s.TranscriptPath, out tokens, out model))
                    {
                        s.ContextTokens = tokens;
                        s.ContextModel = model;
                    }
                }
                int percent = ContextPercent(s);
                if (percent < 0) continue;
                if (percent < 50) s.ContextWarned = false;
                if (percent < 90 || s.ContextWarned) continue;
                s.ContextWarned = true;
                Log.Write("[" + s.ShortId + "] context " + percent + "% of the compaction point");
                if (Config.ContextWarn && !Paused && !IsMuted(s))
                {
                    Session full = s;
                    Balloon(Loc.T("toast.context.title"), WithLabel(SessionLabel(s), Loc.T("toast.context.text", percent)), Level.Compacting, 9000,
                        () => Native.FocusSession(full));
                }
            }
        }

        // The context as a share of the point where this model gets compacted, or -1 when that point is not known yet.
        int ContextPercent(Session s)
        {
            long limit = usage.ContextLimit(s.ContextModel);
            if (limit <= 0 || s.ContextTokens <= 0) return -1;
            return (int)Math.Min(100, Math.Round(100.0 * s.ContextTokens / limit));
        }

        // "Context: 412k tokens · 43% of the compaction point", or the tokens alone while that point is unknown.
        string ContextLine(Session s)
        {
            if (s.ContextTokens <= 0) return null;
            string k = Math.Round(s.ContextTokens / 1000.0).ToString("0", CultureInfo.InvariantCulture);
            int percent = ContextPercent(s);
            return percent < 0 ? Loc.T("menu.session.context", k) : Loc.T("menu.session.context.pct", k, percent);
        }

        // On the tray timer: pushes for a step reached, a limit that ran out by the estimate, and one that is back.
        void CheckLimits()
        {
            LimitsSnapshot s = usage.Snapshot;
            if (!LimitsOn(s)) return;
            WatchLimit(fiveWatch, s.Five, Loc.T("limits.five"), Config.LimitsStep);
            WatchLimit(weekWatch, s.Week, Loc.T("limits.week"), Config.LimitsStepWeek);
        }

        void WatchLimit(LimitWatch w, LimitView v, string name, int stepPercent)
        {
            if (!v.Known) return;
            // A new window: the reset moved on (by more than the drift of an estimated one), or the old one closed; either
            // only once the old reset time has come, so an estimated reset corrected by /usage is no new window.
            bool newWindow = w.Reset != DateTime.MinValue && DateTime.UtcNow >= w.Reset.AddMinutes(-30)
                && (v.NotStarted || (v.Reset != DateTime.MinValue && Math.Abs((v.Reset - w.Reset).TotalMinutes) > 30));
            if (newWindow)
            {
                if (w.Out && Config.LimitsEvents)
                    LimitPush(Loc.T("limits.push.back.title"), Loc.T("limits.push.back.text", name), Level.Idle);
                // A window that was hardly used ending is no news; one that ran out already had its notice above.
                else if (!w.Out && Config.LimitsEvents && w.Percent >= 1)
                    LimitPush(Loc.T("limits.push.reset.title", name), Loc.T("limits.push.reset.text", w.PercentText), Level.Idle);
                w.Percent = 0;
                w.Out = false;
                w.Step = -1;
            }
            if (v.NotStarted) { w.Reset = DateTime.MinValue; return; }
            w.Reset = v.Reset;
            w.Percent = v.Percent;
            w.PercentText = LimitPercent(v);
            int step = stepPercent > 0 ? (int)(v.Percent / stepPercent) : -1;
            // The first look only takes note: a restart must not push what was already known.
            if (w.Step < 0) w.Step = step;
            else if (step > w.Step)
            {
                w.Step = step;
                LimitPush(Loc.T("limits.push.step.title", name, LimitPercent(v)), LimitReset(v), Level.Working);
            }
            if (v.Percent >= 99.5 && !w.Out)
            {
                w.Out = true;
                if (Config.LimitsEvents)
                    LimitPush(Loc.T("limits.push.out.title"), Loc.T("limits.push.out.text", name, LimitPercent(v), LimitReset(v)), Level.Waiting);
            }
        }

        // A notice on this computer (unless notifications here are off or paused) and a push to the phone (if connected).
        void LimitPush(string title, string text, Level lamp)
        {
            Log.Write("Usage limits: notice \"" + title + "\"");
            if (!Paused) Balloon(title, text, lamp, 7000);
            if (Config.NtfyEnabled && !Config.NtfyPaused && !string.IsNullOrEmpty(Config.NtfyTopic))
                Notifier.SendNtfy(Config.NtfyServer, Notifier.Authorization(Config), Config.NtfyTopic, title, text, false, null, null,
                    "bar_chart", false);
        }
    }
}
