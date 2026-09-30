using HarmonyLib;

namespace BonesTweaks
{
    // Souls are never spent. Every 5 collected is one summon slot for a skeleton assistant:
    // SoulsManager.GetNumSummonsAvailable() = souls / 5 (or the challenge's fixed number), and
    // CanSummon() allows a summon while that minus the assistants currently summoned is > 0. So
    // "infinite souls" means an effectively endless number of summons; raising the available count
    // does it, and the HUD counter follows because it shows the same number.
    // The game's own unlock (the summon cutscene) is still required.
    [HarmonyPatch(typeof(SoulsManager), nameof(SoulsManager.GetNumSummonsAvailable))]
    internal static class InfiniteSoulsPatch
    {
        internal const int Plenty = 99;

        private static void Postfix(ref int __result)
        {
            if (Plugin.InfiniteSouls.Value && __result < Plenty) __result = Plenty;
        }
    }
}
