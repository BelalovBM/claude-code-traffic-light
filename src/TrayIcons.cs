using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Semaphore
{
    // The tray icons: a lamp with a shape on it, so the state does not depend on telling colours apart.
    //   green  + check mark   ready
    //   yellow + three dots   working
    //   yellow + clock hands  only waiting for its own background task
    //   blue   + two arrows pressing on a line  compacting the conversation
    //   red    + exclamation  waiting for you
    //   grey   + nothing      no sessions
    static class TrayIcons
    {
        // Drawn on a 16-unit grid and scaled, so the same shapes serve 16, 20, 24 and 32 pixel icons.
        public static Bitmap Draw(Level level, int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                float u = size / 16f;
                float edge = Math.Max(1f, u);
                var lamp = new RectangleF(edge / 2, edge / 2, size - edge, size - edge);

                using (var fill = new SolidBrush(LampColor(level)))
                    g.FillEllipse(fill, lamp);
                using (var outline = new Pen(Color.FromArgb(150, 0, 0, 0), edge))
                    g.DrawEllipse(outline, lamp);

                Color glyph = level == Level.Waiting || level == Level.Compacting ? Palette.GlyphLight : Palette.GlyphDark;
                using (var pen = new Pen(glyph, Math.Max(1.6f, 1.9f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                using (var brush = new SolidBrush(glyph))
                {
                    switch (level)
                    {
                        case Level.Idle:
                            g.DrawLines(pen, new[] { new PointF(4.3f * u, 8.6f * u), new PointF(7.0f * u, 11.3f * u), new PointF(11.8f * u, 5.1f * u) });
                            break;
                        case Level.Working:
                            foreach (float x in new[] { 4.6f, 8f, 11.4f })
                                g.FillEllipse(brush, (x - 1.3f) * u, (8f - 1.3f) * u, 2.6f * u, 2.6f * u);
                            break;
                        case Level.Compacting:
                            // Two arrows pressing on a line: the conversation is squeezed together.
                            g.DrawLines(pen, new[] { new PointF(5.2f * u, 3.3f * u), new PointF(8f * u, 5.9f * u), new PointF(10.8f * u, 3.3f * u) });
                            g.DrawLine(pen, 4.6f * u, 8f * u, 11.4f * u, 8f * u);
                            g.DrawLines(pen, new[] { new PointF(5.2f * u, 12.7f * u), new PointF(8f * u, 10.1f * u), new PointF(10.8f * u, 12.7f * u) });
                            break;
                        case Level.Background:
                            // The hands of a clock on the lamp: waiting for a process, not thinking.
                            g.DrawLines(pen, new[] { new PointF(8f * u, 3.9f * u), new PointF(8f * u, 8.4f * u), new PointF(11.4f * u, 8.4f * u) });
                            break;
                        case Level.Waiting:
                            g.DrawLine(pen, 8f * u, 3.8f * u, 8f * u, 8.8f * u);
                            g.FillEllipse(brush, (8f - 1.2f) * u, (12.1f - 1.2f) * u, 2.4f * u, 2.4f * u);
                            break;
                    }
                }
            }
            return bmp;
        }

        public static Color LampColor(Level level)
        {
            switch (level)
            {
                case Level.Idle: return Palette.Green;
                case Level.Working:
                case Level.Background: return Palette.Yellow;
                case Level.Compacting: return Palette.Blue;
                case Level.Waiting: return Palette.Red;
                default: return Palette.Gray;
            }
        }

        // A big picture for pop-ups; the caller keeps the icon, the system draws from its handle.
        public static Icon CreateLarge(Level level, int size)
        {
            using (Bitmap bmp = Draw(level, size))
            {
                IntPtr h = bmp.GetHicon();
                try
                {
                    using (Icon tmp = Icon.FromHandle(h))
                        return (Icon)tmp.Clone();
                }
                finally { Native.DestroyIcon(h); }
            }
        }

        // The icon at the size the system uses in the notification area for the current scaling.
        public static Icon Create(Level level)
        {
            int size = Math.Max(16, System.Windows.Forms.SystemInformation.SmallIconSize.Width);
            using (Bitmap bmp = Draw(level, size))
            {
                IntPtr h = bmp.GetHicon();
                try
                {
                    using (Icon tmp = Icon.FromHandle(h))
                        return (Icon)tmp.Clone();
                }
                finally { Native.DestroyIcon(h); }
            }
        }
    }
}
