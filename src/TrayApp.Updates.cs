using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Semaphore
{
    // New versions: looked for once a day when that is switched on (or when asked from the About page). A newer one
    // gets an entry at the top of the menu and one notice; nothing is downloaded or installed by itself. The prices of
    // the models from LiteLLM come on the same daily round.
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
                if (AppPaths.IsSandbox) return;
                // The prices of the models from LiteLLM, on the same daily round.
                if (Config.PricesAuto && DateTime.Now.Ticks - Config.PricesTriedTicks >= TimeSpan.FromHours(23).Ticks)
                    LoadPricesNow(null);
                if (!Config.CheckUpdates) return;
                if (DateTime.Now.Ticks - Config.UpdateCheckedTicks < TimeSpan.FromHours(23).Ticks) return;
                CheckUpdatesNow(null);
            };
            updateTimer.Start();
        }

        // Loads LiteLLM's prices and applies them; also for the button in the settings. done gets the number of prices
        // that changed, or the error, on the UI thread.
        public void LoadPricesNow(Action<int, string> done)
        {
            ApiPrices.LoadLiteLlm((prices, error, fetched) => ui.Post(_ =>
            {
                int changed = 0;
                // The next try a day later once the file came, even if it held nothing to use (no reason to fetch
                // its 3 MB again within the day); an hour later when the network failed.
                Config.PricesTriedTicks = fetched ? DateTime.Now.Ticks : DateTime.Now.AddHours(-22).Ticks;
                if (error == null)
                {
                    changed = ApiPrices.Compare(prices).Count;
                    List<string> skipped = ApiPrices.Apply(prices, DateTime.Now);
                    changed = Math.Max(0, changed - skipped.Count);
                    Config.PricesLoadedTicks = DateTime.Now.Ticks;
                    Log.Write("Prices from LiteLLM applied, " + changed + " changed" + (skipped.Count > 0 ? ", left out: " + string.Join(", ", skipped) : ""));
                }
                Config.Save();
                if (done != null) done(changed, error);
            }, null));
        }

        // How current the prices are: the day they last came from LiteLLM, or the day the built-in ones were checked.
        internal DateTime PricesAsOf
        {
            get
            {
                DateTime loaded = Config.PricesLoadedTicks > 0 ? new DateTime(Config.PricesLoadedTicks) : DateTime.MinValue;
                return loaded > ApiPrices.BuiltInDate ? loaded.Date : ApiPrices.BuiltInDate;
            }
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
