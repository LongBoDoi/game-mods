using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace OC2Tweaks
{
    /// <summary>
    /// One plugin holding every tweak in this folder, each switchable from its own tab in the
    /// panel. It replaces the separate LobbyTools and QuickKeyRestart plugins: two plugins meant
    /// two config files, two hotkeys documented in two places, and nowhere in game to turn either
    /// off. The installer migrates both of their configs onto this one.
    /// </summary>
    [BepInPlugin(Id, "Overcooked 2 - Bộ công cụ", "2.0.0")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "com.manhl.overcooked2.tweaks";
        internal static ManualLogSource Log;

        // Panel
        internal static ConfigEntry<KeyCode> ToggleKey;
        // Lobby
        internal static ConfigEntry<bool> ForceHost;
        // Quick restart
        internal static ConfigEntry<bool> QuickRestartEnabled;
        internal static ConfigEntry<KeyboardShortcut> QuickRestartKey;

        internal static bool ForceHostReady;
        internal static bool ConnectionMonitorReady;
        internal static BanStore Bans;
        private Harmony harmony;
        private GameObject ui;

        private void Awake()
        {
            Log = Logger;
            ToggleKey = Config.Bind("General", "ToggleKey", KeyCode.F6,
                "Mở/đóng bảng công cụ. F6 tránh trùng với F1/F2 của Host Force Theme.");
            ForceHost = Config.Bind("Matchmaking", "ForceHost", false,
                "Tự tạo phòng Arcade/Versus thay vì tìm phòng của người khác. Áp dụng từ lần ghép trận tiếp theo.");
            QuickRestartEnabled = Config.Bind("QuickRestart", "Enabled", true,
                "Bật phím chơi lại nhanh. Có thể bật/tắt trong thẻ Phím tắt của bảng công cụ.");
            QuickRestartKey = Config.Bind("QuickRestart", "RestartKey",
                new KeyboardShortcut(KeyCode.F5),
                "Phím chơi lại màn đang chơi ngay lập tức, bỏ qua menu tạm dừng và câu hỏi xác nhận.");

            Bans = new BanStore(Path.Combine(Paths.ConfigPath, Id + ".bans.tsv"));
            try
            {
                Bans.Load();
                Log.LogInfo("Đã tải danh sách cấm: " + Bans.Snapshot().Length + " tài khoản.");
            }
            catch (Exception error) { Log.LogError("Không thể tải danh sách cấm: " + error.Message); }

            harmony = new Harmony(Id);
            try
            {
                ConnectionMonitor.Validate();
                harmony.PatchAll(typeof(PersistentPing));
                ConnectionMonitorReady = true;
            }
            catch (Exception error) { Log.LogError("Không thể đo độ trễ kết nối: " + error); }
            try { harmony.PatchAll(typeof(MenuInputGuard)); }
            catch (Exception error) { Log.LogWarning("Không thể chặn thao tác vào menu phía sau bảng công cụ: " + error); }
            try
            {
                harmony.PatchAll(typeof(ForceHostMatchmaking));
                ForceHostReady = true;
            }
            catch (Exception error) { Log.LogError("Không thể bật chức năng làm chủ phòng: " + error); }

            // Reflection, not a patch: nothing to unpatch, so a missing method only switches this
            // one tweak off and leaves the rest of the panel working.
            if (!QuickRestart.Ready)
                Log.LogError("Không bật được chức năng chơi lại nhanh: không tìm thấy InGamePauseMenu.OnRestartConfirmed trong bản game này.");

            ui = new GameObject("OC2Tweaks.UI");
            ui.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(ui);
            ui.AddComponent<ToolsUI>();
            Log.LogInfo("Đã tải bộ công cụ Overcooked 2. Nhấn " + ToggleKey.Value +
                " để mở bảng công cụ." +
                (QuickRestart.Ready && QuickRestartEnabled.Value
                    ? " Nhấn " + QuickRestartKey.Value + " khi đang chơi để chơi lại màn."
                    : ""));
        }

        private void OnDestroy()
        {
            if (ui != null) Destroy(ui);
            if (harmony != null) harmony.UnpatchSelf();
            ForceHostReady = false;
            ConnectionMonitorReady = false;
        }
    }

    [HarmonyPatch(typeof(T17StandaloneInputModule), "Process")]
    internal static class MenuInputGuard
    {
        private static bool Prefix() { return !ToolsUI.BlockMenuInput; }
    }
}
