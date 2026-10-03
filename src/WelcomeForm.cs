using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace Semaphore
{
    // Shown once on the first start: explains the colours and applies the basic choices.
    sealed class WelcomeForm : Form
    {
        sealed class Choice
        {
            public string Code, Text;
            public override string ToString() { return Text; }
        }

        static readonly int Width2 = Ui.S(460);
        readonly Config cfg;
        readonly Action applyConfig;

        public bool Connect = true;
        public bool Autostart = true;
        public bool Phone;

        public WelcomeForm(Config cfg, Icon icon, Action applyConfig)
        {
            this.cfg = cfg;
            this.applyConfig = applyConfig;
            Icon = icon;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            TopMost = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Width2 + Ui.S(40), Ui.S(470));
            Font = Ui.Font(9f);
            BuildUI();
        }

        void BuildUI()
        {
            SuspendLayout();
            foreach (Control c in Controls.Cast<Control>().ToArray()) { Controls.Remove(c); c.Dispose(); }
            Text = Loc.T("wiz.title");

            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = Ui.Pad(20, 14, 20, 0),
            };

            flow.Controls.Add(LanguageRow());
            flow.Controls.Add(new Label
            {
                Text = Loc.T("app.name"),
                AutoSize = true,
                Font = Ui.Font(Font.FontFamily, 15f, FontStyle.Bold),
                Margin = Ui.Pad(3, 14, 3, 6),
            });
            flow.Controls.Add(Text2(Loc.T("wiz.intro")));
            flow.Controls.Add(StatusRow(Level.Working, Loc.T("wiz.yellow")));
            flow.Controls.Add(StatusRow(Level.Compacting, Loc.T("wiz.blue")));
            flow.Controls.Add(StatusRow(Level.Waiting, Loc.T("wiz.red")));
            flow.Controls.Add(StatusRow(Level.Idle, Loc.T("wiz.green")));
            var hint = Text2(Loc.T("general.findicon"));
            hint.Tag = "muted";
            flow.Controls.Add(hint);

            flow.Controls.Add(Check(Loc.T("wiz.connect"), Connect, v => Connect = v, 16));
            flow.Controls.Add(Check(Loc.T("chk.autostart"), Autostart, v => Autostart = v, 2));
            flow.Controls.Add(Check(Loc.T("wiz.phone"), Phone, v => Phone = v, 2));

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = Ui.S(58) };
            var finish = new Button { Text = Loc.T("btn.finish"), Width = Ui.S(110), Height = Ui.S(30), DialogResult = DialogResult.OK };
            var skip = new Button { Text = Loc.T("btn.skip"), Width = Ui.S(110), Height = Ui.S(30), DialogResult = DialogResult.Cancel };
            finish.Location = new Point(ClientSize.Width - Ui.S(20 + 110 + 8 + 110), Ui.S(14));
            skip.Location = new Point(ClientSize.Width - Ui.S(20 + 110), Ui.S(14));
            bottom.Controls.Add(finish);
            bottom.Controls.Add(skip);
            AcceptButton = finish;
            CancelButton = skip;

            Controls.Add(flow);
            Controls.Add(bottom);
            Theme.Apply(this);
            ResumeLayout();
        }

        Control LanguageRow()
        {
            var row = new Panel { Width = Width2, Height = Ui.S(30), Margin = Ui.Pad(3) };
            var label = new Label { Text = Loc.T("label.language"), Left = 0, Top = Ui.S(6), Width = Ui.S(140), Height = Ui.S(24), AutoEllipsis = true };
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Left = Ui.S(150), Top = Ui.S(2), Width = Width2 - Ui.S(150) };
            combo.Items.Add(new Choice { Code = "auto", Text = Loc.T("lang.auto") });
            foreach (var l in Loc.Languages) combo.Items.Add(new Choice { Code = l.Code, Text = l.Name });
            combo.SelectedIndex = 0;
            for (int i = 0; i < combo.Items.Count; i++)
                if (((Choice)combo.Items[i]).Code == cfg.Language) combo.SelectedIndex = i;
            combo.SelectedIndexChanged += (s, e) =>
            {
                var c = combo.SelectedItem as Choice;
                if (c == null || c.Code == cfg.Language) return;
                cfg.Language = c.Code;
                applyConfig();
                BeginInvoke((Action)BuildUI);
            };
            row.Controls.Add(label);
            row.Controls.Add(combo);
            return row;
        }

        static Control Text2(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new Size(Width2, 0),
                Margin = Ui.Pad(3, 2, 3, 8),
            };
        }

        // The same shapes as in the tray, so the explanation matches what the user will see there.
        static Control StatusRow(Level level, string text)
        {
            var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Ui.Pad(12, 2, 3, 2) };
            row.Controls.Add(new PictureBox { Image = TrayIcons.Draw(level, Ui.S(20)), SizeMode = PictureBoxSizeMode.AutoSize, Margin = Ui.Pad(0, 0, 6, 0) });
            row.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(Width2 - Ui.S(40), 0), Margin = Ui.Pad(0, 3, 0, 0) });
            return row;
        }
        // A CheckBox does not wrap when auto-sized, so the height is measured for the wrapped text.
        Control Check(string text, bool value, Action<bool> set, int top)
        {
            Size wrapped = TextRenderer.MeasureText(text, Font, new Size(Width2 - Ui.S(24), 0),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            var cb = new CheckBox
            {
                Text = text,
                Checked = value,
                AutoSize = false,
                UseMnemonic = false,
                Width = Width2,
                Height = Math.Max(Ui.S(22), wrapped.Height + Ui.S(6)),
                Margin = Ui.Pad(3, top, 3, 2),
            };
            cb.CheckedChanged += (s, e) => set(cb.Checked);
            return cb;
        }
    }
}
