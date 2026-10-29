using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace Semaphore
{
    // The statistics of the use: what it came to, what it went on, what made it dearer, which sessions, which days.
    // One window opened from the tray menu (and from the settings); the only choice in it is the period. On a
    // subscription the share of the weekly limit leads and the dollars at API prices stand next to it; with a sign-in
    // by API key there are only the dollars.
    sealed class StatsForm : Form
    {
        readonly TrayApp app;
        readonly PeriodTabs tabs;
        readonly Panel scroller;
        readonly StatsView view;
        readonly Timer timer = new Timer { Interval = 30000 };
        readonly Panel bottom;
        readonly Button close;

        // Like the settings, the window is per-monitor aware (TrayApp.ShowStats): on a monitor with another scale it is
        // not stretched by Windows but draws itself for that monitor. The factor is the monitor's DPI over the system's.
        internal static float DpiFactor = 1f;
        const int WM_DPICHANGED = 0x02E0;

        public StatsForm(TrayApp app, Icon icon)
        {
            this.app = app;
            Text = Loc.T("stats.title");
            Icon = icon;
            Font = Ui.Font(9f);
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            MinimizeBox = true;
            MaximizeBox = false;
            ClientSize = Ui.Fit(760, 760);
            MinimumSize = new Size(Ui.S(560), Ui.S(420));

            tabs = new PeriodTabs { Dock = DockStyle.Top, Height = Ui.S(46) };
            tabs.Changed += Refill;
            bottom = new Panel { Dock = DockStyle.Bottom, Height = Ui.S(50) };
            close = new Button { Text = Loc.T("btn.close"), AutoSize = true, Anchor = AnchorStyles.Right | AnchorStyles.Top };
            close.Click += (s, e) => Close();
            bottom.Controls.Add(close);
            bottom.Resize += (s, e) => PlaceClose();
            CancelButton = close;

            scroller = new Panel { Dock = DockStyle.Fill };
            // Up and down only: the content always takes the width of the window.
            scroller.HorizontalScroll.Maximum = 0;
            scroller.HorizontalScroll.Enabled = false;
            scroller.HorizontalScroll.Visible = false;
            scroller.AutoScroll = true;
            view = new StatsView(app) { Left = 0, Top = 0 };
            scroller.Controls.Add(view);
            scroller.Resize += (s, e) => FitView();
            Controls.Add(scroller);
            Controls.Add(bottom);
            Controls.Add(tabs);
            Theme.Apply(this);
            BackColor = Theme.Back;

            timer.Tick += (s, e) => Refill();
            Shown += (s, e) => { Refill(); timer.Start(); close.Focus(); };
            FormClosed += (s, e) => timer.Dispose();
        }

        void PlaceClose()
        {
            using (Ui.Use(DpiFactor))
                close.Location = new Point(bottom.ClientSize.Width - close.Width - Ui.S(16), (bottom.Height - close.Height) / 2);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            DpiFactor = 1f;
            int dpi = Native.WindowDpi(Handle);
            if (dpi > 0 && dpi != Ui.SystemDpi)
            {
                float ratio = dpi / (float)Ui.SystemDpi;
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

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        // On a monitor with another scale: the fonts and the fixed sizes for it, and the size Windows suggests.
        void ApplyDpi(int dpi, Rectangle bounds)
        {
            DpiFactor = dpi / (float)Ui.SystemDpi;
            using (Ui.Use(DpiFactor))
            {
                Font = Ui.Font(9f);
                tabs.Height = Ui.S(46);
                bottom.Height = Ui.S(50);
                MinimumSize = new Size(Ui.S(560), Ui.S(420));
            }
            Bounds = bounds;
            PlaceClose();
            FitView();
            Invalidate(true);
        }

        // The theme changed while the window is open: the controls take the new colours, the drawing uses them anyway.
        public void ReapplyTheme()
        {
            Theme.Apply(this);
            BackColor = Theme.Back;
            Invalidate(true);
        }

        // On the timer and on another period: the figures again, the place in the window and the selection kept.
        void Refill()
        {
            DateTime now = DateTime.UtcNow;
            DateTime from = tabs.Selected == 0 ? DateTime.Today.ToUniversalTime() : now.AddDays(tabs.Selected == 1 ? -7 : -30);
            view.Show(app.Usage.Report(from, now), tabs.Selected);
            FitView();
        }

        void FitView()
        {
            int width = scroller.ClientSize.Width;
            if (view.Width != width) view.Width = width;
            view.Height = view.Measure(width);
        }

        // "Today / 7 days / 30 days": tabs drawn in the colours of the theme, the chosen one bold and underlined.
        sealed class PeriodTabs : Control
        {
            public int Selected = 2;
            public event Action Changed;
            readonly string[] names = { Loc.T("stats.period.today"), Loc.T("stats.period.week"), Loc.T("stats.period.month") };
            Rectangle[] boxes = new Rectangle[0];

            public PeriodTabs()
            {
                DoubleBuffered = true;
                ResizeRedraw = true;
                SetStyle(ControlStyles.Selectable, true);
                TabStop = true;
                AccessibleRole = AccessibleRole.PageTabList;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                using (Ui.Use(DpiFactor)) PaintTabs(e);
            }

            void PaintTabs(PaintEventArgs e)
            {
                e.Graphics.Clear(Theme.Back);
                boxes = new Rectangle[names.Length];
                int x = Ui.S(16);
                using (var bold = new Font(Font, FontStyle.Bold))
                    for (int i = 0; i < names.Length; i++)
                    {
                        Font f = i == Selected ? bold : Font;
                        Size size = TextRenderer.MeasureText(names[i], bold);
                        var box = new Rectangle(x, Ui.S(8), size.Width + Ui.S(20), Height - Ui.S(12));
                        boxes[i] = box;
                        TextRenderer.DrawText(e.Graphics, names[i], f, box, i == Selected ? Theme.Fore : Theme.Muted,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                        if (i == Selected)
                            using (var b = new SolidBrush(Palette.AccentBar))
                                e.Graphics.FillRectangle(b, box.Left + Ui.S(6), box.Bottom - Ui.S(3), box.Width - Ui.S(12), Ui.S(3));
                        if (Focused && i == Selected) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(box, -2, -2));
                        x = box.Right + Ui.S(4);
                    }
                using (var pen = new Pen(Theme.Border))
                    e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                Focus();
                for (int i = 0; i < boxes.Length; i++) if (boxes[i].Contains(e.Location)) Choose(i);
            }

            protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Left || keyData == Keys.Right || base.IsInputKey(keyData); }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Left) Choose(Math.Max(0, Selected - 1));
                if (e.KeyCode == Keys.Right) Choose(Math.Min(names.Length - 1, Selected + 1));
            }

            protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
            protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

            void Choose(int i)
            {
                if (i == Selected) return;
                Selected = i;
                AccessibleName = names[i];
                Invalidate();
                var h = Changed;
                if (h != null) h();
            }
        }
    }

    // The content of the window, drawn in one piece: cards, bars, the table of sessions and the days.
    sealed class StatsView : Control
    {
        readonly TrayApp app;
        UsageReport report;
        int period;
        string selected;
        // Where things were drawn, for the mouse: the rows of sessions and the link to the prices.
        readonly List<KeyValuePair<Rectangle, string>> rows = new List<KeyValuePair<Rectangle, string>>();
        Rectangle pricesLink;
        readonly Dictionary<string, string> names = new Dictionary<string, string>();

        public StatsView(TrayApp app)
        {
            this.app = app;
            DoubleBuffered = true;
            ResizeRedraw = true;
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
        }

        public void Show(UsageReport r, int period)
        {
            report = r;
            this.period = period;
            names.Clear();
            Invalidate();
        }

        // The height the content needs at this width.
        public int Measure(int width)
        {
            using (Ui.Use(StatsForm.DpiFactor))
            using (Graphics g = CreateGraphics()) return Draw(g, width, false);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Back);
            using (Ui.Use(StatsForm.DpiFactor)) Draw(e.Graphics, Width, true);
        }

        // ---- the figures in words ----

        CultureInfo Culture
        {
            get
            {
                try { return CultureInfo.GetCultureInfo(Loc.Code ?? "en"); }
                catch { return CultureInfo.InvariantCulture; }
            }
        }

        bool Limit { get { return report != null && report.LimitKnown && report.Total.Weeks.HasValue; } }

        // The share of a part in the leading unit: the limit on a subscription, the dollars otherwise.
        double Share(UsageAmount a)
        {
            if (Limit) return report.Total.Weeks > 0 ? (a.Weeks ?? 0) / report.Total.Weeks.Value : 0;
            return report.Total.Usd > 0 ? a.Usd / report.Total.Usd : 0;
        }

        string Percent(double share) { return Math.Round(100 * share).ToString("0", Culture) + "%"; }

        string WeekPercent(double? weeks) { return weeks.HasValue ? "≈" + Math.Round(100 * weeks.Value).ToString("0", Culture) + "%" : "—"; }

        // "≈ 14% of the week" for a day, "≈ 2.4 weeks of the limit" for longer.
        string LimitTotal(double weeks)
        {
            return period == 0 || weeks < 1
                ? Loc.T("stats.limit.percent", Math.Round(100 * weeks).ToString("0", Culture))
                : Loc.T("stats.limit.weeks", weeks.ToString("0.0", Culture));
        }

        string SessionName(UsageSessionRow row)
        {
            string n;
            if (!names.TryGetValue(row.Session, out n)) names[row.Session] = n = app.StatsSessionName(row.Session, row.Path);
            return n;
        }

        // ---- drawing (and measuring: the same pass without painting) ----

        int Draw(Graphics g, int width, bool paint)
        {
            // The places for the mouse are those of the last painting, not of a measuring pass.
            if (paint) { rows.Clear(); pricesLink = Rectangle.Empty; }
            int margin = Ui.S(16), pad = Ui.S(14);
            int y = Ui.S(12);
            if (report == null) return y;
            var c = new Painter(g, Font, paint);
            int inner = width - 2 * margin - 2 * pad;
            int left = margin + pad;
            if (report.Total.Count == 0)
            {
                y = c.Card(margin, y, width - 2 * margin, pad, yy => c.Text(Loc.T("stats.empty"), left, yy, inner, Theme.Muted));
                return y + margin;
            }

            // The total.
            y = c.Card(margin, y, width - 2 * margin, pad, yy =>
            {
                yy = c.Text(Loc.T("stats.total"), left, yy, inner, Theme.Fore, c.Header) + Ui.S(6);
                string lead = Limit ? LimitTotal(report.Total.Weeks.Value) : ApiPrices.Dollars(report.Total.Usd, Culture) + " " + Loc.T("stats.at.api");
                yy = c.Text(lead, left, yy, inner, Theme.Fore, c.Big);
                if (Limit) yy = c.Text(ApiPrices.Dollars(report.Total.Usd, Culture) + " " + Loc.T("stats.at.api"), left, yy + Ui.S(2), inner, Theme.Fore);
                return c.Text(Loc.T("stats.total.line", ApiPrices.Tokens(report.Total.Tokens, Culture), report.Total.Count.ToString("N0", Culture),
                    ApiPrices.Dollars(report.Total.Usd / Math.Max(1, report.Total.Count), Culture)), left, yy + Ui.S(2), inner, Theme.Muted);
            });

            // What it went on.
            y = c.Card(margin, y, width - 2 * margin, pad, yy =>
            {
                yy = c.Text(Loc.T("stats.parts"), left, yy, inner, Theme.Fore, c.Header) + Ui.S(4);
                yy = Captions(c, left, yy, inner);
                yy = Bar(c, Loc.T("stats.part.read"), report.Reading, left, yy, inner);
                yy = Bar(c, Loc.T("stats.part.write"), report.Writing, left, yy, inner);
                return Bar(c, Loc.T("stats.part.reply"), report.Replies, left, yy, inner);
            });

            // What made it dearer: only what is a noticeable share, each with what to do about it.
            y = c.Card(margin, y, width - 2 * margin, pad, yy =>
            {
                yy = c.Text(Loc.T("stats.dearer"), left, yy, inner, Theme.Fore, c.Header) + Ui.S(4);
                bool any = false;
                if (Share(report.LargeContext) >= 0.1)
                {
                    if (!any) yy = Captions(c, left, yy, inner);
                    any = true;
                    yy = Bar(c, Loc.T("stats.dear.large"), report.LargeContext, left, yy, inner);
                    yy = c.Text(Loc.T("stats.dear.large.tip"), left + Ui.S(12), yy, inner - Ui.S(12), Theme.Muted) + Ui.S(6);
                }
                if (Share(report.ColdStarts) >= 0.1)
                {
                    if (!any) yy = Captions(c, left, yy, inner);
                    any = true;
                    yy = Bar(c, Loc.T("stats.dear.cold", report.ColdStarts.Count.ToString("N0", Culture)), report.ColdStarts, left, yy, inner);
                    yy = c.Text(Loc.T("stats.dear.cold.tip"), left + Ui.S(12), yy, inner - Ui.S(12), Theme.Muted) + Ui.S(6);
                }
                if (!any) yy = c.Text(Loc.T("stats.dear.none"), left, yy, inner, Theme.Muted) + Ui.S(4);
                return c.Text(Loc.T("stats.compactions", report.Compactions.ToString("N0", Culture)), left, yy, inner, Theme.Muted);
            });

            // The sessions.
            y = c.Card(margin, y, width - 2 * margin, pad, yy =>
            {
                yy = c.Text(Loc.T("stats.sessions"), left, yy, inner, Theme.Fore, c.Header) + Ui.S(4);
                return Sessions(c, left, yy, inner);
            });

            // The days, for a period longer than one.
            if (period > 0 && report.Days.Count > 0)
                y = c.Card(margin, y, width - 2 * margin, pad, yy =>
                {
                    yy = c.Text(Loc.T("stats.days"), left, yy, inner, Theme.Fore, c.Header) + Ui.S(4);
                    return Days(c, left, yy, inner);
                });

            // How sure the figures are, and the prices.
            y += Ui.S(2);
            y = c.Text(Loc.T(Limit ? "stats.accuracy" : "stats.accuracy.api"), margin + Ui.S(4), y, width - 2 * margin, Theme.Muted) + Ui.S(4);
            if (report.Unpriced.Count > 0)
                y = c.Text(Loc.T("cost.menu.unpriced", string.Join(", ", report.Unpriced)), margin + Ui.S(4), y, width - 2 * margin, Palette.StatusWarn) + Ui.S(4);
            Size link = TextRenderer.MeasureText(Loc.T("stats.prices"), Font);
            if (paint) pricesLink = new Rectangle(margin + Ui.S(4), y, link.Width, link.Height);
            c.Text(Loc.T("stats.prices"), margin + Ui.S(4), y, link.Width + 2, Palette.Link, c.Underline);
            return y + link.Height + margin;
        }

        // The column headings of the bars: "≈ limit" over the share of the limit, "$ API" over the share of the dollars
        // (the dollars themselves need no heading).
        int Captions(Painter c, int left, int y, int inner)
        {
            int[] cols = Columns(left, inner);
            if (Limit) c.Right(Loc.T("stats.col.limit"), cols[0], y, Theme.Muted);
            c.Right(Loc.T("stats.col.api"), cols[1], y, Theme.Muted);
            return y + Font.Height + Ui.S(2);
        }

        // The right edges of the number columns: the share of the limit, the share of the dollars, the dollars.
        int[] Columns(int left, int inner)
        {
            int right = left + inner;
            return new[] { right - Ui.S(150), right - Ui.S(90), right };
        }

        int Bar(Painter c, string label, UsageAmount a, int left, int y, int inner)
        {
            int[] cols = Columns(left, inner);
            int labelWidth = Math.Min(Ui.S(260), inner / 2 - Ui.S(20));
            int barLeft = left + labelWidth + Ui.S(8);
            int barRight = cols[0] - Ui.S(60);
            int h = Math.Max(c.Measure(label, labelWidth), Font.Height);
            c.Text(label, left, y, labelWidth, Theme.Fore);
            if (barRight > barLeft)
            {
                int length = (int)Math.Round((barRight - barLeft) * Math.Max(0, Math.Min(1, Share(a))));
                c.Fill(barLeft, y + Ui.S(3), barRight - barLeft, Font.Height - Ui.S(6), Theme.Hover);
                c.Fill(barLeft, y + Ui.S(3), length, Font.Height - Ui.S(6), Palette.AccentBar);
            }
            if (Limit) c.Right(Percent(report.Total.Weeks > 0 ? (a.Weeks ?? 0) / report.Total.Weeks.Value : 0), cols[0], y, Theme.Fore);
            c.Right(Percent(report.Total.Usd > 0 ? a.Usd / report.Total.Usd : 0), cols[1], y, Theme.Fore);
            c.Right(ApiPrices.Dollars(a.Usd, Culture), cols[2], y, Theme.Fore);
            return y + h + Ui.S(6);
        }

        int Sessions(Painter c, int left, int y, int inner)
        {
            // Session | Model | Steps | Largest context | ≈ week | $ API: the numbers take what their heading or their
            // widest value needs, the name of the session the rest.
            var shown = report.Sessions.Take(50).ToList();
            Func<string, IEnumerable<string>, int> need = (head, values) =>
                values.Concat(new[] { head }).Max(t => c.Width(t)) + Ui.S(14);
            int wModel = need(Loc.T("stats.col.model"), shown.Select(r => Model(r.Model)));
            int wSteps = need(Loc.T("stats.col.steps"), shown.Select(r => r.Steps.ToString("N0", Culture)));
            int wContext = need(Loc.T("stats.col.context"), shown.Select(r => ApiPrices.Tokens(r.MaxContext, Culture)));
            int wWeek = Limit ? need(Loc.T("stats.col.week"), shown.Select(r => WeekPercent(r.Amount.Weeks))) : 0;
            int wUsd = need(Loc.T("stats.col.api"), shown.Select(r => ApiPrices.Dollars(r.Amount.Usd, Culture)));
            int right = left + inner;
            int e5 = right, e4 = e5 - wUsd, e3 = e4 - wWeek, e2 = e3 - wContext, e1 = e2 - wSteps, e0 = e1 - wModel;
            int[] edges = { e0, e1, e2, e3, e4, e5 };
            int nameWidth = edges[0] - left - Ui.S(10);
            c.Text(Loc.T("stats.col.session"), left, y, nameWidth, Theme.Muted);
            c.Text(Loc.T("stats.col.model"), edges[0], y, edges[1] - edges[0], Theme.Muted);
            c.Right(Loc.T("stats.col.steps"), edges[2], y, Theme.Muted);
            c.Right(Loc.T("stats.col.context"), edges[3], y, Theme.Muted);
            if (Limit) c.Right(Loc.T("stats.col.week"), edges[4], y, Theme.Muted);
            c.Right(Loc.T("stats.col.api"), edges[5], y, Theme.Muted);
            y += Font.Height + Ui.S(4);
            int rowHeight = Font.Height + Ui.S(8);
            foreach (UsageSessionRow row in shown)
            {
                var box = new Rectangle(left - Ui.S(6), y, inner + Ui.S(12), rowHeight);
                if (c.Painting) rows.Add(new KeyValuePair<Rectangle, string>(box, row.Session));
                if (row.Session == selected) c.Fill(box.X, box.Y, box.Width, box.Height, Theme.Hover);
                int ty = y + Ui.S(4);
                c.Line(SessionName(row), left, ty, nameWidth, Theme.Fore);
                c.Line(Model(row.Model), edges[0], ty, edges[1] - edges[0] - Ui.S(6), Theme.Fore);
                c.Right(row.Steps.ToString("N0", Culture), edges[2], ty, Theme.Fore);
                c.Right(ApiPrices.Tokens(row.MaxContext, Culture), edges[3], ty, Theme.Fore);
                if (Limit) c.Right(WeekPercent(row.Amount.Weeks), edges[4], ty, Theme.Fore);
                c.Right(ApiPrices.Dollars(row.Amount.Usd, Culture), edges[5], ty, Theme.Fore);
                y += rowHeight;
            }
            if (report.Sessions.Count > 50)
                y = c.Text(Loc.T("stats.sessions.more", report.Sessions.Count - 50), left, y + Ui.S(4), inner, Theme.Muted);
            return c.Text(Loc.T("stats.sessions.hint"), left, y + Ui.S(6), inner, Theme.Muted);
        }

        // "claude-opus-5-5" → "Opus 5.5".
        static string Model(string id)
        {
            string[] parts = (id ?? "").Split('-');
            if (parts.Length < 3 || parts[0] != "claude") return id;
            var digits = parts.Skip(2).TakeWhile(p => p.Length <= 2 && p.All(char.IsDigit)).ToList();
            return char.ToUpperInvariant(parts[1][0]) + parts[1].Substring(1) + (digits.Count > 0 ? " " + string.Join(".", digits) : "");
        }

        int Days(Painter c, int left, int y, int inner)
        {
            int count = period == 1 ? 7 : 30;
            DateTime last = DateTime.Today, first = last.AddDays(-(count - 1));
            var values = new double[count];
            foreach (var d in report.Days)
            {
                int i = (int)(d.Key - first).TotalDays;
                if (i >= 0 && i < count) values[i] = Limit ? d.Value.Weeks ?? 0 : d.Value.Usd;
            }
            double max = Math.Max(values.Max(), 1e-9);
            string top = Limit ? WeekPercent(max) : ApiPrices.Dollars(max, Culture);
            y = c.Text(Loc.T("stats.days.max", top), left, y, inner, Theme.Muted) + Ui.S(4);
            int chart = Ui.S(110);
            int slot = inner / count;
            for (int i = 0; i < count; i++)
            {
                int h = (int)Math.Round(chart * values[i] / max);
                if (values[i] > 0 && h < 2) h = 2;
                c.Fill(left + i * slot + Ui.S(1), y + chart - h, Math.Max(1, slot - Ui.S(3)), h, Palette.AccentBar);
            }
            c.Fill(left, y + chart, slot * count, 1, Theme.Border);
            y += chart + Ui.S(4);
            // A date under the first bar of each week (every day for a week).
            for (int i = 0; i < count; i += period == 1 ? 1 : 7)
                c.Text(first.AddDays(i).ToString("dd.MM", Culture), left + i * slot, y, Ui.S(60), Theme.Muted);
            return y + Font.Height;
        }

        // ---- the mouse and the keys ----

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Cursor = pricesLink.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (pricesLink.Contains(e.Location)) { app.ShowPriceSettings(); return; }
            foreach (var r in rows)
                if (r.Key.Contains(e.Location)) { selected = r.Value; Invalidate(); }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            foreach (var r in rows)
                if (r.Key.Contains(e.Location)) app.FocusSessionById(r.Value);
        }

        protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Up || keyData == Keys.Down || base.IsInputKey(keyData); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (rows.Count == 0) return;
            int i = rows.FindIndex(r => r.Value == selected);
            if (e.KeyCode == Keys.Down) i = Math.Min(rows.Count - 1, i + 1);
            else if (e.KeyCode == Keys.Up) i = Math.Max(0, i - 1);
            else if (e.KeyCode == Keys.Enter && i >= 0) { app.FocusSessionById(selected); return; }
            else return;
            selected = rows[Math.Max(0, i)].Value;
            Invalidate();
        }

        // Text, rectangles and cards, measured always and painted only on the painting pass.
        sealed class Painter
        {
            readonly Graphics g;
            readonly Font font;
            bool paint;
            public readonly Font Header, Big, Underline;

            public bool Painting { get { return paint; } }

            public Painter(Graphics g, Font font, bool paint)
            {
                this.g = g;
                this.font = font;
                this.paint = paint;
                Header = new Font(font.FontFamily, font.Size + 1.5f, FontStyle.Bold);
                Big = new Font(font.FontFamily, font.Size + 5f, FontStyle.Bold);
                Underline = new Font(font, FontStyle.Underline);
            }

            const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;

            public int Width(string text)
            {
                return TextRenderer.MeasureText(g, text, font, new Size(int.MaxValue, 0), TextFormatFlags.NoPrefix).Width;
            }

            public int Measure(string text, int width, Font f = null)
            {
                return TextRenderer.MeasureText(g, text, f ?? font, new Size(Math.Max(1, width), 0), Wrap).Height;
            }

            // Wrapped text; returns where the next line starts.
            public int Text(string text, int x, int y, int width, Color color, Font f = null)
            {
                int h = Measure(text, width, f);
                if (paint) TextRenderer.DrawText(g, text, f ?? font, new Rectangle(x, y, width, h), color, Wrap);
                return y + h;
            }

            // One line, cut with "…" when it does not fit.
            public void Line(string text, int x, int y, int width, Color color)
            {
                if (paint) TextRenderer.DrawText(g, text, font, new Rectangle(x, y, Math.Max(1, width), font.Height), color,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            }

            // Text that ends at "right".
            public void Right(string text, int right, int y, Color color)
            {
                if (!paint) return;
                Size s = TextRenderer.MeasureText(g, text, font);
                TextRenderer.DrawText(g, text, font, new Point(right - s.Width, y), color, TextFormatFlags.NoPrefix);
            }

            public void Fill(int x, int y, int w, int h, Color color)
            {
                if (!paint || w <= 0 || h <= 0) return;
                using (var b = new SolidBrush(color)) g.FillRectangle(b, x, y, w, h);
            }

            // A card as in the settings: a frame around content laid out by "body" (which returns where it ended).
            public int Card(int x, int y, int width, int pad, Func<int, int> body)
            {
                // Measured first, so that the surface of the card is under its content.
                bool painting = paint;
                paint = false;
                int end = body(y + pad) + pad;
                paint = painting;
                if (paint)
                {
                    Fill(x, y, width, end - y, Theme.Surface);
                    body(y + pad);
                    using (var pen = new Pen(Theme.Border)) g.DrawRectangle(pen, x, y, width - 1, end - y - 1);
                }
                return end + Ui.S(10);
            }
        }
    }
}
