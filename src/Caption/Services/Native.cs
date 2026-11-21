using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Caption.Services
{
    internal static class Native
    {
        // ---------- Process info ----------

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        /// <summary>Full exe path of a process, or null if it can't be read.</summary>
        public static string? GetProcessPath(uint pid)
        {
            if (pid == 0) return null;
            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString(0, (int)size) : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

        public readonly record struct ProcessEntry(uint Pid, uint ParentPid, string ExeName);

        /// <summary>Every running process with its parent, from one snapshot.</summary>
        public static List<ProcessEntry> GetProcesses()
        {
            var list = new List<ProcessEntry>();
            IntPtr snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
            try
            {
                var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
                if (!Process32FirstW(snap, ref e)) return list;
                do list.Add(new ProcessEntry(e.th32ProcessID, e.th32ParentProcessID, e.szExeFile ?? ""));
                while (Process32NextW(snap, ref e));
            }
            finally
            {
                CloseHandle(snap);
            }
            return list;
        }

        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

        /// <summary>Hands unused memory back to Windows (used when Caption goes idle in the tray).</summary>
        public static void TrimWorkingSet()
        {
            try
            {
                GC.Collect(2, GCCollectionMode.Optimized, false);
                using var p = System.Diagnostics.Process.GetCurrentProcess();
                SetProcessWorkingSetSize(p.Handle, new IntPtr(-1), new IntPtr(-1));
            }
            catch
            {
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetDllDirectory(string? path);

        [DllImport("user32.dll")]
        public static extern bool AllowSetForegroundWindow(int processId);

        // ---------- Icons ----------

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHDefExtractIconW(string iconFile, int index, uint flags,
            out IntPtr largeIcon, out IntPtr smallIcon, uint iconSize);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        public static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr iconName);

        public static readonly IntPtr IDI_APPLICATION = new(32512);

        /// <summary>First icon of an exe at (roughly) the requested pixel size. Caller must DestroyIcon it.</summary>
        public static IntPtr ExtractIcon(string path, int size)
        {
            try
            {
                uint sizes = (uint)(size & 0xFFFF) | ((uint)(size & 0xFFFF) << 16);
                int hr = SHDefExtractIconW(path, 0, 0, out IntPtr large, out IntPtr small, sizes);
                if (small != IntPtr.Zero) DestroyIcon(small);
                if (hr != 0)
                {
                    if (large != IntPtr.Zero) DestroyIcon(large);
                    return IntPtr.Zero;
                }
                return large;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        // ---------- Windows ----------

        public const int SM_CXSMICON = 49;

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int RegisterWindowMessage(string message);

        public const int GWL_STYLE = -16;
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

        public static void SetExStyle(IntPtr hwnd, int style, bool on)
        {
            try
            {
                long current = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
                long next = on ? current | (long)style : current & ~(long)style;
                if (next != current) SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(next));
            }
            catch
            {
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        public static readonly IntPtr HWND_TOPMOST = new(-1);
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWA_CAPTION_COLOR = 35;
        public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        /// <summary>The visible bounds of a window (without the invisible resize border), in physical pixels.</summary>
        public static bool GetVisibleBounds(IntPtr hwnd, out RECT rect)
        {
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf<RECT>()) == 0)
                return true;
            return GetWindowRect(hwnd, out rect);
        }

        /// <summary>Work area (screen minus taskbar) of the monitor a window or point is on, in physical pixels.</summary>
        public static RECT GetWorkArea(IntPtr hwnd, bool fullMonitor = false)
        {
            IntPtr mon = hwnd != IntPtr.Zero ? MonitorFromWindow(hwnd, 2) : IntPtr.Zero;
            if (mon == IntPtr.Zero)
            {
                GetCursorPos(out var pt);
                mon = MonitorFromPoint(pt, 2);
            }
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(mon, ref info);
            return fullMonitor ? info.rcMonitor : info.rcWork;
        }

        // ---------- Menu mask key ----------

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);

        private const byte VK_MASK = 0xE8; // unassigned virtual key
        private const uint KEYEVENTF_KEYUP = 0x0002;

        /// <summary>Stops a lone Alt/Win release (after a hotkey) from opening a menu or the Start menu.</summary>
        public static void TapMaskKey()
        {
            try
            {
                keybd_event(VK_MASK, 0, 0, UIntPtr.Zero);
                keybd_event(VK_MASK, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            catch
            {
            }
        }

        // ---------- Global hotkey ----------

        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_NOREPEAT = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // ---------- OS version ----------

        [StructLayout(LayoutKind.Sequential)]
        private struct OSVERSIONINFOEX
        {
            public int dwOSVersionInfoSize;
            public int dwMajorVersion;
            public int dwMinorVersion;
            public int dwBuildNumber;
            public int dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szCSDVersion;
            public ushort wServicePackMajor, wServicePackMinor, wSuiteMask;
            public byte wProductType, wReserved;
        }

        [DllImport("ntdll.dll")]
        private static extern int RtlGetVersion(ref OSVERSIONINFOEX info);

        private static int _build = -1;

        /// <summary>Windows build number (22000+ is Windows 11).</summary>
        public static int WindowsBuild
        {
            get
            {
                if (_build >= 0) return _build;
                try
                {
                    var v = new OSVERSIONINFOEX { dwOSVersionInfoSize = Marshal.SizeOf<OSVERSIONINFOEX>(), szCSDVersion = "" };
                    RtlGetVersion(ref v);
                    _build = v.dwBuildNumber;
                }
                catch
                {
                    _build = Environment.OSVersion.Version.Build;
                }
                return _build;
            }
        }
    }
}
