using System;
using System.Collections.Generic;
using System.Linq;

namespace Semaphore
{
    // One reply of Claude as a transcript recorded it: the tokens of each kind, in which session, when.
    sealed class UsageStep
    {
        public DateTime At;        // UTC
        public string Model;
        public string Session;     // the session id; a subagent's steps belong to the session that started it
        public string Path;        // the session's own transcript (for its title)
        public bool Sidechain;     // a subagent's step
        public long Input, Write5m, Write1h, Read, Output, Searches;
        public bool Fast;

        public long Write { get { return Write5m + Write1h; } }
        public long Tokens { get { return Input + Write + Read + Output; } }
        // What the model had in front of it at this step.
        public long Context { get { return Input + Write + Read; } }
        public double Cost { get { return UsageMath.Cost(Input, Write, Read, Output); } }
        public double? Usd { get { return ApiPrices.Usd(Model, Input, Write5m, Write1h, Read, Output, Searches, Fast); } }
    }

    sealed class UsageCompaction
    {
        public DateTime At;
        public string Session;
    }

    // A figure in both units: dollars at API prices, and the share of the weekly limit (1 = a whole week; null when
    // the limit is unknown: a sign-in by API key, or no exact figures yet).
    sealed class UsageAmount
    {
        public double Usd;
        public double? Weeks;
        public long Tokens;
        public int Count;

        internal void Add(double usd, double? weeks, long tokens)
        {
            Usd += usd;
            if (weeks.HasValue) Weeks = (Weeks ?? 0) + weeks.Value;
            Tokens += tokens;
            Count++;
        }
    }

    sealed class UsageSessionRow
    {
        public string Session, Path, Model;
        public int Steps;
        public long MaxContext;
        public UsageAmount Amount = new UsageAmount();
    }

    sealed class UsageReport
    {
        public UsageAmount Total = new UsageAmount();
        // What the use went on: reading the context from the cache, writing it there (with the little plain input),
        // and the replies with their thinking.
        public UsageAmount Reading = new UsageAmount(), Writing = new UsageAmount(), Replies = new UsageAmount();
        // What made it dearer: steps with a large context, the context written again after a pause longer than the
        // cache lasts (the write part of those steps), and the compactions.
        public UsageAmount LargeContext = new UsageAmount(), ColdStarts = new UsageAmount();
        public int Compactions;
        public List<UsageSessionRow> Sessions = new List<UsageSessionRow>();
        public List<KeyValuePair<DateTime, UsageAmount>> Days = new List<KeyValuePair<DateTime, UsageAmount>>();
        public List<string> Unpriced = new List<string>();
        public bool LimitKnown;
    }

    // The statistics of a period, apart from files and windows, so that they can be tested.
    static class UsageStats
    {
        // A context from which every step is dear: each one reads all of it again.
        internal const long LargeContext = 600000;
        // A write after a pause counts as the context written again only when it is this large: smaller writes are
        // the new part of a step, which is written in any case.
        internal const long ColdWrite = 50000;

        internal static UsageReport Build(IList<UsageStep> all, IList<UsageCompaction> compactions, DateTime fromUtc, DateTime toUtc, UsageMath.Weights weights)
        {
            var r = new UsageReport { LimitKnown = weights != null };
            // The pause before a step is measured in its own line of work: the session, or one of its subagents.
            var previous = new Dictionary<string, UsageStep>();
            var sessions = new Dictionary<string, UsageSessionRow>();
            var modelUse = new Dictionary<string, Dictionary<string, double>>();
            var days = new SortedDictionary<DateTime, UsageAmount>();
            foreach (UsageStep s in all.OrderBy(x => x.At))
            {
                string line = s.Session + (s.Sidechain ? "|side" : "");
                UsageStep before;
                previous.TryGetValue(line, out before);
                previous[line] = s;
                if (s.At < fromUtc || s.At >= toUtc) continue;

                double? usd = s.Usd;
                if (!usd.HasValue && !r.Unpriced.Contains(s.Model)) r.Unpriced.Add(s.Model);
                double? weeks = Weeks(weights, s.Model, s.Cost);
                r.Total.Add(usd ?? 0, weeks, s.Tokens);

                // The parts, each priced as the whole step is.
                double readUsd = 0, writeUsd = 0, replyUsd = 0;
                if (usd.HasValue)
                {
                    readUsd = ApiPrices.Usd(s.Model, 0, 0, 0, s.Read, 0, 0, s.Fast).Value;
                    writeUsd = ApiPrices.Usd(s.Model, s.Input, s.Write5m, s.Write1h, 0, 0, 0, s.Fast).Value;
                    replyUsd = usd.Value - readUsd - writeUsd;
                }
                r.Reading.Add(readUsd, Weeks(weights, s.Model, UsageMath.Cost(0, 0, s.Read, 0)), s.Read);
                r.Writing.Add(writeUsd, Weeks(weights, s.Model, UsageMath.Cost(s.Input, s.Write, 0, 0)), s.Input + s.Write);
                r.Replies.Add(replyUsd, Weeks(weights, s.Model, UsageMath.Cost(0, 0, 0, s.Output)), s.Output);

                if (s.Context > LargeContext) r.LargeContext.Add(usd ?? 0, weeks, s.Tokens);
                // The cache lasts an hour when it was written for an hour (Claude Code on a subscription), else five
                // minutes; a step after a longer pause writes the whole context again.
                TimeSpan lasts = s.Write1h > 0 ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(5);
                if (before != null && s.Write >= ColdWrite && s.At - before.At > lasts)
                    r.ColdStarts.Add(writeUsd, Weeks(weights, s.Model, UsageMath.Cost(0, s.Write, 0, 0)), s.Write);

                UsageSessionRow row;
                if (!sessions.TryGetValue(s.Session, out row)) sessions[s.Session] = row = new UsageSessionRow { Session = s.Session };
                if (!s.Sidechain || row.Path == null) row.Path = s.Path;
                row.Steps++;
                row.MaxContext = Math.Max(row.MaxContext, s.Context);
                row.Amount.Add(usd ?? 0, weeks, s.Tokens);
                Dictionary<string, double> byModel;
                if (!modelUse.TryGetValue(s.Session, out byModel)) modelUse[s.Session] = byModel = new Dictionary<string, double>();
                double m;
                byModel.TryGetValue(s.Model, out m);
                byModel[s.Model] = m + s.Cost;

                DateTime day = s.At.ToLocalTime().Date;
                UsageAmount d;
                if (!days.TryGetValue(day, out d)) days[day] = d = new UsageAmount();
                d.Add(usd ?? 0, weeks, s.Tokens);
            }
            r.Compactions = compactions.Count(c => c.At >= fromUtc && c.At < toUtc);
            foreach (UsageSessionRow row in sessions.Values)
                row.Model = modelUse[row.Session].OrderByDescending(kv => kv.Value).First().Key;
            r.Sessions = sessions.Values.OrderByDescending(x => r.LimitKnown ? x.Amount.Weeks ?? 0 : x.Amount.Usd).ThenByDescending(x => x.Amount.Usd).ToList();
            r.Days = days.ToList();
            return r;
        }

        // The share of the weekly limit a cost makes: the weights give percent of the week per unit of cost.
        static double? Weeks(UsageMath.Weights w, string model, double cost)
        {
            return w == null ? (double?)null : w.For(model) * cost / 100;
        }
    }
}
