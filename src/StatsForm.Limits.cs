using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace Semaphore
{
    // The tab of the limits: the weeks (each one's end figure, the current one with where its pace leads, and how
    // each week filled up), then the 5-hour windows of 30 days with the ones that ran out, and a few figures about them.
    sealed partial class StatsView
    {
        const int WeekRows = 6, WeekCurves = 4;

        int DrawLimits(Painter c, int width)
        {
            int margin = Ui.S(16), pad = Ui.S(14);
            int y = Ui.S(12);
            int inner = width - 2 * margin - 2 * pad;
            int left = margin + pad;
            string empty = limits != null && limits.NoSubscription ? Loc.T("stats.lim.api")
                : history == null || (history.Weeks.Count == 0 && history.Fives.Count == 0) ? Loc.T("limits.menu.setup") : null;
            if (empty != null)
            {
                y = c.Card(margin, y, width - 2 * margin, pad, yy => c.Text(empty, left, yy, inner, Theme.Muted));
                return y + margin;
            }
            DateTime now = DateTime.UtcNow;

            if (history.Weeks.Count > 0)
                y = c.Card(margin, y, width - 2 * margin, pad, yy =>
                {
                    yy = c.Text(Loc.T("stats.lim.week"), left, yy, inner, Theme.Fore, c.Header) + Ui.S(6);
                    LimitWindow cur = history.Weeks.LastOrDefault(w => w.Current);
                    if (cur != null)
                    {
                        yy = c.Text(Loc.T("stats.lim.now", Figure(cur.Last, cur.Estimated)), left, yy, inner, Theme.Fore, c.Big);
                        yy = c.Text(Loc.T("limits.reset.on", When(cur.End)), left, yy + Ui.S(2), inner, Theme.Muted);
                        string pace = Pace(cur, now);
                        if (pace != null) yy = c.Text(pace, left, yy + Ui.S(2), inner, Theme.Fore);
                        yy += Ui.S(10);
                    }
                    yy = c.Text(Loc.T("stats.lim.weeks"), left, yy, inner, Theme.Fore, c.Bold) + Ui.S(6);
                    yy = Weeks(c, left, yy, inner, now);
                    yy = c.Text(Loc.T("stats.lim.curve"), left, yy + Ui.S(8), inner, Theme.Fore, c.Bold);
                    yy = c.Text(Loc.T("stats.lim.curve.hint"), left, yy, inner, Theme.Muted) + Ui.S(8);
                    return Curves(c, left, yy, inner, cur, now);
                });

            y = c.Card(margin, y, width - 2 * margin, pad, yy =>
            {
                yy = c.Text(Loc.T("stats.lim.five"), left, yy, inner, Theme.Fore, c.Header) + Ui.S(2);
                yy = c.Text(Loc.T("stats.lim.five.hint"), left, yy, inner, Theme.Muted) + Ui.S(8);
                return Fives(c, left, yy, inner, now);
            });

            y += Ui.S(2);
            y = c.Text(Loc.T("stats.lim.accuracy"), margin + Ui.S(4), y, width - 2 * margin, Theme.Muted);
            return y + margin;
        }

        // "63%" for Claude Code's own figure, "≈63%" with the estimate in it.
        string Figure(double percent, bool estimated)
        {
            return (estimated ? "≈" : "") + Math.Round(percent).ToString("0", Culture) + "%";
        }

        string When(DateTime utc) { return utc.ToLocalTime().ToString("ddd dd.MM HH:mm", Culture); }

        string Duration(TimeSpan t)
        {
            int minutes = Math.Max(0, (int)Math.Round(t.TotalMinutes));
            return minutes >= 60 ? Loc.T("stats.dur.hm", minutes / 60, minutes % 60) : Loc.T("stats.dur.m", minutes);
        }

        // Where the use of the week so far leads by its reset, at the same pace (nights and days off included, as
        // they are in what is left of the week). Said only after half a day: before that the pace means little.
        string Pace(LimitWindow week, DateTime now)
        {
            if (week.OutAt.HasValue) return Loc.T("stats.lim.out", When(week.OutAt.Value));
            double hours = (now - week.Start).TotalHours;
            if (hours < 12 || week.Last <= 0) return null;
            double rate = week.Last / hours;
            double atReset = week.Last + rate * (week.End - now).TotalHours;
            if (atReset >= 100) return Loc.T("stats.lim.pace.out", When(now.AddHours((100 - week.Last) / rate)));
            return Loc.T("stats.lim.pace.end", Math.Round(atReset).ToString("0", Culture));
        }

        // A row a week, the latest at the bottom: its dates, a bar to 100%, its figure and how it ended.
        int Weeks(Painter c, int left, int y, int inner, DateTime now)
        {
            var rows = history.Weeks.Skip(Math.Max(0, history.Weeks.Count - WeekRows)).ToList();
            Func<LimitWindow, string> label = w => w.Start.ToLocalTime().ToString("dd.MM", Culture) + "–" + w.End.ToLocalTime().ToString("dd.MM", Culture);
            Func<LimitWindow, string> note = w => Figure(w.Peak, w.Estimated)
                + (w.OutAt.HasValue ? " · " + Loc.T("stats.lim.out", When(w.OutAt.Value)) : "")
                + (w.EarlyReset.HasValue ? " · " + Loc.T("stats.lim.early", w.EarlyReset.Value.ToLocalTime().ToString("ddd dd.MM", Culture)) : "")
                + (w.Current ? " · " + Loc.T("stats.lim.running") : "");
            int labelWidth = rows.Max(w => c.Width(label(w))) + Ui.S(12);
            int noteWidth = rows.Max(w => c.Width(note(w))) + Ui.S(8);
            int full = Math.Max(Ui.S(80), inner - labelWidth - noteWidth);
            foreach (LimitWindow w in rows)
            {
                c.Line(label(w), left, y, labelWidth, w.Current ? Theme.Fore : Theme.Muted);
                int length = (int)Math.Round(full * Math.Max(0, Math.Min(100, w.Peak)) / 100);
                // The track shows where 100% is: a week is measured against the whole limit, not against the others.
                c.Fill(left + labelWidth, y + Ui.S(3), full, Font.Height - Ui.S(6), Theme.Hover);
                c.Fill(left + labelWidth, y + Ui.S(3), length, Font.Height - Ui.S(6), w.OutAt.HasValue ? Palette.StatusError : Palette.AccentBar);
                c.Line(note(w), left + labelWidth + full + Ui.S(8), y, inner - labelWidth - full - Ui.S(8), w.OutAt.HasValue ? Palette.StatusError : Theme.Fore);
                y += Font.Height + Ui.S(6);
            }
            return y;
        }

        // How the percentage of each week grew over its seven days: the weeks before thin and grey, the current one in
        // the accent colour, and its pace on to the reset dashed.
        int Curves(Painter c, int left, int y, int inner, LimitWindow cur, DateTime now)
        {
            var weeks = history.Weeks.Where(w => !w.Current).ToList();
            weeks = weeks.Skip(Math.Max(0, weeks.Count - WeekCurves)).ToList();
            int gutter = c.Width("100%") + Ui.S(8);
            int plotLeft = left + gutter, plotWidth = inner - gutter;
            int chart = Ui.S(120);
            int top = y + Font.Height / 2, bottom = top + chart;
            foreach (int mark in new[] { 0, 50, 100 })
            {
                int ly = bottom - chart * mark / 100;
                c.Fill(plotLeft, ly, plotWidth, 1, mark == 0 ? Theme.Muted : Theme.Border);
                c.Right(mark + "%", plotLeft - Ui.S(6), ly - Font.Height / 2, Theme.Muted);
            }
            Func<LimitWindow, KeyValuePair<DateTime, double>, PointF> at = (w, p) => new PointF(
                plotLeft + (float)(plotWidth * Math.Max(0, Math.Min(1, (p.Key - w.Start).TotalDays / 7))),
                bottom - (float)(chart * Math.Max(0, Math.Min(100, p.Value)) / 100));
            foreach (LimitWindow w in weeks)
                c.Lines(Steps(w.Curve).Select(p => at(w, p)).ToArray(), Theme.Muted, Ui.S(1) * 1.2f);
            if (cur != null)
            {
                c.Lines(Steps(cur.Curve).Select(p => at(cur, p)).ToArray(), Palette.AccentBar, Ui.S(1) * 2.5f);
                double hours = (now - cur.Start).TotalHours;
                if (hours >= 12 && cur.Last > 0 && !cur.OutAt.HasValue)
                {
                    double rate = cur.Last / hours;
                    double end = Math.Min(100, cur.Last + rate * (cur.End - now).TotalHours);
                    DateTime endAt = end >= 100 ? now.AddHours((100 - cur.Last) / rate) : cur.End;
                    c.Lines(new[] { at(cur, new KeyValuePair<DateTime, double>(now, cur.Last)), at(cur, new KeyValuePair<DateTime, double>(endAt, end)) },
                        Palette.AccentBar, Ui.S(1) * 1.5f, true);
                }
            }
            // The days of the week under the chart, from the day the current (or the last) week began.
            DateTime first = (cur ?? history.Weeks[history.Weeks.Count - 1]).Start;
            y = bottom + Ui.S(4);
            for (int d = 0; d < 7; d++)
                c.Center(first.AddDays(d).ToLocalTime().ToString("ddd", Culture), plotLeft + (int)(plotWidth * (d + 0.5) / 7), y, Theme.Muted);
            return y + Font.Height;
        }

        // A percentage stays as it was until the next point: the line goes across, then up.
        static IEnumerable<KeyValuePair<DateTime, double>> Steps(List<KeyValuePair<DateTime, double>> curve)
        {
            for (int i = 0; i < curve.Count; i++)
            {
                if (i > 0) yield return new KeyValuePair<DateTime, double>(curve[i].Key, curve[i - 1].Value);
                yield return curve[i];
            }
        }

        // The 5-hour windows of the last 30 days on a line of time: a bar where each one was, as high as its peak, red
        // where it ran out; then the counts.
        int Fives(Painter c, int left, int y, int inner, DateTime now)
        {
            DateTime first = DateTime.Today.AddDays(-29).ToUniversalTime(), last = DateTime.Today.AddDays(1).ToUniversalTime();
            var shown = history.Fives.Where(w => w.End > first && w.Peak > 0).ToList();
            int gutter = c.Width("100%") + Ui.S(8);
            int plotLeft = left + gutter, plotWidth = inner - gutter;
            int chart = Ui.S(90);
            int top = y + Font.Height / 2, bottom = top + chart;
            foreach (int mark in new[] { 0, 50, 100 })
            {
                int ly = bottom - chart * mark / 100;
                c.Fill(plotLeft, ly, plotWidth, 1, mark == 0 ? Theme.Muted : Theme.Border);
                c.Right(mark + "%", plotLeft - Ui.S(6), ly - Font.Height / 2, Theme.Muted);
            }
            double span = (last - first).TotalMinutes;
            Func<DateTime, int> x = t => plotLeft + (int)Math.Round(plotWidth * Math.Max(0, Math.Min(1, (t - first).TotalMinutes / span)));
            foreach (LimitWindow w in shown)
            {
                int x0 = x(w.Start), x1 = Math.Max(x0 + Ui.S(2), x(w.End) - 1);
                int h = Math.Max(2, (int)Math.Round(chart * Math.Min(100, w.Peak) / 100));
                c.Fill(x0, bottom - h, x1 - x0, h, w.OutAt.HasValue ? Palette.StatusError : Palette.AccentBar);
                if (c.Painting)
                    days.Add(new KeyValuePair<Rectangle, string>(new Rectangle(x0 - 1, top, x1 - x0 + 2, chart + 1), FiveTip(w, now)));
            }
            y = bottom + Ui.S(4);
            for (int d = 0; d < 30; d += 7)
                c.Center(DateTime.Today.AddDays(-29 + d).ToString("dd.MM", Culture), x(first.AddDays(d + 0.5)), y, Theme.Muted);
            y += Font.Height + Ui.S(10);

            if (shown.Count == 0) return c.Text(Loc.T("stats.empty"), left, y, inner, Theme.Muted);
            var outs = shown.Where(w => w.OutAt.HasValue).ToList();
            TimeSpan lost = TimeSpan.Zero;
            foreach (LimitWindow w in outs) lost += (w.End < now ? w.End : now) - w.OutAt.Value;
            string count = Loc.T("stats.lim.count", shown.Count.ToString("N0", Culture), outs.Count.ToString("N0", Culture));
            if (outs.Count > 0) count += " · " + Loc.T("stats.lim.lost", Duration(lost));
            y = c.Text(count, left, y, inner, outs.Count > 0 ? Palette.StatusError : Theme.Fore);
            y = c.Text(Loc.T("stats.lim.avg", Math.Round(shown.Average(w => w.Peak)).ToString("0", Culture)), left, y + Ui.S(2), inner, Theme.Fore);
            // The five hours in a row (across midnight too) with the most of the use.
            int best = 0;
            double most = 0;
            for (int h = 0; h < 24; h++)
            {
                double sum = 0;
                for (int k = 0; k < 5; k++) sum += history.Hours[(h + k) % 24];
                if (sum > most) { most = sum; best = h; }
            }
            if (most > 0)
                y = c.Text(Loc.T("stats.lim.hours", best.ToString("00") + ":00", ((best + 5) % 24).ToString("00") + ":00",
                    Math.Round(100 * most).ToString("0", Culture)), left, y + Ui.S(2), inner, Theme.Fore);
            return y;
        }

        string FiveTip(LimitWindow w, DateTime now)
        {
            var parts = new List<string> { When(w.Start) + "–" + w.End.ToLocalTime().ToString("HH:mm", Culture) };
            parts.Add(Loc.T("stats.lim.tip.peak", Figure(w.Peak, w.Estimated)));
            if (w.OutAt.HasValue)
                parts.Add(Loc.T("stats.lim.tip.out", w.OutAt.Value.ToLocalTime().ToString("HH:mm", Culture), Duration((w.End < now ? w.End : now) - w.OutAt.Value)));
            if (w.EarlyReset.HasValue) parts.Add(Loc.T("stats.lim.early", w.EarlyReset.Value.ToLocalTime().ToString("HH:mm", Culture)));
            if (w.Current) parts.Add(Loc.T("stats.lim.running"));
            if (w.Estimated) parts.Add(Loc.T("stats.lim.tip.est"));
            return string.Join("\n", parts);
        }
    }
}
