using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Semaphore
{
    // A shortcut that works in any program: Windows sends WM_HOTKEY to this hidden window. Written as text
    // ("Win+Alt+C") because the Keys enumeration has no Windows-key modifier.
    sealed class Hotkey : NativeWindow, IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        const int WM_HOTKEY = 0x0312;
        const int Id = 1;
        const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

        // The shortcuts offered in the settings; "" is off. A free choice would let the user pick one that
        // Windows itself takes before any program sees it.
        public static readonly string[] Choices = { "Win+Alt+C", "Ctrl+Alt+C", "Win+Shift+C", "Ctrl+Shift+F12", "" };
        public const string Default = "Win+Alt+C";

        bool registered;
        public event Action Pressed;

        // The shortcut that is in use, or the reason it is not ("" when switched off).
        public string Current { get; private set; }
        public bool Taken { get; private set; }

        public Hotkey()
        {
            CreateHandle(new CreateParams());
        }

        public void Set(string text)
        {
            Unregister();
            Current = text ?? "";
            Taken = false;
            uint mods, key;
            if (!Parse(Current, out mods, out key)) return;
            // A test instance must not take the shortcut away from the copy the user runs.
            if (AppPaths.IsSandbox && Environment.GetEnvironmentVariable("TRAFFICLIGHT_ALLOW_HOTKEY") != "1") return;
            registered = RegisterHotKey(Handle, Id, mods | MOD_NOREPEAT, key);
            Taken = !registered;
            Log.Write(registered ? "Shortcut " + Current + " is set" : "Shortcut " + Current + " is taken by another program");
        }

        static bool Parse(string text, out uint mods, out uint key)
        {
            mods = 0; key = 0;
            if (string.IsNullOrEmpty(text)) return false;
            string[] parts = text.Split('+');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                switch (parts[i].Trim().ToLowerInvariant())
                {
                    case "win": mods |= MOD_WIN; break;
                    case "ctrl": mods |= MOD_CONTROL; break;
                    case "alt": mods |= MOD_ALT; break;
                    case "shift": mods |= MOD_SHIFT; break;
                    default: return false;
                }
            }
            Keys k;
            if (mods == 0 || !Enum.TryParse(parts[parts.Length - 1].Trim(), true, out k)) return false;
            key = (uint)k;
            return true;
        }

        void Unregister()
        {
            if (registered) UnregisterHotKey(Handle, Id);
            registered = false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && (int)m.WParam == Id)
            {
                var pressed = Pressed;
                if (pressed != null) pressed();
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            Unregister();
            DestroyHandle();
        }
    }
}
