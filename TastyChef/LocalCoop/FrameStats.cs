using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;

namespace LocalCoop
{
    // While co-op is on, every window logs its frame rate every 10 seconds, so a window that
    // stutters can be told apart from one that is simply slow (and from the others).
    internal static class FrameStats
    {
        private static int s_frames;
        private static float s_worst, s_start = -1f;

        public static void Update()
        {
            if (!Coop.Active) { s_start = -1f; return; }
            float now = Time.realtimeSinceStartup;
            if (s_start < 0f) { s_start = now; s_frames = 0; s_worst = 0f; return; }
            s_frames++;
            s_worst = Mathf.Max(s_worst, Time.unscaledDeltaTime);
            if (now - s_start < 10f) return;

            float fps = s_frames / (now - s_start);
            IntPtr hwnd = WindowLayout.Hwnd();
            bool front = hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd;
            string priority;
            try { priority = Process.GetCurrentProcess().PriorityClass.ToString(); } catch { priority = "?"; }
            Plugin.Log.LogInfo($"Frames: {fps:F0} fps, worst {s_worst * 1000f:F0} ms | vSync {QualitySettings.vSyncCount}, cap {Application.targetFrameRate}, " +
                $"monitor {Screen.currentResolution.refreshRateRatio.value:F0} Hz, {Screen.fullScreenMode} {Screen.width}x{Screen.height}, " +
                $"{(front ? "in front" : "behind")}, priority {priority}");
            s_start = now;
            s_frames = 0;
            s_worst = 0f;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    }
}
