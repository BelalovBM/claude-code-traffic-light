using System.Drawing;

namespace Semaphore
{
    // Every colour of the interface in one place. Text and status colours are chosen per theme so that
    // text stays at 4.5:1 or better and borders and icon glyphs at 3:1 (checked by tools\tests\contrast.ps1).
    static class Palette
    {
        // The three lamps. They are the same in both themes; the shape drawn on them (see TrayIcons)
        // carries the meaning for people who cannot tell red from green.
        public static readonly Color Green = Color.FromArgb(46, 184, 76);
        public static readonly Color Yellow = Color.FromArgb(255, 193, 7);
        public static readonly Color Red = Color.FromArgb(229, 57, 53);
        public static readonly Color Blue = Color.FromArgb(33, 118, 210);
        public static readonly Color Gray = Color.FromArgb(150, 150, 150);

        // Glyph colours on the lamps: dark on green and yellow, white on red.
        public static readonly Color GlyphDark = Color.FromArgb(30, 30, 30);
        public static readonly Color GlyphLight = Color.White;

        public static Color Lamp(State state)
        {
            switch (state)
            {
                case State.Waiting: return Red;
                case State.Working: return Yellow;
                case State.Compacting: return Blue;
                default: return Green;
            }
        }

        // Text-sized status colours.
        public static Color StatusOk { get { return Theme.HighContrast ? SystemColors.WindowText : Theme.Dark ? Color.FromArgb(46, 160, 67) : Color.FromArgb(30, 120, 50); } }
        public static Color StatusWarn { get { return Theme.HighContrast ? SystemColors.WindowText : Theme.Dark ? Color.FromArgb(214, 140, 0) : Color.FromArgb(150, 95, 0); } }

        public static Color StatusError { get { return Theme.HighContrast ? SystemColors.WindowText : Theme.Dark ? Color.FromArgb(255, 112, 108) : Color.FromArgb(176, 32, 32); } }

        // The accent used for step numbers and links' active state; text, so it must keep 4.5:1.
        public static Color AccentText { get { return Theme.HighContrast ? SystemColors.WindowText : Theme.Dark ? Yellow : Color.FromArgb(150, 100, 0); } }

        // The selected-page bar in the navigation is a graphical part (3:1).
        public static Color AccentBar { get { return Theme.HighContrast ? SystemColors.Highlight : Theme.Dark ? Yellow : Color.FromArgb(176, 118, 0); } }

        public static Color Link { get { return Theme.HighContrast ? SystemColors.HotTrack : Theme.Dark ? Color.FromArgb(120, 170, 255) : Color.FromArgb(0, 90, 200); } }
        public static Color LinkActive { get { return Theme.HighContrast ? SystemColors.WindowText : Theme.Dark ? Yellow : Color.FromArgb(150, 100, 0); } }

        // Borders of buttons and inputs need 3:1 against the page and against a card.
        public static Color ControlBorder { get { return Theme.HighContrast ? SystemColors.WindowText : Theme.Dark ? Color.FromArgb(120, 120, 120) : Color.FromArgb(130, 130, 130); } }
    }
}
