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
    //   - Infinite souls (InfiniteSouls.cs): skeleton assistants can be summoned without limit.
    //   - Cafe size (CafeSize.cs): pick any of the six restaurant layouts at any rating.
    // All start OFF; the panel (TweakUI.cs) turns them on. Once any has been on, leaderboard
    // uploads are skipped for the rest of the session (LeaderboardGuard.cs).
    [BepInPlugin(Guid, "Bone's Cafe Tweaks", "1.2.0")]
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
        internal static ConfigEntry<bool> InfiniteSouls;
        internal static ConfigEntry<bool> CafeSizeEnabled;
        internal static ConfigEntry<int> CafeSizeLevel;
        internal static ConfigEntry<bool> CafeSizeCustom;
        internal static ConfigEntry<int> CafeWidth;
        internal static ConfigEntry<int> CafeDepth;
        internal static ConfigEntry<bool> CafeSizeLastLoadedCustom;
        internal static ConfigEntry<bool> KeepLeaderboardsClean;

        internal static bool AnyTweakOn =>
            FastAct.Value || InfiniteIngredients.Value || InfiniteSouls.Value || CafeSizeEnabled.Value;

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

            InfiniteSouls = Config.Bind("Souls", "Infinite", false,
                "Skeleton assistants can be summoned without limit (normally one per 5 souls collected). The summon unlock is still required.");

            CafeSizeEnabled = Config.Bind("CafeSize", "Enabled", false,
                "Use the restaurant size below instead of the one your rating gives. Takes effect when the cafe next loads. Not used in challenges or the tutorial.");
            CafeSizeLevel = Config.Bind("CafeSize", "Size", 5,
                new ConfigDescription("Restaurant layout: 0 = smallest (about 5x8) ... 5 = largest (15x25). Furniture outside a smaller cafe goes back to your inventory.",
                    new AcceptableValueRange<int>(0, 5)));
            CafeSizeCustom = Config.Bind("CafeSize", "Custom", false,
                "Build a restaurant of the width and depth below (from the largest layout) instead of using a preset.");
            CafeWidth = Config.Bind("CafeSize", "Width", 25,
                new ConfigDescription("Custom size: floor tiles from left wall to right wall (the largest preset is 25).",
                    new AcceptableValueRange<int>(CustomLayout.MinWidth, CustomLayout.MaxWidth)));
            CafeDepth = Config.Bind("CafeSize", "Depth", 15,
                new ConfigDescription("Custom size: floor tiles from the front wall to the back wall (the largest preset is 15).",
                    new AcceptableValueRange<int>(CustomLayout.MinDepth, CustomLayout.MaxDepth)));
            CafeSizeLastLoadedCustom = Config.Bind("CafeSize", "LastLoadedCustom", false,
                "Internal: set while a custom size is loaded, so switching it off can tidy furniture left outside the walls.");

            KeepLeaderboardsClean = Config.Bind("General", "KeepLeaderboardsClean", true,
                "Skip Steam leaderboard uploads for the rest of the session once any tweak has been on.");

            TweaksUsed = AnyTweakOn;
            Config.Save();   // write the file with every option documented

            var harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(HoldActPatch));
            harmony.PatchAll(typeof(InfiniteTakePatch));
            harmony.PatchAll(typeof(InfiniteStockPatch));
            harmony.PatchAll(typeof(InfiniteSoulsPatch));
            harmony.PatchAll(typeof(CafeSizeOverridePatch));
            harmony.PatchAll(typeof(CafeLayoutPatch));
            harmony.PatchAll(typeof(CafeFurnitureBoundsPatch));
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
