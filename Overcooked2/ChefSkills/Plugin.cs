using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

[assembly: System.Reflection.AssemblyVersion("4.3.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("4.3.0.0")]

namespace ChefSkills
{
    /// <summary>
    /// Chef Skills: after a kitchen finishes loading, each player shapes their chef on a
    /// skill-select screen built from the game's own dialog art, raising some skills by lowering
    /// others so the total always stays at zero. Skills change cook, burn, chop, wash, serve, run,
    /// dash, throw and firefighting speed for that player's chef for the rest of the level (see
    /// Patches.cs).
    /// </summary>
    [BepInPlugin(Id, "Chef Skills", "4.3.0")]
    // Loads after TiengViet when it is installed, so the language it sets applies from the start.
    [BepInDependency("com.manhl.overcooked2.tiengviet", BepInDependency.DependencyFlags.SoftDependency)]
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
            SpecialCatalog.Bind(Config);
            SpecialInput.Bind(Config);
            SkillBarSettings.Bind(Config);

            _harmony = new Harmony(Id);
            try
            {
                // The core patches (skill screen, the nine skills, dash sync) all work or the
                // plugin turns itself off. Each special's patches are applied on their own, so one
                // that doesn't fit this game version only turns that special off.
                var specials = new List<Type>();
                foreach (Type type in typeof(Plugin).Assembly.GetTypes())
                {
                    if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
                    if (type.Namespace == SpecialNamespace) specials.Add(type);
                    else _harmony.CreateClassProcessor(type).Patch();
                }
                int failed = 0;
                foreach (Type type in specials)
                {
                    try
                    {
                        _harmony.CreateClassProcessor(type).Patch();
                    }
                    catch (Exception error)
                    {
                        failed++;
                        Log.LogWarning("Không áp dụng được " + type.Name + " (kỹ năng đặc biệt đó sẽ không hoạt động): " + error.Message);
                    }
                }
                SpecialRuntime.RegisterModules();
                Log.LogInfo("Đã tải Chef Skills: mỗi màn sẽ hiển thị màn chọn kỹ năng trước khi bắt đầu (" +
                    (specials.Count - failed) + "/" + specials.Count + " bản vá kỹ năng đặc biệt).");
            }
            catch (Exception error)
            {
                _harmony.UnpatchSelf();
                Log.LogError("Không thể bật Chef Skills: " + error);
            }
        }

        /// <summary>Namespace of the special-skill modules, whose patches may fail one by one.</summary>
        internal const string SpecialNamespace = "ChefSkills.Specials";

        private void Update()
        {
            SkillSync.Tick();
            SpecialNet.Poll();
            try
            {
                SpecialRuntime.Update();
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Update", error);
            }
        }

        private void OnDestroy()
        {
            if (_harmony != null) _harmony.UnpatchSelf();
        }
    }
}
