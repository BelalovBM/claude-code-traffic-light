using System;
using System.Drawing;
using System.Windows.Forms;

namespace Semaphore
{
    // New versions: looked for once a day when that is switched on (or when asked from the About page). A newer one
    // gets an entry at the top of the menu and one notice; nothing is downloaded or installed by itself.
    sealed partial class TrayApp
    {
        readonly System.Windows.Forms.Timer updateTimer = new System.Windows.Forms.Timer { Interval = 60 * 1000 };
        string newVersion, newVersionUrl;

        void StartUpdates()
        {
            // The first look a minute after the start, then every hour whether a day has passed.
            updateTimer.Tick += (s, e) =>
            {
                updateTimer.Interval = 60 * 60 * 1000;
                if (!Config.CheckUpdates || AppPaths.IsSandbox) return;
                if (DateTime.Now.Ticks - Config.UpdateCheckedTicks < TimeSpan.FromHours(23).Ticks) return;
                CheckUpdatesNow(null);
            };
            updateTimer.Start();
        }

        // Also for the About page; onResult runs on the UI thread.
        public void CheckUpdatesNow(Action<Updates.Result> onResult)
        {
            Updates.Check(r => ui.Post(_ =>
            {
                if (r.Error == null)
                {
                    Config.UpdateCheckedTicks = DateTime.Now.Ticks;
                    if (r.Newer)
                    {
                        newVersion = r.Latest;
                        newVersionUrl = string.IsNullOrEmpty(r.Url) ? AppInfo.RepoUrl + "/releases/latest" : r.Url;
                        if (Config.UpdateNotifiedVersion != r.Latest)
                        {
                            Config.UpdateNotifiedVersion = r.Latest;
                            if (!Paused)
                                Balloon(Loc.T("update.title", r.Latest), Loc.T("update.text", AppInfo.Version), Level.Idle, 10000, OpenNewVersion);
                        }
                    }
                    else newVersion = null;
                    Config.Save();
                    BuildMenu();
                }
                if (onResult != null) onResult(r);
            }, null));
        }

        void OpenNewVersion()
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(newVersionUrl ?? AppInfo.RepoUrl + "/releases/latest") { UseShellExecute = true }); }
            catch { }
        }

        void AddUpdateToMenu(ToolStripItemCollection items)
        {
            if (newVersion == null) return;
            var item = new GuardedItem(Loc.T("menu.update", newVersion));
            item.ForeColor = Theme.Fore;
            item.Font = new Font(item.Font, FontStyle.Bold);
            item.Click += (o, e) => OpenNewVersion();
            items.Add(item);
            items.Add(new ToolStripSeparator());
        }
    }
}
