using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace HoldAct
{
    // The game already repeats Act (chop/grind) while the button is held, via
    // PlayerController.CheckHoldChopKey: it fires once currentTimeActHold > 0.25f, then resets.
    // We swap that hard-coded 0.25f for our own threshold, so the repeat rate is configurable and
    // the first repeat can wait a little longer than the rest (so a normal tap never double-chops).
    [BepInPlugin(Guid, "Hold Act", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "manhl.bonescafe.holdact";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> RepeatInterval;
        internal static ConfigEntry<float> InitialDelay;

        private void Awake()
        {
            Log = Logger;
            Enabled = Config.Bind("General", "Enabled", true,
                "Use the settings below. When false, holding Act behaves exactly like the unmodded game (a repeat every 0.25 s).");
            RepeatInterval = Config.Bind("General", "RepeatInterval", 0.1f,
                new ConfigDescription("Seconds between repeated Act presses (chop, grind...) while the button is held. Vanilla is 0.25.",
                    new AcceptableValueRange<float>(0.02f, 1f)));
            InitialDelay = Config.Bind("General", "InitialDelay", 0.25f,
                new ConfigDescription("Seconds the button must be held before repeating starts, so a quick tap still acts only once. Vanilla is 0.25.",
                    new AcceptableValueRange<float>(0.02f, 1f)));

            new Harmony(Guid).PatchAll(typeof(HoldChopPatch));
            Log.LogInfo($"Hold Act loaded (interval {RepeatInterval.Value}s, initial delay {InitialDelay.Value}s)");
        }
    }

    [HarmonyPatch(typeof(PlayerController), "CheckHoldChopKey")]
    internal static class HoldChopPatch
    {
        private static readonly AccessTools.FieldRef<PlayerController, float> HoldTime =
            AccessTools.FieldRefAccess<PlayerController, float>("currentTimeActHold");

        // Players whose current hold has already produced at least one repeat.
        private static readonly HashSet<PlayerController> Repeating = new HashSet<PlayerController>();

        public static float Threshold(PlayerController pc)
        {
            if (!Plugin.Enabled.Value)
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
                Plugin.Log.LogError("CheckHoldChopKey no longer matches the expected code; hold speed is unchanged.");
                return instructions;
            }

            // [ldfld currentTimeActHold] [ldc.r4 0.25]  ->  [ldfld currentTimeActHold] [ldarg.0] [call Threshold]
            return matcher
                .Advance(1)
                .SetAndAdvance(OpCodes.Ldarg_0, null)
                .Insert(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(HoldChopPatch), nameof(Threshold))))
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
