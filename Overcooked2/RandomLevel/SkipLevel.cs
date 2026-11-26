using System;
using OC2DIYLevel;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RandomLevel
{
    internal static class SkipLevel
    {
        private const float Cooldown = 3f;
        private static object loadingOwner;
        private static float lastTransition = float.NegativeInfinity;

        internal static void Reset()
        {
            loadingOwner = null;
            lastTransition = float.NegativeInfinity;
        }

        internal static ServerCampaignFlowController LiveFlow()
        {
            string scene = SceneManager.GetActiveScene().name;
            foreach (var flow in Resources.FindObjectsOfTypeAll<ServerCampaignFlowController>())
                if (flow != null && flow.gameObject.activeInHierarchy && flow.gameObject.scene.IsValid() &&
                    flow.gameObject.scene.name == scene && flow.InRound) return flow;
            return null;
        }

        internal static bool Busy(object owner, float now)
        {
            // Keep the old controller locked until the scene is replaced, even on slow loads.
            return ReferenceEquals(loadingOwner, owner) || now - lastTransition < Cooldown;
        }

        internal static bool CanSkip(ServerCampaignFlowController flow, float now)
        {
            return flow != null && Plugin.Enabled && Plugin.CanControl() && Plugin.Rotation.Count > 1 && !Busy(flow, now);
        }

        internal static void MarkTransition(object owner, float now)
        {
            loadingOwner = owner;
            lastTransition = now;
        }

        internal static bool AllowRestart(float now)
        {
            if (!Plugin.Enabled || !Plugin.CanControl()) return true;
            var flow = LiveFlow();
            if (flow == null) return loadingOwner == null;
            if (Busy(flow, now)) return false;
            MarkTransition(flow, now);
            return true;
        }

        internal static bool TrySkip(float now)
        {
            var flow = LiveFlow();
            if (!CanSkip(flow, now)) return false;
            var settings = GameUtils.GetGameSession().LevelSettings;
            if (settings == null || settings.SceneDirectoryVarientEntry == null) return false;
            string current = settings.SceneDirectoryVarientEntry.SceneName;
            // Do not tear down a live round if its settings have already changed for loading.
            if (current != SceneManager.GetActiveScene().name) return false;
            var multiplayer = GameUtils.RequireManager<MultiplayerController>();
            if (multiplayer == null) return false;
            string next = Plugin.Rotation.Next(flow, current);
            if (string.IsNullOrEmpty(next) || next == current) return false;

            MarkTransition(flow, now);
            Plugin.Results = null;
            DIYLevelEntryUI.selectedLevel = next;
            try
            {
                // Match Pause -> Restart's teardown, then use the pack's normal network loader
                // with another scene. No SkipToEnd / results routine means no score for this round.
                multiplayer.StopSynchronisation();
                DIYLevelAssetBundleManager.LoadLevelServer(next);
                Plugin.Log.LogInfo("Đã bỏ qua " + current + "; đang tải màn ngẫu nhiên: " + next);
                return true;
            }
            catch (Exception error)
            {
                // A partial teardown is not safe to repeat on the same controller.
                Plugin.Log.LogError("Không chuyển được sang màn ngẫu nhiên: " + error);
                return false;
            }
        }
    }
}
