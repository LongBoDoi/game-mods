using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace LocalCoop
{
    // Puts this copy's window where Player 1 said (layout.txt): borderless, at an exact screen
    // rectangle. Guest windows also never take focus, so the keyboard stays with Player 1 even
    // when someone clicks a guest window.
    internal static class WindowLayout
    {
        public enum Mode { Auto, SplitScreen }
        public enum Split { SideBySide, Stacked }

        public struct Rect
        {
            public int X, Y, W, H;
            public override string ToString() => $"{X},{Y} {W}x{H}";
            public bool SameAs(Rect o) => X == o.X && Y == o.Y && W == o.W && H == o.H;
        }

        /// <summary>Players with a window right now (from layout.txt; Player 1 alone otherwise).</summary>
        public static readonly List<int> LivePlayers = new List<int> { 1 };

        /// <summary>The size a guest was started at (from -screen-width / -screen-height).</summary>
        public static int LaunchWidth, LaunchHeight;

        private static string s_layoutText;
        private static Rect? s_target;
        private static float s_nextPoll, s_nextCheck;
        private static int s_applyStage;     // 0 idle, 1 resolution requested, 2 placed
        private static int s_stageFrame;
        private static IntPtr s_hwnd;

        // Player 1's window as it was before co-op, to put back when the last guest leaves.
        private static bool s_saved;
        private static FullScreenMode s_savedMode;
        private static int s_savedW, s_savedH;
        private static long s_savedStyle, s_savedExStyle;
        private static RECT s_savedRect;

        static WindowLayout()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "-screen-width") int.TryParse(args[i + 1], out LaunchWidth);
                else if (args[i] == "-screen-height") int.TryParse(args[i + 1], out LaunchHeight);
            }
        }

        public static void Update()
        {
            if (Time.realtimeSinceStartup >= s_nextPoll)
            {
                s_nextPoll = Time.realtimeSinceStartup + 0.5f;
                string text = Coop.Read("layout.txt");
                if (text != s_layoutText)
                {
                    s_layoutText = text;
                    ReadLayout(text);
                }
            }

            if (s_target == null || s_applyStage == 0) return;
            Rect r = s_target.Value;
            // Every step waits a few frames for Unity to finish the window change before it.
            if (s_applyStage != Verify && Time.frameCount - s_stageFrame < 3) return;

            switch (s_applyStage)
            {
                case PartialPlace:
                    Place(r);
                    Next(Verify);
                    return;
                case FullMoveToMonitor:
                    // Windowed now; park it inside the target monitor, then go fullscreen there.
                    IntPtr hwnd = Hwnd();
                    if (hwnd != IntPtr.Zero)
                        SetWindowPos(hwnd, IntPtr.Zero, r.X + r.W / 4, r.Y + r.H / 4, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                    Next(FullGoFullscreen);
                    return;
                case FullGoFullscreen:
                    Screen.SetResolution(r.W, r.H, FullScreenMode.FullScreenWindow);
                    Next(FullNoActivate);
                    return;
                case FullNoActivate:
                    MarkNoActivate();
                    Next(Verify);
                    return;
                case Verify:
                    if (Time.realtimeSinceStartup < s_nextCheck) return;
                    s_nextCheck = Time.realtimeSinceStartup + 1f;
                    if (IsPlaced(r)) return;
                    // The game or the user moved it; put it back - but never fight forever.
                    if (++s_retries > 5)
                    {
                        Plugin.Log.LogWarning($"Window for Player {Coop.Player} keeps moving away from {r}; leaving it where it is.");
                        s_applyStage = 0;
                        return;
                    }
                    Begin(r);
                    return;
            }
        }

        private const int PartialPlace = 1, FullMoveToMonitor = 2, FullGoFullscreen = 3, FullNoActivate = 4, Verify = 5;
        private static int s_retries;
        private static bool s_wholeMonitor;

        private static void Next(int stage)
        {
            s_applyStage = stage;
            s_stageFrame = Time.frameCount;
            s_nextCheck = Time.realtimeSinceStartup + 1f;
        }

        private static void ReadLayout(string text)
        {
            LivePlayers.Clear();
            Rect? mine = null;
            foreach (string[] f in Coop.Lines(text))
            {
                if (f.Length < 5 || !int.TryParse(f[0], out int p)) continue;
                LivePlayers.Add(p);
                if (p != Coop.Player) continue;
                mine = new Rect
                {
                    X = int.Parse(f[1], CultureInfo.InvariantCulture),
                    Y = int.Parse(f[2], CultureInfo.InvariantCulture),
                    W = int.Parse(f[3], CultureInfo.InvariantCulture),
                    H = int.Parse(f[4], CultureInfo.InvariantCulture),
                };
            }
            if (LivePlayers.Count == 0) LivePlayers.Add(1);

            if (mine == null)
            {
                // Player 1 with no guests left: give the window back as it was.
                if (!Coop.IsGuest && s_target != null) Restore();
                s_target = null;
                s_applyStage = 0;
                return;
            }
            if (s_target != null && s_target.Value.SameAs(mine.Value)) return;
            s_target = mine;
            s_retries = 0;
            Begin(mine.Value);
        }

        private static void Begin(Rect r)
        {
            if (!Coop.IsGuest && !s_saved) Save();
            IntPtr target = MonitorFromRect(r, out bool whole);
            s_wholeMonitor = whole;
            Plugin.Log.LogInfo($"Window for Player {Coop.Player}: {r}{(whole ? " (whole monitor)" : "")}");

            if (whole)
            {
                // A window the size of a whole monitor is what Unity's borderless fullscreen is for;
                // a plain window that big gets borders added around it.
                IntPtr hwnd = Hwnd();
                bool there = hwnd != IntPtr.Zero && MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) == target;
                if (there)
                {
                    if (Screen.fullScreenMode != FullScreenMode.FullScreenWindow || Screen.width != r.W || Screen.height != r.H)
                        Screen.SetResolution(r.W, r.H, FullScreenMode.FullScreenWindow);
                    Next(FullNoActivate);
                }
                else
                {
                    Screen.SetResolution(Math.Max(320, r.W / 2), Math.Max(240, r.H / 2), FullScreenMode.Windowed);
                    Next(FullMoveToMonitor);
                }
                return;
            }

            if (Screen.fullScreenMode != FullScreenMode.Windowed || Screen.width != r.W || Screen.height != r.H)
                Screen.SetResolution(r.W, r.H, FullScreenMode.Windowed);
            Next(PartialPlace);
        }

        /// <summary>The monitor a rectangle is on, and whether it covers that monitor exactly.</summary>
        private static IntPtr MonitorFromRect(Rect r, out bool whole)
        {
            var rc = new RECT { Left = r.X, Top = r.Y, Right = r.X + r.W, Bottom = r.Y + r.H };
            IntPtr mon = MonitorFromRect(ref rc, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            whole = GetMonitorInfo(mon, ref info) && info.rcMonitor.Left == rc.Left && info.rcMonitor.Top == rc.Top
                && info.rcMonitor.Right == rc.Right && info.rcMonitor.Bottom == rc.Bottom;
            return mon;
        }

        /// <summary>Guests never take focus: the keyboard stays with Player 1.</summary>
        private static void MarkNoActivate()
        {
            if (!Coop.IsGuest) return;
            IntPtr hwnd = Hwnd();
            if (hwnd == IntPtr.Zero) return;
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            if ((ex & WS_EX_NOACTIVATE) == 0) SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_NOACTIVATE));
        }

        private static void Place(Rect r)
        {
            IntPtr hwnd = Hwnd();
            if (hwnd == IntPtr.Zero) return;
            long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
            style &= ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_MAXIMIZE);
            style |= WS_POPUP | WS_VISIBLE;
            SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));
            MarkNoActivate();
            SetWindowPos(hwnd, IntPtr.Zero, r.X, r.Y, r.W, r.H, SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        }

        private static bool IsPlaced(Rect r)
        {
            IntPtr hwnd = Hwnd();
            if (hwnd == IntPtr.Zero) return true;
            if (!GetWindowRect(hwnd, out RECT wr)) return true;
            long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
            bool ok;
            if (s_wholeMonitor)
            {
                IntPtr target = MonitorFromRect(r, out _);
                ok = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) == target && Screen.fullScreenMode == FullScreenMode.FullScreenWindow;
            }
            else
            {
                ok = wr.Left == r.X && wr.Top == r.Y && wr.Right - wr.Left == r.W && wr.Bottom - wr.Top == r.H
                    && (style & WS_CAPTION) == 0 && Screen.fullScreenMode == FullScreenMode.Windowed;
            }
            if (!ok && s_misplacedLogs++ < 5)
                Plugin.Log.LogInfo($"Window moved off its place: at {wr.Left},{wr.Top} {wr.Right - wr.Left}x{wr.Bottom - wr.Top}, " +
                    $"style 0x{style:X}, mode {Screen.fullScreenMode} {Screen.width}x{Screen.height}; wanted {r}.");
            return ok;
        }

        private static int s_misplacedLogs;

        private static void Save()
        {
            IntPtr hwnd = Hwnd();
            s_savedMode = Screen.fullScreenMode;
            s_savedW = Screen.width;
            s_savedH = Screen.height;
            if (hwnd != IntPtr.Zero)
            {
                s_savedStyle = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
                s_savedExStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
                GetWindowRect(hwnd, out s_savedRect);
            }
            s_saved = true;

            // The game re-applies its own saved display settings at every boot, but only when it
            // has them. Make sure it does, so a co-op window size never becomes the next start's.
            if (!PlayerPrefs.HasKey("tc_settings_fullscreen"))
                PlayerPrefs.SetInt("tc_settings_fullscreen", (int)s_savedMode);
            if (PlayerPrefs.GetInt("tc_settings_resolution_width", 0) <= 0 || PlayerPrefs.GetInt("tc_settings_resolution_height", 0) <= 0)
            {
                PlayerPrefs.SetInt("tc_settings_resolution_width", s_savedW);
                PlayerPrefs.SetInt("tc_settings_resolution_height", s_savedH);
            }
            PlayerPrefs.Save();
        }

        private static void Restore()
        {
            if (!s_saved) return;
            s_saved = false;
            Plugin.Log.LogInfo($"Restoring Player 1's window ({s_savedMode} {s_savedW}x{s_savedH}).");
            IntPtr hwnd = Hwnd();
            if (s_savedMode == FullScreenMode.Windowed && hwnd != IntPtr.Zero)
            {
                SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(s_savedStyle));
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(s_savedExStyle));
                SetWindowPos(hwnd, IntPtr.Zero, s_savedRect.Left, s_savedRect.Top, s_savedRect.Right - s_savedRect.Left,
                    s_savedRect.Bottom - s_savedRect.Top, SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
            }
            Screen.SetResolution(s_savedW, s_savedH, s_savedMode);
        }

        // --- Player 1: decide everyone's rectangle ------------------------------------------

        public static List<KeyValuePair<int, Rect>> Plan(IList<int> players)
        {
            var result = new List<KeyValuePair<int, Rect>>();
            List<Monitor> monitors = Monitors();
            if (monitors.Count == 0) return result;

            // Player 1's monitor first, then the others left to right.
            Monitor home = monitors[0];
            IntPtr hwnd = Hwnd();
            if (hwnd != IntPtr.Zero)
            {
                IntPtr hm = MonitorFromWindow(hwnd, MONITOR_DEFAULTTOPRIMARY);
                home = monitors.Find(m => m.Handle == hm) ?? home;
            }
            var order = new List<Monitor> { home };
            foreach (Monitor m in monitors)
                if (m != home) order.Add(m);

            if (Plugin.Layout.Value == Mode.Auto && order.Count >= players.Count)
            {
                for (int i = 0; i < players.Count; i++) result.Add(new KeyValuePair<int, Rect>(players[i], order[i].Bounds));
                return result;
            }

            Rect b = home.Bounds;
            if (players.Count == 1)
            {
                result.Add(new KeyValuePair<int, Rect>(players[0], b));
            }
            else if (players.Count == 2)
            {
                bool stacked = Plugin.SplitDirection.Value == Split.Stacked;
                for (int i = 0; i < 2; i++)
                {
                    Rect r = stacked
                        ? new Rect { X = b.X, Y = b.Y + i * (b.H / 2), W = b.W, H = i == 0 ? b.H / 2 : b.H - b.H / 2 }
                        : new Rect { X = b.X + i * (b.W / 2), Y = b.Y, W = i == 0 ? b.W / 2 : b.W - b.W / 2, H = b.H };
                    result.Add(new KeyValuePair<int, Rect>(players[i], r));
                }
            }
            else
            {
                int halfW = b.W / 2, halfH = b.H / 2;
                for (int i = 0; i < players.Count && i < 4; i++)
                {
                    int col = i % 2, row = i / 2;
                    result.Add(new KeyValuePair<int, Rect>(players[i], new Rect
                    {
                        X = b.X + col * halfW,
                        Y = b.Y + row * halfH,
                        W = col == 0 ? halfW : b.W - halfW,
                        H = row == 0 ? halfH : b.H - halfH,
                    }));
                }
            }
            return result;
        }

        private sealed class Monitor
        {
            public IntPtr Handle;
            public Rect Bounds;
            public bool Primary;
        }

        private static List<Monitor> Monitors()
        {
            var list = new List<Monitor>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data) =>
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (GetMonitorInfo(hMon, ref info))
                {
                    list.Add(new Monitor
                    {
                        Handle = hMon,
                        Primary = (info.dwFlags & 1) != 0,
                        Bounds = new Rect { X = info.rcMonitor.Left, Y = info.rcMonitor.Top, W = info.rcMonitor.Right - info.rcMonitor.Left, H = info.rcMonitor.Bottom - info.rcMonitor.Top },
                    });
                }
                return true;
            }, IntPtr.Zero);
            list.Sort((a, b) => a.Primary != b.Primary ? (a.Primary ? -1 : 1) : a.Bounds.X != b.Bounds.X ? a.Bounds.X.CompareTo(b.Bounds.X) : a.Bounds.Y.CompareTo(b.Bounds.Y));
            return list;
        }

        // --- This process's game window -------------------------------------------------------

        public static IntPtr Hwnd()
        {
            if (s_hwnd != IntPtr.Zero && IsWindow(s_hwnd)) return s_hwnd;
            s_hwnd = IntPtr.Zero;
            uint pid = (uint)Process.GetCurrentProcess().Id;
            var cls = new StringBuilder(64);
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out uint owner);
                if (owner != pid || !IsWindowVisible(h)) return true;
                cls.Length = 0;
                GetClassName(h, cls, cls.Capacity);
                if (cls.ToString() != "UnityWndClass") return true;
                s_hwnd = h;
                return false;
            }, IntPtr.Zero);
            return s_hwnd;
        }

        // --- Win32 --------------------------------------------------------------------------

        private const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
        private const long WS_POPUP = 0x80000000L, WS_VISIBLE = 0x10000000L, WS_CAPTION = 0x00C00000L,
            WS_THICKFRAME = 0x00040000L, WS_SYSMENU = 0x00080000L, WS_MINIMIZEBOX = 0x00020000L,
            WS_MAXIMIZEBOX = 0x00010000L, WS_MAXIMIZE = 0x01000000L, WS_EX_NOACTIVATE = 0x08000000L;
        private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020, SWP_SHOWWINDOW = 0x0040;
        private const uint MONITOR_DEFAULTTOPRIMARY = 1, MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rc, IntPtr data);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);
    }
}
