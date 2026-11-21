using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OC2Tweaks
{
    /// <summary>
    /// One key restarts the level you are playing, doing what Pause -> Restart -> yes does
    /// without the menu or the confirmation dialog.
    ///
    /// Was its own plugin (QuickKeyRestart); it is a plain class here so the panel can turn it
    /// off and rebind it in game.
    /// </summary>
    internal static class QuickRestart
    {
        /// <summary>
        /// A restart tears down and reloads the level, so a second one while that is still in
        /// flight can strand the match. Repeats inside this window are dropped.
        /// </summary>
        internal const float Cooldown = 3f;

        // The handler the pause menu's "restart?" dialog calls on yes. Non-public, hence
        // AccessTools; null when a game update renames it, which switches this tweak off and
        // leaves the rest of the panel alone.
        private static readonly MethodInfo OnRestartConfirmed =
            AccessTools.Method(typeof(InGamePauseMenu), "OnRestartConfirmed", Type.EmptyTypes);

        private static float lastRestart = float.NegativeInfinity;

        internal static bool Ready { get { return OnRestartConfirmed != null; } }

        /// <summary>Seconds left on the cooldown, 0 when a restart would go through now.</summary>
        internal static float Cooling(float now)
        {
            return Mathf.Max(0f, Cooldown - (now - lastRestart));
        }

        /// <summary>
        /// Called every frame while the panel is closed. Reads the key itself rather than taking
        /// a bool, so a bad binding in the config file cannot throw on every frame.
        /// </summary>
        internal static void Tick(float now)
        {
            if (!Ready || !Plugin.QuickRestartEnabled.Value) return;

            bool pressed;
            try { pressed = Plugin.QuickRestartKey.Value.IsDown(); }
            catch (Exception error)
            {
                Plugin.Log.LogWarning("Không đọc được phím đã cài trong cấu hình: " + error.Message);
                return;
            }
            if (!pressed) return;

            string result = Restart(now);
            if (result != null) Plugin.Log.LogInfo(result);
        }

        /// <summary>
        /// Restarts the running level. Returns a line to show and log, describing what happened,
        /// so the Phím tắt tab can say why a press did nothing.
        /// </summary>
        internal static string Restart(float now)
        {
            if (!Ready) return "Không bật được chức năng chơi lại nhanh trong bản game này.";
            if (!Plugin.QuickRestartEnabled.Value) return "Chức năng chơi lại nhanh đang tắt.";

            float cooling = Cooling(now);
            if (cooling > 0f)
                return "Bỏ qua yêu cầu chơi lại: vừa chơi lại xong (chờ " +
                    cooling.ToString("0.0") + " giây).";

            try
            {
                if (!IsLevelRunning())
                    return "Bỏ qua yêu cầu chơi lại: không ở trong màn đang chơi (đang tải?).";

                InGamePauseMenu menu = FindLivePauseMenu();
                if (menu == null)
                    return "Bỏ qua yêu cầu chơi lại: không tìm thấy InGamePauseMenu trong scene.";

                lastRestart = now;
                OnRestartConfirmed.Invoke(menu, null);
                return "Đang chơi lại màn hiện tại.";
            }
            catch (Exception error)
            {
                Plugin.Log.LogError("Không chơi lại được màn: " + error);
                return "Không chơi lại được màn: " + error.Message;
            }
        }

        /// <summary>
        /// True when a flow controller is active in the hierarchy, which is the game's own marker
        /// for "a level is running". Restarting during a scene change is what strands a match on
        /// a black screen, so this is checked first.
        /// </summary>
        internal static bool IsLevelRunning()
        {
            foreach (ServerFlowControllerBase found in
                Resources.FindObjectsOfTypeAll<ServerFlowControllerBase>())
                if (found != null && found.gameObject.activeInHierarchy) return true;
            return false;
        }

        /// <summary>
        /// FindObjectsOfTypeAll also returns the menu's prefab templates, which belong to no
        /// scene and throw when driven. Take the first instance that is in a scene.
        /// </summary>
        private static InGamePauseMenu FindLivePauseMenu()
        {
            foreach (InGamePauseMenu found in Resources.FindObjectsOfTypeAll<InGamePauseMenu>())
            {
                if (found == null) continue;
                Scene scene = found.gameObject.scene;
                if (scene.IsValid()) return found;
            }
            return null;
        }
    }
}
