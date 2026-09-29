using System;
using System.Collections.Generic;
using Kebab.Interaction;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PlayerTweaks;

/// <summary>
/// Lets you hold the mouse button at a store instead of clicking once per item.
///
/// Store shelves are not UI buttons - they are world-space Interactions. MarketStock,
/// InteractionBasedShoppingItemStock and AppearanceItemMarketStock all derive from
/// Kebab.Interaction.Interaction, which exposes the interaction state and a public
/// TriggerInteraction(InteractionType). So rather than simulating clicks, we re-fire
/// the game's own interaction on whatever shelf you are currently looking at, which
/// keeps prices, stock limits, sounds and network calls exactly as the game does them.
///
/// The first click is still delivered by the game as normal; we only start repeating
/// after a short hold, so a single click adds exactly one item as before.
/// </summary>
public static class StoreRepeater
{
    /// <summary>
    /// Repeats per second while the button is held. Public so the shopping-box put-back
    /// reuses the same cadence - two hold-to-repeat actions that felt different would be
    /// worse than either.
    /// </summary>
    public const float RepeatRate = 30f;

    /// <summary>How long to hold before repeating starts, so one click still adds one.</summary>
    public const float RepeatDelay = 0.3f;

    private static readonly List<Interaction> Stocks = new();
    private static readonly List<MarketStockOverlay> Overlays = new();
    private static readonly HashSet<string> Logged = new();

    private static float _scanTimer;
    private static float _holdTime;
    private static float _nextFire;
    private static int _firedThisHold;

    /// <summary>Shown on the Extras tab so it is obvious what the feature is doing.</summary>
    public static string Status = "chưa hoạt động";

    public static void Tick(bool overlayOpen)
    {
        if (!Plugin.StoreHoldRepeat.Value) { Reset("đã tắt"); return; }

        // While the overlay is up the mouse belongs to our own UI.
        if (overlayOpen) { Reset("đang mở bảng điều khiển"); return; }

        var mouse = Mouse.current;
        if (mouse == null) { Reset("không có chuột"); return; }

        bool left = mouse.leftButton.isPressed;
        bool right = mouse.rightButton.isPressed;

        // Only a full release ends a hold. Previously any frame without a hovered shelf
        // zeroed the timer, so the smallest flicker in the interaction state meant the
        // repeat delay was never reached and nothing ever fired.
        if (!left && !right) { Reset("chưa hoạt động"); return; }

        RefreshStocks();

        var target = FindHovered();
        if (target == null)
        {
            Status = "con trỏ không ở trên kệ hàng";
            return;                       // pause, but keep the hold alive
        }

        _holdTime += Time.unscaledDeltaTime;
        if (_holdTime < RepeatDelay)
        {
            Status = "đang giữ...";
            return;
        }

        if (Time.unscaledTime < _nextFire) return;

        _nextFire = Time.unscaledTime + 1f / RepeatRate;

        // Advisory only. The exact meaning of CanInteract is not visible from the stub
        // assemblies, and silently swallowing every repeat on it is what hid the problem
        // last time - the game's own handlers validate internally anyway.
        try
        {
            if (!target.CanInteract(true))
                LogOnce($"CanInteract(true) is false on {target.name}; firing anyway.");
        }
        catch (Exception e)
        {
            LogOnce($"CanInteract threw on {target.name}: {e.Message}");
        }

        // Right button drives the alternate interaction, which is how the game models
        // "the other" action on a shelf - removing what you just added.
        //
        // The two directions deliberately call different levels of the same pipeline.
        // MarketStock overrides OnObjectInteracted and RPC_ObjectAlternateInteracted,
        // but NOT OnObjectAlternateInteracted - so the obvious symmetric call lands on
        // an empty base method and silently does nothing. Removing has to go one level
        // down, straight to the override that actually carries the logic.
        bool alternate = right && !left;

        try
        {
            if (alternate)
            {
                target.RPC_ObjectAlternateInteracted();
                // That override changes the basket but not the on-screen counter - the
                // counter is driven by the higher-level add path, which the alternate
                // action has no equivalent of. Mirror the decrement onto the overlay so
                // the number tracks the hold the same way it does when adding.
                StepOverlayAmount(-1);
            }
            else
            {
                target.OnObjectInteracted();
            }

            _firedThisHold++;
            if (_firedThisHold == 1 || _firedThisHold == 25)
                Plugin.Logger.LogInfo(
                    $"Store repeat fired {_firedThisHold}x via " +
                    $"{(alternate ? "RPC_ObjectAlternateInteracted" : "OnObjectInteracted")} on {target.name}.");
            Status = $"{(alternate ? "đang bớt" : "đang thêm")} ở {target.name} (x{_firedThisHold})";
        }
        catch (Exception e)
        {
            LogOnce($"{(alternate ? "RPC_ObjectAlternateInteracted" : "OnObjectInteracted")} failed on {target.name}: {e.Message}");
            Reset("thao tác thất bại");
        }
    }

    private static void Reset(string status)
    {
        _holdTime = 0f;
        _nextFire = 0f;
        _firedThisHold = 0;
        Status = status;
    }

    /// <summary>
    /// The shelf currently under the crosshair. Add versus remove is decided by which
    /// method we call, not by which object we pick, so this just finds the hovered one.
    /// </summary>
    private static Interaction FindHovered()
    {
        foreach (var s in Stocks)
        {
            if (s == null || !s.interactable) continue;

            var state = s.m_InteractionState;
            if (state != InteractionState.Hover && state != InteractionState.Interact) continue;

            LogOnce($"Store repeat target: {s.name} ({s.GetIl2CppType().Name})");
            return s;
        }
        return null;
    }

    /// <summary>
    /// Nudges the visible stock counter by <paramref name="delta"/>.
    ///
    /// This is a presentation-only mirror: the basket itself has already been changed by
    /// the interaction. It reads the number the overlay is currently showing rather than
    /// tracking its own, so if the game re-renders the counter from the real basket the
    /// two reconcile instead of drifting apart.
    /// </summary>
    private static void StepOverlayAmount(int delta)
    {
        try
        {
            foreach (var o in Overlays)
            {
                if (o == null || !o.isActiveAndEnabled) continue;

                var label = o.amountText;
                if (label == null) continue;

                if (!TryReadInt(label.text, out int shown)) continue;

                int next = Mathf.Max(0, shown + delta);
                if (next == shown) continue;

                o.SetAmount(next);
                o.DoAmountScalePunch();

                // Deliberately not logging the numbers: this runs up to 30x a second, and
                // a per-value message would both flood the log and grow the dedupe set
                // without bound.
                LogOnce($"Overlay counter mirroring active on {o.name}.");
                return;
            }
        }
        catch (Exception e)
        {
            LogOnce($"Overlay refresh failed: {e.Message}");
        }
    }

    private static bool TryReadInt(string text, out int value)
    {
        value = 0;
        if (string.IsNullOrEmpty(text)) return false;

        // The label carries decoration such as "x12", so keep only the digits.
        var digits = new System.Text.StringBuilder();
        foreach (char c in text)
            if (c >= '0' && c <= '9') digits.Append(c);

        return digits.Length > 0 && int.TryParse(digits.ToString(), out value);
    }

    private static void RefreshStocks()
    {
        _scanTimer -= Time.unscaledDeltaTime;
        if (Stocks.Count > 0 && _scanTimer > 0f) return;

        _scanTimer = 3f;
        Stocks.Clear();

        Collect<MarketStock>();
        Collect<InteractionBasedShoppingItemStock>();
        Collect<AppearanceItemMarketStock>();

        Overlays.Clear();
        try
        {
            var found = UnityEngine.Object.FindObjectsByType<MarketStockOverlay>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (found != null)
                foreach (var o in found)
                    if (o != null) Overlays.Add(o);

            LogOnce($"Market stock overlays found: {Overlays.Count}");
        }
        catch (Exception e)
        {
            LogOnce($"Scan for MarketStockOverlay failed: {e.Message}");
        }
    }

    private static void Collect<T>() where T : Interaction
    {
        try
        {
            var found = UnityEngine.Object.FindObjectsByType<T>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            if (found == null) return;

            foreach (var f in found)
                if (f != null) Stocks.Add(f);
        }
        catch (Exception e)
        {
            LogOnce($"Scan for {typeof(T).Name} failed: {e.Message}");
        }
    }

    private static void LogOnce(string message)
    {
        if (Logged.Add(message)) Plugin.Logger.LogInfo(message);
    }
}
