using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace LocalCoop
{
    // Every copy of the game sees every keyboard, mouse and controller on the PC. This decides,
    // in each copy, which devices are its player's and swallows the input of all others before
    // the game sees it (InputSystem.onEvent runs before device state is written, and a handled
    // event is dropped).
    //
    //   Player 1: keyboard, mouse, and every controller no guest has taken.
    //   Guest n : only the controller(s) it took.
    //
    // Taking a controller: a guest waiting for one asks for LB + RB pressed together. All
    // copies see that same physical press at (nearly) the same moment, so the guest writes
    // "<player> <UTC ticks>" to claims.txt and every other copy matches it to the controller
    // on which *it* saw LB + RB at that time. Device ids differ between processes, and two
    // identical controllers look the same, but the moment of the press is shared.
    internal static class InputOwnership
    {
        private const double MatchWindowSeconds = 0.4;
        private const double ResolveGiveUpSeconds = 5.0;

        private sealed class PadState
        {
            public bool Left, Right;
            public readonly List<long> ChordTicks = new List<long>();
        }

        private sealed class Claim
        {
            public int Player;
            public long Ticks;
            public int DeviceId = -1;        // in this process; -1 until matched
            public float FirstSeen;
            public bool GaveUp;
        }

        private static readonly Dictionary<int, PadState> s_pads = new Dictionary<int, PadState>();
        private static readonly List<Claim> s_claims = new List<Claim>();
        private static readonly Dictionary<int, int> s_owner = new Dictionary<int, int>();   // deviceId -> player
        private static string s_claimsText;
        private static float s_nextPoll;
        private static bool s_backgroundApplied;
        private static long s_claimingSinceTicks;

        /// <summary>Guests: the controller they took (null until then).</summary>
        public static Gamepad MyPad { get; private set; }

        /// <summary>Guests: true while this guest is the one asking for LB + RB.</summary>
        public static bool AwaitingClaim { get; private set; }

        /// <summary>Guests: a lower-numbered guest is still choosing, so this one waits.</summary>
        public static bool WaitingForOtherGuest { get; private set; }

        public static void Install(Harmony harmony)
        {
            InputSystem.onEvent += OnEvent;

            // InputRouter remembers the last-used controller for prompts, glyphs and rumble; it
            // must only ever pick one of ours.
            harmony.Patch(AccessTools.Method(typeof(InputRouter), "NoteDevice"),
                prefix: new HarmonyMethod(typeof(InputOwnership), nameof(NoteDevicePrefix)));
            // Steam Input's native API merges every controller into one virtual pad.
            harmony.Patch(AccessTools.Method(typeof(SteamPlatformBridge), "EnsureNativeGamepad"),
                prefix: new HarmonyMethod(typeof(InputOwnership), nameof(EnsureNativeGamepadPrefix)));
        }

        public static bool IsMine(InputDevice device)
        {
            if (!Coop.Active || device == null) return true;
            int owner = OwnerOf(device);
            return owner == Coop.Player;
        }

        private static int OwnerOf(InputDevice device)
        {
            if (device is Gamepad)
                return s_owner.TryGetValue(device.deviceId, out int p) ? p : 1;
            // Keyboard, mouse, pen, touch and anything else: Player 1.
            return 1;
        }

        private static void OnEvent(InputEventPtr ev, InputDevice device)
        {
            if (device == null || !Coop.Active) return;
            if (device is Gamepad pad && (ev.IsA<StateEvent>() || ev.IsA<DeltaStateEvent>())) WatchChord(ev, pad);
            if (!IsMine(device)) ev.handled = true;
        }

        private static void WatchChord(InputEventPtr ev, Gamepad pad)
        {
            if (!s_pads.TryGetValue(pad.deviceId, out PadState st)) s_pads[pad.deviceId] = st = new PadState();
            bool was = st.Left && st.Right;
            if (pad.leftShoulder.ReadValueFromEvent(ev, out float l)) st.Left = l >= 0.5f;
            if (pad.rightShoulder.ReadValueFromEvent(ev, out float r)) st.Right = r >= 0.5f;
            if (was || !(st.Left && st.Right)) return;

            long now = DateTime.UtcNow.Ticks;
            st.ChordTicks.Add(now);
            if (st.ChordTicks.Count > 8) st.ChordTicks.RemoveAt(0);

            if (Coop.IsGuest && AwaitingClaim && MyPad == null && !s_owner.ContainsKey(pad.deviceId) && now >= s_claimingSinceTicks)
            {
                MyPad = pad;
                s_owner[pad.deviceId] = Coop.Player;
                s_claims.Add(new Claim { Player = Coop.Player, Ticks = now, DeviceId = pad.deviceId });
                Coop.AppendLine("claims.txt", Coop.Player + " " + now.ToString(CultureInfo.InvariantCulture));
                AwaitingClaim = false;
                pad.MakeCurrent();
                Plugin.Log.LogInfo($"Player {Coop.Player} took controller {Describe(pad)}.");
            }
        }

        public static void Update()
        {
            if (!Coop.Active) { s_backgroundApplied = false; return; }

            if (!s_backgroundApplied)
            {
                // Guests never have focus, and Player 1 may lose it to a guest window for a moment.
                Application.runInBackground = true;
                if (InputSystem.settings.backgroundBehavior != InputSettings.BackgroundBehavior.IgnoreFocus)
                    InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
                s_backgroundApplied = true;
                LogDevices();
            }

            if (Time.realtimeSinceStartup < s_nextPoll) return;
            s_nextPoll = Time.realtimeSinceStartup + 0.25f;

            string text = Coop.Read("claims.txt") ?? string.Empty;
            if (text != s_claimsText)
            {
                s_claimsText = text;
                ReloadClaims(text);
            }
            ResolveClaims();
            DropRemovedDevices();
            UpdateClaimTurn();
        }

        private static void ReloadClaims(string text)
        {
            var fresh = new List<Claim>();
            foreach (string[] f in Coop.Lines(text))
            {
                if (f.Length < 2 || !int.TryParse(f[0], out int player) || !long.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks)) continue;
                Claim known = s_claims.Find(c => c.Player == player && c.Ticks == ticks);
                fresh.Add(known ?? new Claim { Player = player, Ticks = ticks, FirstSeen = Time.realtimeSinceStartup });
            }
            s_claims.Clear();
            s_claims.AddRange(fresh);

            // Rebuild ownership from what is still claimed (Player 1 prunes guests that left).
            s_owner.Clear();
            foreach (Claim c in s_claims)
                if (c.DeviceId >= 0) s_owner[c.DeviceId] = c.Player;

            if (Coop.IsGuest && MyPad != null && !(s_owner.TryGetValue(MyPad.deviceId, out int me) && me == Coop.Player))
            {
                Plugin.Log.LogInfo("This player's controller claim is gone; asking again.");
                MyPad = null;
            }
        }

        private static void ResolveClaims()
        {
            foreach (Claim c in s_claims)
            {
                if (c.DeviceId >= 0 || c.GaveUp) continue;
                int best = -1;
                double bestGap = MatchWindowSeconds;
                foreach (var kv in s_pads)
                {
                    if (s_owner.ContainsKey(kv.Key)) continue;
                    foreach (long t in kv.Value.ChordTicks)
                    {
                        double gap = Math.Abs(t - c.Ticks) / (double)TimeSpan.TicksPerSecond;
                        if (gap <= bestGap) { bestGap = gap; best = kv.Key; }
                    }
                }
                if (best >= 0)
                {
                    c.DeviceId = best;
                    s_owner[best] = c.Player;
                    InputDevice d = InputSystem.GetDeviceById(best);
                    Plugin.Log.LogInfo($"Controller {Describe(d)} belongs to Player {c.Player}.");
                }
                else if (Time.realtimeSinceStartup - c.FirstSeen > ResolveGiveUpSeconds)
                {
                    c.GaveUp = true;
                    Plugin.Log.LogWarning($"Could not tell which controller Player {c.Player} took (this window never saw its LB + RB).");
                }
            }
        }

        private static void DropRemovedDevices()
        {
            if (MyPad != null && !MyPad.added)
            {
                Plugin.Log.LogInfo("This player's controller was disconnected; asking for one again.");
                s_owner.Remove(MyPad.deviceId);
                MyPad = null;
            }
        }

        private static void UpdateClaimTurn()
        {
            if (!Coop.IsGuest || MyPad != null) { AwaitingClaim = false; WaitingForOtherGuest = false; return; }

            // Guests choose in order: Player 2 first, then 3, then 4, so one press is never
            // taken by two of them.
            bool earlierGuestChoosing = false;
            foreach (int p in WindowLayout.LivePlayers)
            {
                if (p < 2 || p >= Coop.Player) continue;
                bool hasClaim = s_claims.Exists(c => c.Player == p);
                if (!hasClaim) { earlierGuestChoosing = true; break; }
            }
            WaitingForOtherGuest = earlierGuestChoosing;
            bool awaiting = !earlierGuestChoosing;
            if (awaiting && !AwaitingClaim) s_claimingSinceTicks = DateTime.UtcNow.Ticks;
            AwaitingClaim = awaiting;
        }

        private static bool NoteDevicePrefix(InputDevice device) => IsMine(device);

        private static bool EnsureNativeGamepadPrefix(SteamPlatformBridge __instance)
        {
            if (!Coop.Active) return true;
            if (Traverse.Create(__instance).Field("_nativeGamepad").GetValue() != null)
            {
                Plugin.Log.LogInfo("Turning off Steam Input's merged controller for local co-op.");
                AccessTools.Method(typeof(SteamPlatformBridge), "RemoveNativeGamepad").Invoke(__instance, null);
            }
            return false;
        }

        private static void LogDevices()
        {
            foreach (InputDevice d in InputSystem.devices)
                Plugin.Log.LogInfo($"Device #{d.deviceId}: {Describe(d)} background={d.canRunInBackground}");
        }

        public static string Describe(InputDevice d)
        {
            if (d == null) return "(none)";
            string product = string.IsNullOrEmpty(d.description.product) ? d.layout : d.description.product;
            return $"{product} [{d.description.interfaceName}]";
        }
    }
}
