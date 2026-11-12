using System;
using System.Collections.Generic;
using System.Linq;

namespace Semaphore
{
    // One 5-hour window or one week, as far as it can be told afterwards.
    sealed class LimitWindow
    {
        public DateTime Start, End;    // UTC
        public double Peak;            // percent of the limit
        public double Last;            // percent at the end, or now for the current one
        // Part of the figure is the estimate from the transcripts: use after the last exact figure, or no exact figure.
        public bool Estimated;
        public bool Current;
        // When it reached 100%; null when it did not.
        public DateTime? OutAt;
        // When Anthropic reset it before its time (the use fell between two figures); null when it did not.
        public DateTime? EarlyReset;
        // How the percentage grew: exact figures and the estimate between them, at most a point a quarter of an hour.
        public List<KeyValuePair<DateTime, double>> Curve = new List<KeyValuePair<DateTime, double>>();
    }

    // A finished window as limit-history.json keeps it: the transcripts it was estimated from are deleted by Claude
    // Code after a month and the exact figures are kept 90 days, the summary stays. Times in ms since 1970 (UTC).
    sealed class SavedLimitWindow
    {
        public long StartMs { get; set; }
        public long EndMs { get; set; }
        public double Peak { get; set; }
        public double Last { get; set; }
        public bool Estimated { get; set; }
        public long OutAtMs { get; set; }       // 0: it did not run out
        public long EarlyResetMs { get; set; }  // 0: it was not reset early
        // The curve of a week, a point an hour; none for a 5-hour window.
        public List<long> CurveMs { get; set; }
        public List<double> CurveValues { get; set; }
    }

    sealed class SavedLimitHistory
    {
        public List<SavedLimitWindow> Fives { get; set; }
        public List<SavedLimitWindow> Weeks { get; set; }
    }

    sealed class LimitHistoryReport
    {
        public List<LimitWindow> Fives = new List<LimitWindow>(), Weeks = new List<LimitWindow>();
        // The use of each local day of the transcripts in each hour, weighed as the 5-hour limit counts it.
        public Dictionary<DateTime, double[]> Hours = new Dictionary<DateTime, double[]>();
    }

    // The history of both limits, from the exact figures kept (usage-limits.json) and the steps of the transcripts.
    // A window with exact figures has them as its points, the use between them and after the last one estimated with
    // the weights the figures taught; a window without any (no /usage was run in it) is estimated as a whole.
    static class LimitHistory
    {
        internal static readonly TimeSpan Five = TimeSpan.FromHours(5), Week = TimeSpan.FromDays(7);
        // Figures whose reset times are this close belong to the same window (Claude Code rounds them differently).
        static readonly TimeSpan Same = TimeSpan.FromMinutes(30);

        internal static LimitHistoryReport Build(IList<UsageAnchor> anchors, IList<UsageStep> steps,
            UsageMath.Weights fiveWeights, UsageMath.Weights weekWeights, DateTime now, DateTime keptFrom)
        {
            var r = new LimitHistoryReport();
            var ordered = steps.Where(s => s.At <= now).OrderBy(s => s.At).ToList();
            var all = anchors.OrderBy(a => a.AtMs).ToList();

            // The 5-hour windows: those the figures name, then those opened by a message outside any of them.
            var fives = Clusters(all.Where(a => a.Five.HasValue), a => a.FiveReset)
                .Select(c => new KeyValuePair<DateTime, List<UsageAnchor>>(c.Key - Five, c.Value)).ToList();
            var bounds = fives.Select(c => new KeyValuePair<DateTime, DateTime>(c.Key, c.Key + Five)).ToList();
            if (fiveWeights != null)
                foreach (UsageStep s in ordered)
                {
                    if (bounds.Any(b => s.At >= b.Key && s.At < b.Value)) continue;
                    DateTime end = s.At + Five;
                    // Windows do not overlap: one that a figure names later starts where this one has to end.
                    foreach (var b in bounds) if (b.Key > s.At && b.Key < end) end = b.Key;
                    bounds.Add(new KeyValuePair<DateTime, DateTime>(s.At, end));
                    fives.Add(new KeyValuePair<DateTime, List<UsageAnchor>>(s.At, new List<UsageAnchor>()));
                }
            foreach (var c in fives)
            {
                DateTime end = bounds.First(b => b.Key == c.Key).Value;
                // The figures named a reset after the next window began: Anthropic reset this one early, and it ended
                // where the next one began (a limit run out in it came back then, not at the reset time).
                DateTime next = bounds.Where(b => b.Key > c.Key).Select(b => b.Key).DefaultIfEmpty(DateTime.MaxValue).Min();
                bool early = c.Value.Count > 0 && next < end;
                if (early) end = next;
                LimitWindow w = Window(c.Key, end, c.Value, a => a.Five.Value, ordered, fiveWeights, now, keptFrom);
                if (early && w.EarlyReset == null) w.EarlyReset = end;
                r.Fives.Add(w);
            }
            r.Fives = r.Fives.OrderBy(w => w.Start).ToList();

            // The weeks: those the figures name, and between, before and after them every seven days from a known reset.
            var weeks = Clusters(all.Where(a => a.Week.HasValue), a => a.WeekReset).ToList();
            var ends = weeks.Select(c => c.Key).ToList();
            if (ends.Count > 0 && weekWeights != null)
            {
                var more = new List<DateTime>();
                for (int i = 0; i + 1 < ends.Count; i++)
                    for (DateTime e = ends[i] + Week; e < ends[i + 1] - Same; e += Week) more.Add(e);
                DateTime earliest = ordered.Count > 0 ? ordered[0].At : now;
                // Only whole weeks: one that began before the transcripts kept here would show too little.
                for (DateTime e = ends[0] - Week; e > earliest && e - Week >= keptFrom; e -= Week) more.Add(e);
                for (DateTime e = ends[ends.Count - 1] + Week; e - Week < now; e += Week) more.Add(e);
                weeks.AddRange(more.Select(e => new KeyValuePair<DateTime, List<UsageAnchor>>(e, new List<UsageAnchor>())));
            }
            foreach (var c in weeks)
            {
                LimitWindow w = Window(c.Key - Week, c.Key, c.Value, a => a.Week.Value, ordered, weekWeights, now, keptFrom);
                // A week with no figure and no use is not worth a row.
                if (c.Value.Count > 0 || w.Peak > 0 || w.Current) r.Weeks.Add(w);
            }
            r.Weeks = r.Weeks.OrderBy(w => w.Start).ToList();

            foreach (UsageStep s in ordered)
            {
                DateTime local = s.At.ToLocalTime();
                double[] day;
                if (!r.Hours.TryGetValue(local.Date, out day)) r.Hours[local.Date] = day = new double[24];
                day[local.Hour] += fiveWeights != null ? fiveWeights.For(s.Model) * s.Cost : s.Cost;
            }
            return r;
        }

        // The days the chart of the 5-hour windows shows, oldest first: each local day a used window touches (one that
        // runs past midnight, both), and today; the days between with no window are left out. While there are fewer
        // than "least", the days before the first are added, so the chart is as wide as before the history began.
        internal static List<DateTime> UsedDays(IEnumerable<LimitWindow> fives, DateTime today, DateTime now, int least)
        {
            var set = new HashSet<DateTime> { today.Date };
            foreach (LimitWindow w in fives)
            {
                if (w.Peak <= 0) continue;
                DateTime end = (w.End < now ? w.End : now).AddTicks(-1);
                if (end < w.Start) end = w.Start;
                for (DateTime d = w.Start.ToLocalTime().Date; d <= end.ToLocalTime().Date && d <= today.Date; d = d.AddDays(1)) set.Add(d);
            }
            var days = set.OrderBy(d => d).ToList();
            while (days.Count < least) days.Insert(0, days[0].AddDays(-1));
            return days;
        }

        // The share of the use of the days from "first" to "last" (local) in each hour of the day; null when the
        // transcripts tell of no use in them.
        internal static double[] HourShares(LimitHistoryReport r, DateTime first, DateTime last)
        {
            var sum = new double[24];
            foreach (var d in r.Hours)
                if (d.Key >= first && d.Key <= last)
                    for (int h = 0; h < 24; h++) sum[h] += d.Value[h];
            double total = sum.Sum();
            if (total <= 0) return null;
            for (int h = 0; h < 24; h++) sum[h] /= total;
            return sum;
        }

        // The figures grouped by the reset they name, each group under the reset of its last figure.
        static List<KeyValuePair<DateTime, List<UsageAnchor>>> Clusters(IEnumerable<UsageAnchor> figures, Func<UsageAnchor, DateTime> reset)
        {
            var groups = new List<KeyValuePair<DateTime, List<UsageAnchor>>>();
            foreach (UsageAnchor a in figures)
            {
                DateTime at = reset(a);
                int i = groups.FindIndex(g => (g.Key - at).Duration() < Same);
                if (i < 0) groups.Add(new KeyValuePair<DateTime, List<UsageAnchor>>(at, new List<UsageAnchor> { a }));
                else
                {
                    groups[i].Value.Add(a);
                    groups[i] = new KeyValuePair<DateTime, List<UsageAnchor>>(at, groups[i].Value);
                }
            }
            return groups.OrderBy(g => g.Key).ToList();
        }

        static LimitWindow Window(DateTime start, DateTime end, List<UsageAnchor> figures, Func<UsageAnchor, double> percent,
            List<UsageStep> steps, UsageMath.Weights weights, DateTime now, DateTime keptFrom)
        {
            var w = new LimitWindow { Start = start, End = end, Current = start <= now && now < end };
            DateTime upTo = end < now ? end : now;
            var points = new List<KeyValuePair<DateTime, double>> { new KeyValuePair<DateTime, double>(start, 0) };
            var exact = new List<bool> { false };
            double value = 0, sinceFigure = 0;
            int f = 0;
            var inWindow = figures.Where(a => a.At >= start && a.At <= end).OrderBy(a => a.AtMs).ToList();
            // The figures after which the use fell: Anthropic reset the limit early, the reset time staying the same.
            var falls = new HashSet<int>();
            double lastFigure = -1;
            Action figure = () =>
            {
                value = percent(inWindow[f]);
                sinceFigure = 0;
                if (lastFigure >= 0 && value < lastFigure - 5)
                {
                    falls.Add(points.Count);
                    if (w.EarlyReset == null) w.EarlyReset = inWindow[f].At;
                }
                lastFigure = value;
                points.Add(new KeyValuePair<DateTime, double>(inWindow[f].At, value));
                exact.Add(true);
            };
            foreach (UsageStep s in steps)
            {
                if (s.At < start) continue;
                if (s.At >= upTo) break;
                for (; f < inWindow.Count && inWindow[f].At <= s.At; f++) figure();
                if (weights == null) continue;
                double add = weights.For(s.Model) * s.Cost;
                value = Math.Min(100, value + add);
                sinceFigure += add;
                points.Add(new KeyValuePair<DateTime, double>(s.At, value));
                exact.Add(false);
            }
            for (; f < inWindow.Count; f++) figure();
            if (w.Current) { points.Add(new KeyValuePair<DateTime, double>(now, value)); exact.Add(false); }

            // The use only grows within a window: an estimate above the exact figure that followed it was too high, and
            // the real figure then was at most that one (so a run to 100% that the next figure denies did not happen).
            // A figure after an early reset says nothing of the use before it.
            double cap = double.MaxValue;
            for (int i = points.Count - 1; i >= 0; i--)
            {
                if (exact[i]) cap = falls.Contains(i) ? double.MaxValue : points[i].Value;
                else if (points[i].Value > cap) points[i] = new KeyValuePair<DateTime, double>(points[i].Key, cap);
            }

            w.Peak = points.Max(p => p.Value);
            w.Last = value;
            foreach (var p in points) if (p.Value >= 99.5) { w.OutAt = p.Key; break; }
            // Estimated: no figure at all, use added after the last one, or a window that began before the transcripts
            // kept here (its use after the last figure is not known).
            w.Estimated = inWindow.Count == 0 || sinceFigure >= 0.5 || (start < keptFrom && inWindow[inWindow.Count - 1].At < upTo);
            w.Curve = Thin(points);
            return w;
        }

        // ---- what is kept of finished windows ----

        // The windows computed now joined with those kept: a finished window is kept (and its kept copy replaced) when
        // the transcripts still cover all of it; one that began before them is shown as it was kept, if it was. Kept
        // windows the figures and transcripts no longer tell of stay as they are. The current window is not kept until
        // it ends. Returns whether the kept ones changed.
        internal static bool Merge(List<LimitWindow> computed, List<LimitWindow> kept, DateTime now, DateTime keptFrom, out List<LimitWindow> merged)
        {
            bool changed = false;
            merged = new List<LimitWindow>();
            var matched = new HashSet<int>();
            foreach (LimitWindow c in computed)
            {
                int i = kept.FindIndex(k => (k.Start - c.Start).Duration() < Same || (k.End - c.End).Duration() < Same);
                if (i >= 0) matched.Add(i);
                if (c.Current || c.End > now) { merged.Add(c); continue; }
                if (i < 0)
                {
                    matched.Add(kept.Count);
                    kept.Add(c);
                    changed = true;
                    merged.Add(c);
                }
                else if (c.Start >= keptFrom)
                {
                    if (!SameWindow(kept[i], c)) { kept[i] = c; changed = true; }
                    merged.Add(c);
                }
                else merged.Add(kept[i]);
            }
            for (int i = 0; i < kept.Count; i++) if (!matched.Contains(i)) merged.Add(kept[i]);
            merged = merged.OrderBy(w => w.Start).ToList();
            return changed;
        }

        static bool SameWindow(LimitWindow a, LimitWindow b)
        {
            return a.Start == b.Start && a.End == b.End && Math.Abs(a.Peak - b.Peak) < 0.05 && Math.Abs(a.Last - b.Last) < 0.05
                && a.Estimated == b.Estimated && a.OutAt == b.OutAt && a.EarlyReset == b.EarlyReset;
        }

        internal static SavedLimitWindow ToSaved(LimitWindow w, bool curve)
        {
            var s = new SavedLimitWindow
            {
                StartMs = UsageAnchor.ToMs(w.Start), EndMs = UsageAnchor.ToMs(w.End), Peak = Math.Round(w.Peak, 2), Last = Math.Round(w.Last, 2),
                Estimated = w.Estimated, OutAtMs = w.OutAt.HasValue ? UsageAnchor.ToMs(w.OutAt.Value) : 0,
                EarlyResetMs = w.EarlyReset.HasValue ? UsageAnchor.ToMs(w.EarlyReset.Value) : 0,
            };
            if (curve)
            {
                var points = Thin(w.Curve, TimeSpan.FromHours(1));
                s.CurveMs = points.Select(p => UsageAnchor.ToMs(p.Key)).ToList();
                s.CurveValues = points.Select(p => Math.Round(p.Value, 2)).ToList();
            }
            return s;
        }

        internal static LimitWindow FromSaved(SavedLimitWindow s)
        {
            var w = new LimitWindow
            {
                Start = UsageAnchor.Ms(s.StartMs), End = UsageAnchor.Ms(s.EndMs), Peak = s.Peak, Last = s.Last, Estimated = s.Estimated,
                OutAt = s.OutAtMs != 0 ? UsageAnchor.Ms(s.OutAtMs) : (DateTime?)null,
                EarlyReset = s.EarlyResetMs != 0 ? UsageAnchor.Ms(s.EarlyResetMs) : (DateTime?)null,
            };
            if (s.CurveMs != null && s.CurveValues != null)
                for (int i = 0; i < Math.Min(s.CurveMs.Count, s.CurveValues.Count); i++)
                    w.Curve.Add(new KeyValuePair<DateTime, double>(UsageAnchor.Ms(s.CurveMs[i]), s.CurveValues[i]));
            return w;
        }

        static List<KeyValuePair<DateTime, double>> Thin(List<KeyValuePair<DateTime, double>> points)
        {
            return Thin(points, TimeSpan.FromMinutes(15));
        }

        // One point per slot (the last of it), the first and the last kept.
        static List<KeyValuePair<DateTime, double>> Thin(List<KeyValuePair<DateTime, double>> points, TimeSpan every)
        {
            var thin = new List<KeyValuePair<DateTime, double>>();
            long slot = long.MinValue;
            foreach (var p in points)
            {
                long s = p.Key.Ticks / every.Ticks;
                if (s == slot && thin.Count > 1) thin[thin.Count - 1] = p;
                else thin.Add(p);
                slot = s;
            }
            return thin;
        }
    }
}
