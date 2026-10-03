using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Semaphore
{
    sealed partial class SettingsForm
    {
        // ---- pages ----

        void GeneralPage(FlowLayoutPanel p)
        {
            Section(p, Loc.T("sec.appearance"), c =>
            {
                c.Controls.Add(Row(Loc.T("label.language"), LanguageCombo(), 140));
                c.Controls.Add(Row(Loc.T("label.theme"), ThemeCombo(), 140));
            });

            Section(p, Loc.T("sec.startup"), c =>
            {
                c.Controls.Add(Check(Loc.T("chk.autostart"), cfg.Autostart, v => cfg.Autostart = v));
                c.Controls.Add(Check(Loc.T("chk.startup.notice"), cfg.StartupNotice, v => cfg.StartupNotice = v));
                // A new icon is easy to miss: Windows 10 and 11 put it under the arrow until it is dragged out.
                c.Controls.Add(Note(Loc.T("general.findicon")));
            });

            Section(p, Loc.T("sec.keyboard"), c =>
            {
                var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
                foreach (string k in Hotkey.Choices)
                    combo.Items.Add(new Choice { Code = k, Text = k.Length == 0 ? Loc.T("hotkey.off") : k });
                string current = cfg.Hotkey ?? "";
                combo.SelectedIndex = Math.Max(0, Array.IndexOf(Hotkey.Choices, current));
                var state = ResultLabel();
                Action paint = () =>
                {
                    Hotkey h = app.Shortcut;
                    SetResult(state, h.Taken ? "▲ " + Loc.T("hotkey.taken", h.Current) : "", Palette.StatusWarn);
                };
                combo.SelectedIndexChanged += (s, e) =>
                {
                    cfg.Hotkey = ((Choice)combo.SelectedItem).Code;
                    app.ApplyConfig();
                    paint();
                };
                c.Controls.Add(Row(Loc.T("label.hotkey"), combo, 140));
                c.Controls.Add(state);
                paint();
                c.Controls.Add(Note(Loc.T("hotkey.hint")));
            });

            Section(p, Loc.T("sec.check"), c =>
            {
                var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, MaximumSize = new Size(RowWidth, 0) };
                var sound = new Button { Text = Loc.T("btn.sound.test"), AutoSize = true };
                sound.Click += (s, e) => System.Media.SystemSounds.Exclamation.Play();
                var toast = new Button { Text = Loc.T("btn.toast.test"), AutoSize = true };
                toast.Click += (s, e) => app.ShowTestNotification();
                var log = new Button { Text = Loc.T("btn.log"), AutoSize = true };
                log.Click += (s, e) => OpenLog();
                buttons.Controls.Add(sound);
                buttons.Controls.Add(toast);
                buttons.Controls.Add(log);
                c.Controls.Add(buttons);
            });
        }
    }
}
