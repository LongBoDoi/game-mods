using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PlayerTweaks;

/// <summary>
/// Hold the modifier key and click a plate, baking tray or dessert tray to put down everything
/// you are carrying, arranged, instead of placing each item in turn.
///
/// Which call actually places an item took two measurements to find, and both wrong turns are
/// worth recording because the interop stubs carry no bodies to read. Replaying
/// <c>PlateItem</c> with the arguments the game had just used does nothing - it is only the
/// final positioning step. Driving <c>InvokeItemEvent()</c> does nothing either, because
/// <c>currentEvent</c> stays null throughout: that is the family of events belonging to an
/// item itself, such as attaching to an Attacher slot, and free plating is not one of them.
/// Plating is a *fixed* item event - <c>FixedItemEventsCtrl</c> offers
/// <c>FixedItemEvent.Plating</c> every frame you aim at something that takes a placement, and
/// the click runs <c>InvokeFixedItemEvent()</c>. That is what this calls.
///
/// That offer is also the only check on where a run may start. It is the game's own answer to
/// "will this take an item", so it covers every container without a list of types to keep up
/// to date, and it excludes the ones that must not be swept up - a counter offers PutDown and
/// a bin offers Trashing, and neither is this.
///
/// Two things make the run read as one action rather than a sequence.
///
/// Nothing here waits on a timer. Earlier versions spaced the placements out and waited a
/// further third of a second before arranging, which is most of what made the armful go down
/// visibly one at a time. Each step now happens on the first frame the game will accept it.
///
/// And the plate is arranged as items land, not afterwards, with the *final* item count fixed
/// at the start - see the minSlots argument on <see cref="PlateTweaks.AlignPlate"/>. Every
/// item therefore drops straight into the slot it is going to keep, instead of the plate
/// re-flowing on each arrival and everything snapping into place at the end.
///
/// What remains is the game's own floor: an item can only be plated once it is in hand, and
/// the game animates it there. That cannot be removed without bypassing the plating event,
/// which owns the parenting, the carry queue and the networked side - and a plate's contents
/// decide which dish it is, so getting that wrong would not be cosmetic.
/// </summary>
public static class PlaceAll
{
    /// <summary>Hard cap on one run, so a mis-read queue cannot loop indefinitely.</summary>
    private const int MaxPerRun = 64;

    /// <summary>Abandon a run that has made no progress at all for this long.</summary>
    private const float StallTimeout = 1.5f;

    /// <summary>
    /// Wait this long after asking for the next item before asking again. Not a pace - the
    /// game answers on its own most of the time and this only stops the request repeating
    /// every frame while the pickup it already started is still running.
    /// </summary>
    private const float PullRetry = 0.25f;

    private enum Phase { Idle, Placing }

    public static string Status = "chưa hoạt động";

    private static Phase _phase = Phase.Idle;

    private static CharacterItemInteract _owner;
    private static Transform _plate;

    private static int _slots;           // items the plate will hold when the run finishes
    private static int _startedWith;     // items carried when the run started
    private static int _lastOnPlate;
    private static IntPtr _lastHand;
    private static float _lastProgressAt;
    private static float _pullAt;

    // Set from the PlatingEventCtrl postfix: the game offering a plating this frame is the
    // only trustworthy "you can place right now" signal, and it costs nothing to read.
    private static int _platingFrame = -1;
    private static CharacterItemInteract _platingOwner;

    private static readonly HashSet<string> Logged = new();

    public static void Install(BepInEx.Logging.ManualLogSource log)
    {
        try
        {
            var target = HarmonyLib.AccessTools.Method(
                typeof(CharacterItemInteract), nameof(CharacterItemInteract.PlatingEventCtrl));

            if (target == null)
            {
                log.LogWarning("PlaceAll: PlatingEventCtrl not found; feature off.");
                return;
            }

            new HarmonyLib.Harmony(Plugin.Guid + ".placeall").Patch(
                target,
                postfix: new HarmonyLib.HarmonyMethod(
                    HarmonyLib.AccessTools.Method(typeof(PlaceAll), nameof(PlatingCtrlPostfix))));

            log.LogInfo("PlaceAll: ready - modifier + click places a whole armful onto any " +
                        "plate or tray that accepts a plating.");
        }
        catch (Exception e)
        {
            log.LogWarning($"PlaceAll: install failed: {e.Message}");
        }
    }

    /// <summary>
    /// Records that the game is offering a plating this frame.
    ///
    /// Read-only; the return value is left exactly as the game computed it. Asking the game
    /// whether a placement is available beats inferring it from what is in hand and what is
    /// under the crosshair, both of which can be true when plating is still refused.
    /// </summary>
    private static void PlatingCtrlPostfix(CharacterItemInteract __instance, bool __result)
    {
        if (!__result || __instance == null) return;
        _platingFrame = Time.frameCount;
        _platingOwner = __instance;
    }

    public static void Tick(CharacterItemInteract interact, bool overlayOpen)
    {
        if (_phase == Phase.Idle)
        {
            CheckTrigger(interact, overlayOpen);
            return;
        }

        // Only ever drive the local player's character. The patch is on the type, so a remote
        // player's plating runs through it too.
        if (interact == null || _owner == null || interact != _owner) { Finish("mất nhân vật"); return; }
        if (!Plugin.PlaceAllEnabled.Value) { Finish(null); return; }

        TickPlacing();
    }

    /// <summary>
    /// Starts a run on modifier + click.
    ///
    /// The click is deliberately not swallowed: the game handles it as an ordinary placement
    /// and puts the first item down, and this takes over for the rest. Blocking it would mean
    /// reproducing the first placement too, for no gain.
    /// </summary>
    private static void CheckTrigger(CharacterItemInteract interact, bool overlayOpen)
    {
        if (overlayOpen || interact == null) return;
        if (!Plugin.PlaceAllEnabled.Value) return;
        if (!ModifierHeld()) return;

        try
        {
            if (Mouse.current == null || !Mouse.current.leftButton.wasPressedThisFrame) return;
        }
        catch { return; }

        int carried = QueueCount(interact);
        if (carried <= 0) { Status = "chỉ đang cầm một vật phẩm"; return; }

        // The only gate that matters. The game offering FixedItemEvent.Plating at what you are
        // aiming at means it will take a placement there, whether that is a plate, a baking
        // tray or a dessert tray - and it rules out the cases that must not be swept up,
        // since a counter offers PutDown and a bin offers Trashing, neither of which is this.
        // A type test of our own would only be a worse version of the same question.
        if (!PlatingOffered(interact)) { Status = "hãy nhìn vào một chiếc đĩa hoặc khay"; return; }

        var plate = PlateTweaks.ResolvePlacementTarget(interact);
        if (plate == null)
        {
            Status = "không xác định được chỗ đặt";
            return;
        }

        _owner = interact;
        _plate = plate;
        _startedWith = carried + 1;
        _lastOnPlate = PlateTweaks.CountPlacedItems(plate);
        _slots = Mathf.Min(_lastOnPlate + _startedWith, MaxPerRun);
        _lastHand = IntPtr.Zero;
        _phase = Phase.Placing;

        float now = Time.unscaledTime;
        _lastProgressAt = now;
        _pullAt = now;

        Status = $"đang đặt {_startedWith} vật phẩm...";
        LogOnce($"PlaceAll: run started with {_startedWith} item(s) onto '{plate.name}', " +
                $"which already holds {_lastOnPlate}; laying out for {_slots} slots.");
    }

    private static void TickPlacing()
    {
        float now = Time.unscaledTime;

        // Let the aim reach the plate through whatever is already on it, then ask the game to
        // re-decide whether a placement is available. Both are needed: without the first the
        // crosshair ends up resting on the item just placed and the game offers nothing,
        // without the second it never re-evaluates while the mouse is still.
        SeeThroughPlacedItems();
        RefreshPlacement();

        var hand = Hand();

        // Arrange the moment something lands, into the layout fixed when the run started, so
        // each item appears where it is going to stay.
        int on = PlateTweaks.CountPlacedItems(_plate);
        if (on != _lastOnPlate)
        {
            _lastOnPlate = on;
            _lastProgressAt = now;
            Arrange(_slots);
        }

        if (hand != null && hand.Pointer != _lastHand)
        {
            _lastHand = hand.Pointer;
            _lastProgressAt = now;
        }

        if (hand == null)
        {
            if (QueueCount(_owner) <= 0) { Finish(null); return; }

            // The game brings the next item down by itself after a placement. Ask only if it
            // has not, and not again while the pickup it started is still running.
            if (now >= _pullAt)
            {
                _pullAt = now + PullRetry;
                try { _owner.PickUpQueuedItem(); }
                catch (Exception e)
                {
                    LogOnce($"PlaceAll: PickUpQueuedItem failed: {e.GetType().Name}: {e.Message}");
                    Finish("không lấy được vật phẩm tiếp theo");
                    return;
                }
            }
        }
        else if (!Busy() && PlatingOffered(_owner))
        {
            // No throttle. The game's own inItemEvent flag is what paces this, and adding a
            // delay on top of it is exactly what made the armful go down one visible item at
            // a time.
            try { _owner.InvokeFixedItemEvent(); }
            catch (Exception e)
            {
                LogOnce($"PlaceAll: InvokeFixedItemEvent failed: {e.GetType().Name}: {e.Message}");
                Finish("gọi thao tác đặt thất bại");
                return;
            }
        }

        if (now - _lastProgressAt > StallTimeout)
            Finish(hand == null ? "chờ vật phẩm tiếp theo quá lâu"
                                : "không còn chỗ đặt - " + Describe());
    }

    private static void Finish(string why)
    {
        if (why != null) LogOnce($"PlaceAll: stopped - {why}.");

        // Before anything else: the plate has to be solid again whatever happened above.
        RestorePlacedItemColliders();

        var plate = _plate;
        int placed = Mathf.Max(0, _startedWith - QueueCount(_owner) - (Hand() == null ? 0 : 1));

        // A last pass with the real count, so a run that placed fewer than expected still
        // ends on a tidy plate rather than a grid with holes in it.
        _phase = Phase.Idle;
        _owner = null;
        _plate = null;

        try
        {
            if (plate != null) PlateTweaks.AlignPlate(plate);
            Status = why == null ? $"đã đặt và canh {placed} vật phẩm"
                                 : $"đã đặt {placed} vật phẩm rồi dừng";
        }
        catch (Exception e)
        {
            Status = $"đã đặt {placed} vật phẩm, canh thất bại";
            LogOnce($"PlaceAll: final arrange failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void Arrange(int slots)
    {
        try { PlateTweaks.AlignPlate(_plate, slots); }
        catch (Exception e)
        {
            LogOnce($"PlaceAll: arrange failed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Colliders switched off for the run, so they can be switched back on.</summary>
    private static readonly List<Collider> _suppressed = new();

    /// <summary>
    /// Hides the items already on the plate from the aim ray, for the duration of the run.
    ///
    /// This is the thing that made a single click place only one item. The moment the first
    /// item lands, it is sitting under the crosshair - so the game's raycast reports that
    /// item, not the plate, and no plating is offered. Nudging the mouse to aim at bare plate
    /// was doing this by hand.
    ///
    /// Disabling the colliders rather than filtering the hit afterwards is deliberate: the
    /// game does its own raycast inside the calls being driven here, and a RaycastHit's
    /// collider cannot be rewritten from outside anyway. Turning them off means the game's own
    /// ray reaches the plate with nothing else to change.
    ///
    /// Re-scanned every frame, because each arrival adds another obstacle. Everything switched
    /// off is remembered and restored in <see cref="Finish"/>; the items are parented to the
    /// plate and moved by transform rather than physics, so they do not fall anywhere in the
    /// meantime.
    /// </summary>
    private static void SeeThroughPlacedItems()
    {
        try
        {
            foreach (var item in PlateTweaks.PlacedItems(_plate))
            {
                if (item == null) continue;

                var colliders = item.GetComponentsInChildren<Collider>(false);
                if (colliders == null) continue;

                foreach (var col in colliders)
                {
                    if (col == null || !col.enabled) continue;
                    col.enabled = false;
                    _suppressed.Add(col);
                }
            }
        }
        catch (Exception e)
        {
            LogOnce($"PlaceAll: could not clear the aim path: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void RestorePlacedItemColliders()
    {
        foreach (var col in _suppressed)
        {
            try { if (col != null) col.enabled = true; }
            catch { }
        }
        _suppressed.Clear();
    }

    /// <summary>
    /// Makes the character work out afresh whether a placement is available.
    ///
    /// Both calls are the game's own, and both are ones it makes every frame itself while you
    /// are aiming and moving. Running them from here is what lets a run finish with the mouse
    /// held perfectly still.
    ///
    /// The raycast is taken fresh where possible and falls back to the character's last one.
    /// A stale hit is fine for this purpose: it still points at the plate, which is exactly
    /// where the player is looking when they have not moved.
    /// </summary>
    private static void RefreshPlacement()
    {
        try { _owner.SendEventRays(); }
        catch (Exception e) { LogOnce($"PlaceAll: SendEventRays failed: {e.GetType().Name}: {e.Message}"); }

        if (PlatingOffered(_owner)) return;

        try
        {
            var hit = _owner.hitInfo;

            try
            {
                var fresh = _owner.SendInteractionRaycast();
                if (fresh != null && fresh.HasValue) hit = fresh.Value;
            }
            catch { /* keep the character's own last hit */ }

            _owner.FixedItemEventsCtrl(ref hit);
        }
        catch (Exception e)
        {
            LogOnce($"PlaceAll: FixedItemEventsCtrl failed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// Whether the game is offering a plating right now.
    ///
    /// Two signals, because neither alone is enough. PlatingEventCtrl returning true is the
    /// most direct evidence but only exists on frames the character bothered to evaluate -
    /// one frame of slack, since the two components' Update order is arbitrary but fixed.
    /// FixedEventsAvailable persists between evaluations, so it covers the frames in between.
    ///
    /// The event type is checked rather than assumed: the same call also runs put-down and
    /// trashing, and invoking blind at a bin would throw the armful away.
    /// </summary>
    private static bool PlatingOffered(CharacterItemInteract interact)
    {
        if (interact == null) return false;

        if (_platingOwner != null && _platingOwner == interact &&
            Time.frameCount - _platingFrame <= 1)
        {
            return true;
        }

        try { return interact.FixedEventsAvailable && interact.fixedEventType == FixedItemEvent.Plating; }
        catch { return false; }
    }

    private static bool ModifierHeld()
    {
        try
        {
            var kb = Keyboard.current;
            if (kb == null) return false;

            switch ((Plugin.PlaceAllModifier.Value ?? "Shift").Trim().ToLowerInvariant())
            {
                case "none": return true;
                case "ctrl":
                case "control": return kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed;
                case "alt": return kb.leftAltKey.isPressed || kb.rightAltKey.isPressed;
                default: return kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
            }
        }
        catch { return false; }
    }

    /// <summary>The character's own view of why it will not place, for the log.</summary>
    private static string Describe()
    {
        try
        {
            return $"available={_owner.FixedEventsAvailable}, type={_owner.fixedEventType}, " +
                   $"inEvent={_owner.inItemEvent}, interacting={_owner.isInteracting}";
        }
        catch { return "?"; }
    }

    /// <summary>True while an item event or interaction is already running.</summary>
    private static bool Busy()
    {
        try { return _owner.inItemEvent || _owner.isInteracting; }
        catch { return false; }
    }

    private static ItemIdentifier Hand()
    {
        try { return _owner == null ? null : _owner.currentItem; }
        catch { return null; }
    }

    private static int QueueCount(CharacterItemInteract interact)
    {
        try
        {
            if (interact == null) return 0;
            var queue = interact.pickedItemsWaitingInQueue;
            return queue == null ? 0 : queue.Count;
        }
        catch { return 0; }
    }

    private static void LogOnce(string message)
    {
        if (Logged.Add(message)) Plugin.Logger.LogInfo(message);
    }
}
