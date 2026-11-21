using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ChefSkills
{
    /// <summary>
    /// Keeps dashes in step online, so a dash never happens on only one machine.
    ///
    /// The game sends the host each player's raw dash button, and the host and the player's own
    /// machine then each decide for themselves whether the press starts a dash: the press is used
    /// up even when the dash is still recharging, the host also refuses while the chef is being
    /// bumped (which the player's machine never sees), and a quick tap whose press and release
    /// reach the host together is never seen there at all. Whenever they disagree, one side's chef
    /// dashes a few metres and the other's doesn't, and the player's chef gets snapped back to
    /// where the host has it.
    ///
    /// So when everyone has Chef Skills, the player's machine decides: it sends a short press for
    /// each dash its chef actually made (and nothing for presses it refused), and the host starts
    /// a dash for every press it receives, without its own recharge or bump checks, and never
    /// misses one. While the chef isn't being moved by the usual controls (in a cannon, steering
    /// something), the raw button is sent as before.
    /// </summary>
    internal static class DashSync
    {
        // How long the press sent for one dash is held down.
        private const float PulseSeconds = 0.08f;

        // A press latched on the host that nothing claimed is dropped after this long.
        private const float LatchSeconds = 0.5f;

        private sealed class Report
        {
            public int Pending;
            public bool High;
            public float HighSince;
            public int MovedFrame = -10;
        }

        // Chef on this machine (GameObject instance ID) -> what to send as its dash button.
        private static readonly Dictionary<int, Report> Reports = new Dictionary<int, Report>();

        // Host: a remote chef's dash button -> when a press arrived that hasn't been claimed yet.
        private static readonly Dictionary<NetworkLogicalButton, float> Latched =
            new Dictionary<NetworkLogicalButton, float>();

        internal static readonly FieldInfo PressClaimedField = AccessTools.Field(typeof(LogicalButtonBase), "m_pressClaimed");

        /// <summary>Online with everyone on this Chef Skills (see <see cref="SkillSync.EveryoneHasMod"/>).</summary>
        internal static bool Active
        {
            get { return SkillSync.EveryoneHasMod; }
        }

        /// <summary>A new level: nothing from the last one is still to be sent.</summary>
        internal static void Reset()
        {
            Reports.Clear();
            Latched.Clear();
        }

        private static Report For(GameObject chef)
        {
            int id = chef.GetInstanceID();
            Report report;
            if (!Reports.TryGetValue(id, out report))
            {
                report = new Report();
                Reports[id] = report;
            }
            return report;
        }

        /// <summary>This machine's chef is moved by the usual controls this frame.</summary>
        internal static void Moved(GameObject chef)
        {
            For(chef).MovedFrame = Time.frameCount;
        }

        /// <summary>This machine's chef just started a dash.</summary>
        internal static void Dashed(GameObject chef)
        {
            For(chef).Pending++;
        }

        /// <summary>
        /// The dash button to send for this machine's chef this frame: one short press per dash it
        /// made (released for at least a frame between two), or the raw button while the usual
        /// controls aren't moving it.
        /// </summary>
        internal static bool Reported(GameObject chef, bool rawDown)
        {
            Report report = For(chef);
            float now = Time.unscaledTime;
            if (report.High)
            {
                if (now - report.HighSince >= PulseSeconds || report.Pending > 0) report.High = false;
            }
            else if (report.Pending > 0)
            {
                report.Pending--;
                report.High = true;
                report.HighSince = now;
            }
            bool moving = Time.frameCount - report.MovedFrame <= 2;
            return report.High || (!moving && rawDown);
        }

        /// <summary>Host: a press of a remote chef's dash button just arrived.</summary>
        internal static void Latch(NetworkLogicalButton button)
        {
            Latched[button] = Time.unscaledTime;
            // A new press, even if the release before it was never seen.
            if (PressClaimedField != null) PressClaimedField.SetValue(button, false);
        }

        /// <summary>
        /// Host: whether a remote chef's dash button should read as down because a press arrived
        /// and nothing has claimed it yet (its release may already have arrived too).
        /// </summary>
        internal static bool IsLatched(NetworkLogicalButton button)
        {
            float since;
            if (!Latched.TryGetValue(button, out since)) return false;
            bool claimed = PressClaimedField == null || (bool)PressClaimedField.GetValue(button);
            if (claimed || Time.unscaledTime - since > LatchSeconds)
            {
                Latched.Remove(button);
                return false;
            }
            return true;
        }
    }

    /// <summary>A stand-in for a chef's dash button while the game reads it to send it to the host.</summary>
    internal sealed class ReportedButton : LogicalButtonBase
    {
        private ILogicalButton _real;
        private bool _down;

        internal void Set(ILogicalButton real, bool down)
        {
            _real = real;
            _down = down;
        }

        public override bool IsDown()
        {
            return _down;
        }

        public override void GetLogicTreeData(out AcyclicGraph<ILogicalElement, LogicalLinkInfo> _graph,
            out AcyclicGraph<ILogicalElement, LogicalLinkInfo>.Node _head)
        {
            _real.GetLogicTreeData(out _graph, out _head);
        }
    }

    /// <summary>
    /// A player's machine sends its chef's dashes instead of the raw dash button (see
    /// <see cref="DashSync"/>). The game reads the button once per frame here to send it.
    /// </summary>
    [HarmonyPatch(typeof(ClientInputTransmitter), "UpdateSynchronising")]
    internal static class DashReportPatch
    {
        private static readonly FieldInfo PadField = AccessTools.Field(typeof(ClientInputTransmitter), "m_Pad");
        private static readonly FieldInfo DashField = PadField != null ? AccessTools.Field(PadField.FieldType, "m_Dash") : null;
        private static readonly ReportedButton Stand = new ReportedButton();
        private static object _pad;
        private static ILogicalButton _real;

        private static void Prefix(ClientInputTransmitter __instance)
        {
            if (!DashSync.Active || DashField == null || !__instance.IsInControl()) return;
            // The host's own chefs aren't sent anywhere.
            if (__instance.GetComponent<ServerInputReceiver>() != null) return;
            object pad = PadField.GetValue(__instance);
            ILogicalButton real = pad != null ? DashField.GetValue(pad) as ILogicalButton : null;
            if (real == null) return;
            Stand.Set(real, DashSync.Reported(__instance.gameObject, real.IsDown()));
            DashField.SetValue(pad, Stand);
            _pad = pad;
            _real = real;
        }

        [HarmonyFinalizer]
        private static void Finalizer()
        {
            if (_pad != null) DashField.SetValue(_pad, _real);
            _pad = null;
            _real = null;
        }
    }

    /// <summary>
    /// Host: a dash press from another machine is kept until something claims it, so a quick tap
    /// whose press and release arrive together still dashes.
    /// </summary>
    [HarmonyPatch(typeof(ServerInputReceiver), "InputServerMessageReceived")]
    internal static class DashLatchPatch
    {
        private static readonly FieldInfo ButtonField = AccessTools.Field(typeof(ServerInputReceiver), "m_DashNetworkButton");
        private static readonly FieldInfo DownField = AccessTools.Field(typeof(NetworkLogicalButton), "m_IsDown");

        private static void Prefix(ServerInputReceiver __instance, out bool __state)
        {
            __state = true;
            if (!DashSync.Active || ButtonField == null || DownField == null) return;
            var button = ButtonField.GetValue(__instance) as NetworkLogicalButton;
            if (button != null) __state = (bool)DownField.GetValue(button);
        }

        private static void Postfix(ServerInputReceiver __instance, bool __state)
        {
            if (__state || !DashSync.Active || ButtonField == null || DownField == null) return;
            var button = ButtonField.GetValue(__instance) as NetworkLogicalButton;
            if (button != null && (bool)DownField.GetValue(button)) DashSync.Latch(button);
        }
    }

    /// <summary>Host: a latched dash press reads as down until it is claimed.</summary>
    [HarmonyPatch(typeof(NetworkLogicalButton), "IsDown")]
    internal static class DashLatchReadPatch
    {
        private static void Postfix(NetworkLogicalButton __instance, ref bool __result)
        {
            if (!__result && DashSync.IsLatched(__instance)) __result = true;
        }
    }

    /// <summary>
    /// A stronger dash goes further, but doesn't knock other chefs harder: a chef that gets
    /// knocked is only knocked on the host, and its own player's machine then has to snap it to
    /// where the host has it, so a harder knock means a bigger snap.
    /// </summary>
    [HarmonyPatch(typeof(ClientPlayerControlsImpl_Default), "OnDashCollision")]
    internal static class DashBumpPatch
    {
        private static void Prefix(ClientPlayerControlsImpl_Default __instance, ref Vector3 _relativeVelocity)
        {
            float dash = Plugin.Skills.Multiplier(__instance.gameObject, Skill.Dashing);
            if (dash > 1f) _relativeVelocity /= dash;
        }
    }
}
