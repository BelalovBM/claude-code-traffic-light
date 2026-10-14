using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Semaphore
{
    sealed partial class SettingsForm
    {

        // Which Claude Code the background /usage starts: the one found by itself, or a file the user points to.
        Control ClaudeWhere()
        {
            // The width is taken now: RowWidth is the card's only while the card is built, and a long path must wrap
            // inside the card instead of widening it (which brought a horizontal scroll bar).
            int width = RowWidth - Ui.S(48);
            var box = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, MaximumSize = new Size(width, 0) };
            // "keep": the colour is set here (grey, or the warning colour when nothing is found), not by the theme.
            var text = new Label { AutoSize = true, Tag = "keep", Margin = Ui.Pad(0, 6, 8, 3), MaximumSize = new Size(width, 0) };
            var choose = new Button { Text = Loc.T("btn.claude.choose"), AutoSize = true, Margin = Ui.Pad(0, 0, 8, 3) };
            var auto = new LinkLabel { Text = Loc.T("link.claude.auto"), AutoSize = true, Margin = Ui.Pad(0, 6, 0, 3), LinkColor = Palette.Link, ActiveLinkColor = Palette.LinkActive };
            Action show = () =>
            {
                string found = UsageRefresh.FindClaude(cfg.ClaudePath);
                text.Text = found == null
                    ? Loc.T(string.IsNullOrWhiteSpace(cfg.ClaudePath) ? "limits.claude.none" : "limits.claude.missing", cfg.ClaudePath)
                    : Loc.T(string.IsNullOrWhiteSpace(cfg.ClaudePath) ? "limits.claude.found" : "limits.claude.chosen", found);
                text.ForeColor = found == null ? Palette.StatusWarn : Theme.Muted;
                auto.Visible = !string.IsNullOrWhiteSpace(cfg.ClaudePath);
            };
            choose.Click += (s, e) =>
            {
                using (var d = new OpenFileDialog { Filter = "Claude Code (claude.exe, claude.cmd)|claude.exe;claude.cmd|*.exe;*.cmd|*.exe;*.cmd", CheckFileExists = true })
                {
                    // Where the file is now, or else where Claude Code's installer puts it (the user's folder if not).
                    string found = UsageRefresh.FindClaude(cfg.ClaudePath);
                    string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    string usual = Path.Combine(home, ".local", "bin");
                    d.InitialDirectory = found != null ? Path.GetDirectoryName(found) : Directory.Exists(usual) ? usual : home;
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    cfg.ClaudePath = d.FileName;
                    cfg.Save();
                    show();
                }
            };
            auto.LinkClicked += (s, e) => { cfg.ClaudePath = ""; cfg.Save(); show(); };
            box.Controls.Add(text);
            box.Controls.Add(choose);
            box.Controls.Add(auto);
            show();
            return box;
        }

        // What happens on this computer. Which sessions are running is runtime information, not a setting;
        // a single session is silenced from its entry in the tray menu.
        void NotificationsPage(FlowLayoutPanel p)
        {
            Section(p, Loc.T("sec.local"), c =>
            {
                // A pause for a while from the tray menu is shown here, with a way to end it early.
                if (cfg.DndUntilTicks > DateTime.Now.Ticks && cfg.NotificationsEnabled)
                {
                    Label paused = ResultLabel();
                    paused.Font = new Font(Font, FontStyle.Bold);
                    SetResult(paused, Loc.T("notif.paused.until", new DateTime(cfg.DndUntilTicks).ToString("HH:mm")), Palette.StatusWarn);
                    c.Controls.Add(paused);
                    var resume = new Button { Text = Loc.T("btn.resume"), AutoSize = true, Margin = Ui.Pad(3, 0, 3, 8) };
                    resume.Click += (s, e) =>
                    {
                        cfg.DndUntilTicks = 0;
                        cfg.Save();
                        BeginInvoke((Action)Rebuild);
                    };
                    c.Controls.Add(resume);
                }
                var master = (CheckBox)Check(Loc.T("chk.notifications"), cfg.NotificationsEnabled, v => cfg.NotificationsEnabled = v);
                c.Controls.Add(master);
                // What to show is only a choice while showing anything is on.
                var parts = new[]
                {
                    Check(Loc.T("chk.sound.waiting"), cfg.SoundWaiting, v => cfg.SoundWaiting = v),
                    Check(Loc.T("chk.toast.waiting"), cfg.ToastWaiting, v => cfg.ToastWaiting = v),
                    Check(Loc.T("chk.sound.done"), cfg.SoundDone, v => cfg.SoundDone = v),
                    Check(Loc.T("chk.toast.done"), cfg.ToastDone, v => cfg.ToastDone = v),
                    Check(Loc.T("chk.toast.compact"), cfg.ToastCompact, v => cfg.ToastCompact = v),
                    Check(Loc.T("chk.context.warn"), cfg.ContextWarn, v => cfg.ContextWarn = v),
                };
                foreach (Control part in parts)
                {
                    part.Margin = Ui.Pad(24, 3, 3, 3);
                    part.Width = RowWidth - Ui.S(24);
                    part.Enabled = cfg.NotificationsEnabled;
                    c.Controls.Add(part);
                }
                master.CheckedChanged += (s, e) => { foreach (Control part in parts) part.Enabled = master.Checked; };
                // This one also goes to the phone, so it does not depend on the switch above.
                c.Controls.Add(Row(Loc.T("label.background.minutes"),
                    Spin(cfg.BackgroundNotifyMinutes, 0, 600, v => cfg.BackgroundNotifyMinutes = v), NumberLabelWidth));
                c.Controls.Add(Note(Loc.T("notif.session.hint")));
            });

            // Claude's usage limits: where they are shown, and what goes to the phone about them.
            Section(p, Loc.T("sec.limits"), c =>
            {
                // A sign-in by API key has no such limits: the switch would do nothing, so it says so instead.
                if (!UsageTracker.HasSubscription())
                {
                    c.Controls.Add(Note(Loc.T("limits.nosubscription")));
                    return;
                }
                var master = (CheckBox)Check(Loc.T("chk.limits.show"), cfg.LimitsShow, v => cfg.LimitsShow = v);
                c.Controls.Add(master);
                // The labels of the two steps are long: they take the whole row but the field, on one line where it fits.
                int stepLabel = NumberLabelWidth - 24;
                var parts = new System.Collections.Generic.List<Control>
                {
                    Check(Loc.T("chk.limits.push"), cfg.LimitsInPush, v => cfg.LimitsInPush = v),
                    Check(Loc.T("chk.limits.events"), cfg.LimitsEvents, v => cfg.LimitsEvents = v),
                    Row(Loc.T("label.limits.step"), Spin(cfg.LimitsStep, 0, 50, v => cfg.LimitsStep = v), stepLabel),
                    Row(Loc.T("label.limits.step.week"), Spin(cfg.LimitsStepWeek, 0, 50, v => cfg.LimitsStepWeek = v), stepLabel),
                };
                var auto = (CheckBox)Check(Loc.T("chk.limits.auto"), cfg.LimitsAutoRefresh, v => cfg.LimitsAutoRefresh = v);
                parts.Add(auto);
                Control where = ClaudeWhere();
                parts.Add(where);
                foreach (Control part in parts)
                {
                    part.Margin = Ui.Pad(24, 3, 3, 3);
                    part.Width = RowWidth - Ui.S(24);
                    part.Enabled = cfg.LimitsShow;
                    c.Controls.Add(part);
                }
                where.Margin = Ui.Pad(48, 0, 3, 6);
                where.Enabled = cfg.LimitsShow && cfg.LimitsAutoRefresh;
                master.CheckedChanged += (s, e) =>
                {
                    foreach (Control part in parts) part.Enabled = master.Checked;
                    where.Enabled = master.Checked && auto.Checked;
                };
                auto.CheckedChanged += (s, e) => where.Enabled = master.Checked && auto.Checked;
                Details(c, Loc.T("limits.hint"), Loc.T("limits.hint.estimate"));
            });
        }
    }
}
