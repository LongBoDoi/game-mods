using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ChefSkills.Specials
{
    /// <summary>
    /// Reach, catching, throwing and crate specials: Long Arms, Magnet, Bulk Buyer, Catcher's Mitt,
    /// Sticky Fingers, Homing Throw, Lob, Assembly Line, Juggler, Rebound, Quartermaster, Shove,
    /// Weak Arm and Butterfingers.
    ///
    /// Where things run: the interaction scan (Long Arms) runs only on the machine that owns the
    /// chef, and the host trusts whatever it picks, so widening it there is enough. Catching, throws,
    /// crate pickups and respawns are decided on the host only (Server* components); everything
    /// this module changes there goes through the game's own server paths (velocities of synced
    /// rigidbodies, station AddItem, ServerSpawnPrefab, carrier TakeItem), so clients follow.
    /// </summary>
    internal sealed class ThrowingModule : ISpecialModule
    {
        // --- tuning -----------------------------------------------------------------------------
        private const float SpareCooldown = 15f;
        private const float SpareRange = 4f;           // how far from the crate a spare may be put
        private const float QuartermasterRange = 2.5f; // crate to the quartermaster
        private const float MagnetRange = 3f;
        private const float MagnetStop = 0.9f;         // don't push items into the chef's feet
        private const float MagnetSpeed = 3f;
        private const float JugglerWindow = 0.5f;
        private const float JugglerVelocity = 1.2247f; // sqrt(1.5): range grows with speed squared
        private const float LobAngle = 60f;
        private const float HomingRange = 14f;
        private const float HomingArc = 50f;
        private const float HomingTurn = 4.5f;         // radians per second
        private const float AssemblyRange = 12f;
        private const float AssemblyArc = 40f;
        private const float AssemblyDelay = 0.12f;     // let the pickup settle before the throw
        private const float ReboundNear = 2.5f;
        private const float ReboundAimed = 4.5f;
        private const float ReboundCounterRange = 3.5f;

        private sealed class Flight
        {
            public GameObject Item;
            public ServerThrowableItem Throwable;
            public IAttachment Attachment;
            public ChefState Thrower;
            public ChefState Homing;
            public ChefState Aimed;
            public float Started;
        }

        private sealed class PendingThrow
        {
            public ChefState Chef;
            public ChefState Target;
            public GameObject Item;
            public float At;
        }

        private static readonly Dictionary<int, Flight> s_flights = new Dictionary<int, Flight>();
        private static readonly List<int> s_done = new List<int>();
        private static readonly List<PendingThrow> s_pending = new List<PendingThrow>();
        private static readonly Dictionary<int, float> s_crateCooldown = new Dictionary<int, float>();

        private static ServerAttachStation[] s_stations;
        private static float s_stationsAt = -100f;
        private static ServerPhysicalAttachment[] s_loose;
        private static float s_looseAt = -100f;
        private static float s_tile;

        // Set around the game's own scans so the shared GetCollidersInArc patch knows whose scan it is.
        internal static GameObject ReachScan;
        internal static GameObject CatchScan;

        // Set around an Assembly Line throw so the throw postfix aims it at the teammate.
        private static ChefState s_aimChef;
        private static ChefState s_aimTarget;

        private static readonly GridIndex[] s_offsets =
        {
            new GridIndex(1, 0, 0), new GridIndex(-1, 0, 0), new GridIndex(0, 0, 1), new GridIndex(0, 0, -1)
        };

        public void Register()
        {
            SpecialRuntime.Tick += OnTick;
            SpecialRuntime.LevelStarted += Reset;
            SpecialRuntime.LevelEnded += Reset;
        }

        private static void Reset()
        {
            s_flights.Clear();
            s_pending.Clear();
            s_crateCooldown.Clear();
            s_stations = null;
            s_stationsAt = -100f;
            s_loose = null;
            s_looseAt = -100f;
            s_tile = 0f;
            ReachScan = null;
            CatchScan = null;
            s_aimChef = null;
            s_aimTarget = null;
        }

        // --- every frame (host) -----------------------------------------------------------------

        private static void OnTick(float dt)
        {
            if (!SpecialNet.IsServer) return;
            if (s_pending.Count > 0) RunPendingThrows();
            if (s_flights.Count > 0) UpdateFlights(dt);
            UpdateMagnets(dt);
        }

        // --- Long Arms ---------------------------------------------------------------------------

        /// <summary>One grid tile in metres (the reach Long Arms adds).</summary>
        internal static float TileSize()
        {
            if (s_tile > 0f) return s_tile;
            s_tile = 1.2f;
            if (GridManager.GetActiveCount() > 0)
            {
                GridManager grid = GridManager.GetActive(0);
                float size = (grid.GetPosFromGridLocation(new GridIndex(1, 0, 0)) -
                              grid.GetPosFromGridLocation(new GridIndex(0, 0, 0))).magnitude;
                if (size > 0.5f && size < 3f) s_tile = size;
            }
            return s_tile;
        }

        /// <summary>
        /// Grid-selection levels only offer the occupant of the next tile. Add the one two tiles
        /// away in the facing direction too; the game's scan still prefers the nearer one whenever
        /// that one is usable (it picks the usable candidate closest to the chef's hands).
        /// </summary>
        internal static void AddFarGridOccupant(Transform scanner, Collider[] colliders, int mask)
        {
            Vector3 forward = scanner.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f) return;
            forward.Normalize();

            Collider best = null;
            float bestDot = 0.6f;
            int count = GridManager.GetActiveCount();
            for (int i = 0; i < count; i++)
            {
                GridManager grid = GridManager.GetActive(i);
                if (grid == null) continue;
                GridIndex here = grid.GetUnclampedGridLocationFromPos(scanner.position);
                GameObject own = grid.GetGridOccupant(here);
                // Same rule as the game: standing on a real occupant means no grid selection here.
                if (own != null && !own.CompareTag("Hazard") && !own.CompareTag("Travelator") && !own.CompareTag("MovingPlatform"))
                    continue;
                Vector3 origin = grid.GetPosFromGridLocation(here);
                for (int j = 0; j < s_offsets.Length; j++)
                {
                    GridIndex next = here + s_offsets[j];
                    Vector3 direction = grid.GetPosFromGridLocation(next) - origin;
                    direction.y = 0f;
                    if (direction.sqrMagnitude < 1e-4f) continue;
                    float dot = Vector3.Dot(direction.normalized, forward);
                    if (dot <= bestDot) continue;
                    GameObject far = grid.GetGridOccupant(next + s_offsets[j]);
                    if (far == null) continue;
                    if (mask != -1 && (mask & (1 << far.layer)) == 0) continue;
                    Collider collider = far.GetComponent<Collider>();
                    if (collider == null) continue;
                    best = collider;
                    bestDot = dot;
                }
            }
            if (best == null) return;
            for (int k = 0; k < colliders.Length; k++)
                if (colliders[k] == best) return;
            for (int k = 0; k < colliders.Length; k++)
            {
                if (colliders[k] != null) continue;
                colliders[k] = best;
                return;
            }
        }

        // --- crates: Bulk Buyer, Quartermaster, Assembly Line -------------------------------------

        internal static void OnCratePickup(ServerPickupItemSpawner crate, ICarrier carrier)
        {
            if (crate == null || carrier == null) return;
            ChefState taker = SpecialRuntime.StateOf(carrier.AccessGameObject());
            if (taker == null || taker.Chef == null) return;
            ServerPlayerAttachmentCarrier hands = taker.Chef.GetComponent<ServerPlayerAttachmentCarrier>();
            GameObject held = hands != null ? hands.InspectCarriedItem() : null;
            float now = SpecialRuntime.Now;

            bool spare = false;
            if (taker.Has(Special.BulkBuyer) && now >= taker.Get("Throwing.BulkNext", 0f))
            {
                if (SpawnSpare(crate, held))
                {
                    taker.Set("Throwing.BulkNext", now + SpareCooldown);
                    spare = true;
                }
            }

            int crateKey = crate.gameObject.GetInstanceID();
            float crateReady;
            s_crateCooldown.TryGetValue(crateKey, out crateReady);
            if (!spare && now >= crateReady && !taker.Has(Special.LoneWolf))
            {
                foreach (ChefState mate in SpecialRuntime.Teammates(taker))
                {
                    if (!mate.Has(Special.Quartermaster) || mate.Chef == null) continue;
                    if (FlatDistance(mate.Position, crate.transform.position) > QuartermasterRange) continue;
                    if (SpawnSpare(crate, held)) s_crateCooldown[crateKey] = now + SpareCooldown;
                    break;
                }
            }

            if (taker.Has(Special.AssemblyLine) && held != null && held.GetComponent<ServerThrowableItem>() != null)
            {
                ChefState target = FacedOnly(taker, AssemblyRange, AssemblyArc);
                if (target != null)
                    s_pending.Add(new PendingThrow { Chef = taker, Target = target, Item = held, At = now + AssemblyDelay });
            }
        }

        /// <summary>Host: spawns another of the crate's item (the crate's own spawn path) on the nearest free counter.</summary>
        private static bool SpawnSpare(ServerPickupItemSpawner crate, GameObject sample)
        {
            GameObject prefab = crate.GetItemPrefab();
            if (prefab == null) return false;
            Vector3 cratePosition = crate.transform.position;
            ServerAttachStation station = NearestFreeStation(cratePosition, SpareRange, sample);
            if (station == null) return false;
            Transform point = station.GetAttachPoint(prefab);
            Vector3 position = point != null ? point.position : station.transform.position;
            GameObject spare = NetworkUtils.ServerSpawnPrefab(crate.gameObject, prefab, position, Quaternion.identity);
            if (spare == null) return false;
            station.AddItem(spare, Flat(station.transform.position - cratePosition), new PlacementContext(PlacementContext.Source.Game));
            return true;
        }

        private static void RunPendingThrows()
        {
            float now = SpecialRuntime.Now;
            for (int i = s_pending.Count - 1; i >= 0; i--)
            {
                PendingThrow pending = s_pending[i];
                if (now < pending.At) continue;
                s_pending.RemoveAt(i);
                ChefState chef = pending.Chef;
                if (chef.Chef == null || chef.Controls == null || pending.Item == null || pending.Target.Chef == null) continue;
                ServerPlayerAttachmentCarrier hands = chef.Chef.GetComponent<ServerPlayerAttachmentCarrier>();
                if (hands == null || hands.InspectCarriedItem() != pending.Item) continue;
                PlayerControlsImpl_Default impl = chef.Controls.GetActiveControlsImpl() as PlayerControlsImpl_Default;
                if (impl == null || impl.m_serverImpl == null) continue;
                // The game's own server throw (checks, animation event, catch alerts); the throw
                // postfix turns its direction towards the teammate.
                s_aimChef = chef;
                s_aimTarget = pending.Target;
                try
                {
                    impl.m_serverImpl.ReceiveThrowEvent(pending.Item);
                }
                finally
                {
                    s_aimChef = null;
                    s_aimTarget = null;
                }
            }
        }

        // --- throws: Juggler, Lob, Homing Throw, Assembly Line aim, Rebound tracking ----------------

        internal static void OnCaught(ServerAttachmentCatcher catcher)
        {
            ChefState state = SpecialRuntime.StateOf(catcher.gameObject);
            if (state != null && state.Has(Special.Juggler)) state.Set("Throwing.CaughtAt", SpecialRuntime.Now);
        }

        /// <summary>
        /// Host, right after the game set the thrown item's velocity. Works on that velocity rather
        /// than the thrower's settings, so it stacks with THROWING's force change.
        /// </summary>
        internal static void OnThrown(ServerAttachmentThrower thrower, GameObject item)
        {
            ChefState state = SpecialRuntime.StateOf(thrower.gameObject);
            if (state == null) return;
            IAttachment attachment = item.RequestInterface<IAttachment>();
            RigidbodyMotion motion = attachment != null ? attachment.AccessMotion() : null;
            if (motion == null) return;

            Vector3 velocity = motion.GetVelocity();
            bool changed = false;

            if (state.Has(Special.Juggler) &&
                SpecialRuntime.Now - state.Get("Throwing.CaughtAt", -100f) <= JugglerWindow)
            {
                velocity *= JugglerVelocity;
                changed = true;
            }

            if (state.Has(Special.Lob))
            {
                // Same range on flat ground (range ~ H * V), but launched at LobAngle.
                float horizontal = new Vector2(velocity.x, velocity.z).magnitude;
                float steep = Mathf.Tan(LobAngle * Mathf.Deg2Rad);
                float current = horizontal > 0.01f ? velocity.y / horizontal : 0f;
                if (horizontal > 0.01f && current < steep)
                {
                    float scale = Mathf.Sqrt(Mathf.Max(current, 0.15f) / steep);
                    velocity.x *= scale;
                    velocity.z *= scale;
                    velocity.y = horizontal * scale * steep;
                    changed = true;
                }
            }

            if (s_aimChef == state && s_aimTarget != null && s_aimTarget.Chef != null)
            {
                Vector3 to = s_aimTarget.Position - item.transform.position;
                to.y = 0f;
                float horizontal = new Vector2(velocity.x, velocity.z).magnitude;
                if (to.sqrMagnitude > 0.01f && horizontal > 0.01f)
                {
                    to = to.normalized * horizontal;
                    velocity.x = to.x;
                    velocity.z = to.z;
                    changed = true;
                }
            }

            if (changed) motion.SetVelocity(velocity);

            ChefState homing = state.Has(Special.HomingThrow) ? FacedOnly(state, HomingRange, HomingArc) : null;
            if (homing == null && s_aimChef == state && state.Has(Special.HomingThrow)) homing = s_aimTarget;
            ChefState aimed = AimedRebounder(state, item.transform.position, velocity);
            if (homing == null && aimed == null && !SpecialRuntime.AnyHas(Special.Rebound)) return;

            ServerThrowableItem throwable = item.GetComponent<ServerThrowableItem>();
            if (throwable == null) return;
            s_flights[item.GetInstanceID()] = new Flight
            {
                Item = item, Throwable = throwable, Attachment = attachment, Thrower = state,
                Homing = homing, Aimed = aimed, Started = SpecialRuntime.Now
            };
        }

        /// <summary>The Rebound chef (not the thrower) the throw is heading for, if any.</summary>
        private static ChefState AimedRebounder(ChefState thrower, Vector3 from, Vector3 velocity)
        {
            Vector3 direction = new Vector3(velocity.x, 0f, velocity.z);
            if (direction.sqrMagnitude < 0.01f) return null;
            ChefState best = null;
            float bestDistance = 14f;
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState chef = chefs[i];
                if (chef == thrower || chef.Chef == null || !chef.Has(Special.Rebound)) continue;
                Vector3 offset = chef.Position - from;
                offset.y = 0f;
                float distance = offset.magnitude;
                if (distance > bestDistance || Vector3.Angle(direction, offset) > 30f) continue;
                best = chef;
                bestDistance = distance;
            }
            return best;
        }

        private static void UpdateFlights(float dt)
        {
            s_done.Clear();
            float now = SpecialRuntime.Now;
            foreach (KeyValuePair<int, Flight> pair in s_flights)
            {
                Flight flight = pair.Value;
                if (flight.Item == null || flight.Throwable == null || now - flight.Started > 6f)
                {
                    s_done.Add(pair.Key);
                    continue;
                }
                if (flight.Throwable.IsFlying())
                {
                    if (flight.Homing != null) Steer(flight, dt);
                    continue;
                }
                s_done.Add(pair.Key);
                Landed(flight);
            }
            for (int i = 0; i < s_done.Count; i++) s_flights.Remove(s_done[i]);
        }

        /// <summary>Homing Throw: turns the item's horizontal velocity towards the teammate.</summary>
        private static void Steer(Flight flight, float dt)
        {
            ChefState target = flight.Homing;
            if (target.Chef == null)
            {
                flight.Homing = null;
                return;
            }
            RigidbodyMotion motion = flight.Attachment.AccessMotion();
            if (motion == null) return;
            Vector3 velocity = motion.GetVelocity();
            Vector3 horizontal = new Vector3(velocity.x, 0f, velocity.z);
            float speed = horizontal.magnitude;
            Vector3 to = target.Position - motion.GetPosition();
            to.y = 0f;
            // Close enough, or flown past: stop bending.
            if (speed < 0.5f || to.magnitude < 0.8f || Vector3.Angle(horizontal, to) > 100f)
            {
                flight.Homing = null;
                return;
            }
            Vector3 turned = Vector3.RotateTowards(horizontal, to.normalized * speed, HomingTurn * dt, 0f);
            motion.SetVelocity(new Vector3(turned.x, velocity.y, turned.z));
        }

        /// <summary>Rebound: a throw nobody caught, landing near a Rebound chef, goes onto a counter.</summary>
        private static void Landed(Flight flight)
        {
            if (flight.Attachment == null || flight.Attachment.IsAttached()) return;
            if (!flight.Item.activeInHierarchy) return;
            Vector3 position = flight.Item.transform.position;
            ChefState keeper = null;
            float best = float.MaxValue;
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState chef = chefs[i];
                if (chef == flight.Thrower || chef.Chef == null || !chef.Has(Special.Rebound)) continue;
                float distance = FlatDistance(chef.Position, position);
                float limit = chef == flight.Aimed ? ReboundAimed : ReboundNear;
                if (distance <= limit && distance < best)
                {
                    keeper = chef;
                    best = distance;
                }
            }
            if (keeper == null) return;
            ServerAttachStation station = NearestFreeStation(keeper.Position, ReboundCounterRange, flight.Item);
            if (station == null) return;
            station.AddItem(flight.Item, Flat(station.transform.position - position), new PlacementContext(PlacementContext.Source.Game));
        }

        // --- Magnet ------------------------------------------------------------------------------

        private static void UpdateMagnets(float dt)
        {
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            bool any = false;
            for (int i = 0; i < chefs.Count && !any; i++)
                any = chefs[i].Chef != null && chefs[i].Has(Special.Magnet);
            if (!any) return;

            float now = SpecialRuntime.Now;
            if (s_loose == null || now - s_looseAt > 1f)
            {
                s_loose = UnityEngine.Object.FindObjectsOfType<ServerPhysicalAttachment>();
                s_looseAt = now;
            }

            float blend = Mathf.Clamp01(8f * dt);
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState chef = chefs[i];
                if (chef.Chef == null || !chef.Has(Special.Magnet)) continue;
                if (chef.Controls != null && chef.Controls.m_bRespawning) continue;
                Vector3 home = chef.Position;
                for (int j = 0; j < s_loose.Length; j++)
                {
                    ServerPhysicalAttachment item = s_loose[j];
                    if (item == null || !item.isActiveAndEnabled || item.IsAttached()) continue;
                    Rigidbody body = item.AccessRigidbody();
                    if (body == null || body.isKinematic) continue;
                    Vector3 offset = home - body.position;
                    if (Mathf.Abs(offset.y) > 0.8f) continue;
                    offset.y = 0f;
                    float distance = offset.magnitude;
                    if (distance > MagnetRange || distance < MagnetStop) continue;
                    ServerThrowableItem throwable = item.GetComponent<ServerThrowableItem>();
                    if (throwable != null && throwable.IsFlying()) continue;
                    RigidbodyMotion motion = item.AccessMotion();
                    if (motion == null) continue;
                    Vector3 velocity = motion.GetVelocity();
                    Vector3 pull = offset / distance * MagnetSpeed;
                    motion.SetVelocity(new Vector3(Mathf.Lerp(velocity.x, pull.x, blend), velocity.y,
                        Mathf.Lerp(velocity.z, pull.z, blend)));
                }
            }
        }

        // --- Shove ---------------------------------------------------------------------------------

        /// <summary>Host: the chef a Shove dash hit lets go of what it holds (unless Sticky Fingers).</summary>
        internal static void ShoveDrop(GameObject shover, PlayerControls victim)
        {
            if (victim == null || victim.gameObject == shover) return;
            if (SpecialRuntime.Has(victim.gameObject, Special.StickyFingers)) return;
            ServerPlayerAttachmentCarrier hands = victim.GetComponent<ServerPlayerAttachmentCarrier>();
            if (hands == null || hands.InspectCarriedItem() == null) return;
            GameObject item = hands.TakeItem();
            if (item == null) return;
            IAttachment attachment = item.RequestInterface<IAttachment>();
            RigidbodyMotion motion = attachment != null ? attachment.AccessMotion() : null;
            if (motion == null) return;
            Vector3 away = victim.transform.position - shover.transform.position;
            away.y = 0f;
            away = away.sqrMagnitude > 0.01f ? away.normalized : victim.transform.forward;
            motion.SetVelocity(away * 3f + Vector3.up * 2f);
        }

        // --- helpers -------------------------------------------------------------------------------

        /// <summary>The teammate within <paramref name="arc"/> degrees of the chef's facing (smallest angle), or null.</summary>
        internal static ChefState FacedOnly(ChefState chef, float range, float arc)
        {
            ChefState best = null;
            float bestAngle = arc;
            foreach (ChefState other in SpecialRuntime.Teammates(chef))
            {
                if (other.Chef == null) continue;
                if (other.Controls != null && other.Controls.m_bRespawning) continue;
                Vector3 offset = other.Position - chef.Position;
                offset.y = 0f;
                float distance = offset.magnitude;
                if (distance > range || distance < 0.3f) continue;
                Vector3 forward = chef.Forward;
                forward.y = 0f;
                float angle = Vector3.Angle(forward, offset);
                if (angle > bestAngle) continue;
                best = other;
                bestAngle = angle;
            }
            return best;
        }

        /// <summary>Host: the nearest empty counter (not a crate or a conveyor) that would take this item.</summary>
        private static ServerAttachStation NearestFreeStation(Vector3 around, float range, GameObject sample)
        {
            float now = SpecialRuntime.Now;
            if (s_stations == null || now - s_stationsAt > 5f)
            {
                s_stations = UnityEngine.Object.FindObjectsOfType<ServerAttachStation>();
                s_stationsAt = now;
            }
            ServerAttachStation best = null;
            float bestDistance = range;
            var context = new PlacementContext(PlacementContext.Source.Game);
            for (int i = 0; i < s_stations.Length; i++)
            {
                ServerAttachStation station = s_stations[i];
                if (station == null || !station.isActiveAndEnabled || station.HasItem()) continue;
                Vector3 position = station.transform.position;
                if (Mathf.Abs(position.y - around.y) > 2f) continue;
                float distance = FlatDistance(position, around);
                if (distance > bestDistance) continue;
                if (station.GetComponent<ServerPickupItemSpawner>() != null) continue;
                if (station.GetComponent<ServerConveyorStation>() != null) continue;
                if (sample != null && !station.CanAttachToSelf(sample, context)) continue;
                best = station;
                bestDistance = distance;
            }
            return best;
        }

        private static float FlatDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        private static Vector2 Flat(Vector3 v)
        {
            Vector2 flat = new Vector2(v.x, v.z);
            return flat.sqrMagnitude > 1e-4f ? flat.normalized : new Vector2(0f, 1f);
        }
    }

    // ================================================================================================
    // Patches
    // ================================================================================================

    /// <summary>LONG ARMS: marks the owner's interaction scan (pick up, put down, chop, wash).</summary>
    [HarmonyPatch(typeof(PlayerControls), "FindNearbyObjects")]
    internal static class LongArmsScanPatch
    {
        private static void Prefix(PlayerControls __instance)
        {
            ThrowingModule.ReachScan = null;
            if (!SpecialRuntime.InLevel) return;
            try
            {
                if (SpecialRuntime.Has(__instance.gameObject, Special.LongArms))
                    ThrowingModule.ReachScan = __instance.gameObject;
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("LongArms.Scan", error);
            }
        }

        private static void Finalizer()
        {
            ThrowingModule.ReachScan = null;
        }
    }

    /// <summary>CATCHER'S MITT: marks the host's catch scan for a chef with the mitt.</summary>
    [HarmonyPatch(typeof(PlayerControls), "ScanForCatch")]
    internal static class CatchScanPatch
    {
        private static void Prefix(PlayerControls __instance)
        {
            ThrowingModule.CatchScan = null;
            if (!SpecialRuntime.InLevel) return;
            try
            {
                if (SpecialRuntime.Has(__instance.gameObject, Special.CatchersMitt))
                    ThrowingModule.CatchScan = __instance.gameObject;
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("CatchersMitt.Scan", error);
            }
        }

        private static void Finalizer()
        {
            ThrowingModule.CatchScan = null;
        }
    }

    /// <summary>
    /// The arc scan both of the above use. Long Arms: one tile more radius (the scan's aim point is
    /// kept where it was, so nearer things still win) plus the tile beyond on grid levels.
    /// Catcher's Mitt: 3 m all the way round instead of 2 m in front.
    /// </summary>
    [HarmonyPatch(typeof(InteractWithItemHelper), "GetCollidersInArc")]
    internal static class ArcScanPatch
    {
        private static void Prefix(ref float detectionRadius, ref float arc, Transform _transform, out float __state)
        {
            __state = 0f;
            if (ThrowingModule.ReachScan == null && ThrowingModule.CatchScan == null) return;
            try
            {
                if (_transform == null) return;
                GameObject scanner = _transform.gameObject;
                if (scanner == ThrowingModule.ReachScan)
                {
                    __state = detectionRadius;
                    detectionRadius += ThrowingModule.TileSize();
                }
                else if (scanner == ThrowingModule.CatchScan)
                {
                    detectionRadius = Mathf.Max(detectionRadius, 3f);
                    arc = 2f * Mathf.PI;
                }
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Throwing.ArcScan", error);
            }
        }

        private static void Postfix(Transform _transform, Collider[] _colliders, int _layerMask, bool _gridSelection,
            float __state, ref Vector3 __result)
        {
            if (__state <= 0f) return;
            try
            {
                Vector3 forward = _transform.forward;
                forward.y = 0f;
                __result = _transform.position + 0.5f * __state * forward.normalized;
                if (_gridSelection) ThrowingModule.AddFarGridOccupant(_transform, _colliders, _layerMask);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("LongArms.ArcScan", error);
            }
        }
    }

    /// <summary>
    /// BUTTERFINGERS: never catches. CATCHER'S MITT: the host's catch check allows any direction
    /// and a longer distance for this one call (restored in the finalizer).
    /// </summary>
    [HarmonyPatch(typeof(ServerAttachmentCatcher), "CanHandleCatch")]
    internal static class CatchCheckPatch
    {
        private static readonly FieldInfo CatcherField = AccessTools.Field(typeof(ServerAttachmentCatcher), "m_catcher");

        private static bool Prefix(ServerAttachmentCatcher __instance, ref bool __result, out Vector2 __state)
        {
            __state = new Vector2(-1f, -1f);
            if (!SpecialRuntime.InLevel) return true;
            try
            {
                ChefState state = SpecialRuntime.StateOf(__instance.gameObject);
                if (state == null) return true;
                if (state.Has(Special.Butterfingers))
                {
                    __result = false;
                    return false;
                }
                if (state.Has(Special.CatchersMitt) && CatcherField != null)
                {
                    var catcher = CatcherField.GetValue(__instance) as AttachmentCatcher;
                    if (catcher != null)
                    {
                        __state = new Vector2(catcher.m_catchDistance, catcher.m_catchAngleMax);
                        catcher.m_catchDistance = Mathf.Max(catcher.m_catchDistance + 1f, 3.2f);
                        catcher.m_catchAngleMax = 180f;
                    }
                }
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("CatchersMitt.Check", error);
            }
            return true;
        }

        private static void Finalizer(ServerAttachmentCatcher __instance, Vector2 __state)
        {
            if (__state.x < 0f || CatcherField == null) return;
            try
            {
                var catcher = CatcherField.GetValue(__instance) as AttachmentCatcher;
                if (catcher == null) return;
                catcher.m_catchDistance = __state.x;
                catcher.m_catchAngleMax = __state.y;
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("CatchersMitt.Restore", error);
            }
        }
    }

    /// <summary>JUGGLER: remembers when the chef caught something (host).</summary>
    [HarmonyPatch(typeof(ServerAttachmentCatcher), "HandleCatch")]
    internal static class CatchDonePatch
    {
        private static void Postfix(ServerAttachmentCatcher __instance)
        {
            if (!SpecialRuntime.InLevel) return;
            try
            {
                ThrowingModule.OnCaught(__instance);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Juggler.Catch", error);
            }
        }
    }

    /// <summary>JUGGLER, LOB, HOMING THROW, ASSEMBLY LINE aim and REBOUND tracking (host).</summary>
    [HarmonyPatch(typeof(ServerAttachmentThrower), "ThrowItem")]
    internal static class ThrownPatch
    {
        private static void Postfix(ServerAttachmentThrower __instance, GameObject _object)
        {
            if (!SpecialRuntime.InLevel || _object == null) return;
            try
            {
                ThrowingModule.OnThrown(__instance, _object);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Throwing.Thrown", error);
            }
        }
    }

    /// <summary>WEAK ARM on the player's machine: no aiming, no throw request.</summary>
    [HarmonyPatch(typeof(ClientThrowableItem), "CanHandleThrow")]
    internal static class WeakArmClientPatch
    {
        private static void Postfix(IClientThrower _thrower, ref bool __result)
        {
            if (!__result || !SpecialRuntime.InLevel) return;
            try
            {
                var thrower = _thrower as Component;
                if (thrower != null && SpecialRuntime.Has(thrower.gameObject, Special.WeakArm)) __result = false;
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("WeakArm.Client", error);
            }
        }
    }

    /// <summary>WEAK ARM on the host, which makes the throw.</summary>
    [HarmonyPatch(typeof(ServerThrowableItem), "CanHandleThrow")]
    internal static class WeakArmServerPatch
    {
        private static void Postfix(IThrower _thrower, ref bool __result)
        {
            if (!__result || !SpecialRuntime.InLevel) return;
            try
            {
                var thrower = _thrower as Component;
                if (thrower != null && SpecialRuntime.Has(thrower.gameObject, Special.WeakArm)) __result = false;
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("WeakArm.Server", error);
            }
        }
    }

    /// <summary>BULK BUYER, QUARTERMASTER and ASSEMBLY LINE: a chef took an item from a crate (host).</summary>
    [HarmonyPatch(typeof(ServerPickupItemSpawner), "HandlePickup")]
    internal static class CratePickupPatch
    {
        // After PREP COOK (Stations.cs) has swapped in the chopped item, so ASSEMBLY LINE throws that.
        [HarmonyPriority(Priority.Low)]
        private static void Postfix(ServerPickupItemSpawner __instance, ICarrier _carrier)
        {
            if (!SpecialRuntime.InLevel) return;
            try
            {
                ThrowingModule.OnCratePickup(__instance, _carrier);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Throwing.Crate", error);
            }
        }
    }

    /// <summary>STICKY FINGERS: falling off the map or drowning keeps what the chef holds (host).</summary>
    [HarmonyPatch(typeof(ServerPlayerRespawnBehaviour), "DropOrDestroyHeldItems")]
    internal static class StickyRespawnPatch
    {
        private static bool Prefix(ServerPlayerRespawnBehaviour __instance)
        {
            if (!SpecialRuntime.InLevel) return true;
            try
            {
                return !SpecialRuntime.Has(__instance.gameObject, Special.StickyFingers);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("StickyFingers.Respawn", error);
                return true;
            }
        }
    }

    /// <summary>
    /// SHOVE: a dash by a chef with Shove knocks harder (every machine runs the knock the same way;
    /// the multiplier is restored in the finalizer) and, on the host, the chef it hit drops what it holds.
    /// </summary>
    [HarmonyPatch(typeof(ClientPlayerControlsImpl_Default), "OnDashCollision")]
    internal static class ShovePatch
    {
        private static readonly FieldInfo ControlsField =
            AccessTools.Field(typeof(ClientPlayerControlsImpl_Default), "m_controls");
        private static PlayerControls.ImpactData _changed;
        private static float _multiplier;

        private static void Prefix(ClientPlayerControlsImpl_Default __instance, out bool __state)
        {
            __state = false;
            if (!SpecialRuntime.InLevel || ControlsField == null) return;
            try
            {
                if (!__instance.IsDashing()) return;
                var controls = ControlsField.GetValue(__instance) as PlayerControls;
                if (controls == null || controls.Movement == null || !SpecialRuntime.Has(controls.gameObject, Special.Shove)) return;
                __state = true;
                PlayerControls.ImpactData impact = controls.Movement.DashImpactData;
                if (impact == null) return;
                _changed = impact;
                _multiplier = impact.Multiplier;
                impact.Multiplier = _multiplier * ShoveStrength;
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Shove.Prefix", error);
            }
        }

        private const float ShoveStrength = 1.75f;

        private static void Postfix(ClientPlayerControlsImpl_Default __instance, PlayerControls _otherPlayer, bool __state)
        {
            if (!__state || !SpecialNet.IsServer) return;
            try
            {
                var controls = ControlsField.GetValue(__instance) as PlayerControls;
                if (controls != null) ThrowingModule.ShoveDrop(controls.gameObject, _otherPlayer);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Shove.Drop", error);
            }
        }

        private static void Finalizer()
        {
            if (_changed != null) _changed.Multiplier = _multiplier;
            _changed = null;
        }
    }
}
