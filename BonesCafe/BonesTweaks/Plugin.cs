using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace BonesTweaks
{
    // Bone's Cafe Tweaks: an in-game panel (F1) with switchable tweaks.
    //   - Fast Act hold (HoldAct.cs): holding Act repeats chop/grind faster than the game's 4/s.
    //   - Infinite ingredients (InfiniteIngredients.cs): the premium fridge never runs out.
    // Both start OFF; the panel (TweakUI.cs) turns them on. Once either has been on, leaderboard
    // uploads are skipped for the rest of the session (LeaderboardGuard.cs).
    [BepInPlugin(Guid, "Bone's Cafe Tweaks", "1.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "manhl.bonescafe.tweaks";

        internal static ManualLogSource Log;
        internal static ConfigFile Cfg;

        internal static ConfigEntry<string> ToggleKey;
        internal static ConfigEntry<bool> FastAct;
        internal static ConfigEntry<float> RepeatInterval;
        internal static ConfigEntry<float> InitialDelay;
        internal static ConfigEntry<bool> InfiniteIngredients;
        internal static ConfigEntry<bool> KeepLeaderboardsClean;

        /// <summary>True once any tweak has been switched on in this game session.</summary>
        internal static bool TweaksUsed;

        private void Awake()
        {
            Log = Logger;
            Cfg = Config;
            // Sliders change a value every frame while dragged; the panel saves on release instead.
            Config.SaveOnConfigSet = false;

            ToggleKey = Config.Bind("General", "ToggleKey", "F1",
                "Key that opens and closes the panel (Unity Input System key name, e.g. F1, F2, Insert).");

            FastAct = Config.Bind("FastAct", "Enabled", false,
                "Holding Act (J / gamepad X) repeats chop, grind... at the speed below. Off = the game's own 4 per second.");
            RepeatInterval = Config.Bind("FastAct", "RepeatInterval", 0.1f,
                new ConfigDescription("Seconds between repeats while Act is held. Vanilla is 0.25.",
                    new AcceptableValueRange<float>(0.02f, 1f)));
            InitialDelay = Config.Bind("FastAct", "InitialDelay", 0.25f,
                new ConfigDescription("Seconds Act must be held before repeating starts, so a quick tap still acts once. Vanilla is 0.25.",
                    new AcceptableValueRange<float>(0.02f, 1f)));

            InfiniteIngredients = Config.Bind("Ingredients", "Infinite", false,
                "Taking a premium ingredient (beef, fish, egg...) from the fridge does not use it up, and every discovered one counts as in stock.");

            KeepLeaderboardsClean = Config.Bind("General", "KeepLeaderboardsClean", true,
                "Skip Steam leaderboard uploads for the rest of the session once any tweak has been on.");

            TweaksUsed = FastAct.Value || InfiniteIngredients.Value;
            Config.Save();   // write the file with every option documented

            var harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(HoldActPatch));
            harmony.PatchAll(typeof(InfiniteTakePatch));
            harmony.PatchAll(typeof(InfiniteStockPatch));
            harmony.PatchAll(typeof(LeaderboardGuard));

            // Our own object rather than the plugin's: BepInEx's manager object can be destroyed
            // by games that clean up the scene, and the panel must survive every scene load.
            var host = new GameObject("BonesTweaks.UI");
            host.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(host);
            host.AddComponent<TweakUI>();

            Log.LogInfo($"Bone's Cafe Tweaks loaded - press {ToggleKey.Value} in game for the panel.");
        }
    }
}
