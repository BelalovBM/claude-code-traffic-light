using System;
using System.Drawing;
using System.Windows.Forms;

namespace Semaphore
{
    // Sizes in the interface are written in design units (pixels at 100% scaling) and turned into real
    // pixels here. The program is system-DPI aware, so text already scales with the system setting;
    // every pixel value in the layout has to scale with it too, otherwise labels no longer fit.
    //
    // TRAFFICLIGHT_UI_SCALE (for example 1.5) pretends another scaling, fonts included, so that the
    // layout can be checked on a screen set to a lower scaling (tools\screenshots).
    static class Ui
    {
        // The scale of the system (or the pretended one) and the one in use right now: a window on a monitor with
        // another scale builds its controls inside Use() with its own factor.
        static readonly float systemScale, systemFontScale = 1f;
        public static float Scale { get; private set; }
        // Fonts are given in points and scale by themselves with the system DPI; only the pretended
        // scale and a monitor's own scale have to be applied to them by hand.
        public static float FontScale { get; private set; }
        // The real DPI of the system, which fonts follow by themselves.
        public static readonly int SystemDpi = 96;

        static Ui()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                    SystemDpi = (int)Math.Round(g.DpiX);
            }
            catch { }
            systemScale = SystemDpi / 96f;

            float forced;
            string text = Environment.GetEnvironmentVariable("TRAFFICLIGHT_UI_SCALE");
            if (!string.IsNullOrEmpty(text) && float.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out forced) && forced >= 0.75f && forced <= 4f)
            {
                // The fonts already carry the real scaling: they only need what is missing up to the pretended one.
                systemFontScale = forced / systemScale;
                systemScale = forced;
            }
            Scale = systemScale;
            FontScale = systemFontScale;
        }

        sealed class Scope : IDisposable
        {
            readonly float scale, font;
            public Scope() { scale = Scale; font = FontScale; }
            public void Dispose() { Scale = scale; FontScale = font; }
        }

        // Sizes and fonts for a window whose monitor has another scale than the system: factor is the monitor's
        // DPI divided by the system's. Everything built inside the using block gets it.
        public static IDisposable Use(float factor)
        {
            var scope = new Scope();
            Scale = systemScale * factor;
            FontScale = systemFontScale * factor;
            return scope;
        }

        public static int S(int designPixels)
        {
            return (int)Math.Round(designPixels * Scale);
        }

        public static Padding Pad(int all)
        {
            return new Padding(S(all));
        }

        public static Padding Pad(int left, int top, int right, int bottom)
        {
            return new Padding(S(left), S(top), S(right), S(bottom));
        }

        public static Font Font(float points)
        {
            return new Font("Segoe UI", points * FontScale);
        }

        public static Font Font(FontFamily family, float points, FontStyle style)
        {
            return new Font(family, points * FontScale, style);
        }

        // The window may not be larger than the screen it opens on.
        public static Size Fit(int designWidth, int designHeight)
        {
            Rectangle area = Native.TrayScreen().WorkingArea;
            return new Size(Math.Min(S(designWidth), area.Width - S(40)), Math.Min(S(designHeight), area.Height - S(80)));
        }
    }
}
