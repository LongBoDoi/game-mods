using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace PlayerTweaks;

/// <summary>
/// Keeps the cursor free while the overlay is open, no matter who tries to lock it.
///
/// Disabling the game's CursorManager was not enough: the log showed it stayed
/// disabled (1 re-enable) while something else re-locked the cursor 100+ times.
/// The interop assemblies are stubs with no method bodies, so the caller cannot be
/// identified by reading the decompiled source.
///
/// Rather than keep racing an unknown writer once per frame - which is what produced
/// the visible flicker - we patch the property setters themselves and rewrite the
/// incoming value. Every writer still runs and still updates its own state; it just
/// ends up writing the value we want, so there is nothing left to flicker between.
/// </summary>
internal static class CursorGuard
{
    /// <summary>Set by the overlay while it wants a usable mouse pointer.</summary>
    public static bool Active;

    /// <summary>True once both setters are patched; the overlay falls back if not.</summary>
    public static bool Installed { get; private set; }

    public static void Install(ManualLogSource log)
    {
        try
        {
            var harmony = new Harmony(Plugin.Guid + ".cursor");

            bool a = TryPatch(harmony, log, AccessTools.PropertySetter(typeof(Cursor), nameof(Cursor.lockState)),
                              nameof(LockStatePrefix), "Cursor.lockState");
            bool b = TryPatch(harmony, log, AccessTools.PropertySetter(typeof(Cursor), nameof(Cursor.visible)),
                              nameof(VisiblePrefix), "Cursor.visible");

            Installed = a && b;
            log.LogInfo(Installed
                ? "Cursor guard installed - the cursor cannot be locked while the overlay is open."
                : "Cursor guard only partially installed; the overlay will fall back to per-frame writes.");
        }
        catch (Exception e)
        {
            Installed = false;
            log.LogWarning($"Cursor guard failed to install: {e.Message}");
        }
    }

    private static bool TryPatch(Harmony harmony, ManualLogSource log,
                                 System.Reflection.MethodBase target, string prefixName, string label)
    {
        if (target == null)
        {
            log.LogWarning($"Cursor guard: could not resolve {label}.");
            return false;
        }

        try
        {
            harmony.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(typeof(CursorGuard), prefixName)));
            return true;
        }
        catch (Exception e)
        {
            log.LogWarning($"Cursor guard: patching {label} failed: {e.Message}");
            return false;
        }
    }

    // Rewrite the argument instead of skipping the call. Blocking the setter outright
    // would leave callers believing they had locked the cursor, which tends to make
    // them retry forever; letting the write proceed with our value keeps them settled.

    private static void LockStatePrefix(ref CursorLockMode value)
    {
        if (Active) value = CursorLockMode.None;
    }

    private static void VisiblePrefix(ref bool value)
    {
        if (Active) value = true;
    }
}
