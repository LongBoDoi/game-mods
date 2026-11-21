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
    /// One plugin holding every tweak in this folder. Since 3.0.0 its settings live in the game's
    /// own Settings menu (<b>Settings → BỘ CÔNG CỤ</b>) instead of a separate F6 window: players and
    /// their ping, kick and ban, the corner ping overlay, force host, quick restart and the saved
    /// ban list, all on rows that look and navigate like the game's own.
    /// </summary>
    [BepInPlugin(Id, "Overcooked 2 - Bộ công cụ", Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "com.manhl.overcooked2.tweaks";
        public const string Version = "3.0.0";
        internal static ManualLogSource Log;

        // Lobby
        internal static ConfigEntry<bool> ForceHost;
        // Quick restart
        internal static ConfigEntry<bool> QuickRestartEnabled;
        internal static ConfigEntry<KeyboardShortcut> QuickRestartKey;
        // Ping overlay
        internal static ConfigEntry<bool> PingOverlay;
        internal static ConfigEntry<OverlayCorner> PingOverlayCorner;
        internal static ConfigEntry<KeyboardShortcut> PingOverlayKey;
        // Settings screen
        internal static ConfigEntry<KeyboardShortcut> PreviousTabKey;
        internal static ConfigEntry<KeyboardShortcut> NextTabKey;

        internal static bool ForceHostReady;
        internal static bool ConnectionMonitorReady;
        internal static bool PingRelayReady;
        internal static bool SettingsReady;
        internal static bool PlayerCardKickReady;
        internal static BanStore Bans;
        private Harmony harmony;
        private GameObject runtime;

        private void Awake()
        {
            Log = Logger;
            ForceHost = Config.Bind("Matchmaking", "ForceHost", false,
                "Tự tạo phòng Arcade/Versus thay vì tìm phòng của người khác. Áp dụng từ lần ghép trận tiếp theo.");
            QuickRestartEnabled = Config.Bind("QuickRestart", "Enabled", true,
                "Bật phím chơi lại nhanh. Bật/tắt trong Cài đặt → Bộ công cụ.");
            QuickRestartKey = Config.Bind("QuickRestart", "RestartKey",
                new KeyboardShortcut(KeyCode.F5),
                "Phím chơi lại màn đang chơi ngay lập tức, bỏ qua menu tạm dừng và câu hỏi xác nhận. Ví dụ tổ hợp: R + LeftControl.");
            PingOverlay = Config.Bind("PingOverlay", "Enabled", false,
                "Hiện độ trễ của mọi người chơi ở một góc màn hình.");
            PingOverlayCorner = Config.Bind("PingOverlay", "Corner", OverlayCorner.TopRight,
                "Góc màn hình hiện độ trễ: TopLeft, TopRight, BottomLeft, BottomRight.");
            PingOverlayKey = Config.Bind("PingOverlay", "ToggleKey",
                new KeyboardShortcut(KeyCode.F9),
                "Phím bật/tắt bảng độ trễ ở góc màn hình. F9 tránh trùng với F7/F8 của RandomLevel.");
            PreviousTabKey = Config.Bind("Settings", "PreviousTabKey", new KeyboardShortcut(KeyCode.Z),
                "Chuyển sang thẻ bên trái trong Cài đặt → OC2 TWEAKS. Tay cầm: LB.");
            NextTabKey = Config.Bind("Settings", "NextTabKey", new KeyboardShortcut(KeyCode.C),
                "Chuyển sang thẻ bên phải trong Cài đặt → OC2 TWEAKS. Tay cầm: RB.");

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
                SettingsScreen.Validate();
                harmony.PatchAll(typeof(SettingsHook));
                SettingsReady = true;
            }
            catch (Exception error)
            {
                Log.LogError("Không thêm được Bộ công cụ vào màn hình Cài đặt. Các chức năng vẫn chạy theo tệp cấu hình: " + error);
            }
            try
            {
                PlayerCards.Validate();
                PlayerCardKickReady = true;
                harmony.PatchAll(typeof(PlayerCards.AllowKick));
                harmony.PatchAll(typeof(PlayerCards.ShowKick));
                harmony.PatchAll(typeof(PlayerCards.KickPrompt));
            }
            catch (Exception error)
            {
                PlayerCardKickReady = false;
                Log.LogWarning("Không nối được nút Kick trên thẻ người chơi của game; vẫn đuổi được trong Cài đặt → OC2 Tweaks: " + error.Message);
            }
            try
            {
                ConnectionMonitor.Validate();
                harmony.PatchAll(typeof(PersistentPing));
                ConnectionMonitorReady = true;
            }
            catch (Exception error) { Log.LogError("Không thể đo độ trễ kết nối: " + error); }
            if (ConnectionMonitorReady)
            {
                try
                {
                    harmony.PatchAll(typeof(PingRelayReceive));
                    PingRelayReady = true;
                }
                catch (Exception error) { Log.LogError("Không thể chia sẻ độ trễ giữa chủ phòng và người chơi: " + error); }
            }
            try
            {
                harmony.PatchAll(typeof(ForceHostMatchmaking));
                ForceHostReady = true;
            }
            catch (Exception error) { Log.LogError("Không thể bật chức năng làm chủ phòng: " + error); }

            // Reflection, not a patch: nothing to unpatch, so a missing method only switches this
            // one tweak off and leaves the rest working.
            if (!QuickRestart.Ready)
                Log.LogError("Không bật được chức năng chơi lại nhanh: không tìm thấy InGamePauseMenu.OnRestartConfirmed trong bản game này.");

            runtime = new GameObject("OC2Tweaks.Runtime");
            runtime.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(runtime);
            runtime.AddComponent<Runtime>();
            Log.LogInfo("Đã tải bộ công cụ Overcooked 2 " + Version + ". Mở Cài đặt → Bộ công cụ để chỉnh." +
                (QuickRestart.Ready && QuickRestartEnabled.Value
                    ? " Nhấn " + QuickRestartKey.Value + " khi đang chơi để chơi lại màn."
                    : ""));
        }

        private void OnDestroy()
        {
            if (runtime != null) Destroy(runtime);
            if (harmony != null) harmony.UnpatchSelf();
            ForceHostReady = false;
            ConnectionMonitorReady = false;
            PingRelayReady = false;
            SettingsReady = false;
            PlayerCardKickReady = false;
        }
    }
}
