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
                c.Controls.Add(Check(Loc.T("chk.prices.auto"), cfg.PricesAuto, v => cfg.PricesAuto = v));
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
                var table = Column(RowWidth, Ui.Pad(0));
                c.Controls.Add(table);
                var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = Ui.Pad(0, 6, 0, 0), MaximumSize = new Size(RowWidth, 0) };
                var load = new Button { Text = Loc.T("btn.prices.load"), AutoSize = true, Margin = Ui.Pad(3, 0, 8, 3) };
                var reset = new Button { Text = Loc.T("btn.prices.reset"), AutoSize = true, Margin = Ui.Pad(0, 0, 8, 3) };
                buttons.Controls.Add(load);
                buttons.Controls.Add(reset);
                // One line next to the buttons: when the prices last came from LiteLLM, or what a press just did.
                Label result = ResultLabel();
                result.Margin = Ui.Pad(0, 7, 0, 3);
                result.MaximumSize = new Size(RowWidth - buttons.GetPreferredSize(Size.Empty).Width - Ui.S(8), 0);
                buttons.Controls.Add(result);
                c.Controls.Add(buttons);

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
                // When the prices last came from LiteLLM, by the daily round or by the button.
                Func<string> loaded = () => cfg.PricesLoadedTicks > 0
                    ? Loc.T("prices.loaded", new DateTime(cfg.PricesLoadedTicks).ToString("g", Loc.Culture))
                    : Loc.T("prices.loaded.never");
                SetResult(result, loaded(), Theme.Muted);
                load.Click += (s, e) =>
                {
                    load.Enabled = false;
                    SetResult(result, Loc.T("prices.load.busy"), Theme.Muted);
                    app.LoadPricesNow((changed, error) =>
                    {
                        if (load.IsDisposed) return;
                        load.Enabled = true;
                        if (error != null) { SetResult(result, Loc.T("prices.load.failed", error), Palette.StatusError); return; }
                        fill();
                        SetResult(result, changed == 0 ? Loc.T("prices.load.same", loaded()) : Loc.T("prices.load.done", loaded(), changed), Palette.StatusOk);
                    });
                };

                // Where the loaded prices come from and where to check them, under More: the card stays short.
                Control more = Details(c, Note(Loc.T("prices.own")), Note(Loc.T("prices.source")),
                    WebLink(ApiPrices.LiteLlmPage.Replace("https://", ""), ApiPrices.LiteLlmPage),
                    Note(Loc.T("prices.check")),
                    WebLink(ApiPrices.AnthropicPage.Replace("https://", ""), ApiPrices.AnthropicPage));
                RowWidth = full;

                // The height of the part: what it needs with More closed and at most six models, again after the
                // rows change; more models scroll, as the sessions in the statistics do. Opened, More is scrolled
                // into sight; closed, the part goes back to its top.
                Action fit = () =>
                {
                    if (more.Visible) return;
                    int height = inner.GetPreferredSize(Size.Empty).Height;
                    // The table: a heading, a row per model, the note under them.
                    int models = table.Controls.Count - 2;
                    if (models > VisiblePriceRows)
                    {
                        Control row = table.Controls[1];
                        height -= (models - VisiblePriceRows) * (row.GetPreferredSize(Size.Empty).Height + row.Margin.Vertical);
                    }
                    area.Height = height;
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

        const int VisiblePriceRows = 6;

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
}
