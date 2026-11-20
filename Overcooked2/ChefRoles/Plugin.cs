using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ChefRoles
{
    /// <summary>
    /// Chef Roles: after a kitchen finishes loading, each local player picks a role (or none) on a
    /// role-select screen built from the game's own pause-menu panel. Roles change cook, chop, serve
    /// and run speed for that player's chef for the rest of the level (see Patches.cs).
    /// </summary>
    [BepInPlugin(Id, "Chef Roles", "1.0.0")]
    [BepInProcess("Overcooked2.exe")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "com.manhl.overcooked2.chefroles";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<KeyboardShortcut> SkipKey;
        internal static ConfigEntry<float> AutoConfirmSeconds;
        internal static readonly RoleManager Roles = new RoleManager();

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            Enabled = Config.Bind("General", "Enabled", true,
                "Bật màn chọn vai trò trước mỗi màn và hiệu ứng của vai trò.");
            SkipKey = Config.Bind("General", "SkipRolesKey", new KeyboardShortcut(KeyCode.F9),
                "Bỏ qua màn chọn vai trò: mọi người chơi dùng vai trò mặc định (không vai trò).");
            AutoConfirmSeconds = Config.Bind("General", "AutoConfirmSeconds", 30f,
                "Sau bao nhiêu giây thì tự động xác nhận lựa chọn hiện tại (0 = chờ đến khi mọi người sẵn sàng). " +
                "Khi chơi online, máy chủ chờ mọi người chơi xong phần mở màn, nên đừng để 0.");
            RoleStats.Bind(Config);

            _harmony = new Harmony(Id);
            try
            {
                _harmony.PatchAll(typeof(Plugin).Assembly);
                Log.LogInfo("Đã tải Chef Roles: mỗi màn sẽ hiển thị màn chọn vai trò trước khi bắt đầu.");
            }
            catch (Exception error)
            {
                _harmony.UnpatchSelf();
                Log.LogError("Không thể bật Chef Roles: " + error);
            }
        }

        private void Update()
        {
            if (RoleSelectScreen.IsOpen && SkipKey.Value.IsDown())
                RoleSelectScreen.RequestSkip();
        }

        private void OnDestroy()
        {
            if (_harmony != null) _harmony.UnpatchSelf();
        }
    }
}
