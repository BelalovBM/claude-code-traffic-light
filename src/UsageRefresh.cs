using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Semaphore
{
    // The exact usage limits without the user typing /usage: Claude Code's own command, run hidden in its print mode.
    // Claude Code asks Anthropic with its own sign-in (the program never sees it) and keeps the figures in
    // ~/.claude.json, where UsageTracker reads them as it reads those of a /usage typed by hand. No model is asked, so
    // no limit is used; the run is not saved as a session and runs no hooks, so it shows nowhere.
    static class UsageRefresh
    {
        // The estimate between two exact figures stayed within a point while it rose by less than about twenty points
        // (two or three percent of the rise); asking at half that keeps it there with room to spare. The week fills
        // about seven times slower, so its rise is measured in smaller steps.
        internal const double FiveRise = 10, WeekRise = 5;
        // The thresholds above hold while the estimate misses by about this share of what it added (two or three
        // percent, as it did on the figures so far). When it misses by more (a model, an effort or a size of context
        // the weights were not learnt on), the threshold comes down so that the expected miss stays half a point:
        // a tenth off gives 5, a fifth 2.5, and never below NearFiveRise (NearWeekRise for the week).
        internal const double UsualMiss = 0.03, AllowedMiss = 0.5;
        // From 90% on, where a point decides whether the work can go on, much closer: a rise of two points (one for the
        // week) and an exact figure no older than a quarter of an hour.
        internal const double NearEnd = 90, NearFiveRise = 2, NearWeekRise = 1;
        // While sessions work, an exact figure at least this often even when the estimate hardly moves.
        internal static readonly TimeSpan Stale = TimeSpan.FromMinutes(60), NearStale = TimeSpan.FromMinutes(15);

        // The brake, whatever the reason above says: never sooner than five minutes after the last run, twice as long
        // after each failure in a row up to an hour, and at most a hundred runs a day. Claude Code itself asks Anthropic
        // at most about once a minute, so a run sooner would only repeat the same figures anyway.
        internal static readonly TimeSpan MinGap = TimeSpan.FromMinutes(5), MaxGap = TimeSpan.FromMinutes(60);
        // A last line, above what the five minutes can let through in a day (288): reached only if the brake itself
        // failed (a clock that jumps), never by a long day of work.
        internal const int PerDay = 300;

        // Why an exact figure is worth asking for now, or null. Only while some session did something since the last
        // run: nothing is asked while Claude is idle.
        internal static string Reason(LimitView five, LimitView week, DateTime lastRun, DateTime lastActivity, DateTime nowUtc)
        {
            return Reason(five, week, UsualMiss, UsualMiss, lastRun, lastActivity, nowUtc);
        }

        internal static string Reason(LimitView five, LimitView week, double fiveMiss, double weekMiss, DateTime lastRun, DateTime lastActivity, DateTime nowUtc)
        {
            if (lastActivity <= lastRun) return null;
            string r = Reason(five, Rise(FiveRise, NearFiveRise, fiveMiss), NearFiveRise, nowUtc);
            return r ?? Reason(week, Rise(WeekRise, NearWeekRise, weekMiss), NearWeekRise, nowUtc);
        }

        // The rise of the estimate at which an exact figure is asked for, by how much the estimate has been missing.
        internal static double Rise(double usual, double least, double miss)
        {
            if (miss <= 0) return usual;
            return Math.Max(least, Math.Min(usual, AllowedMiss / miss));
        }

        // A new exact figure against the estimate shown just before it, in the same window: the miss as a share of
        // what the estimate had added, smoothed with the earlier ones so that one figure does not swing it, and so
        // that the threshold goes back up as the estimate gets right again. A small addition says nothing (the exact
        // figures are whole percents), nor does another window.
        internal static double Learn(double miss, LimitView shown, double exact, DateTime exactReset)
        {
            if (shown == null || !shown.Known || shown.Exact || !shown.HasExact || shown.NotStarted) return miss;
            if (shown.Reset == DateTime.MinValue || Math.Abs((shown.Reset - exactReset).TotalMinutes) >= 30) return miss;
            double added = shown.Percent - shown.ExactPercent;
            if (added < 3 || exact < shown.ExactPercent - 5) return miss;
            double now = Math.Abs(shown.Percent - exact) / added;
            return 0.5 * miss + 0.5 * Math.Min(1, now);
        }

        static string Reason(LimitView v, double rise, double nearRise, DateTime nowUtc)
        {
            if (v == null || v.NotStarted || v.Exact) return null;
            if (!v.HasExact) return "no exact figure in this window";
            if (!v.Known) return "nothing to weigh the use by";
            bool near = v.Percent >= NearEnd;
            double rose = v.Percent - v.ExactPercent;
            if (rose >= (near ? nearRise : rise)) return "the estimate rose " + rose.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " points" + (near ? " near the end" : "");
            if (v.NewModel) return "a model without figures of its own";
            if (nowUtc - v.ExactAt >= (near ? NearStale : Stale)) return "the exact figure is " + (int)(nowUtc - v.ExactAt).TotalMinutes + " minutes old";
            return null;
        }

        internal static TimeSpan Gap(int failures)
        {
            double minutes = MinGap.TotalMinutes * Math.Pow(2, Math.Max(0, Math.Min(failures, 10)));
            return TimeSpan.FromMinutes(Math.Min(MaxGap.TotalMinutes, minutes));
        }

        // A last run "in the future" (the clock was put back) counts as just now, so it cannot block, nor let runs
        // through, for ever.
        internal static bool Allowed(DateTime lastRunUtc, int failures, int runsToday, DateTime nowUtc)
        {
            if (runsToday >= PerDay) return false;
            if (lastRunUtc > nowUtc) lastRunUtc = nowUtc;
            return nowUtc - lastRunUtc >= Gap(failures);
        }

        // The file the user chose, when it exists; otherwise the one found by itself.
        internal static string FindClaude(string chosen)
        {
            if (!string.IsNullOrWhiteSpace(chosen))
            {
                try { if (File.Exists(chosen)) return chosen; }
                catch (ArgumentException) { }
                return null;
            }
            return FindClaude();
        }

        // Claude Code's own installer puts it in ~/.local/bin; otherwise one on the PATH, or the one inside the VS Code
        // extension. Null when there is none.
        internal static string FindClaude()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string local = Path.Combine(home, ".local", "bin", "claude.exe");
            if (File.Exists(local)) return local;
            // An npm install puts claude.cmd on the PATH instead of an .exe.
            foreach (string name in new[] { "claude.exe", "claude.cmd" })
                foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                {
                    try
                    {
                        string p = Path.Combine(dir.Trim().Trim('"'), name);
                        if (dir.Trim().Length > 0 && File.Exists(p)) return p;
                    }
                    catch (ArgumentException) { }
                }
            try
            {
                string ext = Path.Combine(home, ".vscode", "extensions");
                if (Directory.Exists(ext))
                    return Directory.GetDirectories(ext, "anthropic.claude-code-*")
                        .Select(d => Path.Combine(d, "resources", "native-binary", "claude.exe"))
                        .Where(File.Exists)
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault();
            }
            catch (Exception) { }
            return null;
        }

        // When Claude Code last saved the figures (its "fetchedAtMs"), 0 when unknown.
        static long FetchedAt()
        {
            try
            {
                string path = Path.Combine(Path.GetDirectoryName(SessionRegistry.ClaudeDir.TrimEnd('\\', '/')), ".claude.json");
                string text;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(fs, Encoding.UTF8))
                    text = r.ReadToEnd();
                int at = text.IndexOf("\"cachedUsageUtilization\"", StringComparison.Ordinal);
                if (at < 0) return 0;
                Match m = Regex.Match(text.Substring(at, Math.Min(400, text.Length - at)), "\"fetchedAtMs\"\\s*:\\s*(\\d+)");
                return m.Success ? long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
            }
            catch (Exception) { return 0; }
        }

        // The figures in what /usage printed: "5 hours 30%, week 23%", either alone, or null when there is none. Claude
        // Code leaves out a line whose limit Anthropic did not report (the 5-hour one before a window starts, say), and
        // prints only its header when no figures came at all.
        internal static string Figures(string text)
        {
            Match five = Regex.Match(text ?? "", @"Current session:\s*(\d+)%");
            Match week = Regex.Match(text ?? "", @"Current week \(all models\):\s*(\d+)%");
            if (!five.Success && !week.Success) return null;
            return (five.Success ? "5 hours " + five.Groups[1].Value + "%" : "") + (five.Success && week.Success ? ", " : "")
                + (week.Success ? "week " + week.Groups[1].Value + "%" : "");
        }

        // Only the header ("You are currently using your subscription..."): Claude Code ran and is signed in, but had no
        // figures from Anthropic this time. Not a breakdown: it is tried again later, and told only if it lasts.
        internal static bool OnlyHeader(string text)
        {
            return Figures(text) == null && (text ?? "").IndexOf("You are currently using", StringComparison.Ordinal) >= 0;
        }

        // Runs /usage once (on a worker thread: it takes a few seconds) and says in the log how it went. Null when the
        // figures came, otherwise why not, in words for the user; "passing" when Claude Code worked but had no figures.
        internal static string Run(string chosen, out bool passing)
        {
            passing = false;
            string claude = FindClaude(chosen);
            if (claude == null) { Log.Write("Usage limits: no Claude Code found to run /usage"); return Loc.T("limits.auto.noclaude"); }
            long before = FetchedAt();
            string args = "-p \"/usage\" --no-session-persistence --settings \"{\\\"disableAllHooks\\\":true}\"";
            bool cmd = claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
            var psi = new ProcessStartInfo(cmd ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : claude,
                cmd ? "/d /s /c \"\"" + claude + "\" " + args + "\"" : args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = AppPaths.DataDir,
            };
            var watch = Stopwatch.StartNew();
            try
            {
                using (Process p = Process.Start(psi))
                {
                    p.StandardInput.Close();
                    var output = p.StandardOutput.ReadToEndAsync();
                    var errors = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(60000))
                    {
                        try { p.Kill(); } catch { }
                        Log.Write("Usage limits: /usage in the background took over a minute and was stopped");
                        return Loc.T("limits.auto.timeout");
                    }
                    string text = output.Result + "\n" + errors.Result;
                    string figures = Figures(text);
                    // Printed figures count only once Claude Code saved them where the program reads them: otherwise the
                    // reason to ask would stay and the next run would come as soon as the brake allows.
                    long after = FetchedAt();
                    bool saved = after != before || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - after < 2 * 60 * 1000;
                    if (figures != null && saved)
                    {
                        Log.Write("Usage limits: /usage in the background, " + figures + " in " + watch.Elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s");
                        return null;
                    }
                    if (figures != null)
                    {
                        Log.Write("Usage limits: /usage in the background printed figures but ~/.claude.json did not change");
                        return Loc.T("limits.auto.notsaved");
                    }
                    // The whole answer goes to the log, so that the reason can be seen; the user gets its first line.
                    string[] lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
                    string all = string.Join(" | ", lines);
                    if (all.Length > 600) all = all.Substring(0, 600) + "...";
                    passing = OnlyHeader(text);
                    Log.Write("Usage limits: /usage in the background gave no figures (exit " + p.ExitCode + (passing ? ", only the header" : "") + "): " + (all.Length > 0 ? all : "(no output)"));
                    string first = lines.Length > 0 ? lines[0] : "(no output)";
                    if (first.Length > 160) first = first.Substring(0, 160);
                    return Loc.T("limits.auto.nofigures", first);
                }
            }
            catch (Exception ex)
            {
                Log.Write("Usage limits: /usage in the background failed: " + ex.Message);
                return ex.Message;
            }
        }
    }
}
