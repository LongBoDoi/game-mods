using System;
using System.Collections.Generic;
using UnityEngine;

namespace PlayerTweaks;

/// <summary>
/// Keeps waiters from drawing clean plates out of chosen cabinets, so one cabinet can be held
/// back as a reserve instead of every cabinet draining at whatever rate the game picks.
///
/// A plate cabinet is a <c>DishStock</c> whose DishType is Plate. When a plate is needed,
/// <c>StaffManager.GetPlateFromPlateStocks</c> walks the cabinets by way of
/// <c>TryToGetDishFromDishStock(plateName, i, callback)</c> - one call per cabinet, index and
/// all - so that walk is where a cabinet can be passed over without touching how plates are
/// taken, counted or replicated.
///
/// Cabinets are remembered by where they stand, rounded to 10cm. That is not the first choice
/// it looks like: this used to key on <c>RoIdNetVar</c>, on the reading that it was the game's
/// id for a placed object. It is not. <c>PlacedRestaurantObject.RO</c> is a
/// <c>RestaurantObject</c> - the shop catalogue entry, carrying objectName, objectPrice and the
/// prefab list - so RoIdNetVar names the *model*, and every plate cabinet of that model shares
/// it. Excluding one cabinet excluded all of them, and because the host's set then never
/// changed on a second mark, it also swallowed the broadcast that a client was waiting for.
///
/// A position is unique per placed object and comes back identical from a save, which is the
/// property actually needed. The cost is that moving a cabinet in build mode detaches its
/// mark - a visible, recoverable loss, unlike a shared id that quietly governs the wrong
/// cabinets.
/// </summary>
public static class PlateStocks
{
    public static string Status = "chưa loại trừ tủ nào";

    private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Logged = new();

    /// <summary>The config text the set was last built from, so parsing is not per frame.</summary>
    private static string _raw;

    // ------------------------------------------------------------------ patch

    public static void Install(BepInEx.Logging.ManualLogSource log)
    {
        try
        {
            var target = HarmonyLib.AccessTools.Method(
                typeof(StaffManager), nameof(StaffManager.TryToGetDishFromDishStock));

            if (target == null)
            {
                log.LogWarning("PlateStocks: TryToGetDishFromDishStock not found; feature off.");
                return;
            }

            var harmony = new HarmonyLib.Harmony(Plugin.Guid + ".platestocks");

            harmony.Patch(
                target,
                prefix: new HarmonyLib.HarmonyMethod(
                    HarmonyLib.AccessTools.Method(typeof(PlateStocks), nameof(SkipExcluded))),
                postfix: new HarmonyLib.HarmonyMethod(
                    HarmonyLib.AccessTools.Method(typeof(PlateStocks), nameof(LeaveWalk))));

            log.LogInfo("PlateStocks: patched StaffManager.TryToGetDishFromDishStock.");

            InstallWatchers(log, harmony);
        }
        catch (Exception e)
        {
            log.LogWarning($"PlateStocks: install failed: {e.Message}");
        }
    }

    /// <summary>
    /// Watches the two other ways into a cabinet, and only reports on them.
    ///
    /// The guard above covers <c>StaffManager.GetPlateFromPlateStocks</c> ->
    /// <c>TryToGetDishFromDishStock</c>, which is the "hand a waiter a plate now" path. It is
    /// not obviously the only one: <c>StaffManager.GetAvailableDishStock(DishType)</c> hands
    /// out a whole cabinet, and three waiter states hold a <c>DishStock</c> of their own
    /// (<c>WaiterPickUpDishState</c>, <c>WaiterMoveDishesToShelf</c>,
    /// <c>WaiterWashDishState</c>). Whether any of those ends up drawing a clean plate cannot
    /// be read out of the interop stubs, and guessing would mean either leaving a hole or
    /// blocking a path that also serves the player.
    ///
    /// So this changes nothing and says what happened. <c>DishStock.GetDish</c> is where a
    /// plate actually leaves a cabinet - the callback type is the one
    /// <c>TryToGetDishFromDishStock</c> carries - so a take from an excluded cabinet that
    /// arrives here *without* the walk being in the call is, by construction, a route the
    /// guard cannot see. Neither method is virtual, so no DeclaringType guard is needed.
    /// </summary>
    private static void InstallWatchers(BepInEx.Logging.ManualLogSource log, HarmonyLib.Harmony harmony)
    {
        try
        {
            var take = HarmonyLib.AccessTools.Method(typeof(DishStock), nameof(DishStock.GetDish));
            if (take != null)
                harmony.Patch(take, prefix: new HarmonyLib.HarmonyMethod(
                    HarmonyLib.AccessTools.Method(typeof(PlateStocks), nameof(WatchTake))));
            else
                log.LogWarning("PlateStocks: DishStock.GetDish not found; the leak watcher is off.");

            var pick = HarmonyLib.AccessTools.Method(
                typeof(StaffManager), nameof(StaffManager.GetAvailableDishStock));
            if (pick != null)
                harmony.Patch(pick, postfix: new HarmonyLib.HarmonyMethod(
                    HarmonyLib.AccessTools.Method(typeof(PlateStocks), nameof(WatchChoice))));
            else
                log.LogWarning("PlateStocks: GetAvailableDishStock not found; that watcher is off.");
        }
        catch (Exception e)
        {
            log.LogWarning($"PlateStocks: leak watchers not installed: {e.Message}");
        }
    }

    /// <summary>How deep we are inside the game's plate walk, so a take can be attributed.</summary>
    private static int _inWalk;

    private static void LeaveWalk()
    {
        if (_inWalk > 0) _inWalk--;
    }

    /// <summary>
    /// Reports a plate leaving an excluded cabinet by a route the guard never saw.
    ///
    /// Deliberately never skips the original. The player takes plates out of cabinets by hand
    /// too, and nothing here can tell a waiter's call from theirs - blocking this would take
    /// the reserve away from the person keeping it.
    /// </summary>
    private static void WatchTake(DishStock __instance, string dishName)
    {
        if (__instance == null || ActiveCount() == 0) return;

        try
        {
            if (__instance.DishType != DishType.Plate || !IsExcluded(__instance)) return;

            if (_inWalk > 0)
            {
                // The walk already reported why it allowed this one.
                LogOnce($"PlateStocks: '{Label(__instance)}' was drawn from through the walk " +
                        $"for '{dishName}', which the guard had already allowed.");
                return;
            }

            LogOnce($"PlateStocks: '{Label(__instance)}' is excluded and a '{dishName}' was " +
                    "taken from it WITHOUT the plate walk running - so something other than " +
                    "GetPlateFromPlateStocks reaches this cabinet. If this line appears while " +
                    "no player was taking a plate by hand, that route is the leak.");
        }
        catch (Exception e)
        {
            LogOnce($"PlateStocks: take watcher failed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Reports the staff manager handing out an excluded cabinet wholesale.</summary>
    private static void WatchChoice(DishType dishType, DishStock __result)
    {
        if (__result == null || dishType != DishType.Plate || ActiveCount() == 0) return;

        try
        {
            if (!IsExcluded(__result)) return;

            LogOnce($"PlateStocks: GetAvailableDishStock handed out '{Label(__result)}', which " +
                    "is excluded. This is the cabinet the waiter AI states are given, so it " +
                    "is the likely second route into a reserve.");
        }
        catch (Exception e)
        {
            LogOnce($"PlateStocks: choice watcher failed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// Sends the walk to a cabinet that can actually supply the plate, instead of this one.
    ///
    /// This used to hand the walk to <c>i + 1</c> and let the game carry on from there, which
    /// left a hole big enough to look like the whole feature was broken: when the excluded
    /// cabinet was the *last* index there was no <c>i + 1</c> to go to, so the guard gave up
    /// and the reserve was drained - even with plates sitting in every other cabinet. The
    /// game does not always start its walk at zero, so "the last index" is not a rare corner;
    /// it is wherever the walk happens to have reached.
    ///
    /// Jumping straight to a cabinet that has been checked to hold this plate also fixes the
    /// quieter half of the same bug. <c>i + 1</c> was only ever "not this one" - it could be
    /// empty, or excluded itself, and then the outcome depended on the game resuming the walk
    /// on its own.
    ///
    /// The exclusion is still deliberately not absolute: with nothing else able to supply, the
    /// original runs untouched, so the reserve is used rather than service stopping. A waiter
    /// that is never handed a plate is a worse outcome than a reserve being dipped into, and
    /// it is not a state the player could diagnose from inside the game.
    /// </summary>
    private static bool SkipExcluded(StaffManager __instance, string plateName, int i,
                                     Il2CppSystem.Action<ItemIdentifier> callback)
    {
        // First statement in the method, and paired with LeaveWalk as the postfix, so the two
        // stay balanced however this returns - a postfix still runs when a prefix skips the
        // original. This is only read by WatchTake, to tell a take that came through the walk
        // from one that did not.
        _inWalk++;

        SyncExcluded();
        if (ActiveCount() == 0 || __instance == null) return true;

        try
        {
            var stocks = PlateCabinets(__instance);
            if (stocks == null || i < 0 || i >= stocks.Count) return true;

            var stock = stocks[i];
            if (stock == null || !IsExcluded(stock)) return true;

            int next = IndexThatCanSupply(stocks, plateName, i);
            if (next < 0)
            {
                LogOnce($"PlateStocks: '{Label(stock)}' is excluded but nothing else has " +
                        $"'{plateName}' left, so it is being used anyway.");
                return true;
            }

            LogOnce($"PlateStocks: '{Label(stock)}' is excluded, so the walk was sent from " +
                    $"index {i} to index {next} ('{Label(stocks[next])}') for '{plateName}'.");

            __instance.TryToGetDishFromDishStock(plateName, next, callback);
            return false;
        }
        catch (Exception e)
        {
            LogOnce($"PlateStocks: skip check failed: {e.GetType().Name}: {e.Message}");
            return true;      // never let our guard turn into a new failure mode
        }
    }

    /// <summary>
    /// The index of a cabinet that is not excluded and still holds this plate, or -1.
    ///
    /// Searched forwards from <paramref name="from"/> and then wrapped round, so the answer is
    /// the nearest one the game's own walk would have reached next - but unlike the walk, it
    /// does not run out at the end of the list. Wrapping is safe precisely because the cabinet
    /// returned has been checked to hold the plate: it is not a cabinet the walk skipped for a
    /// reason, it is one it had not got to yet.
    /// </summary>
    private static int IndexThatCanSupply(Il2CppSystem.Collections.Generic.List<DishStock> stocks,
                                          string plateName, int from)
    {
        int count = stocks.Count;

        for (int step = 1; step <= count; step++)
        {
            int at = (from + step) % count;
            if (at == from) continue;

            var other = stocks[at];
            if (other == null || IsExcluded(other)) continue;
            if (CanSupply(other, plateName)) return at;
        }

        return -1;
    }

    /// <summary>Whether this cabinet still holds the plate being asked for.</summary>
    private static bool CanSupply(DishStock stock, string plateName)
    {
        try
        {
            if (stock.CurrentDishes <= 0) return false;

            // An empty plate name means the cabinet was never asked for a specific plate;
            // treat it as able to supply rather than ruling it out on a blank.
            if (string.IsNullOrEmpty(plateName)) return true;

            return string.Equals(stock.DishName, plateName, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static Il2CppSystem.Collections.Generic.List<DishStock> PlateCabinets(StaffManager staff)
    {
        try
        {
            var all = staff.dishStocks;
            if (all == null) return null;

            return all.TryGetValue(DishType.Plate, out var list) ? list : null;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------- the choices

    /// <summary>
    /// A cabinet's identity for the config file: where it stands, rounded to 10cm.
    ///
    /// Rounded because a position reloaded from a save need only be equal to within float
    /// drift, and coarse enough that drift cannot move a cabinet into a neighbouring bucket -
    /// two cabinets can never stand 10cm apart. See the class remarks for why the
    /// restaurant-object id this used to key on cannot be used: it names the model, so every
    /// cabinet of one kind answered to the same key.
    /// </summary>
    public static string KeyOf(DishStock stock)
    {
        if (stock == null) return null;

        try
        {
            var p = stock.transform.position;
            return $"at{Mathf.RoundToInt(p.x * 10f)}_{Mathf.RoundToInt(p.y * 10f)}_{Mathf.RoundToInt(p.z * 10f)}";
        }
        catch { return null; }
    }

    /// <summary>
    /// The cabinet a key names, for a request arriving from another machine.
    ///
    /// Exact first, and then the nearest cabinet within a quarter of a metre of the place the
    /// key describes. The fallback is there because the two machines round independently: a
    /// cabinet standing within float drift of a 10cm boundary can round one way here and the
    /// other way there, and a request that misses by one bucket would otherwise be refused
    /// with nothing to show for it. Quarter of a metre is far below the spacing of two
    /// cabinets, so the nearest is never ambiguous.
    /// </summary>
    public static DishStock CabinetByKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;

        var cabinets = Cabinets();

        foreach (var stock in cabinets)
            if (stock != null && KeyOf(stock) == key) return stock;

        if (!PositionOfKey(key, out var wanted)) return null;

        DishStock best = null;
        float bestSqr = 0.25f * 0.25f;

        foreach (var stock in cabinets)
        {
            if (stock == null) continue;

            try
            {
                float sqr = (stock.transform.position - wanted).sqrMagnitude;
                if (sqr > bestSqr) continue;

                bestSqr = sqr;
                best = stock;
            }
            catch { }
        }

        if (best != null)
            LogOnce($"PlateStocks: '{key}' matched the cabinet at '{KeyOf(best)}' by position; " +
                    "the two machines round its coordinates differently.");

        return best;
    }

    /// <summary>The world position a key was built from, or false if it is not one of ours.</summary>
    private static bool PositionOfKey(string key, out Vector3 position)
    {
        position = default;

        if (key == null || !key.StartsWith("at", StringComparison.Ordinal)) return false;

        var parts = key.Substring(2).Split('_');
        if (parts.Length != 3) return false;

        if (!int.TryParse(parts[0], out int x) ||
            !int.TryParse(parts[1], out int y) ||
            !int.TryParse(parts[2], out int z)) return false;

        position = new Vector3(x * 0.1f, y * 0.1f, z * 0.1f);
        return true;
    }

    /// <summary>How many cabinets are excluded by whichever list is in force here.</summary>
    private static int ActiveCount()
        => PlateStocksNet.FollowsHost ? PlateStocksNet.RemoteCount : Excluded.Count;

    /// <summary>
    /// Whether waiters should pass this cabinet over.
    ///
    /// On a co-op client the answer is the host's, not this machine's: the host runs the staff
    /// AI, so its list is the only one that changes anything. A client that has not been told
    /// yet reports nothing excluded, which is the game's own behaviour rather than a guess.
    /// </summary>
    public static bool IsExcluded(DishStock stock)
    {
        if (PlateStocksNet.FollowsHost) return PlateStocksNet.RemoteHas(stock);

        SyncExcluded();
        if (Excluded.Count == 0) return false;

        string key = KeyOf(stock);
        return key != null && Excluded.Contains(key);
    }

    public static void SetExcluded(DishStock stock, bool exclude)
    {
        // A client cannot decide this for itself - it asks, and the answer arrives as the
        // host's next broadcast.
        if (PlateStocksNet.FollowsHost)
        {
            PlateStocksNet.RequestToggle(stock, exclude);
            return;
        }

        string key = KeyOf(stock);
        if (key == null) return;

        SyncExcluded();

        bool changed = exclude ? Excluded.Add(key) : Excluded.Remove(key);

        if (changed)
        {
            _raw = string.Join("|", Excluded);
            Plugin.PlateStockExcluded.Value = _raw;
            Plugin.PlateStockExcluded.ConfigFile.Save();

            UpdateStatus();
        }

        // Outside the changed check on purpose. A client that asked for a state the host is
        // already in still needs to be told so, or its request sits unconfirmed and the menu
        // reverts as if the key press had been ignored.
        PlateStocksNet.BroadcastNow();
    }

    public static void IncludeAll()
    {
        if (PlateStocksNet.FollowsHost)
        {
            PlateStocksNet.RequestClear();
            return;
        }

        SyncExcluded();
        if (Excluded.Count == 0) return;

        Excluded.Clear();
        _raw = "";
        Plugin.PlateStockExcluded.Value = "";
        Plugin.PlateStockExcluded.ConfigFile.Save();

        UpdateStatus();
        PlateStocksNet.BroadcastNow();
    }

    private static void SyncExcluded()
    {
        string raw = Plugin.PlateStockExcluded.Value ?? "";
        if (raw == _raw) return;

        _raw = raw;
        Excluded.Clear();

        int legacy = 0;
        foreach (var part in raw.Split('|'))
        {
            var key = part.Trim();
            if (key.Length == 0) continue;

            // Keys written before the identity fix were "ro<id>", which named the cabinet's
            // model rather than the cabinet. Such an entry can no longer match anything, and
            // keeping it would only report a count for exclusions that do nothing.
            if (!key.StartsWith("at", StringComparison.Ordinal)) { legacy++; continue; }

            Excluded.Add(key);
        }

        if (legacy > 0)
        {
            _raw = string.Join("|", Excluded);
            Plugin.PlateStockExcluded.Value = _raw;
            Plugin.PlateStockExcluded.ConfigFile.Save();

            Plugin.Logger.LogInfo(
                $"PlateStocks: dropped {legacy} exclusion(s) saved under the old shared " +
                "restaurant-object id - those marked every cabinet of the same model. " +
                "Mark the cabinets you meant again.");
        }

        UpdateStatus();
    }

    /// <summary>What the panel says, kept in step with whose list is in force.</summary>
    private static void UpdateStatus()
    {
        bool client = PlateStocksNet.FollowsHost;
        int count = client ? PlateStocksNet.RemoteCount : Excluded.Count;

        string core = count == 0 ? "chưa loại trừ tủ nào" : $"đang loại trừ {count} tủ";
        string note = PlateStocksNet.StatusSuffix;

        Status = note.Length == 0 ? core : $"{core} ({note})";
        _statusKey = StatusKey(client, count, note);
    }

    /// <summary>Cheap check for whether anything the status text is built from has moved.</summary>
    private static string StatusKey(bool client, int count, string note) => $"{client}|{count}|{note}";

    private static string _statusKey = "";

    private static void RefreshStatus()
    {
        bool client = PlateStocksNet.FollowsHost;
        int count = client ? PlateStocksNet.RemoteCount : Excluded.Count;

        if (StatusKey(client, count, PlateStocksNet.StatusSuffix) != _statusKey) UpdateStatus();
    }

    // ------------------------------------------------------ the floating menu

    /// <summary>How far a cabinet may be and still be picked up by the aim ray.</summary>
    private const float AimRange = 6f;

    /// <summary>The cabinet under the crosshair, or null. Read by the overlay.</summary>
    public static DishStock Aimed { get; private set; }

    /// <summary>Where to draw the floating menu, in GUI coordinates.</summary>
    public static Vector2 AimedScreenPos { get; private set; }

    private static Camera _cam;

    /// <summary>
    /// Tracks the cabinet being looked at and toggles it on the hotkey.
    ///
    /// A key rather than a mouse click, because in play the cursor is locked to the camera and
    /// a click there is the game's own interact - it would open the cabinet rather than reach
    /// a menu. The key is the same arrangement the box tweaks already use.
    /// </summary>
    public static void Tick(CharacterItemInteract interact, bool overlayOpen)
    {
        // Ahead of every early return: the host has to keep broadcasting and a client has to
        // keep listening whether or not anyone is looking at a cabinet.
        PlateStocksNet.Tick();
        RefreshStatus();

        // Keeps the cabinet list - and the one-line identity report built from it - alive
        // without the settings panel having to be open. A co-op disagreement is diagnosed by
        // comparing that line on the two machines, so it has to appear on a machine where
        // nobody opened the panel.
        if (Time.unscaledTime >= _nextIdleScan)
        {
            _nextIdleScan = Time.unscaledTime + IdleScanSeconds;
            Cabinets();
        }

        if (overlayOpen || interact == null) { Aimed = null; return; }

        var stock = AimedCabinet(interact);
        Aimed = stock;

        if (stock == null) return;

        try
        {
            var cam = AimCamera(interact);
            if (cam != null)
            {
                var p = cam.WorldToScreenPoint(stock.transform.position);
                // Behind the camera projects to a point in front of it; drop those rather
                // than drawing the menu over the wrong part of the screen.
                if (p.z <= 0f) { Aimed = null; return; }
                AimedScreenPos = new Vector2(p.x, Screen.height - p.y);
            }
        }
        catch { }

        if (ToggleKeyPressed()) SetExcluded(stock, !IsExcluded(stock));
    }

    private static DishStock AimedCabinet(CharacterItemInteract interact)
    {
        try
        {
            var cam = AimCamera(interact);
            if (cam == null) return null;

            var t = cam.transform;
            if (!Physics.Raycast(t.position, t.forward, out var hit, AimRange, ~0,
                                 QueryTriggerInteraction.Ignore))
                return null;

            // Walk up from whatever the ray struck: the collider is usually on a door or a
            // shelf under the cabinet rather than on the cabinet object itself.
            for (var node = hit.transform; node != null; node = node.parent)
            {
                var stock = node.GetComponent<DishStock>();
                if (stock == null) continue;

                try { if (stock.DishType != DishType.Plate) return null; }
                catch { return null; }

                return stock;
            }
        }
        catch (Exception e)
        {
            LogOnce($"PlateStocks: aim ray failed: {e.GetType().Name}: {e.Message}");
        }

        return null;
    }

    private static Camera AimCamera(CharacterItemInteract interact)
    {
        if (_cam != null) return _cam;

        try { _cam = interact.fpsCamera; } catch { }
        if (_cam == null) { try { _cam = Camera.main; } catch { } }

        return _cam;
    }

    private static bool ToggleKeyPressed()
    {
        try
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) return false;

            string name = Plugin.PlateStockToggleKey.Value;
            if (string.IsNullOrWhiteSpace(name)) return false;

            if (!Enum.TryParse<UnityEngine.InputSystem.Key>(name, true, out var key) ||
                key == UnityEngine.InputSystem.Key.None)
                return false;

            return kb[key].wasPressedThisFrame;
        }
        catch { return false; }
    }

    // -------------------------------------------------------------- the panel

    /// <summary>
    /// The plate cabinets in the restaurant, refreshed on a timer.
    ///
    /// The panel redraws several times a frame, so this cannot be a scene search per call.
    /// A second is short enough that a cabinet bought mid-session turns up promptly.
    /// </summary>
    private const float RescanSeconds = 1f;

    /// <summary>
    /// How often the list is refreshed when nothing is asking for it. Far slower than
    /// RescanSeconds on purpose: this exists only so the identity report and the host's
    /// broadcast have something to work from, and a scene search is not free.
    /// </summary>
    private const float IdleScanSeconds = 5f;

    private static readonly List<DishStock> Cached = new();
    private static float _nextScan;
    private static float _nextIdleScan;

    public static List<DishStock> Cabinets()
    {
        if (Time.unscaledTime < _nextScan) return Cached;
        _nextScan = Time.unscaledTime + RescanSeconds;

        Cached.Clear();

        try
        {
            var found = UnityEngine.Object.FindObjectsByType<DishStock>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            if (found != null)
                for (int i = 0; i < found.Length; i++)
                {
                    var stock = found[i];
                    if (stock == null) continue;

                    try { if (stock.DishType != DishType.Plate) continue; }
                    catch { continue; }

                    Cached.Add(stock);
                }
        }
        catch (Exception e)
        {
            LogOnce($"PlateStocks: cabinet scan failed: {e.GetType().Name}: {e.Message}");
        }

        ReportIdentities();
        return Cached;
    }

    private static int _reportedCabinets = -1;

    /// <summary>
    /// Says once what each cabinet is called, and complains loudly if two answer to the same
    /// name.
    ///
    /// A shared key is exactly the failure that made marking one cabinet mark them all, and
    /// from inside the game it looks like a mod that ignores which cabinet you are aiming at.
    /// It is worth one log line to make that visible as itself.
    /// </summary>
    private static void ReportIdentities()
    {
        if (Cached.Count == 0 || Cached.Count == _reportedCabinets) return;
        _reportedCabinets = Cached.Count;

        try
        {
            var keys = new List<string>(Cached.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var clashes = new HashSet<string>(StringComparer.Ordinal);

            foreach (var stock in Cached)
            {
                string key = KeyOf(stock) ?? "?";
                keys.Add(key);
                if (!seen.Add(key)) clashes.Add(key);
            }

            Plugin.Logger.LogInfo($"PlateStocks: {Cached.Count} plate cabinets - {string.Join(", ", keys)}");

            if (clashes.Count > 0)
                Plugin.Logger.LogWarning(
                    $"PlateStocks: {clashes.Count} cabinet key(s) are shared by more than one " +
                    $"cabinet ({string.Join(", ", clashes)}); marking one will mark the others.");
        }
        catch (Exception e)
        {
            LogOnce($"PlateStocks: could not report cabinet identities: {e.Message}");
        }
    }

    /// <summary>A cabinet described for the panel: what it holds and how full it is.</summary>
    public static string Label(DishStock stock)
    {
        if (stock == null) return "?";

        try
        {
            string name = stock.DishName;
            if (string.IsNullOrEmpty(name)) name = stock.name;
            return $"{name} ({stock.CurrentDishes}/{stock.MaxCapacity})";
        }
        catch { return "?"; }
    }

    private static void LogOnce(string message)
    {
        if (Logged.Add(message)) Plugin.Logger.LogInfo(message);
    }
}
