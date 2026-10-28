using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace MonitorCoop
{
    /// <summary>Feeds the per-monitor rectangles into the game's own split-screen layout.</summary>
    /// <remarks>Game1.SetWindowSize builds a list of (x, y, width, height) fractions of the window - one per
    /// player - picks its own instance's entry, and then sizes that instance's render targets, viewport and
    /// menus from it. Replacing just that one value leaves every bit of the vanilla path intact, which is
    /// why this is a transpiler rather than a reimplementation of the method.</remarks>
    internal static class SplitScreenPatch
    {
        private static IMonitor Log;

        /// <summary>The layout to apply, or null to leave the game's own split-screen layout alone.
        /// Only set while the window really does span the monitors in the plan.</summary>
        public static SplitPlan Plan;

        /// <summary>The value to force Game1.zoomModifier to while the plan applies.</summary>
        public static float ZoomModifier = 1f;

        private static bool LoggedSlotError;
        private static bool LoggedPositionError;

        /// <summary>Patches the game. Returns false if the method no longer looks the way this mod expects,
        /// in which case nothing is patched and the mod should stay out of the way.</summary>
        public static bool Apply(Harmony harmony, IMonitor monitor)
        {
            Log = monitor;

            MethodInfo target = AccessTools.Method(typeof(Game1), nameof(Game1.SetWindowSize));
            if (target == null)
            {
                monitor.Log("Game1.SetWindowSize not found; this mod needs an update for this game version.", LogLevel.Error);
                return false;
            }

            try
            {
                harmony.Patch(target,
                    postfix: new HarmonyMethod(typeof(SplitScreenPatch), nameof(MoveWindowSlot)),
                    transpiler: new HarmonyMethod(typeof(SplitScreenPatch), nameof(Transpile)));
            }
            catch (Exception ex)
            {
                monitor.Log("Could not patch Game1.SetWindowSize: " + ex.Message, LogLevel.Error);
                return false;
            }

            return Patched;
        }

        /// <summary>Whether the transpiler found its anchor. False means the game's layout is untouched.</summary>
        public static bool Patched { get; private set; }

        /// <summary>Replaces the slot the game picked for this instance with the player's monitor.</summary>
        public static Vector4 GetSlot(Vector4 vanilla, Game1 instance, int w, int h)
        {
            try
            {
                int player;
                SplitPlan plan = GetPlanFor(instance, w, h, out player);
                if (plan == null)
                    return vanilla;

                // The game halves the zoom for split-screen because each player has a fraction of the
                // window; on a monitor each, that fraction is gone. This runs before the render targets are
                // sized, which is the only place the value still has an effect.
                instance.zoomModifier = ZoomModifier;

                float x, y, width, height;
                Layout.ToSlot(plan.Players[player], w, h, out x, out y, out width, out height);
                return new Vector4(x, y, width, height);
            }
            catch (Exception ex)
            {
                if (!LoggedSlotError)
                {
                    LoggedSlotError = true;
                    Log.Log("Could not apply the monitor layout: " + ex, LogLevel.Error);
                }
                return vanilla;
            }
        }

        /// <summary>Puts the player's window at exactly the right pixel. The fractions above decide the size
        /// (and with it the render targets); the position is only used to blit the finished screen, so it can
        /// be set exactly here instead of being left to rounding.</summary>
        public static void MoveWindowSlot(Game1 __instance, int w, int h)
        {
            try
            {
                int player;
                SplitPlan plan = GetPlanFor(__instance, w, h, out player);
                if (plan == null)
                    return;

                __instance.localMultiplayerWindow.X = plan.Players[player].X;
                __instance.localMultiplayerWindow.Y = plan.Players[player].Y;
            }
            catch (Exception ex)
            {
                if (!LoggedPositionError)
                {
                    LoggedPositionError = true;
                    Log.Log("Could not place a player's window: " + ex, LogLevel.Error);
                }
            }
        }

        /// <summary>The plan to use for this instance, or null if the game should keep its own layout.
        /// The window size has to match the plan, so the vanilla layout stays in force until the window
        /// has actually been resized to span the monitors.</summary>
        private static SplitPlan GetPlanFor(Game1 instance, int w, int h, out int player)
        {
            player = 0;

            SplitPlan plan = Plan;
            if (plan == null || !Patched)
                return null;
            if (plan.Window.Width != w || plan.Window.Height != h)
                return null;

            GameRunner runner = GameRunner.instance;
            if (runner == null || runner.gameInstances == null || runner.gameInstances.Count != plan.PlayerCount)
                return null;

            // SetWindowSize reads its instance index off Game1.game1, which the game points at the instance
            // being updated; fall back to the receiver if that is ever not the case.
            Game1 current = Game1.game1 ?? instance;
            player = current.instanceIndex;
            if (player < 0 || player >= plan.PlayerCount)
                return null;

            return plan;
        }

        private static IEnumerable<CodeInstruction> Transpile(
            IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo getItem = AccessTools.Method(typeof(List<Vector4>), "get_Item", new[] { typeof(int) });
            MethodInfo hook = AccessTools.Method(typeof(SplitScreenPatch), nameof(GetSlot));

            List<CodeInstruction> original = new List<CodeInstruction>(instructions);
            List<CodeInstruction> patched = new List<CodeInstruction>(original.Count + 4);
            int hooks = 0;

            foreach (CodeInstruction instruction in original)
            {
                patched.Add(instruction);
                if (getItem == null || !instruction.Calls(getItem))
                    continue;

                // The list entry is on the stack; hand it to GetSlot along with the instance and the window
                // size, and use what comes back instead.
                patched.Add(new CodeInstruction(OpCodes.Ldarg_0));
                patched.Add(new CodeInstruction(OpCodes.Ldarg_1));
                patched.Add(new CodeInstruction(OpCodes.Ldarg_2));
                patched.Add(new CodeInstruction(OpCodes.Call, hook));
                hooks++;
            }

            if (hooks != 1)
            {
                Log.Log("Game1.SetWindowSize no longer looks the way this mod expects (" + hooks
                    + " split-screen layout lookups found, expected 1); leaving the game's own layout alone.",
                    LogLevel.Error);
                Patched = false;
                return original;
            }

            Patched = true;
            return patched;
        }
    }
}
