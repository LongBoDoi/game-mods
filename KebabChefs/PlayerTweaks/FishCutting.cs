using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Unity.Netcode;
using UnityEngine;

namespace PlayerTweaks;

/// <summary>
/// Removes the knife minigame that gates cutting a fish.
///
/// FishCutEvent runs the whole thing from one coroutine: DoEvent() opens cutCanvas and
/// starts CutCoroutine(), the player steers knifeIndicator between the spikes, every
/// spike touched bumps negativeCutCount, and the sweep ends in CompleteCutting().
///
/// So rather than invent a result, this calls the game's own CompleteCutting() the moment
/// the minigame opens, with negativeCutCount forced to zero - the fish comes out exactly
/// as a flawless run would have left it.
///
/// Co-op then needs no channel of its own, which is the opposite of how the plate-cabinet
/// exclusion had to work. That one steered host-side staff AI from a local setting, so the
/// two machines disagreed. This steers nothing: the minigame is local UI belonging to one
/// player's own item event, and the slicing behind it already replicates on Vegetable's own
/// InitSliceServerRpc/ClientRpc pair. Both players skipping is two independent local skips,
/// and only one player having the mod is fine - the other simply plays the minigame.
///
/// What co-op does demand is that this never finishes a cut that is not ours. DoEvent is
/// the local path (a remote player's cut arrives through DoEventRemote and opens no
/// canvas), so the patch may trust itself; the watchdog, which can find a board any way at
/// all, has to prove the local player owns it first.
/// </summary>
public static class FishCutting
{
    /// <summary>
    /// Cutting boards seen opening the minigame, so the watchdog has something to poll
    /// without scanning the scene every frame.
    /// </summary>
    private static readonly List<FishCutEvent> Known = new();

    /// <summary>
    /// Boards whose DoEvent we have actually seen fire. That only happens on the machine
    /// of the player doing the cutting, so it is proof of local ownership that survives
    /// the local player letting go of the event afterwards.
    /// </summary>
    private static readonly HashSet<IntPtr> LocalEver = new();

    private static readonly HashSet<string> Logged = new();

    private static int _skipped;
    private static string _failure;
    private static float _scanTimer;
    private static bool _patchFired;

    public static string Status =>
        !Plugin.FishCutSkip.Value ? "chưa hoạt động"
        : _failure != null ? $"lỗi: {_failure}"
        : _skipped == 0 ? "đang bật - chưa cắt con cá nào"
        : $"đã bỏ qua {_skipped} lần cắt cá";

    // ------------------------------------------------------------------ install

    /// <summary>
    /// DoEvent is FishCutEvent's own override of the ItemEvent entry point, and is what
    /// opens the minigame. Patched rather than polled so the canvas never gets a frame
    /// on screen.
    /// </summary>
    public static void Install(BepInEx.Logging.ManualLogSource log)
    {
        try
        {
            var target = HarmonyLib.AccessTools.Method(
                typeof(FishCutEvent), nameof(FishCutEvent.DoEvent));

            if (target == null) { log.LogWarning("Fish: could not resolve FishCutEvent.DoEvent."); return; }

            new HarmonyLib.Harmony(Plugin.Guid + ".fishcut").Patch(
                target,
                postfix: new HarmonyLib.HarmonyMethod(
                    HarmonyLib.AccessTools.Method(typeof(FishCutting), nameof(DoEventPostfix))));

            log.LogInfo("Fish: patched FishCutEvent.DoEvent.");
        }
        catch (Exception e)
        {
            log.LogWarning($"Fish: patching DoEvent failed: {e.Message}");
        }
    }

    /// <summary>
    /// Remembers the board whether or not the option is on, so switching it on mid-shift
    /// does not have to wait for a scene scan to find anything.
    /// </summary>
    private static void DoEventPostfix(FishCutEvent __instance)
    {
        if (__instance == null) return;

        _patchFired = true;
        Remember(__instance);
        try { LocalEver.Add(__instance.Pointer); } catch { }

        if (!Plugin.FishCutSkip.Value) return;
        Finish(__instance, "DoEvent");
    }

    // -------------------------------------------------------------------- tick

    public static void Tick(CharacterItemInteract interact)
    {
        if (!Plugin.FishCutSkip.Value) return;

        // Only worth scanning while the patch has never fired. If DoEvent turns out not to
        // be the entry point on this build, a scan is the only way to notice a minigame
        // that has already opened; once the patch has fired the cache is authoritative and
        // FindObjectsByType is pure cost.
        if (!_patchFired)
        {
            _scanTimer -= Time.unscaledDeltaTime;
            if (_scanTimer <= 0f) { _scanTimer = 5f; Rescan(); }
        }

        for (int i = Known.Count - 1; i >= 0; i--)
        {
            var ev = Known[i];
            if (ev == null) { Known.RemoveAt(i); continue; }

            try
            {
                if (ev.isCutting && Ours(ev, interact)) Finish(ev, "watchdog");
            }
            catch (Exception e)
            {
                LogOnce($"Fish: watchdog failed: {e.Message}");
                Known.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Whether the cut in progress on this board is the local player's.
    ///
    /// Either we saw DoEvent fire on it here, or the local character's current item event
    /// is this one - which it can be as the FishCutEvent itself or as the ItemSliceEvent
    /// that owns it, so both are checked. Anything else belongs to somebody else's screen
    /// and must be left alone; in co-op, finishing it would cut their fish from under them.
    /// </summary>
    private static bool Ours(FishCutEvent ev, CharacterItemInteract interact)
    {
        try
        {
            if (LocalEver.Contains(ev.Pointer)) return true;
            if (interact == null) return false;

            var current = interact.currentEvent;
            if (current == null) return false;
            if (current.Pointer == ev.Pointer) return true;

            var slice = current.TryCast<ItemSliceEvent>();
            if (slice == null) return false;

            var owned = slice.fishCutEvent;
            return owned != null && owned.Pointer == ev.Pointer;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ finish

    private static void Finish(FishCutEvent ev, string via)
    {
        try
        {
            if (!ev.isCutting) return;

            // Captured before completing, because the event is free to let go of the fish
            // as part of finishing, and this is the only handle on whether the cut landed.
            var fish = ev.targetFish;

            // A flawless run is what this is standing in for, so no spike ever counted.
            ev.negativeCutCount = 0;

            // FishCutEvent has no Update - the coroutine is the whole minigame - so
            // stopping it is what stops it, and it also keeps the sweep from reaching its
            // own CompleteCutting() later and cutting the same fish twice.
            var running = ev.cutCoroutine;
            if (running != null)
            {
                ev.StopCoroutine(running);
                ev.cutCoroutine = null;
            }

            ev.CompleteCutting();

            // CompleteCutting is the game's success path and should have torn the minigame
            // down with it. If it has not, the canvas would sit there with the left click
            // still captured, so finish the teardown by hand.
            if (ev.isCutting) ev.EndCutting();
            HideCanvas(ev);

            _skipped++;
            _failure = null;

            // Once per session, and worth the noise: this is the line that says whether the
            // skip actually cut a fish, and whether it did so as host or as client. Without
            // it a co-op failure looks exactly like a co-op success from this side.
            if (_skipped == 1)
                Plugin.Logger.LogInfo(
                    $"Fish: minigame skipped via {via} as {Role()} - {SliceState(fish)}.");
        }
        catch (Exception e)
        {
            _failure = e.Message;
            LogOnce($"Fish: could not skip the minigame ({e.Message}). " +
                    "Turn the option off and cut by hand.");

            // The coroutine may already be stopped, which would leave the player staring at
            // a canvas that nothing is driving any more. Getting out is worth more than the
            // fish.
            try { if (ev.isCutting) ev.EndCutting(); } catch { }
            try { HideCanvas(ev); } catch { }
        }
    }

    /// <summary>Which side of a co-op session this is, for the log line.</summary>
    private static string Role()
    {
        try
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening) return "solo";
            return nm.IsServer ? "host" : "client";
        }
        catch { return "unknown"; }
    }

    /// <summary>
    /// What the fish looks like straight after completing. The slice replicates through
    /// Vegetable's own RPCs, so on a client this is the local view and may still be a frame
    /// behind - but "0 of 3" here is still the signature of a skip that cut nothing.
    /// </summary>
    private static string SliceState(Vegetable fish)
    {
        if (fish == null) return "no fish on the event to check";

        try { return $"fish sliced={fish.isSliced}, {fish.completedSlices}/{fish.sliceAmount} slices"; }
        catch (Exception e) { return $"could not read the fish ({e.Message})"; }
    }

    private static void HideCanvas(FishCutEvent ev)
    {
        var canvas = ev.cutCanvas;
        if (canvas != null && !ev.isCutting && canvas.activeSelf) canvas.SetActive(false);
    }

    // ------------------------------------------------------------------- cache

    private static void Rescan()
    {
        try
        {
            var found = UnityEngine.Object.FindObjectsByType<FishCutEvent>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            if (found == null) return;
            foreach (var ev in found) Remember(ev);
        }
        catch (Exception e)
        {
            LogOnce($"Fish: scan failed: {e.Message}");
        }
    }

    private static void Remember(FishCutEvent ev)
    {
        if (ev == null) return;

        try
        {
            IntPtr key = ev.Pointer;
            foreach (var known in Known)
                if (known != null && known.Pointer == key) return;

            Known.Add(ev);
        }
        catch { }
    }

    private static void LogOnce(string message)
    {
        if (Logged.Add(message)) Plugin.Logger.LogWarning(message);
    }
}
