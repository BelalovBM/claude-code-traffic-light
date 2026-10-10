using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Semaphore
{
    // A hook runs as a descendant of the Claude Code process, so walking up the parent chain
    // leads to the terminal or editor window that hosts the session.
    static class ProcessTree
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ProcessEntry
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public IntPtr DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int PriorityClassBase;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        // A hook asks twice (window, process); the snapshot of all processes is built once for both.
        // The long-running tray app must not reuse it, processes come and go.
        public static bool ReuseSnapshot;
        static Dictionary<int, int> snapshot;

        static Dictionary<int, int> ParentMap()
        {
            if (!ReuseSnapshot) return BuildParentMap();
            return snapshot ?? (snapshot = BuildParentMap());
        }

        static Dictionary<int, int> BuildParentMap()
        {
            var map = new Dictionary<int, int>();
            IntPtr snap = CreateToolhelp32Snapshot(2, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
            try
            {
                var e = new ProcessEntry { Size = (uint)Marshal.SizeOf(typeof(ProcessEntry)) };
                for (bool ok = Process32First(snap, ref e); ok; ok = Process32Next(snap, ref e))
                    map[(int)e.ProcessId] = (int)e.ParentProcessId;
            }
            finally { CloseHandle(snap); }
            return map;
        }

        [DllImport("kernel32.dll")] static extern bool FreeConsole();
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool AttachConsole(uint processId);
        [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        // Under Windows Terminal a shell has no window of its own: its console window is a hidden
        // pseudo-window owned by the terminal window, which is the one to bring forward.
        static bool ConsoleOwner(int pid, out long hwnd, out string name)
        {
            hwnd = 0;
            name = null;
            FreeConsole();
            if (!AttachConsole((uint)pid)) return false;
            try
            {
                IntPtr console = GetConsoleWindow();
                if (console == IntPtr.Zero) return false;
                IntPtr owner = GetAncestor(console, 3); // GA_ROOTOWNER
                if (owner == IntPtr.Zero) owner = console;
                uint ownerPid;
                GetWindowThreadProcessId(owner, out ownerPid);
                hwnd = owner.ToInt64();
                using (Process p = Process.GetProcessById((int)ownerPid))
                    name = p.ProcessName;
                return true;
            }
            catch { return false; }
            finally { FreeConsole(); }
        }

        // The nearest ancestor that owns a window: Windows Terminal, a console host, an editor.
        public static bool FindHostWindow(out long hwnd, out string name)
        {
            return FindHostWindowFrom(Process.GetCurrentProcess().Id, out hwnd, out name);
        }

        // The Claude Code process itself, with its start time: a closed terminal kills it without
        // a SessionEnd hook, so the tray app watches this process to notice that the session is gone.
        public static bool FindClaudeProcess(out int pid, out long startedFileTime)
        {
            pid = 0;
            startedFileTime = 0;
            try
            {
                Dictionary<int, int> parents = ParentMap();
                int current = Process.GetCurrentProcess().Id;
                for (int depth = 0; depth < 16; depth++)
                {
                    int parent;
                    if (!parents.TryGetValue(current, out parent) || parent <= 0) break;
                    current = parent;
                    try
                    {
                        using (Process p = Process.GetProcessById(current))
                        {
                            if (p.ProcessName.Equals("claude", StringComparison.OrdinalIgnoreCase))
                            {
                                pid = current;
                                startedFileTime = p.StartTime.ToFileTimeUtc();
                                return true;
                            }
                        }
                    }
                    catch { break; }
                }
            }
            catch { }
            return false;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
        [DllImport("ntdll.dll")]
        static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr buffer, int length, out int returned);

        // The command line of another process of this user, or null when it cannot be read.
        internal static string CommandLine(int pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
            if (h == IntPtr.Zero) return null;
            try
            {
                int need;
                NtQueryInformationProcess(h, 60, IntPtr.Zero, 0, out need); // ProcessCommandLineInformation
                if (need <= 0 || need > 1 << 20) return null;
                IntPtr buf = Marshal.AllocHGlobal(need);
                try
                {
                    if (NtQueryInformationProcess(h, 60, buf, need, out need) != 0) return null;
                    // A UNICODE_STRING: length in bytes, maximum length, then the pointer to the text.
                    int bytes = Marshal.ReadInt16(buf);
                    IntPtr text = Marshal.ReadIntPtr(buf, IntPtr.Size);
                    return bytes <= 0 || text == IntPtr.Zero ? "" : Marshal.PtrToStringUni(text, bytes / 2);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch { return null; }
            finally { CloseHandle(h); }
        }

        // How many commands Claude Code started are still running under its process. Each one is a shell with a
        // recognisable command line (its saved shell state, or its PowerShell launcher). Between turns no command runs
        // in the foreground, so these are its background tasks. Null when the process cannot be looked into.
        public static int? RunningToolShells(int claudePid)
        {
            if (claudePid <= 0) return null;
            try
            {
                using (Process.GetProcessById(claudePid)) { }
            }
            catch { return null; }
            int count = 0;
            foreach (KeyValuePair<int, int> p in BuildParentMap())
            {
                if (p.Value != claudePid || p.Key == claudePid) continue;
                string line = CommandLine(p.Key);
                if (line == null) continue;
                if (IsToolShell(line)) count++;
            }
            return count;
        }

        internal static bool IsToolShell(string commandLine)
        {
            return commandLine.IndexOf(".claude/shell-snapshots/", StringComparison.OrdinalIgnoreCase) >= 0
                || commandLine.IndexOf(@".claude\shell-snapshots\", StringComparison.OrdinalIgnoreCase) >= 0
                || commandLine.IndexOf("CLAUDE_CODE_SHELL_LAUNCHER", StringComparison.Ordinal) >= 0;
        }

        public static bool FindHostWindowFrom(int pid, out long hwnd, out string name)
        {
            hwnd = 0;
            name = null;
            try
            {
                Dictionary<int, int> parents = ParentMap();
                for (int depth = 0; depth < 16; depth++)
                {
                    int parent;
                    if (!parents.TryGetValue(pid, out parent) || parent <= 0) break;
                    pid = parent;
                    try
                    {
                        using (Process p = Process.GetProcessById(pid))
                        {
                            if (p.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase)) break;
                            if (p.MainWindowHandle != IntPtr.Zero)
                            {
                                hwnd = p.MainWindowHandle.ToInt64();
                                name = p.ProcessName;
                                return true;
                            }
                        }
                        if (ConsoleOwner(pid, out hwnd, out name)) return true;
                    }
                    catch { break; }
                }
            }
            catch { }
            return false;
        }
    }
}
