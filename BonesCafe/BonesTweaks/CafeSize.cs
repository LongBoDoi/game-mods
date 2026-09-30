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
    // A preset size is: (1) Awake picks our layout instead of the rating's, and (2)
    // GetCafeSizeOverride reports it. A custom width x depth uses layout 5 and reshapes it
    // (CustomLayout.cs). The choice is fixed when the cafe scene loads and held until the next
    // load, so changing it in the panel mid-day never leaves bounds out of step with the walls.
    // Challenge mode (its own fixed size) and the tutorial are left alone.
    internal static class CafeSize
    {
        /// <summary>Approximate floor size of each preset, from the game's own bounds.</summary>
        internal static readonly string[] Dimensions = { "5x8", "7x8", "7x12", "7x16", "10x20", "15x25" };

        internal readonly struct Choice : IEquatable<Choice>
        {
            public readonly int Index;      // layout 0..5, or -1 = the game decides
            public readonly bool Custom;
            public readonly int Width, Depth;

            public Choice(int index, bool custom, int width, int depth)
            {
                Index = index; Custom = custom; Width = custom ? width : 0; Depth = custom ? depth : 0;
            }

            public static readonly Choice Game = new Choice(-1, false, 0, 0);
            public bool Equals(Choice o) => Index == o.Index && Custom == o.Custom && Width == o.Width && Depth == o.Depth;
        }

        private static Choice? _active;

        internal static int Chosen => Mathf.Clamp(Plugin.CafeSizeLevel.Value, 0, 5);
        internal static int ChosenWidth => Mathf.Clamp(Plugin.CafeWidth.Value, CustomLayout.MinWidth, CustomLayout.MaxWidth);
        internal static int ChosenDepth => Mathf.Clamp(Plugin.CafeDepth.Value, CustomLayout.MinDepth, CustomLayout.MaxDepth);

        /// <summary>What the panel currently asks for.</summary>
        private static Choice Wanted()
        {
            if (!Applies()) return Choice.Game;
            return Plugin.CafeSizeCustom.Value
                ? new Choice(5, true, ChosenWidth, ChosenDepth)
                : new Choice(Chosen, false, 0, 0);
        }

        /// <summary>The choice in force for the loaded cafe.</summary>
        internal static Choice Active => _active ?? (_active = Wanted()).Value;

        /// <summary>True while the loaded cafe differs from what the panel shows.</summary>
        internal static bool PendingReload => !Active.Equals(Wanted());

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
        public static int LayoutIndex(GameData gd) => Active.Index >= 0 ? Active.Index : gd.GetRatingLevel();

        internal static void ForgetDecision() => _active = null;
    }

    [HarmonyPatch(typeof(GameData), nameof(GameData.GetCafeSizeOverride))]
    internal static class CafeSizeOverridePatch
    {
        private static void Postfix(ref int __result)
        {
            int size = CafeSize.Active.Index;
            if (size >= 0) __result = size;
        }
    }

    /// <summary>The game's furniture cleanup knows only the six presets; use the custom floor instead.</summary>
    [HarmonyPatch(typeof(GameController), "CheckFurnitureBounds")]
    internal static class CafeFurnitureBoundsPatch
    {
        private static bool Prefix()
        {
            if (!CafeSize.Active.Custom) return true;
            CustomLayout.ClearOutside();
            return false;
        }
    }

    [HarmonyPatch(typeof(GameController), "Awake")]
    internal static class CafeLayoutPatch
    {
        private static readonly MethodInfo GetRatingLevel = AccessTools.Method(typeof(GameData), nameof(GameData.GetRatingLevel));
        private static readonly MethodInfo CheckFurnitureBounds = AccessTools.Method(typeof(GameController), "CheckFurnitureBounds");
        private static readonly AccessTools.FieldRef<GameController, bool> IsTutorialLevel =
            AccessTools.FieldRefAccess<GameController, bool>("isTutorialLevel");
        private static readonly AccessTools.FieldRef<GameController, GameObject[]> InteriorLayouts =
            AccessTools.FieldRefAccess<GameController, GameObject[]>("interiorLayouts");

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

        // After Awake has picked and loaded the layout:
        //  - custom size: reshape layout 5, then clear furniture outside the new floor;
        //  - any size the game would not have picked, or the first load after one: run the game's
        //    own furniture-bounds cleanup, which it only does itself when shrinking at the top
        //    rating (and never twice - its Destroy is deferred, so a second pass would refund twice).
        private static void Postfix(GameController __instance)
        {
            if (IsTutorialLevel(__instance) || MainGameState.Instance.isChallengeMode) return;
            try
            {
                var choice = CafeSize.Active;
                var gd = GameData.Instance;
                int size = gd.GetCafeSizeOverride();
                bool gameCleaned = gd.GetRatingLevel() >= 5 && size != 5;

                if (choice.Custom)
                {
                    SetLastLoadedCustom(true);
                    CustomLayout.SetSize(choice.Width, choice.Depth);
                    CustomLayout.Build(InteriorLayouts(__instance)[choice.Index]);
                    if (!gameCleaned) CustomLayout.ClearOutside();   // otherwise the prefix above already ran it
                }
                else if (choice.Index >= 0)
                {
                    SetLastLoadedCustom(true);
                    if (!gameCleaned) CheckFurnitureBounds.Invoke(__instance, new object[] { size });
                }
                else if (Plugin.CafeSizeLastLoadedCustom.Value)
                {
                    SetLastLoadedCustom(false);
                    if (!gameCleaned) CheckFurnitureBounds.Invoke(__instance, new object[] { size });
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Cafe size: {e}");
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
