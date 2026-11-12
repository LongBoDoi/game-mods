using HarmonyLib;
using OC2DIYLevel;
using UnityEngine;

namespace RandomLevel
{
    [HarmonyPatch(typeof(DIYLevelEntryUI), "AddUI")]
    internal static class AddRandomButton
    {
        private static void Postfix()
        {
            var menu = AccessTools.Field(typeof(DIYLevelEntryUI), "levelSetSelectionMenu").GetValue(null) as FrontendOptionsMenu;
            if (menu == null) return;
            const string path = "SettingsBody/ContentPC/Viewport/Content/RandomLevel_All";
            if (menu.transform.Find(path) != null) return;
            var button = OC2DIYLevel.UIUtils.AddButton(menu, "RandomLevel_All", "Chơi ngẫu nhiên — tất cả màn", "Chơi ngẫu nhiên — tất cả màn", Plugin.StartRandom);
            if (button != null) button.transform.SetAsFirstSibling();
        }
    }

    [HarmonyPatch(typeof(DIYLevelEntryUI), "OnLevelSelected")]
    internal static class ManualSelection
    {
        private static void Prefix()
        {
            if (!Plugin.SelectingRandom) Plugin.Reset();
        }
    }

    [HarmonyPatch(typeof(GameUtils), "QuitLevel")]
    internal static class QuitRandom
    {
        private static void Prefix() { Plugin.Reset(); }
    }

    [HarmonyPatch(typeof(ScoreScreenOutroFlowroutine), "Run")]
    internal static class ResultsStarted
    {
        // Run creates the outro iterator after Setup has created the score controller. Track it
        // now: the key hint becomes visible with the score screen, before its animation settles.
        private static void Prefix(StarRatingUIController ___m_starRatingController)
        {
            if (!Plugin.Enabled || !Plugin.CanControl()) return;
            Plugin.Results = ___m_starRatingController;
            Plugin.ResultsInputReady = false;
        }
    }

    [HarmonyPatch(typeof(StarRatingUIController), "SetButtonActive")]
    internal static class ResultsReady
    {
        private static void Postfix(StarRatingUIController __instance)
        {
            if (Plugin.Enabled && Plugin.CanControl())
            {
                Plugin.Results = __instance;
                Plugin.ResultsInputReady = true;
            }
        }
    }

    [HarmonyPatch(typeof(InGamePauseMenu), "OnRestartConfirmed")]
    internal static class RestartDuringTransition
    {
        private static bool Prefix()
        {
            return SkipLevel.AllowRestart(Time.realtimeSinceStartup);
        }
    }

}
