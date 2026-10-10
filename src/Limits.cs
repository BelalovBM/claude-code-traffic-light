using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace Semaphore
{
    // Claude's usage limits: the 5-hour window and the week, in percent, as /usage shows them.
    //
    // Claude Code gives these figures to no hook, and its status line (which gets them) does not run in VS Code. What it
    // does is keep the last figures it fetched (on /usage, or at the limit) in ~/.claude.json. Those exact figures are
    // the anchors. Between them the program counts what the transcripts say was used since, weighted per model by
    // what the anchors so far showed, and adds it on top: an estimate, marked with "≈". Each new /usage corrects it.
    // The program never asks Anthropic for anything and never touches the sign-in of Claude Code.

    // An exact figure from Claude Code, with what the transcripts showed was used in each window up to that moment.
    sealed class UsageAnchor
    {
        public long AtMs { get; set; }
        public double? Five { get; set; }
        public long FiveResetMs { get; set; }
        public double? Week { get; set; }
        public long WeekResetMs { get; set; }
        public Dictionary<string, double> FiveCost { get; set; }
        public Dictionary<string, double> WeekCost { get; set; }

        [ScriptIgnore] public DateTime At { get { return Ms(AtMs); } }
        [ScriptIgnore] public DateTime FiveReset { get { return Ms(FiveResetMs); } }
        [ScriptIgnore] public DateTime WeekReset { get { return Ms(WeekResetMs); } }

        internal static DateTime Ms(long ms) { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms); }
        internal static long ToMs(DateTime utc) { return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }
    }

    // One limit as it stands now.
    sealed class LimitView
    {
        public bool Known;          // a percentage can be given
        public double Percent;
        public bool Exact;          // the figure is Claude Code's own, nothing was used since
        public bool NotStarted;     // the 5-hour window begins with the next message
        public DateTime Reset;      // UTC; MinValue when unknown
        public bool ResetApprox;
        public double ExactPercent; // the last exact figure in this window, and when it was taken
        public DateTime ExactAt;
        public bool HasExact;
    }

    sealed class LimitsSnapshot
    {
        public LimitView Five, Week;
        public bool AnyAnchor;
    }

    // The arithmetic, apart from files, so that it can be tested.
    static class UsageMath
    {
        // Tokens weighted roughly as they are priced: reading from the cache is cheap, writing to it a little dearer than
        // plain input, the reply dearest. Across two different weeks this followed the official weekly figure to about 1%.
        internal static double Cost(long input, long cacheWrite, long cacheRead, long output)
        {
            return input + 1.25 * cacheWrite + 0.1 * cacheRead + 5.0 * output;
        }

        internal sealed class Sample
        {
            public Dictionary<string, double> Cost;
            public double Percent;
            public DateTime At;
        }

        internal sealed class Weights
        {
            public double Common;
            public Dictionary<string, double> ByModel = new Dictionary<string, double>();
            public double For(string model)
            {
                double w;
                return model != null && ByModel.TryGetValue(model, out w) ? w : Common;
            }
        }

        // Percent per unit of cost, one figure per model, fitted to the anchors (newer ones count more). A model with
        // little evidence stays near the common figure; a model never seen gets the common figure. Null without anchors.
        internal static Weights Fit(IList<Sample> samples, DateTime now)
        {
            var use = samples.Where(s => s.Cost != null && s.Percent >= 1 && s.Cost.Values.Sum() > 0).ToList();
            if (use.Count == 0) return null;
            int n = use.Count;
            var r = new double[n];
            var total = new double[n];
            for (int a = 0; a < n; a++)
            {
                double age = Math.Max(0, (now - use[a].At).TotalDays);
                r[a] = Math.Pow(0.5, age / 14.0) / Math.Pow(Math.Max(use[a].Percent, 5), 2);
                total[a] = use[a].Cost.Values.Sum();
            }
            double num = 0, den = 0;
            for (int a = 0; a < n; a++) { num += r[a] * use[a].Percent * total[a]; den += r[a] * total[a] * total[a]; }
            var w = new Weights { Common = num / den };
            var models = use.SelectMany(s => s.Cost.Keys).Distinct().ToList();
            foreach (string m in models) w.ByModel[m] = w.Common;
            // A pull towards the common figure, so that one anchor cannot swing a model far on its own.
            double lambda = 0.05 * den;
            for (int pass = 0; pass < 40; pass++)
            {
                foreach (string m in models)
                {
                    double nm = 0, dm = 0;
                    for (int a = 0; a < n; a++)
                    {
                        double x;
                        if (!use[a].Cost.TryGetValue(m, out x) || x <= 0) continue;
                        double others = 0;
                        foreach (var kv in use[a].Cost) if (kv.Key != m) others += w.For(kv.Key) * kv.Value;
                        nm += r[a] * x * (use[a].Percent - others);
                        dm += r[a] * x * x;
                    }
                    double v = (nm + lambda * w.Common) / (dm + lambda);
                    w.ByModel[m] = Math.Max(w.Common / 5, Math.Min(w.Common * 5, v));
                }
            }
            return w;
        }

        internal static double Apply(Weights w, Dictionary<string, double> cost)
        {
            double p = 0;
            foreach (var kv in cost) p += w.For(kv.Key) * kv.Value;
            return p;
        }
    }

    sealed class UsageTracker
    {
        struct Entry
        {
            public DateTime At;   // UTC
            public string Model;
            public double Cost;
        }

        const int KeepDays = 8;
        readonly object gate = new object();
        readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        readonly Dictionary<string, long> offsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        List<UsageAnchor> anchors = new List<UsageAnchor>();
        DateTime claudeJsonWritten;
        bool scanned;
        Timer timer;
        volatile LimitsSnapshot snapshot = new LimitsSnapshot { Five = new LimitView(), Week = new LimitView() };

        public LimitsSnapshot Snapshot { get { return snapshot; } }
        // A new exact figure arrived (on the timer thread).
        public event Action AnchorAdded;

        static string AnchorsPath { get { return Path.Combine(AppPaths.DataDir, "usage-limits.json"); } }
        static string ClaudeJson { get { return Path.Combine(Path.GetDirectoryName(SessionRegistry.ClaudeDir.TrimEnd('\\', '/')), ".claude.json"); } }

        public void Start()
        {
            LoadAnchors();
            timer = new Timer(_ => Update(), null, 1000, 30000);
        }

        public void Stop()
        {
            if (timer != null) timer.Dispose();
        }

        int busy;

        void Update()
        {
            // The first pass reads days of transcripts and can outlast the timer period.
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            try
            {
                ScanTranscripts();
                ReadClaudeJson();
                LimitsSnapshot s = Compute(DateTime.UtcNow);
                snapshot = s;
            }
            catch (Exception ex) { Log.Write("Usage limits: " + ex.Message); }
            finally { Interlocked.Exchange(ref busy, 0); }
        }

        // ---- the transcripts: what each reply used ----

        static readonly Regex ModelRx = new Regex("\"message\":\\{\"model\":\"([^\"]+)\",\"id\":\"([^\"]+)\"", RegexOptions.Compiled);
        static readonly Regex NumRx = new Regex("\"(input_tokens|cache_creation_input_tokens|cache_read_input_tokens|output_tokens)\":(\\d+)", RegexOptions.Compiled);
        static readonly Regex TimeRx = new Regex("\"timestamp\":\"([^\"]+)\"", RegexOptions.Compiled);
        static readonly Regex RequestRx = new Regex("\"requestId\":\"([^\"]+)\"", RegexOptions.Compiled);

        void ScanTranscripts()
        {
            string root = Path.Combine(SessionRegistry.ClaudeDir, "projects");
            if (!Directory.Exists(root)) { scanned = true; return; }
            DateTime since = DateTime.UtcNow.AddDays(-KeepDays);
            foreach (string path in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.LastWriteTimeUtc < since) continue;
                    long from;
                    offsets.TryGetValue(path, out from);
                    if (info.Length < from) from = 0;
                    if (info.Length == from) continue;
                    offsets[path] = ReadFrom(path, from);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            lock (gate)
            {
                foreach (string key in entries.Where(kv => kv.Value.At < since).Select(kv => kv.Key).ToList()) entries.Remove(key);
            }
            scanned = true;
        }

        // Reads whole lines from the offset on and returns where the next read starts (a line still being written
        // is left for the next time).
        long ReadFrom(string path, long from)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                fs.Seek(from, SeekOrigin.Begin);
                var buffer = new MemoryStream();
                long lineStart = from;
                int b;
                var chunk = new byte[1 << 16];
                int read;
                long pos = from;
                while ((read = fs.Read(chunk, 0, chunk.Length)) > 0)
                {
                    for (int i = 0; i < read; i++)
                    {
                        b = chunk[i];
                        pos++;
                        if (b == '\n')
                        {
                            Line(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
                            buffer.SetLength(0);
                            lineStart = pos;
                        }
                        else buffer.WriteByte((byte)b);
                    }
                }
                return lineStart;
            }
        }

        void Line(string line)
        {
            if (line.IndexOf("\"usage\"", StringComparison.Ordinal) < 0 || line.IndexOf("\"role\":\"assistant\"", StringComparison.Ordinal) < 0) return;
            Match model = ModelRx.Match(line);
            if (!model.Success) return;
            // The usage and the time come after the reply text, so the last match of each is the real one.
            long input = 0, write = 0, readCache = 0, output = 0;
            foreach (Match m in NumRx.Matches(line))
            {
                long v = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                switch (m.Groups[1].Value)
                {
                    case "input_tokens": input = v; break;
                    case "cache_creation_input_tokens": write = v; break;
                    case "cache_read_input_tokens": readCache = v; break;
                    default: output = v; break;
                }
            }
            MatchCollection times = TimeRx.Matches(line);
            if (times.Count == 0) return;
            DateTime at;
            if (!DateTime.TryParse(times[times.Count - 1].Groups[1].Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out at)) return;
            MatchCollection req = RequestRx.Matches(line);
            // A reply is written in several lines with the same message id; the last one counts.
            string key = model.Groups[2].Value + "|" + (req.Count > 0 ? req[req.Count - 1].Groups[1].Value : "");
            var e = new Entry { At = at, Model = model.Groups[1].Value, Cost = UsageMath.Cost(input, write, readCache, output) };
            lock (gate) entries[key] = e;
        }

        Dictionary<string, double> CostByModel(DateTime from, DateTime to)
        {
            var d = new Dictionary<string, double>();
            lock (gate)
                foreach (Entry e in entries.Values)
                    if (e.At >= from && e.At < to)
                    {
                        double v;
                        d.TryGetValue(e.Model, out v);
                        d[e.Model] = v + e.Cost;
                    }
            return d;
        }

        DateTime FirstUseAfter(DateTime t)
        {
            DateTime first = DateTime.MaxValue;
            lock (gate)
                foreach (Entry e in entries.Values)
                    if (e.At >= t && e.At < first) first = e.At;
            return first;
        }

        // ---- the exact figures ----

        void ReadClaudeJson()
        {
            string path = ClaudeJson;
            if (!scanned || !File.Exists(path)) return;
            DateTime written = File.GetLastWriteTimeUtc(path);
            if (written == claudeJsonWritten) return;
            claudeJsonWritten = written;
            string text;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs, Encoding.UTF8))
                text = r.ReadToEnd();
            UsageAnchor a = ParseCache(text);
            if (a == null) return;
            lock (gate) if (anchors.Any(x => x.AtMs == a.AtMs)) return;
            if (a.Five.HasValue && a.FiveReset > a.At) a.FiveCost = CostByModel(a.FiveReset.AddHours(-5), a.At);
            if (a.Week.HasValue && a.WeekReset > a.At) a.WeekCost = CostByModel(a.WeekReset.AddDays(-7), a.At);
            lock (gate)
            {
                anchors.Add(a);
                DateTime old = DateTime.UtcNow.AddDays(-90);
                anchors = anchors.Where(x => x.At >= old).OrderBy(x => x.AtMs).ToList();
                if (anchors.Count > 300) anchors.RemoveRange(0, anchors.Count - 300);
            }
            SaveAnchors();
            Log.Write("Usage limits: exact figures from Claude Code, 5 hours " + Fmt(a.Five) + "%, week " + Fmt(a.Week) + "%");
            var h = AnchorAdded;
            if (h != null) h();
        }

        static string Fmt(double? v) { return v.HasValue ? v.Value.ToString("0", CultureInfo.InvariantCulture) : "?"; }

        // The block "cachedUsageUtilization" of ~/.claude.json. Only that block is parsed: the file has keys that differ
        // only in letter case, which a parser of the whole file refuses.
        internal static UsageAnchor ParseCache(string text)
        {
            int key = text.IndexOf("\"cachedUsageUtilization\"", StringComparison.Ordinal);
            if (key < 0) return null;
            int start = text.IndexOf('{', key);
            if (start < 0) return null;
            int end = MatchingBrace(text, start);
            if (end < 0) return null;
            var d = Json.Parse(text.Substring(start, end - start + 1)) as IDictionary<string, object>;
            if (d == null) return null;
            object at;
            if (!d.TryGetValue("fetchedAtMs", out at) || at == null) return null;
            var u = d.ContainsKey("utilization") ? d["utilization"] as IDictionary<string, object> : null;
            if (u == null) return null;
            var a = new UsageAnchor { AtMs = Convert.ToInt64(at, CultureInfo.InvariantCulture) };
            long reset;
            a.Five = Window(u, "five_hour", out reset);
            a.FiveResetMs = reset;
            a.Week = Window(u, "seven_day", out reset);
            a.WeekResetMs = reset;
            return a.Five.HasValue || a.Week.HasValue ? a : null;
        }

        static double? Window(IDictionary<string, object> u, string name, out long resetMs)
        {
            resetMs = 0;
            var w = u.ContainsKey(name) ? u[name] as IDictionary<string, object> : null;
            if (w == null || !w.ContainsKey("utilization") || w["utilization"] == null) return null;
            DateTimeOffset reset;
            if (w.ContainsKey("resets_at") && w["resets_at"] is string
                && DateTimeOffset.TryParse((string)w["resets_at"], CultureInfo.InvariantCulture, DateTimeStyles.None, out reset))
                resetMs = reset.ToUnixTimeMilliseconds();
            if (resetMs == 0) return null;
            return Convert.ToDouble(w["utilization"], CultureInfo.InvariantCulture);
        }

        static int MatchingBrace(string s, int open)
        {
            int depth = 0;
            bool inString = false;
            for (int i = open; i < s.Length; i++)
            {
                char c = s[i];
                if (inString)
                {
                    if (c == '\\') i++;
                    else if (c == '"') inString = false;
                }
                else if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0) return i;
            }
            return -1;
        }

        void LoadAnchors()
        {
            try
            {
                if (!File.Exists(AnchorsPath)) return;
                var list = new JavaScriptSerializer().Deserialize<List<UsageAnchor>>(File.ReadAllText(AnchorsPath, Encoding.UTF8));
                if (list != null) lock (gate) anchors = list.OrderBy(x => x.AtMs).ToList();
            }
            catch (Exception ex) { Log.Write("usage-limits.json could not be read: " + ex.Message); }
        }

        void SaveAnchors()
        {
            if (AppPaths.Disabled) return;
            try
            {
                string json;
                lock (gate) json = new JavaScriptSerializer().Serialize(anchors);
                File.WriteAllText(AnchorsPath, json, new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Write("usage-limits.json could not be written: " + ex.Message); }
        }

        // ---- now ----

        LimitsSnapshot Compute(DateTime now)
        {
            List<UsageAnchor> list;
            lock (gate) list = anchors.ToList();
            var s = new LimitsSnapshot { AnyAnchor = list.Count > 0 };
            s.Five = View(list, now, true);
            s.Week = View(list, now, false);
            return s;
        }

        LimitView View(List<UsageAnchor> list, DateTime now, bool five)
        {
            var v = new LimitView { Reset = DateTime.MinValue };
            UsageAnchor a = list.Where(x => five ? x.Five.HasValue : x.Week.HasValue).OrderByDescending(x => x.AtMs).FirstOrDefault();
            if (a == null) return v;
            UsageMath.Weights w = UsageMath.Fit(list
                .Where(x => five ? x.Five.HasValue && x.FiveCost != null : x.Week.HasValue && x.WeekCost != null)
                .Select(x => new UsageMath.Sample { Cost = five ? x.FiveCost : x.WeekCost, Percent = five ? x.Five.Value : x.Week.Value, At = x.At })
                .ToList(), now);

            DateTime reset = five ? a.FiveReset : a.WeekReset, start;
            double basePercent = 0;
            DateTime from;
            if (five)
            {
                if (now < reset)
                {
                    start = reset.AddHours(-5);
                }
                else
                {
                    // A new window opens with the first message after the last one closed and lasts five hours.
                    DateTime t = reset;
                    while (true)
                    {
                        DateTime first = FirstUseAfter(t);
                        if (first == DateTime.MaxValue) { v.Known = true; v.NotStarted = true; v.Percent = 0; return v; }
                        if (first.AddHours(5) > now) { start = first; reset = first.AddHours(5); v.ResetApprox = true; break; }
                        t = first.AddHours(5);
                    }
                }
            }
            else
            {
                while (reset <= now) reset = reset.AddDays(7);
                start = reset.AddDays(-7);
            }
            v.Reset = reset;
            if (a.At >= start && a.At < reset)
            {
                basePercent = five ? a.Five.Value : a.Week.Value;
                from = a.At;
                v.HasExact = true;
                v.ExactPercent = basePercent;
                v.ExactAt = a.At;
            }
            else from = start;

            Dictionary<string, double> since = CostByModel(from, now);
            if (since.Count == 0 || since.Values.Sum() <= 0)
            {
                v.Known = true;
                v.Percent = basePercent;
                v.Exact = v.HasExact;
                return v;
            }
            if (w == null) return v;   // used since, but nothing to weigh it by yet
            v.Known = true;
            v.Percent = Math.Min(100, basePercent + UsageMath.Apply(w, since));
            return v;
        }
    }
}
