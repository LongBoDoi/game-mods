using System;
using HarmonyLib;
using UnityEngine;

namespace LoadTweaks;

/// <summary>
/// Finds, and shortens, the wait that the load is actually made of.
///
/// By this point the pause after each batch has survived being given more frames, a faster
/// game clock, thirteen thousand fewer log lines, a bigger spawn batch and a quicker
/// networking tick. It sits at a quarter of a second whatever any of those do. A delay that
/// ignores the frame rate, ignores game time and ignores how much work is going on is a
/// delay measured against the wall clock - and in a Unity coroutine there is essentially
/// one way to write that.
///
/// So this counts every kind of waiting the engine does during a load, which settles what
/// the pause is rather than inferring it, and shortens the real-time kind.
///
/// On shortening being safe: the coroutine still yields, and Unity still resumes it on the
/// following frame. Every step still happens in the same order with a frame in between, so
/// anything that relies on a frame passing - components starting, objects registering - is
/// unaffected. The only thing that changes is how long the frame after is waited for. This
/// is a much smaller intervention than running the coroutine faster than it asked to run,
/// which is the thing that could leave a restaurant half-built.
/// </summary>
public static class WaitCutter
{
    public static long RealtimePolls;
    public static long RealtimeCut;

    public static long UntilPolls;
    public static long UntilWaiting;

    public static long PredicatePolls;
    public static long PredicateFalse;

    private static bool _cutInstalled;
    private static bool _traceInstalled;

    public static void Install(BepInEx.Logging.ManualLogSource log)
    {
        var harmony = new Harmony(Plugin.Guid + ".waits");

        if (Plugin.SkipRealtimeWaits.Value)
            _cutInstalled = Patch(log, harmony, typeof(WaitForSecondsRealtime), "keepWaiting",
                                  nameof(RealtimeKeepWaiting));
        else
            log.LogInfo("WaitCutter: real-time waits left alone.");

        if (!Plugin.TraceWaits.Value)
        {
            log.LogInfo("WaitCutter: wait tracing off.");
            return;
        }

        _traceInstalled = Patch(log, harmony, typeof(WaitUntil), "keepWaiting", nameof(UntilKeepWaiting));

        // The predicate of the WaitUntil inside GameServer.WaitForGameDataLoad, compiled out
        // as an ordinary method. If the pause is that WaitUntil, this is polled once per
        // frame throughout it - which tells us the pause is a condition being waited on
        // rather than a timer, and the condition becomes the thing to chase.
        try
        {
            var predicate = AccessTools.Method(typeof(GameServer), "_WaitForGameDataLoad_b__117_0");
            if (predicate == null)
            {
                log.LogInfo("WaitCutter: the load predicate was not found under the name it had; " +
                            "it moves with each game build.");
            }
            else
            {
                harmony.Patch(predicate, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(WaitCutter), nameof(PredicateResult))));
                log.LogInfo("WaitCutter: watching the load predicate.");
            }
        }
        catch (Exception e)
        {
            log.LogWarning($"WaitCutter: could not watch the load predicate: {e.Message}");
        }
    }

    private static bool Patch(BepInEx.Logging.ManualLogSource log, Harmony harmony,
                              Type owner, string property, string handler)
    {
        try
        {
            var getter = AccessTools.PropertyGetter(owner, property);
            if (getter == null)
            {
                log.LogWarning($"WaitCutter: {owner.Name}.{property} not found.");
                return false;
            }

            harmony.Patch(getter, postfix: new HarmonyMethod(AccessTools.Method(typeof(WaitCutter), handler)));
            log.LogInfo($"WaitCutter: watching {owner.Name}.{property}.");
            return true;
        }
        catch (Exception e)
        {
            log.LogWarning($"WaitCutter: patching {owner.Name}.{property} failed: {e.Message}");
            return false;
        }
    }

    // ------------------------------------------------------------------ hooks

    private static void RealtimeKeepWaiting(ref bool __result)
    {
        // Outside a load this is a single comparison and nothing else. The game's own
        // real-time waits during play are none of this mod's business.
        if (!LoadProbe.Loading) return;

        RealtimePolls++;

        if (!__result) return;
        if (!Plugin.Enabled.Value || !Plugin.SkipRealtimeWaits.Value) return;

        // Say the wait is over. The coroutine still resumes on the next frame, not this one.
        __result = false;
        RealtimeCut++;
    }

    private static void UntilKeepWaiting(bool __result)
    {
        if (!LoadProbe.Loading) return;
        UntilPolls++;
        if (__result) UntilWaiting++;
    }

    private static void PredicateResult(bool __result)
    {
        if (!LoadProbe.Loading) return;
        PredicatePolls++;
        if (!__result) PredicateFalse++;
    }

    public static void Reset()
    {
        RealtimePolls = RealtimeCut = 0;
        UntilPolls = UntilWaiting = 0;
        PredicatePolls = PredicateFalse = 0;
    }

    public static string Describe()
    {
        if (!_cutInstalled && !_traceInstalled) return "waits: not watched";

        return $"waits during load: real-time polled {RealtimePolls} (cut short {RealtimeCut}), " +
               $"WaitUntil polled {UntilPolls} (still waiting {UntilWaiting}), " +
               $"load predicate polled {PredicatePolls} (said 'not yet' {PredicateFalse})";
    }
}
