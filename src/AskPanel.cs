using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Semaphore
{
    // The small panel near the tray where a permission prompt or a multiple-choice question from Claude Code
    // is shown in full and answered with a click. The tray menu is no place for long text and buttons.
    // It never takes the keyboard focus, so it cannot swallow what the user is typing.
    // What identifies the session that asks.
    sealed class AskSession
    {
        public string Name, Id, WorkDir, Cwd;
        public int Pid;
    }

    sealed class AskPanel : Form
    {
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        static readonly int ContentWidth = Ui.S(400);
        const int Gap = 14;

        public readonly string Token;
        public readonly string SessionId;
        public DateTime Deadline { get; private set; }
        public bool Expired { get; private set; }

        Label timerLabel;
        Label phoneLabel;
        FlowLayoutPanel flowPanel;
        Label crossLabel;
        LinkLabel laterLink;
        System.Windows.Forms.Timer countdown;
        readonly System.Collections.Generic.List<Control> answerControls = new System.Collections.Generic.List<Control>();

        // The mouse moves over the panel: the user is reading it.
        public event Action Hovered;

        void AttachHover(Control c)
        {
            c.MouseMove += (s, e) => { var h = Hovered; if (h != null) h(); };
            foreach (Control child in c.Controls) AttachHover(child);
        }

        readonly ToolTip tips = new ToolTip { ShowAlways = true, AutoPopDelay = 20000 };

        public AskPanel(Approvals.Request request, AskSession session,
            Action<int> pick, Action<bool> decide, Action other, Action dismiss, bool readOnly = false)
        {
            Token = request.Token;
            SessionId = request.SessionId;
            Deadline = request.Deadline;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Font = Ui.Font(9f);
            Padding = Ui.Pad(Gap);
            // Never drawn (no frame), but a screen reader announces the window by it.
            Text = Loc.T("toast.waiting.title");
            AccessibleName = Text;

            var flow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                Location = new Point(Ui.S(Gap), Ui.S(Gap)),
            };

            // The same lamp and title the pop-up used to have, so the panel is the one place that announces the request.
            var head = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Ui.Pad(0, 0, 0, 8) };
            head.Controls.Add(new PictureBox
            {
                Image = TrayIcons.Draw(Level.Waiting, Ui.S(36)),
                SizeMode = PictureBoxSizeMode.AutoSize,
                Margin = Ui.Pad(0, 0, 10, 0),
            });
            var titles = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Ui.Pad(0) };
            titles.Controls.Add(new Label
            {
                Text = Loc.T("toast.waiting.title"),
                AutoSize = true,
                Font = new Font(Font.FontFamily, Font.Size + 1.5f, FontStyle.Bold),
                Margin = Ui.Pad(0, 2, 0, 0),
            });
            titles.Controls.Add(new Label
            {
                Text = Loc.T(readOnly ? "ask.header.readonly" : "ask.header"),
                AutoSize = true,
                Tag = "keep",
                ForeColor = Palette.StatusWarn,
                Font = new Font(Font, FontStyle.Bold),
                Margin = Ui.Pad(0, 0, 0, 0),
            });
            head.Controls.Add(titles);
            flow.Controls.Add(head);

            // How long the request can still be answered: the program stops waiting at the deadline, and then
            // only the window of Claude Code is left. The last seconds are red.
            // Filled in by ShowPhoneSent when the request has been pushed to the phone.
            phoneLabel = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(ContentWidth, 0),
                Tag = "muted",
                Margin = Ui.Pad(0, 0, 0, 6),
                Visible = false,
            };
            flow.Controls.Add(phoneLabel);
            if (request.Deadline > DateTime.MinValue && !readOnly)
            {
                timerLabel = new Label
                {
                    AutoSize = true,
                    MaximumSize = new Size(ContentWidth, 0),
                    Tag = "keep",
                    Margin = Ui.Pad(0, 0, 0, 8),
                };
                flow.Controls.Add(timerLabel);
                countdown = new System.Windows.Forms.Timer { Interval = 500 };
                countdown.Tick += (s, e) => UpdateCountdown();
                UpdateCountdown();
                countdown.Start();
            }

            // Which session it is, apart from the request: name, the folders (shortened, the full path in a tip) and ids.
            var who = new Label
            {
                Text = session.Name.Replace("&", "&&"),
                AutoSize = true,
                MaximumSize = new Size(ContentWidth, 0),
                Font = new Font(Font, FontStyle.Bold),
                Margin = Ui.Pad(0, 0, 0, 2),
            };
            flow.Controls.Add(who);
            if (!string.IsNullOrEmpty(session.WorkDir))
                flow.Controls.Add(PathLine("menu.session.workdir", session.WorkDir));
            if (!string.IsNullOrEmpty(session.Cwd))
                flow.Controls.Add(PathLine("menu.session.cwd", session.Cwd));
            flow.Controls.Add(Line(Loc.T("menu.session.id", session.Id, session.Pid > 0 ? session.Pid.ToString() : "?"), true, 0, 0, 0));
            var rule = new Panel { Height = 1, Width = ContentWidth, Tag = "rule", Margin = Ui.Pad(0, 10, 0, 10) };
            flow.Controls.Add(rule);
            if (readOnly)
                flow.Controls.Add(Line(Loc.T("ask.readonly.note"), true, 0, 0, 8));

            if (request.Options != null)
            {
                flow.Controls.Add(Line(request.Question, false, 0, 4, 8));
                for (int i = 0; i < request.Options.Count && !readOnly; i++)
                {
                    int index = i;
                    var button = Choice(request.Options[i]);
                    button.Click += (s, e) => { pick(index); Close(); };
                    flow.Controls.Add(button);
                    answerControls.Add(button);
                    string description = request.Descriptions != null && i < request.Descriptions.Count ? request.Descriptions[i] : "";
                    if (!string.IsNullOrEmpty(description))
                        flow.Controls.Add(Line(description, true, 8, 0, 0));
                }
                if (readOnly)
                {
                    // Nothing can answer it from here; the answers are listed so the question can be understood.
                    for (int i = 0; i < request.Options.Count; i++)
                    {
                        flow.Controls.Add(Line("•  " + request.Options[i], false, 0, 3, 0));
                        string d = request.Descriptions != null && i < request.Descriptions.Count ? request.Descriptions[i] : "";
                        if (!string.IsNullOrEmpty(d)) flow.Controls.Add(Line(d, true, 14, 0, 0));
                    }
                }
                var more = Choice(readOnly ? Loc.T("ask.open.window") : Loc.T("menu.approve.other"));
                if (!readOnly) more.Tag = "muted-button";
                more.Click += (s, e) => { other(); Close(); };
                more.Margin = Ui.Pad(0, 10, 0, 0);
                flow.Controls.Add(more);
            }
            else if (readOnly)
            {
                flow.Controls.Add(Line(request.Tool, false, 0, 4, 2));
                if (!string.IsNullOrEmpty(request.Summary))
                {
                    var command = Line(request.Summary, false, 0, 0, 10);
                    command.Font = new Font("Consolas", Font.Size);
                    flow.Controls.Add(command);
                }
                var open = Choice(Loc.T("ask.open.window"));
                open.Click += (s, e) => { other(); Close(); };
                flow.Controls.Add(open);
            }
            else
            {
                flow.Controls.Add(Line(request.Tool, false, 0, 4, 2));
                if (!string.IsNullOrEmpty(request.Summary))
                {
                    var command = Line(request.Summary, false, 0, 0, 10);
                    command.Font = new Font("Consolas", Font.Size);
                    flow.Controls.Add(command);
                }
                var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Ui.Pad(0, 4, 0, 0) };
                var allow = Choice(Loc.T("approve.push.allow"));
                allow.Width = (ContentWidth - Ui.S(10)) / 2;
                allow.Click += (s, e) => { decide(true); Close(); };
                answerControls.Add(allow);
                var deny = Choice(Loc.T("approve.push.deny"));
                deny.Width = (ContentWidth - Ui.S(10)) / 2;
                deny.Click += (s, e) => { decide(false); Close(); };
                answerControls.Add(deny);
                buttons.Controls.Add(allow);
                buttons.Controls.Add(deny);
                flow.Controls.Add(buttons);
            }

            var later = new LinkLabel
            {
                Text = readOnly ? Loc.T("btn.ask.close") : Loc.T("btn.ask.later"),
                AutoSize = true,
                LinkColor = Palette.Link,
                ActiveLinkColor = Palette.LinkActive,
                Margin = Ui.Pad(0, 10, 0, 0),
            };
            later.LinkClicked += (s, e) => { dismiss(); Close(); };
            laterLink = later;
            dismissAction = dismiss;
            flow.Controls.Add(later);

            flowPanel = flow;
            Controls.Add(flow);
            Theme.Apply(this);
            foreach (Control c in flow.Controls) RestyleMutedButtons(c);

            Size need = flow.GetPreferredSize(new Size(ContentWidth, 0));
            ClientSize = new Size(ContentWidth + 2 * Ui.S(Gap), need.Height + 2 * Ui.S(Gap));
            // A cross in the corner closes the panel the same way as "Later"; the request stays in the menu.
            var cross = new Label
            {
                Text = "✕",
                AutoSize = false,
                Width = Ui.S(26),
                Height = Ui.S(26),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font(Font.FontFamily, Font.Size + 1f),
                ForeColor = Theme.Muted,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                Tag = "keep",
            };
            cross.Left = ClientSize.Width - cross.Width - Ui.S(8);
            cross.Top = Ui.S(8);
            cross.MouseEnter += (s, e) => { cross.ForeColor = Theme.Fore; };
            cross.MouseLeave += (s, e) => { cross.ForeColor = Theme.Muted; };
            cross.Click += (s, e) => { dismiss(); Close(); };
            tips.SetToolTip(cross, Loc.T("btn.ask.later"));
            crossLabel = cross;
            Controls.Add(cross);
            cross.BringToFront();
            UpdateCountdown();

            AttachHover(this);
            area = Native.TrayScreen().WorkingArea;
            Location = new Point(area.Right - Width - Ui.S(12), area.Bottom - Height - Ui.S(12));
        }

        void UpdateCountdown()
        {
            if (timerLabel == null || Expired) return;
            TimeSpan left = Deadline - DateTime.Now;
            if (left <= TimeSpan.Zero) { Expire(); return; }
            timerLabel.Text = Loc.T("ask.timer", string.Format("{0}:{1:00}", (int)left.TotalMinutes, left.Seconds));
            bool urgent = left.TotalSeconds <= 15;
            timerLabel.ForeColor = urgent ? Palette.StatusError : Theme.Muted;
            timerLabel.Font = new Font(Font, urgent ? FontStyle.Bold : FontStyle.Regular);
        }

        // The request also went to the phone: it can be answered there.
        public void ShowPhoneSent(DateTime at)
        {
            if (phoneLabel == null || phoneLabel.Visible || Expired) return;
            phoneLabel.Text = Loc.T("ask.sentphone", at.ToString("HH:mm"));
            phoneLabel.Visible = true;
            Reflow();
        }

        // The time is up: the buttons no longer work, what is left is the window of Claude Code.
        public void ExpireNow() { Expire(); }

        void Expire()
        {
            if (Expired || timerLabel == null) return;
            Expired = true;
            if (countdown != null) countdown.Stop();
            foreach (Control c in answerControls) c.Enabled = false;
            if (laterLink != null) laterLink.Text = Loc.T("btn.ask.close");
            timerLabel.Text = Loc.T("ask.expired.panel");
            timerLabel.ForeColor = Palette.StatusError;
            timerLabel.Font = new Font(Font, FontStyle.Bold);
            Reflow();
        }

        // The corner of the monitor with the tray icon, chosen once so the panel does not jump between screens.
        Rectangle area;

        // The text changed its height: the panel keeps its place at the bottom right.
        void Reflow()
        {
            Size need = flowPanel.GetPreferredSize(new Size(ContentWidth, 0));
            ClientSize = new Size(ContentWidth + 2 * Ui.S(Gap), need.Height + 2 * Ui.S(Gap));
            if (crossLabel != null) crossLabel.Left = ClientSize.Width - crossLabel.Width - Ui.S(8);
            if (area.IsEmpty) area = Native.TrayScreen().WorkingArea;
            Location = new Point(area.Right - Width - Ui.S(12), area.Bottom - Height - Ui.S(12));
        }

        static void RestyleMutedButtons(Control c)
        {
            var b = c as Button;
            if (b != null && "muted-button".Equals(b.Tag)) b.ForeColor = Theme.Muted;
            if (c is Panel && "rule".Equals(c.Tag)) c.BackColor = Theme.Border;
        }

        // A folder shortened to its end; the whole path shows when the mouse rests on it.
        Label PathLine(string key, string path)
        {
            Label label = Line(Loc.T(key, TrayApp.ShortPath(path, 46)), true, 0, 0, 0);
            tips.SetToolTip(label, path);
            return label;
        }

        Label Line(string text, bool muted, int left, int top, int bottom)
        {
            return new Label
            {
                Text = text.Replace("&", "&&"),
                AutoSize = true,
                MaximumSize = new Size(ContentWidth - Ui.S(left), 0),
                Tag = muted ? "muted" : null,
                Margin = Ui.Pad(left, top, 0, bottom),
            };
        }

        // A full-width button whose text wraps; its height follows the wrapped text.
        Button Choice(string text)
        {
            Size wrapped = TextRenderer.MeasureText(text, Font, new Size(ContentWidth - Ui.S(24), 0),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            return new Button
            {
                Text = text,
                AutoSize = false,
                UseMnemonic = false,
                Width = ContentWidth,
                Height = Math.Max(Ui.S(30), wrapped.Height + Ui.S(14)),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = Ui.Pad(8, 0, 8, 0),
                Margin = Ui.Pad(0, 4, 0, 0),
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                tips.Dispose();
                if (countdown != null) countdown.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        Action dismissAction;

        // Opened from the keyboard shortcut: the panel takes the focus (it never does on its own), so Tab, Enter and
        // a screen reader work in it. Escape then closes it like "Later".
        public void TakeFocus()
        {
            Native.Activate(Handle);
            Control first = answerControls.FirstOrDefault(c => c.Enabled) ?? laterLink;
            if (first != null) first.Focus();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                if (dismissAction != null) dismissAction();
                Close();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Palette.StatusWarn, Ui.S(2)))
                e.Graphics.DrawRectangle(pen, 1, 1, Width - 2, Height - 2);
        }
    }
}
