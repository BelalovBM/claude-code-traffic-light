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
        // From 90% on, where a point decides whether the work can go on, much closer: a rise of two points (one for the
        // week) and an exact figure no older than a quarter of an hour.
        internal const double NearEnd = 90, NearFiveRise = 2, NearWeekRise = 1;
        // While sessions work, an exact figure at least this often even when the estimate hardly moves.
        internal static readonly TimeSpan Stale = TimeSpan.FromMinutes(60), NearStale = TimeSpan.FromMinutes(15);

        // The brake, whatever the reason above says: never sooner than five minutes after the last run, twice as long
        // after each failure in a row up to an hour, and at most a hundred runs a day. Claude Code itself asks Anthropic
        // at most about once a minute, so a run sooner would only repeat the same figures anyway.
        internal static readonly TimeSpan MinGap = TimeSpan.FromMinutes(5), MaxGap = TimeSpan.FromMinutes(60);
        internal const int PerDay = 100;

        // Why an exact figure is worth asking for now, or null. Only while some session did something since the last
        // run: nothing is asked while Claude is idle.
        internal static string Reason(LimitView five, LimitView week, DateTime lastRun, DateTime lastActivity, DateTime nowUtc)
        {
            if (lastActivity <= lastRun) return null;
            string r = Reason(five, FiveRise, NearFiveRise, nowUtc);
            return r ?? Reason(week, WeekRise, NearWeekRise, nowUtc);
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

        // Runs /usage once (on a worker thread: it takes a few seconds) and says in the log how it went. Null when the
        // figures came, otherwise why not, in words for the user.
        internal static string Run(string chosen)
        {
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
                    Match m = Regex.Match(text, @"Current session:\s*(\d+)%");
                    // Printed figures count only once Claude Code saved them where the program reads them: otherwise the
                    // reason to ask would stay and the next run would come as soon as the brake allows.
                    long after = FetchedAt();
                    bool saved = after != before || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - after < 2 * 60 * 1000;
                    if (m.Success && saved)
                    {
                        Log.Write("Usage limits: /usage in the background, " + m.Groups[1].Value + "% in " + watch.Elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s");
                        return null;
                    }
                    if (m.Success)
                    {
                        Log.Write("Usage limits: /usage in the background printed figures but ~/.claude.json did not change");
                        return Loc.T("limits.auto.notsaved");
                    }
                    string first = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "(no output)";
                    if (first.Length > 160) first = first.Substring(0, 160);
                    Log.Write("Usage limits: /usage in the background gave no figures (exit " + p.ExitCode + "): " + first);
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
