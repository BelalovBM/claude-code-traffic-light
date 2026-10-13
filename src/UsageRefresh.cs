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
        // Claude Code itself asks Anthropic at most about once a minute and answers from what it kept in between, so a
        // shorter period would only repeat the same figures.
        internal const int MinMinutes = 2, MaxMinutes = 120;

        // Due when the last run is old enough and some session did something since: nothing is asked while idle.
        internal static bool Due(DateTime lastRun, DateTime lastActivity, DateTime now, int minutes)
        {
            return now - lastRun >= TimeSpan.FromMinutes(minutes) && lastActivity > lastRun;
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

        // Runs /usage once (on a worker thread: it takes a few seconds) and says in the log how it went. Null when the
        // figures came, otherwise why not, in words for the user.
        internal static string Run()
        {
            string claude = FindClaude();
            if (claude == null) { Log.Write("Usage limits: no Claude Code found to run /usage"); return Loc.T("limits.auto.noclaude"); }
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
                    if (m.Success)
                    {
                        Log.Write("Usage limits: /usage in the background, " + m.Groups[1].Value + "% in " + watch.Elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s");
                        return null;
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
