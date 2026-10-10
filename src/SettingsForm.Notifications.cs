using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Semaphore
{
    sealed partial class SettingsForm
    {

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
                c.Controls.Add(Check(Loc.T("chk.limits.show"), cfg.LimitsShow, v => cfg.LimitsShow = v));
                c.Controls.Add(Check(Loc.T("chk.limits.push"), cfg.LimitsInPush, v => cfg.LimitsInPush = v));
                c.Controls.Add(Check(Loc.T("chk.limits.events"), cfg.LimitsEvents, v => cfg.LimitsEvents = v));
                c.Controls.Add(Row(Loc.T("label.limits.step"), Spin(cfg.LimitsStep, 0, 50, v => cfg.LimitsStep = v), NumberLabelWidth));
                c.Controls.Add(Row(Loc.T("label.limits.step.week"), Spin(cfg.LimitsStepWeek, 0, 50, v => cfg.LimitsStepWeek = v), NumberLabelWidth));
                Details(c, Loc.T("limits.hint"), Loc.T("limits.hint.estimate"));
            });
        }
    }
}
