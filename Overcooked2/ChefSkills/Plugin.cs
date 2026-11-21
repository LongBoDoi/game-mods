using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ChefSkills
{
    /// <summary>
    /// Chef Skills: after a kitchen finishes loading, each player shapes their chef on a
    /// skill-select screen built from the game's own dialog art, raising some skills by lowering
    /// others so the total always stays at zero. Skills change cook, burn, chop, wash, serve, run,
    /// dash, throw and firefighting speed for that player's chef for the rest of the level (see
    /// Patches.cs).
    /// </summary>
    [BepInPlugin(Id, "Chef Skills", "2.0.0")]
    [BepInProcess("Overcooked2.exe")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "com.manhl.overcooked2.chefskills";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static readonly SkillManager Skills = new SkillManager();

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            Enabled = Config.Bind("General", "Enabled", true,
                "Bật màn chọn kỹ năng trước mỗi màn và hiệu ứng của kỹ năng.");
            SkillStats.Bind(Config);

            _harmony = new Harmony(Id);
            try
            {
                _harmony.PatchAll(typeof(Plugin).Assembly);
                Log.LogInfo("Đã tải Chef Skills: mỗi màn sẽ hiển thị màn chọn kỹ năng trước khi bắt đầu.");
            }
            catch (Exception error)
            {
                _harmony.UnpatchSelf();
                Log.LogError("Không thể bật Chef Skills: " + error);
            }
        }

        private void Update()
        {
            SkillSync.Tick();
        }

        private void OnDestroy()
        {
            if (_harmony != null) _harmony.UnpatchSelf();
        }
    }
}
