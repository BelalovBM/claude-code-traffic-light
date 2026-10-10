using System;
using System.IO;

namespace Semaphore
{
    // "Remove everything": the program needs no installer, so this undoes what it set up.
    // The .exe itself cannot delete itself while running; the user removes it afterwards.
    static class Uninstaller
    {
        // Returns null on success, or the reason it stopped. Nothing is deleted unless
        // the hooks could be taken out of Claude Code's settings first.
        public static string Run()
        {
            try { HookInstaller.Remove(); }
            catch (Exception ex)
            {
                Log.Write("Uninstall stopped: " + ex.Message);
                return ex.Message;
            }

            Native.SetAutostart(false);
            Toasts.Unregister();
            // The lamp pictures and the old settings folder are shared with the installed program; a test instance
            // removes only its own files.
            if (!AppPaths.IsSandbox)
            {
                try { Directory.Delete(Path.Combine(Path.GetTempPath(), "ClaudeCodeTrafficLight"), true); }
                catch { }
            }

            // The settings folder is the one that holds the .exe, so only this program's own files go.
            string dir = AppPaths.DataDir;
            AppPaths.Disabled = true;
            try
            {
                foreach (string pattern in new[] { "config.json*", "trafficlight.log*", "requests.log*", "usage-limits.json*" })
                    foreach (string file in Directory.GetFiles(dir, pattern))
                        File.Delete(file);
                if (!AppPaths.IsSandbox && Directory.Exists(AppPaths.LegacyDir) && !string.Equals(AppPaths.LegacyDir, dir, StringComparison.OrdinalIgnoreCase))
                    Directory.Delete(AppPaths.LegacyDir, true);
            }
            catch (Exception ex) { return ex.Message; }
            return null;
        }
    }
}
