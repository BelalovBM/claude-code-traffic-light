using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Semaphore
{
    sealed partial class SettingsForm : Form
    {
        sealed class Choice
        {
            public string Code, Text;
            public override string ToString() { return Text; }
        }

        // Width of the column that controls are currently laid out in; TwoColumns narrows it.
        int RowWidth = Ui.S(740);
        readonly TrayApp app;
        int page;
        Config cfg { get { return app.Config; } }

        static readonly string[] PageKeys = { "nav.general", "nav.notifications", "nav.channels", "nav.approve", "nav.connection", "nav.about" };

        public SettingsForm(TrayApp app, Icon icon)
        {
            this.app = app;
            Icon = icon;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = Ui.Font(9f);
            ClientSize = FitContent();
            MinimumSize = new Size(Ui.S(760), Ui.S(520));
            BuildUI();
            live.Tick += (s, e) => { if (refresher != null && !IsDisposed) refresher(); };
            live.Start();

            // The layout depends on the width (one or two columns), so it is redone when the size settles.
            ResizeEnd += (s, e) => Rebuild();
            Resize += (s, e) =>
            {
                if (WindowState != lastState) { lastState = WindowState; Rebuild(); }
            };
        }

        // The window is created per-monitor aware (TrayApp.ShowSettings): on a monitor whose scale differs from the
        // system's, Windows does not stretch it, and it builds its controls for that monitor instead. dpiFactor is
        // the monitor's DPI divided by the system's.
        float dpiFactor = 1f;
        const int WM_DPICHANGED = 0x02E0;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int dpi = Native.WindowDpi(Handle);
            if (dpi > 0 && dpi != Ui.SystemDpi)
            {
                float ratio = dpi / (float)Ui.SystemDpi / dpiFactor;
                ApplyDpi(dpi, new Rectangle(Left, Top, (int)(Width * ratio), (int)(Height * ratio)));
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DPICHANGED)
            {
                var r = (RECT)System.Runtime.InteropServices.Marshal.PtrToStructure(m.LParam, typeof(RECT));
                ApplyDpi((int)((long)m.WParam & 0xFFFF), Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom));
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        // Moved to a monitor with another scale: take the size Windows suggests and build the page again for it.
        void ApplyDpi(int dpi, Rectangle bounds)
        {
            float factor = dpi / (float)Ui.SystemDpi;
            bool changed = Math.Abs(factor - dpiFactor) > 0.01f;
            dpiFactor = factor;
            using (Ui.Use(dpiFactor))
                MinimumSize = new Size(Ui.S(760), Ui.S(520));
            Bounds = bounds;
            if (changed)
            {
                Log.Write("Settings window moved to a monitor at " + (dpi * 100 / 96) + "%, rebuilt for it");
                Rebuild();
            }
        }

        // Widest the controls get; a wider window only adds empty space on the right.
        const int ContentWidth = 720;
        const int NavWidth = 170;

        // The window is as large as its tallest page needs, so no page scrolls when the screen allows it.
        Size FitContent()
        {
            int tallest = 0;
            int savedPage = page;
            Action savedRefresher = refresher;
            RowWidth = Ui.S(ContentWidth);
            for (int i = 0; i < PageKeys.Length; i++)
            {
                using (var probe = NewPagePanel())
                {
                    page = i;
                    // Outside the window the panel would take the default font, smaller than the window's.
                    probe.Font = Font;
                    probe.Dock = DockStyle.None;
                    probe.AutoScroll = false;
                    probe.Size = new Size(RowWidth + Ui.S(40), Ui.S(4000));
                    BuildPage(probe);
                    Theme.Apply(probe);
                    probe.PerformLayout();
                    int bottom = 0;
                    foreach (Control c in probe.Controls) bottom = Math.Max(bottom, c.Bottom + c.Margin.Bottom);
                    tallest = Math.Max(tallest, bottom + probe.Padding.Bottom);
                }
            }
            page = savedPage;
            refresher = savedRefresher;
            pending.Clear();
            Rectangle area = Native.TrayScreen().WorkingArea;
            int width = Ui.S(NavWidth + 32 + ContentWidth + 4) + SystemInformation.VerticalScrollBarWidth;
            int height = tallest + Ui.S(8);
            return new Size(Math.Min(width, area.Width - Ui.S(40)), Math.Min(height, area.Height - Ui.S(80)));
        }

        FlowLayoutPanel NewPagePanel()
        {
            return new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = Ui.Pad(16, 6, 16, 8),
                TabIndex = 1,
            };
        }

        void BuildPage(FlowLayoutPanel panel)
        {
            switch (page)
            {
                case 0: GeneralPage(panel); break;
                case 1: NotificationsPage(panel); break;
                case 2: PhonePage(panel); break;
                case 3: ApprovePage(panel); break;
                case 4: ConnectionPage(panel); break;
                default: AboutPage(panel); break;
            }
        }

        FormWindowState lastState = FormWindowState.Normal;
        ListBox nav;
        FlowLayoutPanel content;
        // Called by the timer to bring the visible page up to date without rebuilding it.
        Action refresher;
        // Text fields that were edited but not yet saved; written on a pause, on leaving the field and on close.
        readonly System.Collections.Generic.List<Action> pending = new System.Collections.Generic.List<Action>();
        readonly Timer live = new Timer { Interval = 1500 };

        void Rebuild()
        {
            if (IsDisposed) return;
            FlushPending();
            BuildUI();
        }

        // The theme changed while the window is open.
        public void ReapplyTheme()
        {
            Rebuild();
        }

        public void GoTo(int index)
        {
            page = Math.Max(0, Math.Min(PageKeys.Length - 1, index));
            FlushPending();
            if (nav != null && nav.SelectedIndex != page) nav.SelectedIndex = page;
            ShowPage();
        }

        // Escape closes the window, except when it only has to fold an open drop-down list.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            var combo = ActiveControl as ComboBox;
            if (keyData == Keys.Escape && (combo == null || !combo.DroppedDown))
            {
                Close();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            FlushPending();
            live.Stop();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) live.Dispose();
            base.Dispose(disposing);
        }

        void FlushPending()
        {
            var all = pending.ToArray();
            pending.Clear();
            foreach (Action a in all) a();
        }

        void BuildUI()
        {
            using (Ui.Use(dpiFactor)) BuildUICore();
        }

        void BuildUICore()
        {
            SuspendLayout();
            Font = Ui.Font(9f);
            string focusName = ActiveControl == null ? null : ActiveControl.Name;
            foreach (Control c in Controls.Cast<Control>().ToArray()) { Controls.Remove(c); c.Dispose(); }
            Text = Loc.T("settings.title");

            nav = new ListBox
            {
                Dock = DockStyle.Left,
                Width = Ui.S(NavWidth),
                BorderStyle = BorderStyle.None,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = Ui.S(36),
                IntegralHeight = false,
                Name = "nav",
                AccessibleName = Loc.T("settings.title"),
                TabIndex = 0,
            };
            foreach (string k in PageKeys) nav.Items.Add(Loc.T(k));
            nav.DrawItem += DrawNavItem;
            nav.GotFocus += (s, e) => nav.Invalidate();
            nav.LostFocus += (s, e) => nav.Invalidate();
            nav.SelectedIndex = Math.Min(page, PageKeys.Length - 1);
            nav.SelectedIndexChanged += (s, e) =>
            {
                if (nav.SelectedIndex < 0 || nav.SelectedIndex == page) return;
                page = nav.SelectedIndex;
                FlushPending();
                ShowPage();
            };

            Controls.Add(nav);

            ShowPage();
            nav.BackColor = Theme.Surface;
            ResumeLayout();

            if (focusName != null)
            {
                Control again = FindByName(this, focusName);
                if (again != null) again.Focus();
            }
        }

        // Replaces only the page, so the navigation keeps the keyboard focus while arrows are pressed.
        void ShowPage()
        {
            using (Ui.Use(dpiFactor)) ShowPageCore();
        }

        void ShowPageCore()
        {
            SuspendLayout();
            if (content != null) { Controls.Remove(content); content.Dispose(); }
            refresher = null;
            // What is left of the window for controls after the navigation, the page padding and a scroll bar.
            RowWidth = Math.Max(Ui.S(300), Math.Min(Ui.S(ContentWidth),
                ClientSize.Width - Ui.S(NavWidth) - Ui.S(32) - SystemInformation.VerticalScrollBarWidth - Ui.S(4)));
            content = NewPagePanel();
            BuildPage(content);
            Controls.Add(content);
            Controls.SetChildIndex(content, 0);
            Describe(content);
            Theme.Apply(this);
            nav.BackColor = Theme.Surface;
            ResumeLayout();
        }

        static Control FindByName(Control root, string name)
        {
            foreach (Control c in root.Controls)
            {
                if (c.Name == name && c.TabStop) return c;
                Control inner = FindByName(c, name);
                if (inner != null) return inner;
            }
            return null;
        }

        // Screen readers announce AccessibleName; the same name finds the control again after a rebuild.
        static void Describe(Control root)
        {
            foreach (Control c in root.Controls)
            {
                if (string.IsNullOrEmpty(c.AccessibleName) && (c is CheckBox || c is Button || c is LinkLabel))
                    c.AccessibleName = c.Text;
                if (string.IsNullOrEmpty(c.Name) && !string.IsNullOrEmpty(c.AccessibleName)) c.Name = c.AccessibleName;
                Describe(c);
            }
        }

        void DrawNavItem(object sender, DrawItemEventArgs e)
        {
            using (Ui.Use(dpiFactor)) DrawNavItemCore(sender, e);
        }

        void DrawNavItemCore(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var list = (ListBox)sender;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (var back = new SolidBrush(selected ? Theme.Hover : Theme.Surface))
                e.Graphics.FillRectangle(back, e.Bounds);
            if (selected)
                using (var bar = new SolidBrush(Palette.AccentBar))
                    e.Graphics.FillRectangle(bar, e.Bounds.Left, e.Bounds.Top, Ui.S(4), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, list.Items[e.Index].ToString(), Font,
                new Rectangle(e.Bounds.Left + Ui.S(16), e.Bounds.Top, e.Bounds.Width - Ui.S(16), e.Bounds.Height),
                Theme.Fore, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            if (selected && list.Focused)
            {
                Rectangle r = Rectangle.Inflate(e.Bounds, -Ui.S(2), -Ui.S(2));
                ControlPaint.DrawFocusRectangle(e.Graphics, r, Theme.Fore, Theme.Hover);
            }
        }
    }
}
