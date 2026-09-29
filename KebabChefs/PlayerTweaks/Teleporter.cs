using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace PlayerTweaks;

/// <summary>
/// Finds interesting places in the loaded scene and moves the player to them.
///
/// Locations are discovered from the game's own components rather than hardcoded
/// coordinates, so this keeps working across different maps and after game updates.
/// The key one is <c>Store</c>, which carries a <c>StoreType</c> (Grocery, Butcher,
/// Market, Festival) and a <c>StorePoint</c> transform marking where to stand.
/// </summary>
public static class Teleporter
{
    public sealed class Location
    {
        public string Name;
        public Vector3 Position;
        public bool Custom;

        /// <summary>
        /// Set for the home door. Moving the player there by hand skips the game's own
        /// transition, which is what loads and unloads the house model - so the house can
        /// end up missing or left rendered. When this is present we call the game's
        /// MoveToHome instead of teleporting.
        /// </summary>
        public ApartmentDoorInteraction Door;
    }

    /// <summary>Per-category cap so one repeated prop cannot flood the list.</summary>
    private const int MaxPerCategory = 4;

    // ------------------------------------------------------------- discovery

    public static List<Location> Discover()
    {
        var found = new List<Location>();

        AddStores(found);
        AddHomes(found);
        AddSimple<ATMInteraction>(found, "Cây ATM");
        AddRestaurantDoor(found);

        return found;
    }

    private static void AddStores(List<Location> into)
    {
        var stores = SafeFind<Store>();
        if (stores == null) return;

        var counts = new Dictionary<string, int>();
        foreach (var s in stores)
        {
            if (s == null) continue;

            var point = s.StorePoint != null ? s.StorePoint : s.transform;
            if (point == null) continue;

            string label = StoreLabel(s.StoreType);
            counts.TryGetValue(label, out int n);
            if (n >= MaxPerCategory) continue;
            counts[label] = n + 1;

            into.Add(new Location
            {
                Name = n == 0 ? label : $"{label} {n + 1}",
                Position = point.position,
            });
        }
    }

    private static string StoreLabel(StoreType type) => type switch
    {
        StoreType.Grocery => "Cửa hàng tạp hóa",
        StoreType.Butcher => "Cửa hàng thịt",
        StoreType.Market => "Chợ / Hoa quả",
        StoreType.Festival => "Quầy lễ hội",
        StoreType.AsianMarket => "Chợ châu Á",
        _ => $"Cửa hàng ({type})",
    };

    /// <summary>Only the outside door. The indoor counterpart is a duplicate for our purposes.</summary>
    private static void AddHomes(List<Location> into)
    {
        var doors = SafeFind<ApartmentDoorInteraction>();
        if (doors == null) return;

        foreach (var d in doors)
        {
            if (d == null || d.IsIndoor) continue;

            var point = d.HousePoint != null ? d.HousePoint : d.transform;
            if (point == null) continue;

            // Name kept as-is so existing hotkey bindings, which key off the name,
            // keep working.
            into.Add(new Location { Name = "Nhà (Cửa)", Position = point.position, Door = d });
            return;
        }
    }

    /// <summary>
    /// Just the customer entrance.
    ///
    /// A plain "restaurantDoor" filter returned four, because restricted staff doors and
    /// house doors also qualify, and a double door is modelled as two separate
    /// interactions pointing at each other via doubleDoor. Excluding the restricted and
    /// house variants and collapsing double-door pairs leaves the front entrance.
    /// </summary>
    private static void AddRestaurantDoor(List<Location> into)
    {
        var doors = SafeFind<RestaurantDoorInteraction>();
        if (doors == null) return;

        // The map has a dozen qualifying doors because every restaurant building has one,
        // and there are two the player can own - the small and the large. Anchor on the
        // one actually in use (GameData.ActiveRestaurantID) rather than whichever sign or
        // door happened to be enumerated first, then take the nearest door to it.
        bool haveAnchor = false;
        string anchorSource = "none";
        Vector3 anchor = Vector3.zero;

        var active = ResolveActiveRestaurant();
        var anchors = new List<Vector3>();

        if (active != null)
        {
            // A single reference point is not reliable: anchoring on outdoorPoint alone
            // put the nearest door 20m away, so it is clearly not near the entrance.
            // Collect every landmark the restaurant offers and let the door match against
            // whichever is closest.
            AddPoint(anchors, active.transform);
            AddPoint(anchors, active.outdoorPoint);
            AddPoint(anchors, active.NPCStandOutsidePoint);
            AddPoint(anchors, active.signParent);

            if (anchors.Count > 0)
            {
                haveAnchor = true;
                anchorSource = $"active restaurant #{SafeRestaurantId(active)}";
            }
        }

        if (!haveAnchor)
        {
            // Fall back to the open/closed sign, which at least only exists at a
            // restaurant the player runs.
            var signs = SafeFind<RestaurantAvailableInteraction>();
            if (signs != null)
            {
                foreach (var s in signs)
                {
                    if (s == null) continue;
                    anchors.Add(s.transform.position);
                    haveAnchor = true;
                    anchorSource = "open/closed sign";
                    break;
                }
            }
        }
        if (anchors.Count > 0) anchor = anchors[0];

        var seen = new HashSet<int>();
        RestaurantDoorInteraction best = null;
        float bestDist = float.MaxValue;
        int candidates = 0;

        foreach (var d in doors)
        {
            if (d == null) continue;
            if (!d.restaurantDoor || d.restrictedDoor || d.IsHouseDoor) continue;
            if (seen.Contains(d.GetInstanceID())) continue;

            seen.Add(d.GetInstanceID());
            if (d.doubleDoor != null) seen.Add(d.doubleDoor.GetInstanceID());
            candidates++;

            if (!haveAnchor)
            {
                best ??= d;
                continue;
            }

            // Distance to the nearest of the restaurant's landmarks, not to one chosen
            // arbitrarily - the entrance is close to at least one of them.
            foreach (var a in anchors)
            {
                float dist = Vector3.SqrMagnitude(d.transform.position - a);
                if (dist < bestDist) { bestDist = dist; best = d; }
            }
        }

        if (best == null) return;

        into.Add(new Location { Name = "Nhà hàng", Position = best.transform.position });

        Plugin.Logger.LogInfo(haveAnchor
            ? $"Restaurant door: closest of {candidates} to {anchorSource} ({Mathf.Sqrt(bestDist):0.0}m)."
            : $"Restaurant door: no anchor found; fell back to the first of {candidates}.");
    }

    /// <summary>
    /// The restaurant the players are actually running. GameData.ActiveRestaurantID is
    /// the authoritative answer; BuildMode.currentRestaurant is a reasonable second guess
    /// when the save data is not reachable.
    /// </summary>
    private static Restaurant ResolveActiveRestaurant()
    {
        var all = SafeFind<Restaurant>();

        // Record what we actually see. The first attempt fell through silently, which
        // left it ambiguous whether the id was missing or simply matched nothing.
        var ids = new List<string>();
        if (all != null)
            foreach (var r in all)
                if (r != null) ids.Add(SafeRestaurantId(r).ToString());

        var data = ReadGameData(out string dataSource);
        int wanted = int.MinValue;
        if (data != null)
        {
            try { wanted = data.ActiveRestaurantID; } catch { }
        }

        Plugin.Logger.LogInfo(
            $"Restaurant: gameData={dataSource} ActiveRestaurantID=" +
            $"{(wanted == int.MinValue ? "?" : wanted.ToString())} " +
            $"sceneRestaurants=[{string.Join(",", ids)}]");

        if (data != null && all != null && wanted != int.MinValue)
        {
            foreach (var r in all)
            {
                if (r == null) continue;
                if (SafeRestaurantId(r) == wanted) return r;
            }
        }

        try
        {
            var modes = SafeFind<BuildMode>();
            if (modes != null)
            {
                foreach (var m in modes)
                {
                    if (m == null) continue;
                    var cur = m.currentRestaurant;
                    if (cur != null) return cur;
                }
            }
        }
        catch (Exception e)
        {
            LogOnce($"Restaurant: BuildMode lookup failed: {e.Message}");
        }

        return null;
    }

    private static void AddPoint(List<Vector3> into, Transform t)
    {
        try { if (t != null) into.Add(t.position); } catch { }
    }

    private static int SafeRestaurantId(Restaurant r)
    {
        try { return r.restaurantID; } catch { return int.MinValue; }
    }

    /// <summary>Save data, from whichever of the two holders currently has it.</summary>
    private static GameData ReadGameData(out string source)
    {
        try
        {
            var d = ItemRelator.gameData;
            if (d != null) { source = "ItemRelator"; return d; }
        }
        catch { /* try the next one */ }

        try
        {
            var logic = KebabLogic.Instance;
            var d = logic == null ? null : logic.LoadedGameData;
            if (d != null) { source = "KebabLogic"; return d; }
        }
        catch { /* nothing else to try */ }

        source = "null";
        return null;
    }

    private static void AddSimple<T>(List<Location> into, string label) where T : Component
    {
        var items = SafeFind<T>();
        if (items == null) return;

        int n = 0;
        foreach (var it in items)
        {
            if (it == null || n >= MaxPerCategory) continue;
            into.Add(new Location
            {
                Name = n == 0 ? label : $"{label} {n + 1}",
                Position = it.transform.position,
            });
            n++;
        }
    }

    private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<T> SafeFind<T>()
        where T : UnityEngine.Object
    {
        try
        {
            return UnityEngine.Object.FindObjectsByType<T>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Scan for {typeof(T).Name} failed: {e.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------- teleport

    // How far above/below the target we look for a floor, and how far above that
    // floor the capsule's feet are placed.
    private const float ProbeUp = 2f;
    private const float ProbeDown = 60f;
    private const float FootClearance = 0.05f;

    /// <summary>
    /// Moves the player onto solid ground at <paramref name="target"/>.
    ///
    /// Three things matter here and each caused a fall-through on its own:
    ///  * the CharacterController may live on a different GameObject than the
    ///    FirstPersonController, and moving the wrong transform desyncs the two;
    ///  * re-enabling the controller while its capsule overlaps the floor lets PhysX
    ///    depenetrate downward, punching the player through thin geometry;
    ///  * a marker transform is not necessarily standing on anything.
    /// So we raycast for real ground first and refuse the jump if there is none.
    /// </summary>
    public static bool TeleportTo(FirstPersonController fpc, Vector3 target, out string error)
    {
        error = null;
        if (fpc == null) { error = "Không tìm thấy người chơi."; return false; }

        try
        {
            var cc = fpc.m_CharacterController;
            var root = cc != null ? cc.transform : fpc.transform;

            if (!TryFindGround(target, out Vector3 ground))
            {
                error = "không có nền đất ở vị trí đó";
                return false;
            }

            // Land where the floor actually is. The probe may have succeeded from a
            // sideways offset, and standing at the original x/z in that case means
            // standing over the hole that made the centre probe fail.
            float y = ground.y + FootClearance;
            if (cc != null) y += cc.skinWidth + cc.height * 0.5f - cc.center.y;

            var safe = new Vector3(ground.x, y, ground.z);

            // Prefer the game's own Teleport. FirstPersonController is a NetworkBehaviour
            // holding a NetworkTransform, so its version keeps the network side coherent -
            // moving the transform by hand leaves netcode to interpolate a huge jump.
            bool viaGame = false;
            try
            {
                fpc.Teleport(safe);
                viaGame = true;
            }
            catch (Exception e)
            {
                LogOnce($"FirstPersonController.Teleport failed ({e.Message}); moving the transform directly.");
            }

            if (!viaGame)
            {
                bool wasEnabled = false;
                if (cc != null) { wasEnabled = cc.enabled; cc.enabled = false; }
                root.position = safe;
                if (cc != null) cc.enabled = wasEnabled;
            }

            // Drop any momentum so we do not keep sliding - or falling - after arriving.
            fpc.m_MoveDir = Vector3.zero;

            Plugin.Logger.LogInfo(
                $"Teleport: {(viaGame ? "game Teleport()" : "direct move")} -> " +
                $"({safe.x:0.0}, {safe.y:0.0}, {safe.z:0.0}), ground={ground.y:0.0}, " +
                $"drift={Vector3.Distance(new Vector3(target.x, 0f, target.z), new Vector3(ground.x, 0f, ground.z)):0.00}m");
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    /// <summary>
    /// Runs the game's own door transition, so the house model is loaded or unloaded the
    /// way it would be if you had walked through.
    /// </summary>
    public static bool TryUseDoor(Location loc, out string error)
    {
        error = null;
        if (loc?.Door == null) return false;

        try
        {
            loc.Door.MoveToHome();
            Plugin.Logger.LogInfo($"Teleport: used the game's door transition for '{loc.Name}'.");
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            LogOnce($"Door transition failed for '{loc.Name}': {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// How close to the indoor door counts as "inside the house". There is no flag for
    /// this on Building, so proximity to the interior door is the available signal. The
    /// measured distance is logged on every attempt so this can be tuned from real numbers
    /// rather than guessed at.
    /// </summary>
    private const float InsideRadius = 25f;

    /// <summary>
    /// Leaves the house through the interior door when the player is inside it.
    ///
    /// Teleporting straight out skips the transition that unloads the interior and
    /// restores the exterior model, which is why the house rendered wrongly afterwards.
    /// The interior door has its own MoveToHome, and it is the way back out.
    /// </summary>
    public static bool TryExitHome(FirstPersonController fpc)
    {
        if (fpc == null) return false;

        try
        {
            var doors = SafeFind<ApartmentDoorInteraction>();
            if (doors == null) return false;

            var here = fpc.transform.position;

            foreach (var d in doors)
            {
                if (d == null || !d.IsIndoor) continue;
                if (!d.isActiveAndEnabled) continue;

                var point = d.HousePoint != null ? d.HousePoint : d.transform;
                if (point == null) continue;

                float dist = Vector3.Distance(here, point.position);
                Plugin.Logger.LogInfo($"Teleport: indoor door {dist:0.0}m away (inside if <= {InsideRadius}).");

                if (dist > InsideRadius) continue;

                d.MoveToHome();
                Plugin.Logger.LogInfo("Teleport: left the house through the interior door.");
                return true;
            }
        }
        catch (Exception e)
        {
            LogOnce($"Exit-home check failed: {e.Message}");
        }
        return false;
    }

    private static readonly HashSet<string> Logged = new();

    private static void LogOnce(string message)
    {
        if (Logged.Add(message)) Plugin.Logger.LogWarning(message);
    }

    /// <summary>
    /// Probe for floor near the target.
    ///
    /// A single ray straight down from the exact point is not enough: a door pivot can
    /// sit inside wall geometry, and a ray that starts inside a collider does not hit it,
    /// so the probe came back empty and the jump was refused. We therefore try a few
    /// origins - higher up, then nudged sideways - and take the first that finds floor.
    /// </summary>
    private static bool TryFindGround(Vector3 target, out Vector3 ground)
    {
        ground = target;

        foreach (float up in new[] { ProbeUp, 6f, 15f })
        {
            foreach (var offset in ProbeOffsets)
            {
                var origin = target + offset + Vector3.up * up;

                if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit,
                                    up + ProbeDown, ~0, QueryTriggerInteraction.Ignore))
                {
                    ground = hit.point;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Centre first, then a small ring around it, so an embedded pivot still resolves.</summary>
    private static readonly Vector3[] ProbeOffsets =
    {
        Vector3.zero,
        new Vector3(0.9f, 0f, 0f),
        new Vector3(-0.9f, 0f, 0f),
        new Vector3(0f, 0f, 0.9f),
        new Vector3(0f, 0f, -0.9f),
    };

    // ------------------------------------------------- custom waypoint store

    // Serialised as  name~x~y~z|name~x~y~z  using invariant culture, because this
    // machine formats floats with a comma and that would corrupt the round-trip.
    private const char RecordSep = '|';
    private const char FieldSep = '~';

    public static List<Location> LoadCustom(string raw)
    {
        var list = new List<Location>();
        if (string.IsNullOrWhiteSpace(raw)) return list;

        foreach (var record in raw.Split(RecordSep))
        {
            if (string.IsNullOrWhiteSpace(record)) continue;
            var parts = record.Split(FieldSep);
            if (parts.Length != 4) continue;

            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)) continue;
            if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)) continue;
            if (!float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) continue;

            list.Add(new Location { Name = parts[0], Position = new Vector3(x, y, z), Custom = true });
        }
        return list;
    }

    // ------------------------------------------------------ hotkey bindings

    /// <summary>Location name -> key, using the same name~value|name~value shape.</summary>
    public static Dictionary<string, UnityEngine.InputSystem.Key> LoadBindings(string raw)
    {
        var map = new Dictionary<string, UnityEngine.InputSystem.Key>();
        if (string.IsNullOrWhiteSpace(raw)) return map;

        foreach (var record in raw.Split(RecordSep))
        {
            if (string.IsNullOrWhiteSpace(record)) continue;
            var parts = record.Split(FieldSep);
            if (parts.Length != 2) continue;

            if (Enum.TryParse<UnityEngine.InputSystem.Key>(parts[1], true, out var key)
                && key != UnityEngine.InputSystem.Key.None)
            {
                map[parts[0]] = key;
            }
        }
        return map;
    }

    public static string SaveBindings(Dictionary<string, UnityEngine.InputSystem.Key> map)
    {
        var sb = new StringBuilder();
        foreach (var kv in map)
        {
            if (sb.Length > 0) sb.Append(RecordSep);
            string safe = kv.Key.Replace(RecordSep, '-').Replace(FieldSep, '-');
            sb.Append(safe).Append(FieldSep).Append(kv.Value);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Keys offered for binding. A curated list rather than every Key enum value -
    /// the enum includes modifiers and IME entries that either cannot be read as a
    /// plain press or would make a miserable hotkey.
    /// </summary>
    public static readonly UnityEngine.InputSystem.Key[] BindableKeys = BuildBindableKeys();

    private static UnityEngine.InputSystem.Key[] BuildBindableKeys()
    {
        var names = new List<string>();
        for (char c = 'A'; c <= 'Z'; c++) names.Add(c.ToString());
        for (int i = 1; i <= 12; i++) names.Add("F" + i);
        for (int i = 0; i <= 9; i++) { names.Add("Digit" + i); names.Add("Numpad" + i); }
        names.AddRange(new[]
        {
            "Home", "End", "Insert", "Delete", "PageUp", "PageDown",
            "Backquote", "Minus", "Equals", "LeftBracket", "RightBracket",
            "Semicolon", "Quote", "Comma", "Period", "Slash", "Backslash",
        });

        var keys = new List<UnityEngine.InputSystem.Key>();
        foreach (var n in names)
        {
            if (Enum.TryParse<UnityEngine.InputSystem.Key>(n, true, out var k)
                && k != UnityEngine.InputSystem.Key.None)
            {
                keys.Add(k);
            }
        }
        return keys.ToArray();
    }

    public static string SaveCustom(List<Location> list)
    {
        var sb = new StringBuilder();
        foreach (var l in list)
        {
            if (sb.Length > 0) sb.Append(RecordSep);
            // Strip separators out of the name so a rename can never break parsing.
            string safe = l.Name.Replace(RecordSep, '-').Replace(FieldSep, '-');
            sb.Append(safe).Append(FieldSep)
              .Append(l.Position.x.ToString("R", CultureInfo.InvariantCulture)).Append(FieldSep)
              .Append(l.Position.y.ToString("R", CultureInfo.InvariantCulture)).Append(FieldSep)
              .Append(l.Position.z.ToString("R", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
