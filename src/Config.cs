using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Semaphore
{
    static class AppPaths
    {
        // Set once everything has been removed, so nothing writes the data folder back.
        public static bool Disabled;

        static string dataDir;

        // A test instance (TRAFFICLIGHT_DATA_DIR set) gets its own pipes and single-instance mutex: the hooks of the
        // real Claude Code (which know nothing of the variable) never reach it, and it can run next to the real program.
        public static readonly string InstanceSuffix = MakeSuffix();
        // A test or self-check instance: it keeps away from what belongs to the installed program (autostart,
        // notification registration, the keyboard shortcut).
        public static bool IsSandbox { get { return InstanceSuffix.Length > 0; } }

        // An explicit static constructor: without it the runtime may compute the suffix as soon as Main is compiled,
        // before --selfcheck has chosen its folders, and the self-check would talk to the installed program instead.
        static AppPaths() { }

        static string MakeSuffix()
        {
            string d = Environment.GetEnvironmentVariable("TRAFFICLIGHT_DATA_DIR");
            if (string.IsNullOrEmpty(d)) return "";
            uint hash = 2166136261;
            foreach (char c in d.ToLowerInvariant()) { hash ^= c; hash *= 16777619; }
            return "." + hash.ToString("x8");
        }

        // Settings and the log live next to the .exe, so the program leaves nothing scattered around the
        // system. TRAFFICLIGHT_DATA_DIR overrides that (tests). A folder the program cannot write to
        // (for example Program Files) falls back to %APPDATA%.
        public static string DataDir
        {
            get
            {
                string d = Environment.GetEnvironmentVariable("TRAFFICLIGHT_DATA_DIR");
                if (!string.IsNullOrEmpty(d))
                {
                    Directory.CreateDirectory(d);
                    return d;
                }
                if (dataDir == null) dataDir = Resolve();
                return dataDir;
            }
        }

        // The folder an older version used.
        public static string LegacyDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeCodeTrafficLight"); }
        }

        static string Resolve()
        {
            string exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (CanWrite(exeDir))
            {
                MigrateLegacy(exeDir);
                return exeDir;
            }
            Directory.CreateDirectory(LegacyDir);
            return LegacyDir;
        }

        static bool CanWrite(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        // Settings made by an older version are copied once; the old folder stays until "Remove everything".
        static void MigrateLegacy(string exeDir)
        {
            try
            {
                string target = Path.Combine(exeDir, "config.json");
                string old = Path.Combine(LegacyDir, "config.json");
                if (!File.Exists(target) && File.Exists(old)) File.Copy(old, target);
            }
            catch { }
        }
    }

    static class Log
    {
        static readonly object gate = new object();

        public static string FilePath { get { return Path.Combine(AppPaths.DataDir, "trafficlight.log"); } }

        public static void Write(string text)
        {
            if (AppPaths.Disabled) return;
            try
            {
                lock (gate)
                {
                    TrimToLastHalf(FilePath, 512 * 1024);
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + text + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }

        // A file grown past the limit keeps its newer half: the recent history is what a problem is looked for in.
        public static void TrimToLastHalf(string path, long limit)
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length <= limit) return;
            string[] old = File.ReadAllLines(path, Encoding.UTF8);
            var keep = new string[old.Length - old.Length / 2];
            Array.Copy(old, old.Length / 2, keep, 0, keep.Length);
            File.WriteAllLines(path, keep, new UTF8Encoding(false));
        }
    }

    sealed class Config
    {
        public string Language { get; set; }
        public string Theme { get; set; }
        public bool Autostart { get; set; }
        public bool NotificationsEnabled { get; set; }
        public bool SoundWaiting { get; set; }
        public bool SoundDone { get; set; }
        public bool ToastWaiting { get; set; }
        public bool ToastDone { get; set; }
        public int StaleMinutes { get; set; }
        public bool FirstRunDone { get; set; }

        // Remote (phone) notification rules.
        public bool RemoteWaiting { get; set; }
        public int RemoteWaitingAfterMinutes { get; set; }
        public bool RemoteDone { get; set; }
        public int RemoteDoneMinMinutes { get; set; }
        public bool RemoteDetails { get; set; }

        public bool NtfyEnabled { get; set; }
        // Quick switch for pushes while the channel itself stays configured.
        public bool NtfyPaused { get; set; }
        // A test message reached the phone for this server and topic (a hash: the topic itself is a secret).
        // The connection steps fold away until either of them changes.
        public string NtfyTestedHash { get; set; }

        [ScriptIgnore]
        public string NtfyCurrentHash
        {
            get
            {
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes((NtfyServer ?? "").Trim().TrimEnd('/') + "|" + NtfyTopic));
                    return BitConverter.ToString(h, 0, 12).Replace("-", "");
                }
            }
        }

        [ScriptIgnore]
        public bool NtfyTested { get { return !string.IsNullOrEmpty(NtfyTestedHash) && NtfyTestedHash == NtfyCurrentHash; } }

        // The server in use. It follows the choice below: the public one, or the remembered own one.
        public string NtfyServer { get; set; }
        public const string PublicServer = "https://ntfy.sh";
        // The user picked their own server; its address, user and password are kept while the public one is used,
        // so switching back and forth needs no typing.
        public bool NtfyOwn { get; set; }
        public string NtfyOwnServer { get; set; }

        public void ApplyNtfyServer()
        {
            NtfyServer = NtfyOwn && !string.IsNullOrWhiteSpace(NtfyOwnServer) ? NtfyOwnServer.Trim() : PublicServer;
        }

        public string NtfyTopicProtected { get; set; }

        // For a server with access control: user name and password, or just an access token.
        public string NtfyUser { get; set; }
        public string NtfySecretProtected { get; set; }

        [ScriptIgnore]
        public string NtfySecret
        {
            get { return Secret.Unprotect(NtfySecretProtected); }
            set { NtfySecretProtected = Secret.Protect(value); }
        }

        [ScriptIgnore]
        public string NtfyTopic
        {
            get { return Secret.Unprotect(NtfyTopicProtected); }
            set { NtfyTopicProtected = Secret.Protect(value); }
        }

        // Answering permission prompts from the phone: off until the user opts in.
        public bool RemoteApprove { get; set; }
        // Answering permission prompts from the tray menu on this computer.
        public bool LocalApprove { get; set; }
        // A balloon when the program starts, so the new tray icon is not missed.
        public bool StartupNotice { get; set; }
        // A pop-up when Claude starts and finishes compacting the conversation.
        public bool ToastCompact { get; set; }
        // A panel near the tray when Claude asks something that can be answered from there.
        public bool PopupAsk { get; set; }
        // The phone gets a push only when the notification on the computer was ignored (it closed by itself).
        public bool PhoneOnlyIfIgnored { get; set; }
        // The shortcut that opens the waiting request (or the menu) from any program; "" is off.
        public string Hotkey { get; set; }
        // "Do not disturb" for the computer (sounds, pop-ups, the panel) until this moment; the phone is not affected.
        public long DndUntilTicks { get; set; }
        public int RemoteApproveMinutes { get; set; }
        public string ReplyTopicProtected { get; set; }

        [ScriptIgnore]
        public string ReplyTopic
        {
            get { return Secret.Unprotect(ReplyTopicProtected); }
            set { ReplyTopicProtected = Secret.Protect(value); }
        }

        // Claude's usage limits: shown in the menu and the tooltip, added to every push, a notice every LimitsStep percent
        // of the 5-hour window and every LimitsStepWeek percent of the week (0: none), and a notice when either runs
        // out by the estimate and when it is back.
        public bool LimitsShow { get; set; }
        public bool LimitsInPush { get; set; }
        public int LimitsStep { get; set; }
        public int LimitsStepWeek { get; set; }
        public bool LimitsEvents { get; set; }

        // A notice (here and on the phone) when Claude has been waiting this many minutes for its own background task:
        // a server left running or a stuck task would otherwise look like work for ever. 0: no notice.
        public int BackgroundNotifyMinutes { get; set; }

        // Ids of sessions whose notifications are switched off.
        public List<string> MutedSessions { get; set; }

        public Config()
        {
            MutedSessions = new List<string>();
            NtfyUser = "";
            NtfySecretProtected = "";
            RemoteApproveMinutes = 5;
            ReplyTopicProtected = "";
            RemoteWaiting = true;
            RemoteWaitingAfterMinutes = 1;
            RemoteDone = true;
            RemoteDoneMinMinutes = 2;
            NtfyServer = "https://ntfy.sh";
            NtfyTopicProtected = "";
            Language = "auto";
            Theme = "system";
            NotificationsEnabled = true;
            StartupNotice = true;
            ToastCompact = true;
            PopupAsk = true;
            PhoneOnlyIfIgnored = true;
            Hotkey = Semaphore.Hotkey.Default;
            SoundWaiting = true;
            SoundDone = true;
            ToastWaiting = true;
            ToastDone = true;
            StaleMinutes = 30;
            LimitsShow = true;
            LimitsInPush = true;
            LimitsEvents = true;
            BackgroundNotifyMinutes = 30;
        }

        public static string FilePath { get { return Path.Combine(AppPaths.DataDir, "config.json"); } }
        public static string BackupPath { get { return FilePath + ".bak"; } }
        static string TempPath { get { return FilePath + ".tmp"; } }

        // The main file could not be read: the next save must not move it over the good backup.
        static bool mainBroken;

        public static Config Load()
        {
            if (File.Exists(FilePath))
            {
                Config c = Read(FilePath);
                if (c != null) return c;
                mainBroken = true;
                if (File.Exists(BackupPath))
                {
                    c = Read(BackupPath);
                    if (c != null)
                    {
                        Log.Write("config.json could not be read, the settings were taken from config.json.bak");
                        return c;
                    }
                }
                Log.Write("config.json could not be read, default settings are used");
            }
            return new Config();
        }

        static Config Read(string path)
        {
            try
            {
                var c = new JavaScriptSerializer().Deserialize<Config>(File.ReadAllText(path, Encoding.UTF8));
                if (c == null) return null;
                if (c.StaleMinutes < 1) c.StaleMinutes = 30;
                if (c.MutedSessions == null) c.MutedSessions = new List<string>();
                if (c.RemoteApproveMinutes < 1 || c.RemoteApproveMinutes > 30) c.RemoteApproveMinutes = 5;
                if (c.LimitsStep < 0 || c.LimitsStep > 50) c.LimitsStep = 0;
                if (c.LimitsStepWeek < 0 || c.LimitsStepWeek > 50) c.LimitsStepWeek = 0;
                if (c.BackgroundNotifyMinutes < 0 || c.BackgroundNotifyMinutes > 600) c.BackgroundNotifyMinutes = 30;
                if (c.NtfyEnabled && string.IsNullOrEmpty(c.NtfyTopic)) c.NtfyTopic = Secret.RandomTopic();
                // Settings from before the choice existed: any address other than the public one was an own server.
                if (c.NtfyOwnServer == null)
                {
                    string old = (c.NtfyServer ?? "").Trim();
                    bool own = old.Length > 0 && !string.Equals(old.TrimEnd('/'), PublicServer, StringComparison.OrdinalIgnoreCase);
                    c.NtfyOwn = own;
                    c.NtfyOwnServer = own ? old : "";
                }
                c.ApplyNtfyServer();
                return c;
            }
            catch (Exception ex)
            {
                Log.Write("Config load failed (" + Path.GetFileName(path) + "): " + ex.Message);
                return null;
            }
        }

        // The new text goes to a temporary file first and then takes the place of the old one, which becomes the
        // backup: a write cut short (power, a full disk) leaves either the old settings or the new ones, never half.
        public void Save()
        {
            if (AppPaths.Disabled) return;
            try
            {
                File.WriteAllText(TempPath, Json.Pretty(new JavaScriptSerializer().DeserializeObject(new JavaScriptSerializer().Serialize(this))), new UTF8Encoding(false));
                if (File.Exists(FilePath) && !mainBroken)
                    File.Replace(TempPath, FilePath, BackupPath, true);
                else
                {
                    File.Copy(TempPath, FilePath, true);
                    File.Delete(TempPath);
                    mainBroken = false;
                }
            }
            catch (Exception ex) { Log.Write("Config save failed: " + ex.Message); }
        }
    }
}
