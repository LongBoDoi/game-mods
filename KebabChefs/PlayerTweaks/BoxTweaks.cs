using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PlayerTweaks;

/// <summary>
/// Right-click a shopping box to put the held item back into it.
///
/// There is no dormant put-back path to unlock: the InventoryPlacingEvent on these boxes
/// is the ordinary "E to open" interaction, and its PlaceItem needs state the input
/// pipeline builds up beforehand. Earlier attempts to patch prerequisites or invoke that
/// method cold achieved nothing (and one crashed the game).
///
/// So instead of reconstructing that pipeline, we perform the state change directly with
/// the game's own networked item RPCs:
///   * ItemManager.AddIngredientServerRpc  - add the ingredient to the box's stack
///   * ItemManager.DestroyItemServerRpc    - remove the item from the player's hand
/// Both are ServerRpcs, so the server stays authoritative and co-op clients agree.
/// </summary>
public static class BoxTweaks
{
    private static readonly HashSet<string> Logged = new();
    private static float _scanTimer;
    private static ItemManager _itemManager;

    public static string Status = "đã tắt";

    /// <summary>
    /// Stops a shopping box vanishing the moment its last item is taken.
    ///
    /// The box removes itself from ItemInventory.OnIngredientDecreased once the stack
    /// empties, which makes put-back useless in the one case you most want it. The prefix
    /// only intervenes on that final decrement - while items remain, the method runs
    /// normally so pile visuals and counts keep updating.
    ///
    /// The patch is always installed; BoxKeepEmpty is read inside the prefix rather than
    /// here, so the option can be turned on and off in game without a restart.
    ///
    /// OnIngredientDecreased is declared on ItemInventory and is not virtual, so unlike
    /// the CheckPrerequisites attempt that crashed the game, __instance here can only ever
    /// be the type we ask for. The declaring-type guard below enforces that.
    /// </summary>
    public static void Install(BepInEx.Logging.ManualLogSource log)
    {
        try
        {
            var target = HarmonyLib.AccessTools.Method(
                typeof(ItemInventory), nameof(ItemInventory.OnIngredientDecreased));

            if (target == null)
            {
                log.LogWarning("Box: could not resolve ItemInventory.OnIngredientDecreased.");
                return;
            }

            if (target.DeclaringType != typeof(ItemInventory))
            {
                log.LogWarning($"Box: OnIngredientDecreased resolves to {target.DeclaringType?.Name}, " +
                               "not ItemInventory - refusing to patch a shared method.");
                return;
            }

            var harmony = new HarmonyLib.Harmony(Plugin.Guid + ".box");

            harmony.Patch(
                target,
                prefix: new HarmonyLib.HarmonyMethod(
                    HarmonyLib.AccessTools.Method(typeof(BoxTweaks), nameof(KeepEmptyBox))));

            // Skipping OnIngredientDecreased is not enough on its own. The log from a real
            // session shows "kept ... alive after its last item was taken" firing and the box
            // still vanishing, so whatever removes it is a separate call - and ItemManager has
            // four ways in. All are guarded, because which one runs depends on whether this
            // machine is the host, and only one line of the log tells us which.
            int guarded = 0;
            foreach (var name in new[]
                     {
                         nameof(ItemManager.DestroyItem),
                         nameof(ItemManager.DestroyItemOnServer),
                         nameof(ItemManager.DestroyItemServerRpc),
                         nameof(ItemManager.DestroyItemClientRpc),
                     })
            {
                try
                {
                    var destroy = HarmonyLib.AccessTools.Method(typeof(ItemManager), name);
                    if (destroy == null) continue;

                    harmony.Patch(destroy, prefix: new HarmonyLib.HarmonyMethod(
                        HarmonyLib.AccessTools.Method(typeof(BoxTweaks), nameof(BlockBoxDestroy))));
                    guarded++;
                }
                catch (Exception e)
                {
                    log.LogWarning($"Box: could not guard ItemManager.{name}: {e.Message}");
                }
            }

            // Every way the game has of taking something out of a stack, watched so the box is
            // marked *before* it can be destroyed. See WatchDecrease for why the mark cannot
            // wait for OnIngredientDecreased on a co-op client.
            int watched = 0;
            foreach (var name in new[]
                     {
                         nameof(ItemManager.DecreaseIngredient),
                         nameof(ItemManager.DecreaseIngredientServerRpc),
                         nameof(ItemManager.DecreaseIngredientClientRpc),
                         nameof(ItemManager.DecreaseIngredientInternal),
                     })
            {
                try
                {
                    var decrease = HarmonyLib.AccessTools.Method(typeof(ItemManager), name);
                    if (decrease == null) continue;

                    harmony.Patch(decrease, prefix: new HarmonyLib.HarmonyMethod(
                        HarmonyLib.AccessTools.Method(typeof(BoxTweaks), nameof(WatchDecrease))));
                    watched++;
                }
                catch (Exception e)
                {
                    log.LogWarning($"Box: could not watch ItemManager.{name}: {e.Message}");
                }
            }

            // And the game's own "this is the last one" flag, which is the earliest and most
            // direct notice that a box is about to empty on the machine doing the taking.
            bool lastItemWatched = false;
            try
            {
                var flag = HarmonyLib.AccessTools.PropertyGetter(
                    typeof(InventoryPlacingEvent), nameof(InventoryPlacingEvent.IsPlacingLastItem));

                if (flag != null)
                {
                    harmony.Patch(flag, postfix: new HarmonyLib.HarmonyMethod(
                        HarmonyLib.AccessTools.Method(typeof(BoxTweaks), nameof(WatchLastItem))));
                    lastItemWatched = true;
                }
            }
            catch (Exception e)
            {
                log.LogWarning($"Box: could not watch IsPlacingLastItem: {e.Message}");
            }

            log.LogInfo($"Box: patched ItemInventory.OnIngredientDecreased, {guarded} of 4 " +
                        $"ItemManager destroy paths and {watched} of 4 decrease paths" +
                        (lastItemWatched ? " plus the last-item flag" : "") +
                        " (empty boxes will persist).");
        }
        catch (Exception e)
        {
            log.LogWarning($"Box: install failed: {e.Message}");
        }
    }

    /// <summary>Returns false to skip the original, which is what would remove the box.</summary>
    private static bool KeepEmptyBox(ItemInventory __instance)
    {
        if (!Plugin.BoxKeepEmpty.Value || __instance == null) return true;

        // Egg trays, tofu packs and the like are ItemInventory too, but they are meant
        // to disappear when emptied. Only the shopping boxes should linger.
        if (!IsShoppingBox(__instance)) return true;

        try
        {
            var stack = __instance.inventoryStack;
            if (stack == null) return true;

            var list = stack.Ingredients;
            // Anything left in the box means this is an ordinary decrement: let it run.
            if (list == null || list.Count > 0) return true;
        }
        catch
        {
            return true;   // never let our guard turn into a new failure mode
        }

        Protect(__instance);
        LogOnce($"Box: kept '{__instance.name}' alive after its last item was taken.");
        return false;
    }

    // ------------------------------------------------ blocking the actual destroy

    /// <summary>
    /// How long after a box empties its destruction is refused.
    ///
    /// Deliberately a window rather than a blanket rule for empty boxes. An empty box you
    /// carry to the bin yourself is destroyed through the same calls, and refusing that
    /// would leave boxes you cannot get rid of - a worse bug than the one being fixed. The
    /// automatic removal happens immediately on the last item leaving, so a few seconds
    /// covers it with room to spare.
    /// </summary>
    private const float KeepAliveSeconds = 5f;

    /// <summary>Boxes emptied recently, by item index, with when it happened.</summary>
    private static readonly Dictionary<int, float> _protectedBoxes = new();

    private static void Protect(ItemInventory box)
    {
        try { _protectedBoxes[box.itemIndex] = Time.unscaledTime; }
        catch { }
    }

    /// <summary>
    /// Marks a shopping box that is one ingredient away from empty, before the decrement runs.
    ///
    /// This is what makes the option hold on a co-op client, and the reason it did not before.
    /// The mark used to be set from OnIngredientDecreased, which is a *reaction* to the stack
    /// changing. On the machine doing the taking that reaction lands first and the guard is
    /// armed in time; on the other machine the decrement and the destruction arrive as two
    /// separate messages, and nothing says they are applied in that order. When the destroy
    /// won the race the box was not marked yet, the guard waved it through, and the box
    /// disappeared for one player and not the other - "sometimes", from the seat it happened in.
    ///
    /// Watching the decrement itself removes the race rather than narrowing it: every path that
    /// can empty a box runs through one of these four, and the mark is set before any of them
    /// has done anything.
    /// </summary>
    private static void WatchDecrease(ItemManager __instance, int gID)
    {
        if (!Plugin.BoxKeepEmpty.Value || __instance == null) return;

        try
        {
            var item = __instance.GetItemByGameID(gID);
            if (item == null) return;

            var inv = item.TryCast<ItemInventory>();
            if (inv == null || !IsShoppingBox(inv)) return;

            // More than one kind of thing left means this decrement cannot empty the box.
            // One entry might still hold several of that thing, so marking is conservative -
            // and a mark that turns out to be unnecessary only holds a bin for a few seconds.
            if (IngredientCount(inv) > 1) return;

            Protect(inv);
        }
        catch { }
    }

    /// <summary>
    /// The game's own "the item being taken is the last one" flag, used as a mark.
    ///
    /// A postfix that reads the answer and changes nothing: the flag is the earliest notice
    /// there is, and it is the game's judgement rather than ours about what "last" means.
    /// </summary>
    private static void WatchLastItem(InventoryPlacingEvent __instance, bool __result)
    {
        if (!__result || !Plugin.BoxKeepEmpty.Value || __instance == null) return;

        try
        {
            var inv = __instance.inventory;
            if (inv == null || !IsShoppingBox(inv)) return;

            Protect(inv);
        }
        catch { }
    }

    /// <summary>How many kinds of thing are left in a box; -1 when it cannot be read.</summary>
    private static int IngredientCount(ItemInventory inv)
    {
        try
        {
            var stack = inv.inventoryStack;
            if (stack == null) return -1;

            var list = stack.Ingredients;
            return list == null ? -1 : list.Count;
        }
        catch { return -1; }
    }

    /// <summary>Whether a player has this box in their hands right now.</summary>
    private static bool IsHeld(ItemInventory inv)
    {
        try { return inv.isHanded; }
        catch { return false; }
    }

    /// <summary>
    /// Refuses to destroy a box that has just been emptied.
    ///
    /// Every ItemManager destroy entry point runs through here, so the guard holds whether
    /// the call originates locally, on the host, or arrives as an RPC. The item is looked up
    /// rather than trusted from the index alone, and anything that is not a shopping box we
    /// just kept alive is passed straight through.
    /// </summary>
    private static bool BlockBoxDestroy(ItemManager __instance, int itemIndex)
    {
        if (!Plugin.BoxKeepEmpty.Value) return true;

        try
        {
            bool marked = _protectedBoxes.TryGetValue(itemIndex, out float at);

            if (marked && Time.unscaledTime - at > KeepAliveSeconds)
            {
                _protectedBoxes.Remove(itemIndex);
                marked = false;   // window passed: this is you binning it, not the auto-tidy
            }

            var item = __instance == null ? null : __instance.GetItemByIndex(itemIndex);
            var inv = item == null ? null : item.TryCast<ItemInventory>();
            if (inv == null || !IsShoppingBox(inv)) return true;

            if (!marked)
            {
                // A box nobody is holding, with nothing left in it, is the auto-tidy however
                // it got here - so the guard no longer needs to have seen it empty to save it.
                // The two exceptions stay honest: something still inside means this is not the
                // tidy-up at all, and a box in a player's hands is one being carried to the
                // bin, which has to keep working or empty boxes become undestroyable.
                if (IngredientCount(inv) != 0 || IsHeld(inv))
                {
                    LogOnce($"Box: let '{inv.name}' (index {itemIndex}) be destroyed - " +
                            $"{IngredientCount(inv)} left, held={IsHeld(inv)}.");
                    return true;
                }

                // Marked as it is refused, so the keep-alive window starts here too and the
                // box can still be binned. Back-dated to leave only a second of it: this
                // branch fires on a destroy we did not see coming, which the auto-tidy sends
                // instantly, so a second is all the cover it needs - and a player who really
                // is throwing an empty box away gets to try again straight away rather than
                // standing at the bin for five seconds.
                try { _protectedBoxes[itemIndex] = Time.unscaledTime - (KeepAliveSeconds - 1f); }
                catch { }
                LogOnce($"Box: refused to destroy empty box '{inv.name}' (index {itemIndex}) " +
                        "that nothing had marked - the destroy arrived before the emptying did.");
                return false;
            }

            LogOnce($"Box: refused to destroy emptied box '{inv.name}' (index {itemIndex}).");
            return false;
        }
        catch
        {
            return true;          // never let our guard turn into a new failure mode
        }
    }

    public static void Tick(CharacterItemInteract interact, bool overlayOpen, ulong localClientId)
    {
        if (!Plugin.BoxPutBack.Value)
        {
            Status = "đã tắt";
            return;
        }

        Status = "chuột phải vào thùng để bỏ đồ vào lại";

        if (!overlayOpen && interact != null)
        {
            TickPutBack(interact);
            TickTakeOut(interact);
            TickDestroyHold(interact, localClientId);
        }

        _scanTimer -= Time.unscaledDeltaTime;
        if (_scanTimer > 0f) return;
        _scanTimer = 5f;
        _itemManager = null;   // re-resolve periodically in case of a scene change
    }

    // ------------------------------------------------- hold to destroy a stack

    /// <summary>How long the key must be held before the stack is destroyed.</summary>
    private const float DestroyHoldSeconds = 1.0f;

    /// <summary>0..1 while the destroy key is held over a valid item; drives the bar.</summary>
    public static float HoldProgress { get; private set; }

    /// <summary>What is about to be destroyed, shown above the bar.</summary>
    public static string HoldLabel { get; private set; }

    private static float _holdTime;
    private static string _holdKeyName;
    private static bool _armed = true;

    /// <summary>Why the last resolution attempt gave up; surfaced in the log and status.</summary>
    private static string _bailReason = "not attempted";

    /// <summary>
    /// Holding the configured key over a hovered slot wipes that whole item type from
    /// the box. A hold rather than a tap, because this is destructive and unrecoverable.
    /// </summary>
    private static void TickDestroyHold(CharacterItemInteract interact, ulong localClientId)
    {
        if (!IsDestroyKeyHeld())
        {
            _armed = true;              // released, so the next hold may fire
            _holdTime = 0f;
            HoldProgress = 0f;
            HoldLabel = null;
            return;
        }

        // One destroy per press. Without this the timer simply restarted and fired again
        // for as long as the key was down.
        if (!_armed) { HoldProgress = 0f; HoldLabel = null; return; }

        if (!TryResolveHoveredStack(interact, out var stack, out var ingredient, out string key, out float amount))
        {
            _holdTime = 0f;
            HoldProgress = 0f;
            HoldLabel = null;
            Status = $"không có gì để hủy ({_bailReason})";
            LogOnce($"Box destroy: gave up - {_bailReason}");
            return;
        }

        // Our own timer, not the game's bar. CharacterItemInteract.ProgressBarIsMaxAlt
        // never completed - 120 frames of holding, three separate attempts - so it needs
        // game input state we cannot supply from outside.
        _holdTime += Time.unscaledDeltaTime;
        HoldProgress = Mathf.Clamp01(_holdTime / DestroyHoldSeconds);
        HoldLabel = $"Đang hủy {key} x{amount:0.##}";
        Status = $"giữ {Plugin.BoxDestroyKey.Value} để hủy {key}";

        if (_holdTime < DestroyHoldSeconds) return;

        // Clear before acting so a failure cannot leave the bar stuck full, and disarm
        // so holding the key does not roll straight into the next stack.
        _holdTime = 0f;
        HoldProgress = 0f;
        HoldLabel = null;
        _armed = false;

        try
        {
            // Prefer the networked route so co-op players see the same box. The earlier
            // conclusion that DecreaseIngredientServerRpc "does nothing" was drawn while
            // this code was targeting the wrong stack entirely, so it was never a fair
            // test. RemoveIngredient is a direct local call and desyncs in multiplayer.
            // Fire the networked call first, then remove locally without waiting.
            //
            // Waiting to see whether the server route landed added a visible pause before
            // the stack vanished, and testing showed it never lands anyway. Both paths aim
            // at the same end state - the entry gone - so applying both is idempotent:
            // removing an already-removed entry does nothing. That keeps the removal
            // instant while still replicating if the server route ever does work.
            var mgr = ResolveItemManager();
            if (mgr != null)
                mgr.DecreaseIngredientServerRpc(stack.gameID, key, amount, localClientId);

            stack.RemoveIngredient(ingredient, true);

            Status = $"đã hủy {key} x{amount:0.##}";
            Plugin.Logger.LogInfo(
                $"Box: destroyed stack '{key}' x{amount} from stackGID={stack.gameID} " +
                $"(remaining={ReadAmount(stack, key):0.##}).");
        }
        catch (Exception e)
        {
            Status = "hủy thất bại - xem log";
            LogOnce($"Box: destroy failed: {e.Message}");
        }
    }

    /// <summary>
    /// Maps the highlighted slot onto a real stack entry. The visible slots are drawn
    /// from the stack in order, optionally scrolled by curIndex, so try the scrolled
    /// index first and the raw index second.
    /// </summary>
    private static bool TryStackEntryAt(IngredientStack stack, ItemInventoryNavigator nav, int slotIndex,
                                        out Ingredient ingredient, out string key, out float amount)
    {
        ingredient = null;
        key = null;
        amount = 0f;

        try
        {
            var list = stack.Ingredients;
            if (list == null || list.Count == 0) return false;

            int scroll = 0;
            try { scroll = nav.curIndex; } catch { }

            foreach (int candidate in new[] { scroll + slotIndex, slotIndex })
            {
                if (candidate < 0 || candidate >= list.Count) continue;

                var d = list[candidate];
                var ing = d?.Ingredient;
                if (ing == null) continue;

                ingredient = ing;
                key = ing.ingredientKey;
                amount = d.Amount;

                if (!string.IsNullOrEmpty(key) && amount > 0f)
                {
                    LogOnce($"Box destroy: mapped slot {slotIndex} (scroll={scroll}) to stack entry " +
                            $"{candidate} = '{key}' x{amount:0.##}.");
                    return true;
                }
            }
        }
        catch (Exception e)
        {
            LogOnce($"Box destroy: stack-index mapping failed: {e.Message}");
        }
        return false;
    }

    /// <summary>One-off dump of the slot pool against the real stack contents.</summary>
    private static void DumpSlotMapping(ItemInventoryNavigator nav,
                                        Il2CppSystem.Collections.Generic.List<ItemInventorySlot> slots,
                                        int activeSlot, IngredientStack stack)
    {
        try
        {
            var slotDesc = new List<string>();
            for (int i = 0; i < slots.Count && i < 20; i++)
            {
                var s = slots[i];
                if (s == null) { slotDesc.Add($"{i}:null"); continue; }

                bool on = false;
                try { on = s.gameObject.activeInHierarchy; } catch { }
                string k = "?";
                try { k = s.Ingredient == null ? "-" : s.Ingredient.ingredientKey; } catch { }
                slotDesc.Add($"{i}:{k}{(on ? "" : "(off)")}");
            }

            var stackDesc = new List<string>();
            var list = stack.Ingredients;
            if (list != null)
                for (int i = 0; i < list.Count && i < 20; i++)
                {
                    var d = list[i];
                    stackDesc.Add($"{i}:{(d?.Ingredient == null ? "?" : d.Ingredient.ingredientKey)}x{d.Amount:0.##}");
                }

            int scroll = 0;
            try { scroll = nav.curIndex; } catch { }

            LogOnce($"Box slot map: activeSlot={activeSlot} curIndex={scroll} " +
                    $"slots=[{string.Join(" ", slotDesc)}] stack=[{string.Join(" ", stackDesc)}]");
        }
        catch (Exception e)
        {
            LogOnce($"Box slot map failed: {e.Message}");
        }
    }

    /// <summary>How much of a key the stack holds now; 0 when the entry is gone.</summary>
    private static float ReadAmount(IngredientStack stack, string key)
    {
        try
        {
            var list = stack.Ingredients;
            if (list == null) return 0f;

            for (int i = 0; i < list.Count; i++)
            {
                var d = list[i];
                var k = d?.Ingredient?.ingredientKey;
                if (!string.IsNullOrEmpty(k) && string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                    return d.Amount;
            }
        }
        catch { }
        return 0f;
    }

    private static bool IsDestroyKeyHeld()
    {
        try
        {
            var kb = Keyboard.current;
            if (kb == null) return false;

            string name = Plugin.BoxDestroyKey.Value;
            if (string.IsNullOrWhiteSpace(name)) return false;

            if (!Enum.TryParse<Key>(name, true, out var key) || key == Key.None)
            {
                // Only complain once per bad value, not every frame.
                if (_holdKeyName != name)
                {
                    _holdKeyName = name;
                    LogOnce($"Box: '{name}' is not a valid InputSystem key name.");
                }
                return false;
            }

            _holdKeyName = name;
            return kb[key].isPressed;
        }
        catch { return false; }
    }

    /// <summary>
    /// The item type currently highlighted in an open box, and how much of it there is.
    /// ItemInventoryNavigator tracks the highlighted slot as an index into its own list.
    /// </summary>
    private static bool TryResolveHoveredStack(CharacterItemInteract interact,
                                               out IngredientStack stack, out Ingredient ingredient,
                                               out string key, out float amount)
    {
        stack = null;
        ingredient = null;
        key = null;
        amount = 0f;

        try
        {
            var navs = UnityEngine.Object.FindObjectsByType<ItemInventoryNavigator>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            _bailReason = $"navigators={(navs == null ? "null" : navs.Length.ToString())}";
            if (navs == null || navs.Length == 0) return false;

            foreach (var nav in navs)
            {
                if (nav == null) { _bailReason = "navigator null"; continue; }
                if (!nav.isActiveAndEnabled) { _bailReason = "navigator inactive"; continue; }

                var slots = nav.inventorySlots;
                if (slots == null || slots.Count == 0) { _bailReason = "no slots"; continue; }

                // inventorySlots is a fixed pool of 15; only the first few are shown for
                // a box holding 3 items. Work in terms of the visible ones so indexes
                // line up with the stack instead of with dormant pool entries.
                var visible = new List<ItemInventorySlot>();
                for (int i = 0; i < slots.Count; i++)
                {
                    var s = slots[i];
                    if (s == null) continue;
                    bool on = false;
                    try { on = s.gameObject.activeInHierarchy; } catch { }
                    if (on) visible.Add(s);
                }
                if (visible.Count == 0) { _bailReason = "no visible slots"; continue; }

                // The highlighted slot switches its selectedGO on, which tracks the actual
                // highlight. activeSlot pointed at pool entry 3 while only 3 items existed.
                int idx = -1;
                for (int i = 0; i < visible.Count; i++)
                {
                    var sel = visible[i].selectedGO;
                    bool selOn = false;
                    try { selOn = sel != null && sel.activeSelf; } catch { }
                    if (selOn) { idx = i; break; }
                }

                if (idx < 0)
                {
                    int a = nav.activeSlot;
                    if (a >= 0 && a < visible.Count) idx = a;
                }
                if (idx < 0) { _bailReason = $"no highlighted slot (visible={visible.Count})"; continue; }

                var slot = visible[idx];
                var ing = slot.Ingredient;   // may be null on a recycled slot; handled below
                key = ing == null ? null : ing.ingredientKey;

                // The box you are aiming at is the authority on which stack to edit.
                // A global "first ItemInventory holding this key" search reported success
                // against a different box; nav.currentInventory then came back null.
                var aimed = ResolveTargetInventory(interact, null);
                if (aimed == null)
                {
                    _bailReason = "not aiming at a box";
                    continue;
                }

                // Destroying a whole stack is for the cardboard shopping boxes only.
                // Wiping an egg tray or tofu pack this way is not what the option is for.
                if (!IsShoppingBox(aimed))
                {
                    _bailReason = $"'{aimed.name}' is not a shopping box";
                    continue;
                }

                try { stack = aimed.inventoryStack; } catch { stack = null; }
                if (stack == null)
                {
                    _bailReason = $"no stack on '{aimed.name}'";
                    continue;
                }

                DumpSlotMapping(nav, slots, idx, stack);

                // Trust the slot's own Ingredient only when the stack agrees. A recycled
                // slot can name an item this box never held, or hold nothing at all - in
                // both cases the visible position maps onto the stack directly.
                amount = string.IsNullOrEmpty(key) ? 0f : ReadAmount(stack, key);

                if (amount <= 0f)
                {
                    if (!TryStackEntryAt(stack, nav, idx, out ingredient, out key, out amount))
                    {
                        _bailReason = $"visible slot {idx} maps to nothing in stack {stack.gameID}";
                        continue;
                    }
                }
                else
                {
                    ingredient = ing;
                }

                return true;
            }
        }
        catch (Exception e)
        {
            LogOnce($"Box: hovered-slot lookup failed: {e.Message}");
        }
        return false;
    }

    private static float _putHoldTime;
    private static float _putNextFire;

    // Items already handed to the server, and not yet gone from our own hand.
    //
    // Both halves of a put-back are ServerRpcs, so on a co-op client the item stays in
    // currentItem for a whole round trip after being sent. Firing blind into that put one
    // item into the box a dozen times over, so the first fix was to wait for the hand to
    // change before firing again - correct, but it pinned the repeat to one item per round
    // trip while the same hold at a shop shelf runs at the full 30 a second. The shelf is
    // fast because it never waits: it fires and lets the server serialise the calls.
    //
    // Waiting is not what keeps the numbers right, though - sending each item exactly once
    // is. So the lock is now a register of what has already gone out, keyed by pointer as
    // well as item index because indices get recycled. A repeat puts back the first item in
    // hand that is not in the register, which empties a full hand at the repeat rate and
    // still cannot send the same item twice.
    private static readonly List<HandRef> _sentBack = new();

    private readonly struct HandRef
    {
        public readonly IntPtr Ptr;
        public readonly int Index;

        public HandRef(IntPtr ptr, int index) { Ptr = ptr; Index = index; }

        public bool Matches(ItemIdentifier item)
        {
            if (item == null) return false;
            try { return item.Pointer == Ptr && item.itemIndex == Index; }
            catch { return false; }
        }
    }

    /// <summary>Scratch list for the current hand contents; reused so a 30Hz repeat does not litter.</summary>
    private static readonly List<ItemIdentifier> _hand = new();

    private static void ClearSentBack() => _sentBack.Clear();

    /// <summary>Everything the player is carrying, the item actually in hand first.</summary>
    private static void CollectHand(CharacterItemInteract interact)
    {
        _hand.Clear();

        try
        {
            var cur = interact.currentItem;
            if (cur != null) _hand.Add(cur);
        }
        catch { }

        try
        {
            var queued = interact.pickedItemsWaitingInQueue;
            if (queued != null)
                for (int i = 0; i < queued.Count; i++)
                {
                    var q = queued[i];
                    if (q != null) _hand.Add(q);
                }
        }
        catch { }
    }

    /// <summary>
    /// Forgets sent items the server has now taken away.
    ///
    /// Without this the register would only grow, and a hold long enough to span a refill
    /// would start skipping items it had never actually sent. An entry is dropped as soon
    /// as its item is no longer in hand, which is exactly the acknowledgement the old code
    /// used to block on - it just no longer stops anything while it waits.
    /// </summary>
    private static void PruneSentBack()
    {
        for (int i = _sentBack.Count - 1; i >= 0; i--)
        {
            bool stillHeld = false;
            for (int j = 0; j < _hand.Count; j++)
                if (_sentBack[i].Matches(_hand[j])) { stillHeld = true; break; }

            if (!stillHeld) _sentBack.RemoveAt(i);
        }
    }

    private static bool AlreadySent(ItemIdentifier item)
    {
        for (int i = 0; i < _sentBack.Count; i++)
            if (_sentBack[i].Matches(item)) return true;
        return false;
    }

    private static void MarkSent(ItemIdentifier item)
    {
        try { _sentBack.Add(new HandRef(item.Pointer, item.itemIndex)); }
        catch { }
    }

    /// <summary>
    /// One click puts one item back; holding keeps going, at the same rate and after the
    /// same delay as holding at a shop shelf.
    /// </summary>
    private static void TickPutBack(CharacterItemInteract interact)
    {
        var mouse = Mouse.current;
        if (mouse == null) return;

        // A fresh press always puts exactly one back, so a quick click is never a burst.
        if (mouse.rightButton.wasPressedThisFrame)
        {
            _putHoldTime = 0f;
            _putNextFire = 0f;
            ClearSentBack();     // a deliberate click is always let through
            TryPutBack(interact);
            return;
        }

        if (!mouse.rightButton.isPressed || !Plugin.BoxHoldRepeat.Value)
        {
            _putHoldTime = 0f;
            ClearSentBack();
            return;
        }

        _putHoldTime += Time.unscaledDeltaTime;
        if (_putHoldTime < StoreRepeater.RepeatDelay) return;

        if (Time.unscaledTime < _putNextFire) return;
        _putNextFire = Time.unscaledTime + 1f / StoreRepeater.RepeatRate;

        TryPutBack(interact);
    }

    private static float _takeHoldTime;
    private static float _takeNextFire;
    private static int _takeLogs;

    // Takes that have been sent and not yet answered.
    //
    // On the host a take applies inside the call, so it can be judged the instant it
    // returns. On a co-op client it cannot: the speciality goes out as an RPC and the item
    // arrives some frames later. Measuring straight afterwards there reads the state from
    // *before* the take, which is what broke this for clients - the box had already
    // decremented locally while the item had not arrived, so `boxLost && !handGained`
    // matched on the very first repeat and latched the safety stop.
    //
    // The first fix was a lock: one take out at a time, nothing sent until it landed. That
    // was correct but slow, because it turned the repeat rate into one item per round trip
    // while the same hold at a shop shelf runs at the full 30 a second. So the lock is now
    // a window instead. Several takes may be in flight at once, bounded by how many free
    // hands the player has - which is not an arbitrary throttle but the real limit: asking
    // for more items than you can hold is exactly what destroys them. Within that bound the
    // repeat runs at shelf speed and the server serialises the calls, and past it the
    // window drains at whatever rate items actually arrive.
    private static int _takeInFlight;
    private static float _takeBoxBase;
    private static int _takeHandBase;
    private static float _takeInFlightSince;

    /// <summary>How long to give the server before the whole window counts as lost.</summary>
    private const float TakeSettleTimeout = 1.5f;

    private static void ClearTakePending() => _takeInFlight = 0;

    /// <summary>
    /// How many more items the player could hold.
    ///
    /// Unknown capacity answers 1 rather than 0, so an unreadable holder array degrades to
    /// the old one-at-a-time behaviour instead of switching the feature off.
    /// </summary>
    private static int FreeHands(CharacterItemInteract interact)
    {
        try
        {
            var holders = interact.itemHolderQueue;
            if (holders == null || holders.Length == 0) return 1;

            return Mathf.Max(0, holders.Length - HeldCount(interact));
        }
        catch { return 1; }
    }

    /// <summary>
    /// Set when a take is seen removing an item from the box without it arriving in hand.
    /// Cleared the moment the key is released, so one bad case stops the current hold
    /// rather than switching the feature off for the rest of the session - which is what
    /// the old session-wide latch did, and why take-out appeared to stop working
    /// permanently after the hands filled up once.
    /// </summary>
    private static bool _takeStopped;

    /// <summary>The last reason a repeat did nothing, so a silent no-op is never silent.</summary>
    private static string _takeBail = "chưa thử";
    private static int _takeBailLogs;

    private static void TakeBail(string reason)
    {
        Status = $"lấy đồ liên tục: {reason}";

        if (_takeBail == reason) return;
        _takeBail = reason;

        if (_takeBailLogs < 40)
        {
            _takeBailLogs++;
            Plugin.Logger.LogInfo($"Box: take-out repeat did nothing - {reason}");
        }
    }

    /// <summary>
    /// Holding the interact key at a box keeps taking items out, mirroring the put-back.
    ///
    /// The first press is the game's own - we only start repeating after the same delay
    /// the shelf uses, so a single tap still takes exactly one.
    /// </summary>
    private static void TickTakeOut(CharacterItemInteract interact)
    {
        // Taking out of a box is the game's interact key - F by default - not a mouse
        // button. Watching left click meant the repeat was tied to an input the box
        // never responds to.
        if (!IsKeyHeld(Plugin.BoxTakeOutKey.Value))
        {
            // Releasing is the reset. A stop only ever lasts for one hold.
            _takeHoldTime = 0f;
            _takeStopped = false;
            ClearTakePending();
            return;
        }

        if (!Plugin.BoxHoldRepeat.Value)
        {
            TakeBail("tùy chọn giữ để lặp đang tắt");
            return;
        }

        if (_takeStopped) return;

        _takeHoldTime += Time.unscaledDeltaTime;
        if (_takeHoldTime < StoreRepeater.RepeatDelay) return;

        if (Time.unscaledTime < _takeNextFire) return;
        _takeNextFire = Time.unscaledTime + 1f / StoreRepeater.RepeatRate;

        TryTakeOut(interact);
    }

    private static void TryTakeOut(CharacterItemInteract interact)
    {
        try
        {
            ItemIdentifier held = null;
            try { held = interact.currentItem; } catch { }

            var inv = ResolveTargetInventory(interact, held);
            if (inv == null) { TakeBail("chưa nhắm vào thùng nào"); return; }
            if (!IsShoppingBox(inv)) { TakeBail($"'{inv.name}' không phải thùng hàng"); return; }

            // Bring the window back in line with what has actually arrived before deciding
            // whether anything more may go out.
            if (!ReconcileTakes(interact, inv)) return;

            // Stop at the carry limit rather than at the damage. Firing with full hands
            // takes the item out of the box and then has nowhere to put it, which destroys
            // it - that is exactly what tripped the safety stop on the 21st repeat. Takes
            // already in flight count against the limit, because each of them is an item
            // that is going to need a hand when it lands.
            if (FreeHands(interact) - _takeInFlight <= 0)
            {
                TakeBail("tay đã đầy");
                return;
            }

            // Until a route is known to work, only one take may be outstanding. Judging a
            // route means asking "did anything change after this one call", and overlapping
            // calls make that unanswerable. The window only opens once the answer is known.
            if (!_takeRoutePinned && _takeInFlight > 0) return;

            ItemInventorySpeciality spec = null;
            try { spec = inv._inventorySpeciality; } catch { }

            if (spec == null)
            {
                TakeBail($"'{inv.name}' không hỗ trợ lấy nhanh");
                LogOnce($"Box: '{inv.name}' has no take speciality - cannot repeat taking out.");
                return;
            }

            // Measure both sides: the box losing an item is not the same as the player
            // gaining one. Checking only the box would hide the worst outcome - items
            // removed from the box and never handed over, i.e. destroyed.
            float boxBefore = TotalAmount(inv);
            int handBefore = HeldCount(interact);

            bool onServer = IsServer(inv);

            string route;
            try { route = FireTake(interact, inv, spec, onServer); }
            catch (Exception e)
            {
                // A route that throws is a route that does not exist on this build. Step
                // past it immediately rather than waiting out the timeout for it.
                route = $"route {_takeRoute} threw: {e.Message}";
                Plugin.Logger.LogWarning($"Box: take-out {route}");
                AdvanceTakeRoute();
                return;
            }

            if (_takeLogs < 20)
            {
                _takeLogs++;
                Plugin.Logger.LogInfo(
                    $"Box: take-out #{_takeLogs} from '{inv.name}' via '{route}' " +
                    $"({(onServer ? "host" : "client")}), box before {boxBefore:0.##}, " +
                    $"hand before {handBefore}, in flight {_takeInFlight}");
            }

            if (onServer)
            {
                // The host applies its own take inside the call, so the result is already
                // on the table and judging it now keeps the feel instant. Nothing is ever
                // in flight here, which is why the host never needed the window.
                JudgeTake(interact, inv, boxBefore, handBefore,
                          TotalAmount(inv), HeldCount(interact));
                return;
            }

            if (_takeInFlight == 0) _takeInFlightSince = Time.unscaledTime;
            _takeInFlight++;
            Status = $"đang lấy đồ từ {inv.name} ({_takeInFlight} đang chờ)";
        }
        catch (Exception e)
        {
            ClearTakePending();
            LogOnce($"Box: take-out failed: {e.Message}");
        }
    }

    // Which client route is being tried, and whether one has been seen working.
    //
    // Guessing this from signatures alone has already been wrong once - BeginSpeciality
    // looked like the networked entry point because the base class holds sendRPC,
    // isCalledByMe and LastCalledPlayerId, and it did nothing. The stubs carry no method
    // bodies, so there is no reading the answer out of the assembly; the honest way to
    // pick is to try each candidate and keep whichever actually delivers an item. Once one
    // works it is pinned for the session.
    private static int _takeRoute;
    private static bool _takeRoutePinned;

    /// <summary>How many client routes <see cref="FireTake"/> knows about.</summary>
    private const int TakeRouteCount = 5;

    /// <summary>
    /// Sets a take going, by the route that suits who is asking.
    ///
    /// The host keeps DoItemSpeciality, which is the local implementation and is what has
    /// always worked there - it is the server, so its own state change is the authoritative
    /// one. A client calling that same method only ever changes its own copy of the box,
    /// which the server then contradicts.
    ///
    /// The client ladder is ordered most-likely-correct first:
    ///   0  CharacterItemInteract.ExecuteSpeciality(spec, item, Done) - the player-side
    ///      driver the input pipeline itself goes through, and the only candidate that
    ///      takes the player, the speciality and the item together
    ///   1  the same call with Begin, in case Done skips a step the game needs
    ///   2  ItemManager.CallRemoteItemSpeciality - the explicit "run this on everyone"
    ///      wrapper over DoItemSpecialityServerRpc
    ///   3  ItemSpeciality.BeginSpeciality - already tried and found wanting, kept only so
    ///      the ladder is exhaustive
    ///   4  the local call, which at least matches host behaviour
    /// </summary>
    private static string FireTake(CharacterItemInteract interact, ItemInventory inv,
                                   ItemInventorySpeciality spec, bool onServer)
    {
        if (onServer)
        {
            spec.DoItemSpeciality();
            return "DoItemSpeciality";
        }

        switch (_takeRoute)
        {
            case 0:
                interact.ExecuteSpeciality(spec, inv, ItemEventState.Done);
                return "ExecuteSpeciality(Done)";

            case 1:
                interact.ExecuteSpeciality(spec, inv, ItemEventState.Begin);
                return "ExecuteSpeciality(Begin)";

            case 2:
            {
                var mgr = ResolveItemManager();
                if (mgr == null) return "CallRemoteItemSpeciality (no ItemManager)";

                // descKey is the only string this speciality carries that could name it.
                // If it is the wrong vocabulary the server simply will not match it, which
                // the settle check reads as "no effect" and steps past.
                string key = "";
                try { key = spec.descKey ?? ""; } catch { }

                mgr.CallRemoteItemSpeciality(inv, key, ItemEventState.Done);
                return $"CallRemoteItemSpeciality('{key}')";
            }

            case 3:
                spec.BeginSpeciality();
                return "BeginSpeciality";

            default:
                spec.DoItemSpeciality();
                return "DoItemSpeciality (local fallback)";
        }
    }

    /// <summary>
    /// Steps to the next client route after one produced nothing, and reports when the
    /// ladder is exhausted rather than looping around it forever.
    /// </summary>
    private static bool AdvanceTakeRoute()
    {
        if (_takeRoutePinned) return false;   // a route already works; this was some other stall

        _takeRoute++;
        if (_takeRoute < TakeRouteCount)
        {
            Plugin.Logger.LogInfo(
                $"Box: take-out route {_takeRoute - 1} produced nothing on this client; " +
                $"trying route {_takeRoute} on the next press.");
            return true;
        }

        _takeRoute = TakeRouteCount - 1;   // stay on the local fallback rather than wrap
        return false;
    }

    /// <summary>
    /// Credits arrived items against the in-flight window, and returns true when sending
    /// may continue.
    ///
    /// Counting rather than matching is deliberate: an ItemSpeciality take does not hand
    /// back any handle on the item it will produce, so there is nothing to pair an arrival
    /// with. What can be counted is how many more items are in hand than when the window
    /// opened, and that is enough - the three outcomes that matter are still distinct.
    /// Items arrive (credit them and keep going), the box drains without them arriving
    /// (destructive, stop the hold), or nothing at all moves for a whole timeout (the route
    /// does not work on this client, step the ladder instead of hammering the server).
    /// </summary>
    private static bool ReconcileTakes(CharacterItemInteract interact, ItemInventory inv)
    {
        float boxNow = TotalAmount(inv);
        int handNow = HeldCount(interact);

        // Nothing outstanding, so now is the moment the baseline is true.
        if (_takeInFlight <= 0)
        {
            _takeInFlight = 0;
            _takeBoxBase = boxNow;
            _takeHandBase = handNow;
            return true;
        }

        // The hand shrank - something was used, dropped or served while takes were out.
        // That answers none of them, so re-baseline and keep waiting rather than reading
        // the drop as a take having failed.
        if (handNow < _takeHandBase)
        {
            _takeHandBase = handNow;
            _takeBoxBase = boxNow;
            _takeInFlightSince = Time.unscaledTime;
            return true;
        }

        int arrived = handNow - _takeHandBase;
        if (arrived > 0)
        {
            _takeInFlight = Mathf.Max(0, _takeInFlight - arrived);
            _takeHandBase = handNow;
            _takeBoxBase = boxNow;
            _takeInFlightSince = Time.unscaledTime;

            // This route delivers. Stop walking the ladder, and let the window open.
            if (!_takeRoutePinned)
            {
                _takeRoutePinned = true;
                Plugin.Logger.LogInfo($"Box: take-out route {_takeRoute} works on this client - pinned.");
            }

            _takeBail = null;
            Status = $"đang lấy đồ từ {inv.name}";
            return true;
        }

        // Nothing has arrived yet. At 30 sends a second that is the normal state for the
        // first few frames of a hold, so it is only worth reacting to once a full timeout
        // has passed with the window still untouched.
        if (Time.unscaledTime - _takeInFlightSince < TakeSettleTimeout)
            return true;

        bool boxLost = boxNow < _takeBoxBase;
        float boxBase = _takeBoxBase;
        int handBase = _takeHandBase;

        _takeInFlight = 0;
        _takeBoxBase = boxNow;
        _takeHandBase = handNow;

        if (boxLost)
        {
            JudgeTake(interact, inv, boxBase, handBase, boxNow, handNow);
            return false;
        }

        // Nothing moved on either side, so this route is not the one. Step to the next and
        // let the player press again; only give up once the ladder is exhausted.
        if (AdvanceTakeRoute())
        {
            Status = $"đang thử cách lấy đồ khác ({_takeRoute + 1}/{TakeRouteCount})";
            return false;
        }

        _takeStopped = true;
        Status = "không lấy được đồ từ xa - xem log (thả phím để thử lại)";
        LogOnce("Box: every take-out route produced no change on this client. The routes " +
                "tried were ExecuteSpeciality(Done/Begin), CallRemoteItemSpeciality and " +
                "BeginSpeciality. What is left is doing it by hand the way the put-back " +
                "does - ItemManager.CreateItemServerRpc for the item then " +
                "DecreaseIngredientServerRpc for the box - which needs the item-spawn " +
                "arguments pinned down first.");
        return false;
    }

    /// <summary>Applies the safety rules to a take whose outcome is now known.</summary>
    private static void JudgeTake(CharacterItemInteract interact, ItemInventory inv,
                                  float boxBefore, int handBefore,
                                  float boxAfter, int handAfter)
    {
        bool boxLost = boxAfter < boxBefore;
        bool handGained = handAfter > handBefore;

        // Stop immediately if items are leaving the box without arriving. Continuing
        // at 30 a second would empty the box into nothing.
        if (boxLost && !handGained)
        {
            // If the hands filled up on this very call, the capacity check was one short
            // rather than the take-out being broken.
            if (HandsFull(interact))
            {
                _takeHoldTime = 0f;
                TakeBail("tay đã đầy");
                return;
            }

            // Stop this hold only. Releasing the key clears it, so a one-off does not
            // cost the player the feature until they restart the game.
            _takeStopped = true;
            Status = "đã dừng lấy đồ liên tục - vật phẩm không tới được tay bạn (thả phím để thử lại)";
            Plugin.Logger.LogWarning(
                "Box: take-out removed an item from the box but nothing arrived in hand. " +
                "Stopped for this hold to avoid destroying items.");
            return;
        }

        _takeBail = null;   // something happened, so the next stall is worth logging again
        Status = handGained ? $"đang lấy đồ từ {inv.name}" : "lấy đồ không có tác dụng";
    }

    /// <summary>
    /// Whether this machine owns the box. ItemInventory reaches NetworkBehaviour through
    /// ItemIdentifier and GameItem, so the flag is the game's own rather than a guess at
    /// who started the session.
    /// </summary>
    private static bool IsServer(ItemInventory inv)
    {
        try { return inv.IsServer; }
        catch { return true; }   // unknown, so behave as it always did
    }

    private static bool IsKeyHeld(string name)
    {
        try
        {
            var kb = Keyboard.current;
            if (kb == null || string.IsNullOrWhiteSpace(name)) return false;

            if (!Enum.TryParse<Key>(name, true, out var key) || key == Key.None) return false;
            return kb[key].isPressed;
        }
        catch { return false; }
    }

    /// <summary>
    /// Whether there is room for one more item.
    ///
    /// The cap is not a number the game exposes - it is the length of itemHolderQueue,
    /// the array of transforms carried items get parented to (the same array the carry
    /// limit option resizes). One item is in hand and the rest are queued, so the queue
    /// being as long as the holder array means every holder is taken.
    /// </summary>
    private static bool HandsFull(CharacterItemInteract interact)
    {
        try
        {
            var holders = interact.itemHolderQueue;
            if (holders == null || holders.Length == 0) return false;   // unknown, so do not block

            // Count what is actually in hand, not just what is queued behind it. Measuring
            // the queue alone was one short - the item currently held is not in that list -
            // so the check passed with every holder already taken, and the take pulled an
            // item out of the box with nowhere to put it.
            return HeldCount(interact) >= holders.Length;
        }
        catch { return false; }
    }

    /// <summary>How many items the player is holding.</summary>
    private static int HeldCount(CharacterItemInteract interact)
    {
        try
        {
            var queued = interact.pickedItemsWaitingInQueue;
            int n = queued == null ? 0 : queued.Count;

            try { if (interact.currentItem != null) n++; } catch { }
            return n;
        }
        catch { return 0; }
    }

    /// <summary>Everything the box holds, so a take can be detected as a drop in total.</summary>
    private static float TotalAmount(ItemInventory inv)
    {
        try
        {
            var list = inv.inventoryStack?.Ingredients;
            if (list == null) return 0f;

            float total = 0f;
            for (int i = 0; i < list.Count; i++)
            {
                var d = list[i];
                if (d != null) total += d.Amount;
            }
            return total;
        }
        catch { return 0f; }
    }

    private static void TryPutBack(CharacterItemInteract interact)
    {
        try
        {
            CollectHand(interact);
            PruneSentBack();

            if (_hand.Count == 0) { ClearSentBack(); Status = "tay không cầm gì"; return; }

            // The first item the server has not been told about yet. Walking past the ones
            // already sent is what replaces the old wait: the round trip still has to
            // happen, it just no longer has to happen before the next item can go out.
            ItemIdentifier held = null;
            for (int i = 0; i < _hand.Count; i++)
                if (!AlreadySent(_hand[i])) { held = _hand[i]; break; }

            if (held == null)
            {
                // Everything in hand is in flight. There is nothing left to send, so this
                // is not a stall to work around - it is the hand being empty already.
                Status = $"đang bỏ {_sentBack.Count} món vào thùng...";
                return;
            }

            // Resolve the target AFTER the held item is known, so it can be excluded.
            // PepperoniPack and MilkBottle are themselves ItemInventory, so "the nearest
            // ItemInventory" can easily be the thing in your own hand.
            var inv = ResolveTargetInventory(interact, held);
            if (inv == null) { Status = "không có thùng nào trước mặt"; return; }

            DescribeTargets(interact, held, inv);

            var mgr = ResolveItemManager();
            if (mgr == null) { Status = "không tìm thấy ItemManager"; LogOnce("Box: ItemManager not found."); return; }

            // The box's own GameItem is the stock we are putting into.
            GameItem stockItem = null;
            try { stockItem = inv.GetComponent<GameItem>(); } catch { }
            if (stockItem == null)
            {
                Status = "thùng này không có GameItem";
                LogOnce($"Box: '{inv.name}' has no GameItem component - cannot stock into it.");
                return;
            }

            // StockItem is the exact inverse of the TakeFromStock used when you pull an
            // item out, so it restores the item itself. Reading the held item's
            // IngredientHolder instead gave back its *contents* - a PepperoniPack came
            // back as 4 loose pepperoni, a milk carton as 200ml of milk.
            //
            // The box's stack stores the item-level key with a count - "MilkBottlex97" -
            // not the contents. So one item is one unit of its own key. The Stock* calls
            // turned out to belong to restaurant stock fridges and did nothing here.
            var stack = inv.inventoryStack;
            if (stack == null) { Status = "thùng này không có chồng đồ"; return; }

            string key = ResolveItemKey(held, stack);
            if (string.IsNullOrEmpty(key))
            {
                Status = "không nhận ra vật phẩm đang cầm";
                LogOnce($"Box: could not resolve an ingredient key for '{held.name}'.");
                return;
            }

            // Add before destroy: if the add throws, the item is still in your hand,
            // which beats destroying it and losing it.
            mgr.AddIngredientServerRpc(stack.gameID, key, 1f);
            mgr.DestroyItemServerRpc(held.itemIndex);
            MarkSent(held);

            Status = $"đã bỏ {key} vào {inv.name}";
            LogOnce($"Box: put back '{key}' x1 into '{inv.name}' (stackGID={stack.gameID}).");
        }
        catch (Exception e)
        {
            Status = "bỏ đồ vào lại thất bại - xem log";
            LogOnce($"Box: put-back failed: {e.Message}");
        }
    }

    /// <summary>
    /// The ingredient key that represents the item itself.
    ///
    /// Prefer a key already present in the box, matched against the item's object name -
    /// that way the exact spelling comes from the game's own data rather than from our
    /// guess at it. Fall back to the cleaned object name when the box holds none yet.
    /// </summary>
    private static string ResolveItemKey(ItemIdentifier held, IngredientStack stack)
    {
        string name = CleanName(held.name);
        if (string.IsNullOrEmpty(name)) return null;

        try
        {
            var list = stack.Ingredients;
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var d = list[i];
                    var key = d?.Ingredient?.ingredientKey;
                    if (!string.IsNullOrEmpty(key) &&
                        string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return key;
                    }
                }
            }
        }
        catch { /* fall through to the name */ }

        return name;
    }

    /// <summary>Unity appends "(Clone)" to instantiated objects; the key never has it.</summary>
    private static string CleanName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        int idx = name.IndexOf("(Clone)", StringComparison.Ordinal);
        if (idx >= 0) name = name.Substring(0, idx);
        return name.Trim();
    }

    private static ItemManager ResolveItemManager()
    {
        if (_itemManager != null) return _itemManager;

        try
        {
            var found = UnityEngine.Object.FindObjectsByType<ItemManager>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            if (found != null && found.Length > 0) _itemManager = found[0];
        }
        catch (Exception e)
        {
            LogOnce($"Box: ItemManager lookup failed: {e.Message}");
        }
        return _itemManager;
    }

    /// <summary>
    /// A cardboard shopping box, as opposed to a product container like an egg tray or
    /// tofu pack.
    ///
    /// Both are ItemInventory, so the type alone cannot tell them apart. The shopping
    /// boxes are the ones carrying an InventoryPlacingEvent - things you can place into -
    /// while product packs have none. The name check is a secondary signal for the same
    /// group, in case a box turns up without the component.
    /// </summary>
    private static bool IsShoppingBox(ItemInventory inv)
    {
        if (inv == null) return false;

        try
        {
            if (inv._inventoryPlacingEvent != null) return true;
        }
        catch { /* fall through to the name */ }

        try
        {
            return inv.name != null &&
                   inv.name.IndexOf("ItemInventory", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// The inventory the player is aiming at, never one they are carrying.
    /// </summary>
    private static ItemInventory ResolveTargetInventory(CharacterItemInteract interact, ItemIdentifier held)
    {
        var candidates = new[] { interact.interactingItem, interact.LastInteractingItem };

        foreach (var c in candidates)
        {
            if (c == null) continue;
            if (held != null && c.Pointer == held.Pointer) continue;   // that is our own hand
            if (IsCarried(interact, c)) continue;

            ItemInventory inv = null;
            try { inv = c.TryCast<ItemInventory>(); } catch { }
            if (inv != null) return inv;
        }
        return null;
    }

    /// <summary>
    /// Whether the player is carrying this item, in hand or queued behind it.
    ///
    /// Excluding only the one item being put back was enough while that was always
    /// currentItem. It no longer is - a repeat now works through the queue as well - and
    /// PepperoniPack and MilkBottle are themselves ItemInventory, so without this a box
    /// further down your own hands could be chosen as the thing to put items into.
    ///
    /// Deliberately not using the CollectHand scratch list: this is called from inside the
    /// loop that walks it.
    /// </summary>
    private static bool IsCarried(CharacterItemInteract interact, ItemIdentifier candidate)
    {
        if (candidate == null) return false;

        try
        {
            IntPtr ptr = candidate.Pointer;

            var cur = interact.currentItem;
            if (cur != null && cur.Pointer == ptr) return true;

            var queued = interact.pickedItemsWaitingInQueue;
            if (queued != null)
                for (int i = 0; i < queued.Count; i++)
                {
                    var q = queued[i];
                    if (q != null && q.Pointer == ptr) return true;
                }
        }
        catch { }

        return false;
    }

    /// <summary>
    /// One-off dump of what we resolved. Several attempts at this feature have failed
    /// silently, so record the identities rather than inferring them.
    /// </summary>
    private static void DescribeTargets(CharacterItemInteract interact, ItemIdentifier held, ItemInventory inv)
    {
        string Describe(ItemIdentifier i)
        {
            if (i == null) return "null";
            int gid = -1;
            try { var g = i.GetComponent<GameItem>(); if (g != null) gid = g.gameID; } catch { }
            bool isInv = false;
            try { isInv = i.TryCast<ItemInventory>() != null; } catch { }
            return $"{i.name}(gid={gid}{(isInv ? ",inventory" : "")})";
        }

        int stackGid = -1;
        try { if (inv.inventoryStack != null) stackGid = inv.inventoryStack.gameID; } catch { }

        LogOnce($"Box targets: held={Describe(held)} " +
                $"interacting={Describe(interact.interactingItem)} " +
                $"last={Describe(interact.LastInteractingItem)} " +
                $"chosen={Describe(inv)} stackGID={stackGid}");

        // What vocabulary does the box actually store? If it lists "MilkBottle" then the
        // item-level key is what AddIngredient needs, and the earlier attempt simply used
        // the contents key instead.
        try
        {
            var contents = new List<string>();
            var list = inv.inventoryStack?.Ingredients;
            if (list != null)
            {
                for (int i = 0; i < list.Count && i < 12; i++)
                {
                    var d = list[i];
                    if (d == null) continue;
                    string k = d.Ingredient == null ? "?" : d.Ingredient.ingredientKey;
                    contents.Add($"{k}x{d.Amount:0.##}");
                }
            }
            LogOnce($"Box contents of '{inv.name}': {(contents.Count == 0 ? "(empty)" : string.Join(", ", contents))}");
        }
        catch (Exception e)
        {
            LogOnce($"Box: could not read stack contents: {e.Message}");
        }
    }

    private static void LogOnce(string message)
    {
        if (Logged.Add(message)) Plugin.Logger.LogInfo(message);
    }
}
