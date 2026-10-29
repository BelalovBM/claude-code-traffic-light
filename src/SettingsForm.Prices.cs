using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace Semaphore
{
    sealed partial class SettingsForm
    {
        // The use at API prices: whether the menu shows it, the prices of the models in the transcripts (each with where
        // it came from: the program, LiteLLM or you), loading them from LiteLLM, and the way to the statistics.
        void CostSection(FlowLayoutPanel p)
        {
            Section(p, Loc.T("sec.cost"), c =>
            {
                c.Controls.Add(Check(Loc.T("chk.cost.show"), cfg.CostShow, v => cfg.CostShow = v));
                // The prices in a part of the card that scrolls by itself: it keeps the height it has with More closed,
                // so opening More (or a longer result) does not lengthen the page. Room for the scroll bar is kept
                // from the start, so nothing moves when it appears.
                int full = RowWidth;
                var area = new Panel { AutoScroll = true, Width = full, Margin = Ui.Pad(0) };
                area.HorizontalScroll.Maximum = 0;
                area.HorizontalScroll.Enabled = false;
                area.HorizontalScroll.Visible = false;
                RowWidth = full - SystemInformation.VerticalScrollBarWidth - Ui.S(4);
                var inner = Column(RowWidth, Ui.Pad(0));
                area.Controls.Add(inner);
                c.Controls.Add(area);
                c = inner;
                c.Controls.Add(Header(Loc.T("prices.title")));
                var table = Column(RowWidth, Ui.Pad(0));
                c.Controls.Add(table);
                var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = Ui.Pad(0, 6, 0, 0), MaximumSize = new Size(RowWidth, 0) };
                var load = new Button { Text = Loc.T("btn.prices.load"), AutoSize = true, Margin = Ui.Pad(3, 0, 8, 3) };
                var reset = new Button { Text = Loc.T("btn.prices.reset"), AutoSize = true, Margin = Ui.Pad(0, 0, 8, 3) };
                buttons.Controls.Add(load);
                buttons.Controls.Add(reset);
                c.Controls.Add(buttons);
                Label result = ResultLabel();
                c.Controls.Add(result);

                // The rows are built again after a change of the prices, later than the card: with the card's width
                // taken now, and in the colours of the card.
                int width = RowWidth;
                Action fill = null;
                fill = () =>
                {
                    table.SuspendLayout();
                    foreach (Control old in table.Controls.Cast<Control>().ToList()) old.Dispose();
                    table.Controls.Clear();
                    int saved = RowWidth;
                    RowWidth = width;
                    try { BuildPriceRows(table, () => reset.Enabled = ApiPrices.AnyKept); }
                    finally { RowWidth = saved; }
                    table.ResumeLayout();
                    reset.Enabled = ApiPrices.AnyKept;
                    Control card = table;
                    while (card != null && !(card is CardPanel)) card = card.Parent;
                    if (card != null) Theme.Apply(card);
                };
                fill();
                reset.Click += (s, e) =>
                {
                    ApiPrices.Clear();
                    fill();
                    SetResult(result, Loc.T("prices.reset.done"), Palette.StatusOk);
                };
                load.Click += (s, e) =>
                {
                    load.Enabled = false;
                    SetResult(result, Loc.T("prices.load.busy"), Theme.Muted);
                    ApiPrices.LoadLiteLlm((prices, error) => OnUi(() =>
                    {
                        load.Enabled = true;
                        if (error != null) { SetResult(result, Loc.T("prices.load.failed", error), Palette.StatusError); return; }
                        DateTime when = DateTime.Now;
                        List<ApiPrices.Change> changes = ApiPrices.Compare(prices);
                        if (changes.Count == 0)
                        {
                            ApiPrices.Apply(prices, new string[0], when);
                            fill();
                            SetResult(result, Loc.T("prices.load.same"), Palette.StatusOk);
                            return;
                        }
                        using (var d = new PricesDialog(changes, when, Icon))
                        {
                            if (d.ShowDialog(this) != DialogResult.OK) { SetResult(result, "", Theme.Muted); return; }
                            ApiPrices.Apply(prices, d.Replace, when);
                        }
                        fill();
                        SetResult(result, Loc.T("prices.load.done", changes.Count), Palette.StatusOk);
                    }));
                };

                // Where the loaded prices come from and where to check them, under More: the card stays short.
                Control more = Details(c, Note(Loc.T("prices.source")),
                    WebLink(ApiPrices.LiteLlmPage.Replace("https://", ""), ApiPrices.LiteLlmPage),
                    Note(Loc.T("prices.check")),
                    WebLink(ApiPrices.AnthropicPage.Replace("https://", ""), ApiPrices.AnthropicPage));
                RowWidth = full;

                // The height of the part: what it needs with More closed, again after the rows change. Opened, More
                // is scrolled into sight; closed, the part goes back to its top.
                Action fit = () =>
                {
                    if (!more.Visible) area.Height = inner.GetPreferredSize(Size.Empty).Height;
                };
                fit();
                inner.SizeChanged += (s, e) => fit();
                more.VisibleChanged += (s, e) =>
                {
                    if (more.Visible) BeginInvoke((Action)(() => area.AutoScrollPosition = new Point(0, inner.Height)));
                    else area.AutoScrollPosition = Point.Empty;
                };
                if (Theme.Dark) area.HandleCreated += (s, e) => Native.SetDarkScrollBars(area.Handle);
                ((FlowLayoutPanel)area.Parent).Controls.Add(StatsLink());
            });
        }

        // "Usage statistics": the same window from the card of the limits and from this one.
        Control StatsLink()
        {
            var link = new LinkLabel
            {
                Text = Loc.T("link.stats"),
                AutoSize = true,
                LinkColor = Palette.Link,
                ActiveLinkColor = Palette.LinkActive,
                Margin = Ui.Pad(3, 10, 3, 4),
            };
            link.LinkClicked += (s, e) => app.ShowStats();
            return link;
        }

        // One row per model seen in the transcripts of the last 30 days (and each one with a price typed in): the
        // three prices that matter, editable, and where they came from.
        void BuildPriceRows(FlowLayoutPanel table, Action changed)
        {
            var kept = ApiPrices.Kept();
            var models = app.Usage.SeenModels()
                .Concat(kept.Where(kv => kv.Value.Source == ApiPrices.FromYou).Select(kv => kv.Key))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(m => m, StringComparer.Ordinal).ToList();
            if (models.Count == 0) { table.Controls.Add(Note(Loc.T("prices.none"))); return; }
            // The price columns are as wide as their longest heading ("Cache read"), at least as a number needs.
            int box = new[] { "prices.col.input", "prices.col.output", "prices.col.read" }
                .Max(k => TextRenderer.MeasureText(Loc.T(k), Font).Width) + Ui.S(6);
            box = Math.Max(Ui.S(62), box);
            int from = Ui.S(110);
            int name = RowWidth - 3 * (box + Ui.S(8)) - from - Ui.S(8);
            table.Controls.Add(PriceRow(name, box, from, new Label { Text = Loc.T("prices.col.model"), Tag = "muted" },
                Loc.T("prices.col.input"), Loc.T("prices.col.output"), Loc.T("prices.col.read"), Loc.T("prices.col.from")));
            CultureInfo culture = Loc.Culture;
            foreach (string model in models)
            {
                ApiPrices.Price price = ApiPrices.For(model);
                var input = new TextBox { Width = box, TextAlign = HorizontalAlignment.Right };
                var output = new TextBox { Width = box, TextAlign = HorizontalAlignment.Right };
                var read = new TextBox { Width = box, TextAlign = HorizontalAlignment.Right };
                var origin = new Label { AutoSize = false, Width = from, Height = Ui.S(22), TextAlign = ContentAlignment.MiddleLeft, Tag = "keep" };
                var label = new Label { Text = model, AutoEllipsis = true, Tag = "keep" };
                if (price != null)
                {
                    input.Text = price.Input.ToString("0.###", culture);
                    output.Text = price.Output.ToString("0.###", culture);
                    read.Text = price.Reading.ToString("0.###", culture);
                }
                Action show = () =>
                {
                    ApiPrices.Price now = ApiPrices.For(model);
                    bool own = now != null && now.Source == ApiPrices.FromYou;
                    origin.Text = Origin(now);
                    origin.ForeColor = now == null ? Palette.StatusWarn : Theme.Muted;
                    label.ForeColor = now == null ? Palette.StatusWarn : Theme.Fore;
                    Font f = own ? new Font(Font, FontStyle.Bold) : Font;
                    input.Font = output.Font = read.Font = f;
                };
                show();
                Action save = () =>
                {
                    double? i = Number(input.Text), o = Number(output.Text), r = Number(read.Text);
                    input.ForeColor = i.HasValue && i > 0 ? Theme.Fore : Palette.StatusError;
                    output.ForeColor = o.HasValue && o > 0 ? Theme.Fore : Palette.StatusError;
                    bool readOk = read.Text.Trim().Length == 0 || r.HasValue;
                    read.ForeColor = readOk ? Theme.Fore : Palette.StatusError;
                    if (!(i > 0) || !(o > 0) || !readOk) return;
                    ApiPrices.Price now = ApiPrices.For(model);
                    var typed = new ApiPrices.Price { Input = i.Value, Output = o.Value, CacheRead = r, LongFrom = now != null ? now.LongFrom : 0, LongInput = now != null ? now.LongInput : null, LongOutput = now != null ? now.LongOutput : null };
                    if (now != null && now.SameAs(typed)) return;
                    ApiPrices.SetOwn(model, i.Value, o.Value, r);
                    show();
                    changed();
                };
                Autosave(input, _ => save());
                Autosave(output, _ => save());
                Autosave(read, _ => save());
                table.Controls.Add(PriceRow(name, box, from, label, input, output, read, origin));
            }
            table.Controls.Add(Note(Loc.T("prices.unit")));
        }

        // "program", "LiteLLM, 29.10", "you", or "no price".
        static string Origin(ApiPrices.Price p)
        {
            if (p == null) return Loc.T("prices.from.none");
            if (p.Source == ApiPrices.FromYou) return Loc.T("prices.from.you");
            if (p.Source == ApiPrices.FromLiteLlm)
            {
                DateTime d;
                return DateTime.TryParseExact(p.Date ?? "", "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)
                    ? Loc.T("prices.from.litellm.on", d.ToString("dd.MM", CultureInfo.InvariantCulture)) : Loc.T("prices.from.litellm");
            }
            return Loc.T("prices.from.program");
        }

        // A price as typed: either decimal separator, nothing else.
        static double? Number(string text)
        {
            double v;
            string t = (text ?? "").Trim().Replace(',', '.');
            return t.Length > 0 && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= 0 ? v : (double?)null;
        }

        Control PriceRow(int name, int box, int from, Control model, object input, object output, object read, object origin)
        {
            var row = new Panel { Width = RowWidth, Height = Ui.S(30), Margin = Ui.Pad(3, 1, 3, 1) };
            // The texts stand on the middle line of the fields.
            model.Left = 0; model.Top = Ui.S(4); model.Width = name; model.Height = Ui.S(24);
            var plain = model as Label;
            if (plain != null && !(input is string)) plain.TextAlign = ContentAlignment.MiddleLeft;
            row.Controls.Add(model);
            int x = name + Ui.S(8);
            foreach (object cell in new[] { input, output, read })
            {
                Control c = cell as Control ?? new Label { Text = (string)cell, Width = box, Height = Ui.S(22), TextAlign = ContentAlignment.BottomRight, Tag = "muted" };
                c.Left = x; c.Top = Ui.S(4);
                if (c is Label) c.Top = Ui.S(6);
                row.Controls.Add(c);
                x += box + Ui.S(8);
            }
            Control o = origin as Control ?? new Label { Text = (string)origin, Width = from, Height = Ui.S(22), TextAlign = ContentAlignment.BottomLeft, Tag = "muted" };
            o.Left = x; o.Top = origin is string ? Ui.S(6) : Ui.S(4);
            if (!(origin is string)) o.Height = Ui.S(24);
            row.Controls.Add(o);
            return row;
        }
    }

    // What loading from LiteLLM would change, shown before anything changes: new models, changed prices, and the
    // prices you typed in, each replaced only if ticked. The source is named with its address.
    sealed class PricesDialog : Form
    {
        readonly List<CheckBox> own = new List<CheckBox>();

        public List<string> Replace { get { return own.Where(c => c.Checked).Select(c => (string)c.Tag).ToList(); } }

        public PricesDialog(List<ApiPrices.Change> changes, DateTime when, Icon icon)
        {
            Text = Loc.T("prices.dialog.title");
            Icon = icon;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = Ui.Font(9f);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            int width = Ui.S(520);
            var body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = Ui.Pad(14, 12, 14, 8), MaximumSize = new Size(width + Ui.S(28), Ui.S(560)), AutoScroll = true };
            Func<string, bool, Label> text = (t, muted) => new Label { Text = t, AutoSize = true, MaximumSize = new Size(width, 0), Margin = Ui.Pad(0, 2, 0, 4), Tag = muted ? "muted" : null };
            body.Controls.Add(text(Loc.T("prices.dialog.source", when.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)), false));
            var link = new LinkLabel { Text = ApiPrices.LiteLlmPage.Replace("https://", ""), AutoSize = true, LinkColor = Palette.Link, ActiveLinkColor = Palette.LinkActive, Margin = Ui.Pad(0, 0, 0, 10) };
            link.LinkClicked += (s, e) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ApiPrices.LiteLlmPage) { UseShellExecute = true }); }
                catch { }
            };
            body.Controls.Add(link);
            CultureInfo c = Loc.Culture;
            var changed = changes.Where(x => !x.Own).ToList();
            if (changed.Count > 0)
            {
                body.Controls.Add(text(Loc.T("prices.dialog.changes"), false));
                foreach (ApiPrices.Change x in changed)
                    body.Controls.Add(text(x.Model + "   " + (x.Old == null ? Loc.T("prices.dialog.new", Describe(x.New, c)) : Difference(x.Old, x.New, c)), true));
            }
            var mine = changes.Where(x => x.Own).ToList();
            if (mine.Count > 0)
            {
                body.Controls.Add(text(Loc.T("prices.dialog.own"), false));
                foreach (ApiPrices.Change x in mine)
                {
                    var cb = new ThemedCheckBox { Text = x.Model + "   " + Difference(x.Old, x.New, c), AutoSize = true, Tag = x.Model, Margin = Ui.Pad(0, 2, 0, 2), UseMnemonic = false };
                    own.Add(cb);
                    body.Controls.Add(cb);
                }
            }
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Width = width, Margin = Ui.Pad(0, 12, 0, 0) };
            var cancel = new Button { Text = Loc.T("btn.cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
            var ok = new Button { Text = Loc.T("btn.prices.apply"), AutoSize = true, DialogResult = DialogResult.OK };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            body.Controls.Add(buttons);
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(body);
            Theme.Apply(this);
        }

        // "input 5, output 25, reading the cache 0.5".
        static string Describe(ApiPrices.Price p, CultureInfo c)
        {
            return Loc.T("prices.dialog.prices", p.Input.ToString("0.###", c), p.Output.ToString("0.###", c), p.Reading.ToString("0.###", c));
        }

        // Only what differs: "reading the cache 0.2 → 0.1".
        static string Difference(ApiPrices.Price a, ApiPrices.Price b, CultureInfo c)
        {
            var parts = new List<string>();
            Action<string, double, double> one = (key, x, y) => { if (Math.Abs(x - y) > 1e-9) parts.Add(Loc.T(key) + " " + x.ToString("0.###", c) + " → " + y.ToString("0.###", c)); };
            one("prices.col.input", a.Input, b.Input);
            one("prices.col.output", a.Output, b.Output);
            one("prices.col.read", a.Reading, b.Reading);
            if (parts.Count == 0) parts.Add(Loc.T("prices.dialog.long"));
            return string.Join(", ", parts);
        }
    }
}
