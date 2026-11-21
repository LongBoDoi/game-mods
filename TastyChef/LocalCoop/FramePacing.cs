using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;

namespace LocalCoop
{
    // Every window shares one GPU, and VSync does not hold these windows back: measured on a
    // 144 Hz + 100 Hz pair, the window in front ran at ~270 fps and starved the other one down
    // to 11-18 fps with 300 ms stalls (Windows also favours the foreground app on the GPU).
    //
    // While co-op is on, each window is capped at its own monitor's refresh rate (or the game's
    // own frame-rate limit when that is lower), so no window draws frames nobody can see. Guests
    // also run at above-normal priority and opt out of Windows' power throttling of background
    // processes, so being behind Player 1's window does not slow them down.
    internal static class FramePacing
    {
        private static bool s_applied;
        private static int s_cap;
        private static float s_nextCheck;
        private static int s_savedVSync, s_savedTarget;

        public static void Install(Harmony harmony)
        {
            // The game re-applies its own pacing whenever display settings change.
            harmony.Patch(AccessTools.Method(typeof(GameSettings), nameof(GameSettings.ApplyFrameRatePreference)),
                postfix: new HarmonyMethod(typeof(FramePacing), nameof(AfterGamePacing)));
            if (Coop.IsGuest) BoostThisProcess();
        }

        private static void AfterGamePacing()
        {
            if (!s_applied) return;
            s_savedVSync = QualitySettings.vSyncCount;
            s_savedTarget = Application.targetFrameRate;
            Apply(force: true);
        }

        public static void Update()
        {
            if (!Coop.Active)
            {
                if (s_applied) Restore();
                return;
            }
            if (Time.realtimeSinceStartup < s_nextCheck) return;
            s_nextCheck = Time.realtimeSinceStartup + 1f;   // the window may have moved monitors
            if (!s_applied)
            {
                s_savedVSync = QualitySettings.vSyncCount;
                s_savedTarget = Application.targetFrameRate;
                s_applied = true;
            }
            Apply(force: false);
        }

        private static void Apply(bool force)
        {
            int cap = Cap();
            if (!force && cap == s_cap && QualitySettings.vSyncCount == 0 && Application.targetFrameRate == cap) return;
            if (cap != s_cap) Plugin.Log.LogInfo($"Frame rate capped at {cap} fps while playing together.");
            s_cap = cap;
            // Unity ignores targetFrameRate while VSync is on; VSync does not pace these windows anyway.
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = cap;
        }

        private static int Cap()
        {
            int configured = Plugin.FrameRateCap.Value;
            if (configured > 0) return configured;
            double hz = Screen.currentResolution.refreshRateRatio.value;
            int cap = double.IsNaN(hz) || hz < 30 ? 60 : (int)Math.Round(hz);
            int gameLimit = GameSettings.FrameRateLimit;   // the game's own setting; -1 = none
            if (gameLimit > 0) cap = Math.Min(cap, gameLimit);
            return cap;
        }

        private static void Restore()
        {
            s_applied = false;
            s_cap = 0;
            QualitySettings.vSyncCount = s_savedVSync;
            Application.targetFrameRate = s_savedTarget;
            Plugin.Log.LogInfo("Frame rate back to the game's own setting.");
        }

        private static void BoostThisProcess()
        {
            try
            {
                Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not raise this window's priority: " + e.Message);
            }
            try
            {
                // ProcessPowerThrottling: control execution speed, state "not throttled".
                var state = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = 1, StateMask = 0 };
                if (!SetProcessInformation(GetCurrentProcess(), 4, ref state, Marshal.SizeOf(typeof(PROCESS_POWER_THROTTLING_STATE))))
                    Plugin.Log.LogWarning("Could not opt out of power throttling (error " + Marshal.GetLastWin32Error() + ").");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not opt out of power throttling: " + e.Message);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }

        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);
    }
}
