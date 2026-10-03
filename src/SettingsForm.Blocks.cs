using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Semaphore
{
    sealed partial class SettingsForm
    {
        // Long pages are split in two columns so they fit the window without scrolling.
        void TwoColumns(FlowLayoutPanel page, Action<FlowLayoutPanel, FlowLayoutPanel> build)
        {
            // Too narrow for two columns: the same controls simply follow each other in one.
            if (RowWidth < Ui.S(640)) { build(page, page); return; }

            int gap = Ui.S(24);
            int column = (RowWidth - gap) / 2;
            var row = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                Margin = Ui.Pad(0),
            };
            FlowLayoutPanel left = Column(column, new Padding(0, 0, gap, 0));
            FlowLayoutPanel right = Column(column, Ui.Pad(0));

            // Controls carry a 3 px margin on each side, so they must be narrower than the column,
            // otherwise the right edge of an input is cut off.
            int saved = RowWidth;
            RowWidth = column - Ui.S(8);
            try { build(left, right); }
            finally { RowWidth = saved; }

            row.Controls.Add(left);
            row.Controls.Add(right);
            page.Controls.Add(row);
        }

        static FlowLayoutPanel Column(int width, Padding margin)
        {
            return new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                MinimumSize = new Size(width, 0),
                MaximumSize = new Size(width, 0),
                Margin = margin,
            };
        }

        // Label width that leaves room for a small number box at the end of the row.
        int NumberLabelWidth { get { return (int)Math.Min(RowWidth / Ui.Scale - 100, 420); } }
        void OnUi(Action action)
        {
            try { BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }

        void Info(string text, MessageBoxIcon icon)
        {
            MessageBox.Show(this, text, Loc.T("app.name"), MessageBoxButtons.OK, icon);
        }

        // The result of an action shows next to its button, not in a window that has to be closed.
        Label ResultLabel()
        {
            return new Label
            {
                AutoSize = true,
                MaximumSize = new Size(RowWidth, 0),
                Tag = "keep",
                Margin = Ui.Pad(3, 4, 3, 6),
                AccessibleRole = AccessibleRole.StaticText,
                Visible = false,
            };
        }

        static void SetResult(Label label, string text, Color color)
        {
            label.Visible = !string.IsNullOrEmpty(text);
            label.Text = text;
            label.ForeColor = color;
        }

        // A short explanation instead of a raw server reply for the usual causes.
        static string FriendlyError(string error)
        {
            switch (Notifier.ErrorKind(error))
            {
                case "auth": return Loc.T("err.auth");
                case "forbidden": return Loc.T("err.forbidden");
                case "timeout": return Loc.T("err.timeout");
                case "network": return Loc.T("err.network");
            }
            string flat = (error ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (flat.Length > 160) flat = flat.Substring(0, 160) + "…";
            return Loc.T("test.fail", flat);
        }

        // Runs a send on the pool and writes the outcome into the label under the button.
        void RunTest(Button button, Label result, Action<Action<string>> send, Action delivered = null)
        {
            button.Enabled = false;
            SetResult(result, Loc.T("test.sending"), Theme.Fore);
            send(error => OnUi(() =>
            {
                if (error == null && delivered != null) delivered();
                if (button.IsDisposed || result.IsDisposed) return;
                button.Enabled = true;
                if (error == null) SetResult(result, "✔ " + Loc.T("test.ok"), Palette.StatusOk);
                else SetResult(result, "✖ " + FriendlyError(error), Palette.StatusError);
            }));
        }

        // Debounced saving of a text field: applied 600 ms after the last keystroke, when the field is left
        // and when the window closes.
        void Autosave(TextBox box, Action<string> apply)
        {
            var timer = new Timer { Interval = 600 };
            string last = box.Text;
            Action flush = null;
            flush = () =>
            {
                timer.Stop();
                pending.Remove(flush);
                if (box.IsDisposed || box.Text == last) return;
                last = box.Text;
                apply(box.Text.Trim());
            };
            timer.Tick += (s, e) => flush();
            box.TextChanged += (s, e) =>
            {
                timer.Stop();
                timer.Start();
                if (!pending.Contains(flush)) pending.Add(flush);
            };
            box.Leave += (s, e) => flush();
            box.Disposed += (s, e) => timer.Dispose();
        }

        // A titled card that groups one function of the page; the content gets the card's inner width.
        void Section(FlowLayoutPanel parent, string title, Action<FlowLayoutPanel> build)
        {
            int outer = RowWidth;
            int inner = outer - Ui.S(28 + 12);
            var card = new CardPanel { Margin = Ui.Pad(3, 3, 3, 12) };
            FlowLayoutPanel box = Column(inner, Ui.Pad(0));
            int saved = RowWidth;
            RowWidth = inner - Ui.S(8);
            try
            {
                if (title != null) box.Controls.Add(CardHeader(title));
                build(box);
            }
            finally { RowWidth = saved; }
            card.Controls.Add(box);
            parent.Controls.Add(card);
        }
        // Explanations that are needed once, folded under "More details"; opening them needs no rebuild.
        void Details(FlowLayoutPanel parent, params string[] notes)
        {
            var body = Column(RowWidth, Ui.Pad(0));
            foreach (string n in notes) body.Controls.Add(Note(n));
            body.Visible = false;
            var toggle = new LinkLabel
            {
                Text = "▸  " + Loc.T("details.more"),
                AutoSize = true,
                LinkColor = Palette.Link,
                ActiveLinkColor = Palette.LinkActive,
                Margin = Ui.Pad(3, 6, 3, 4),
            };
            toggle.LinkClicked += (s, e) =>
            {
                body.Visible = !body.Visible;
                toggle.Text = (body.Visible ? "▾  " : "▸  ") + Loc.T(body.Visible ? "details.less" : "details.more");
            };
            parent.Controls.Add(toggle);
            parent.Controls.Add(body);
        }

        // A numbered step: the number stands out, the text wraps next to it.
        Control Step(int number, string text)
        {
            var row = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                Margin = Ui.Pad(3, 8, 3, 2),
            };
            row.Controls.Add(new Label
            {
                Text = number.ToString(),
                AutoSize = false,
                Width = Ui.S(22),
                Height = Ui.S(22),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font(Font, FontStyle.Bold),
                Tag = "keep",
                ForeColor = Palette.AccentText,
                Margin = Ui.Pad(0, 0, 6, 0),
            });
            row.Controls.Add(new Label
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new Size(RowWidth - Ui.S(40), 0),
                Margin = Ui.Pad(0, 3, 0, 0),
            });
            return row;
        }

        Control CardHeader(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Font = new Font(Font.FontFamily, Font.Size + 1.5f, FontStyle.Bold),
                Margin = Ui.Pad(3, 0, 3, 8),
            };
        }

        // The picture is wrapped in a panel as wide as the column so that it sits in the middle.
        Control CenteredPicture(Bitmap image)
        {
            var holder = new Panel { Width = RowWidth, Height = image.Height + Ui.S(8), Margin = Ui.Pad(3) };
            holder.Controls.Add(new PictureBox
            {
                Image = image,
                SizeMode = PictureBoxSizeMode.AutoSize,
                Left = (RowWidth - image.Width) / 2,
                Top = Ui.S(4),
            });
            return holder;
        }

        static string Shorten(string text, int max)
        {
            string flat = (text ?? "").Replace("\r", " ").Replace("\n", " ");
            return flat.Length <= max ? flat : flat.Substring(0, max - 1) + "…";
        }

        // ---- building blocks ----

        Control Plain(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new Size(RowWidth, 0),
                Margin = Ui.Pad(3, 2, 3, 6),
            };
        }

        Control Header(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold),
                Margin = Ui.Pad(3, 14, 3, 4),
            };
        }

        Control Note(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new Size(RowWidth, 0),
                Tag = "muted",
                Margin = Ui.Pad(3, 2, 3, 6),
            };
        }

        Control Row(string label, Control input, int labelWidth)
        {
            // A label that does not fit on one line wraps to two instead of being cut off.
            bool tall = TextRenderer.MeasureText(label, Font, new Size(int.MaxValue, 0), TextFormatFlags.NoPrefix).Width > Ui.S(labelWidth);
            int height = tall ? Ui.S(44) : Ui.S(30);
            var p = new Panel { Width = RowWidth, Height = height, Margin = Ui.Pad(3, 2, 3, 2) };
            var l = tall
                ? new Label { Text = label, Left = 0, Top = Ui.S(3), Width = Ui.S(labelWidth), Height = Ui.S(38) }
                // The default label height is a fixed 23 pixels: at 200% the text no longer fits in it.
                : new Label { Text = label, Left = 0, Top = Ui.S(6), Width = Ui.S(labelWidth), Height = height - Ui.S(6), AutoEllipsis = true };
            input.AccessibleName = label;
            input.Left = Ui.S(labelWidth + 10);
            input.Top = (height - input.Height) / 2;
            if (!(input is NumericUpDown)) input.Width = RowWidth - Ui.S(labelWidth + 10);
            p.Controls.Add(l);
            p.Controls.Add(input);
            return p;
        }

        Control Spin(int value, int min, int max, Action<int> set)
        {
            var n = new NumericUpDown { Minimum = min, Maximum = max, Width = Ui.S(70) };
            n.Value = Math.Max(min, Math.Min(max, value));
            n.ValueChanged += (s, e) =>
            {
                set((int)n.Value);
                app.ApplyConfig();
            };
            return n;
        }

        Control Check(string text, bool value, Action<bool> set)
        {
            Size wrapped = TextRenderer.MeasureText(text, Font, new Size(RowWidth - Ui.S(24), 0),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            var cb = new ThemedCheckBox
            {
                Text = text,
                Checked = value,
                AutoSize = false,
                UseMnemonic = false,
                Width = RowWidth,
                Height = Math.Max(Ui.S(22), wrapped.Height + Ui.S(6)),
                Margin = Ui.Pad(3, 3, 3, 3),
            };
            cb.CheckedChanged += (s, e) =>
            {
                set(cb.Checked);
                app.ApplyConfig();
            };
            return cb;
        }

        Control LanguageCombo()
        {
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            combo.Items.Add(new Choice { Code = "auto", Text = Loc.T("lang.auto") });
            foreach (var l in Loc.Languages)
                combo.Items.Add(new Choice { Code = l.Code, Text = l.Name });
            SelectCode(combo, cfg.Language);
            combo.SelectedIndexChanged += (s, e) =>
            {
                var c = combo.SelectedItem as Choice;
                if (c == null || c.Code == cfg.Language) return;
                cfg.Language = c.Code;
                app.ApplyConfig();
                BeginInvoke((Action)Rebuild);
            };
            return combo;
        }

        Control ThemeCombo()
        {
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            combo.Items.Add(new Choice { Code = "system", Text = Loc.T("theme.system") });
            combo.Items.Add(new Choice { Code = "light", Text = Loc.T("theme.light") });
            combo.Items.Add(new Choice { Code = "dark", Text = Loc.T("theme.dark") });
            SelectCode(combo, cfg.Theme);
            combo.SelectedIndexChanged += (s, e) =>
            {
                var c = combo.SelectedItem as Choice;
                if (c == null || c.Code == cfg.Theme) return;
                cfg.Theme = c.Code;
                app.ApplyConfig();
                BeginInvoke((Action)Rebuild);
            };
            return combo;
        }

        static void SelectCode(ComboBox combo, string code)
        {
            for (int i = 0; i < combo.Items.Count; i++)
                if (((Choice)combo.Items[i]).Code == code) { combo.SelectedIndex = i; return; }
            combo.SelectedIndex = 0;
        }
    }
}
