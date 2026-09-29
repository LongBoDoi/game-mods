using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace PlayerTweaks;

/// <summary>
/// Seasoning quality-of-life: choose how much a single shake adds, and skip the hold.
///
/// Two independent levers in the game:
///  * SpiceItemEvent.spillAmountPerEvent - grams added per application (ships as 1).
///  * CharacterItemInteract.progressBarSpeed - how fast the hold-to-use bar fills.
///
/// progressBarSpeed is a property of the player, not of the spice, so raising it
/// blindly would speed up every held-item event in the game - washing dishes, opening
/// packages, everything. Instead it is only raised while the event currently in
/// progress is actually a SpiceItemEvent, and restored the moment it is not.
/// </summary>
public static class SpiceTweaks
{
    /// <summary>Enough to make the bar fill effectively instantly without being absurd.</summary>
    private const float InstantMultiplier = 20f;

    /// <summary>Duration a seasoning event is cut down to, in seconds.</summary>
    private const float InstantEventLength = 0.05f;

    private static bool _patchLogged;

    /// <summary>
    /// The hold before the spice lands is the item event's own length, which comes from
    /// ItemEvent.GetEventLength - not from progressBarSpeed, which drives a different bar
    /// (ProgressBarIsMax). SpiceItemEvent does not override GetEventLength, so patching
    /// the base and filtering by instance type covers every spice without touching any
    /// other held-item event.
    /// </summary>
    public static void Install(BepInEx.Logging.ManualLogSource log)
    {
        try
        {
            var target = HarmonyLib.AccessTools.Method(
                typeof(ItemEvent), nameof(ItemEvent.GetEventLength), new[] { typeof(Animator) });

            if (target == null) { log.LogWarning("Spice: could not resolve ItemEvent.GetEventLength."); return; }

            new HarmonyLib.Harmony(Plugin.Guid + ".spice").Patch(
                target,
                postfix: new HarmonyLib.HarmonyMethod(
                    HarmonyLib.AccessTools.Method(typeof(SpiceTweaks), nameof(EventLengthPostfix))));

            log.LogInfo("Spice: patched ItemEvent.GetEventLength.");
        }
        catch (Exception e)
        {
            log.LogWarning($"Spice: patching GetEventLength failed: {e.Message}");
        }
    }

    private static void EventLengthPostfix(ItemEvent __instance, ref float __result)
    {
        if (!Plugin.SpiceTweaksEnabled.Value || !Plugin.SpiceInstant.Value) return;
        if (__instance == null) return;

        try
        {
            var spice = __instance.TryCast<SpiceItemEvent>();
            if (spice == null || !IsSpiceJar(spice)) return;
        }
        catch { return; }

        if (!_patchLogged)
        {
            _patchLogged = true;
            Plugin.Logger.LogInfo($"Spice event length {__result:0.00}s -> {InstantEventLength:0.00}s");
        }

        __result = Mathf.Min(__result, InstantEventLength);
    }

    private static readonly List<SpiceItemEvent> Events = new();
    private static readonly HashSet<string> Logged = new();

    private static float _scanTimer;
    private static int _lastJarCount = -1;
    private static int _lastOtherCount = -1;
    private static int _defaultAmount = -1;
    private static bool _defaultHideBar;
    private static float _defaultBarSpeed = -1f;
    private static bool _speedBoosted;

    public static string Status = "chưa hoạt động";

    public static void Tick(CharacterItemInteract interact)
    {
        ApplyAmount();
        ApplySpeed(interact);
    }

    // ------------------------------------------------------------- amount

    private static void ApplyAmount()
    {
        _scanTimer -= Time.unscaledDeltaTime;
        if (Events.Count == 0 || _scanTimer <= 0f)
        {
            _scanTimer = 2f;
            Rescan();
        }

        if (Events.Count == 0) return;

        bool on = Plugin.SpiceTweaksEnabled.Value;

        int target = on ? Mathf.Max(1, Plugin.SpiceAmount.Value) : _defaultAmount;
        if (target < 1) return;   // defaults not captured yet

        // Do NOT touch hideProgressBar. It reads like a cosmetic flag, but setting it
        // true stopped seasoning from working at all - the event evidently depends on
        // the progress bar running to reach completion, so hiding it means the spice
        // never lands. Always force it back to the shipped value in case a previous
        // build left it set.
        foreach (var e in Events)
        {
            if (e == null) continue;
            if (e.spillAmountPerEvent != target) e.spillAmountPerEvent = target;
            if (e.hideProgressBar != _defaultHideBar) e.hideProgressBar = _defaultHideBar;
        }
    }

    private static void Rescan()
    {
        try
        {
            var found = UnityEngine.Object.FindObjectsByType<SpiceItemEvent>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            Events.Clear();
            if (found == null) return;

            // SpiceItemEvent is not exclusive to spice jars - other pourable ingredients
            // use it too, which is why changing the amount was affecting everything.
            // Only jars are seasoning.
            int skipped = 0;
            foreach (var e in found)
            {
                if (e == null) continue;
                if (!IsSpiceJar(e)) { skipped++; continue; }
                Events.Add(e);
            }

            // Report only when the counts change. Putting them in LogOnce would make every
            // scan a unique message, growing the dedupe set without bound as items spawn.
            if (Events.Count != _lastJarCount || skipped != _lastOtherCount)
            {
                _lastJarCount = Events.Count;
                _lastOtherCount = skipped;
                Plugin.Logger.LogInfo(
                    $"Spice: {Events.Count} spice jar events, {skipped} other SpiceItemEvent(s) left alone.");
            }

            // Capture the shipped value once, before we have overwritten anything.
            if (_defaultAmount < 0 && Events.Count > 0)
            {
                _defaultAmount = Events[0].spillAmountPerEvent;
                if (_defaultAmount < 1) _defaultAmount = 1;
                _defaultHideBar = Events[0].hideProgressBar;
                Plugin.Logger.LogInfo(
                    $"Spice jars found: {Events.Count}, default amount per use = {_defaultAmount}, " +
                    $"default hideProgressBar = {_defaultHideBar}");
            }
        }
        catch (Exception e)
        {
            LogOnce($"Spice scan failed: {e.Message}");
        }
    }

    // -------------------------------------------------------------- speed

    private static void ApplySpeed(CharacterItemInteract interact)
    {
        if (interact == null) return;

        if (_defaultBarSpeed < 0f) _defaultBarSpeed = interact.progressBarSpeed;

        bool want = Plugin.SpiceTweaksEnabled.Value
                    && Plugin.SpiceInstant.Value
                    && IsSpicing(interact);

        if (want && !_speedBoosted)
        {
            interact.progressBarSpeed = _defaultBarSpeed * InstantMultiplier;
            _speedBoosted = true;
            Status = "đang bỏ độ trễ nêm gia vị";
        }
        else if (!want && _speedBoosted)
        {
            interact.progressBarSpeed = _defaultBarSpeed;
            _speedBoosted = false;
            Status = "chưa hoạt động";
        }
    }

    /// <summary>Whether this event belongs to an actual spice jar rather than some other pourable.</summary>
    private static bool IsSpiceJar(SpiceItemEvent e)
    {
        try
        {
            var item = e.Item;
            return item != null && item.TryCast<Spicejar>() != null;
        }
        catch { return false; }
    }

    /// <summary>
    /// True while seasoning is what the player is doing - either a spice jar event is
    /// already running, or they are simply holding a jar. The second case matters because
    /// a progress bar shown before the event starts would otherwise run at normal speed.
    /// </summary>
    private static bool IsSpicing(CharacterItemInteract interact)
    {
        try
        {
            var current = interact.currentEvent;
            if (current != null)
            {
                var spice = current.TryCast<SpiceItemEvent>();
                if (spice != null && IsSpiceJar(spice)) return true;
            }

            var held = interact.currentItem ?? interact.interactingItem;
            return held != null && held.TryCast<Spicejar>() != null;
        }
        catch (Exception e)
        {
            LogOnce($"Spice event check failed: {e.Message}");
            return false;
        }
    }

    /// <summary>Puts the game's own values back when the feature is switched off.</summary>
    public static void Restore(CharacterItemInteract interact)
    {
        if (_defaultAmount > 0)
            foreach (var e in Events)
            {
                if (e == null) continue;
                e.spillAmountPerEvent = _defaultAmount;
                e.hideProgressBar = _defaultHideBar;
            }

        if (interact != null && _defaultBarSpeed >= 0f && _speedBoosted)
            interact.progressBarSpeed = _defaultBarSpeed;

        _speedBoosted = false;
        Status = "chưa hoạt động";
    }

    private static void LogOnce(string message)
    {
        if (Logged.Add(message)) Plugin.Logger.LogWarning(message);
    }
}
