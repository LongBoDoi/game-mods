using System;
using System.Collections.Generic;
using HarmonyLib;

namespace BonesTweaks
{
    // The game posts to global Steam leaderboards: challenge scores (ChallengeReport) and
    // "ReputationDayTotal" after every normal day (EndOfDayReport). Scores earned with tweaks on
    // don't belong there, so once any tweak has been on, uploads are skipped for the session.
    //
    // UpdateScore is the only call that uploads. Its completion callback pops the pending
    // leaderboard event and runs the next one; skipping the upload does the same bookkeeping,
    // exactly as the game does when an upload fails, so the report screens carry on normally.
    [HarmonyPatch(typeof(LeaderBoardManager), nameof(LeaderBoardManager.UpdateScore))]
    internal static class LeaderboardGuard
    {
        private static readonly AccessTools.FieldRef<LeaderBoardManager, List<Action>> Events =
            AccessTools.FieldRefAccess<LeaderBoardManager, List<Action>>("leaderboardEvents");

        private static bool Prefix(LeaderBoardManager __instance, int score)
        {
            if (!Plugin.TweaksUsed || !Plugin.KeepLeaderboardsClean.Value) return true;

            Plugin.Log.LogInfo($"Leaderboard upload skipped (score {score}): tweaks were used this session.");
            var events = Events(__instance);
            if (events != null && events.Count > 0)
            {
                events.RemoveAt(events.Count - 1);
                __instance.ProcessLeaderboardEvents();
            }
            return false;
        }
    }
}
