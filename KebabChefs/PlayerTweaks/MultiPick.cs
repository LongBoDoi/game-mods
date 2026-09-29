using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PlayerTweaks;

/// <summary>
/// Scroll while looking at an item to mark more of the same kind; they are picked up
/// together when you click.
///
/// Scrolling only marks. Nothing is handed to the game until the pickup actually happens,
/// so changing your mind costs nothing and the items stay where they are.
///
/// The pickup itself goes through the game's own queue - AddItemToPickupQueue, gated by
/// CanQueueItem - so the carry limit is respected without us second-guessing it, including
/// the raised limit from the carry tweak, which works by lengthening the very array that
/// check consults.
/// </summary>
public static class MultiPick
{
    private static readonly List<ItemIdentifier> Marked = new();
    private static readonly HashSet<string> Logged = new();

    private static string _forItem;

    public static string Status = "chưa hoạt động";

    // ------------------------------------------------------------------ patch

    /// <summary>
    /// Hooks the pickup so the marked extras go along with it.
    ///
    /// PickUpItem is declared on CharacterItemInteract, is not virtual and nothing
    /// overrides it, so __instance can only be the type asked for.
    /// </summary>
    public static void Install(BepInEx.Logging.ManualLogSource log)
    {
        try
        {
            var target = HarmonyLib.AccessTools.Method(
                typeof(CharacterItemInteract), nameof(CharacterItemInteract.PickUpItem));

            if (target == null) { log.LogWarning("MultiPick: could not resolve PickUpItem."); return; }

            if (target.DeclaringType != typeof(CharacterItemInteract))
            {
                log.LogWarning($"MultiPick: PickUpItem resolves to {target.DeclaringType?.Name} - " +
                               "refusing to patch a shared method.");
                return;
            }

            new HarmonyLib.Harmony(Plugin.Guid + ".multipick").Patch(
                target,
                postfix: new HarmonyLib.HarmonyMethod(
                    HarmonyLib.AccessTools.Method(typeof(MultiPick), nameof(OnPickedUp))));

            log.LogInfo("MultiPick: patched CharacterItemInteract.PickUpItem.");

            // With hands full the game bypasses PickUpItem entirely - neither it nor
            // PickUpQueuedItem fires - so hook the queue entry as well. Whichever runs
            // first takes the marked extras; the _inBulk guard stops them doubling up.
            HookAlso(log, nameof(CharacterItemInteract.PickUpQueuedItem), nameof(OnQueuedPickUp));
            HookAlso(log, nameof(CharacterItemInteract.AddItemToPickupQueue), nameof(OnAddedToQueue));
        }
        catch (Exception e)
        {
            log.LogWarning($"MultiPick: install failed: {e.Message}");
        }
    }

    /// <summary>Guards against re-entering our own postfix when we call PickUpItem.</summary>
    private static bool _inBulk;

    private static int _takeLogs;
    private static int _entryLogs;
    private static int _queuedEntryLogs;

    /// <summary>
    /// The queued-pickup path. With hands already full the game may complete a pickup
    /// here rather than through PickUpItem, in which case the other hook never fires.
    /// </summary>
    private static void OnQueuedPickUp(CharacterItemInteract __instance)
    {
        if (_queuedEntryLogs < 20 && __instance != null)
        {
            _queuedEntryLogs++;
            Plugin.Logger.LogInfo(
                $"MultiPick: PickUpQueuedItem hook fired (marked={Marked.Count}, held={HeldCount(__instance)}).");
        }

        OnPickedUp(__instance);
    }

    private static int _queueAddLogs;

    /// <summary>The queue entry, which is how a pickup completes when hands are full.</summary>
    private static void OnAddedToQueue(CharacterItemInteract __instance)
    {
        // Our own TryTake calls this method, so without the guard each take would
        // re-enter and recurse through the whole marked list.
        if (_inBulk) return;

        if (_queueAddLogs < 20 && __instance != null)
        {
            _queueAddLogs++;
            Plugin.Logger.LogInfo(
                $"MultiPick: AddItemToPickupQueue hook fired (marked={Marked.Count}, held={HeldCount(__instance)}).");
        }

        OnPickedUp(__instance);
    }

    private static void HookAlso(BepInEx.Logging.ManualLogSource log, string method, string handler)
    {
        try
        {
            var target = HarmonyLib.AccessTools.Method(typeof(CharacterItemInteract), method);
            if (target == null || target.DeclaringType != typeof(CharacterItemInteract))
            {
                log.LogWarning($"MultiPick: skipping hook on {method}.");
                return;
            }

            new HarmonyLib.Harmony($"{Plugin.Guid}.multipick.{method}").Patch(
                target,
                postfix: new HarmonyLib.HarmonyMethod(
                    HarmonyLib.AccessTools.Method(typeof(MultiPick), handler)));

            log.LogInfo($"MultiPick: patched CharacterItemInteract.{method}.");
        }
        catch (Exception e)
        {
            log.LogWarning($"MultiPick: could not patch {method}: {e.Message}");
        }
    }

    /// <summary>
    /// Attempts each known way of taking an item, in order of how authoritative it is,
    /// stopping as soon as the held count actually rises. Returns which route worked.
    ///
    /// The server RPC is tried first because picking up is server-mediated - the same
    /// reason a raised carry limit does nothing when the host is unmodded.
    /// </summary>
    /// <summary>
    /// The plate or tray being carried, or null.
    ///
    /// Read from currentItem, the waiting queue and the hand holders together, because none
    /// of the three is the whole truth on its own - a plate can be absent from the first two
    /// while plainly still in your hands.
    /// </summary>
    private static Transform HeldContainer(CharacterItemInteract interact)
    {
        try
        {
            var current = interact.currentItem;
            if (current != null && PlateTweaks.IsContainer(current.transform))
                return current.transform;

            var queue = interact.pickedItemsWaitingInQueue;
            if (queue != null)
                for (int i = 0; i < queue.Count; i++)
                {
                    var carried = queue[i];
                    if (carried == null) continue;
                    if (PlateTweaks.IsContainer(carried.transform)) return carried.transform;
                }

            var holders = interact.itemHolderQueue;
            if (holders != null)
                for (int i = 0; i < holders.Length; i++)
                {
                    var holder = holders[i];
                    if (holder == null) continue;

                    for (int c = 0; c < holder.childCount; c++)
                    {
                        var child = holder.GetChild(c);
                        if (child != null && PlateTweaks.IsContainer(child)) return child;
                    }
                }
        }
        catch { }

        return null;
    }

    private static string TryTake(CharacterItemInteract interact, ItemIdentifier item)
    {
        // Success means the item is physically in hand - parented under the player's
        // holder. A rising queue count is not the same thing: it went up while nothing
        // was actually collected, which is why this looked fixed when it was not.
        if (IsHeld(interact, item)) return "already held";

        // Carrying a plate changes what success means: InteractWithItem may put the item on
        // the plate rather than in the hands, and that is the wanted outcome, not a failure.
        // Escalating past it through the routes below is what dragged plated items back out
        // into the hands one at a time.
        bool carryingContainer = HeldContainer(interact) != null;

        try
        {
            interact.InteractWithItem(item);
            if (IsHeld(interact, item)) return "InteractWithItem";
            if (carryingContainer) return "InteractWithItem (onto the plate)";
        }
        catch (Exception e) { LogOnce($"MultiPick: InteractWithItem threw: {e.Message}"); }

        try
        {
            interact.PickUpItemServerRpc(item.itemIndex, OwnerId(interact));
            if (IsHeld(interact, item)) return "PickUpItemServerRpc";
        }
        catch (Exception e) { LogOnce($"MultiPick: PickUpItemServerRpc threw: {e.Message}"); }

        try
        {
            interact.PickUpItem(item);
            if (IsHeld(interact, item)) return "PickUpItem";
        }
        catch (Exception e) { LogOnce($"MultiPick: PickUpItem threw: {e.Message}"); }

        try
        {
            interact.AddItemToPickupQueue(item);
            interact.PickUpQueuedItem();
            if (IsHeld(interact, item)) return "queue + PickUpQueuedItem";
        }
        catch (Exception e) { LogOnce($"MultiPick: queued pickup threw: {e.Message}"); }

        return "nothing worked";
    }

    private static ulong OwnerId(CharacterItemInteract interact)
    {
        try { return interact.OwnerClientId; } catch { return 0UL; }
    }

    /// <summary>Picks up the marked extras alongside the item that was just taken.</summary>
    private static void OnPickedUp(CharacterItemInteract __instance)
    {
        // Log entry before any early return. If this never appears while carrying, the
        // game is not routing full-handed pickups through PickUpItem at all and the hook
        // is simply never firing - a different problem from the take failing.
        if (_entryLogs < 20 && __instance != null)
        {
            _entryLogs++;
            Plugin.Logger.LogInfo(
                $"MultiPick: PickUpItem hook fired (marked={Marked.Count}, held={HeldCount(__instance)}).");
        }

        if (_inBulk || Marked.Count == 0 || __instance == null) return;

        _inBulk = true;
        int taken = 0;

        try
        {
            foreach (var item in Marked)
            {
                if (item == null) continue;

                // The game's own capacity check, so a full pair of hands stops us.
                bool room;
                try { room = __instance.CanQueueItem; } catch { room = false; }
                if (!room)
                {
                    Plugin.Logger.LogInfo(
                        $"MultiPick: stopped after {taken} extra(s) - CanQueueItem went false.");
                    break;
                }

                ClearHighlight(item);

                // Try each route and keep whichever actually puts the item in hand.
                int heldBefore = HeldCount(__instance);
                string used = TryTake(__instance, item);
                bool got = IsHeld(__instance, item);

                if (got) taken++;

                // Not LogOnce: the message was identical every time, so it printed once
                // and hid every later attempt - including all the ones while carrying,
                // which is the case that actually fails. Bounded instead of deduped.
                if (_takeLogs < 40)
                {
                    _takeLogs++;
                    Plugin.Logger.LogInfo(
                        $"MultiPick: take #{_takeLogs} heldBefore={heldBefore} via '{used}' " +
                        $"-> inHand={got} heldAfter={HeldCount(__instance)}");
                }
            }

            Plugin.Logger.LogInfo($"MultiPick: picked up {taken + 1} items together.");
        }
        catch (Exception e)
        {
            LogOnce($"MultiPick: bulk pickup failed: {e.Message}");
        }
        finally
        {
            _inBulk = false;
            ClearAll();
            Status = $"đã nhặt {taken + 1} vật phẩm";
        }
    }

    // ------------------------------------------------------------------- tick

    public static void Tick(CharacterItemInteract interact, bool overlayOpen)
    {
        if (!Plugin.MultiPickEnabled.Value)
        {
            if (Marked.Count > 0) ClearAll();
            Status = "đã tắt";
            return;
        }

        if (overlayOpen || interact == null) return;

        var target = ResolveTarget(interact);

        // Aiming somewhere else abandons the marks: they belong to what you were looking
        // at, and quietly carrying them to the next item would be worse than starting over.
        string key = ItemKey(target);
        if (key != _forItem)
        {
            if (Marked.Count > 0) ClearAll();
            _forItem = key;
        }

        // The game refreshes outlines every frame for whatever is hovered, which wipes
        // ours as soon as they are set. Re-applying each frame is what keeps marked items
        // visibly outlined rather than flickering once and disappearing.
        RefreshHighlights(interact);

        float scroll = ReadScroll();
        bool scrolled = Mathf.Abs(scroll) > 0.01f;

        if (target == null)
        {
            // Report a scroll that found nothing to act on. Returning quietly here is what
            // made this look like the scroll input was being ignored entirely.
            if (scrolled && IsShiftHeld())
                LogOnce("MultiPick: shift-scrolled but nothing aimed at was usable as a target.");
            Status = "giữ Shift và lăn chuột trên một vật phẩm";
            return;
        }

        if (!scrolled) return;

        // Shift is what separates marking from the game's own use of the wheel. Without
        // the modifier the scroll is left entirely alone, so cycling held items keeps
        // working normally and marking only happens when it is asked for.
        //
        // Marks made earlier are deliberately kept when Shift is released - you need both
        // hands free to aim and click, and dropping the selection on release would make
        // the feature unusable.
        if (!IsShiftHeld())
        {
            if (Marked.Count > 0) Status = $"đã đánh dấu {Marked.Count + 1} - nhấn chuột để nhặt";
            return;
        }

        if (scroll > 0f) MarkOne(interact, target);
        else UnmarkOne();
    }

    private static void MarkOne(CharacterItemInteract interact, ItemIdentifier target)
    {
        try
        {
            // Room left is the holder count minus what is already in hand, minus one for
            // the item being pointed at. The old check compared against total capacity and
            // ignored what was already held, so it misjudged the limit whenever the hands
            // were not empty.
            int capacity = CarryCapacity(interact);
            int held = HeldCount(interact);
            int room = capacity - held - 1;

            LogState(capacity, held, room, interact);

            if (capacity > 0 && Marked.Count >= room)
            {
                Status = room <= 0
                    ? $"tay gần đầy ({held}/{capacity}) - không còn chỗ"
                    : $"đã đánh dấu {Marked.Count + 1} - đó là tất cả những gì bạn mang được";
                return;
            }

            var next = FindNextMatching(interact, target);
            if (next == null)
            {
                Status = $"đã đánh dấu {Marked.Count + 1} - không còn cái nào trong {Plugin.MultiPickRadius.Value:0.##}m";
                return;
            }

            Marked.Add(next);
            SetHighlight(interact, next);

            Status = $"đã đánh dấu {Marked.Count + 1}";
            LogOnce($"MultiPick: marking extra '{ItemKey(target)}' items.");
        }
        catch (Exception e)
        {
            LogOnce($"MultiPick: mark failed: {e.Message}");
        }
    }

    private static void UnmarkOne()
    {
        if (Marked.Count == 0) { Status = "đã đánh dấu 1"; return; }

        var last = Marked[Marked.Count - 1];
        Marked.RemoveAt(Marked.Count - 1);
        ClearHighlight(last);

        Status = $"đã đánh dấu {Marked.Count + 1}";
    }

    /// <summary>How many items can be held, from the game's own holder array.</summary>
    private static int CarryCapacity(CharacterItemInteract interact)
    {
        try
        {
            var queue = interact.itemHolderQueue;
            return queue == null ? 0 : queue.Length;
        }
        catch { return 0; }
    }

    /// <summary>How many are already in hand.</summary>
    private static int HeldCount(CharacterItemInteract interact)
    {
        try
        {
            var queued = interact.pickedItemsWaitingInQueue;
            int n = queued == null ? 0 : queued.Count;

            // The item actually in hand is not always part of that queue.
            try { if (interact.currentItem != null) n++; } catch { }

            return n;
        }
        catch { return 0; }
    }

    /// <summary>
    /// One line per distinct state, so a refusal while holding items can be traced to the
    /// numbers rather than guessed at.
    /// </summary>
    private static void LogState(int capacity, int held, int room, CharacterItemInteract interact)
    {
        bool canQueue = false;
        try { canQueue = interact.CanQueueItem; } catch { }

        LogOnce($"MultiPick: capacity={capacity} held={held} room={room} CanQueueItem={canQueue}");
    }

    /// <summary>
    /// Nearest item of the same kind that is not already marked.
    ///
    /// Distance is measured from the item you are pointing at, not from the player, so
    /// marking spreads outwards through the pile in front of you rather than reaching
    /// across the room for something that merely happens to be the same kind.
    /// </summary>
    private static ItemIdentifier FindNextMatching(CharacterItemInteract interact, ItemIdentifier target)
    {
        string key = ItemKey(target);
        if (string.IsNullOrEmpty(key)) return null;

        var from = target.transform.position;
        float radius = Mathf.Max(0.1f, Plugin.MultiPickRadius.Value);

        ItemIdentifier best = null;
        float bestSqr = radius * radius;

        // Track the closest same-kind item we rejected, so "none nearby" can be told
        // apart from "one just outside the radius".
        int sameKind = 0;
        float nearestRejected = float.MaxValue;

        try
        {
            var all = UnityEngine.Object.FindObjectsByType<ItemIdentifier>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            if (all == null) return null;

            foreach (var candidate in all)
            {
                if (candidate == null || candidate.Pointer == target.Pointer) continue;
                if (!string.Equals(ItemKey(candidate), key, StringComparison.Ordinal)) continue;
                if (IsMarked(candidate)) continue;

                // Skip anything already in hand. Held items are the same kind and sit
                // right next to the target, so without this the search kept "finding"
                // the ones you are carrying and marking those instead of the pile.
                if (IsHeld(interact, candidate)) continue;

                if (IsSpokenFor(candidate)) continue;

                sameKind++;
                float sqr = (candidate.transform.position - from).sqrMagnitude;

                if (sqr < bestSqr) { bestSqr = sqr; best = candidate; }
                else if (sqr < nearestRejected) nearestRejected = sqr;
            }
        }
        catch (Exception e)
        {
            LogOnce($"MultiPick: search failed: {e.Message}");
        }

        if (best == null)
        {
            string nearest = nearestRejected == float.MaxValue
                ? "none"
                : $"{Mathf.Sqrt(nearestRejected):0.##}m";
            LogOnce($"MultiPick: no match within {radius:0.##}m of '{key}' " +
                    $"({sameKind} same-kind not held, nearest {nearest}).");

            // Say why in the panel too. "Nothing happened" is the same experience whether
            // there is no second item, or one exists but is out of range, and the two want
            // opposite responses from the player - fetch another, or widen the radius.
            Status = sameKind == 0
                ? $"không còn '{key}' nào khác đang rời (thứ khác đã ở trên tay hoặc trong thùng)"
                : $"'{key}' gần nhất cách {nearest}, ngoài bán kính {radius:0.##}m";
        }

        return best;
    }

    private static bool IsMarked(ItemIdentifier item)
    {
        foreach (var m in Marked)
            if (m != null && m.Pointer == item.Pointer) return true;
        return false;
    }

    /// <summary>Whether the player is already carrying this item.</summary>
    private static bool IsHeld(CharacterItemInteract interact, ItemIdentifier item)
    {
        try
        {
            if (interact.IsItemQueued(item)) return true;
        }
        catch { }

        try
        {
            var current = interact.currentItem;
            if (current != null && current.Pointer == item.Pointer) return true;
        }
        catch { }

        // Carried items are parented under the holder, so anything below it is in hand
        // even if the queue does not list it.
        try
        {
            var holder = interact.itemHolderQueueParent;
            if (holder != null && item.transform != null && item.transform.IsChildOf(holder)) return true;
        }
        catch { }

        return false;
    }

    // -------------------------------------------------------------- highlight

    /// <summary>Only used when the border cannot be built - see SetHighlight.</summary>
    private static readonly Color HighlightTint = new Color(0.35f, 1f, 0.45f, 1f);

    /// <summary>Stand-in for the game's border colour if its palette cannot be read.</summary>
    private static readonly Color FallbackOutline = new Color(0.25f, 0.62f, 1f, 1f);

    /// <summary>
    /// A border we put on a marked item, and whether we are the ones who created it.
    ///
    /// The distinction matters on unmark: an effect the game placed - it adds one to
    /// whatever you hover - has to be switched off and left in place, because destroying it
    /// would take the game's own hover border away with it.
    /// </summary>
    private readonly struct MarkOutline
    {
        public readonly HighlightPlus.HighlightEffect Effect;
        public readonly bool Created;

        public MarkOutline(HighlightPlus.HighlightEffect effect, bool created)
        {
            Effect = effect;
            Created = created;
        }
    }

    private static readonly Dictionary<IntPtr, MarkOutline> _outlines = new();

    /// <summary>Items we tinted, so the fallback only ever clears what it set itself.</summary>
    private static readonly HashSet<IntPtr> _tinted = new();

    private static UnityOutlineFX _outlineFX;
    private static float _nextFXSearch;

    /// <summary>Drops to false the first time a border cannot be built, so the tint takes over.</summary>
    private static bool _outlineUsable = true;

    /// <summary>
    /// Marks an item with the same border the game draws around whatever you are looking at.
    ///
    /// UnityOutlineFX.ShowOutline is the call the game itself uses and it was tried first,
    /// but nothing ever rendered from outside - that manager keeps its own cache of the
    /// currently hovered object and rebuilds it every frame, so an entry we add is gone
    /// before anything draws. What it *builds*, though, is an ordinary Highlight Plus
    /// effect, and the render pass draws every enabled effect in the scene rather than only
    /// the ones that manager knows about. So putting our own effect on the item and loading
    /// the manager's profile into it gives the identical border while staying completely
    /// out of the game's hover state - neither of us can clear the other.
    /// </summary>
    private static bool TryOutline(ItemIdentifier item)
    {
        IntPtr key = item.Pointer;

        if (_outlines.TryGetValue(key, out var known))
        {
            var effect = known.Effect;
            if (effect == null) { _outlines.Remove(key); }
            else
            {
                // Re-assert rather than rebuild. Calling SetHighlighted on something already
                // lit restarts its fade-in, which at 60Hz reads as a flicker.
                if (!effect._highlighted) effect.SetHighlighted(true);
                return true;
            }
        }

        var go = item.gameObject;
        if (go == null) return false;

        var existing = go.GetComponent<HighlightPlus.HighlightEffect>();
        if (existing != null)
        {
            if (!existing._highlighted) existing.SetHighlighted(true);
            _outlines[key] = new MarkOutline(existing, false);
            return true;
        }

        var made = go.AddComponent<HighlightPlus.HighlightEffect>();
        if (made == null) return false;

        // The profile carries the whole look - width, blur, quality, see-through. Loading
        // the game's own means a marked item is drawn by exactly the settings the hovered
        // one is, rather than something merely blue.
        var profile = OutlineProfile();
        if (profile != null) made.ProfileLoad(profile);

        made.Type = OutlineType.Object;
        made.outlineColor = OutlineColor();
        made.SetTarget(item.transform);
        made.SetHighlighted(true);

        _outlines[key] = new MarkOutline(made, true);
        return true;
    }

    /// <summary>The game's outline manager, re-sought while it is missing rather than once.</summary>
    private static UnityOutlineFX OutlineFX()
    {
        if (_outlineFX != null) return _outlineFX;

        // Looking once would permanently fail whenever the first mark happens before the
        // level's camera exists, so retry - throttled, since this runs from a per-frame path.
        if (Time.unscaledTime < _nextFXSearch) return null;
        _nextFXSearch = Time.unscaledTime + 2f;

        try
        {
            var found = UnityEngine.Object.FindObjectsByType<UnityOutlineFX>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (found != null && found.Length > 0) _outlineFX = found[0];
        }
        catch { }

        if (_outlineFX == null)
            LogOnce("MultiPick: no UnityOutlineFX found - marked items get a default-looking border.");

        return _outlineFX;
    }

    private static HighlightPlus.HighlightProfile OutlineProfile()
    {
        try { return OutlineFX()?.profile; } catch { return null; }
    }

    /// <summary>The colour the game outlines a hovered item with.</summary>
    private static Color OutlineColor()
    {
        try
        {
            var data = OutlineFX()?.outlineData;
            if (data != null) return data.GetOutlineColor(OutlineType.Object);
        }
        catch { }
        return FallbackOutline;
    }

    private static void ClearOutline(ItemIdentifier item)
    {
        IntPtr key;
        try { key = item.Pointer; } catch { return; }

        if (!_outlines.TryGetValue(key, out var mark)) return;
        _outlines.Remove(key);

        try
        {
            var effect = mark.Effect;
            if (effect == null) return;

            effect.SetHighlighted(false);
            if (mark.Created) UnityEngine.Object.Destroy(effect);
        }
        catch { /* the item may already be gone */ }
    }

    /// <summary>
    /// Marks an item, preferring the game's border and falling back to a tint.
    ///
    /// A MaterialPropertyBlock is the fallback because it is self-contained and always
    /// draws: it overrides per-renderer without touching the shared material, so nothing
    /// else using that material changes colour, and clearing the block restores the
    /// original exactly - unlike assigning Renderer.material, which clones one per item and
    /// leaks them.
    /// </summary>
    private static void SetHighlight(CharacterItemInteract interact, ItemIdentifier item)
    {
        if (item == null) return;

        if (_outlineUsable && Plugin.MultiPickOutline.Value)
        {
            try
            {
                if (TryOutline(item)) return;
            }
            catch (Exception e)
            {
                _outlineUsable = false;
                Plugin.Logger.LogWarning(
                    $"MultiPick: could not build the game's border ({e.Message}) - using the tint instead.");
            }
        }

        SetTint(item);
    }

    private static void SetTint(ItemIdentifier item)
    {
        try
        {
            var renderers = item.GetComponentsInChildren<Renderer>(false);
            if (renderers == null) return;

            foreach (var rend in renderers)
            {
                if (rend == null) continue;

                var block = new MaterialPropertyBlock();
                rend.GetPropertyBlock(block);

                // Cover URP and built-in names; setting one the shader lacks is harmless.
                block.SetColor("_BaseColor", HighlightTint);
                block.SetColor("_Color", HighlightTint);

                rend.SetPropertyBlock(block);
            }

            _tinted.Add(item.Pointer);
        }
        catch (Exception e)
        {
            LogOnce($"MultiPick: highlight failed: {e.Message}");
        }
    }

    /// <summary>What SetHighlight was using last frame, so a mid-game switch can undo itself.</summary>
    private static bool _lastUsedOutline = true;

    /// <summary>Keeps every marked item lit, against the game clearing them.</summary>
    private static void RefreshHighlights(CharacterItemInteract interact)
    {
        if (Marked.Count == 0) return;

        // Flipping the setting only changes what gets applied next, so without this the
        // previous mechanism stays on the already-marked items and both show at once.
        bool wantOutline = _outlineUsable && Plugin.MultiPickOutline.Value;
        if (wantOutline != _lastUsedOutline)
        {
            _lastUsedOutline = wantOutline;
            foreach (var item in Marked) ClearHighlight(item);
        }

        foreach (var item in Marked)
        {
            if (item == null) continue;
            SetHighlight(interact, item);
        }
    }

    private static void ClearHighlight(ItemIdentifier item)
    {
        if (item == null) return;

        ClearOutline(item);
        ClearTint(item);
    }

    /// <summary>
    /// Undoes a tint, and only where one was applied.
    ///
    /// Clearing unconditionally would wipe any property block the game set on an item we
    /// only ever outlined - harmless while the tint was the sole mechanism, not once both
    /// exist.
    /// </summary>
    private static void ClearTint(ItemIdentifier item)
    {
        try
        {
            if (!_tinted.Remove(item.Pointer)) return;

            var renderers = item.GetComponentsInChildren<Renderer>(false);
            if (renderers == null) return;

            foreach (var rend in renderers)
            {
                if (rend == null) continue;
                rend.SetPropertyBlock(null);
            }
        }
        catch { /* the item may already be gone */ }
    }

    private static void ClearAll()
    {
        foreach (var item in Marked) ClearHighlight(item);
        Marked.Clear();

        // Anything still listed belongs to an item that went away without passing through
        // ClearHighlight - picked up, served, or unloaded with the level.
        PruneDeadOutlines();
    }

    private static void PruneDeadOutlines()
    {
        if (_outlines.Count == 0) return;

        List<IntPtr> dead = null;
        foreach (var pair in _outlines)
            if (pair.Value.Effect == null) (dead ??= new List<IntPtr>()).Add(pair.Key);

        if (dead == null) return;
        foreach (var key in dead) _outlines.Remove(key);
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>
    /// The item being pointed at in the world, never one already in hand.
    ///
    /// While carrying things, interactingItem can report a held item. Taking that as the
    /// target centred the search on the player's hands instead of the pile being looked
    /// at, so nothing within the radius matched and marking silently did nothing.
    /// </summary>
    private static ItemIdentifier ResolveTarget(CharacterItemInteract interact)
    {
        foreach (var candidate in SafeCandidates(interact))
        {
            if (candidate == null) continue;
            if (IsHeld(interact, candidate)) continue;
            if (IsSpokenFor(candidate)) continue;

            LogOnce($"MultiPick: target resolved to '{ItemKey(candidate)}'.");
            return candidate;
        }

        // Nothing usable from the interaction fields. While carrying, they report the
        // items in hand rather than what is under the crosshair - and since those are
        // skipped, no target resolved and scrolling did nothing at all. Fall back to the
        // game's own aiming ray, which reports what is actually being looked at.
        var aimed = FromInteractionRay(interact);
        if (aimed != null)
        {
            if (IsSpokenFor(aimed)) return null;

            LogOnce($"MultiPick: target from aim ray -> '{ItemKey(aimed)}'.");
            return aimed;
        }

        return null;
    }

    /// <summary>
    /// Whether an item belongs to something rather than lying about to be picked up.
    ///
    /// A service counter's plate is the case that brought this in: it looks like any other
    /// plate to a search of the scene, so marking would happily take the dish a counter is
    /// serving - and the plates parked underneath a stacked one, which are not even visible.
    /// The game hands those out through the counter's own take-a-serving interaction, and
    /// nothing here should be a second way round it.
    /// </summary>
    private static bool IsSpokenFor(ItemIdentifier item)
    {
        try
        {
            if (!CounterTopUp.IsOnAServiceCounter(item)) return false;

            Status = "món đang ở quầy phục vụ - lấy suất từ quầy chứ không đánh dấu được";
            LogOnce($"MultiPick: '{ItemKey(item)}' is on a service counter - the plate or " +
                    "something on it - so it was left alone. A counter hands its dish out " +
                    "through its own take-a-serving action.");
            return true;
        }
        catch { return false; }
    }

    private static int _rayFailures;
    private static int _rayHits;
    private static Camera _cam;

    /// <summary>The player's view camera, cached once found.</summary>
    private static Camera AimCamera(CharacterItemInteract interact)
    {
        if (_cam != null) return _cam;

        try { _cam = Camera.main; } catch { }

        if (_cam == null)
        {
            // Fall back to a camera on the player, in case main is not tagged.
            try { _cam = interact.transform.root.GetComponentInChildren<Camera>(false); } catch { }
        }

        return _cam;
    }

    /// <summary>
    /// What the player is looking at, from our own camera ray.
    ///
    /// CharacterItemInteract.SendInteractionRaycast was the obvious choice, but it threw a
    /// NullReferenceException on every one of 500 calls - it depends on internal state and
    /// cannot be driven from outside. Casting the ray ourselves avoids that entirely, and
    /// uses the player's own reach so it agrees with what the game will let you pick up.
    /// </summary>
    private static ItemIdentifier FromInteractionRay(CharacterItemInteract interact)
    {
        try
        {
            var cam = AimCamera(interact);
            if (cam == null) return null;

            float reach = 3f;
            try { reach = Mathf.Max(0.5f, interact.itemInteractDistance); } catch { }

            if (!Physics.Raycast(cam.transform.position, cam.transform.forward,
                                 out RaycastHit hit, reach, ~0, QueryTriggerInteraction.Ignore))
                return null;

            var col = hit.collider;
            if (col == null) return null;

            for (var t = col.transform; t != null; t = t.parent)
            {
                ItemIdentifier id = null;
                try { id = t.GetComponent<ItemIdentifier>(); } catch { }

                if (id != null && !IsHeld(interact, id))
                {
                    _rayHits++;
                    if (_rayHits == 1)
                        Plugin.Logger.LogInfo("MultiPick: aim ray is working as the target fallback.");
                    return id;
                }
            }
        }
        catch (Exception e)
        {
            _rayFailures++;
            if (_rayFailures == 1 || _rayFailures == 50 || _rayFailures == 500)
                Plugin.Logger.LogWarning(
                    $"MultiPick: aim ray failed {_rayFailures}x ({e.Message}) - " +
                    $"hits so far {_rayHits}.");
        }
        return null;
    }

    private static List<ItemIdentifier> SafeCandidates(CharacterItemInteract interact)
    {
        var list = new List<ItemIdentifier>(2);
        try { list.Add(interact.interactingItem); } catch { }
        try { list.Add(interact.LastInteractingItem); } catch { }
        return list;
    }

    /// <summary>What counts as "the same item".</summary>
    private static string ItemKey(ItemIdentifier item)
    {
        if (item == null) return null;

        try
        {
            var name = item.GetItemName();
            if (!string.IsNullOrEmpty(name)) return name;
        }
        catch { }

        try { return CleanName(item.name); } catch { return null; }
    }

    private static string CleanName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        int idx = name.IndexOf("(Clone)", StringComparison.Ordinal);
        if (idx >= 0) name = name.Substring(0, idx);
        return name.Trim();
    }

    /// <summary>
    /// Either Shift counts. Binding only the left one would quietly fail for anyone who
    /// reaches for the right, and there is no reason to tell them apart here.
    /// </summary>
    private static bool IsShiftHeld()
    {
        try
        {
            var kb = Keyboard.current;
            if (kb == null) return false;

            return kb[Key.LeftShift].isPressed || kb[Key.RightShift].isPressed;
        }
        catch { return false; }
    }

    private static float ReadScroll()
    {
        try
        {
            var mouse = Mouse.current;
            return mouse == null ? 0f : mouse.scroll.ReadValue().y;
        }
        catch { return 0f; }
    }

    private static void LogOnce(string message)
    {
        if (Logged.Add(message)) Plugin.Logger.LogInfo(message);
    }
}
