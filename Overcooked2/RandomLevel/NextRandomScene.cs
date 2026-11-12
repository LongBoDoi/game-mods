using HarmonyLib;
using OC2DIYLevel;

namespace RandomLevel
{
    [HarmonyPatch(typeof(ServerCampaignFlowController), "GetNextScene")]
    internal static class NextRandomScene
    {
        // Run AFTER the DIY prefix has replaced GetNextScene. Its UpdateSynchronising probes
        // this method too; never perform loading / state changes here. The original game waits
        // for every player's Continue, stops synchronisation, sends progress and loads by name.
        [HarmonyPostfix, HarmonyAfter(Plugin.DiyId), HarmonyPriority(Priority.Last)]
        internal static void Postfix(ServerCampaignFlowController __instance, bool ___m_LevelRestartRequested,
            ref GameState o_loadState, ref GameState o_loadEndState, ref bool o_useLoadingScreen, ref string __result)
        {
            if (!Plugin.Enabled || !Plugin.CanControl()) return;
            var settings = GameUtils.GetGameSession().LevelSettings;
            if (settings == null || settings.SceneDirectoryVarientEntry == null) return;
            string current = settings.SceneDirectoryVarientEntry.SceneName;
            string next = ___m_LevelRestartRequested ? current : Plugin.Rotation.Next(__instance, current);
            if (string.IsNullOrEmpty(next)) return;
            __result = next;
            o_loadState = GameState.LoadKitchen;
            o_loadEndState = GameState.RunKitchen;
            o_useLoadingScreen = true;
            if (!___m_LevelRestartRequested) DIYLevelEntryUI.selectedLevel = next;
        }
    }
}
