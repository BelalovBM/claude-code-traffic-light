using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Semaphore
{
    sealed partial class SettingsForm
    {

        // Three blocks, each one job: the link to Claude Code, where things are kept, and taking it all away.
        void ConnectionPage(FlowLayoutPanel p)
        {
            Section(p, Loc.T("sec.integration"), c =>
            {
                c.Controls.Add(HooksPanel());
                // The list of running sessions is read from files Claude Code does not document; say when it cannot be read.
                if (SessionRegistry.FormatUnknown) c.Controls.Add(Note(Loc.T("claude.format.note")));
            });

            Section(p, Loc.T("sec.diag"), c =>
            {
                c.Controls.Add(Plain(Loc.T("diag.hint", AppPaths.DataDir)));
                var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
                var log = new Button { Text = Loc.T("btn.log"), AutoSize = true };
                log.Click += (s, e) => OpenLog();
                var requests = new Button { Text = Loc.T("btn.requests.log"), AutoSize = true };
                requests.Click += (s, e) => OpenRequestsLog();
                var folder = new Button { Text = Loc.T("btn.folder"), AutoSize = true };
                folder.Click += (s, e) => OpenFolder();
                buttons.Controls.Add(log);
                buttons.Controls.Add(requests);
                buttons.Controls.Add(folder);
                c.Controls.Add(buttons);
                c.Controls.Add(Note(Loc.T("diag.requests")));
            });

            Section(p, Loc.T("sec.uninstall"), c =>
            {
                c.Controls.Add(Plain(Loc.T("uninstall.hint")));
                var wipe = new Button { Text = Loc.T("btn.uninstall"), AutoSize = true };
                wipe.Click += (s, e) => UninstallAll();
                c.Controls.Add(wipe);
            });
        }

        // Opens the journal of requests; an empty file is made first so that the button works before the first request.
        static void OpenRequestsLog()
        {
            try
            {
                string path = Path.Combine(AppPaths.DataDir, "requests.log");
                if (!File.Exists(path)) File.WriteAllText(path, "", new System.Text.UTF8Encoding(false));
                System.Diagnostics.Process.Start(path);
            }
            catch { }
        }

        static void OpenFolder()
        {
            try { System.Diagnostics.Process.Start("explorer.exe", "\"" + AppPaths.DataDir + "\""); }
            catch { }
        }
        void UninstallAll()
        {
            var answer = MessageBox.Show(this, Loc.T("uninstall.confirm"), Loc.T("app.name"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            string error = Uninstaller.Run();
            if (error != null)
            {
                Info(Loc.T("uninstall.error", error), MessageBoxIcon.Error);
                return;
            }
            Info(Loc.T("uninstall.done"), MessageBoxIcon.Information);
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + Application.ExecutablePath + "\"");
            }
            catch { }
            app.Quit();
        }

        Control HooksPanel()
        {
            var p = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                Margin = Ui.Pad(3),
            };

            var label = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(RowWidth, 0),
                Font = new Font(Font, FontStyle.Bold),
                Tag = "keep",
            };
            Action paint = () =>
            {
                HooksStatus status = HookInstaller.GetStatus();
                string key = status == HooksStatus.Installed ? "hooks.installed"
                           : status == HooksStatus.Outdated ? "hooks.outdated" : "hooks.missing";
                SetResult(label, (status == HooksStatus.Installed ? "● " : "▲ ") + Loc.T(key),
                    status == HooksStatus.Installed ? Palette.StatusOk : Palette.StatusWarn);
            };
            paint();
            refresher = paint;
            var result = ResultLabel();
            var hint = new Label
            {
                Text = Loc.T("hooks.hint"),
                AutoSize = true,
                MaximumSize = new Size(RowWidth, 0),
                Margin = Ui.Pad(3, 4, 3, 6),
            };

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            var install = new Button { Text = Loc.T("btn.hooks.install"), AutoSize = true };
            var remove = new Button { Text = Loc.T("btn.hooks.remove"), AutoSize = true };
            install.Click += (s, e) => { RunHookAction(HookInstaller.Install, "msg.hooks.installed", result); paint(); };
            remove.Click += (s, e) => { RunHookAction(HookInstaller.Remove, "msg.hooks.removed", result); paint(); };
            buttons.Controls.Add(install);
            buttons.Controls.Add(remove);

            p.Controls.Add(label);
            p.Controls.Add(hint);
            p.Controls.Add(buttons);
            p.Controls.Add(result);

            // Claude Code inside WSL is out of reach of these hooks; the user should know why it stays silent.
            var wsl = ResultLabel();
            wsl.Font = new Font(Font, FontStyle.Bold);
            var wslNote = Note(Loc.T("wsl.note"));
            wslNote.Visible = false;
            Action showWsl = () =>
            {
                List<string> distros = Wsl.Found;
                bool any = distros != null && distros.Count > 0;
                SetResult(wsl, any ? "▲ " + Loc.T("wsl.found", string.Join(", ", distros)) : "", Palette.StatusWarn);
                wslNote.Visible = any;
            };
            showWsl();
            if (Wsl.Found == null) Wsl.Search(() => OnUi(() => { if (!wsl.IsDisposed) showWsl(); }));
            p.Controls.Add(wsl);
            p.Controls.Add(wslNote);
            return p;
        }

        void RunHookAction(Action action, string okKey, Label result)
        {
            try
            {
                action();
                SetResult(result, "✔ " + Loc.T(okKey), Palette.StatusOk);
            }
            catch (Exception ex)
            {
                Log.Write("Hook action failed: " + ex);
                SetResult(result, "✖ " + Loc.T("msg.hooks.error", ex.Message).Replace('\n', ' '), Palette.StatusError);
            }
        }

        static void OpenLog()
        {
            try
            {
                if (File.Exists(Log.FilePath)) System.Diagnostics.Process.Start(Log.FilePath);
            }
            catch { }
        }
    }
}
