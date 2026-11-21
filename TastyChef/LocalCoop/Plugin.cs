using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace LocalCoop
{
    // Local co-op for Tasty Chef: every extra player gets their own copy of the game in their
    // own window (a monitor each, or a share of one monitor), and those copies join Player 1
    // through the game's own LAN room. Everything the game's online co-op does then just works,
    // because it *is* the game's online co-op - only on one PC.
    //
    //   Player 1  launches the game as usual and presses F9 to open a window for another player.
    //   Guests    (copies started by Player 1) keep their own settings, saves and Unity Services
    //             sign-in, take a controller when someone presses LB + RB on it, and join
    //             Player 1's LAN room on their own.
    //
    //   Coop.cs            who this copy is (player number, shared folder with the other copies)
    //   HostLauncher.cs    Player 1: starts/stops guests, lays out the windows, boot.config fix
    //   GuestIsolation.cs  guests: separate saves, PlayerPrefs, Unity Services profile, name
    //   InputOwnership.cs  every copy: drops input from controllers that belong to someone else
    //   FocusPatch.cs      guests: the game and UI Toolkit treat the guest window as focused
    //   WindowLayout.cs    every copy: borderless window at the rectangle Player 1 assigned
    //   RoomLink.cs        Player 1 publishes its LAN room code; guests join it
    //   Overlay.cs         on-screen prompts
    [BepInPlugin(Guid, "Local Co-op", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "manhl.tastychef.localcoop";
        public const string Version = "1.0.0";

        internal static ManualLogSource Log;
        internal static Harmony Harmony;

        internal static ConfigEntry<KeyboardShortcut> AddPlayerKey;
        internal static ConfigEntry<KeyboardShortcut> RemovePlayerKey;
        internal static ConfigEntry<WindowLayout.Mode> Layout;
        internal static ConfigEntry<WindowLayout.Split> SplitDirection;
        internal static ConfigEntry<bool> GuestAudio;
        internal static ConfigEntry<int> FrameRateCap;
        internal static ConfigEntry<bool> AutoJoin;
        internal static ConfigEntry<bool> AutoCreateRoom;
        internal static ConfigEntry<int> PlayersAtStart;

        private void Awake()
        {
            Log = Logger;

            AddPlayerKey = Config.Bind("Keys", "AddPlayer", new KeyboardShortcut(KeyCode.F9),
                "Player 1's window: opens a window for one more player (up to 4 players).");
            RemovePlayerKey = Config.Bind("Keys", "RemovePlayer", new KeyboardShortcut(KeyCode.F9, KeyCode.LeftShift),
                "Player 1's window: closes the most recently added player's window.");
            Layout = Config.Bind("Windows", "Layout", WindowLayout.Mode.Auto,
                "Auto: a monitor each when there are enough monitors, otherwise Player 1's monitor is shared.\n" +
                "SplitScreen: always share Player 1's monitor.");
            SplitDirection = Config.Bind("Windows", "TwoPlayerSplit", WindowLayout.Split.SideBySide,
                "How two players share one monitor: SideBySide (left / right) or Stacked (top / bottom). " +
                "Three or four players always get a quarter each.");
            FrameRateCap = Config.Bind("Windows", "FrameRateCap", 0,
                new ConfigDescription("While playing together, no window draws faster than this (fps). 0 = each window's own " +
                    "monitor refresh rate (or the game's frame-rate limit when lower). The windows share one graphics card, " +
                    "so a window drawing frames its monitor cannot show slows the others down.",
                    new AcceptableValueRange<int>(0, 500)));
            GuestAudio = Config.Bind("Guests", "Sound", false,
                "Play sound in the other players' windows too. Off by default: every window shows the same " +
                "restaurant, so their sound would only double Player 1's.");
            AutoJoin = Config.Bind("Guests", "AutoJoin", true,
                "Guests join Player 1's LAN room on their own as soon as Player 1 creates it.");
            AutoCreateRoom = Config.Bind("Player1", "AutoCreateLanRoom", true,
                "When another player's window is open and Player 1 is at the title screen, create the LAN room " +
                "for them (Player 1 still picks the save and starts the day from the room).");
            PlayersAtStart = Config.Bind("Player1", "PlayersAtStart", 1,
                new ConfigDescription("Open this many player windows (Player 1 included) as soon as the title screen shows. " +
                    "1 = only when you press the AddPlayer key. Also settable per launch with -tccoop-players <n>.",
                    new AcceptableValueRange<int>(1, Coop.MaxPlayers)));

            Coop.Init(Environment.GetCommandLineArgs());
            Harmony = new Harmony(Guid);

            try
            {
                // Guests must swap their save folder and PlayerPrefs before the game reads either.
                if (Coop.IsGuest) GuestIsolation.Apply(Harmony);
                InputOwnership.Install(Harmony);
                RoomLink.Install(Harmony);
                FramePacing.Install(Harmony);
                if (Coop.IsGuest) FocusPatch.Install(Harmony);
                if (!Coop.IsGuest) HostLauncher.Init();
            }
            catch (Exception e)
            {
                Log.LogError("Setup failed: " + e);
            }

            // BepInEx's own manager object is not guaranteed to tick in every game; run our own.
            var go = new GameObject("LocalCoopRunner");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            go.AddComponent<Runner>();

            Log.LogInfo(Coop.IsGuest
                ? $"Local Co-op {Version}: Player {Coop.Player} window (Player 1 is process {Coop.HostPid})."
                : $"Local Co-op {Version}: Player 1. Press {AddPlayerKey.Value} to open a window for another player.");
        }
    }

    internal sealed class Runner : MonoBehaviour
    {
        private void Update()
        {
            Step(HostLauncher.Update);
            Step(InputOwnership.Update);
            Step(WindowLayout.Update);
            Step(RoomLink.Update);
            Step(FramePacing.Update);
            Step(FrameStats.Update);
            if (Coop.IsGuest) Step(GuestIsolation.Update);
        }

        private void LateUpdate()
        {
            if (Coop.IsGuest && !Plugin.GuestAudio.Value) AudioListener.volume = 0f;
        }

        private void OnGUI() => Step(Overlay.Draw);

        private void OnApplicationQuit()
        {
            Step(GuestIsolation.Flush);
            Step(HostLauncher.Shutdown);
        }

        // One failing feature must not stop the others from running every frame.
        private static readonly System.Collections.Generic.HashSet<string> s_reported = new System.Collections.Generic.HashSet<string>();
        private static void Step(Action a)
        {
            try { a(); }
            catch (Exception e)
            {
                string key = a.Method.DeclaringType?.Name + "." + a.Method.Name + ":" + e.GetType().Name;
                if (s_reported.Add(key)) Plugin.Log.LogError(key + " failed: " + e);
            }
        }
    }
}
