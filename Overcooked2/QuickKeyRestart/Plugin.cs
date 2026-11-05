using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace QuickKeyRestart
{
    /// <summary>
    /// One key restarts the level you are playing, skipping the pause menu and its
    /// "are you sure?" dialog.
    ///
    /// The game is reached entirely by reflection: nothing here is compiled against
    /// Assembly-CSharp, so a game update that renames a type disables the mod with a
    /// log line instead of throwing on every frame.
    /// </summary>
    [BepInPlugin(Id, "Overcooked 2 - Chơi lại nhanh", "1.0.0")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "com.manhl.overcooked2.quickkeyrestart";

        /// <summary>
        /// A restart tears down and reloads the level, so a second one while that is
        /// still in flight can strand the match. Ignore repeats for this long.
        /// </summary>
        private const float RestartCooldown = 3f;

        private static ManualLogSource Log;

        // Resolved once from the loaded game assembly; null means "feature off".
        private static Type pauseMenuType;
        private static MethodInfo onRestartConfirmed;
        private static Type flowControllerType;
        private static bool resolved;

        private static float lastRestart = float.NegativeInfinity;

        private ConfigEntry<KeyboardShortcut> restartKey;

        private void Awake()
        {
            Log = Logger;
            restartKey = Config.Bind("General", "RestartKey",
                new KeyboardShortcut(KeyCode.F5),
                "Phím chơi lại màn đang chơi ngay lập tức. F5 tránh trùng với F6 của LobbyTools và F1/F2 của Host Force Theme.");

            Resolve();

            if (onRestartConfirmed == null)
            {
                Log.LogError("Không bật được chức năng chơi lại nhanh: không tìm thấy InGamePauseMenu.OnRestartConfirmed trong bản game này.");
                return;
            }
            Log.LogInfo("Đã tải mod chơi lại nhanh. Nhấn " + restartKey.Value + " khi đang chơi để chơi lại màn.");
        }

        /// <summary>
        /// Looks up the game types this mod drives. Runs once; failures are logged and
        /// leave the corresponding field null.
        /// </summary>
        private static void Resolve()
        {
            if (resolved) return;
            resolved = true;

            var game = FindGameAssembly();
            if (game == null)
            {
                Log.LogError("Không tìm thấy assembly Assembly-CSharp của game.");
                return;
            }

            pauseMenuType = game.GetType("InGamePauseMenu");
            if (pauseMenuType == null)
            {
                Log.LogError("Không tìm thấy kiểu InGamePauseMenu.");
                return;
            }

            // Non-public: this is the handler the confirmation dialog calls on "yes".
            onRestartConfirmed = pauseMenuType.GetMethod("OnRestartConfirmed",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (onRestartConfirmed == null)
            {
                Log.LogError("Không tìm thấy phương thức InGamePauseMenu.OnRestartConfirmed.");
                return;
            }

            // Optional: used only to tell "in a live level" from "on a menu or loading".
            flowControllerType = game.GetType("ServerFlowControllerBase");
            if (flowControllerType == null)
            {
                Log.LogWarning("Không tìm thấy ServerFlowControllerBase; bỏ qua bước kiểm tra đang trong màn.");
            }
        }

        private static Assembly FindGameAssembly()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (assembly.GetName().Name == "Assembly-CSharp") return assembly;
                }
                catch (Exception error)
                {
                    // A dynamic or partially loaded assembly can refuse GetName(); skip it.
                    Log.LogDebug("Bỏ qua một assembly không đọc được tên: " + error.Message);
                }
            }
            return null;
        }

        private void Update()
        {
            if (onRestartConfirmed == null) return;
            if (!WasRestartPressed()) return;

            if (Time.realtimeSinceStartup - lastRestart < RestartCooldown)
            {
                Log.LogInfo("Bỏ qua yêu cầu chơi lại: vừa chơi lại xong.");
                return;
            }

            RestartLevel();
        }

        private bool WasRestartPressed()
        {
            try
            {
                return restartKey.Value.IsDown();
            }
            catch (Exception error)
            {
                Log.LogWarning("Không đọc được phím đã cài trong cấu hình: " + error.Message);
                return false;
            }
        }

        private static void RestartLevel()
        {
            try
            {
                if (!IsLevelRunning())
                {
                    Log.LogInfo("Bỏ qua yêu cầu chơi lại: không ở trong màn đang chơi (đang tải?).");
                    return;
                }

                var menu = FindLivedPauseMenu();
                if (menu == null)
                {
                    Log.LogWarning("Bỏ qua yêu cầu chơi lại: không tìm thấy InGamePauseMenu trong scene.");
                    return;
                }

                lastRestart = Time.realtimeSinceStartup;
                Log.LogInfo("Đang chơi lại màn hiện tại.");
                onRestartConfirmed.Invoke(menu, null);
            }
            catch (Exception error)
            {
                Log.LogError("Không chơi lại được màn: " + error);
            }
        }

        /// <summary>
        /// True when a flow controller is active in the hierarchy, which is the game's own
        /// marker for "a level is running". Without the type, assume yes.
        /// </summary>
        private static bool IsLevelRunning()
        {
            if (flowControllerType == null) return true;

            foreach (var found in Resources.FindObjectsOfTypeAll(flowControllerType))
            {
                var component = found as Component;
                if (component != null && component.gameObject.activeInHierarchy) return true;
            }
            return false;
        }

        /// <summary>
        /// FindObjectsOfTypeAll also returns the menu's prefab templates, which are not in
        /// any scene and throw when driven. Take the first instance that belongs to a scene.
        /// </summary>
        private static UnityEngine.Object FindLivedPauseMenu()
        {
            foreach (var found in Resources.FindObjectsOfTypeAll(pauseMenuType))
            {
                var component = found as Component;
                if (component == null) continue;
                Scene scene = component.gameObject.scene;
                if (scene.IsValid()) return found;
            }
            return null;
        }
    }
}
