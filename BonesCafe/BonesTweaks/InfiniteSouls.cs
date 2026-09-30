using HarmonyLib;
using UnityEngine;

namespace BonesTweaks
{
    // Souls are never spent. Every 5 collected is one summon slot for a skeleton assistant:
    // SoulsManager.GetNumSummonsAvailable() = souls / 5 (or the challenge's fixed number), and both
    // CanSummon() and the HUD counter use that minus the assistants currently summoned. So
    // "infinite souls" means the free slots stay at 99: the available number is raised to
    // "already summoned + 99", which keeps the counter at 99 however many are summoned.
    // The game's own unlock (the summon cutscene) is still required.
    [HarmonyPatch(typeof(SoulsManager), nameof(SoulsManager.GetNumSummonsAvailable))]
    internal static class InfiniteSoulsPatch
    {
        internal const int Free = 99;

        private static SoulsManager _manager;

        private static void Postfix(ref int __result)
        {
            if (!Plugin.InfiniteSouls.Value) return;
            if (_manager == null) _manager = Object.FindObjectOfType<SoulsManager>();
            int summoned = _manager != null ? _manager.GetNumSummoned() : 0;
            if (__result < summoned + Free) __result = summoned + Free;
        }
    }
}
