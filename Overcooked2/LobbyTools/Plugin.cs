using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace LobbyTools
{
    [BepInPlugin(Id, "Overcooked 2 - Quản lý phòng", "1.0.0")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "com.manhl.overcooked2.lobbytools";
        internal static ManualLogSource Log;
        internal static ConfigEntry<KeyCode> ToggleKey;
        internal static ConfigEntry<bool> ForceHost;
        internal static bool ForceHostReady;
        internal static bool ConnectionMonitorReady;
        internal static BanStore Bans;
        private Harmony harmony;
        private GameObject ui;

        private void Awake()
        {
            Log = Logger;
            ToggleKey = Config.Bind("General", "ToggleKey", KeyCode.F6,
                "Mở/đóng bảng quản lý phòng. F6 tránh trùng với F1/F2 của Host Force Theme và F5 của QuickKeyRestart.");
            ForceHost = Config.Bind("Matchmaking", "ForceHost", false,
                "Tự tạo phòng Arcade/Versus thay vì tìm phòng của người khác. Áp dụng từ lần ghép trận tiếp theo.");
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
            catch (Exception error) { Log.LogWarning("Không thể chặn thao tác vào menu phía sau bảng quản lý: " + error); }
            try
            {
                harmony.PatchAll(typeof(ForceHostMatchmaking));
                ForceHostReady = true;
            }
            catch (Exception error) { Log.LogError("Không thể bật chức năng làm chủ phòng: " + error); }

            ui = new GameObject("LobbyTools.UI");
            ui.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(ui);
            ui.AddComponent<LobbyUI>();
            Log.LogInfo("Đã tải mod quản lý phòng. Nhấn " + ToggleKey.Value + " để quản lý người chơi khi làm chủ phòng. Tài khoản đã cấm sẽ tự động bị đuổi nếu gặp lại.");
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
        private static bool Prefix() { return !LobbyUI.BlockMenuInput; }
    }
}
