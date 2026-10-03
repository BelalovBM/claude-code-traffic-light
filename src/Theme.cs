using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Semaphore
{
    // A bordered block that groups related controls (for example a text and its QR code).
    sealed class CardPanel : Panel
    {
        public CardPanel()
        {
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = Ui.Pad(14);
            Margin = Ui.Pad(3, 10, 3, 6);
            DoubleBuffered = true;
        }

        // A plain Panel does not move its children by Padding, so the single content
        // control is placed inside the border explicitly.
        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            e.Control.Location = new Point(Padding.Left, Padding.Top);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.Border))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    // A warning that has to be read before the switch below it: an amber bar and frame.
    sealed class WarningBox : Panel
    {
        public WarningBox()
        {
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = Ui.Pad(16, 10, 10, 10);
            Margin = Ui.Pad(3, 4, 3, 10);
            DoubleBuffered = true;
        }

        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            e.Control.Location = new Point(Padding.Left, Padding.Top);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Palette.StatusWarn))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            using (var bar = new SolidBrush(Palette.StatusWarn))
                e.Graphics.FillRectangle(bar, 0, 0, Ui.S(6), Height);
        }
    }

    // Windows draws the text of a switched-off check box in its own grey, which nearly disappears on the dark
    // theme and cannot be changed through ForeColor; this one writes that text again in the theme's colour.
    sealed class ThemedCheckBox : CheckBox
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Enabled || Theme.HighContrast || string.IsNullOrEmpty(Text)) return;
            int glyph = CheckBoxRenderer.GetGlyphSize(e.Graphics, System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedDisabled).Width;
            var area = new Rectangle(glyph + Ui.S(3), 0, Width - glyph - Ui.S(3), Height);
            using (var back = new SolidBrush(BackColor))
                e.Graphics.FillRectangle(back, area);
            TextRenderer.DrawText(e.Graphics, Text, Font, area, Theme.Disabled, BackColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }
    }

    static class Theme
    {
        public static bool Dark { get; private set; }
        // Windows' high-contrast mode: the system colours are used as they are, whatever the theme setting says.
        public static bool HighContrast { get; private set; }
        public static Color Back, Fore, Surface, Border, Muted, Hover;
        // Text of a switched-off control: clearly dimmer than the rest, still readable (3:1 on both grounds).
        public static Color Disabled;

        public static void Update(string setting)
        {
            HighContrast = SystemInformation.HighContrast;
            if (HighContrast)
            {
                Color window = SystemColors.Window;
                Dark = (window.R * 299 + window.G * 587 + window.B * 114) / 1000 < 128;
                Back = window;
                Surface = window;
                Fore = SystemColors.WindowText;
                Border = SystemColors.WindowText;
                Muted = SystemColors.GrayText;
                Disabled = SystemColors.GrayText;
                Hover = SystemColors.Highlight;
                return;
            }
            Dark = setting == "dark" || (setting != "light" && SystemUsesDark());
            if (Dark)
            {
                Back = Color.FromArgb(32, 32, 32);
                Surface = Color.FromArgb(45, 45, 45);
                Fore = Color.FromArgb(235, 235, 235);
                Border = Palette.ControlBorder;
                Muted = Color.FromArgb(160, 160, 160);
                Disabled = Color.FromArgb(128, 128, 128);
                Hover = Color.FromArgb(62, 62, 62);
            }
            else
            {
                Back = Color.FromArgb(243, 243, 243);
                Surface = Color.White;
                Fore = Color.FromArgb(25, 25, 25);
                Border = Palette.ControlBorder;
                Muted = Color.FromArgb(105, 105, 105);
                Disabled = Color.FromArgb(125, 125, 125);
                Hover = Color.FromArgb(225, 225, 225);
            }
        }

        static bool SystemUsesDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }

        public static void Apply(Form form)
        {
            form.BackColor = Back;
            form.ForeColor = Fore;
            ApplyTo(form.Controls);
            if (form.IsHandleCreated) Native.SetDarkTitleBar(form.Handle, Dark);
            else form.HandleCreated += (s, e) => Native.SetDarkTitleBar(form.Handle, Dark);
        }

        // Colours the controls added to a page that is already shown.
        public static void Apply(Control root)
        {
            ApplyTo(root.Controls, root is CardPanel);
        }

        // Inside a card the surface is the lighter colour, so inputs take the darker one to stay visible.
        static void ApplyTo(Control.ControlCollection controls, bool inCard = false)
        {
            Color field = inCard ? Back : Surface;
            Color ground = inCard ? Surface : Back;
            foreach (Control c in controls)
            {
                var button = c as Button;
                var combo = c as ComboBox;
                if (button != null)
                {
                    button.FlatStyle = FlatStyle.Flat;
                    button.FlatAppearance.BorderColor = Border;
                    button.FlatAppearance.MouseOverBackColor = Hover;
                    button.BackColor = field;
                    button.ForeColor = Fore;
                }
                else if (combo != null)
                {
                    combo.FlatStyle = FlatStyle.Flat;
                    combo.BackColor = field;
                    combo.ForeColor = Fore;
                }
                else if (c is TextBox || c is NumericUpDown || c is ListBox)
                {
                    c.BackColor = field;
                    c.ForeColor = Fore;
                }
                else if (c is Panel || c is FlowLayoutPanel || c is TableLayoutPanel)
                {
                    c.BackColor = c is CardPanel ? Surface : ground;
                    c.ForeColor = Fore;
                }
                else
                {
                    // Tag "keep": caller chose the colour; "muted": secondary text.
                    string tag = c.Tag as string;
                    if (tag == "muted") c.ForeColor = Muted;
                    else if (tag != "keep") c.ForeColor = Fore;
                }
                if (c.HasChildren) ApplyTo(c.Controls, inCard || c is CardPanel);
            }
        }

        public static ToolStripRenderer MenuRenderer()
        {
            return Dark
                ? new ToolStripProfessionalRenderer(new DarkColors())
                : new ToolStripProfessionalRenderer();
        }

        sealed class DarkColors : ProfessionalColorTable
        {
            public DarkColors() { UseSystemColors = false; }
            public override Color ToolStripDropDownBackground { get { return Surface; } }
            public override Color ImageMarginGradientBegin { get { return Surface; } }
            public override Color ImageMarginGradientMiddle { get { return Surface; } }
            public override Color ImageMarginGradientEnd { get { return Surface; } }
            public override Color MenuBorder { get { return Border; } }
            public override Color MenuItemBorder { get { return Hover; } }
            public override Color MenuItemSelected { get { return Hover; } }
            public override Color MenuItemSelectedGradientBegin { get { return Hover; } }
            public override Color MenuItemSelectedGradientEnd { get { return Hover; } }
            public override Color SeparatorDark { get { return Border; } }
            public override Color SeparatorLight { get { return Border; } }
            public override Color CheckBackground { get { return Hover; } }
            public override Color CheckSelectedBackground { get { return Hover; } }
            public override Color CheckPressedBackground { get { return Hover; } }
        }
    }
}
