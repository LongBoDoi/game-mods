using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PlayerTweaks;

/// <summary>
/// Two limits lifted off the Slicify machine: what it will take, and how many trips it costs.
///
/// The machine is an <c>Autoslicer</c>, a placed restaurant object with two racks -
/// <c>vegAttacher</c> for the thing to cut and <c>plateAttacher</c> for the tray that catches
/// the slices - and a switch that runs <c>SlicerCoroutine</c>.
///
/// **Widening what it takes.** Everything a rack refuses, it refuses in
/// <c>RackAttacher.CanAttach(ref key)</c>, which compares the item's <c>itemProperty</c>
/// against the short list of prefabs the rack was authored with; a postfix there turns the no
/// into a yes. The first attempt at this looked right and did nothing, because of how the rack
/// was recognised: it keyed on <c>rackPRO</c>, a serialized field that a machine's own
/// attachers need not have filled in. Recognising the machine from <c>rackPRO</c> *or* the
/// attacher's runtime parent item is what made it work.
///
/// "Anything" means anything carrying a <c>Vegetable</c> component. That is not a hedge:
/// Vegetable is what holds sliceObject, sliceAmount and the InitSliceServerRpc/ClientRpc pair,
/// it is what the machine's slicing path expects to find, and in this game it covers fish and
/// meat portions as well as greens. Letting a plate onto the rack would only block the slot
/// with something the machine cannot do anything with.
///
/// **Cutting an armful at once.** Shift + click hands the machine everything in hand. The first
/// version of this fed the rack one item at a time through <c>ItemManager.AttachItem</c>, and
/// that is exactly as good as the rack is deep: the machine held the first item for as long as
/// a cut takes while the rest of the armful was tipped onto the floor waiting for a slot that
/// frees once. Five vegetables went in and one came back sliced.
///
/// So the rack is out of this path. <c>Vegetable.AutoSlice(tray, callback)</c> is the call the
/// machine's own coroutine makes once an item reaches the rack, and nothing in it requires the
/// item to be on the rack - it is handed the tray to put the slices on and does the rest.
/// Calling it directly cuts the whole armful in one pass, onto the machine's own tray.
///
/// The hand has to be emptied first, because an item parented to the player while it is being
/// consumed leaves the hand holding something that no longer exists - and emptying it is what
/// took three attempts. The game only ever lets go of the item actually in the hand and then
/// pulls the next one down itself, so the armful comes down a beat at a time however it is
/// asked for; trying to shortcut that with <c>RemoveItemFromQueue</c> and
/// <c>ReleaseAlreadyPickedItemServerRpc</c> released nothing and risked leaving the player
/// holding items they could not drop. What actually had to change was *when* the cutting
/// happens: nothing is cut until the whole armful is down, so the slices appear together even
/// though the items do not. It was never the releasing that had to be simultaneous.
///
/// The cuts are checked afterwards rather than probed beforehand. If every one of them left its
/// item whole, the run falls back to the old one-at-a-time rack feeding and says so in the log;
/// checking after the fact catches the same failure without making every run wait on the first
/// item to prove itself.
///
/// All of it is gated on the machine's own switch. Slicing an armful on a machine that is
/// standing still reads as the mod conjuring slices rather than as the machine doing its job,
/// and switching it off mid-run stops the run.
/// </summary>
public static class SlicerTweaks
{
    public static string Status = "đã tắt";

    private static readonly HashSet<string> Logged = new();

    /// <summary>Whether the CanAttach patch is in place; the widening is off without it.</summary>
    private static bool _patched;

    // ------------------------------------------------------------------ install

    public static void Install(BepInEx.Logging.ManualLogSource log)
    {
        try
        {
            var target = HarmonyLib.AccessTools.Method(
                typeof(RackAttacher), nameof(RackAttacher.CanAttach));

            if (target == null)
            {
                log.LogWarning("Slicer: RackAttacher.CanAttach not found; the machine keeps its own list.");
                return;
            }

            // Guarded the same way the box patch is: CanAttach is declared on Attacher and
            // overridden by five different racks, and only the one on RackAttacher is ours.
            if (target.DeclaringType != typeof(RackAttacher))
            {
                log.LogWarning($"Slicer: CanAttach resolves to {target.DeclaringType?.Name}, " +
                               "not RackAttacher - refusing to patch a shared method.");
                return;
            }

            new HarmonyLib.Harmony(Plugin.Guid + ".slicer").Patch(
                target,
                postfix: new HarmonyLib.HarmonyMethod(
                    HarmonyLib.AccessTools.Method(typeof(SlicerTweaks), nameof(CanAttachPostfix))));

            _patched = true;
            log.LogInfo("Slicer: patched RackAttacher.CanAttach.");
        }
        catch (Exception e)
        {
            log.LogWarning($"Slicer: install failed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// Lets a sliceable item onto a Slicify machine's rack whatever the rack was authored with.
    ///
    /// Only ever turns a no into a yes, and only for the cutting rack - the plate rack is left
    /// alone, or the tray slot would start accepting tomatoes.
    /// </summary>
    private static void CanAttachPostfix(RackAttacher __instance, ref string key, ref bool __result)
    {
        if (__result || !_patched || !Plugin.SlicerAnyItem.Value || __instance == null) return;

        try
        {
            var slicer = MachineOf(__instance);

            if (slicer == null || !IsCuttingRack(slicer, __instance))
            {
                ReportRefusal(__instance, slicer, key);
                return;
            }

            if (!IsSliceableKey(key))
            {
                LogOnce($"Slicer: '{key}' is not something the game can slice, so the rack " +
                        "still refuses it.");
                return;
            }

            __result = true;

            // The role is in the line because the attach itself is host-authoritative -
            // ItemManager.AttachItem -> AttachItemServerRpc -> AttachItemClientRpc ->
            // AttachItemInternal - and whether the far end re-asks CanAttach on arrival
            // cannot be read out of the interop stubs. If a widened item ever sticks on one
            // screen and not the other, this line says which side allowed it.
            LogOnce($"Slicer: allowing '{key}' onto the cutting rack as {Role()}.");
        }
        catch (Exception e)
        {
            LogOnce($"Slicer: attach check failed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>How many distinct refusals are described before the log goes quiet.</summary>
    private const int MaxRefusalReports = 8;

    private static int _refusalReports;

    /// <summary>
    /// Says once what a rack we could not place actually was.
    ///
    /// Without this, "the option does nothing" and "the option is looking at the wrong rack"
    /// read identically from inside the game - which is exactly how the first version of this
    /// shipped looking correct and doing nothing.
    /// </summary>
    private static void ReportRefusal(RackAttacher rack, Autoslicer slicer, string key)
    {
        if (_refusalReports >= MaxRefusalReports) return;

        string line;
        try
        {
            line = $"Slicer: refused '{key}' on rack '{rack.name}' - machine={(slicer == null ? "none" : slicer.name)}, " +
                   $"rackPRO={SafeName(rack.rackPRO)}, parent={SafeName(rack.GetParentItem())}";
        }
        catch (Exception e) { line = $"Slicer: refused '{key}' on a rack we could not read ({e.GetType().Name})"; }

        if (!Logged.Add(line)) return;

        _refusalReports++;
        Plugin.Logger.LogInfo(line);
    }

    /// <summary>The Slicify machine an attacher belongs to, by either of the two links it has.</summary>
    private static Autoslicer MachineOf(RackAttacher rack)
    {
        try
        {
            var pro = rack.rackPRO;
            if (pro != null)
            {
                var byPro = pro.TryCast<Autoslicer>();
                if (byPro != null) return byPro;
            }
        }
        catch { }

        // rackPRO is a serialized convenience that a machine's own attachers need not fill in.
        // parentItem is set by Attacher.SetParent at runtime, so it is there either way.
        try
        {
            var parent = rack.GetParentItem();
            if (parent != null) return parent.TryCast<Autoslicer>();
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Whether this is the slot the machine cuts from rather than the one the tray sits in.
    ///
    /// Asked as "not the plate rack" rather than "is the veg rack". The two are the same
    /// question when both fields are filled in, and when they are not, refusing to widen is
    /// the failure being fixed - whereas the worst a wrong yes can do is let a tomato onto a
    /// tray slot the player can lift it straight back off.
    /// </summary>
    private static bool IsCuttingRack(Autoslicer slicer, RackAttacher rack)
    {
        try
        {
            var veg = slicer.vegAttacher;
            if (veg != null && veg.Pointer == rack.Pointer) return true;

            var plate = slicer.plateAttacher;
            if (plate != null && plate.Pointer == rack.Pointer) return false;
        }
        catch { return false; }

        return true;
    }

    // -------------------------------------------------------- what can be sliced

    /// <summary>Answers already worked out, so a rack's per-frame check is a dictionary hit.</summary>
    private static readonly Dictionary<string, bool> SliceableKeys =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether an item key names something the game knows how to cut.
    ///
    /// Asked of the item catalogue rather than guessed from the name. ItemManager can hand
    /// back the prefab for a property directly; when it cannot, the level's own item list is
    /// walked instead, which is the same data by a slower road.
    /// </summary>
    private static bool IsSliceableKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        if (SliceableKeys.TryGetValue(key, out bool known)) return known;

        var mgr = ResolveItemManager();
        if (mgr == null) return false;      // not cached: the answer would be a false no

        bool sliceable = false;

        try
        {
            var prefab = mgr.GetItemByProperty(key);
            if (prefab != null) sliceable = prefab.TryCast<Vegetable>() != null;
            else sliceable = SearchLevelItems(mgr, key);
        }
        catch (Exception e)
        {
            LogOnce($"Slicer: could not look up '{key}': {e.GetType().Name}: {e.Message}");
            return false;
        }

        SliceableKeys[key] = sliceable;
        return sliceable;
    }

    private static bool SearchLevelItems(ItemManager mgr, string key)
    {
        try
        {
            var items = mgr.levelItems;
            if (items == null) return false;

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null) continue;

                if (!string.Equals(item.itemProperty, key, StringComparison.OrdinalIgnoreCase))
                    continue;

                return item.TryCast<Vegetable>() != null;
            }
        }
        catch { }

        return false;
    }

    /// <summary>Whether this item is one the machine could cut.</summary>
    private static bool IsSliceable(ItemIdentifier item)
    {
        try
        {
            var veg = item == null ? null : item.TryCast<Vegetable>();
            return veg != null && !veg.isSliced && veg.completedSlices == 0;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------ loading a run

    /// <summary>
    /// Most items one Shift + click will take.
    ///
    /// Set to the carry ceiling this mod itself allows, so it can never be the thing that
    /// stops an armful going in - the point of the feature is to empty your hands, and a
    /// smaller number here was doing exactly what it looked like it was doing: quietly leaving
    /// the rest behind. It is still a bound rather than no bound, so a queue that somehow ran
    /// away could not be walked forever.
    /// </summary>
    private const int MaxLoad = CarryTweaks.MaxSupported;

    /// <summary>How far the machine may be and still be the thing being clicked.</summary>
    private const float AimRange = 5f;

    /// <summary>How often a release is asked for, and how often the rack is looked at.</summary>
    private const float StepSeconds = 0.05f;

    /// <summary>How long a run may make no progress before it is given up on.</summary>
    private const float StallTimeout = 12f;

    /// <summary>How long an item may sit on a running machine before the machine is nudged.</summary>
    private const float NudgeAfter = 3f;

    /// <summary>One run: the machine, and what is still to go into it.</summary>
    private static Autoslicer _machine;
    private static readonly List<ItemIdentifier> Waiting = new();
    private static float _nextStep;
    private static float _lastProgress;
    private static int _fed;


    private static ItemManager _itemManager;
    private static float _managerScan;

    private static float _attachedSince;
    private static IntPtr _nudged;

    public static void Tick(CharacterItemInteract interact, bool overlayOpen, ulong playerId)
    {
        if (!Plugin.SlicerLoadArmful.Value && !Plugin.SlicerAnyItem.Value)
        {
            Status = "đã tắt";
            Abandon();
            return;
        }

        UpdateStatus();

        if (!Plugin.SlicerLoadArmful.Value) { Abandon(); return; }

        if (!overlayOpen) CheckTrigger(interact);

        if (_machine == null) return;

        // Switching the machine off mid-run stops it, the same as it would stop the machine's
        // own cutting. What is already out of the hand stays on the floor by the machine.
        if (!IsRunning(_machine))
        {
            Note("máy đã tắt - dừng lại");
            Abandon();
            return;
        }

        if (Time.unscaledTime < _nextStep) return;
        _nextStep = Time.unscaledTime + StepSeconds;

        try { StepRun(interact, playerId); }
        catch (Exception e)
        {
            LogOnce($"Slicer: run failed: {e.GetType().Name}: {e.Message}");
            Abandon();
        }
    }

    /// <summary>
    /// Something to tell the player that outlives the frame it happened in.
    ///
    /// The status line is rebuilt every tick, so a message written when the key was pressed
    /// was overwritten before the panel had drawn it once - which made "the machine is off"
    /// indistinguishable from the click having done nothing at all.
    /// </summary>
    private static string _note;
    private static float _noteUntil;

    private static void Note(string text)
    {
        _note = text;
        _noteUntil = Time.unscaledTime + 4f;
    }

    private static void UpdateStatus()
    {
        string what =
            Plugin.SlicerAnyItem.Value && Plugin.SlicerLoadArmful.Value ? "nhận mọi nguyên liệu + Shift+chuột trái"
            : Plugin.SlicerAnyItem.Value ? "nhận mọi nguyên liệu"
            : "Shift+chuột trái để đưa cả ôm đồ vào";

        if (_note != null && Time.unscaledTime < _noteUntil)
        {
            Status = $"{what} - {_note}";
            return;
        }

        if (_machine != null && Waiting.Count > 0)
        {
            Status = _hint != null
                ? $"{what} - {_hint}, còn {Waiting.Count} món"
                : $"{what} - đang thái, còn {Waiting.Count} món";
            return;
        }

        Status = _fed == 0
            ? $"{what} - chưa thái món nào"
            : $"{what} - đã thái {_fed} món";
    }

    /// <summary>
    /// Starts a run on Shift + left click at a machine.
    ///
    /// The click is not swallowed. Whatever the game would have done with it - usually
    /// nothing, since the rack refuses most of what you are holding - it still does, and this
    /// takes the armful either way.
    /// </summary>
    private static void CheckTrigger(CharacterItemInteract interact)
    {
        if (interact == null || !ShiftHeld()) return;

        try
        {
            if (Mouse.current == null || !Mouse.current.leftButton.wasPressedThisFrame) return;
        }
        catch { return; }

        var slicer = AimedMachine(interact);
        if (slicer == null) return;

        // The switch decides, the same as it does for the machine's own cutting. Without this
        // the armful is sliced by a machine that is standing still, which reads as the mod
        // conjuring slices rather than as the machine doing its job.
        if (!IsRunning(slicer))
        {
            Note("hãy bật máy thái trước");
            return;
        }

        var load = Armful(interact, out int blocked, out int truncated);

        if (load.Count == 0)
        {
            Note(blocked > 0
                ? "không món nào đang cầm có thể thái được"
                : "hãy cầm nguyên liệu rồi Shift + chuột trái vào máy");
            return;
        }

        // Whatever is being left behind, say so at the moment of the click. Leaving items in
        // hand with no explanation is the same complaint whether the cause is a cap or an
        // item the machine cannot cut, and only one of the two is worth changing a setting for.
        if (blocked > 0) Note($"bỏ lại {blocked} món máy không thái được");
        else if (truncated > 0) Note($"mỗi lần chỉ nhận {MaxLoad} món");

        _machine = slicer;
        Waiting.Clear();
        Waiting.AddRange(load);

        _nextStep = 0f;
        _lastProgress = Time.unscaledTime;
        _attachedSince = -1f;
        _cut.Clear();
        _hint = null;
        _askedForTray = false;

        Plugin.Logger.LogInfo(
            $"Slicer: loading {load.Count} item(s) into '{slicer.name}'" +
            (blocked > 0 ? $", stopping at {blocked} it cannot cut" : "") +
            (truncated > 0 ? $", leaving {truncated} over the {MaxLoad} cap" : "") + ".");
    }

    /// <summary>
    /// Everything in hand that the machine could cut, in the order it will come out.
    ///
    /// The leading run only. Carrying a plate behind two tomatoes means the tomatoes go and
    /// the plate stops the list there: the items come out of the hand one at a time, front
    /// first, so reaching anything behind the plate would mean dropping the plate - and
    /// dropping something the player did not ask to put down is worse than taking fewer.
    /// </summary>
    private static List<ItemIdentifier> Armful(CharacterItemInteract interact,
                                               out int blocked, out int truncated)
    {
        var list = new List<ItemIdentifier>();
        blocked = 0;
        truncated = 0;

        try
        {
            var current = interact.currentItem;
            if (current == null) return list;

            if (!IsSliceable(current)) { blocked = 1; return list; }
            list.Add(current);

            var queue = interact.pickedItemsWaitingInQueue;
            if (queue == null) return list;

            for (int i = 0; i < queue.Count; i++)
            {
                // Counted rather than just stopped. A cap that silently takes the first
                // however-many is indistinguishable from the feature half working, which is
                // exactly how the old limit of twelve was found - by someone holding twenty.
                if (list.Count >= MaxLoad) { truncated = queue.Count - i; break; }

                var item = queue[i];
                if (item == null) continue;

                if (!IsSliceable(item)) { blocked = queue.Count - i; break; }
                list.Add(item);
            }
        }
        catch (Exception e)
        {
            LogOnce($"Slicer: could not read what is in hand: {e.GetType().Name}: {e.Message}");
        }

        return list;
    }

    // -------------------------------------------------------------- running one

    /// <summary>Items cut this run, kept until they can be shown to have actually been cut.</summary>
    private static readonly List<ItemIdentifier> _cut = new();
    private static float _verifyAt;

    /// <summary>How long a cut is given to show a result before it is disbelieved.</summary>
    private const float VerifySeconds = 2f;



    /// <summary>Set once AutoSlice has been seen to do nothing, which turns on the rack path.</summary>
    private static bool _autoSliceBroken;

    private static bool _askedForTray;

    /// <summary>Something the player needs to do, shown next to the status.</summary>
    private static string _hint;

    /// <summary>
    /// One step of a run: empty the hand, then cut what is out of it.
    ///
    /// The rack is not in this path at all any more, and that is the point. Feeding items onto
    /// it one at a time meant the machine held the first item for as long as a cut takes while
    /// the rest of the armful was tipped onto the floor waiting for a slot that only frees once
    /// - so five vegetables went in and one came back sliced. <c>Vegetable.AutoSlice(tray,
    /// callback)</c> is the call the machine's own coroutine makes once an item reaches the
    /// rack, and nothing about it requires the item to be on the rack: it takes the tray to put
    /// the slices on and does the rest. Calling it directly cuts the whole armful in one pass.
    ///
    /// The hand still has to be emptied first, because an item parented to the player while it
    /// is being consumed would leave the hand holding something that no longer exists. The game
    /// only ever lets go of the front item and pulls the next one down itself, so the run walks
    /// the armful a step at a time and cuts each item the moment it is free.
    /// </summary>
    private static void StepRun(CharacterItemInteract interact, ulong playerId)
    {
        if (_machine == null) return;

        Prune();

        // Items already cut, watched until they can be shown to have been cut. Checked after
        // the fact rather than before: AutoSlice has been seen working on this build, so
        // holding the armful back behind a two-second probe only made the run slower, and a
        // check afterwards catches the same failure with nothing to put right that the rack
        // fallback cannot put right.
        if (_cut.Count > 0 && Time.unscaledTime >= _verifyAt) Verify();

        if (Waiting.Count == 0)
        {
            // Only the rack path, and an unverified cut, have anything left to wait for.
            if (_cut.Count > 0) return;

            if (_autoSliceBroken)
            {
                WatchForStall();
                if (Time.unscaledTime - _lastProgress > StallTimeout) Abandon();
                return;
            }

            Abandon();
            return;
        }

        if (_autoSliceBroken)
        {
            if (Time.unscaledTime - _lastProgress > StallTimeout) { GaveUp(); return; }
            FeedRack(Waiting[0]);
            return;
        }

        bool stalled = Time.unscaledTime - _lastProgress > StallTimeout;

        // Nothing is cut until the whole armful is out of the hand, so the slices all appear
        // together instead of one every time the player's hand lets go of the next item.
        if (!stalled && EmptyTheHand(interact)) return;

        // The hand would not give the rest up. Those items are left exactly where they are -
        // in the player's hands - rather than being counted as lost, and whatever did come
        // down is cut. Discarding the run here was the old behaviour and it was the wrong
        // half to keep: the player ends up with neither their armful nor any slices.
        if (stalled)
        {
            int stuck = ForgetStillHeld();

            if (stuck > 0)
            {
                Note($"{stuck} món không rời khỏi tay");
                Plugin.Logger.LogWarning(
                    $"Slicer: {stuck} item(s) never left the hand; cutting the " +
                    $"{Waiting.Count} that did." + (_hint == null ? "" : $" ({_hint})"));
            }

            if (Waiting.Count == 0) { Abandon(); return; }
        }

        CutEverything();
    }

    private static void GaveUp()
    {
        Plugin.Logger.LogWarning(
            $"Slicer: gave up with {Waiting.Count} item(s) still to cut - they are by the " +
            "machine." + (_hint == null ? "" : $" ({_hint})"));
        Abandon();
    }

    /// <summary>Drops still-held items from the run, leaving them in the player's hands.</summary>
    private static int ForgetStillHeld()
    {
        int stuck = 0;

        for (int i = Waiting.Count - 1; i >= 0; i--)
        {
            if (!IsHanded(Waiting[i])) continue;

            Waiting.RemoveAt(i);
            stuck++;
        }

        return stuck;
    }

    /// <summary>
    /// Gets the armful out of the player's hands, and says whether it is still working at it.
    ///
    /// It releases whatever of the run is *in the hand right now* - not the front of the list.
    /// That distinction is the whole bug this replaced. The previous version asked the game to
    /// let go of the queue in one frame with <c>RemoveItemFromQueue</c> plus
    /// <c>ReleaseAlreadyPickedItemServerRpc</c>, and when that did nothing it fell back to
    /// releasing <c>Waiting[0]</c> - which by then had already been put down by the one call
    /// that had worked. So it spent twelve seconds asking the game to drop an item that was
    /// lying on the floor, released exactly one of twenty, and timed out. Both real sessions
    /// show it: one <c>PickUpQueuedItem</c> and then nothing.
    ///
    /// The bulk calls are gone with it. Neither did anything here, and RemoveItemFromQueue in
    /// particular takes an item off the carry queue without putting it down - a good way to
    /// leave a player holding something they cannot drop.
    ///
    /// So the armful comes down the way the game does it: release what is in the hand, let the
    /// game pull the next one through, repeat. That is a beat per item, but nothing is cut
    /// until all of it is down, so the slices still appear together - which is what was being
    /// asked for, and it was never the releasing that had to be simultaneous.
    /// </summary>
    private static bool EmptyTheHand(CharacterItemInteract interact)
    {
        if (!AnyHanded()) return false;

        ItemIdentifier current = null;
        try { current = interact.currentItem; } catch { }

        // Nothing of ours in the hand yet, but some of it still held: the game has not pulled
        // the next one down. Waiting is the only right answer; the stall timeout is what stops
        // this being forever.
        if (current == null || !Owns(current)) return true;

        Release(interact, current);
        return true;
    }

    private static bool AnyHanded()
    {
        foreach (var item in Waiting)
            if (IsHanded(item)) return true;

        return false;
    }

    /// <summary>Whether this item is part of the run.</summary>
    private static bool Owns(ItemIdentifier item)
    {
        try
        {
            foreach (var mine in Waiting)
                if (mine != null && mine.Pointer == item.Pointer) return true;
        }
        catch { }

        return false;
    }


    /// <summary>Cuts the whole armful in one pass, so every slice appears at the same moment.</summary>
    private static void CutEverything()
    {
        var tray = Tray();
        if (tray == null)
        {
            _hint = "máy chưa có khay hứng";
            return;
        }

        _hint = null;

        foreach (var item in Waiting)
        {
            if (!Cut(item, tray))
            {
                // One refusal is enough to distrust the whole approach: the rest of the
                // armful is left in the list for the rack path to work through.
                _autoSliceBroken = true;
                return;
            }

            _cut.Add(item);
            _fed++;
        }

        Waiting.Clear();
        _lastProgress = Time.unscaledTime;
        _verifyAt = Time.unscaledTime + VerifySeconds;

        if (_cut.Count > 1)
            LogOnce($"Slicer: cut {_cut.Count} items in one pass.");
    }

    /// <summary>
    /// Checks that the cuts asked for actually happened, and falls back if none did.
    ///
    /// Only a clean sweep of failures turns the fallback on. A single item that will not slice
    /// is the game's business - it may be one the machine genuinely cannot cut - while nothing
    /// at all slicing means the call is not doing what it is being asked to do here.
    /// </summary>
    private static void Verify()
    {
        int landed = 0;
        foreach (var item in _cut)
            if (CutLanded(item)) landed++;

        if (landed == 0 && !_autoSliceBroken)
        {
            _autoSliceBroken = true;
            Waiting.AddRange(_cut);
            _fed -= _cut.Count;
            _lastProgress = Time.unscaledTime;

            Plugin.Logger.LogWarning(
                $"Slicer: AutoSlice left all {_cut.Count} item(s) whole, so cutting an armful " +
                "in one pass is not available on this build; falling back to the machine's " +
                "own rack, one item at a time.");
        }
        else if (landed < _cut.Count)
        {
            // Said in the panel as well as the log. The likeliest reason for a partial result
            // is the tray having no room left for the slices, and that is a thing the player
            // can do something about - but only if they are told the cut did not happen.
            int missed = _cut.Count - landed;
            Note($"{missed} món chưa thái được - khay có thể đã đầy");

            LogOnce($"Slicer: {missed} of {_cut.Count} item(s) came back uncut; " +
                    "they are on the floor by the machine.");
        }

        _cut.Clear();
    }

    /// <summary>The fallback: the machine's own rack, one item at a time.</summary>
    private static void FeedRack(ItemIdentifier front)
    {
        var rack = _machine.vegAttacher;
        if (rack == null) { Abandon(); return; }

        // A busy rack is the machine working, not the run stalling. Counting it as no progress
        // would abandon the rest of the armful whenever a cut took longer than the timeout.
        if (rack.AttachedItem != null)
        {
            _lastProgress = Time.unscaledTime;
            WatchForStall();
            return;
        }

        if (Attach(rack, front))
        {
            Waiting.RemoveAt(0);
            _fed++;
            _lastProgress = Time.unscaledTime;
            _attachedSince = Time.unscaledTime;
        }
    }

    /// <summary>
    /// Runs the game's own auto-slice on one item, putting the slices on the machine's tray.
    /// </summary>
    private static bool Cut(ItemIdentifier item, ItemIdentifier tray)
    {
        try
        {
            var veg = item.TryCast<Vegetable>();
            if (veg == null) return false;

            veg.AutoSlice(tray, SliceCallback());

            if (_fed == 0)
                Plugin.Logger.LogInfo(
                    $"Slicer: cut '{SafeName(item)}' straight onto '{SafeName(tray)}' " +
                    $"({Describe(veg)}).");

            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning(
                $"Slicer: AutoSlice('{SafeName(item)}') threw {e.GetType().Name}: {e.Message}. " +
                "Falling back to the machine's own rack, one item at a time.");
            return false;
        }
    }

    /// <summary>Whether an item has visibly been cut, however the game got there.</summary>
    private static bool CutLanded(ItemIdentifier item)
    {
        try
        {
            if (item == null) return true;            // consumed by the cut
            if (item.IsGettingDestroyed) return true;

            var veg = item.TryCast<Vegetable>();
            if (veg == null) return true;

            return veg.isSliced || veg.completedSlices > 0;
        }
        catch { return true; }                       // unreadable means gone, which is a result
    }

    private static string Describe(Vegetable veg)
    {
        try { return $"sliced={veg.isSliced}, {veg.completedSlices}/{veg.sliceAmount}"; }
        catch { return "state unreadable"; }
    }

    /// <summary>
    /// The tray the slices land on, asking the machine to make one if it has none.
    ///
    /// AutoSlice is given somewhere to put what it makes, so without a tray there is nothing
    /// to cut onto. CreateAndAttachTray is the machine's own call for this and is asked once
    /// per run rather than every step.
    /// </summary>
    private static ItemIdentifier Tray()
    {
        try
        {
            var rack = _machine.plateAttacher;
            var held = rack == null ? null : rack.AttachedItem;
            var tray = held == null ? null : held.TryCast<ItemIdentifier>();

            if (tray != null) return tray;
        }
        catch { }

        if (!_askedForTray)
        {
            _askedForTray = true;

            try
            {
                _machine.CreateAndAttachTray();
                Plugin.Logger.LogInfo("Slicer: the machine had no tray; asked it to make one.");
            }
            catch (Exception e)
            {
                LogOnce($"Slicer: could not give the machine a tray: {e.GetType().Name}: {e.Message}");
            }
        }

        return null;
    }

    /// <summary>
    /// The notification AutoSlice calls for each slice it makes.
    ///
    /// A real delegate rather than null, because there is no way to tell from the stubs whether
    /// AutoSlice guards the call - and a null dereference part-way through slicing would leave
    /// an item half cut. Held in a static field so nothing collects it while IL2CPP holds a
    /// pointer to it.
    /// </summary>
    private static Il2CppSystem.Action<ItemIdentifier> _sliceCallback;
    private static bool _callbackTried;

    private static Il2CppSystem.Action<ItemIdentifier> SliceCallback()
    {
        if (_sliceCallback != null || _callbackTried) return _sliceCallback;
        _callbackTried = true;

        try
        {
            _sliceCallback = Il2CppInterop.Runtime.DelegateSupport
                .ConvertDelegate<Il2CppSystem.Action<ItemIdentifier>>(
                    new Action<ItemIdentifier>(OnSliceMade));
        }
        catch (Exception e)
        {
            LogOnce($"Slicer: could not build the slice callback ({e.GetType().Name}: " +
                    $"{e.Message}); passing none and hoping the game guards it.");
        }

        return _sliceCallback;
    }

    /// <summary>Called from IL2CPP, so it must never throw.</summary>
    private static void OnSliceMade(ItemIdentifier slice)
    {
        try { LogOnce($"Slicer: the first slice back was '{SafeName(slice)}'."); }
        catch { }
    }

    private static void Prune()
    {
        for (int i = Waiting.Count - 1; i >= 0; i--)
        {
            var item = Waiting[i];

            bool gone;
            try { gone = item == null || item.IsGettingDestroyed || !IsSliceable(item); }
            catch { gone = true; }

            if (gone) Waiting.RemoveAt(i);
        }
    }

    private static bool IsHanded(ItemIdentifier item)
    {
        try { return item.isHanded; }
        catch { return false; }
    }

    private static IntPtr _releasing;
    private static float _releasedAt;

    private static void Release(CharacterItemInteract interact, ItemIdentifier front)
    {
        if (interact == null) return;

        try
        {
            // One release per item, then a second to let it land. isHanded clears through the
            // server on a client, so it can still read true for a round trip after the item
            // has genuinely been put down, and asking again in that window would drop the
            // item the game has already pulled into the hand behind it.
            if (_releasing == front.Pointer && Time.unscaledTime - _releasedAt < 1f) return;

            // Only ever the item actually in the hand. ForceRelease drops whatever is there,
            // and dropping something that is not part of this run would be the mod throwing
            // the player's belongings on the floor.
            var current = interact.currentItem;
            if (current == null || current.Pointer != front.Pointer) return;

            _releasing = front.Pointer;
            _releasedAt = Time.unscaledTime;

            interact.PickedItem_ForceRelease();
            _lastProgress = Time.unscaledTime;
        }
        catch (Exception e)
        {
            LogOnce($"Slicer: could not put '{SafeName(front)}' down: {e.GetType().Name}: {e.Message}");
            Abandon();
        }
    }

    private static bool Attach(RackAttacher rack, ItemIdentifier item)
    {
        var mgr = ResolveItemManager();
        if (mgr == null) return false;

        int attacherGid = ParentGameId(rack);
        int itemGid;

        try { itemGid = item.gameID; } catch { return false; }
        if (attacherGid == 0 || itemGid == 0) return false;

        try
        {
            mgr.AttachItem(attacherGid, itemGid, rack.Index);

            if (_fed == 0)
                Plugin.Logger.LogInfo(
                    $"Slicer: put '{SafeName(item)}' on the rack (attacher {attacherGid}, " +
                    $"index {rack.Index}).");

            return true;
        }
        catch (Exception e)
        {
            LogOnce($"Slicer: could not attach '{SafeName(item)}': {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Restarts a machine that took an item and then did nothing with it.
    ///
    /// The coroutine behind the switch is expected to pick a newly attached item up on its
    /// own, which is what makes loading work at all. If it turns out not to, the item would
    /// sit on a spinning machine forever - so an item still whole after three seconds gets one
    /// call to the machine's own SliceAttachedItem, and one only. The isSliced check is what
    /// makes a second call impossible, so a machine working normally can never be made to cut
    /// the same item twice.
    /// </summary>
    private static void WatchForStall()
    {
        if (_machine == null) return;

        GameItem attached;
        bool on;

        try
        {
            on = _machine.IsOn;
            var rack = _machine.vegAttacher;
            attached = rack == null ? null : rack.AttachedItem;
        }
        catch { return; }

        if (!on || attached == null) { _attachedSince = -1f; return; }

        if (_attachedSince < 0f) { _attachedSince = Time.unscaledTime; return; }
        if (Time.unscaledTime - _attachedSince < NudgeAfter) return;
        if (_nudged == attached.Pointer) return;

        _nudged = attached.Pointer;

        try
        {
            var veg = attached.TryCast<Vegetable>();
            if (veg == null || veg.isSliced || veg.completedSlices > 0) return;

            _machine.SliceAttachedItem();
            LogOnce("Slicer: the machine had not started on an item it was given; nudged it once.");
        }
        catch (Exception e)
        {
            LogOnce($"Slicer: nudge failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void Abandon()
    {
        _machine = null;
        Waiting.Clear();
        _cut.Clear();
        _attachedSince = -1f;
        _hint = null;
    }

    /// <summary>Whether the machine's switch is on, read the same way its own coroutine does.</summary>
    private static bool IsRunning(Autoslicer slicer)
    {
        try { return slicer != null && slicer.IsOn; }
        catch { return false; }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The machine under the crosshair, or null.</summary>
    private static Autoslicer AimedMachine(CharacterItemInteract interact)
    {
        try
        {
            Camera cam = null;
            try { cam = interact.fpsCamera; } catch { }
            if (cam == null) cam = Camera.main;
            if (cam == null) return null;

            var t = cam.transform;
            if (!Physics.Raycast(t.position, t.forward, out var hit, AimRange, ~0,
                                 QueryTriggerInteraction.Ignore))
                return null;

            // Walk up from whatever the ray struck: it is usually the tray, the switch or the
            // item already on the rack rather than the machine object itself.
            for (var node = hit.transform; node != null; node = node.parent)
            {
                var slicer = node.GetComponent<Autoslicer>();
                if (slicer != null) return slicer;
            }
        }
        catch (Exception e)
        {
            LogOnce($"Slicer: aim ray failed: {e.GetType().Name}: {e.Message}");
        }

        return null;
    }

    /// <summary>The GameItem that owns this rack, whose id the attach call is addressed to.</summary>
    private static int ParentGameId(RackAttacher rack)
    {
        try
        {
            var parent = rack.GetParentItem();
            if (parent != null) return parent.gameID;
        }
        catch { }

        try { return _machine == null ? 0 : _machine.gameID; }
        catch { return 0; }
    }

    private static bool ShiftHeld()
    {
        try
        {
            var kb = Keyboard.current;
            if (kb == null) return false;

            return kb[Key.LeftShift].isPressed || kb[Key.RightShift].isPressed;
        }
        catch { return false; }
    }

    private static string SafeName(UnityEngine.Object obj)
    {
        try { return obj == null ? "none" : obj.name; } catch { return "?"; }
    }

    /// <summary>Which side of a co-op session this is, for the log line.</summary>
    private static string Role()
    {
        try
        {
            var nm = Unity.Netcode.NetworkManager.Singleton;
            if (nm == null || !nm.IsListening) return "solo";
            return nm.IsServer ? "host" : "client";
        }
        catch { return "unknown"; }
    }

    private static ItemManager ResolveItemManager()
    {
        if (_itemManager != null) return _itemManager;
        if (Time.unscaledTime < _managerScan) return null;
        _managerScan = Time.unscaledTime + 2f;

        try
        {
            var found = UnityEngine.Object.FindObjectsByType<ItemManager>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            if (found != null && found.Length > 0) _itemManager = found[0];
        }
        catch (Exception e)
        {
            LogOnce($"Slicer: could not find the item manager: {e.Message}");
        }

        return _itemManager;
    }

    private static void LogOnce(string message)
    {
        if (Logged.Add(message)) Plugin.Logger.LogInfo(message);
    }
}
