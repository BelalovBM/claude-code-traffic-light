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

    sealed class LimitHistoryReport
    {
        public List<LimitWindow> Fives = new List<LimitWindow>(), Weeks = new List<LimitWindow>();
        // The share of the 5-hour use of the last 30 days in each local hour of the day.
        public double[] Hours = new double[24];
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
                r.Fives.Add(Window(c.Key, end, c.Value, a => a.Five.Value, ordered, fiveWeights, now, keptFrom));
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

            DateTime from = now.AddDays(-30);
            double total = 0;
            foreach (UsageStep s in ordered)
            {
                if (s.At < from) continue;
                double v = fiveWeights != null ? fiveWeights.For(s.Model) * s.Cost : s.Cost;
                r.Hours[s.At.ToLocalTime().Hour] += v;
                total += v;
            }
            if (total > 0) for (int h = 0; h < 24; h++) r.Hours[h] /= total;
            return r;
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

        // One point per quarter of an hour (the last of it), the first and the last kept.
        static List<KeyValuePair<DateTime, double>> Thin(List<KeyValuePair<DateTime, double>> points)
        {
            var thin = new List<KeyValuePair<DateTime, double>>();
            long slot = long.MinValue;
            foreach (var p in points)
            {
                long s = p.Key.Ticks / TimeSpan.FromMinutes(15).Ticks;
                if (s == slot && thin.Count > 1) thin[thin.Count - 1] = p;
                else thin.Add(p);
                slot = s;
            }
            return thin;
        }
    }
}
