using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Forms;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Semaphore
{
    // Windows notifications with a button ("Answer"). The old balloon of the tray icon can only be clicked as a
    // whole; a real toast can carry a button, and a click on the rest of it closes it like any other toast.
    //
    // A program without an installer is known to Windows by an application id: the program sets it for its own
    // process and registers a display name under HKCU (removed again by "Remove everything"). Nothing else is
    // written. If anything here does not work (an old Windows, notifications switched off for the program),
    // the caller falls back to the balloon.
    static class Toasts
    {
        public const string Aumid = "BelalovBM.ClaudeCodeTrafficLight";
        const string Group = "ctl";

        [DllImport("shell32.dll")]
        static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string id);

        static bool ready;
        public static bool Ready { get { return ready; } }

        const string KeyPath = @"Software\Classes\AppUserModelId\" + Aumid;

        // The registration was made by this test instance, which removes it again when it exits.
        static bool createdHere;

        public static void Prepare(string displayName, string iconPath)
        {
            try
            {
                SetCurrentProcessExplicitAppUserModelID(Aumid);
                bool existed;
                using (RegistryKey old = Registry.CurrentUser.OpenSubKey(KeyPath)) existed = old != null;
                // A test instance leaves the registration of the installed program as it is (its icon points into the
                // folder of that program); it only creates one where there is none, and takes it away afterwards.
                if (!(AppPaths.IsSandbox && existed))
                {
                    using (RegistryKey k = Registry.CurrentUser.CreateSubKey(KeyPath))
                    {
                        k.SetValue("DisplayName", displayName);
                        if (!string.IsNullOrEmpty(iconPath)) k.SetValue("IconUri", iconPath);
                    }
                    createdHere = AppPaths.IsSandbox && !existed;
                }
                ready = true;
            }
            catch (Exception ex) { Log.Write("Toast registration failed: " + ex.Message); }
        }

        // When the program exits: a test instance removes only the registration it made itself.
        public static void Release()
        {
            if (createdHere) Unregister(true);
        }

        public static void Unregister(bool evenInSandbox = false)
        {
            if (AppPaths.IsSandbox && !evenInSandbox) return;
            try { Registry.CurrentUser.DeleteSubKeyTree(KeyPath, false); }
            catch { }
        }

        // Shows the toast (the button only when buttonLabel is given). onActivated gets "answer" for the button and
        // "" for the rest of the toast; onClosedByUser runs when the user closed it with the cross. Both run on a
        // pool thread. False means "use the balloon".
        public static bool Show(string tag, string title, string body, string picturePath, string buttonLabel, int closeAfterMs,
            Action<string> onActivated, Action onClosedByUser)
        {
            if (!ready) return false;
            try { return ShowCore(tag, title, body, picturePath, buttonLabel, closeAfterMs, onActivated, onClosedByUser); }
            catch (Exception ex)
            {
                Log.Write("Toast failed, using the balloon: " + ex.Message);
                return false;
            }
        }

        static bool ShowCore(string tag, string title, string body, string picturePath, string buttonLabel, int closeAfterMs,
            Action<string> onActivated, Action onClosedByUser)
        {
            string image = string.IsNullOrEmpty(picturePath) ? ""
                : "<image placement=\"appLogoOverride\" src=\"" + SecurityElement.Escape(new Uri(picturePath).AbsoluteUri) + "\"/>";
            string xml = "<toast><visual><binding template=\"ToastGeneric\">" + image
                + "<text>" + SecurityElement.Escape(title) + "</text>"
                + "<text>" + SecurityElement.Escape(body) + "</text>"
                + "</binding></visual>"
                + (buttonLabel == null ? ""
                    : "<actions><action content=\"" + SecurityElement.Escape(buttonLabel) + "\" arguments=\"answer\" activationType=\"foreground\"/></actions>")
                + "<audio silent=\"true\"/></toast>";

            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var toast = new ToastNotification(doc) { Tag = tag, Group = Group };
            toast.Activated += (s, e) =>
            {
                var args = e as ToastActivatedEventArgs;
                if (onActivated != null) onActivated(args == null ? "" : args.Arguments);
            };
            toast.Dismissed += (s, e) =>
            {
                if (e.Reason == ToastDismissalReason.UserCanceled && onClosedByUser != null) onClosedByUser();
            };
            ToastNotificationManager.CreateToastNotifier(Aumid).Show(toast);

            // After the banner has gone the toast would sit in the notification centre, where a click could not
            // reach this program any more; take it away.
            var close = new System.Windows.Forms.Timer { Interval = Math.Max(1000, closeAfterMs) };
            close.Tick += (s, e) =>
            {
                close.Stop();
                close.Dispose();
                try { ToastNotificationManager.History.Remove(tag, Group, Aumid); }
                catch { }
            };
            close.Start();
            return true;
        }
    }
}
