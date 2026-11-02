using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Semaphore
{
    // The tab of the limits: the weeks as bars (the current one with where its pace leads; a click on a bar shows how
    // that week filled up), then the 5-hour windows on a line of time with the ones that ran out, and a few figures
    // of the last 30 days. Both charts scroll sideways when there is more than fits at a readable width.
    sealed partial class StatsView
    {
        // A chart that scrolls sideways by slots (weeks, or days): how many there are, how many fit, how many of the
        // latest are scrolled out of sight on the right (0: the latest in sight, which new ones keep).
        sealed class Strip
        {
            public int Back, Count, Visible;
            public Rectangle Area, Track, Thumb;
            public int DragX = -1, DragBack;
            public int First { get { return Math.Max(0, Count - Visible - Back); } }
            public bool Scrolls { get { return Count > Visible; } }
            public void Clamp() { Back = Math.Max(0, Math.Min(Back, Math.Max(0, Count - Visible))); }
        }

        readonly Strip weekStrip = new Strip(), fiveStrip = new Strip();
        // The week whose curve is shown, by its start; null for the current one (or the last).
        DateTime? chosenWeek;
        readonly List<KeyValuePair<Rectangle, DateTime>> weekBars = new List<KeyValuePair<Rectangle, DateTime>>();
        const int VisibleDays = 10;

        int DrawLimits(Painter c, int width)
        {
            if (c.Painting)
            {
                weekBars.Clear();
                weekStrip.Area = weekStrip.Track = weekStrip.Thumb = Rectangle.Empty;
                fiveStrip.Area = fiveStrip.Track = fiveStrip.Thumb = Rectangle.Empty;
            }
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
                    yy = c.Text(Loc.T("stats.lim.weeks"), left, yy, inner, Theme.Fore, c.Bold);
                    yy = c.Text(Loc.T("stats.lim.weeks.hint"), left, yy, inner, Theme.Muted) + Ui.S(6);
                    yy = WeekBars(c, left, yy, inner);

                    LimitWindow chosen = Chosen();
                    yy = c.Text(Loc.T("stats.lim.curve", Range(chosen)), left, yy + Ui.S(10), inner, Theme.Fore, c.Bold);
                    yy = c.Text(Note(chosen), left, yy, inner, chosen.OutAt.HasValue ? Palette.StatusError : Theme.Muted);
                    if (chosen.Current) yy = c.Text(Loc.T("stats.lim.curve.hint"), left, yy, inner, Theme.Muted);
                    return Curve(c, left, yy + Ui.S(8), inner, chosen, now);
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

        LimitWindow Chosen()
        {
            LimitWindow w = chosenWeek.HasValue ? history.Weeks.FirstOrDefault(x => x.Start == chosenWeek.Value) : null;
            return w ?? history.Weeks.LastOrDefault(x => x.Current) ?? history.Weeks[history.Weeks.Count - 1];
        }

        // ---- the figures in words ----

        // "63%" for Claude Code's own figure, "≈63%" with the estimate in it.
        string Figure(double percent, bool estimated)
        {
            return (estimated ? "≈" : "") + Math.Round(percent).ToString("0", Culture) + "%";
        }

        string When(DateTime utc) { return utc.ToLocalTime().ToString("ddd dd.MM HH:mm", Culture); }

        string Range(LimitWindow w)
        {
            return w.Start.ToLocalTime().ToString("dd.MM", Culture) + "–" + w.End.ToLocalTime().ToString("dd.MM", Culture);
        }

        // "85% · ran out Mon 13.10 19:00 · early reset Sun 12.10 · running".
        string Note(LimitWindow w)
        {
            var parts = new List<string> { Figure(w.Peak, w.Estimated) };
            if (w.OutAt.HasValue) parts.Add(Loc.T("stats.lim.out", When(w.OutAt.Value)));
            if (w.EarlyReset.HasValue) parts.Add(Loc.T("stats.lim.early", w.EarlyReset.Value.ToLocalTime().ToString("ddd dd.MM", Culture)));
            if (w.Current) parts.Add(Loc.T("stats.lim.running"));
            return string.Join(" · ", parts);
        }

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

        // ---- the weeks ----

        // A bar a week up to 100%, its figure over it and the day it began under it, at a width that stays readable;
        // the chart scrolls when they do not all fit. The chosen week stands on a lighter ground.
        int WeekBars(Painter c, int left, int y, int inner)
        {
            var weeks = history.Weeks;
            LimitWindow chosen = Chosen();
            int gutter = c.Width("100%") + Ui.S(8);
            int plotLeft = left + gutter, plotWidth = inner - gutter;
            int slot = Ui.S(58), bar = Ui.S(34);
            weekStrip.Count = weeks.Count;
            weekStrip.Visible = Math.Max(1, plotWidth / slot);
            weekStrip.Clamp();
            int chart = Ui.S(100);
            int top = y + Font.Height + Ui.S(2), bottom = top + chart;
            Marks(c, plotLeft, plotWidth, bottom, chart);
            for (int k = 0; k < weekStrip.Visible && weekStrip.First + k < weeks.Count; k++)
            {
                LimitWindow w = weeks[weekStrip.First + k];
                int x = plotLeft + k * slot;
                var box = new Rectangle(x + Ui.S(2), y, slot - Ui.S(4), bottom - y + Font.Height + Ui.S(6));
                if (w == chosen) c.Fill(box.X, box.Y, box.Width, box.Height, Theme.Hover);
                int h = Math.Max(2, (int)Math.Round(chart * Math.Max(0, Math.Min(100, w.Peak)) / 100));
                int bx = x + (slot - bar) / 2;
                c.Fill(bx, bottom - h, bar, h, w.OutAt.HasValue ? Palette.StatusError : Palette.AccentBar);
                c.Center(Figure(w.Peak, w.Estimated), x + slot / 2, bottom - h - Font.Height - Ui.S(1), Theme.Fore);
                c.Center(w.Start.ToLocalTime().ToString("dd.MM", Culture), x + slot / 2, bottom + Ui.S(4), w == chosen || w.Current ? Theme.Fore : Theme.Muted);
                if (c.Painting)
                {
                    weekBars.Add(new KeyValuePair<Rectangle, DateTime>(box, w.Start));
                    days.Add(new KeyValuePair<Rectangle, string>(box, Range(w) + "\n" + Note(w) + (w.Estimated ? "\n" + Loc.T("stats.lim.tip.est") : "")));
                }
            }
            if (c.Painting) weekStrip.Area = new Rectangle(plotLeft, y, plotWidth, bottom - y + Font.Height + Ui.S(6));
            y = bottom + Font.Height + Ui.S(8);
            return Scroll(c, weekStrip, plotLeft, y, plotWidth);
        }

        // The marks of 0, 50 and 100% across a chart.
        void Marks(Painter c, int plotLeft, int plotWidth, int bottom, int chart)
        {
            foreach (int mark in new[] { 0, 50, 100 })
            {
                int ly = bottom - chart * mark / 100;
                c.Fill(plotLeft, ly, plotWidth, 1, mark == 0 ? Theme.Muted : Theme.Border);
                c.Right(mark + "%", plotLeft - Ui.S(6), ly - Font.Height / 2, Theme.Muted);
            }
        }

        // The scroll bar under a chart that does not fit: a thin track with a thumb as wide as the visible part.
        int Scroll(Painter c, Strip s, int x, int y, int width)
        {
            if (!s.Scrolls) return y;
            var track = new Rectangle(x, y, width, Ui.S(6));
            int thumbWidth = Math.Max(Ui.S(24), width * s.Visible / s.Count);
            int thumbLeft = x + (int)Math.Round((width - thumbWidth) * (double)s.First / (s.Count - s.Visible));
            c.Fill(track.X, track.Y, track.Width, track.Height, Theme.Hover);
            c.Fill(thumbLeft, track.Y, thumbWidth, track.Height, s.DragX >= 0 ? Theme.Fore : Theme.Muted);
            if (c.Painting)
            {
                s.Track = Rectangle.Inflate(track, 0, Ui.S(4));
                s.Thumb = new Rectangle(thumbLeft, track.Y - Ui.S(4), thumbWidth, track.Height + Ui.S(8));
            }
            return track.Bottom + Ui.S(4);
        }

        // How the percentage of the chosen week grew over its seven days; for the current week its pace on to the
        // reset, dashed.
        int Curve(Painter c, int left, int y, int inner, LimitWindow week, DateTime now)
        {
            int gutter = c.Width("100%") + Ui.S(8);
            int plotLeft = left + gutter, plotWidth = inner - gutter;
            int chart = Ui.S(120);
            int top = y + Font.Height / 2, bottom = top + chart;
            Marks(c, plotLeft, plotWidth, bottom, chart);
            Func<KeyValuePair<DateTime, double>, PointF> at = p => new PointF(
                plotLeft + (float)(plotWidth * Math.Max(0, Math.Min(1, (p.Key - week.Start).TotalDays / 7))),
                bottom - (float)(chart * Math.Max(0, Math.Min(100, p.Value)) / 100));
            c.Lines(Steps(week.Curve).Select(at).ToArray(), week.OutAt.HasValue ? Palette.StatusError : Palette.AccentBar, Ui.S(1) * 2.5f);
            double hours = (now - week.Start).TotalHours;
            if (week.Current && hours >= 12 && week.Last > 0 && !week.OutAt.HasValue)
            {
                double rate = week.Last / hours;
                double end = Math.Min(100, week.Last + rate * (week.End - now).TotalHours);
                DateTime endAt = end >= 100 ? now.AddHours((100 - week.Last) / rate) : week.End;
                c.Lines(new[] { at(new KeyValuePair<DateTime, double>(now, week.Last)), at(new KeyValuePair<DateTime, double>(endAt, end)) },
                    Palette.AccentBar, Ui.S(1) * 1.5f, true);
            }
            y = bottom + Ui.S(4);
            for (int d = 0; d < 7; d++)
                c.Center(week.Start.AddDays(d).ToLocalTime().ToString("ddd dd.MM", Culture), plotLeft + (int)(plotWidth * (d + 0.5) / 7), y, Theme.Muted);
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

        // ---- the 5-hour windows ----

        // Ten days at a time on a line of time, the chart scrolling back to the first window kept: a bar where each
        // window was, as high as its peak, red where it ran out; then the counts of the last 30 days.
        int Fives(Painter c, int left, int y, int inner, DateTime now)
        {
            DateTime today = DateTime.Today;
            DateTime firstDay = history.Fives.Count > 0 ? history.Fives.Min(w => w.Start).ToLocalTime().Date : today;
            fiveStrip.Count = Math.Max(VisibleDays, (int)(today - firstDay).TotalDays + 1);
            fiveStrip.Visible = VisibleDays;
            fiveStrip.Clamp();
            DateTime viewFirst = today.AddDays(1 - VisibleDays - fiveStrip.Back);
            DateTime from = viewFirst.ToUniversalTime(), to = viewFirst.AddDays(VisibleDays).ToUniversalTime();

            int gutter = c.Width("100%") + Ui.S(8);
            int plotLeft = left + gutter, plotWidth = inner - gutter;
            int chart = Ui.S(90);
            int top = y + Font.Height / 2, bottom = top + chart;
            double span = (to - from).TotalMinutes;
            Func<DateTime, int> x = t => plotLeft + (int)Math.Round(plotWidth * Math.Max(0, Math.Min(1, (t - from).TotalMinutes / span)));
            // Midnights, faint, so that the windows read against the days.
            for (int d = 1; d < VisibleDays; d++)
                c.Fill(x(viewFirst.AddDays(d).ToUniversalTime()), top, 1, chart, Theme.Border);
            Marks(c, plotLeft, plotWidth, bottom, chart);
            foreach (LimitWindow w in history.Fives.Where(w => w.End > from && w.Start < to && w.Peak > 0))
            {
                int x0 = x(w.Start), x1 = Math.Max(x0 + Ui.S(3), x(w.End) - 1);
                int h = Math.Max(2, (int)Math.Round(chart * Math.Min(100, w.Peak) / 100));
                c.Fill(x0, bottom - h, x1 - x0, h, w.OutAt.HasValue ? Palette.StatusError : Palette.AccentBar);
                if (c.Painting)
                    days.Add(new KeyValuePair<Rectangle, string>(new Rectangle(x0 - 1, top, x1 - x0 + 2, chart + 1), FiveTip(w, now)));
            }
            if (c.Painting) fiveStrip.Area = new Rectangle(plotLeft, top, plotWidth, chart + Font.Height + Ui.S(4));
            y = bottom + Ui.S(4);
            int dayWidth = plotWidth / VisibleDays;
            int every = c.Width("00.00") + Ui.S(8) <= dayWidth ? 1 : 2;
            for (int d = 0; d < VisibleDays; d += every)
                c.Center(viewFirst.AddDays(d).ToString("dd.MM", Culture), plotLeft + dayWidth * d + dayWidth / 2, y, Theme.Muted);
            y = Scroll(c, fiveStrip, plotLeft, y + Font.Height + Ui.S(6), plotWidth) + Ui.S(6);

            var month = history.Fives.Where(w => w.End > now.AddDays(-30) && w.Peak > 0).ToList();
            if (month.Count == 0) return c.Text(Loc.T("stats.empty"), left, y, inner, Theme.Muted);
            var outs = month.Where(w => w.OutAt.HasValue).ToList();
            TimeSpan lost = TimeSpan.Zero;
            foreach (LimitWindow w in outs) lost += (w.End < now ? w.End : now) - w.OutAt.Value;
            string count = Loc.T("stats.lim.count", month.Count.ToString("N0", Culture), outs.Count.ToString("N0", Culture));
            if (outs.Count > 0) count += " · " + Loc.T("stats.lim.lost", Duration(lost));
            y = c.Text(count, left, y, inner, outs.Count > 0 ? Palette.StatusError : Theme.Fore);
            y = c.Text(Loc.T("stats.lim.avg", Math.Round(month.Average(w => w.Peak)).ToString("0", Culture)), left, y + Ui.S(2), inner, Theme.Fore);
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

        // ---- the mouse and the keys on this tab ----

        bool OnLimitsTab { get { return period == StatsForm.LimitsTab && history != null; } }

        // The wheel over a chart that scrolls moves it a week or a day; up is back in time.
        bool LimitsWheel(MouseEventArgs e)
        {
            if (!OnLimitsTab) return false;
            foreach (Strip s in new[] { weekStrip, fiveStrip })
                if (s.Scrolls && s.Area.Contains(e.Location))
                {
                    s.Back += Math.Sign(e.Delta);
                    s.Clamp();
                    Invalidate();
                    return true;
                }
            return false;
        }

        bool LimitsDown(MouseEventArgs e)
        {
            if (!OnLimitsTab) return false;
            foreach (Strip s in new[] { weekStrip, fiveStrip })
            {
                if (s.Thumb.Contains(e.Location))
                {
                    s.DragX = e.X;
                    s.DragBack = s.Back;
                    Capture = true;
                    Invalidate();
                    return true;
                }
                // A click on the track beside the thumb: a page of the chart.
                if (s.Track.Contains(e.Location))
                {
                    s.Back += e.X < s.Thumb.Left ? s.Visible : -s.Visible;
                    s.Clamp();
                    Invalidate();
                    return true;
                }
            }
            foreach (var b in weekBars)
                if (b.Key.Contains(e.Location))
                {
                    chosenWeek = b.Value;
                    Invalidate();
                    return true;
                }
            return false;
        }

        bool LimitsMove(MouseEventArgs e)
        {
            foreach (Strip s in new[] { weekStrip, fiveStrip })
            {
                if (s.DragX < 0) continue;
                // The thumb follows the pointer: its free run is the whole chart.
                int run = Math.Max(1, s.Track.Width - s.Thumb.Width);
                s.Back = s.DragBack - (int)Math.Round((e.X - s.DragX) * (double)(s.Count - s.Visible) / run);
                s.Clamp();
                Invalidate();
                return true;
            }
            return false;
        }

        bool LimitsUp()
        {
            bool any = false;
            foreach (Strip s in new[] { weekStrip, fiveStrip })
                if (s.DragX >= 0) { s.DragX = -1; any = true; }
            if (!any) return false;
            Capture = false;
            Invalidate();
            return true;
        }

        bool OverWeekBar(Point p) { return OnLimitsTab && weekBars.Any(b => b.Key.Contains(p)); }

        // Left and right choose the week before or after, the chart scrolling to keep it in sight.
        bool LimitsKey(KeyEventArgs e)
        {
            if (!OnLimitsTab || history.Weeks.Count == 0 || (e.KeyCode != Keys.Left && e.KeyCode != Keys.Right)) return false;
            int i = history.Weeks.IndexOf(Chosen());
            i = Math.Max(0, Math.Min(history.Weeks.Count - 1, i + (e.KeyCode == Keys.Left ? -1 : 1)));
            chosenWeek = history.Weeks[i].Start;
            if (i < weekStrip.First) weekStrip.Back += weekStrip.First - i;
            else if (i >= weekStrip.First + weekStrip.Visible) weekStrip.Back -= i - (weekStrip.First + weekStrip.Visible) + 1;
            weekStrip.Clamp();
            Invalidate();
            return true;
        }
    }
}
