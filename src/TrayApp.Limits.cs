using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace Semaphore
{
    // Claude's usage limits in the tray: a block at the top of the menu, the start of the tooltip, a line at the end of
    // every push, and pushes as the use grows, runs out and comes back (see Limits.cs for where the figures come from).
    sealed partial class TrayApp
    {
        readonly UsageTracker usage = new UsageTracker();

        // What was last seen of one limit: its window, the last step that was pushed, whether it ran out.
        sealed class LimitWatch
        {
            public DateTime Reset = DateTime.MinValue;
            public int Step = -1;
            public bool Out;
        }

        readonly LimitWatch fiveWatch = new LimitWatch(), weekWatch = new LimitWatch();

        void StartLimits()
        {
            usage.Start();
            Notifier.Footer = LimitsFooter;
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
            if (!Config.LimitsShow || !LimitsVisible(s)) return null;
            return Loc.T("tip.limits", LimitPercent(s.Five), LimitPercent(s.Week));
        }

        // Called for every push (on a pool thread); null adds nothing.
        string LimitsFooter()
        {
            LimitsSnapshot s = usage.Snapshot;
            if (!Config.LimitsInPush || !LimitsVisible(s)) return null;
            return Loc.T("limits.push.footer", LimitPercent(s.Five), LimitReset(s.Five), LimitPercent(s.Week));
        }

        void AddLimitsToMenu(ToolStripItemCollection items)
        {
            if (!Config.LimitsShow) return;
            LimitsSnapshot s = usage.Snapshot;
            var title = new ToolStripLabel(Loc.T("limits.menu.title"));
            title.ForeColor = Theme.Muted;
            items.Add(title);
            if (!s.AnyAnchor)
            {
                items.Add(MutedLine(Loc.T("limits.menu.setup")));
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
            items.Add(new ToolStripSeparator());
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

        // On the tray timer: pushes for a step reached, a limit that ran out by the estimate, and one that is back.
        void CheckLimits()
        {
            LimitsSnapshot s = usage.Snapshot;
            WatchLimit(fiveWatch, s.Five, Loc.T("limits.five"));
            WatchLimit(weekWatch, s.Week, Loc.T("limits.week"));
        }

        void WatchLimit(LimitWatch w, LimitView v, string name)
        {
            if (!v.Known) return;
            // A new window: the reset moved on (by more than the drift of an estimated one), or the old one closed.
            bool newWindow = w.Reset != DateTime.MinValue
                && (v.NotStarted || (v.Reset != DateTime.MinValue && Math.Abs((v.Reset - w.Reset).TotalMinutes) > 30));
            if (newWindow)
            {
                if (w.Out && Config.LimitsEvents)
                    LimitPush(Loc.T("limits.push.back.title"), Loc.T("limits.push.back.text", name));
                w.Out = false;
                w.Step = -1;
            }
            if (v.NotStarted) { w.Reset = DateTime.MinValue; return; }
            w.Reset = v.Reset;
            int step = Config.LimitsStep > 0 ? (int)(v.Percent / Config.LimitsStep) : -1;
            // The first look only takes note: a restart must not push what was already known.
            if (w.Step < 0) w.Step = step;
            else if (step > w.Step)
            {
                w.Step = step;
                LimitPush(Loc.T("limits.push.step.title", name, LimitPercent(v)), LimitReset(v));
            }
            if (v.Percent >= 99.5 && !w.Out)
            {
                w.Out = true;
                if (Config.LimitsEvents)
                    LimitPush(Loc.T("limits.push.out.title"), Loc.T("limits.push.out.text", name, LimitPercent(v), LimitReset(v)));
            }
        }

        void LimitPush(string title, string text)
        {
            Log.Write("Usage limits: push \"" + title + "\"");
            if (Config.NtfyEnabled && !Config.NtfyPaused && !string.IsNullOrEmpty(Config.NtfyTopic))
                Notifier.SendNtfy(Config.NtfyServer, Notifier.Authorization(Config), Config.NtfyTopic, title, text, false, null, null,
                    "bar_chart", false);
        }
    }
}
