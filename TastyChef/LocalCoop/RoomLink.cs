using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace LocalCoop
{
    // Player 1 creates a LAN room from the game's own Multiplayer menu; its room code goes to
    // room.txt, and every guest at the title screen opens the Multiplayer menu and joins it.
    //
    // Only LAN rooms are shared: in a Steam or cross-platform room every window would be the same
    // Steam / Epic account, which those rooms do not let in twice.
    internal static class RoomLink
    {
        private static bool s_hostingLan;
        private static string s_published;
        private static float s_nextPoll;

        private static string s_joinCode;          // guest: code from Player 1
        private static string s_attemptedCode;
        private static float s_attemptedAt = -999f;

        private static readonly FieldInfo s_showingTitle = AccessTools.Field(typeof(MainMenuController), "_showingTitle");
        private static readonly FieldInfo s_showingSlots = AccessTools.Field(typeof(MainMenuController), "_showingSlotSelection");
        private static readonly FieldInfo s_lobby = AccessTools.Field(typeof(MainMenuController), "_multiplayerLobby");
        private static readonly FieldInfo s_presenter = AccessTools.Field(typeof(MainMenuController), "_multiplayerLobbyPresenter");
        private static readonly MethodInfo s_showLobby = AccessTools.Method(typeof(MainMenuController), "ShowMultiplayerLobby");

        public enum GuestState { NoRoom, WaitingForTitle, Joining, InRoom }

        public static GuestState State { get; private set; } = GuestState.NoRoom;
        public static bool HostHasLanRoom => s_published != null;

        public static void Install(Harmony harmony)
        {
            var t = typeof(MultiplayerSessionService);
            harmony.Patch(AccessTools.Method(t, nameof(MultiplayerSessionService.CreateLanHostAsync)),
                prefix: new HarmonyMethod(typeof(RoomLink), nameof(LanPrefix)));
            foreach (string name in new[] { "CreateHostAsync", "CreateSteamHostAsync", "JoinAsync" })
                harmony.Patch(AccessTools.Method(t, name), prefix: new HarmonyMethod(typeof(RoomLink), nameof(OtherPrefix)));
        }

        private static void LanPrefix() => s_hostingLan = true;
        private static void OtherPrefix() => s_hostingLan = false;

        public static void Update()
        {
            if (Time.realtimeSinceStartup < s_nextPoll) return;
            s_nextPoll = Time.realtimeSinceStartup + 0.5f;
            if (Coop.IsGuest) GuestUpdate();
            else HostUpdate();
        }

        /// <summary>The title screen's main menu is up (not a save list, popup or the lobby).</summary>
        public static bool AtTitle(out MainMenuController menu)
        {
            menu = MainMenuController.Instance;
            if (menu == null || !(bool)s_showingTitle.GetValue(menu) || (bool)s_showingSlots.GetValue(menu)) return false;
            if (PopupManager.HasModal) return false;
            var lobby = s_lobby.GetValue(menu) as MultiplayerLobbyController;
            return lobby == null || !lobby.IsVisible;
        }

        private static MultiplayerLobbySessionPresenter OpenLobby(MainMenuController menu)
        {
            var lobby = s_lobby.GetValue(menu) as MultiplayerLobbyController;
            if (lobby == null || !lobby.IsVisible) s_showLobby.Invoke(menu, null);
            return s_presenter.GetValue(menu) as MultiplayerLobbySessionPresenter;
        }

        private static int s_createAttempts;
        private static float s_createAt = -999f;
        private static bool s_autoCreateDone;

        /// <summary>
        /// Player 1 at the title with guests waiting: create the LAN room for them - once per
        /// co-op start. A second try only if the first never produced a room; once Player 1 has
        /// had a room, leaving it is Player 1's choice and is not undone.
        /// </summary>
        private static void AutoCreate(MultiplayerSessionService svc)
        {
            if (svc != null && svc.HasActiveSession) s_autoCreateDone = true;
            if (!Plugin.AutoCreateRoom.Value || s_autoCreateDone || s_createAttempts >= 2) return;
            if (svc != null && svc.IsBusy) return;
            if (Time.realtimeSinceStartup - s_createAt < 20f || !AtTitle(out MainMenuController menu)) return;
            s_createAttempts++;
            s_createAt = Time.realtimeSinceStartup;
            MultiplayerLobbySessionPresenter presenter = OpenLobby(menu);
            bool started = presenter != null && presenter.CreateLan();
            Plugin.Log.LogInfo("Creating a LAN room for the other windows: " + (started ? "started" : "the menu was busy"));
        }

        private static void HostUpdate()
        {
            if (!HostLauncher.HasGuests)
            {
                if (s_published != null) Coop.Write("room.txt", "");
                s_published = null;
                s_createAttempts = 0;
                s_autoCreateDone = false;
                return;
            }
            string code = null;
            MultiplayerSessionService svc = MultiplayerSessionService.Instance;
            AutoCreate(svc);
            if (s_hostingLan && svc != null && svc.HasActiveSession)
            {
                RestaurantRoomSession session = svc.ActiveSession;
                if (session != null && session.IsHost)
                {
                    code = session.Code;
                    if (string.IsNullOrEmpty(code)) code = svc.JoinCode;
                    if (string.IsNullOrEmpty(code)) code = null;
                }
            }
            if (code == s_published && Coop.Read("room.txt") != null) return;
            s_published = code;
            Coop.Write("room.txt", code ?? "");
            if (code != null) Plugin.Log.LogInfo("Shared LAN room " + code + " with the other windows.");
        }

        private static void GuestUpdate()
        {
            string code = (Coop.Read("room.txt") ?? "").Trim();
            s_joinCode = code.Length > 0 ? code : null;

            MultiplayerSessionService svc = MultiplayerSessionService.Instance;
            if (svc != null && svc.HasActiveSession) { State = GuestState.InRoom; return; }
            if (s_joinCode == null) { State = GuestState.NoRoom; return; }
            if (!Plugin.AutoJoin.Value) { State = GuestState.NoRoom; return; }

            if (svc != null && svc.IsBusy) { State = GuestState.Joining; return; }

            // One attempt per code, then a retry every 20 seconds while it keeps failing.
            if (s_joinCode == s_attemptedCode && Time.realtimeSinceStartup - s_attemptedAt < 20f) return;

            // At the title, or in a Multiplayer menu the guest is not yet in a room of.
            MainMenuController menu = MainMenuController.Instance;
            bool lobbyOpen = menu != null && s_lobby.GetValue(menu) is MultiplayerLobbyController l && l.IsVisible;
            if (!AtTitle(out menu) && !(lobbyOpen && !PopupManager.HasModal))
            {
                State = State == GuestState.Joining ? GuestState.Joining : GuestState.WaitingForTitle;
                return;
            }
            s_attemptedCode = s_joinCode;
            s_attemptedAt = Time.realtimeSinceStartup;
            State = GuestState.Joining;

            MultiplayerLobbySessionPresenter presenter = OpenLobby(menu);
            if (presenter == null)
            {
                Plugin.Log.LogWarning("The Multiplayer menu did not open; will try again.");
                return;
            }
            bool started = presenter.Join(s_joinCode);
            Plugin.Log.LogInfo($"Joining Player 1's LAN room {s_joinCode}: {(started ? "started" : "the menu was busy, will retry")}.");
        }
    }
}
