using System;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Semaphore
{
    // Changing the ntfy topic silences every phone subscribed to the old one, so it is
    // done in a separate dialog instead of an editable field on the settings page.
    sealed class TopicDialog : Form
    {
        static readonly Regex Valid = new Regex("^[A-Za-z0-9_-]{1,64}$");
        readonly TextBox box = new TextBox();
        readonly Label status = new Label();
        readonly Button ok = new Button();

        public string Topic { get { return box.Text.Trim(); } }

        public TopicDialog(string current, Icon icon)
        {
            Text = Loc.T("topic.title");
            Icon = icon;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Ui.S(440), Ui.S(270));
            Font = Ui.Font(9f);

            var warn = new Label { Text = Loc.T("topic.warn"), Left = Ui.S(16), Top = Ui.S(14), Width = Ui.S(408), Height = Ui.S(82) };
            box.Left = Ui.S(16); box.Top = Ui.S(102); box.Width = Ui.S(408);
            box.Text = current;
            var random = new Button { Text = Loc.T("topic.random"), Left = Ui.S(16), Top = Ui.S(134), AutoSize = true };
            random.Click += (s, e) => box.Text = Secret.RandomTopic();

            status.Left = Ui.S(16); status.Top = Ui.S(174); status.Width = Ui.S(408); status.Height = Ui.S(44);
            status.Tag = "keep";

            ok.Text = Loc.T("btn.ok");
            ok.Left = Ui.S(244); ok.Top = Ui.S(226); ok.Width = Ui.S(88);
            ok.DialogResult = DialogResult.OK;
            var cancel = new Button { Text = Loc.T("btn.cancel"), Left = Ui.S(338), Top = Ui.S(226), Width = Ui.S(88), DialogResult = DialogResult.Cancel };
            AcceptButton = ok;
            CancelButton = cancel;

            box.TextChanged += (s, e) => Check();
            Controls.AddRange(new Control[] { warn, box, random, status, ok, cancel });
            Theme.Apply(this);
            Check();
        }

        void Check()
        {
            string t = box.Text.Trim();
            bool valid = Valid.IsMatch(t);
            ok.Enabled = valid;
            if (!valid)
            {
                status.Text = Loc.T("topic.invalid");
                status.ForeColor = Palette.StatusError;
            }
            else if (t.Length < 16)
            {
                status.Text = Loc.T("topic.weak");
                status.ForeColor = Palette.StatusWarn;
            }
            else status.Text = "";
        }
    }
}
