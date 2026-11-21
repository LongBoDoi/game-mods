using System;
using System.Reflection;
using HarmonyLib;

namespace OC2Tweaks
{
    [HarmonyPatch(typeof(ClientLobbyFlowController), "TryJoinGame")]
    [HarmonyAfter("com.github.yuxuanchiadm.hostforcetheme")]
    internal static class ForceHostMatchmaking
    {
        // HostGame sets up the game's mode, visibility, host user and completion callback.
        // Calling it preserves the game's own failure handling and public lobby advertising.
        private static readonly MethodInfo HostGame =
            AccessTools.Method(typeof(ClientLobbyFlowController), "HostGame");

        private static void Prepare()
        {
            if (HostGame == null)
                throw new MissingMethodException("ClientLobbyFlowController", "HostGame");
        }

        internal static bool Prefix(ClientLobbyFlowController __instance, bool __runOriginal)
        {
            // HarmonyX runs prefixes even when another prefix has already handled the call.
            // Respect that result, including Host Force Theme's selected-room and host paths.
            if (!__runOriginal || !Plugin.ForceHost.Value) return true;
            HostGame.Invoke(__instance, null);
            Plugin.Log.LogInfo("Luôn làm chủ phòng: đang tạo phòng ghép trận của bạn.");
            return false;
        }
    }
}
