using System;
using System.Collections.Generic;
using UnityEngine;

namespace PlayerTweaks;

/// <summary>
/// Lets a service counter be topped up while portions are still on it, instead of standing
/// there waiting for the last one to be taken.
///
/// **What the game does.** Serving a finished plate onto a counter is an item event that
/// lives on the plate you are holding - <c>ServeTablePlateEvent</c>, whose
/// <c>CheckPrerequisites(ref target)</c> is asked every frame you aim at something whether
/// the serve may be offered. Say yes and the game shows its own prompt; the click runs
/// <c>DoEvent</c>, which opens the dish picker, and picking a dish goes through
/// <c>ItemManager.ServePlateToServiceTable</c> -> <c>ServePlateServerRpc</c> ->
/// <c>ServePlateClientRpc</c> -> <c>ServiceCounter.ServePlate</c>. That whole chain is the
/// game's, is host-authoritative, and is untouched here.
///
/// The only thing this changes is the answer to that one question. A counter holding
/// portions says no; when it has room left we say yes, and everything after that is the
/// game serving a plate exactly the way it always does.
///
/// **Why the counter is found by a ray rather than read off the event.**
/// <c>ServeTablePlateEvent.counter</c> and <c>ItemEvent.eventTarget</c> are both filled in
/// by the game at runtime, and neither has any guarantee of being current on the frame the
/// check *failed* - a field that is only written when the check succeeds still holds the
/// counter from wherever you were standing a minute ago. Aiming a ray from the camera
/// answers "what is in front of me right now" and owes nothing to game state. That is the
/// same lesson as the late-join work: on a path the game has decided not to run, treat
/// every field it maintains as unset until proven otherwise.
///
/// **A counter cannot be added to, so the stack is made rather than found.** Measured, not
/// assumed: an empty counter served 0 -> 4 with capacity 4, and an occupied one served
/// 4 -> 4 - nothing at all, the rating popup running while the plate stayed in hand. A
/// counter is one plate with a countdown, and there is no second plate for a serve to join.
///
/// What a serve *does* do is fill the counter to <c>MaxServingCount</c>. So a top-up reports
/// the counter as deep as **what is still on it plus one counter's worth**, clears it, and
/// lets the game put the new plate on: the refill lands on exactly the total the two plates
/// should have come to, and the portions already there are counted into it rather than lost.
/// Each serve is worth the counter's own number, which is what makes 4, 5 or 6 the size of a
/// plate rather than a ceiling.
///
/// **The serve itself is the game's, and three attempts at doing it here say why.** Calling
/// <c>ItemManager.ServePlateToServiceTable</c> puts the food on the counter - measured, 0 to 8
/// portions - and stops there. The plate stays in the hand, because the counter adopts *the
/// very plate being held* as its active plate and nothing tells the hand to let go. Destroying
/// it would empty the counter it is now serving from. Unparenting it did not free the hand.
/// <c>PickedItem_ForceRelease</c> did free the hand, and dropped the plate loose, because that
/// is what it is for.
///
/// Each fix needed another step nobody had asked for, which is the signal that the serve is
/// not three calls but a whole path. So this does none of it. It prepares the counter -
/// deepen, clear - as a prefix on <c>ServiceSlot.OnServeClicked</c>, and the game's own
/// handler serves: hand, plate, position and all.
///
/// That also settles when the counter may be cleared. Clearing throws away what is on it, and
/// doing that when the picker opens would cost the player their portions for merely looking;
/// on the button, nothing is irreversible until they have chosen.
///
/// The counter's real depth is never edited. It is read on every call of the getter and kept
/// as the baseline, so a counter nobody has stacked answers with the game's own number, and
/// one that has run dry goes back to it.
///
/// **Removing goes back down the same way it came up.** Stacking a counter three plates deep
/// leaves no way back: the game's remove - held at the counter, <c>ResetCounterSpeciality</c> -
/// throws away everything on it and frees the counter for another recipe, which is the right
/// answer for one plate and much too much for three. So while a counter is stacked, removing
/// takes off exactly what a serve put on and leaves the dish and the rest of the food where
/// they are. The last removal, at one plate's worth or less, is the game's own clear, so a
/// counter still ends up properly empty and free for another dish. A counter this mod has not
/// stacked never reaches any of it.
///
/// A plate has to come back each time, or three plates on would come off as one. The game's
/// clear does that by handing back the counter's own plate, which is why only the last removal
/// needs nothing from us; every removal before it leaves that plate holding the rest of the
/// food, so it asks <c>ItemManager.DuplicateServingItemWithChildren</c> for a copy instead -
/// the same call a serving taken off a counter is made with. That is the acting player's to
/// make, not every machine's, so it is gated on this machine's own player having pressed
/// remove.
///
/// **Co-op keeps in step by arithmetic, not by a message.** Nothing here replicates on its
/// own: the depth is a local Harmony postfix and <c>ResetServing()</c> is a plain local call,
/// so the machine that clicked would be the only one to deepen and clear its counter, and
/// everyone else's <c>ServePlate</c> would land on a counter that is still full - which does
/// nothing, as measured. The fix is that <c>ServePlate</c> is reached on *every* machine, at
/// the end of <c>ServePlateClientRpc</c>, so each one deepens and clears its own copy there
/// from its own portion count, which the game already keeps in step. Same inputs, same sum,
/// same answer, and no ordering to get wrong - a side channel would have had to race the very
/// serve it was describing.
///
/// **That one rule decides where every line of this belongs.** Anything only this machine can
/// see - which plate the counter points at, what is hidden, what is pinned, what the card
/// shows - is done by every machine from its own copy, because a message would have to race the
/// thing it describes. Anything the world agrees on - destroying a spent plate, moving one,
/// sending a rating round - is said once, by the host, because saying it twice is one machine
/// answering its own message. The single exception is handing a plate back on a removal, which
/// spawns an item *for a particular player* and so belongs to the machine whose player asked.
///
/// The gap that leaves is that only that machine learns which plate the counter kept. Rather
/// than pass the answer along, each machine mends its own view: a counter with portions left,
/// holding a plate that is in somebody's hands, has had its plate walk off, and the plate on
/// its serving spot is the one it should have. See MendCounters.
///
/// Two consequences worth knowing. The ceiling is left out of that sum deliberately and
/// enforced on the prompt instead, because it is a per-player setting and two different limits
/// would deepen one counter by two different amounts. And **both players need the mod, with
/// the option on**: a player who does not run this refills the counter to its own depth, and
/// since the host runs the waiters, whoever is missing it decides what the food really runs
/// out at.
/// </summary>
public static class CounterTopUp
{
    public static string Status = "đã tắt";

    private static readonly HashSet<string> Logged = new();

    private static void LogOnce(string message)
    {
        if (Logged.Add(message)) Plugin.Logger.LogInfo(message);
    }

    /// <summary>How far to look for the counter being aimed at, in metres.</summary>
    private const float AimRange = 5f;

    private static bool _patched;

    // ---------------------------------------------------------------- install

    /// <summary>
    /// Patches the one decision, and watches the serve so the result can be checked.
    ///
    /// <c>CheckPrerequisites</c> is virtual and around sixty item events override it, so the
    /// declaring-type guard is not a formality: patching the shared base is what crashed the
    /// game during the shopping-box work. <c>AccessTools</c> resolving anything other than
    /// this event's own override means the patch is refused.
    /// </summary>
    public static void Install(BepInEx.Logging.ManualLogSource log)
    {
        var harmony = new HarmonyLib.Harmony(Plugin.Guid + ".countertopup");

        try
        {
            var gate = HarmonyLib.AccessTools.Method(
                typeof(ServeTablePlateEvent), nameof(ServeTablePlateEvent.CheckPrerequisites));

            if (gate == null)
            {
                log.LogWarning("Counter top-up: could not resolve ServeTablePlateEvent.CheckPrerequisites.");
                return;
            }

            if (gate.DeclaringType != typeof(ServeTablePlateEvent))
            {
                log.LogWarning($"Counter top-up: CheckPrerequisites resolves to {gate.DeclaringType?.Name}, " +
                               "not ServeTablePlateEvent - refusing to patch a method every item event shares.");
                return;
            }

            harmony.Patch(gate, postfix: new HarmonyLib.HarmonyMethod(
                HarmonyLib.AccessTools.Method(typeof(CounterTopUp), nameof(AllowTopUp))));

            _patched = true;
        }
        catch (Exception e)
        {
            log.LogWarning($"Counter top-up: could not patch the serve check: {e.GetType().Name}: {e.Message}");
            return;
        }

        // Where the stack is committed. Everything the counter needs doing to it happens
        // here and the game's own handler then serves - which is the whole reason this hangs
        // off the picker's button rather than off the item event.
        try
        {
            var clicked = HarmonyLib.AccessTools.Method(
                typeof(ServiceSlot), nameof(ServiceSlot.OnServeClicked));

            if (clicked != null && clicked.DeclaringType == typeof(ServiceSlot))
            {
                harmony.Patch(clicked, prefix: new HarmonyLib.HarmonyMethod(
                    HarmonyLib.AccessTools.Method(typeof(CounterTopUp), nameof(PrepareStack))));
                _direct = true;
            }
            else
            {
                log.LogWarning("Counter top-up: could not resolve ServiceSlot.OnServeClicked - " +
                               "the dish picker will refuse to stack.");
            }
        }
        catch (Exception e)
        {
            log.LogWarning($"Counter top-up: could not patch OnServeClicked: {e.GetType().Name}: {e.Message}");
        }

        // Without this the picker draws a row for the counter's own dish and then refuses the
        // click on it - the wall this feature first ran into - so the handler above would
        // never be reached.
        try
        {
            var setRecipe = HarmonyLib.AccessTools.Method(
                typeof(ServiceSlot), nameof(ServiceSlot.SetRecipe));

            if (setRecipe != null)
            {
                harmony.Patch(setRecipe,
                    prefix: new HarmonyLib.HarmonyMethod(
                        HarmonyLib.AccessTools.Method(typeof(CounterTopUp), nameof(SlotServable))),
                    postfix: new HarmonyLib.HarmonyMethod(
                        HarmonyLib.AccessTools.Method(typeof(CounterTopUp), nameof(SlotClickable))));
            }
        }
        catch (Exception e)
        {
            LogOnce($"Counter top-up: dish-picker row not unlocked: {e.Message}");
        }

        // How deep a counter is. One serve fills it to this number - measured, not assumed:
        // an empty counter went 0 -> 4 with capacity 4 - so raising it is what makes a
        // counter hold more from a single plate, and is the one lever that does not involve
        // taking anything off the counter first.
        try
        {
            var max = HarmonyLib.AccessTools.PropertyGetter(
                typeof(ServiceCounter), nameof(ServiceCounter.MaxServingCount));

            if (max != null && max.DeclaringType == typeof(ServiceCounter))
            {
                harmony.Patch(max, postfix: new HarmonyLib.HarmonyMethod(
                    HarmonyLib.AccessTools.Method(typeof(CounterTopUp), nameof(StackedCapacity))));
                _capacity = true;
            }
            else
            {
                log.LogWarning("Counter top-up: could not resolve ServiceCounter.MaxServingCount - " +
                               "the capacity setting will do nothing.");
            }
        }
        catch (Exception e)
        {
            log.LogWarning($"Counter top-up: capacity override not installed: {e.Message}");
        }

        // Purely an observation. ServePlate is where a serve lands whichever machine started
        // it, so this is the one place that can say what a top-up did to the count.
        try
        {
            var serve = HarmonyLib.AccessTools.Method(
                typeof(ServiceCounter), nameof(ServiceCounter.ServePlate));

            if (serve != null && serve.DeclaringType == typeof(ServiceCounter))
                harmony.Patch(serve, prefix: new HarmonyLib.HarmonyMethod(
                    HarmonyLib.AccessTools.Method(typeof(CounterTopUp), nameof(WatchServe))));
        }
        catch (Exception e)
        {
            LogOnce($"Counter top-up: serve watcher not installed: {e.Message}");
        }

        // The other half: taking a plate back off.
        //
        // The first attempt hung this on ResetCounter alone, on the reasoning that it is the
        // tail of ResetCounterServerRpc -> ResetCounterClientRpc the way ServePlate is the
        // tail of the serve. Measured 2026-09-05: the remove ran, the counter went to zero,
        // and the ResetCounter hook was never reached at all - so the player's remove empties
        // a counter by some other call. Hence a net rather than a hook: every way a counter
        // can be emptied is covered, and the one the game actually uses says so in the log.
        try
        {
            InstallUnstack(log, harmony);
        }
        catch (Exception e)
        {
            log.LogWarning($"Counter top-up: unstack hook not installed: {e.Message}");
        }

        log.LogInfo("Counter top-up: patched ServeTablePlateEvent.CheckPrerequisites.");
        Status = "sẵn sàng";
    }

    // ------------------------------------------------------------------- gate

    /// <summary>
    /// Turns the game's no into a yes when the counter has room for more.
    ///
    /// Only ever false -> true. A serve the game was already going to allow is left
    /// completely alone, so with the option off - or on a counter that is empty, full, or
    /// not there - this is not in the path at all.
    /// </summary>
    private static void AllowTopUp(ServeTablePlateEvent __instance, ref Transform target, ref bool __result)
    {
        if (__result || !Plugin.CounterTopUpEnabled.Value || __instance == null) return;

        try
        {
            var counter = AimedCounter();
            if (counter == null) return;

            int active = counter.ActiveServings;
            int max = counter.MaxServingCount;

            // Portions still on it is the whole of what this lifts. A counter standing at
            // zero and still refusing is refusing for some other reason - the plate is not
            // finished, the dish is not on today's menu, whatever it may be - and that is
            // not this option's business. HasActivePlate() is deliberately not asked as
            // well: it is a second way of saying the same thing, and a second way to be
            // wrong about a counter that is plainly holding portions.
            if (max <= 0 || active <= 0) return;

            // The game hands back the transform to serve onto only when it is going to allow
            // the serve, so on this path there is usually nothing in it. The counter's own
            // service transform is the place a plate goes.
            if (target == null)
            {
                target = ServeTransform(counter);
                if (target == null)
                {
                    LogOnce("Counter top-up: the counter has no service transform to serve onto; " +
                            "leaving the game's refusal alone.");
                    return;
                }
            }

            _lastAllowed = counter;
            _lastAllowedAt = Time.unscaledTime;

            // The ceiling is enforced here rather than in the sum, so that two players with
            // different limits never deepen a counter differently - see Deepen.
            int step = BaselineOf(counter);
            int ceiling = Plugin.CounterStackLimit.Value;

            if (ceiling > 0 && active + step > ceiling)
            {
                Status = $"đã đạt trần {ceiling} suất";
                return;
            }

            Status = $"quầy còn {active} suất - phục vụ để cộng thêm {step}";

            LogOnce($"Counter top-up: offering a serve onto a counter that still holds " +
                    $"{active} of {max} portions.");

            __result = true;
        }
        catch (Exception e)
        {
            LogOnce($"Counter top-up: serve check failed: {e.GetType().Name}: {e.Message}");
        }
    }

    // --------------------------------------------------------- deeper counters

    private static bool _capacity;

    /// <summary>Whether the capacity hook is installed, for the panel to report.</summary>
    public static bool CanResize => _capacity;

    /// <summary>The counter's own depth, as the game reports it, per counter.</summary>
    private static readonly Dictionary<IntPtr, int> Baseline = new();

    /// <summary>The depth a counter has been stacked up to, per counter.</summary>
    private static readonly Dictionary<IntPtr, int> Stacked = new();

    /// <summary>
    /// Reports a counter as deep as it has been stacked.
    ///
    /// This is what turns "one plate with a countdown" into something that stacks. A serve
    /// fills a counter to <c>MaxServingCount</c>, so raising that number to *what is still on
    /// the counter plus one counter's worth* and then refilling leaves exactly the total the
    /// two plates should have come to. The counter's own depth is never edited - it is read
    /// here on every call and kept as the baseline, so a counter nobody has stacked, and a
    /// counter that has run dry, both answer with the game's own number.
    ///
    /// The card above the counter draws portions as a fixed row of dot images. How it copes
    /// with more portions than it has dots is the game's business and cannot be read from
    /// outside, which is what the ceiling setting is for.
    /// </summary>
    private static void StackedCapacity(ServiceCounter __instance, ref int __result)
    {
        if (__instance == null) return;

        try
        {
            IntPtr key = __instance.Pointer;
            Baseline[key] = __result;

            if (Stacked.TryGetValue(key, out int want) && want > __result) __result = want;
        }
        catch { }
    }

    /// <summary>One counter's worth of portions - the number a single plate is worth.</summary>
    private static int BaselineOf(ServiceCounter counter)
    {
        try
        {
            if (Baseline.TryGetValue(counter.Pointer, out int known) && known > 0) return known;

            // Nothing has asked this counter for its depth yet. Asking now is safe: with no
            // stack recorded the postfix hands back the game's own number unchanged.
            return counter.MaxServingCount;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Forgets the stack on counters that have run dry, so the next plate starts from the
    /// counter's own depth again rather than from wherever it was stacked to yesterday.
    /// </summary>
    private static void ForgetDrainedStacks()
    {
        if (Stacked.Count == 0) return;

        List<IntPtr> drop = null;

        foreach (var pair in Stacked)
        {
            var counter = _stackOwners.TryGetValue(pair.Key, out var c) ? c : null;

            bool gone;
            try { gone = counter == null; } catch { gone = true; }

            // A counter mid-refill reads empty twice over: between the clear and the serve,
            // and again while the serve is in flight to the host and back. Forgetting the
            // stack in either window would let the refill land on the counter's own depth
            // instead of the stacked one, which is the whole point missed by a frame.
            bool refilling = Hold.TryGetValue(pair.Key, out float until) && Time.unscaledTime < until;

            bool dry = false;
            if (!gone && !refilling)
                try { dry = counter.ActiveServings <= 0; } catch { dry = true; }

            if (gone || dry) (drop ??= new List<IntPtr>()).Add(pair.Key);
        }

        if (drop == null) return;

        foreach (var key in drop) Forget(key);
    }

    /// <summary>
    /// Until when a counter's stack must be kept regardless of it reading empty.
    ///
    /// Serving is a round trip - ServerRpc to the host, ClientRpc back - and the counter
    /// reads zero for the whole of it. Without this the housekeeping below would tidy the
    /// stack away in that gap and the plate would land on the counter's own depth.
    /// </summary>
    private static readonly Dictionary<IntPtr, float> Hold = new();

    /// <summary>How long a serve is given to come back before its stack may be forgotten.</summary>
    private const float ServeHoldSeconds = 6f;

    // Kept alongside the stack so a drained counter can be recognised without a scene search.
    private static readonly Dictionary<IntPtr, ServiceCounter> _stackOwners = new();


    // ------------------------------------------------------- committing a stack

    private static bool _direct;

    private static ItemManager _itemManager;

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
            LogOnce($"Counter top-up: ItemManager lookup failed: {e.Message}");
        }

        return _itemManager;
    }

    /// <summary>
    /// The ways a counter might be persuaded to let go of what is on it, in the order they
    /// are tried.
    ///
    /// <c>ResetServing</c> is first because it is the measured answer: on 2026-08-27 it
    /// emptied a counter on the first attempt, where <c>ResetCounterServerRpc</c> had left
    /// all four portions sitting there two and a half seconds later. The rest are kept as
    /// fallbacks rather than deleted - none of these bodies can be read from the outside, and
    /// a counter in some state this has not met may well answer to a different one. The log
    /// names whichever step worked.
    ///
    /// <c>ReturnServing</c> is deliberately absent: it hands the leftovers back as a plate
    /// item, and spawning food is not something to do while guessing.
    /// </summary>
    private static readonly string[] ClearSteps =
    {
        nameof(ServiceCounter.ResetServing),
        "DrainByServing",
        nameof(ServiceCounter.ReleaseActivePlate),
        nameof(ServiceCounter.ResetCounter),
    };

    /// <summary>Runs one way of emptying the counter. Never throws out to the caller.</summary>
    private static void TryClearStep(ServiceCounter counter, int step)
    {
        try
        {
            switch (ClearSteps[step])
            {
                case nameof(ServiceCounter.ResetServing):
                    counter.ResetServing();
                    break;

                // The counter's own countdown, driven the way a customer drives it. Bounded
                // well above any real capacity so a miscount cannot spin here.
                case "DrainByServing":
                    for (int i = 0; i < 64 && counter.ActiveServings > 0; i++)
                        counter.DecreaseServing();
                    break;

                case nameof(ServiceCounter.ReleaseActivePlate):
                    counter.ReleaseActivePlate();
                    break;

                default:
                    counter.ResetCounter();
                    break;
            }
        }
        catch (Exception e)
        {
            LogOnce($"Counter top-up: {ClearSteps[step]} threw: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Whether the counter will now take a plate.</summary>
    private static bool ReadyForPlate(ServiceCounter counter)
    {
        try { if (!counter.HasActivePlate()) return true; } catch { }
        try { return counter.ActiveServings <= 0; } catch { return false; }
    }

    /// <summary>
    /// Empties the counter, trying each way in turn until one of them takes.
    ///
    /// Every step is an ordinary method call, so its effect is there by the time it returns
    /// and the whole walk costs one frame. An earlier version spread this over several
    /// seconds waiting for a ServerRpc round trip, which was only ever needed because the
    /// call it was waiting on did not do anything.
    /// </summary>
    private static bool ClearCounter(ServiceCounter counter)
    {
        if (ReadyForPlate(counter)) return true;

        // Every step here is now also hooked to take one plate off instead of emptying the
        // counter. Without this the two halves would work against each other: a clear meant to
        // make room for a serve would come back having removed one plate's worth and left the
        // rest sitting there.
        _clearing = true;

        try
        {
            for (int step = 0; step < ClearSteps.Length; step++)
            {
                TryClearStep(counter, step);

                if (!ReadyForPlate(counter)) continue;

                LogOnce($"Counter top-up: '{ClearSteps[step]}' is what empties a service counter " +
                        $"(tried {step + 1} of {ClearSteps.Length}).");
                return true;
            }
        }
        finally
        {
            _clearing = false;
        }

        LogOnce($"Counter top-up: none of {string.Join(", ", ClearSteps)} would empty the counter.");
        return false;
    }

    /// <summary>
    /// Deepens and empties the counter at the moment the player commits to the serve, then
    /// lets the game serve.
    ///
    /// This hangs off the dish picker's own button rather than off the item event, and that
    /// placement is the point. Serving the plate ourselves through
    /// <c>ItemManager.ServePlateToServiceTable</c> did put the food on the counter - measured,
    /// 0 to 8 portions - and left the plate sitting in the player's hand, because taking the
    /// plate is not part of that call. Whatever else <c>OnServeClicked</c> does, it does it
    /// here, so the honest thing is to prepare the counter and get out of the way.
    ///
    /// Clearing happens *after* the click for the same reason: it throws away what is on the
    /// counter, and doing that when the picker opens would cost the player their portions for
    /// merely looking. Nothing here is irreversible until they have chosen.
    /// </summary>

    /// <summary>
    /// Reports the counter as deep as what is on it plus one plate, honouring the ceiling.
    /// </summary>
    private static bool Deepen(ServiceCounter counter, int remaining, out int want)
    {
        want = 0;

        int baseline = BaselineOf(counter);
        if (baseline <= 0) return false;

        // Deliberately not clamped to the ceiling here. Every machine in a co-op game runs
        // this same sum and they have to reach the same number, and the ceiling is a local
        // setting - two players with different limits would deepen the same counter by
        // different amounts and disagree about how much food is on it. The ceiling stops the
        // serve being *offered* instead, in AllowTopUp, which is a local decision about a
        // local prompt and so is safe to differ.
        want = remaining + baseline;

        if (want <= remaining) return false;

        IntPtr key = counter.Pointer;
        LastStackAt[key] = Time.unscaledTime;

        // Set before clearing: the counter reads empty from here until the serve lands, and
        // the housekeeping must not tidy the stack away in that window.
        Stacked[key] = want;
        _stackOwners[key] = counter;
        Hold[key] = Time.unscaledTime + ServeHoldSeconds;

        return true;
    }

    private static void Forget(IntPtr key)
    {
        Stacked.Remove(key);
        Hold.Remove(key);
        LastStackAt.Remove(key);

        // Plates may still be waiting their turn on this counter, and they are owed their
        // portions whatever the depth says. Forgetting the owner here is what binned them:
        // a removal that takes a stack down to one plate's worth settles as "no longer
        // stacked", which is true of the depth and quite untrue of the queue. Only the depth
        // is being given up.
        if (Queued.TryGetValue(key, out var queue) && queue.Count > 0) return;

        _stackOwners.Remove(key);
    }

    private static bool PrepareStack(ServiceSlot __instance)
    {
        if (!Plugin.CounterTopUpEnabled.Value || __instance == null) return true;

        try
        {
            var counter = __instance.thisCounter;
            if (counter == null) return true;

            int remaining = counter.ActiveServings;
            if (remaining <= 0) return true;     // an ordinary serve; nothing to stack onto

            // One counter, one dish. Drawing the other rows as unservable is presentation, and
            // presentation is not a rule - this is where the rule lives, so a click that gets
            // through anyway still cannot change what a counter that is already serving serves.
            if (!IsSameDish(__instance.thisRecipe, counter))
            {
                Status = "quầy đang phục vụ món khác - mỗi quầy chỉ một món";
                LogOnce("Counter top-up: that counter is already serving another dish, and a " +
                        "counter serves one dish at a time. Topping up adds to what is on it; " +
                        "to change the dish, take what is there off first.");
                return false;
            }

            if (!Deepen(counter, remaining, out int want)) return true;

            // What is standing on the counter before the new plate lands on top of it, and how
            // much of what the counter holds is still its own - everything below that belongs
            // to plates already underneath it. See SettleStackedServes.
            var wasOn = TopOf(counter);
            int itsOwn = remaining - PortionsQueued(counter.Pointer);

            if (!ClearCounter(counter))
            {
                Forget(counter.Pointer);
                Status = "không dọn được quầy - xem log";
                return true;
            }

            LetGoOf(wasOn, counter, itsOwn);

            _lastAllowed = counter;
            _lastAllowedAt = Time.unscaledTime;

            Status = $"đang cộng dồn: {remaining} + {want - remaining} = {want} suất";
            LogOnce($"Counter top-up: stacking a counter holding {remaining} portions up to {want} " +
                    "from the dish picker; the game's own serve takes it from here.");
        }
        catch (Exception e)
        {
            LogOnce($"Counter top-up: preparing the stack failed: {e.GetType().Name}: {e.Message}");
        }

        return true;
    }


    // --------------------------------------------------- one plate at a time, in turn

    /// <summary>
    /// A plate waiting its turn on a stacked counter, and the counter's verdict on it.
    ///
    /// The verdict is kept as numbers rather than as the <c>CritizationResult</c> the counter
    /// handed out, because there is no telling from outside whether the counter makes a new
    /// one per serve or edits the one it has.
    /// </summary>
    private sealed class Layer
    {
        public Plate Plate;
        public int Portions;

        public bool HasRating;
        public float Bake, Spice, Ing, Timing, Overall;
    }

    /// <summary>Plates queued behind the one a counter is currently serving from.</summary>
    private static readonly Dictionary<IntPtr, List<Layer>> Queued = new();

    /// <summary>The last thing said about each queue, so it is only said when it changes.</summary>
    private static readonly Dictionary<IntPtr, string> _queueState = new();

    // ------------------------------------------- mending a counter on the other screen

    private static float _mendedAt = -1f;

    /// <summary>How often to look for a counter left holding a plate that has walked off.</summary>
    private const float MendEvery = 0.5f;

    /// <summary>How close to the serving spot the replacement plate has to be, in metres.</summary>
    private const float MendReach = 0.6f;

    /// <summary>
    /// Points a counter back at the plate that is actually on it, wherever the removal happened.
    ///
    /// The hand-back is the acting player's to make - it spawns a plate for them, so it must
    /// run on their machine and nowhere else - and the machine that makes it knows which plate
    /// the game left behind, because the callback says so. **No other machine is told.** Their
    /// copy of the counter goes on pointing at the plate that walked off, which is the fault
    /// that took four rounds to find, present on every screen except the one it was fixed on.
    ///
    /// Passing the answer along would mean a message of our own racing the spawn it describes.
    /// There is no need: the broken state says what it is. A counter with portions left,
    /// holding a plate that is in somebody's hands, is a counter whose plate has gone - and the
    /// plate standing on its serving spot is the one it should have. So each machine mends its
    /// own view from what it can see, which is the same agree-by-arithmetic that the portions
    /// and the pile already use.
    ///
    /// It costs nothing on the machine that did the removal: the state is already right there,
    /// so the check fails and it does no work.
    /// </summary>
    private static void MendCounters()
    {
        if (!Plugin.CounterTopUpEnabled.Value) return;
        if (_mendedAt >= 0f && Time.unscaledTime - _mendedAt < MendEvery) return;

        _mendedAt = Time.unscaledTime;

        try
        {
            var counters = UnityEngine.Object.FindObjectsByType<ServiceCounter>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            if (counters == null) return;

            for (int i = 0; i < counters.Length; i++)
            {
                MendCounter(counters[i]);
                ReportCrowding(counters[i]);
            }
        }
        catch (Exception e)
        {
            LogOnce($"Counter top-up: could not look over the counters: {e.Message}");
        }
    }

    /// <summary>
    /// Observation only: says so in the log when more than one plate is on show at a counter's
    /// serving spot, and what this machine believes about each. Two plates on show is the
    /// fault this whole pile exists to prevent, and the line is what tells the two machines'
    /// stories apart.
    /// </summary>
    private static void ReportCrowding(ServiceCounter counter)
    {
        try
        {
            if (counter == null || counter.ActiveServings <= 0) return;

            // Only counters this mod has stacked: a scene search per counter twice a second is
            // not free, and an unstacked counter is the game's own business.
            if (!Queued.ContainsKey(counter.Pointer) && !Stacked.ContainsKey(counter.Pointer)) return;

            var spot = ServeTransform(counter);
            if (spot == null) return;

            var plates = UnityEngine.Object.FindObjectsByType<Plate>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            if (plates == null) return;

            Top.TryGetValue(counter.Pointer, out var top);
            Plate active = null;
            try { active = counter.ActivePlate; } catch { }

            int shown = 0;
            var parts = new System.Text.StringBuilder();

            for (int i = 0; i < plates.Length; i++)
            {
                var p = plates[i];
                var item = p == null ? null : p.thisItem;
                if (item == null || item.isHanded || Hidden.ContainsKey(item.Pointer)) continue;
                if ((item.transform.position - spot.position).sqrMagnitude > MendReach * MendReach) continue;

                shown++;
                parts.Append(shown == 1 ? "" : "; ")
                     .Append(top != null && top.Pointer == p.Pointer ? "top" : "not top")
                     .Append(active != null && active.Pointer == p.Pointer ? ", the counter's own" : "")
                     .Append(IsQueuedPlate(item.Pointer) ? ", queued" : "");
            }

            if (shown < 2) return;

            LogOnce($"Counter top-up: {shown} plates on show at one counter as " +
                    $"{(IsHostSide() ? "host" : "client")} ({counter.ActiveServings} portions, " +
                    $"{PortionsQueued(counter.Pointer)} queued): {parts}.");
        }
        catch { }
    }

    private static void MendCounter(ServiceCounter counter)
    {
        try
        {
            if (counter == null || counter.ActiveServings <= 0) return;

            var plate = counter.ActivePlate;
            if (plate == null) return;      // this machine never knew; see PlateOnCounter

            var item = plate.thisItem;
            if (item == null || !item.isHanded) return;      // nothing wrong with this counter

            var spot = ServeTransform(counter);
            if (spot == null) return;

            var standing = PlateStandingOn(spot.position, plate);
            if (standing == null) return;

            var replacement = standing.GetComponent<Plate>();
            if (replacement == null) return;

            counter.ActivePlate = replacement;
            SetTop(counter, replacement);
            StandPlateOnCounter(counter, standing);
            RefreshCard(counter);

            LogOnce("Counter top-up: this counter was left pointing at a plate somebody is " +
                    "carrying, so it was pointed at the one standing on its serving spot. That " +
                    "is a removal done on the other machine: the plate it hands over is spawned " +
                    "for the player who asked, and only their machine is told which plate the " +
                    "counter kept.");
        }
        catch (Exception e)
        {
            LogOnce($"Counter top-up: a counter could not be mended: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// The plate sitting on a counter's serving spot, if there is one that is not the plate the
    /// counter already believes it has and is not waiting its turn underneath.
    /// </summary>
    private static ItemIdentifier PlateStandingOn(Vector3 spot, Plate notThisOne)
    {
        ItemIdentifier best = null;
        float bestSqr = MendReach * MendReach;

        try
        {
            var plates = UnityEngine.Object.FindObjectsByType<Plate>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            if (plates == null) return null;

            for (int i = 0; i < plates.Length; i++)
            {
                var candidate = plates[i];
                if (candidate == null) continue;
                if (notThisOne != null && candidate.Pointer == notThisOne.Pointer) continue;

                var item = candidate.thisItem;
                if (item == null || item.isHanded) continue;

                // A plate parked under the pile is standing in the same place, and it is not
                // the one being served from - taking it would promote it early and lose the
                // portions still owed to the plate above it.
                if (Hidden.ContainsKey(item.Pointer) || IsQueuedPlate(item.Pointer)) continue;

                float sqr = (item.transform.position - spot).sqrMagnitude;
                if (sqr < bestSqr) { bestSqr = sqr; best = item; }
            }
        }
        catch { }

        return best;
    }

    /// <summary>
    /// The plate the counter is serving from, as far as this machine can tell.
    ///
    /// The counter's own <c>ActivePlate</c> first. But that is only filled in on the machine
    /// whose player served the plate: everywhere else the counter holds the portions with no
    /// plate recorded against them (measured on a host, for a client's serve -
    /// <c>HasActivePlate()</c> false with four portions on it). There the plate is still
    /// physically standing on the serving spot, so that is where it is looked for - skipping
    /// anything carried, and anything already parked in the pile.
    /// </summary>
    /// <summary>
    /// The plate on top of each counter's pile, by counter, as this mod saw it arrive.
    ///
    /// The counter's own <c>ActivePlate</c> cannot be trusted for this in co-op: it is filled in
    /// only on the machine whose player served, and on the others it is left empty or left on
    /// the plate that was there before (measured on a client, for a host's serve - the settle
    /// found the counter "still on the plate it had before"). But <c>ServePlate</c> runs on
    /// every machine and is handed the plate being served, so every machine can know the top
    /// of the pile first-hand, which is what this is.
    /// </summary>
    private static readonly Dictionary<IntPtr, Plate> Top = new();

    private static void SetTop(ServiceCounter counter, Plate plate)
    {
        try
        {
            if (counter == null) return;
            if (plate == null) Top.Remove(counter.Pointer);
            else Top[counter.Pointer] = plate;
        }
        catch { }
    }

    /// <summary>
    /// The plate the counter is serving from: the recorded top if it is still standing there,
    /// otherwise whatever <see cref="PlateOnCounter"/> can find.
    /// </summary>
    private static Plate TopOf(ServiceCounter counter, Plate notThisOne = null)
    {
        try
        {
            if (counter != null && Top.TryGetValue(counter.Pointer, out var plate))
            {
                bool usable;
                try
                {
                    var item = plate == null ? null : plate.thisItem;
                    usable = item != null && !item.isHanded && !Hidden.ContainsKey(item.Pointer)
                             && (notThisOne == null || plate.Pointer != notThisOne.Pointer);
                }
                catch { usable = false; }

                if (usable) return plate;
            }
        }
        catch { }

        return PlateOnCounter(counter, notThisOne);
    }

    private static Plate PlateOnCounter(ServiceCounter counter, Plate notThisOne = null)
    {
        try
        {
            var own = counter.ActivePlate;
            if (own != null && (notThisOne == null || own.Pointer != notThisOne.Pointer))
            {
                var item = own.thisItem;
                if (item != null && !item.isHanded) return own;
            }
        }
        catch { }

        try
        {
            var spot = ServeTransform(counter);
            if (spot == null) return null;

            var standing = PlateStandingOn(spot.position, notThisOne);
            if (standing == null) return null;

            var plate = standing.GetComponent<Plate>();
            if (plate != null)
                LogOnce("Counter top-up: this machine's copy of the counter did not know which " +
                        "plate it was holding, so the plate standing on its serving spot was used.");
            return plate;
        }
        catch { return null; }
    }

    /// <summary>Plates a service counter is currently serving from, by item, refreshed lazily.</summary>
    private static readonly HashSet<IntPtr> _servingPlates = new();
    private static float _servingPlatesAt = -1f;

    /// <summary>How long a look round for serving counters is good for.</summary>
    private const float ServingPlateScan = 0.5f;

    private static void RefreshServingPlates()
    {
        if (_servingPlatesAt >= 0f && Time.unscaledTime - _servingPlatesAt < ServingPlateScan) return;

        _servingPlatesAt = Time.unscaledTime;
        _servingPlates.Clear();

        try
        {
            var counters = UnityEngine.Object.FindObjectsByType<ServiceCounter>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            if (counters == null) return;

            for (int i = 0; i < counters.Length; i++)
            {
                var c = counters[i];
                if (c == null) continue;

                Plate plate = null;
                try { plate = c.ActivePlate; } catch { }
                if (plate == null) continue;

                ItemIdentifier item = null;
                try { item = plate.thisItem; } catch { }
                if (item != null) _servingPlates.Add(item.Pointer);
            }
        }
        catch (Exception e)
        {
            LogOnce($"Counter top-up: could not look round for serving counters: {e.Message}");
        }
    }

    /// <summary>
    /// Whether an item is a plate a service counter is serving from, or one waiting its turn
    /// underneath.
    ///
    /// Asked by anything that lets the player take hold of items in bulk. A counter's plate is
    /// not loose scenery - the game gives it out through its own take-a-serving interaction, so
    /// picking it up by any other route walks off with the food a counter is meant to be
    /// handing out. The plates parked under a stack are worse still: invisible, and owed to
    /// customers who have not been served yet.
    ///
    /// Deliberately not gated on the top-up option. A counter's own plate is off limits whether
    /// or not anything has been stacked onto it.
    /// </summary>
    public static bool IsOnAServiceCounter(ItemIdentifier item)
    {
        if (item == null) return false;

        try
        {
            RefreshServingPlates();

            // Up through the parents, not just the item itself. "The dish on the counter" is
            // the food, and the food is separate items sitting on the plate - each with its own
            // identity, its own name and its own collider, which is exactly what a search of
            // the scene turns up. Guarding only the plate left every ingredient on it free to
            // be marked and carried off, which is the same theft by a shorter route.
            var node = item.transform;

            for (int depth = 0; node != null && depth < 8; depth++, node = node.parent)
            {
                ItemIdentifier at = null;
                try { at = node.GetComponent<ItemIdentifier>(); } catch { }
                if (at == null) continue;

                IntPtr key = at.Pointer;

                // Parked out of sight with its renderers off, waiting for the plate above it to
                // run out. Known for certain, and cheaper than any search.
                if (Hidden.ContainsKey(key)) return true;

                if (_servingPlates.Contains(key)) return true;

                if (IsQueuedPlate(key)) return true;
            }

            return false;
        }
        catch { return false; }
    }

    /// <summary>Whether an item is one of the plates waiting underneath a stacked counter.</summary>
    private static bool IsQueuedPlate(IntPtr key)
    {
        foreach (var pair in Queued)
        {
            var queue = pair.Value;

            for (int i = 0; i < queue.Count; i++)
            {
                try
                {
                    var plate = queue[i].Plate;
                    if (plate == null) continue;

                    var owned = plate.thisItem;
                    if (owned != null && owned.Pointer == key) return true;
                }
                catch { }
            }
        }

        return false;
    }

    /// <summary>How many of a counter's portions belong to plates underneath the one on top.</summary>
    private static int PortionsQueued(IntPtr key)
    {
        if (!Queued.TryGetValue(key, out var queue)) return 0;

        int total = 0;
        for (int i = 0; i < queue.Count; i++) total += queue[i].Portions;

        return total;
    }

    /// <summary>The renderers turned off to hide a waiting plate, so exactly those come back.</summary>
    private static readonly Dictionary<IntPtr, List<Renderer>> Hidden = new();

    /// <summary>Reads the counter's verdict on what it is serving into a layer.</summary>
    private static void ReadRating(ServiceCounter counter, Layer into)
    {
        try
        {
            var results = counter.TasteResults;
            var rating = results == null ? null : results.foodRating;
            if (rating == null) return;

            into.HasRating = true;
            into.Bake = rating.bakeScore;
            into.Spice = rating.spiceScore;
            into.Ing = rating.ingredientScore;
            into.Timing = rating.timingScore;
            into.Overall = rating.overallScore;
        }
        catch { }
    }

    /// <summary>Writes a layer's verdict back onto the counter as that plate comes up.</summary>
    private static void ApplyRating(ServiceCounter counter, Layer from, ItemIdentifier plate)
    {
        if (!from.HasRating) return;

        float was = -1f;

        try
        {
            was = counter.TasteRating;

            var results = counter.TasteResults;
            var rating = results == null ? null : results.foodRating;

            if (rating != null) rating.SetRating(from.Bake, from.Spice, from.Ing, from.Timing, from.Overall);
        }
        catch (Exception e)
        {
            LogOnce($"Counter top-up: a waiting plate's verdict could not be written back: {e.Message}");
        }

        // Writing the numbers is not the same as the card above the counter saying them. The
        // rating on that card is the game's, sent round with SyncRating and drawn by the HUD's
        // own refresh, so a verdict that only lives in TasteResults is one nobody can see - and
        // the counter goes on showing what the plate before it rated.
        try
        {
            // Promotion happens on every machine from its own arithmetic, so the rating would
            // be sent round once per player. One statement is enough, and the host's is the
            // one everyone would end up with anyway.
            if (plate != null && IsHostSide()) counter.SyncRatingServerRpc(plate.itemIndex, from.Overall);
        }
        catch (Exception e)
        {
            LogOnce($"Counter top-up: the counter's rating could not be sent round: " +
                    $"{e.GetType().Name}: {e.Message}");
        }

        RefreshCard(counter);

        try
        {
            LogOnce($"Counter top-up: the card above the counter went from {was:0.00} to " +
                    $"{counter.TasteRating:0.00} as the plate underneath came up (its own verdict " +
                    $"was {from.Overall:0.00}).");
        }
        catch { }
    }

    /// <summary>Makes the card above the counter redraw itself.</summary>
    private static void RefreshCard(ServiceCounter counter)
    {
        try
        {
            var hud = counter.serviceHUD;
            if (hud == null) return;

            hud.UpdateRecipeHUD();
        }
        catch (Exception e)
        {
            LogOnce($"Counter top-up: the counter's card would not redraw: " +
                    $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// Puts a plate away until its turn comes.
    ///
    /// Colliders off so nobody can pick it up or walk into it, pinned so it stays put, and its
    /// renderers switched off so it is not seen - it waits exactly where the serve left it,
    /// under the plate the counter is working through.
    ///
    /// It is deliberately **not** moved out of the world to hide it. Dropping items far below
    /// the floor is how a game is invited to tidy them away as having fallen out of bounds, and
    /// a waiting plate quietly destroyed would put the counter back to serving one dish for the
    /// whole stack - the very fault being fixed. Hiding is a local, reversible thing; moving is
    /// not.
    ///
    /// Its food goes on ageing while it waits, which is the honest answer for food that is
    /// sitting on a counter.
    /// </summary>
    private static bool Park(ItemIdentifier item)
    {
        try
        {
            // Locally, not through the RPC: every machine parks its own copy of this plate, so
            // asking the network to do it would be the same thing said as many times as there
            // are players. Same for the kinematic flag and the renderers below.
            try { item.EnableColliders(false, false); }
            catch (Exception e) { LogOnce($"Counter top-up: a waiting plate kept its colliders: {e.Message}"); }

            try { item.SetKinematic(true); } catch { }

            Hide(item);
            return true;
        }
        catch (Exception e)
        {
            LogOnce($"Counter top-up: a plate could not be put away to wait its turn: " +
                    $"{e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    /// <summary>Switches off the renderers of a plate and its food, remembering which.</summary>
    private static void Hide(ItemIdentifier item)
    {
        try
        {
            var found = item.GetComponentsInChildren<Renderer>(true);
            if (found == null) return;

            var turned = new List<Renderer>();

            for (int i = 0; i < found.Length; i++)
            {
                var r = found[i];
                if (r == null || !r.enabled) continue;

                r.enabled = false;
                turned.Add(r);
            }

            Hidden[item.Pointer] = turned;

            LogOnce($"Counter top-up: a plate waiting its turn was hidden where it stands " +
                    $"({turned.Count} parts), rather than moved out of the world where the game " +
                    "might tidy it away.");
        }
        catch (Exception e)
        {
            LogOnce($"Counter top-up: a waiting plate could not be hidden: " +
                    $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Switches back on exactly the renderers <see cref="Hide"/> turned off.</summary>
    private static void Show(ItemIdentifier item)
    {
        try
        {
            IntPtr key = item.Pointer;
            if (!Hidden.TryGetValue(key, out var turned)) return;

            Hidden.Remove(key);

            for (int i = 0; i < turned.Count; i++)
            {
                var r = turned[i];
                try { if (r != null) r.enabled = true; } catch { }
            }
        }
        catch (Exception e)
        {
            LogOnce($"Counter top-up: a waiting plate could not be shown again: " +
                    $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Brings a waiting plate back out and gives the counter to it.</summary>
    private static bool Promote(ServiceCounter counter, Layer layer)
    {
        try
        {
            bool gone;
            try { gone = layer.Plate == null; } catch { gone = true; }
            if (gone) return false;

            var item = layer.Plate.thisItem;
            if (item == null) return false;

            Show(item);

            try { item.EnableColliders(true, false); }
            catch (Exception e) { LogOnce($"Counter top-up: a plate came back without its colliders: {e.Message}"); }

            counter.ActivePlate = layer.Plate;
            SetTop(counter, layer.Plate);
            StandPlateOnCounter(counter, item);
            ApplyRating(counter, layer, item);

            return true;
        }
        catch (Exception e)
        {
            LogOnce($"Counter top-up: a waiting plate could not be brought back: " +
                    $"{e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    /// <summary>Takes a plate out of the world, if it is not in anybody's hands.</summary>
    private static void Discard(Plate plate)
    {
        try
        {
            bool gone;
            try { gone = plate == null; } catch { gone = true; }
            if (gone) return;

            var item = plate.thisItem;
            if (item == null || item.isHanded) return;

            // Destroying an item is genuinely networked, and every machine reaches this point
            // for its own copy of the same spent plate - so one of them asks and the rest let
            // the answer come to them. The host is the one that asks, because it is the one
            // whose answer everybody already trusts.
            // Everyone else hides their copy straight away, so the spent plate does not stand
            // next to the one that replaced it while the host's destroy is on its way.
            if (!IsHostSide()) { Hide(item); return; }

            var manager = ResolveItemManager();
            if (manager == null) return;

            manager.DestroyItemServerRpc(item.itemIndex);
        }
        catch (Exception e)
        {
            LogOnce($"Counter top-up: a spent plate could not be taken away: " +
                    $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// Hands each plate its own share of the counter, newest first.
    ///
    /// Stacking works by clearing the counter and letting the game refill it, so on its own
    /// the last plate served would decide what every portion is - serve nothing three times
    /// and something good once, and the counter hands out four plates' worth of good food.
    /// Capping the stack at its worst plate was tried and is no better the other way round:
    /// it makes a good dish count as a bad one because of what came before it.
    ///
    /// So neither. Each plate keeps its own food and serves its own portions. The plate on the
    /// counter is the one being drawn from; the ones underneath wait their turn, and as each is
    /// used up the next comes back up with its own ingredients and its own verdict.
    ///
    /// **A pile, not a queue.** The plate served last is the one on top and the first to be
    /// taken from, which is how plates on a counter actually behave and how the player expects
    /// to get them back: serve two and remove twice, and the second one comes off first.
    ///
    /// The arithmetic needs nothing beyond the portion count the game already keeps. Whatever
    /// is waiting adds up to a number of portions; the plate on the counter is finished the
    /// moment the counter is down to that number, because everything below it belongs to the
    /// others. A portion taken by a customer, a plate's worth taken off by a removal and a
    /// stack drained by a whole service all come out right, and none of them has to be
    /// noticed separately.
    /// </summary>
    private static void ServeEachPlateInTurn()
    {
        if (Queued.Count == 0) return;

        List<IntPtr> finished = null;

        foreach (var pair in Queued)
        {
            var queue = pair.Value;
            var counter = _stackOwners.TryGetValue(pair.Key, out var c) ? c : null;

            bool gone;
            try { gone = counter == null; } catch { gone = true; }

            if (gone || queue.Count == 0)
            {
                if (gone) foreach (var layer in queue) Discard(layer.Plate);
                (finished ??= new List<IntPtr>()).Add(pair.Key);
                continue;
            }

            int held;
            try { held = counter.ActiveServings; }
            catch { (finished ??= new List<IntPtr>()).Add(pair.Key); continue; }

            int behind = 0;
            for (int i = 0; i < queue.Count; i++) behind += queue[i].Portions;

            // Says where the stack has got to, once per number it passes through, so a queue
            // that is not coming down can be told from one that never got built.
            string reached = $"{held} portions with {behind} still waiting behind";

            if (!_queueState.TryGetValue(pair.Key, out string was) || was != reached)
            {
                _queueState[pair.Key] = reached;
                LogOnce($"Counter top-up: the counter is at {reached}.");
            }

            // Cleared out from under the queue - the dish was taken off entirely, or the day
            // ended. The plates waiting have nothing left to be served as.
            bool refilling = Hold.TryGetValue(pair.Key, out float until) && Time.unscaledTime < until;

            if (!refilling && held <= 0)
            {
                foreach (var layer in queue) Discard(layer.Plate);

                LogOnce("Counter top-up: the counter was emptied while plates were still waiting " +
                        "their turn, so they were taken away with it.");

                (finished ??= new List<IntPtr>()).Add(pair.Key);
                continue;
            }

            // Still portions on the counter that belong to the plate standing on it.
            if (held > behind) continue;

            var next = queue[0];
            queue.RemoveAt(0);

            // Read before the promote, which points the counter at the plate coming up.
            var spent = TopOf(counter);

            if (!Promote(counter, next))
            {
                LogOnce("Counter top-up: the next plate in the stack was not there any more, so " +
                        "the counter goes on with the one it has.");
                continue;
            }

            // Only once the next plate is safely on the counter - a plate destroyed before its
            // replacement is up would leave the counter serving from nothing.
            Discard(spent);

            LogOnce($"Counter top-up: the plate on the counter was used up at {held} portions, so " +
                    "the plate underneath came back up with its own food and its own rating. Each " +
                    "plate serves its own share, the newest first.");

            if (queue.Count == 0) (finished ??= new List<IntPtr>()).Add(pair.Key);
        }

        if (finished == null) return;

        foreach (var key in finished)
        {
            Queued.Remove(key);
            _queueState.Remove(key);
        }
    }

    // ------------------------------------------------- settling a stacked serve

    /// <summary>A stacked serve whose plates are waiting to be sorted out.</summary>
    private sealed class Leftover
    {
        public Plate Plate;
        public ServiceCounter Counter;
        public float DueAt;
        public int Portions;

        /// <summary>What the counter's dish rated before the serve landed on it.</summary>
        public bool HadRating;
        public float Bake, Spice, Ing, Timing, Overall;
    }

    private static readonly List<Leftover> Leftovers = new();

    /// <summary>
    /// How long to let the serve land before sorting the two plates out.
    ///
    /// The serve is a round trip - the counter does not have its new plate until the host has
    /// said so - and both questions that matter, which plate the counter ended up on and what
    /// it made of it, can only be asked once it has.
    /// </summary>
    private const float LeftoverSettle = 2f;

    /// <summary>Notes the plate a stacked serve is about to land on top of, and its verdict.</summary>
    private static void LetGoOf(Plate plate, ServiceCounter counter, int portions)
    {
        if (plate == null || Leftovers.Count >= 6) return;

        var l = new Leftover
        {
            Plate = plate,
            Counter = counter,
            DueAt = Time.unscaledTime + LeftoverSettle,
            Portions = portions,
        };

        var read = new Layer();
        ReadRating(counter, read);

        l.HadRating = read.HasRating;
        l.Bake = read.Bake;
        l.Spice = read.Spice;
        l.Ing = read.Ing;
        l.Timing = read.Timing;
        l.Overall = read.Overall;

        Leftovers.Add(l);
    }

    /// <summary>
    /// Puts the counter back on the plate it was serving, and sends the newly served one to
    /// the back of the queue.
    ///
    /// The game's refill always leaves the newest plate on the counter, which is the wrong way
    /// round: the plate already there has portions left that belong to it, and they should be
    /// served as what they are before anything else comes out. So the counter is put back onto
    /// its own plate - food, verdict and all - and the new one waits its turn under the floor
    /// with the portions it brought.
    ///
    /// Only the machine whose player clicked does this. The moves and the removals it makes are
    /// the game's own networked ones, so they reach every screen; asking for them from both
    /// ends would be asking twice.
    /// </summary>
    private static void SettleStackedServes()
    {
        if (Leftovers.Count == 0) return;

        float now = Time.unscaledTime;

        for (int i = Leftovers.Count - 1; i >= 0; i--)
        {
            var l = Leftovers[i];
            if (now < l.DueAt) continue;

            Leftovers.RemoveAt(i);

            try
            {
                bool gone;
                try { gone = l.Plate == null || l.Counter == null; } catch { gone = true; }
                if (gone) continue;

                var served = TopOf(l.Counter, l.Plate);

                if (served == null || served.Pointer == l.Plate.Pointer)
                {
                    LogOnce("Counter top-up: the counter is still on the plate it had before the " +
                            "serve, so there is nothing to put in the queue.");
                    continue;
                }

                var older = l.Plate.thisItem;

                if (older == null || older.isHanded)
                {
                    LogOnce("Counter top-up: the plate that was on the counter has been picked up, " +
                            "so the newly served one simply takes over.");
                    continue;
                }

                // The plate that was on the counter, with the share it still had left and the
                // verdict the counter had passed on it - both read before the serve landed.
                var waiting = new Layer
                {
                    Plate = l.Plate,
                    Portions = l.Portions,
                    HasRating = l.HadRating,
                    Bake = l.Bake, Spice = l.Spice, Ing = l.Ing,
                    Timing = l.Timing, Overall = l.Overall,
                };

                if (!Park(older)) continue;

                // The newly served plate keeps the counter, which is where the game's own refill
                // has already put it, along with the verdict it has just passed on it. Nothing
                // to move and nothing to write back.
                IntPtr key = l.Counter.Pointer;
                if (!Queued.TryGetValue(key, out var queue)) Queued[key] = queue = new List<Layer>();

                // Onto the top of the pile, not the end of it: the plate served last is the one
                // taken from first, the way plates on a counter actually behave.
                queue.Insert(0, waiting);

                _stackOwners[key] = l.Counter;

                LogOnce($"Counter top-up: the plate that was on the counter went under the new one " +
                        $"with its {l.Portions} portions still to serve. The newest plate is served " +
                        "from first and the ones below it come back up as it runs out, each with " +
                        "its own food and its own rating.");

                Status = $"chồng suất: mỗi đĩa giữ chất lượng riêng ({queue.Count + 1} đĩa)";
            }
            catch (Exception e)
            {
                LogOnce($"Counter top-up: could not queue a stacked serve: " +
                        $"{e.GetType().Name}: {e.Message}");
            }
        }
    }

    // ------------------------------------------------ dish picker, as a fallback

    /// <summary>
    /// Tells the picker the dish is not already served, so it draws the row as servable.
    ///
    /// Only ever on a counter that is holding portions, which is the only case the game gets
    /// wrong for our purposes - everywhere else the flag is left exactly as the game set it.
    /// </summary>
    private static void SlotServable(Recipe recipe, ServiceCounter counter, ref bool isServed)
    {
        if (!Plugin.CounterTopUpEnabled.Value || counter == null) return;

        try
        {
            // An empty counter is the game's own business: it may be given any dish on today's
            // menu, exactly as it always could.
            if (counter.ActiveServings <= 0) return;

            if (IsSameDish(recipe, counter))
            {
                if (!isServed) return;

                isServed = false;
                LogOnce("Counter top-up: the dish picker had marked the counter's own dish as " +
                        "already served; drawing it as servable so the row can be clicked.");
                return;
            }

            // Opening the picker on a counter that is already serving is this mod's doing, and
            // it brought the rest of the menu with it. A counter serves one dish at a time -
            // topping up is adding to what is there, not changing it - so every other row is
            // drawn as it would be on a counter that cannot take it.
            if (isServed) return;

            isServed = true;
            LogOnce("Counter top-up: a counter that is already serving takes only more of the " +
                    "same dish, so the other dishes on the menu are drawn as unservable.");
        }
        catch { }
    }

    /// <summary>
    /// Whether a picker row is the dish the counter is already serving.
    ///
    /// Recipes travel by name across the network, so that is what they are compared by, with
    /// the reference checked first for the ordinary case. A counter with no dish set, or
    /// anything unreadable, answers yes: this decides whether to *refuse* a serve, and it must
    /// never become a new way for one to fail.
    /// </summary>
    private static bool IsSameDish(Recipe recipe, ServiceCounter counter)
    {
        try
        {
            Recipe active = null;

            try { active = counter.activeRecipe; } catch { }
            if (active == null) { try { active = counter.GetActiveRecipe(); } catch { } }

            if (active == null) return true;
            if (recipe == null) return false;
            if (recipe.Pointer == active.Pointer) return true;

            return string.Equals(recipe.recipeName, active.recipeName,
                                 StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    /// <summary>
    /// Makes the drawn row actually take a click.
    ///
    /// Presentation only - it switches on the button and shows the servable half of the row.
    /// If the game refuses the serve further down, this changes nothing; it only removes the
    /// case where the click never arrives at all.
    /// </summary>
    private static void SlotClickable(ServiceSlot __instance, Recipe recipe, ServiceCounter counter)
    {
        if (__instance == null) return;

        try
        {
            // The picker's rows are reused from one counter to the next, and the game only
            // switches the row's pictures - it never touches the buttons' own interactable
            // flag, because it never turns that off. So a button this mod switched off for a
            // counter that was already serving another dish stayed off when the same row was
            // drawn for an empty counter, and clicking the dish there did nothing at all. Every
            // row is handed back its buttons first; only a busy counter takes them away again.
            if (!Plugin.CounterTopUpEnabled.Value || counter == null || counter.ActiveServings <= 0)
            {
                GiveBackButtons(__instance);
                return;
            }

            bool same = IsSameDish(recipe, counter);

            if (__instance.servableGO != null) __instance.servableGO.SetActive(same);
            if (__instance.unservableGO != null) __instance.unservableGO.SetActive(!same);

            var buttons = __instance.GetComponentsInChildren<UnityEngine.UI.Selectable>(true);
            if (buttons == null) return;

            for (int i = 0; i < buttons.Length; i++)
            {
                var b = buttons[i];
                if (b == null) continue;

                if (same)
                {
                    // The counter's own dish: the game draws it as already served and locks it,
                    // and topping up is exactly a click on it.
                    if (!b.interactable) { b.interactable = true; Touched.Add(b.Pointer); }
                    continue;
                }

                if (!b.interactable) continue;

                b.interactable = false;
                Touched.Add(b.Pointer);
            }
        }
        catch (Exception e)
        {
            LogOnce($"Counter top-up: could not unlock the picker row: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// Picker buttons whose interactable flag this mod changed - switched off for another
    /// dish, or forced on for the counter's own. Only these are ever handed back; a button the
    /// game set for its own reasons and this mod never touched is left alone.
    /// </summary>
    private static readonly HashSet<IntPtr> Touched = new();

    /// <summary>
    /// Hands a reused row's buttons back to the game's own verdict for the dish it now shows,
    /// read off the row's servable picture - which the game has just set, since this runs
    /// after <c>SetRecipe</c>.
    /// </summary>
    private static void GiveBackButtons(ServiceSlot slot)
    {
        if (Touched.Count == 0) return;

        try
        {
            bool servable = true;
            try { if (slot.servableGO != null) servable = slot.servableGO.activeSelf; } catch { }

            var buttons = slot.GetComponentsInChildren<UnityEngine.UI.Selectable>(true);
            if (buttons == null) return;

            for (int i = 0; i < buttons.Length; i++)
            {
                var b = buttons[i];
                if (b == null || !Touched.Remove(b.Pointer)) continue;

                b.interactable = servable;
                LogOnce("Counter top-up: a picker row reused from a counter that was already " +
                        $"serving was handed back to the game's own state (servable = {servable}).");
            }
        }
        catch (Exception e)
        {
            LogOnce($"Counter top-up: could not switch a picker row back on: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Where a plate goes on this counter, however the counter names it.</summary>
    private static Transform ServeTransform(ServiceCounter counter)
    {
        try
        {
            var t = counter.ServiceTransform;
            if (t != null) return t;
        }
        catch { }

        try { return counter.servicePlateTransform; }
        catch { return null; }
    }

    // ------------------------------------------------------------------- aim

    private static Camera _cam;

    // The check runs once per frame per item event, and holding two plates means two events
    // asking the same question about the same counter. One ray per frame answers all of them.
    private static int _rayFrame = -1;
    private static ServiceCounter _rayCounter;

    /// <summary>The service counter under the crosshair, or null.</summary>
    private static ServiceCounter AimedCounter()
    {
        if (_rayFrame == Time.frameCount) return _rayCounter;

        _rayFrame = Time.frameCount;
        _rayCounter = null;

        try
        {
            if (_cam == null) _cam = Camera.main;
            if (_cam == null) return null;

            var t = _cam.transform;
            if (!Physics.Raycast(t.position, t.forward, out var hit, AimRange, ~0,
                                 QueryTriggerInteraction.Ignore))
                return null;

            // The collider struck is a lid, a shelf or the counter's serving surface rather
            // than the object carrying the component, so walk up to find it.
            for (var node = hit.transform; node != null; node = node.parent)
            {
                var counter = node.GetComponent<ServiceCounter>();
                if (counter != null) { _rayCounter = counter; break; }
            }
        }
        catch (Exception e)
        {
            LogOnce($"Counter top-up: aim ray failed: {e.GetType().Name}: {e.Message}");
        }

        return _rayCounter;
    }

    // ------------------------------------------------------- watching a serve

    /// <summary>The counter the gate last said yes to, so a serve can be attributed to it.</summary>
    private static ServiceCounter _lastAllowed;
    private static float _lastAllowedAt;

    private sealed class Pending
    {
        public ServiceCounter Counter;
        public int Before;
        public int Max;
        public float DueAt;
        public bool WasTopUp;
    }

    private static readonly List<Pending> Waiting = new();

    /// <summary>
    /// How long to wait before reading the count back.
    ///
    /// Reading it in a postfix would be reading it too early: the serve spawns the counter's
    /// plate through the item manager and waits for it (<c>WaitForServePlateFromRelation</c>),
    /// so the number right after the call is the number from before it.
    /// </summary>
    private const float SettleSeconds = 1.5f;

    /// <summary>Records what the counter held just before a serve landed on it.</summary>
    /// <summary>When each counter was last stacked, to recognise a repeated serve.</summary>
    private static readonly Dictionary<IntPtr, float> LastStackAt = new();

    /// <summary>
    /// A serve is not allowed to stack the same counter twice in quick succession.
    ///
    /// The machine that started the serve has already deepened and cleared the counter by the
    /// time this runs, and reads zero portions, so it falls out on its own. This guards the
    /// case that cannot be reasoned about from here: <c>ServePlate</c> arriving more than once
    /// for one serve would otherwise read the freshly filled count and stack again on top.
    /// </summary>
    private const float RestackGuardSeconds = 2f;

    /// <summary>
    /// Deepens and clears the counter on **this** machine, wherever the serve came from.
    ///
    /// This is what makes the feature work in co-op, and it needs no messages of its own.
    /// <c>ServePlate</c> is reached on every machine - the chain ends in
    /// <c>ServePlateClientRpc</c>, and it was reached even on an occupied counter, which is
    /// how "4 to 4 portions" came to be measured in the first place. So every player runs this
    /// same sum against their own copy of the counter, whose portion count the game already
    /// keeps in step, and they arrive at the same depth independently. Agreement by identical
    /// arithmetic rather than by a message that could arrive after the serve it describes.
    ///
    /// The machine whose player clicked has already done this in <see cref="PrepareStack"/>
    /// and reads zero portions here, so it skips - the work happens once per machine either
    /// way.
    ///
    /// Both players need the mod, and both need the option on. There is no way around that
    /// from one side: a player who does not run this sees the counter refill to its own depth,
    /// and since the host is the one running the waiters, whoever is missing it decides what
    /// the food actually runs out at.
    /// </summary>
    private static void StackHere(ServiceCounter counter, ItemIdentifier incoming)
    {
        if (!Plugin.CounterTopUpEnabled.Value) return;

        try
        {
            int remaining = counter.ActiveServings;
            if (remaining <= 0) return;          // empty counter, or we prepared it ourselves

            if (LastStackAt.TryGetValue(counter.Pointer, out float last)
                && Time.unscaledTime - last < RestackGuardSeconds)
                return;

            if (!Deepen(counter, remaining, out int want)) return;

            // Exactly what the machine that clicked does, from its own copy of the counter.
            // Without this the pile is only ever built where the click happened, and everyone
            // else's counter goes on showing one dish for the whole stack - which is the fault
            // the pile exists to fix, present on every screen but one.
            //
            // Not ActivePlate alone: on a machine that did not serve the plate itself, the
            // counter holds the portions without knowing which plate they came on -
            // HasActivePlate() is false and ActivePlate is empty. Reading only that is what
            // left every co-op screen but one showing the whole pile side by side.
            // The plate being served is excluded: on this machine it can already be standing on
            // the spot by the time the serve reaches the counter, and taking it for the old
            // one would bury the plate that has just arrived.
            Plate arriving = null;
            try { arriving = incoming != null ? incoming.GetComponent<Plate>() : null; } catch { }

            var wasOn = TopOf(counter, arriving);
            int itsOwn = remaining - PortionsQueued(counter.Pointer);

            if (!ClearCounter(counter))
            {
                Forget(counter.Pointer);
                return;
            }

            if (wasOn == null)
                LogOnce("Counter top-up: a serve arrived on a counter with no plate to be found " +
                        "on it, so there was nothing to put under the new one.");

            LetGoOf(wasOn, counter, itsOwn);

            LogOnce($"Counter top-up: a serve arrived on a counter holding {remaining} portions, " +
                    $"so it was deepened to {want} here too and the plate that was on it went " +
                    "under the new one. This is the co-op half, and it runs on every machine " +
                    "that has the mod.");
        }
        catch (Exception e)
        {
            LogOnce($"Counter top-up: could not stack an incoming serve: {e.GetType().Name}: {e.Message}");
        }
    }

    // ------------------------------------------------------- taking a plate off

    /// <summary>Whether the unstack hook is installed, for the panel to report.</summary>
    private static bool _unstacks;

    public static bool Unstacks => _unstacks;

    /// <summary>
    /// Set while <see cref="ClearCounter"/> is emptying a counter on purpose, so the unstack
    /// prefix lets that through instead of turning it into a one-plate removal.
    /// </summary>
    private static bool _clearing;

    /// <summary>A removal in progress on one counter.</summary>
    private sealed class Reduction
    {
        public ServiceCounter Counter;
        public int Target;
        public int Was;
        public int Step;
        public float GraceUntil;
        public float DeadlineAt;
    }

    private static readonly Dictionary<IntPtr, Reduction> Reducing = new();

    /// <summary>When each counter was last unstacked, to absorb a doubled call.</summary>
    private static readonly Dictionary<IntPtr, float> LastReduceAt = new();

    /// <summary>
    /// How long a client waits to see whether the host's decrease reaches it by itself
    /// before doing the arithmetic on its own copy.
    /// </summary>
    private const float ReplicationGrace = 1f;

    /// <summary>How long a removal is given to reach its number before it is given up on.</summary>
    private const float ReduceDeadline = 5f;

    /// <summary>Which side of a co-op session this is.</summary>
    private static bool IsHostSide()
    {
        try
        {
            var nm = Unity.Netcode.NetworkManager.Singleton;
            if (nm == null || !nm.IsListening) return true;   // solo: this machine is authority
            return nm.IsServer;
        }
        catch { return true; }
    }

    /// <summary>
    /// Every call on a counter that could be the one that empties it.
    ///
    /// The first attempt patched <c>ResetCounter</c> alone, reasoning that it is the tail of
    /// <c>ResetCounterServerRpc</c> -&gt; <c>ResetCounterClientRpc</c> the way
    /// <c>ServePlate</c> is the tail of a serve. Measured: the remove ran - the watcher on
    /// <c>ResetCounterSpeciality.DoItemSpeciality</c> said so - the counter went to zero, and
    /// the <c>ResetCounter</c> prefix was never reached. So the remove empties a counter
    /// through some other call, and which one is not readable from the stubs.
    ///
    /// All four are therefore covered. Only one of them is the real one and the rest never
    /// fire on a removal, which costs nothing: each is gated on the counter being stacked, a
    /// state only this mod creates, and the first to fire takes the removal while the others
    /// are absorbed by the guard. The log names whichever it was.
    /// </summary>
    private static readonly string[] EmptyingCalls =
    {
        nameof(ServiceCounter.ResetServing),
        nameof(ServiceCounter.ResetRecipe),
        nameof(ServiceCounter.ReleaseActivePlate),
        nameof(ServiceCounter.ResetCounter),
    };

    /// <summary>
    /// Casts the net: a skip-capable prefix on every way a counter can be emptied, and a
    /// look-but-do-not-touch prefix on the two RPC ends.
    ///
    /// **The RPC ends are only ever watched, never skipped.** Skipping a send is how a
    /// removal would go missing on the far machine: the client presses remove, its ServerRpc
    /// never leaves, and the host goes on believing the counter is full. So the message is
    /// always allowed to travel, and the emptying is refused at the far end of it - which is
    /// reached on every machine, so every machine refuses alike and then does the same
    /// subtraction.
    ///
    /// <c>ResetCounterClientRpc</c> is where a removal is first *known* to be real, so the
    /// number to come down to is worked out there, from the count before anything has
    /// cleared it. Doing that in the emptying call itself would be a step too late if some
    /// earlier call in the chain has already zeroed the count.
    /// </summary>
    private static void InstallUnstack(BepInEx.Logging.ManualLogSource log, HarmonyLib.Harmony harmony)
    {
        var stop = new HarmonyLib.HarmonyMethod(
            HarmonyLib.AccessTools.Method(typeof(CounterTopUp), nameof(RemoveOnePlate)));

        int caught = 0;

        for (int i = 0; i < EmptyingCalls.Length; i++)
        {
            try
            {
                var m = HarmonyLib.AccessTools.Method(typeof(ServiceCounter), EmptyingCalls[i]);

                if (m == null || m.DeclaringType != typeof(ServiceCounter))
                {
                    log.LogWarning($"Counter top-up: could not resolve ServiceCounter.{EmptyingCalls[i]}.");
                    continue;
                }

                harmony.Patch(m, prefix: stop);
                caught++;
            }
            catch (Exception e)
            {
                log.LogWarning($"Counter top-up: {EmptyingCalls[i]} not hooked: {e.Message}");
            }
        }

        _unstacks = caught > 0;

        if (!_unstacks)
            log.LogWarning("Counter top-up: nothing that empties a counter could be hooked - " +
                           "removing a dish will clear the whole stack as the game does.");

        // Where the removal is recognised and the target worked out, on every machine at once.
        try
        {
            var rpc = HarmonyLib.AccessTools.Method(
                typeof(ServiceCounter), nameof(ServiceCounter.ResetCounterClientRpc));

            if (rpc != null)
                harmony.Patch(rpc, prefix: new HarmonyLib.HarmonyMethod(
                    HarmonyLib.AccessTools.Method(typeof(CounterTopUp), nameof(NoteRemoval))));
            else
                log.LogWarning("Counter top-up: ResetCounterClientRpc not found; a removal will " +
                               "be recognised from the emptying call instead.");
        }
        catch (Exception e)
        {
            log.LogWarning($"Counter top-up: removal notice not installed: {e.Message}");
        }

        // Observations, both of them. Together with the line each emptying call logs, these
        // are what will finally say what the chain from the player's remove actually is.
        try
        {
            var send = HarmonyLib.AccessTools.Method(
                typeof(ServiceCounter), nameof(ServiceCounter.ResetCounterServerRpc));

            if (send != null)
                harmony.Patch(send, prefix: new HarmonyLib.HarmonyMethod(
                    HarmonyLib.AccessTools.Method(typeof(CounterTopUp), nameof(WatchResetRequest))));

            var remove = HarmonyLib.AccessTools.Method(
                typeof(ResetCounterSpeciality), nameof(ResetCounterSpeciality.DoItemSpeciality));

            if (remove != null && remove.DeclaringType == typeof(ResetCounterSpeciality))
                harmony.Patch(remove, prefix: new HarmonyLib.HarmonyMethod(
                    HarmonyLib.AccessTools.Method(typeof(CounterTopUp), nameof(WatchRemove))));
        }
        catch (Exception e)
        {
            LogOnce($"Counter top-up: remove watchers not installed: {e.Message}");
        }
    }

    /// <summary>
    /// Starts a removal, if the counter is stacked and one is not already in hand.
    ///
    /// While a counter is holding more than one plate's worth, removing takes off exactly what
    /// a serve put on - the counter's own number - and leaves the dish and the rest of the
    /// food alone. Serving is +4 and removing is -4, and the last removal, at one plate's
    /// worth or less, is the game's own clear, so a counter still ends up properly empty and
    /// free for another dish.
    /// </summary>
    /// <returns>true if a removal is now in hand and the counter must not be emptied.</returns>
    private static bool BeginRemoval(ServiceCounter counter, string via)
    {
        if (_clearing || !Plugin.CounterTopUpEnabled.Value || counter == null) return false;

        try
        {
            IntPtr key = counter.Pointer;

            // Already coming down. A removal reaches a counter through several calls in a row
            // and, on the machine whose player pressed the key, may reach it through the RPC
            // as well; all of that is one removal, not four.
            if (Reducing.ContainsKey(key)) return true;

            if (LastReduceAt.TryGetValue(key, out float last)
                && Time.unscaledTime - last < RestackGuardSeconds)
                return true;

            int remaining = counter.ActiveServings;
            int step = BaselineOf(counter);

            // Not stacked: this is an ordinary counter being cleared, which is the game's
            // business and none of ours. Also covers the last removal of a stack.
            if (step <= 0 || remaining <= step) return false;

            int target = remaining - step;

            LastReduceAt[key] = Time.unscaledTime;
            _stackOwners[key] = counter;

            // The count is about to move, and on a client it may move a second later. Hold the
            // stack across the whole of it, or the housekeeping tidies it away mid-flight.
            Hold[key] = Time.unscaledTime + ServeHoldSeconds;

            Reducing[key] = new Reduction
            {
                Counter = counter,
                Target = target,
                Was = remaining,
                Step = step,
                GraceUntil = Time.unscaledTime + ReplicationGrace,
                DeadlineAt = Time.unscaledTime + ReduceDeadline,
            };

            // What the counter is holding before anything is asked of it. Followed from here
            // so that what becomes of it is on the record - see FollowPlates.
            try
            {
                var own = counter.ActivePlate;
                Follow("the counter's own plate", own == null ? null : own.thisItem, counter);
            }
            catch { }

            // A plate's worth of food is coming off, so a plate has to come back - one per
            // removal, the same way one goes on per serve. The game's own clear does this by
            // handing back the counter's plate, which is why the last removal of a stack needs
            // nothing from us; every removal before it is ours to answer for.
            bool handedBack = HandBackAPlate(counter);

            // The host is the authority on how much food is on a counter, so it acts at once -
            // unless a plate has just been asked for, in which case it waits long enough to see
            // whether that took portions off by itself. A client always waits: if the host's
            // decrease reaches it on its own, doing the subtraction here as well would take off
            // two plates instead of one. Whether it does reach it cannot be read from the stubs
            // - DecreaseServing sits next to an OnDecreasedServingClientRpc - so this measures
            // instead of assuming, and says which it was in the log.
            if (handedBack && IsHostSide())
                Reducing[key].GraceUntil = Time.unscaledTime + HandBackSettle;
            else if (IsHostSide())
                DrainTo(counter, target);

            LogOnce($"Counter top-up: a removal reached the counter through {via}. Stacked to " +
                    $"{remaining}, so one plate's worth ({step}) comes off, leaving {target}; " +
                    "the dish stays on it.");

            Status = $"đã bớt {step} suất: {remaining} → {target}";
            return true;
        }
        catch (Exception e)
        {
            LogOnce($"Counter top-up: starting a removal failed, letting the game clear the " +
                    $"counter: {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Refuses to empty a counter that a removal is taking one plate off, and lets every other
    /// clear through untouched.
    /// </summary>
    private static bool RemoveOnePlate(ServiceCounter __instance,
                                       System.Reflection.MethodBase __originalMethod)
    {
        if (_clearing || !Plugin.CounterTopUpEnabled.Value || __instance == null) return true;

        try
        {
            string via = __originalMethod?.Name ?? "an emptying call";

            // Logged whether or not it is ours to take, because between them these lines are
            // what say which call the player's remove actually empties a counter with.
            LogOnce($"Counter top-up: {via} ran on a counter holding {__instance.ActiveServings} " +
                    $"portions as {(IsHostSide() ? "host" : "client")}.");

            return !BeginRemoval(__instance, via);
        }
        catch (Exception e)
        {
            LogOnce($"Counter top-up: unstack check failed: {e.GetType().Name}: {e.Message}");
            return true;
        }
    }

    /// <summary>
    /// Recognises a removal at the point it is known to be real, and on every machine at once.
    /// Never skips: the message has to be allowed to travel.
    /// </summary>
    private static void NoteRemoval(ServiceCounter __instance)
    {
        if (__instance == null) return;
        BeginRemoval(__instance, "ResetCounterClientRpc");
    }

    /// <summary>An observation: the removal asking the host for permission.</summary>
    private static void WatchResetRequest(ServiceCounter __instance)
    {
        if (__instance == null) return;

        try
        {
            LogOnce($"Counter top-up: ResetCounterServerRpc was called as " +
                    $"{(IsHostSide() ? "host" : "client")} on a counter holding " +
                    $"{__instance.ActiveServings} portions.");
        }
        catch { }
    }

    /// <summary>
    /// How long to leave the count alone after asking for a plate back, in case that call
    /// takes portions off the counter by itself.
    /// </summary>
    private const float HandBackSettle = 0.4f;

    /// <summary>When this machine's own player last asked to remove a dish, per counter.</summary>
    private static readonly Dictionary<IntPtr, float> RemoveAskedHere = new();

    /// <summary>How long that stays true for - one removal, not the next one.</summary>
    private const float AskedWindow = 2f;

    /// <summary>
    /// Whether it was this machine's player who pressed remove at this counter.
    ///
    /// It matters because handing a plate back is the acting player's business, not every
    /// machine's. The call replicates through <c>DuplicateServingItemWithChildrenServerRpc</c>
    /// and spawns the plate for the player who made it, so running it on both machines would
    /// give a plate to someone who never asked for one.
    /// </summary>
    private static bool AskedHere(ServiceCounter counter)
    {
        try
        {
            return RemoveAskedHere.TryGetValue(counter.Pointer, out float at)
                   && Time.unscaledTime - at < AskedWindow;
        }
        catch { return false; }
    }

    /// <summary>
    /// Gives the player a plate of what is on the counter, the way taking a serving off it
    /// does.
    ///
    /// A stacked counter holds one plate and a number, so there is nothing to hand over on a
    /// removal that leaves food behind - the plate the game would give back is still holding
    /// the rest of it. <c>ItemManager.DuplicateServingItemWithChildren</c> is the game's own
    /// answer to that, and it is what a serving taken off a counter goes through: it makes a
    /// carryable plate from the counter's own, children and all, and it has the full
    /// ServerRpc/ClientRpc/Internal chain, so the plate exists on both machines.
    ///
    /// It is a *duplicate*, not a hand-over: the counter keeps its plate, and taking the
    /// portions off is a separate step, which is exactly the division this needs since the
    /// count is being managed here anyway. If that turns out to be wrong and it drains the
    /// counter as well, the removal settles on whatever it finds rather than draining twice,
    /// and says so.
    /// </summary>
    private static bool HandBackAPlate(ServiceCounter counter)
    {
        if (!AskedHere(counter)) return false;

        try
        {
            var manager = ResolveItemManager();
            if (manager == null)
            {
                LogOnce("Counter top-up: no ItemManager, so no plate could be handed back for " +
                        "a removal - the portions still came off.");
                return false;
            }

            var plate = counter.ActivePlate;
            var item = plate == null ? null : plate.thisItem;

            if (item == null)
            {
                LogOnce("Counter top-up: the counter has no active plate to copy, so a removal " +
                        "took the portions off without handing a dish back.");
                return false;
            }

            // The callback comes back inside this call - measured, its line is logged before
            // the one below - so the counter it belongs to can simply be parked here.
            _handBackCounter = counter;
            _lastHandedBack = null;
            manager.DuplicateServingItemWithChildren(counter, item, HandBackCallback());
            _handBackCounter = null;

            LogOnce("Counter top-up: asked the game for a plate of what is on the counter, the " +
                    "same way a serving taken off one is made, so a removal gives a dish back.");

            PointTheCounterAtTheNewPlate(counter, plate);

            return true;
        }
        catch (Exception e)
        {
            LogOnce($"Counter top-up: could not hand a plate back: {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    /// <summary>The plate the last hand-back produced, filled in by the callback.</summary>
    private static ItemIdentifier _lastHandedBack;

    /// <summary>
    /// Points the counter at the plate that stayed behind, because the one it was holding has
    /// just left with the player.
    ///
    /// This is the fault, measured 2026-09-05 by following both plates through two removals.
    /// <c>DuplicateServingItemWithChildren</c> does not do what its name suggests from the
    /// counter's side: the plate that ends up in the player's hands is the counter's *own*,
    /// and the newly made one is left standing at the counter. The counter's
    /// <c>ActivePlate</c> is not updated to match, so it still believes it is holding the
    /// plate the player has carried off - and the removal that finally empties it releases
    /// that plate, which yanks it out of wherever the player had put it down. From the
    /// player's side: remove twice and the first dish moves instead of a second one arriving.
    ///
    /// Two rounds were lost to guessing at this, one of them acting on any change to
    /// <c>ActivePlate</c> at all, which fired on a legitimate release and took the player's
    /// dish off them. So this only ever fires on the one change it can name: the counter is
    /// still pointing at the exact plate that was passed to the hand-back, and there is a new
    /// plate to point it at instead.
    /// </summary>
    private static void PointTheCounterAtTheNewPlate(ServiceCounter counter, Plate wasHolding)
    {
        try
        {
            var made = _lastHandedBack;
            _lastHandedBack = null;

            if (made == null || counter == null || wasHolding == null) return;

            // Only if nothing else has already moved it on.
            var now = counter.ActivePlate;
            if (now == null || now.Pointer != wasHolding.Pointer) return;

            var replacement = made.GetComponent<Plate>();
            if (replacement == null)
            {
                LogOnce("Counter top-up: the plate handed back has no Plate on it, so the counter " +
                        "was left pointing at the one the player is carrying.");
                return;
            }

            if (replacement.Pointer == wasHolding.Pointer) return;   // the same plate; nothing to do

            counter.ActivePlate = replacement;
            SetTop(counter, replacement);

            LogOnce("Counter top-up: the hand-back gives the player the counter's own plate and " +
                    "leaves a new one behind, so the counter was pointed at the new one. Without " +
                    "that it goes on believing it holds the plate the player walked off with, and " +
                    "the removal that empties it drags that plate back out of their hands.");

            StandPlateOnCounter(counter, made);

            // The dish has not changed - the new plate is a copy of the one that just left - but
            // the rating on the card is sent round against a plate, and this is a different one.
            // Say it again for the plate that is actually there now, or the card goes on quoting
            // a plate the player has walked off with.
            try
            {
                counter.SyncRatingServerRpc(made.itemIndex, counter.TasteRating);
            }
            catch (Exception e)
            {
                LogOnce($"Counter top-up: the rating could not be moved onto the new plate: " +
                        $"{e.GetType().Name}: {e.Message}");
            }

            RefreshCard(counter);
        }
        catch (Exception e)
        {
            LogOnce($"Counter top-up: could not point the counter at its new plate: " +
                    $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// Puts the plate the hand-back left behind where the counter's plate belongs.
    ///
    /// It is made loose in the world with nothing holding it up - measured, it lands on the
    /// floor beside the counter - because the step that would have placed it is the counter's
    /// own, and on a removal that is exactly the step being refused. So it is placed here, at
    /// the counter's service transform, which is where the game puts a plate it is serving
    /// from and where the plate it replaces was standing a moment ago.
    ///
    /// Moved the networked way as well as the local one: <c>transform.position</c> alone is
    /// this machine only - the lesson from plate alignment - and
    /// <c>RepositionItemServerRpc</c> is the game's own move, with the host authoritative and
    /// the item's NetworkTransform carrying it to everyone else.
    /// </summary>
    private static void StandPlateOnCounter(ServiceCounter counter, ItemIdentifier plate)
    {
        try
        {
            var where = ServeTransform(counter);

            if (where == null)
            {
                LogOnce("Counter top-up: the counter has no service transform, so the plate left " +
                        "behind by a hand-back was not stood on it.");
                return;
            }

            var position = where.position;

            // Locally first, so the plate is in the right place on this machine whatever the
            // host makes of the call below.
            plate.transform.position = position;

            try { plate.Rotate(where.rotation); }
            catch (Exception e) { LogOnce($"Counter top-up: could not turn the new plate: {e.Message}"); }

            // The local move above is what every machine needs; the networked one is a single
            // authoritative statement about where the item is, so only one machine makes it.
            // Both ends running it would have the host answering its own message.
            if (IsHostSide())
            {
                var manager = ResolveItemManager();
                if (manager != null) manager.RepositionItemServerRpc(plate.gameID, position);
            }

            // And it has to be told to stay there. A plate the game is serving from does not
            // fall off its counter, and a freshly spawned item has live physics - which is why
            // moving it to the right spot was not enough on its own, and it went on to the
            // floor from there. SetKinematic is the game's own way of saying "this item is
            // being held in place by something", which is exactly what a counter does.
            try { plate.SetKinematic(true); }
            catch (Exception e)
            {
                LogOnce($"Counter top-up: the new plate could not be pinned to the counter " +
                        $"({e.GetType().Name}: {e.Message}), so it may roll off it.");
            }

            LogOnce("Counter top-up: the plate the hand-back left behind was stood on the " +
                    "counter's serving spot, where the one the player took off it had been.");
        }
        catch (Exception e)
        {
            LogOnce($"Counter top-up: could not stand the new plate on the counter: " +
                    $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>One plate being followed after a removal, and the last thing said about it.</summary>
    private sealed class PlateWatch
    {
        public string Role;
        public ItemIdentifier Item;
        public ServiceCounter Counter;
        public float Until;
        public string Last;
    }

    private static readonly List<PlateWatch> Watching = new();

    /// <summary>The counter a hand-back is in flight for, for the callback to attribute it.</summary>
    private static ServiceCounter _handBackCounter;

    /// <summary>
    /// How long the two plates involved in a removal are followed for. Long enough to cover
    /// a second removal and whatever the game does after it.
    /// </summary>
    private const float PlateWatchSeconds = 90f;

    /// <summary>
    /// Follows both plates a removal involves, and says every time either one changes.
    ///
    /// This exists because four attempts at reasoning out where the player's first dish goes
    /// have each been wrong. The counter's plate is not taken by the hand-back - measured
    /// 2026-09-05, it was unchanged for the whole three seconds after one - and the dish that
    /// was handed back was still alive after the removal that empties the counter. So the
    /// disappearance is neither of the two things it looked like, and the honest next step is
    /// to watch both plates and let them say what happens rather than guess a fifth time.
    ///
    /// Nothing here changes anything. An earlier version put the counter's plate back when it
    /// changed, which fired on the *legitimate* release at the second removal and took the
    /// player's dish off them - a good illustration of why a guard that cannot tell which
    /// change it is looking at should not be acting on it.
    /// </summary>
    private static void FollowPlates()
    {
        if (Watching.Count == 0) return;

        float now = Time.unscaledTime;

        for (int i = Watching.Count - 1; i >= 0; i--)
        {
            var w = Watching[i];

            string state = StateOf(w);

            if (state != w.Last)
            {
                w.Last = state;
                LogOnce($"Counter top-up: {w.Role} is {state}.");
            }

            if (now >= w.Until || state == "gone") Watching.RemoveAt(i);
        }
    }

    /// <summary>Where a plate is and who has it, in one line. Never throws.</summary>
    private static string StateOf(PlateWatch w)
    {
        bool alive;
        try { alive = w.Item != null; } catch { alive = false; }
        if (!alive) return "gone";

        string handed;
        try { handed = w.Item.isHanded ? "in someone's hands" : "not held"; }
        catch { handed = "held, unreadably"; }

        string where;
        try
        {
            var parent = w.Item.transform.parent;
            where = parent == null ? "loose in the world" : $"under '{parent.name}'";
        }
        catch { where = "somewhere unreadable"; }

        // The food, not just the plate. "The dish disappears" may well mean what is *on* the
        // plate rather than the plate itself, and a duplicate made "with children" is exactly
        // the sort of thing that can end up sharing its food with the plate it came from.
        string food;
        try
        {
            int children = w.Item.transform.childCount;
            food = children == 0 ? "with nothing on it" : $"carrying {children}";
        }
        catch { food = "carrying an unreadable amount"; }

        // Whether it is being held in place or is loose under gravity. A plate on a counter is
        // pinned; a freshly spawned one is not, and rolls off.
        string physics;
        try
        {
            var body = w.Item.itemRigidbody;
            physics = body == null ? "no physics"
                    : body.isKinematic ? "pinned"
                    : "free to fall";
        }
        catch { physics = "physics unreadable"; }

        string counters = "";
        try
        {
            if (w.Counter != null)
            {
                var active = w.Counter.ActivePlate;
                var item = active == null ? null : active.thisItem;

                if (item != null && item.Pointer == w.Item.Pointer)
                    counters = ", and it is the counter's own plate";
            }
        }
        catch { }

        return $"{handed}, {where}, {food}, {physics}{counters}";
    }

    /// <summary>Starts following a plate, if there is room.</summary>
    private static void Follow(string role, ItemIdentifier item, ServiceCounter counter)
    {
        if (item == null || Watching.Count >= 6) return;

        try
        {
            var w = new PlateWatch
            {
                Role = role,
                Item = item,
                Counter = counter,
                Until = Time.unscaledTime + PlateWatchSeconds,
            };

            w.Last = StateOf(w);
            Watching.Add(w);

            LogOnce($"Counter top-up: {role} starts out {w.Last}.");
        }
        catch { }
    }

    /// <summary>
    /// A real delegate rather than null: whether the game guards the callback cannot be read
    /// from the stubs, and a null dereference here would be in the middle of spawning an item.
    /// Held in a static field so nothing collects it while IL2CPP holds a pointer to it.
    /// </summary>
    private static Il2CppSystem.Action<ItemIdentifier> _handBack;
    private static bool _handBackTried;

    private static Il2CppSystem.Action<ItemIdentifier> HandBackCallback()
    {
        if (_handBack != null || _handBackTried) return _handBack;
        _handBackTried = true;

        try
        {
            _handBack = Il2CppInterop.Runtime.DelegateSupport
                .ConvertDelegate<Il2CppSystem.Action<ItemIdentifier>>(
                    new Action<ItemIdentifier>(OnPlateHandedBack));
        }
        catch (Exception e)
        {
            LogOnce($"Counter top-up: could not build the hand-back callback ({e.GetType().Name}: " +
                    $"{e.Message}); passing none and hoping the game guards it.");
        }

        return _handBack;
    }

    /// <summary>Called from IL2CPP, so it must never throw.</summary>
    private static void OnPlateHandedBack(ItemIdentifier plate)
    {
        try
        {
            string name;
            try { name = plate == null ? "nothing" : plate.name; } catch { name = "an item"; }

            LogOnce($"Counter top-up: the plate handed back for a removal was '{name}'.");

            _lastHandedBack = plate;
            Follow("the dish handed back", plate, _handBackCounter);
        }
        catch { }
    }

    /// <summary>
    /// Counts the counter down to a number, the way a customer takes a portion.
    ///
    /// Re-reads between steps rather than counting off <paramref name="target"/> calls, so a
    /// portion taken while this runs cannot make it overshoot. It also stops the moment a
    /// call fails to move the number, which is what a decrease the host owns would look like
    /// from a client.
    /// </summary>
    private static bool DrainTo(ServiceCounter counter, int target)
    {
        for (int i = 0; i < 64; i++)
        {
            int before;
            try { before = counter.ActiveServings; } catch { return false; }

            if (before <= target) return true;

            try { counter.DecreaseServing(); }
            catch (Exception e)
            {
                LogOnce($"Counter top-up: DecreaseServing threw: {e.GetType().Name}: {e.Message}");
                return false;
            }

            int after;
            try { after = counter.ActiveServings; } catch { return false; }

            if (after >= before)
            {
                LogOnce("Counter top-up: DecreaseServing did not move the count on this machine, " +
                        "so the portion count is not this side's to change - the counter will " +
                        "follow whatever the host says it holds.");
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Sees each removal through, and records the stack at the depth it has come down to.
    ///
    /// The depth is lowered only once the portions have actually gone, never at the moment the
    /// removal starts: the card above the counter draws portions against that depth, and a
    /// counter briefly holding more than it is deep is exactly the sort of thing that makes a
    /// fixed row of dot images misbehave.
    /// </summary>
    private static void PumpReductions()
    {
        if (Reducing.Count == 0) return;

        float now = Time.unscaledTime;
        List<IntPtr> done = null;

        foreach (var pair in Reducing)
        {
            var r = pair.Value;

            bool gone;
            try { gone = r.Counter == null; } catch { gone = true; }

            if (gone) { (done ??= new List<IntPtr>()).Add(pair.Key); continue; }

            int held;
            try { held = r.Counter.ActiveServings; }
            catch { (done ??= new List<IntPtr>()).Add(pair.Key); continue; }

            if (held > r.Target)
            {
                if (now < r.GraceUntil) continue;

                // The grace has passed with the count still up, so the host's decrease is not
                // going to arrive - this machine keeps its own tally and has to do the sum.
                if (!IsHostSide())
                    LogOnce("Counter top-up: a removal did not reach this machine on its own, so " +
                            "the plate was taken off here as well - each machine keeps its own " +
                            "count and they agree by doing the same arithmetic.");

                DrainTo(r.Counter, r.Target);

                try { held = r.Counter.ActiveServings; } catch { }

                if (held > r.Target && now < r.DeadlineAt) continue;
            }
            else if (!IsHostSide())
            {
                LogOnce("Counter top-up: a removal reached this machine on its own - the host's " +
                        "decrease replicates, so the client does not repeat it.");
            }

            Settle(pair.Key, r, held);
            (done ??= new List<IntPtr>()).Add(pair.Key);
        }

        if (done == null) return;

        foreach (var key in done) Reducing.Remove(key);
    }

    /// <summary>Records where a counter has ended up, and lets the stack go if it is spent.</summary>
    private static void Settle(IntPtr key, Reduction r, int held)
    {
        Hold.Remove(key);

        // Down to what a plate is worth or less: this is an ordinary counter again, so hand
        // the depth back to the game rather than leaving it reported as a stack.
        if (held <= r.Step)
        {
            Forget(key);
        }
        else
        {
            Stacked[key] = held;
            _stackOwners[key] = r.Counter;
        }

        // Only ever from something outside this - the drain stops the moment it reaches the
        // number - so it says the plate hand-back takes portions off the counter as well, and
        // that the removal should stop draining on top of it.
        if (held < r.Target)
            LogOnce($"Counter top-up: a removal ended at {held} portions rather than {r.Target}, " +
                    "so something other than the count-down is taking food off the counter - " +
                    "most likely the plate hand-back does it too.");

        LogOnce($"Counter top-up: a removal took a counter from {r.Was} to {held} portions " +
                $"(one plate's worth is {r.Step}), and the dish is still on it.");

        Status = $"đã bớt: {r.Was} → {held} suất";
    }

    /// <summary>
    /// Notes that the player's own remove action ran here, which is the one thing that would
    /// let the rule above be narrowed.
    ///
    /// Right now a removal is recognised by the counter being stacked, which is a state only
    /// this mod creates - so anything else in the game that clears a counter is only ever
    /// touched on a counter that has been stacked. If this line turns out to appear on both
    /// machines for one player's remove, the hook can be gated on the action itself instead.
    /// </summary>
    private static void WatchRemove(ResetCounterSpeciality __instance)
    {
        if (__instance == null) return;

        try
        {
            bool mine = __instance.isCalledByMe;

            LogOnce($"Counter top-up: the remove-dish action ran on this machine as " +
                    $"{(IsHostSide() ? "host" : "client")}, called by me: {mine}.");

            // Which machine is owed a plate. This runs before the counter is touched - the
            // measured order is DoItemSpeciality, then ResetServing, then ResetRecipe - so the
            // answer is already here by the time the removal is recognised.
            var counter = __instance.thisCounter;
            if (mine && counter != null) RemoveAskedHere[counter.Pointer] = Time.unscaledTime;
        }
        catch { }
    }

    private static void WatchServe(ServiceCounter __instance, ItemIdentifier plateItem)
    {
        if (__instance == null) return;

        StackHere(__instance, plateItem);

        // After StackHere, which needed the old top to know what goes under the new one.
        try { if (plateItem != null) SetTop(__instance, plateItem.GetComponent<Plate>()); }
        catch { }

        if (Waiting.Count > 8) return;

        try
        {
            bool wasTopUp = _lastAllowed != null
                            && _lastAllowed.Pointer == __instance.Pointer
                            && Time.unscaledTime - _lastAllowedAt < 10f;

            Waiting.Add(new Pending
            {
                Counter = __instance,
                Before = __instance.ActiveServings,
                Max = __instance.MaxServingCount,
                DueAt = Time.unscaledTime + SettleSeconds,
                WasTopUp = wasTopUp,
            });
        }
        catch { }
    }

    /// <summary>
    /// Reports what a serve did to the count.
    ///
    /// This is the part that is measured rather than assumed. Whether one serve fills the
    /// counter or adds a fixed number of portions decides whether refusing at capacity is
    /// enough to keep it inside the limit, and the only honest way to know is to watch a
    /// real serve do it.
    /// </summary>
    public static void Tick()
    {



        // Before the housekeeping: a counter coming down from a removal is held by Hold, and
        // this is what releases it once it has arrived.
        PumpReductions();

        // Nothing acts on this - it only says where the two plates a removal involves have got
        // to, because four rounds of reasoning about that have each been wrong.
        FollowPlates();

        SettleStackedServes();
        ServeEachPlateInTurn();

        // Everything above is driven by something this machine did. This is the one that puts
        // right what another machine did, from what can be seen rather than from a message.
        MendCounters();

        ForgetDrainedStacks();

        if (Waiting.Count == 0) return;

        float now = Time.unscaledTime;

        for (int i = Waiting.Count - 1; i >= 0; i--)
        {
            var p = Waiting[i];
            if (now < p.DueAt) continue;

            Waiting.RemoveAt(i);

            try
            {
                if (p.Counter == null) continue;

                int after = p.Counter.ActiveServings;
                int max = p.Counter.MaxServingCount;

                string what = p.WasTopUp ? "stacked serve" : "serve";

                LogOnce($"Counter top-up: {what} took the counter from {p.Before} to {after} " +
                        $"portions, depth now {max}.");

                if (p.WasTopUp)
                    Status = after <= p.Before
                        ? $"không cộng được: vẫn {after} suất"
                        : $"đã cộng dồn: {p.Before} → {after} suất";
            }
            catch { }
        }
    }

    /// <summary>Whether the serve check is actually hooked, for the panel to report.</summary>
    public static bool Patched => _patched;

    /// <summary>
    /// Whether either route to a stacked serve is hooked - the item event, or the dish
    /// picker's own button as its fallback.
    ///
    /// With neither, the prompt still appears and the picker still opens, but serving does
    /// not stack - worth saying in the panel rather than leaving the player wondering why the
    /// number does not move.
    /// </summary>
    public static bool StacksOnClick => _direct;
}
