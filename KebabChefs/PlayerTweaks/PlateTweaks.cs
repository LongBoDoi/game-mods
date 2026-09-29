using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PlayerTweaks;

/// <summary>
/// Tidies items that have been placed freely onto a plate or tray.
///
/// Placed items become children of the plate - ItemIdentifier is parented, with
/// SetParentClientRpc / WaitForHostToParentMe behind it - so the plate's direct children
/// are exactly the things sitting on it. Arranging them is then a matter of moving those
/// transforms into a ring rather than driving the plating event again.
///
/// Each item keeps its own height. Forcing a common Y would sink tall items into the
/// plate and float flat ones, since they rest at different offsets.
/// </summary>
public static class PlateTweaks
{
    /// <summary>Fallback plate radius when the bounds cannot be measured.</summary>
    private const float DefaultRadius = 0.11f;

    private static readonly HashSet<string> Logged = new();
    private static bool _armed = true;

    public static string Status = "chưa hoạt động";

    /// <summary>
    /// Makes the placement preview move in steps rather than smoothly.
    ///
    /// The item is not forced to a slot - you still aim it wherever you like. Only the
    /// resolution changes, so lining two items up becomes a matter of landing on the same
    /// grid coordinate instead of eyeballing it.
    /// </summary>
    public static void Install(BepInEx.Logging.ManualLogSource log)
    {
        try
        {
            var harmony = new HarmonyLib.Harmony(Plugin.Guid + ".plate");

            // These two are the game's placement paths, and probing showed they run every
            // frame while aiming. Build mode is untouched by design: its placement lives on
            // BuildMode (PlaceRestaurantObject / PreviewRestaurantObject) and already has a
            // grid of its own.
            //
            // TrashingEventCtrl is deliberately left alone - snapping an aim point at a bin
            // is not alignment, and could make you miss it.
            Probe(harmony, log, nameof(CharacterItemInteract.PlatingEventCtrl),
                  nameof(PlatingCtrlPrefix));
            Probe(harmony, log, nameof(CharacterItemInteract.PutDownEventCtrl),
                  nameof(PutDownCtrlPrefix));

            log.LogInfo("Plate: patched plating and put-down (ghost snaps to a grid).");
        }
        catch (Exception e)
        {
            log.LogWarning($"Plate: install failed: {e.Message}");
        }
    }

    private static void Probe(HarmonyLib.Harmony harmony, BepInEx.Logging.ManualLogSource log,
                              string method, string handler, bool postfix = false)
    {
        try
        {
            var target = HarmonyLib.AccessTools.Method(typeof(CharacterItemInteract), method);
            if (target == null || target.DeclaringType != typeof(CharacterItemInteract))
            {
                log.LogWarning($"Plate: skipping probe on {method}.");
                return;
            }

            var hm = new HarmonyLib.HarmonyMethod(
                HarmonyLib.AccessTools.Method(typeof(PlateTweaks), handler));

            if (postfix) harmony.Patch(target, postfix: hm);
            else harmony.Patch(target, prefix: hm);
        }
        catch (Exception e)
        {
            log.LogWarning($"Plate: probe on {method} failed: {e.Message}");
        }
    }

    /// <summary>
    /// Quantises the aim point the preview follows, so the ghost still tracks where you
    /// are looking but lands on grid coordinates rather than anywhere at all.
    ///
    /// This runs every frame while you hover a plate and the ghost follows its RaycastHit,
    /// so rounding hit.point before the game reads it is what turns smooth movement into
    /// stepped movement - the same feel as build mode's grid.
    ///
    /// Rounding happens in the plate's local space, so the grid is aligned to the plate
    /// and turns with it instead of being locked to world axes.
    /// </summary>
    private static void PlatingCtrlPrefix(CharacterItemInteract __instance, ref RaycastHit hit)
    {
        Fired("PlatingEventCtrl");
        SnapHit(ref hit, __instance);
    }

    /// <summary>Putting an item down on a counter, table or shelf.</summary>
    private static void PutDownCtrlPrefix(CharacterItemInteract __instance, ref RaycastHit hit)
    {
        Fired("PutDownEventCtrl");
        SnapHit(ref hit, __instance);
    }

    /// <summary>
    /// Rounds the aim point to a grid, in the local space of whatever is being placed on.
    ///
    /// Using the struck surface as the frame means the grid lines up with that counter,
    /// table or plate rather than the world, so a rotated surface gets a rotated grid.
    /// Height is left alone: the item rests on the surface, and rounding Y would push it
    /// into or above it.
    /// </summary>
    private static void SnapHit(ref RaycastHit hit, CharacterItemInteract interact)
    {
        if (!Plugin.PlateAutoAlign.Value) return;

        try
        {
            // Prefer the plate when there is one, so plating keeps its own frame; fall
            // back to whatever collider the ray actually struck.
            var frame = PlateFromHit(hit) ?? SurfaceFromHit(hit);
            if (frame == null && interact != null) frame = ResolveAimedPlate(interact);
            if (frame == null) return;

            // Two modes, switched by the toggle key. Auto-snap always goes flush against
            // the nearest placed item; grid is the default and simply rounds the aim.
            // Auto falls back to the grid only when there is nothing to snap to.
            if (Plugin.PlateEdgeSnap.Value && TryEdgeSnap(ref hit, interact, frame)) return;

            float cell = Mathf.Max(0.005f, Plugin.PlateGridSize.Value);

            var local = frame.InverseTransformPoint(hit.point);
            local.x = Mathf.Round(local.x / cell) * cell;
            local.z = Mathf.Round(local.z / cell) * cell;

            hit.point = frame.TransformPoint(local);
        }
        catch (Exception e)
        {
            LogOnce($"Plate: grid snap failed: {e.Message}");
        }
    }

    /// <summary>
    /// Auto-snap mode: places the ghost flush against the nearest already-placed item.
    ///
    /// The offset is the two half-widths added together, so the items end up touching
    /// rather than overlapping or leaving a gap. The direction is rounded to the frame's
    /// four axes, which is what makes repeated placements line up into rows instead of
    /// fanning out around the neighbour.
    ///
    /// Returns false when the surface is empty, so the first item still lands on the grid.
    /// </summary>
    private static bool TryEdgeSnap(ref RaycastHit hit, CharacterItemInteract interact, Transform frame)
    {
        Transform held = null;
        try { held = interact?.currentItem?.transform; } catch { }

        var placed = GatherNeighbours(hit, frame, held, interact);

        // Distinct states only, so this reports which surfaces find nothing without
        // flooding the log at one line per frame.
        LogOnce($"Plate: auto-snap on '{(frame == null ? "?" : frame.name)}' " +
                $"found {placed.Count} neighbour(s), holding " +
                $"'{(held == null ? "nothing" : held.name)}'.");

        if (placed.Count == 0) return false;

        // Measure from the visual centre, not transform.position. An item's pivot is often
        // at its base or a corner, and offsetting from the pivot is what left a gap
        // between the two cartons even though the arithmetic looked right.
        Transform neighbour = null;
        Vector3 neighbourCentre = default;
        float bestSqr = float.MaxValue;

        foreach (var item in placed)
        {
            if (item == null || item == held) continue;   // never snap to the ghost itself
            if (!TryBounds(item, out var b)) continue;

            float dx = hit.point.x - b.center.x;
            float dz = hit.point.z - b.center.z;
            float sqr = dx * dx + dz * dz;

            if (sqr < bestSqr) { bestSqr = sqr; neighbour = item; neighbourCentre = b.center; }
        }

        if (neighbour == null) return false;

        var toCursor = new Vector3(hit.point.x - neighbourCentre.x, 0f, hit.point.z - neighbourCentre.z);
        if (toCursor.sqrMagnitude < 1e-8f) return false;   // dead centre: no direction
        toCursor.Normalize();

        // Push along the neighbour's own axes, not the plate's.
        //
        // Two boxes only sit flush when the offset runs along the face they meet on. The
        // items are usually turned relative to the plate, so using the plate's axes moved
        // the ghost diagonally across the carton's face and left a wedge of a gap no
        // amount of correcting the distance could close.
        var dir = NearestAxis(neighbour, toCursor);

        // Reach measured on the items' own oriented boxes, not their world AABBs.
        float gap = ReachAlong(neighbour, neighbourCentre, dir)
                  + ReachAlong(held, HeldCentre(held), dir);
        var wanted = neighbourCentre + dir * gap;

        // hit.point positions the item's pivot, so convert the wanted centre back.
        var pivotOffset = Vector3.zero;
        if (held != null && TryBounds(held, out var hb))
            pivotOffset = new Vector3(hb.center.x - held.position.x, 0f, hb.center.z - held.position.z);

        hit.point = new Vector3(wanted.x - pivotOffset.x, hit.point.y, wanted.z - pivotOffset.z);
        return true;
    }

    /// <summary>How far around the aim point to look for something to sit flush against.</summary>
    private const float NeighbourSearchRadius = 1.0f;

    /// <summary>
    /// How far an item's base may sit from the aimed surface height and still count as a
    /// neighbour. Wide enough for the two not to be resting on a perfectly level plane,
    /// narrow enough that a shelf below the counter is not treated as the same surface.
    /// </summary>
    private const float SameSurfaceTolerance = 0.3f;

    private static readonly List<Transform> NeighbourBuffer = new();

    /// <summary>
    /// The already-placed items near the aim point.
    ///
    /// This used to be the surface's own children, which is why auto-snap only ever worked
    /// with milk cartons: those are placed onto something that parents them, whereas an
    /// item put down on an ordinary counter or table is not reparented at all. The list
    /// came back empty everywhere else and the mode quietly fell through to the grid.
    ///
    /// Proximity in the world is the property actually wanted - "the thing next to where I
    /// am pointing" - and it holds for every item type regardless of how the game parents
    /// it. The surface's children are still merged in, since on a plate they are the right
    /// answer and may sit outside the sphere.
    /// </summary>
    private static List<Transform> GatherNeighbours(RaycastHit hit, Transform frame,
                                                    Transform held, CharacterItemInteract interact)
    {
        NeighbourBuffer.Clear();

        try
        {
            var hits = Physics.OverlapSphere(hit.point, NeighbourSearchRadius, ~0,
                                             QueryTriggerInteraction.Ignore);
            if (hits != null)
            {
                foreach (var col in hits)
                {
                    if (col == null) continue;

                    var item = ItemRootOf(col.transform);
                    if (item == null) continue;

                    // Only things resting at the height being aimed at. Without this the
                    // sphere reaches through the counter and snaps to whatever is on the
                    // shelf beneath it.
                    if (!TryBounds(item, out var b)) continue;
                    if (Mathf.Abs(b.min.y - hit.point.y) > SameSurfaceTolerance) continue;

                    AddNeighbour(item, frame, held, interact);
                }
            }
        }
        catch (Exception e)
        {
            LogOnce($"Plate: neighbour search failed: {e.Message}");
        }

        // Children of the surface as well - on a plate these are exactly the items on it.
        if (frame != null)
            foreach (var child in GatherPlacedItems(frame))
                AddNeighbour(child, frame, held, interact);

        return NeighbourBuffer;
    }

    private static void AddNeighbour(Transform item, Transform frame, Transform held,
                                     CharacterItemInteract interact)
    {
        if (item == null) return;
        if (held != null && (item == held || item.IsChildOf(held) || held.IsChildOf(item))) return;

        // The surface being placed onto is not a neighbour. A plate is itself an item, so
        // without this the ghost would try to sit flush against the plate it is going on.
        if (frame != null && (item == frame || frame.IsChildOf(item))) return;

        if (IsCarried(item, interact)) return;

        foreach (var existing in NeighbourBuffer)
            if (existing == item) return;

        NeighbourBuffer.Add(item);
    }

    /// <summary>The nearest ItemIdentifier at or above a collider, or null.</summary>
    private static Transform ItemRootOf(Transform t)
    {
        try
        {
            for (var cur = t; cur != null; cur = cur.parent)
                if (cur.GetComponent<ItemIdentifier>() != null) return cur;
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Whether an item is in someone's hands rather than on the surface. Carried items
    /// hang off the player, and leaning over a counter puts them well inside the search
    /// sphere - snapping to one of those would follow you around instead of the counter.
    /// </summary>
    private static bool IsCarried(Transform item, CharacterItemInteract interact)
    {
        try
        {
            for (var cur = item; cur != null; cur = cur.parent)
                if (cur.GetComponent<CharacterItemInteract>() != null) return true;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// The horizontal axis of <paramref name="of"/> (±right, ±forward) closest to a
    /// direction. Flattened to the horizontal plane so a tilted item still yields a
    /// sideways push rather than one angled into the surface.
    /// </summary>
    private static Vector3 NearestAxis(Transform of, Vector3 dir)
    {
        var right = Vector3.ProjectOnPlane(of.right, Vector3.up).normalized;
        var fwd = Vector3.ProjectOnPlane(of.forward, Vector3.up).normalized;

        var best = right;
        float bestDot = float.MinValue;

        foreach (var axis in new[] { right, -right, fwd, -fwd })
        {
            if (axis.sqrMagnitude < 1e-6f) continue;
            float d = Vector3.Dot(axis, dir);
            if (d > bestDot) { bestDot = d; best = axis; }
        }
        return best;
    }

    private static Vector3 HeldCentre(Transform t)
        => TryBounds(t, out var b) ? b.center : (t == null ? Vector3.zero : t.position);

    /// <summary>
    /// How far an item actually reaches from <paramref name="centre"/> along a direction.
    ///
    /// Renderer.bounds is a world-space AABB, which for a rotated object is considerably
    /// larger than the object itself - a carton turned 30 degrees reports a box wide
    /// enough to contain it diagonally. Reserving that on both sides is what left a
    /// carton-wide gap between the two.
    ///
    /// So this uses each renderer's localBounds with its own transform axes, giving the
    /// support of the real oriented box. For an upright carton approached side-on that is
    /// exactly half its width, whatever angle it is turned to.
    /// </summary>
    private static float ReachAlong(Transform item, Vector3 centre, Vector3 dir)
    {
        if (item == null) return 0f;

        try
        {
            var renderers = item.GetComponentsInChildren<Renderer>(false);
            if (renderers == null) return 0f;

            float reach = 0f;
            bool any = false;

            foreach (var rend in renderers)
            {
                if (rend == null) continue;

                var t = rend.transform;
                var lb = rend.localBounds;
                var ex = Vector3.Scale(lb.extents, t.lossyScale);

                // Support of an oriented box: each local axis contributes by how much it
                // points along dir.
                float half = Mathf.Abs(Vector3.Dot(dir, t.right)) * ex.x
                           + Mathf.Abs(Vector3.Dot(dir, t.up)) * ex.y
                           + Mathf.Abs(Vector3.Dot(dir, t.forward)) * ex.z;

                float from = Vector3.Dot(t.TransformPoint(lb.center) - centre, dir);

                reach = any ? Mathf.Max(reach, from + half) : from + half;
                any = true;
            }

            if (any) return Mathf.Max(0f, reach);
        }
        catch { /* fall through to the world box */ }

        // Fallback: the old AABB estimate. Too generous for rotated items, but better
        // than snapping items on top of each other if localBounds is unavailable.
        if (!TryBounds(item, out var b)) return 0f;
        return 0.5f * (Mathf.Abs(dir.x) * b.size.x + Mathf.Abs(dir.z) * b.size.z);
    }

    /// <summary>Combined world bounds of an item's renderers.</summary>
    private static bool TryBounds(Transform t, out Bounds bounds)
    {
        bounds = default;
        if (t == null) return false;

        try
        {
            var renderers = t.GetComponentsInChildren<Renderer>(false);
            if (renderers == null) return false;

            bool any = false;
            foreach (var rend in renderers)
            {
                if (rend == null) continue;
                if (!any) { bounds = rend.bounds; any = true; }
                else bounds.Encapsulate(rend.bounds);
            }
            return any;
        }
        catch { return false; }
    }

    private static Transform SurfaceFromHit(RaycastHit hit)
    {
        try
        {
            var col = hit.collider;
            return col == null ? null : col.transform;
        }
        catch { return null; }
    }

    /// <summary>Walks up from whatever the ray struck, looking for a plate.</summary>
    private static Transform PlateFromHit(RaycastHit hit)
    {
        try
        {
            var col = hit.collider;
            if (col == null) return null;

            for (var t = col.transform; t != null; t = t.parent)
                if (t.GetComponent<Plate>() != null) return t;
        }
        catch { }
        return null;
    }

    private static void HoldPositionPostfix() => Fired("GetHoldPosition");

    /// <summary>Reports which positioning call actually runs, and how often.</summary>
    private static void Fired(string what)
    {
        if (!_fireCounts.TryGetValue(what, out int n)) n = 0;
        n++;
        _fireCounts[what] = n;

        if (n == 1 || n == 120)
            Plugin.Logger.LogInfo($"Plate probe: {what} fired {n}x.");
    }

    private static readonly Dictionary<string, int> _fireCounts = new();

    /// <summary>The plate the player is currently aiming at, if any.</summary>
    private static Transform ResolveAimedPlate(CharacterItemInteract interact)
    {
        foreach (var candidate in new[] { interact.interactingItem, interact.LastInteractingItem })
        {
            if (candidate == null) continue;
            try
            {
                var t = candidate.transform;
                if (t != null && IsPlate(t)) return t;
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Anything items can be arranged on: plates, dessert and dish trays, and - by the
    /// last test - any other item that already has things parented to it.
    ///
    /// That behavioural fallback is deliberate. There is no dedicated baking-tray class in
    /// the assembly, so a fixed type list would silently miss it and any similar container
    /// added later. "Has items sitting on it" is the property we actually care about.
    /// </summary>
    /// <summary>
    /// Whatever is being aimed at, for a caller that already knows the game will accept a
    /// placement there.
    ///
    /// <see cref="ResolvePlate"/> is preferred first, since it identifies known containers.
    /// The fallback matters for an empty baking tray: it has no Plate component, is neither
    /// tray type, and has nothing parented to it yet, so every type test says no - and it is
    /// exactly the case where you most want to put an armful down. When the game has already
    /// offered a plating at the thing under the crosshair, that is better evidence than any
    /// type test we can write.
    /// </summary>
    internal static Transform ResolvePlacementTarget(CharacterItemInteract interact)
    {
        var known = ResolvePlate(interact);
        if (known != null) return known;

        if (interact == null) return null;

        try
        {
            foreach (var candidate in new[] { interact.interactingItem, interact.LastInteractingItem })
            {
                if (candidate == null) continue;
                var t = candidate.transform;
                if (t != null) return t;
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Something items can be put onto - used to recognise a plate or tray held in hand.
    ///
    /// Wider than <see cref="IsPlate"/> by one test: anything carrying an Attacher slot. That
    /// is the component items actually attach to, so it identifies a baking tray while it is
    /// still empty, which every other test here misses - a bare tray has no Plate component,
    /// is neither tray type, and has nothing parented to it to infer from.
    /// </summary>
    internal static bool IsContainer(Transform t)
    {
        if (t == null) return false;
        if (IsPlate(t)) return true;

        try { return t.GetComponentInChildren<Attacher>(true) != null; }
        catch { return false; }
    }

    private static bool IsPlate(Transform t)
    {
        try
        {
            if (t.GetComponent<Plate>() != null) return true;
            if (t.GetComponentInChildren<Plate>(true) != null) return true;

            var id = t.GetComponent<ItemIdentifier>();
            if (id != null)
            {
                if (id.TryCast<DessertTray>() != null) return true;
                if (id.TryCast<DishTray>() != null) return true;
            }

            return HasChildItems(t);
        }
        catch { return false; }
    }

    /// <summary>Which rule matched, so an unrecognised tray is diagnosable from the log.</summary>
    private static string DescribeContainer(Transform t)
    {
        try
        {
            if (t.GetComponent<Plate>() != null) return "Plate";
            if (t.GetComponentInChildren<Plate>(true) != null) return "Plate(child)";

            var id = t.GetComponent<ItemIdentifier>();
            if (id != null)
            {
                if (id.TryCast<DessertTray>() != null) return "DessertTray";
                if (id.TryCast<DishTray>() != null) return "DishTray";
            }

            return "generic container";
        }
        catch { return "?"; }
    }

    /// <summary>True when at least one direct child is a placed item.</summary>
    private static bool HasChildItems(Transform t)
    {
        try
        {
            for (int i = 0; i < t.childCount; i++)
            {
                var child = t.GetChild(i);
                if (child == null) continue;
                if (child.GetComponent<ItemIdentifier>() != null) return true;
            }
        }
        catch { }
        return false;
    }

    public static void Tick(CharacterItemInteract interact, bool overlayOpen)
    {
        if (overlayOpen || interact == null) return;

        // Switches between the two snapping modes rather than turning snapping off.
        // Edge-triggered, so holding the key does not flap the setting every frame.
        if (KeyPressedThisFrame(Plugin.PlateSnapToggleKey.Value))
        {
            bool auto = !Plugin.PlateEdgeSnap.Value;
            Plugin.PlateEdgeSnap.Value = auto;
            Plugin.PlateEdgeSnap.ConfigFile.Save();

            Status = auto ? "chế độ ghép cạnh" : "chế độ lưới";
            Plugin.Logger.LogInfo($"Plate: snapping switched to {(auto ? "auto-snap" : "grid")} mode.");
        }

        if (!KeyHeld())
        {
            _armed = true;
            return;
        }

        if (!_armed) return;      // one align per press
        _armed = false;

        Align(interact);
    }

    private static bool KeyPressedThisFrame(string name)
    {
        try
        {
            var kb = Keyboard.current;
            if (kb == null || string.IsNullOrWhiteSpace(name)) return false;

            if (!Enum.TryParse<Key>(name, true, out var key) || key == Key.None) return false;
            return kb[key].wasPressedThisFrame;
        }
        catch { return false; }
    }

    private static bool KeyHeld()
    {
        try
        {
            var kb = Keyboard.current;
            if (kb == null) return false;

            string name = Plugin.PlateAlignKey.Value;
            if (string.IsNullOrWhiteSpace(name)) return false;

            if (!Enum.TryParse<Key>(name, true, out var key) || key == Key.None) return false;
            return kb[key].isPressed;
        }
        catch { return false; }
    }

    public static void Align(CharacterItemInteract interact)
    {
        var plate = ResolvePlate(interact);
        if (plate == null)
        {
            Status = "hãy nhìn vào một chiếc đĩa hoặc khay";
            return;
        }

        AlignPlate(plate);
    }

    /// <summary>
    /// Arranges a plate that has already been identified.
    ///
    /// Split out of <see cref="Align"/> so place-all can arrange the container it just
    /// filled rather than whatever the player happens to be looking at by the time the last
    /// item lands - they may well have turned away already.
    /// </summary>
    /// <param name="minSlots">
    /// Lay the grid out for at least this many items, even if fewer are there yet.
    ///
    /// Place-all passes the final total so the layout is decided once. Sizing the grid to
    /// whatever happens to be on the plate at the moment would re-flow it on every arrival -
    /// two items, then three, then four, each a different grid - and the whole armful would
    /// visibly shuffle about as it landed. With the final count known up front each item
    /// drops straight into the slot it is going to keep.
    /// </param>
    internal static void AlignPlate(Transform plate, int minSlots = 0)
    {
        try
        {
            if (plate == null)
            {
                Status = "hãy nhìn vào một chiếc đĩa hoặc khay";
                return;
            }

            var items = GatherPlacedItems(plate);
            if (items.Count == 0)
            {
                Status = $"không có gì trên {plate.name}";
                return;
            }

            DumpHierarchy(plate, items);

            _syncOk = 0;
            _syncFail = 0;

            // Straighten before measuring. An item's footprint depends on which way it is
            // turned, so a carton sitting at 40 degrees reports a wider box than the same
            // carton squared up - measuring first would size the grid to the crooked one.
            float yaw = plate.eulerAngles.y;
            foreach (var item in items) FaceYaw(item, yaw);

            float plateRadius = MeasurePlateRadius(plate, items);
            float itemRadius = MeasureLargestItemRadius(items);

            // Keep whole items inside the rim, not just their centres. Without allowing
            // for the item's own size, anything on the outer ring hangs over the edge.
            float usable = Mathf.Max(plateRadius - itemRadius, plateRadius * 0.2f);

            Arrange(plate.transform, items, usable, minSlots);

            Status = $"đã canh thẳng {items.Count} vật phẩm trên {plate.name}";
            Plugin.Logger.LogInfo(
                $"Plate: aligned {items.Count} item(s) on '{plate.name}' [{DescribeContainer(plate)}] " +
                $"(plate={plateRadius:0.###}, item={itemRadius:0.###}, usable={usable:0.###}, " +
                $"synced={_syncOk}, unsynced={_syncFail}).");
        }
        catch (Exception e)
        {
            Status = "canh thẳng thất bại - xem log";
            LogOnce($"Plate: align failed: {e.Message}");
        }
    }

    /// <summary>The plate or tray being looked at, or the one in hand as a fallback.</summary>
    internal static Transform ResolvePlate(CharacterItemInteract interact)
    {
        if (interact == null) return null;

        ItemIdentifier[] candidates;
        try
        {
            candidates = new[] { interact.interactingItem, interact.LastInteractingItem, interact.currentItem };
        }
        catch { return null; }

        foreach (var candidate in candidates)
        {
            if (candidate == null) continue;

            try
            {
                var t = candidate.transform;
                if (t == null) continue;

                if (IsPlate(t)) return t;
                if (candidate.TryCast<PlateItem>() != null) return t;
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// The items sitting on the plate - the topmost ItemIdentifier down each branch.
    ///
    /// Direct children alone were not enough. Some item types are not parented to the
    /// plate itself but to an Attacher slot underneath it, and those slots are laid out
    /// radially in the prefab. Those items were never gathered, so align skipped them
    /// and they kept the game's own ring arrangement - which is the "some types still
    /// come out in a sunflower" case.
    ///
    /// Descending stops at the first ItemIdentifier on each branch, so an ingredient
    /// stacked on another item still belongs to that item and the stack is moved as one
    /// piece rather than pulled apart.
    /// </summary>
    private static List<Transform> GatherPlacedItems(Transform plate)
    {
        var list = new List<Transform>();
        CollectItems(plate, list, 0);
        return list;
    }

    /// <summary>
    /// How many items are currently sitting on a container.
    ///
    /// Place-all uses this to tell when the last item it sent has finished its attach
    /// animation and been parented, which is the earliest moment arranging can work. Waiting
    /// a fixed time instead meant either arranging too early or making the player wait.
    /// </summary>
    internal static int CountPlacedItems(Transform plate)
    {
        if (plate == null) return 0;
        try { return GatherPlacedItems(plate).Count; }
        catch { return 0; }
    }

    /// <summary>The items sitting on a container. Place-all needs the items, not the count.</summary>
    internal static List<Transform> PlacedItems(Transform plate)
    {
        if (plate == null) return new List<Transform>();
        try { return GatherPlacedItems(plate); }
        catch { return new List<Transform>(); }
    }

    /// <summary>Depth cap, so a deep prop hierarchy cannot turn this into a full scene walk.</summary>
    private const int MaxGatherDepth = 4;

    private static void CollectItems(Transform node, List<Transform> into, int depth)
    {
        if (node == null || depth > MaxGatherDepth) return;

        for (int i = 0; i < node.childCount; i++)
        {
            var child = node.GetChild(i);
            if (child == null) continue;

            ItemIdentifier id = null;
            try { id = child.GetComponent<ItemIdentifier>(); } catch { }

            if (id != null) { into.Add(child); continue; }   // an item: take it, do not descend

            // A holder - an Attacher slot, or a plain grouping object. Keep looking.
            CollectItems(child, into, depth + 1);
        }
    }

    /// <summary>
    /// One-off dump of what lives under a plate, so an arrangement that still comes out
    /// wrong can be traced to the hierarchy rather than guessed at.
    /// </summary>
    private static void DumpHierarchy(Transform plate, List<Transform> items)
    {
        try
        {
            var lines = new List<string>();

            void Walk(Transform node, int depth)
            {
                if (node == null || depth > MaxGatherDepth) return;

                for (int i = 0; i < node.childCount && lines.Count < 40; i++)
                {
                    var c = node.GetChild(i);
                    if (c == null) continue;

                    bool isItem = false, isAttacher = false;
                    try { isItem = c.GetComponent<ItemIdentifier>() != null; } catch { }
                    try { isAttacher = c.GetComponent<Attacher>() != null; } catch { }

                    bool gathered = items.Contains(c);

                    lines.Add($"{new string(' ', depth * 2)}{c.name}" +
                              $"{(isItem ? " [item]" : "")}{(isAttacher ? " [attacher]" : "")}" +
                              $"{(gathered ? " <-aligned" : "")}");

                    if (!isItem) Walk(c, depth + 1);
                }
            }

            Walk(plate, 0);
            LogOnce($"Plate hierarchy of '{plate.name}':\n  " + string.Join("\n  ", lines));
        }
        catch (Exception e)
        {
            LogOnce($"Plate: hierarchy dump failed: {e.Message}");
        }
    }

    /// <summary>
    /// The plate's own radius, ignoring anything sitting on it.
    ///
    /// Measuring the first renderer under the plate picked up a placed item instead, which
    /// inflated the radius and threw the layout off the plate entirely.
    /// </summary>
    private static float MeasurePlateRadius(Transform plate, List<Transform> items)
    {
        try
        {
            bool any = false;
            Bounds bounds = default;

            var renderers = plate.GetComponentsInChildren<Renderer>(false);
            if (renderers != null)
            {
                foreach (var rend in renderers)
                {
                    if (rend == null) continue;
                    if (BelongsToAnItem(rend.transform, plate, items)) continue;

                    if (!any) { bounds = rend.bounds; any = true; }
                    else bounds.Encapsulate(rend.bounds);
                }
            }

            if (any)
            {
                var size = bounds.size;
                float r = Mathf.Max(size.x, size.z) * 0.5f;
                if (r > 0.01f) return r;
            }
        }
        catch { }
        return DefaultRadius;
    }

    /// <summary>True when this renderer sits under one of the placed items.</summary>
    private static bool BelongsToAnItem(Transform t, Transform plate, List<Transform> items)
    {
        for (var cur = t; cur != null && cur != plate; cur = cur.parent)
        {
            foreach (var item in items)
                if (cur == item) return true;
        }
        return false;
    }

    /// <summary>Half the footprint of the biggest item, so the ring can allow for it.</summary>
    private static float MeasureLargestItemRadius(List<Transform> items)
    {
        float largest = 0f;

        foreach (var item in items)
        {
            try
            {
                var renderers = item.GetComponentsInChildren<Renderer>(false);
                if (renderers == null) continue;

                foreach (var rend in renderers)
                {
                    if (rend == null) continue;
                    var size = rend.bounds.size;
                    largest = Mathf.Max(largest, Mathf.Max(size.x, size.z) * 0.5f);
                }
            }
            catch { }
        }

        return largest;
    }

    /// <summary>
    /// Lays the items out as a centred rectangular grid.
    ///
    /// The grid is fitted to the square that sits inside the plate's usable circle
    /// (side = radius * sqrt(2)), so the corner items stay on the plate. Sizing it to the
    /// full diameter instead would push the corners over the rim, which is the same class
    /// of overflow that scattered the first version across the table.
    ///
    /// A trailing partial row is centred rather than left-aligned, so seven items read as
    /// a tidy 3/3/1 block instead of an L shape.
    /// </summary>
    private static void Arrange(Transform plate, List<Transform> items, float maxRadius,
                                int minSlots = 0)
    {
        var centre = plate.position;

        // Every slot position below depends on this count and nothing else, which is what
        // makes a part-filled plate lay out identically to the finished one.
        int slots = Mathf.Max(items.Count, minSlots);

        if (slots == 1)
        {
            if (items.Count == 1)
                MoveTo(items[0], new Vector3(centre.x, items[0].position.y, centre.z));
            return;
        }

        int cols = Mathf.CeilToInt(Mathf.Sqrt(slots));
        int rows = Mathf.CeilToInt(slots / (float)cols);

        float side = maxRadius * 1.41421356f;      // inscribed square of the usable circle
        float stepX = side / cols;
        float stepZ = side / rows;

        // Rotate the grid with the plate so it lines up with the rim, not the world axes.
        float yaw = plate.eulerAngles.y * Mathf.Deg2Rad;
        float cos = Mathf.Cos(yaw);
        float sin = Mathf.Sin(yaw);

        for (int i = 0; i < items.Count; i++)
        {
            int row = i / cols;
            int col = i % cols;

            // Centre the last row when it is not full. Measured against the slot count, not
            // the items present, so an arriving item does not shift the ones already down.
            int inThisRow = Mathf.Min(cols, slots - row * cols);
            float rowWidth = inThisRow * stepX;

            float localX = -rowWidth * 0.5f + stepX * (col + 0.5f);
            float localZ = -side * 0.5f + stepZ * (row + 0.5f);

            var target = new Vector3(
                centre.x + localX * cos + localZ * sin,
                items[i].position.y,        // keep each item's own resting height
                centre.z - localX * sin + localZ * cos);

            MoveTo(items[i], target);
        }
    }

    /// <summary>
    /// What ItemIdentifier.Rotate does to a transform. The interop assemblies carry no
    /// method bodies, so "rotate to" versus "rotate by" cannot be read - it is measured
    /// once, the first time an item is straightened, and remembered.
    /// </summary>
    private enum RotateKind { Unknown, Absolute, Relative, Unusable }

    private static RotateKind _rotateKind = RotateKind.Unknown;

    /// <summary>
    /// Turns an item to a common yaw, leaving its tilt alone.
    ///
    /// Position alone was not enough: the items landed on a tidy grid but each kept
    /// whatever angle it was dropped at, so four milk cartons read as a crooked fan rather
    /// than a block. Only the yaw is overwritten - an item's pitch and roll are how it
    /// rests on the surface, and forcing those would lay upright things on their side.
    /// </summary>
    private static void FaceYaw(Transform item, float yaw)
    {
        if (item == null) return;

        try
        {
            var e = item.rotation.eulerAngles;
            var target = Quaternion.Euler(e.x, yaw, e.z);

            // Already square to within a degree: leave it, and do not spend an RPC on it.
            if (Quaternion.Angle(item.rotation, target) < 1f) return;

            ApplyRotation(item, target);
        }
        catch (Exception ex)
        {
            LogOnce($"Plate: could not straighten '{item.name}': {ex.Message}");
        }
    }

    private static void ApplyRotation(Transform item, Quaternion target)
    {
        ItemIdentifier id = null;
        try { id = item.GetComponent<ItemIdentifier>(); } catch { }

        if (id == null || _rotateKind == RotateKind.Unusable)
        {
            item.rotation = target;      // local only; nothing better is available
            _syncFail++;
            return;
        }

        var before = item.rotation;

        try
        {
            switch (_rotateKind)
            {
                case RotateKind.Unknown:
                    // Classify by effect: call it with the wanted rotation and see where
                    // the transform ends up. Guessing wrong here would either double-apply
                    // the turn or send the wrong value to the other players.
                    id.Rotate(target);

                    if (Quaternion.Angle(item.rotation, target) < 1f)
                        _rotateKind = RotateKind.Absolute;
                    else if (Quaternion.Angle(item.rotation, before * target) < 1f)
                        _rotateKind = RotateKind.Relative;
                    else
                        _rotateKind = RotateKind.Unusable;

                    Plugin.Logger.LogInfo(
                        $"Plate: ItemIdentifier.Rotate classified as {_rotateKind} " +
                        $"(before={before.eulerAngles.y:0.#}, after={item.rotation.eulerAngles.y:0.#}, " +
                        $"wanted={target.eulerAngles.y:0.#}).");
                    break;

                case RotateKind.Absolute:
                    id.Rotate(target);
                    break;

                case RotateKind.Relative:
                    // Same end state, expressed as a delta from where it is now.
                    id.Rotate(Quaternion.Inverse(before) * target);
                    break;
            }

            _syncOk++;
        }
        catch (Exception e)
        {
            _rotateKind = RotateKind.Unusable;
            _syncFail++;
            LogOnce($"Plate: ItemIdentifier.Rotate threw ({e.Message}); straightening locally only.");
        }

        // Assert the result regardless of which route ran, so the local view is right even
        // if the game's call did something other than what was measured.
        item.rotation = target;
    }

    private static void MoveTo(Transform item, Vector3 position)
    {
        // Already there. Worth checking because place-all re-arranges on every arrival, and
        // without this each pass would send a reposition RPC for every item on the plate
        // rather than the one that just landed.
        try
        {
            if (item != null && (item.position - position).sqrMagnitude < 1e-6f) return;
        }
        catch { }

        // Move locally first so the arrangement is instant for the player who pressed the
        // key, then tell the server. The reverse order would leave a visible lag on your
        // own screen while the round trip completes.
        try { item.position = position; }
        catch (Exception e) { LogOnce($"Plate: could not move '{item.name}': {e.Message}"); }

        Replicate(item, position);
    }

    private static ItemManager _itemManager;
    private static int _syncOk, _syncFail;

    /// <summary>
    /// Tells the server where the item went, so the other players see the same plate.
    ///
    /// Setting transform.position is a purely local change - the alignment looked right on
    /// the machine that pressed the key and nowhere else. ItemManager.RepositionItemServerRpc
    /// is the game's own networked move, so the host stays authoritative over item positions
    /// exactly as it does for pickups.
    /// </summary>
    private static void Replicate(Transform item, Vector3 position)
    {
        try
        {
            var mgr = ResolveItemManager();
            if (mgr == null) { _syncFail++; return; }

            GameItem gi = null;
            try { gi = item.GetComponent<GameItem>(); } catch { }

            // The RPC addresses items by game ID, so an item without one cannot be sent.
            if (gi == null) { _syncFail++; return; }

            mgr.RepositionItemServerRpc(gi.gameID, position);
            _syncOk++;
        }
        catch (Exception e)
        {
            _syncFail++;
            LogOnce($"Plate: reposition RPC failed: {e.Message}");
        }
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
            LogOnce($"Plate: ItemManager lookup failed: {e.Message}");
        }
        return _itemManager;
    }

    private static void LogOnce(string message)
    {
        if (Logged.Add(message)) Plugin.Logger.LogWarning(message);
    }
}
