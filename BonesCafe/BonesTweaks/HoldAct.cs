using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;

namespace BonesTweaks
{
    // The game already repeats Act (chop/grind) while the button is held, via
    // PlayerController.CheckHoldChopKey: it fires once currentTimeActHold > 0.25f, then resets.
    // We swap that hard-coded 0.25f for our own threshold, so the repeat rate is configurable and
    // the first repeat can wait a little longer than the rest (so a normal tap never double-chops).
    [HarmonyPatch(typeof(PlayerController), "CheckHoldChopKey")]
    internal static class HoldActPatch
    {
        private static readonly AccessTools.FieldRef<PlayerController, float> HoldTime =
            AccessTools.FieldRefAccess<PlayerController, float>("currentTimeActHold");

        // Players whose current hold has already produced at least one repeat.
        private static readonly HashSet<PlayerController> Repeating = new HashSet<PlayerController>();

        public static float Threshold(PlayerController pc)
        {
            if (!Plugin.FastAct.Value)
                return 0.25f;
            return Repeating.Contains(pc) ? Plugin.RepeatInterval.Value : Plugin.InitialDelay.Value;
        }

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var field = AccessTools.Field(typeof(PlayerController), "currentTimeActHold");
            var matcher = new CodeMatcher(instructions)
                .MatchStartForward(
                    new CodeMatch(OpCodes.Ldfld, field),
                    new CodeMatch(i => i.opcode == OpCodes.Ldc_R4 && (float)i.operand == 0.25f));

            if (matcher.IsInvalid)
            {
                Plugin.Log.LogError("CheckHoldChopKey no longer matches the expected code; Act hold speed is unchanged.");
                return instructions;
            }

            // [ldfld currentTimeActHold] [ldc.r4 0.25]  ->  [ldfld currentTimeActHold] [ldarg.0] [call Threshold]
            return matcher
                .Advance(1)
                .SetAndAdvance(OpCodes.Ldarg_0, null)
                .Insert(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(HoldActPatch), nameof(Threshold))))
                .InstructionEnumeration();
        }

        [HarmonyPostfix]
        private static void Postfix(PlayerController __instance)
        {
            // The method zeroes the timer only when the hold fired an Act. OnAct (a fresh press)
            // also zeroes it, but that runs before Update adds this frame's deltaTime, so a zero
            // here always means a repeat just happened.
            if (HoldTime(__instance) == 0f)
                Repeating.Add(__instance);
            else if (!IsActHeld(__instance))
                Repeating.Remove(__instance);
        }

        private static bool IsActHeld(PlayerController pc)
        {
            var input = pc.GetComponent<UnityEngine.InputSystem.PlayerInput>();
            var action = input != null ? input.currentActionMap?.FindAction("ActHold") : null;
            return action != null && action.ReadValue<float>() > 0.1f;
        }
    }
}
