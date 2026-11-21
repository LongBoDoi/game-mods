using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using OC2DIYLevel;
using OC2DIYLevel.Extension;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RandomLevel
{
    [BepInPlugin(Id, "Overcooked 2 - Màn ngẫu nhiên", "1.1.3")]
    [BepInDependency("dev.gua.overcooked.diylevel", "0.10.0")]
    [BepInProcess("Overcooked2.exe")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "com.manhl.overcooked2.randomlevel";
        internal const string DiyId = "dev.gua.overcooked.diylevel";
        internal static ManualLogSource Log;
        internal static bool Enabled;
        internal static bool SelectingRandom;
        internal static readonly RandomRotation Rotation = new RandomRotation(new System.Random());
        internal static readonly MethodInfo SelectLevel = AccessTools.Method(typeof(DIYLevelEntryUI), "OnLevelSelected", new[] { typeof(string) });
        private Harmony harmony;
        private ConfigEntry<KeyboardShortcut> exitKey;
        private ConfigEntry<KeyboardShortcut> skipKey;
        private GUIStyle resultsHintStyle;
        internal static StarRatingUIController Results;
        internal static bool ResultsInputReady;

        private void Awake()
        {
            Log = Logger;
            exitKey = Config.Bind("General", "ExitResultsKey", new KeyboardShortcut(KeyCode.F7),
                "Về menu chính từ bảng kết quả khi đang chơi ngẫu nhiên.");
            skipKey = Config.Bind("General", "SkipKey", new KeyboardShortcut(KeyCode.F8),
                "Bỏ qua màn đang chơi để tải ngay một màn ngẫu nhiên khác. Chỉ chủ phòng; cần ít nhất 2 màn hợp lệ.");
            harmony = new Harmony(Id);
            try
            {
                if (SelectLevel == null) throw new MissingMethodException("DIYLevelEntryUI.OnLevelSelected(string)");
                harmony.PatchAll(typeof(Plugin).Assembly);
                SceneManager.sceneLoaded += SceneLoaded;
                Log.LogInfo("Đã tải RandomLevel. More Levels → Chơi ngẫu nhiên — tất cả màn. " + skipKey.Value + " bỏ qua màn; Continue chuyển màn; Restart chơi lại; " + exitKey.Value + " về menu chính ở bảng kết quả.");
            }
            catch (Exception error)
            {
                harmony.UnpatchSelf();
                Log.LogError("Không thể bật RandomLevel: " + error);
            }
        }

        internal static bool IsDiySession()
        {
            var session = GameUtils.GetGameSession();
            return session != null && session.DLC == 15;
        }

        internal static bool CanControl()
        {
            // Custom Arcade has its own matchmaking / rotation; this mod serves More Levels.
            return IsDiySession() && ServerGameSetup.Mode == GameMode.Campaign &&
                !OC2DIYLevel.CustomArcade.SteamLobbyManager.isHosting && AnalyticsPublicizer.IsServer();
        }

        internal static void Reset()
        {
            Enabled = false;
            Results = null;
            ResultsInputReady = false;
            Rotation.ClearPending();
            SkipLevel.Reset();
        }

        private static void SceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode != LoadSceneMode.Single) return;
            Results = null;
            ResultsInputReady = false;
            Rotation.ClearPending();
            if (scene.name == "StartScreen" || scene.name == "Lobbies") Reset();
        }

        internal static void StartRandom()
        {
            if (!AnalyticsPublicizer.IsServer()) return;
            try
            {
                var scenes = new List<string>();
                if (DIYLevelAssetBundleManager.levelSetInfos == null) return;
                foreach (var set in DIYLevelAssetBundleManager.levelSetInfos)
                {
                    if (set.Value == null || set.Value.levelInfos == null) continue;
                    foreach (var level in set.Value.levelInfos)
                    {
                        if (level == null || string.IsNullOrEmpty(level.sceneName)) continue;
                        // Match the loader's lower-case asset bundle lookup; omit missing bundles.
                        if (File.Exists(Path.Combine(set.Key, level.sceneName.ToLowerInvariant())))
                            scenes.Add(level.sceneName);
                        else Log.LogWarning("Bỏ qua màn thiếu asset bundle: " + level.sceneName);
                    }
                }
                Rotation.SetLevels(scenes);
                string next = Rotation.Next(new object(), null);
                if (next == null)
                {
                    Log.LogWarning("Không có màn DIY hợp lệ để chọn ngẫu nhiên.");
                    return;
                }
                SelectingRandom = true;
                // Use the pack's own save-slot picker, preserving scores and local/online setup.
                SelectLevel.Invoke(null, new object[] { next });
                Enabled = true;
                Log.LogInfo("Bắt đầu lượt ngẫu nhiên: " + Rotation.Count + " màn; màn đầu: " + next);
            }
            catch (Exception error)
            {
                Reset();
                Log.LogError("Không bắt đầu được màn ngẫu nhiên: " + error);
            }
            finally { SelectingRandom = false; }
        }

        private static bool ShowResults()
        {
            return Enabled && Results != null && Results.gameObject.activeInHierarchy && CanControl();
        }

        private void Update()
        {
            if (Enabled && CanControl() && skipKey.Value.IsDown()) SkipLevel.TrySkip(Time.realtimeSinceStartup);
            if (ShowResults() && ResultsInputReady && Results.AllowedToSkip() && exitKey.Value.IsDown()) ExitResults();
        }

        private static void ExitResults()
        {
            Reset();
            GameUtils.QuitLevel();
        }

        private void OnGUI()
        {
            if (!ShowResults()) return;
            if (resultsHintStyle == null)
                resultsHintStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleRight, fontSize = 22 };
            float scale = Mathf.Min(Screen.width / 1920f, Screen.height / 1080f);
            var oldMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1));
            resultsHintStyle.normal.textColor = ResultsInputReady ? Color.white : Color.gray;
            GUI.Label(new Rect(Screen.width / scale - 570, 18, 550, 36),
                exitKey.Value + " — Về menu chính", resultsHintStyle);
            GUI.matrix = oldMatrix;
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= SceneLoaded;
            if (harmony != null) harmony.UnpatchSelf();
            Reset();
        }
    }
}
