using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace BonesTweaks
{
    // The restaurant is one of six pre-built interiors, GameController.interiorLayouts[0..5]. Awake
    // turns on interiorLayouts[rating level] - or, at the top rating (5), the size the player picked
    // in the game's own cafe-size menu (GameData.GetCafeSizeOverride). Everything else that cares
    // about size asks GetCafeSizeOverride: assistant path bounds (StepGoto), the furniture-bounds
    // cleanup when shrinking (GameController.CheckFurnitureBounds), menu goals (RatingSystem).
    //
    // So a custom size is: (1) Awake picks our layout instead of the rating's, and (2)
    // GetCafeSizeOverride reports it. The size is fixed when the cafe scene loads and held until the
    // next load, so changing it in the panel mid-day never leaves the bounds out of step with the
    // walls. Challenge mode (its own fixed size) and the tutorial are left alone.
    internal static class CafeSize
    {
        /// <summary>Approximate floor size of each layout, from the game's own bounds.</summary>
        internal static readonly string[] Dimensions = { "5x8", "7x8", "7x12", "7x16", "10x20", "15x25" };

        private const int Undecided = -2;
        private static int _active = Undecided;

        internal static int Chosen => Mathf.Clamp(Plugin.CafeSizeLevel.Value, 0, 5);

        /// <summary>The size in force for the loaded cafe, or -1 when the game decides.</summary>
        internal static int Active
        {
            get
            {
                if (_active == Undecided) _active = Applies() ? Chosen : -1;
                return _active;
            }
        }

        /// <summary>True while the loaded cafe uses a size that differs from what the panel shows.</summary>
        internal static bool PendingReload => Active != (Applies() ? Chosen : -1);

        private static bool Applies()
        {
            try
            {
                if (!Plugin.CafeSizeEnabled.Value) return false;
                var state = MainGameState.Instance;
                if (state == null || state.isChallengeMode || state.isTutorialMode) return false;
                return GameData.Instance != null && GameData.Instance.IsTutorialCompleted();
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Called by GameController.Awake in place of GetRatingLevel() when indexing interiorLayouts.</summary>
        public static int LayoutIndex(GameData gd) => Active >= 0 ? Active : gd.GetRatingLevel();

        internal static void ForgetDecision() => _active = Undecided;
    }

    [HarmonyPatch(typeof(GameData), nameof(GameData.GetCafeSizeOverride))]
    internal static class CafeSizeOverridePatch
    {
        private static void Postfix(ref int __result)
        {
            int size = CafeSize.Active;
            if (size >= 0) __result = size;
        }
    }

    [HarmonyPatch(typeof(GameController), "Awake")]
    internal static class CafeLayoutPatch
    {
        private static readonly MethodInfo GetRatingLevel = AccessTools.Method(typeof(GameData), nameof(GameData.GetRatingLevel));
        private static readonly MethodInfo CheckFurnitureBounds = AccessTools.Method(typeof(GameController), "CheckFurnitureBounds");
        private static readonly AccessTools.FieldRef<GameController, bool> IsTutorialLevel =
            AccessTools.FieldRefAccess<GameController, bool>("isTutorialLevel");

        // Decide afresh for every cafe load: LoadGame (the first thing Awake does) has run by the
        // time anything asks, so the decision sees this save's rating and tutorial state.
        private static void Prefix() => CafeSize.ForgetDecision();

        // interiorLayouts[GameData.Instance.GetRatingLevel()].SetActive(true)
        //   -> interiorLayouts[CafeSize.LayoutIndex(GameData.Instance)].SetActive(true)
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var matcher = new CodeMatcher(instructions)
                .MatchStartForward(
                    new CodeMatch(i => i.Calls(GetRatingLevel)),
                    new CodeMatch(System.Reflection.Emit.OpCodes.Ldelem_Ref));
            if (matcher.IsInvalid)
            {
                Plugin.Log.LogError("GameController.Awake no longer matches the expected code; custom cafe size only works at the top rating.");
                return instructions;
            }
            return matcher
                .SetInstruction(new CodeInstruction(System.Reflection.Emit.OpCodes.Call, AccessTools.Method(typeof(CafeSize), nameof(CafeSize.LayoutIndex))))
                .InstructionEnumeration();
        }

        // Below the top rating the game never shrinks a cafe, so it never clears furniture that is
        // now outside the walls; do what it does at the top rating (items go back to the inventory,
        // assistants outside are unsummoned). At the top rating Awake already did it with our size.
        // Likewise when a custom size is switched off: the previous load may have placed furniture
        // where the rating's own (smaller) cafe has no floor, and the game would never clear it.
        private static void Postfix(GameController __instance)
        {
            if (IsTutorialLevel(__instance) || MainGameState.Instance.isChallengeMode) return;
            try
            {
                int rating = GameData.Instance.GetRatingLevel();
                int size = CafeSize.Active;
                if (size >= 0)
                {
                    SetLastLoadedCustom(true);
                    if (size < 5 && rating < 5) CheckFurnitureBounds.Invoke(__instance, new object[] { size });
                }
                else if (Plugin.CafeSizeLastLoadedCustom.Value)
                {
                    SetLastLoadedCustom(false);
                    if (rating < 5) CheckFurnitureBounds.Invoke(__instance, new object[] { rating });
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Cafe size: furniture bounds check failed: {e.Message}");
            }
        }

        private static void SetLastLoadedCustom(bool value)
        {
            if (Plugin.CafeSizeLastLoadedCustom.Value == value) return;
            Plugin.CafeSizeLastLoadedCustom.Value = value;
            Plugin.Cfg.Save();
        }
    }
}
