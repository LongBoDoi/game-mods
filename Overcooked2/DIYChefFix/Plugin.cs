using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

namespace DIYChefFix
{
    /// <summary>
    /// DIYChef Fix: repairs the default chef and the chef grid of the third-party DIYChef FastInit
    /// plugin (dev.gua.overcooked.diychef 2.4.0) without changing its DLL. See Fixes for what is
    /// wrong and how each part is fixed.
    /// </summary>
    [BepInPlugin(Id, "DIYChef Fix", Version)]
    [BepInDependency("com.manhl.overcooked2.tiengviet", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(DiyChef.Guid)]
    [BepInProcess("Overcooked2.exe")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "com.manhl.overcooked2.diycheffix";
        public const string Version = "1.0.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> ShowAllChefs;
        internal static ConfigEntry<bool> LoadAllCustomChefs;
        internal static ConfigEntry<bool> UnlockBaseGameChefs;

        private Harmony _harmony;
        private bool _running;

        private void Awake()
        {
            Log = Logger;
            ShowAllChefs = Config.Bind("Chefs", "ShowAllChefs", true,
                Lang.T("Offer every unlocked chef in DIYChef's chef grid, carousel and lobby switch, after the chefs listed in prefer.txt. Off: only the prefer.txt chefs, like DIYChef on its own.",
                    "Hiện mọi đầu bếp đã mở khóa trong lưới chọn, vòng chọn và nút đổi ở sảnh của DIYChef, sau các đầu bếp trong prefer.txt. Tắt: chỉ các đầu bếp trong prefer.txt, như DIYChef gốc."));
            LoadAllCustomChefs = Config.Bind("Chefs", "LoadAllCustomChefs", true,
                Lang.T("Load every custom chef folder in DIYChef's Resources folder, even ones prefer.txt doesn't list. Off: only the ones prefer.txt lists.",
                    "Nạp mọi thư mục đầu bếp tự làm trong thư mục Resources của DIYChef, kể cả khi prefer.txt không ghi. Tắt: chỉ những cái prefer.txt ghi."));
            UnlockBaseGameChefs = Config.Bind("Chefs", "UnlockBaseGameChefs", true,
                Lang.T("Base-game chefs your save hasn't unlocked yet can be picked and used as the default chef. DLC chefs still need their DLC. Nothing is written to the save.",
                    "Có thể chọn (và đặt làm mặc định) các đầu bếp của game gốc mà bản lưu chưa mở khóa. Đầu bếp DLC vẫn cần có DLC. Không ghi gì vào bản lưu."));

            string problem;
            if (!DiyChef.Init(out problem))
            {
                Log.LogError("This DIYChef version isn't the one DIYChef Fix knows (2.4.0): " + problem + ". Nothing is changed.");
                return;
            }

            _harmony = new Harmony(Id);
            try
            {
                _harmony.Patch(DiyChef.LoadAllCustomChefs, prefix: new HarmonyMethod(typeof(Fixes), "BeforeLoadCustomChefs"));
                _harmony.Patch(DiyChef.ConfigureDefaults, prefix: new HarmonyMethod(typeof(Fixes), "ConfigureDefaults"));
                _harmony.Patch(DiyChef.GridShow, postfix: new HarmonyMethod(typeof(Fixes), "AfterGridShow"));
                _harmony.Patch(AccessTools.Method(typeof(MetaGameProgress), "GetUnlockedAvatars"),
                    postfix: new HarmonyMethod(typeof(Fixes), "AddBaseGameChefs"));
            }
            catch (Exception e)
            {
                Log.LogError("Patching DIYChef failed; nothing is changed: " + e);
                _harmony.UnpatchSelf();
                return;
            }
            _running = true;
            Log.LogInfo("DIYChef Fix " + Version + " ready (DIYChef folder: " + DiyChef.Folder + ")");
        }

        private void Update()
        {
            if (_running) Fixes.Poll();
        }

        private void OnDestroy()
        {
            if (_harmony != null) _harmony.UnpatchSelf();
        }
    }
}
