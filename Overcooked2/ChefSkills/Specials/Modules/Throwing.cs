using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ChefSkills.Specials
{
    /// <summary>
    /// Reach, catching, throwing and crate specials: Long Arms, Bulk Buyer, Catcher's Mitt (with its
    /// rebound and magnet), Sticky Fingers, Homing Throw, Lob, Assembly Line, Juggler, Quartermaster, Shove,
    /// Weak Arm and Butterfingers.
    ///
    /// Where things run: the interaction scan (Long Arms) runs only on the machine that owns the
    /// chef, and the host trusts whatever it picks, so widening it there is enough. Catching, throws,
    /// crate pickups, dash bumps and respawns are decided on the host (Server* components; the host
    /// simulates every chef's dash, see DashSync); everything this module changes there goes through
    /// the game's own server paths (velocities of synced rigidbodies, station AddItem,
    /// ServerSpawnPrefab, carrier TakeItem), so clients follow. Every throw and catch patch looks up
    /// the specials of the chef doing it (the thrower, the catcher), never anyone else's.
    ///
    /// What players see: host code shows its effects on every machine (SpecialFx ...All), and the
    /// skill bar's timers (Bulk Buyer, Quartermaster recharge; Juggler, Assembly Line windows) are
    /// sent to every machine with the chef's own topic, since only the host knows when they start.
    /// </summary>
    internal sealed class ThrowingModule : ISpecialModule
    {
        // --- tuning -----------------------------------------------------------------------------
        private const float BulkCooldown = 8f;
        private const float QuartermasterCooldown = 5f;
        private const float SpareRange = 4f;           // how far from the crate a spare may be put
        private const float SpareBoardRange = 6f;      // Bulk Buyer: an empty board for a choppable spare
        private const float QuartermasterRange = 5f;
        // Quartermaster: the spare goes on a free counter this close to the quartermaster (else by the crate).
        private const float QuartermasterDrop = 2f;   // crate to the quartermaster
        private const float MagnetRange = 6f;
        private const float MagnetStop = 0.9f;         // don't push items into the chef's feet
        private const float MagnetSpeed = 6f;
        private const float MagnetGrab = 1.3f;         // empty hands take a pulled item from this close
        private const float MagnetOthers = 1.5f;       // items this close to another chef are theirs
        private const float MagnetOwnSeconds = 3f;     // the chef's own dropped/thrown item isn't pulled back
        private const float JugglerWindow = 2f;        // host time: covers the round trip online
        private const float JugglerVelocity = 1.2247f; // sqrt(1.5): range grows with speed squared
        private const float LobAngle = 60f;
        private const float LobVelocity = 1.14f;       // ~1.3x the range (range grows with speed squared)
        private const float HomingRange = 10f;         // times THROWING
        private const float HomingArc = 30f;
        private const float StrongArmRange = 8f;       // a toss aimed at a teammate
        private const float AssemblyRange = 10f;       // times THROWING
        private const float AssemblyArc = 45f;
        private const float AssemblyWindow = 2.5f;     // after taking from a crate, turn to a teammate
        private const float AssemblySettle = 0.15f;    // let the pickup settle before the throw
        private const float FlightMinTime = 0.35f;     // aimed throws: seconds in the air
        private const float FlightMaxTime = 1.6f;
        private const float SteerAcceleration = 30f;   // m/s^2 of sideways correction in flight
        private const float CatchHeight = 0.6f;        // aimed throws arrive this far over the feet
        private const float ReboundNear = 4.5f;
        private const float ReboundAimed = 6f;
        private const float ReboundCounterRange = 4.5f;
        private const float ReboundFall = 1.5f;        // this far under its launch height: it has landed (a pit)
        private const float FlightMarkSeconds = 0.4f;

        // Network topics: (Special * 10 + n). Each carries a skill-bar timer for that special.
        private static readonly ushort TopicBulk = (ushort)((int)Special.BulkBuyer * 10 + 1);
        private static readonly ushort TopicQuartermaster = (ushort)((int)Special.Quartermaster * 10 + 1);
        private static readonly ushort TopicJuggler = (ushort)((int)Special.Juggler * 10 + 1);
        private static readonly ushort TopicAssembly = (ushort)((int)Special.AssemblyLine * 10 + 1);
        // Client -> host: my Shove dash hit this chef (Chef = dasher, I = victim, Vector1 = dasher position).
        private static readonly ushort TopicShove = (ushort)((int)Special.Shove * 10 + 1);
        private const float ShoveRange = 3f;
        private const float ShoveDropCooldown = 3f;    // the same chef can't be made to drop again sooner

        private sealed class Flight
        {
            public GameObject Item;
            public ServerThrowableItem Throwable;
            public IAttachment Attachment;
            public ChefState Thrower;
            /// <summary>Homing Throw / Assembly Line: the teammate it flies to (steered all the way).</summary>
            public ChefState Target;
            /// <summary>The special whose ring follows the item while it flies (None: no ring).</summary>
            public Special Mark;
            public ChefState Aimed;
            public float Started;
            public float LaunchHeight;
            public float MaxHorizontal;
            /// <summary>A STRONG ARM toss (no throwable part: it flies until it settles or is caught).</summary>
            public bool Toss;
        }

        private sealed class Armed
        {
            public ChefState Chef;
            public GameObject Item;
            public float From;
            public float Until;
        }

        private static readonly Dictionary<int, Flight> s_flights = new Dictionary<int, Flight>();
        private static readonly List<int> s_done = new List<int>();
        private static readonly List<Armed> s_armed = new List<Armed>();
        private static readonly Dictionary<int, float> s_oops = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> s_shoved = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> s_shoveDropped = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> s_magnetMarked = new Dictionary<int, float>();

        private static ServerAttachStation[] s_stations;
        private static float s_stationsAt = -100f;
        private static ServerPhysicalAttachment[] s_loose;
        private static float s_looseAt = -100f;
        private static float s_tile;

        // Set around the game's own scans so the shared GetCollidersInArc patch knows whose scan it is.
        internal static GameObject ReachScan;
        internal static GameObject CatchScan;

        // Catcher's Mitt: the catcher whose catch this frame only the mitt made possible.
        private static GameObject s_assistCatcher;
        private static int s_assistFrame = -1;

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
            SpecialNet.Register(TopicBulk, OnTimerMessage);
            SpecialNet.Register(TopicQuartermaster, OnTimerMessage);
            SpecialNet.Register(TopicJuggler, OnTimerMessage);
            SpecialNet.Register(TopicAssembly, OnTimerMessage);
            SpecialNet.Register(TopicShove, OnShoveRequest);
        }

        private static void Reset()
        {
            s_flights.Clear();
            s_armed.Clear();
            s_oops.Clear();
            s_shoved.Clear();
            s_shoveDropped.Clear();
            s_magnetMarked.Clear();
            s_stations = null;
            s_stationsAt = -100f;
            s_loose = null;
            s_looseAt = -100f;
            s_tile = 0f;
            ReachScan = null;
            CatchScan = null;
            s_assistCatcher = null;
            s_assistFrame = -1;
            s_aimChef = null;
            s_aimTarget = null;
        }

        // --- every frame (host) -----------------------------------------------------------------

        private static void OnTick(float dt)
        {
            if (!SpecialNet.IsServer) return;
            if (s_armed.Count > 0) RunArmed();
            if (s_flights.Count > 0) UpdateFlights(dt);
            UpdateMagnets(dt);
        }

        // --- skill-bar timers (host -> every machine) -----------------------------------------------

        private static ushort TimerTopic(Special special)
        {
            switch (special)
            {
                case Special.BulkBuyer: return TopicBulk;
                case Special.Quartermaster: return TopicQuartermaster;
                case Special.Juggler: return TopicJuggler;
                default: return TopicAssembly;
            }
        }

        /// <summary>Host: shows a timer on this chef's icon for <paramref name="special"/> on every machine (0 clears it).</summary>
        private static void ShareTimer(ChefState chef, Special special, float seconds)
        {
            if (chef == null) return;
            if (chef.Id == 0)
            {
                chef.SetTimer(special, SpecialRuntime.Now + seconds, seconds);
                return;
            }
            SpecialNet.SendToAll(new SpecialMessage { Topic = TimerTopic(special), Chef = chef.Id, I = (int)special, A = seconds });
        }

        private static void OnTimerMessage(SpecialMessage message)
        {
            ChefState chef = SpecialRuntime.StateById(message.Chef);
            if (chef == null) return;
            chef.SetTimer((Special)message.I, SpecialRuntime.Now + message.A, message.A);
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

        /// <summary>Further than a chef without Long Arms could have reached (flat metres to the object).</summary>
        internal static bool BeyondReach(Vector3 chef, GameObject target)
        {
            return target != null && FlatDistance(chef, target.transform.position) > 1.45f * TileSize();
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

        /// <summary>Owner's machine: the object the chef would use now is out of normal reach; ring it while it is.</summary>
        internal static void ShowReach(PlayerControls controls, PlayerControls.InteractionObjects found)
        {
            if (found == null) return;
            ChefState owner = SpecialRuntime.StateOf(controls.gameObject);
            if (owner == null || !owner.Local) return;
            GameObject target = found.m_TheOriginalHandlePickup;
            if (target == null && found.m_interactable != null) target = found.m_interactable.gameObject;
            if (target == null || !BeyondReach(controls.transform.position, target)) return;
            SpecialFx.Mark(target, SpecialFx.ColourOf(Special.LongArms), 0.15f, null, 0f);
        }

        /// <summary>Host: a Long Arms chef picked up, put down or started working on something out of normal reach.</summary>
        internal static void ReachedFar(GameObject chef, Vector3 at, bool work, GameObject target)
        {
            SpecialFx.BeamAll(chef.transform.position, at, Special.LongArms, 0.45f);
            SpecialFx.BurstAll(at, Special.LongArms);
            if (work && target != null) SpecialFx.MarkAll(target, Special.LongArms, 1f, null);
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
                if (SpawnSpare(crate, held, Special.BulkBuyer, true))
                {
                    taker.Set("Throwing.BulkNext", now + BulkCooldown);
                    ShareTimer(taker, Special.BulkBuyer, BulkCooldown);
                    spare = true;
                }
            }

            if (!spare && !taker.Has(Special.LoneWolf) && SpecialRuntime.AnyHas(Special.Quartermaster))
            {
                foreach (ChefState mate in SpecialRuntime.Teammates(taker))
                {
                    if (mate.Chef == null || !mate.Has(Special.Quartermaster)) continue;
                    if (now < mate.Get("Throwing.QuartermasterNext", 0f)) continue;
                    if (FlatDistance(mate.Position, crate.transform.position) > QuartermasterRange) continue;
                    if (!SpawnSpare(crate, held, Special.Quartermaster, false, mate.Position)) break;
                    mate.Set("Throwing.QuartermasterNext", now + QuartermasterCooldown);
                    ShareTimer(mate, Special.Quartermaster, QuartermasterCooldown);
                    SpecialFx.TextAll(mate.Chef, Lang.Both("SPARE!", "DỰ PHÒNG!"), Special.Quartermaster);
                    break;
                }
            }

            // JUGGLER: taking from a crate opens the quick re-throw window too.
            if (taker.Has(Special.Juggler) && held != null)
            {
                taker.Set("Throwing.CaughtAt", now);
                ShareTimer(taker, Special.Juggler, JugglerWindow);
            }

            if (taker.Has(Special.AssemblyLine) && held != null && held.GetComponent<ServerThrowableItem>() != null)
            {
                for (int i = s_armed.Count - 1; i >= 0; i--)
                    if (s_armed[i].Chef == taker) s_armed.RemoveAt(i);
                s_armed.Add(new Armed { Chef = taker, Item = held, From = now + AssemblySettle, Until = now + AssemblyWindow });
                ShareTimer(taker, Special.AssemblyLine, AssemblyWindow);
            }
        }

        /// <summary>
        /// Host: spawns another of the crate's item (the crate's own spawn path) on the nearest free
        /// counter. <paramref name="board"/> (BULK BUYER): something to chop goes onto the nearest
        /// empty chopping board first, if there is one in reach.
        /// </summary>
        private static bool SpawnSpare(ServerPickupItemSpawner crate, GameObject sample, Special special, bool board)
        {
            return SpawnSpare(crate, sample, special, board, null);
        }

        /// <summary>
        /// As above; with <paramref name="nearChef"/> (Quartermaster) the spare goes on a free
        /// counter within 2 m of that spot when there is one, so the quartermaster stages it.
        /// </summary>
        private static bool SpawnSpare(ServerPickupItemSpawner crate, GameObject sample, Special special, bool board, Vector3? nearChef)
        {
            GameObject prefab = crate.GetItemPrefab();
            if (prefab == null) return false;
            Vector3 cratePosition = crate.transform.position;
            ServerAttachStation station = null;
            bool onBoard = false;
            if (nearChef.HasValue)
            {
                station = NearestFreeStation(nearChef.Value, QuartermasterDrop, sample, false);
                if (station != null) cratePosition = nearChef.Value;
            }
            if (station == null && board && prefab.GetComponent<WorkableItem>() != null)
            {
                // The held one may be chopped already (Prep Cook): only a raw one says what a board takes.
                GameObject raw = sample != null && sample.GetComponent<WorkableItem>() != null ? sample : null;
                station = NearestFreeStation(cratePosition, SpareBoardRange, raw, true);
                onBoard = station != null;
            }
            if (station == null) station = NearestFreeStation(cratePosition, SpareRange, sample, false);
            if (station == null) return false;
            Transform point = station.GetAttachPoint(prefab);
            Vector3 position = point != null ? point.position : station.transform.position;
            GameObject spare = NetworkUtils.ServerSpawnPrefab(crate.gameObject, prefab, position, Quaternion.identity);
            if (spare == null) return false;
            var context = new PlacementContext(PlacementContext.Source.Game);
            if (onBoard && !station.CanAttachToSelf(spare, context))
            {
                // The board won't take it after all: the nearest free counter, as before.
                station = NearestFreeStation(cratePosition, SpareRange, spare, false);
                if (station == null)
                {
                    NetworkUtils.DestroyObject(spare);
                    return false;
                }
                point = station.GetAttachPoint(spare);
                position = point != null ? point.position : station.transform.position;
                spare.transform.position = position;
            }
            station.AddItem(spare, Flat(station.transform.position - cratePosition), context);
            // By position: the new item may reach other machines after this effect does.
            SpecialFx.BeamAll(crate.transform.position, position, special, 0.5f);
            SpecialFx.BurstAll(position, special);
            SpecialFx.TextAllAt(position + Vector3.up * 0.9f, "+1", special);
            return true;
        }

        /// <summary>Host: Assembly Line chefs who took from a crate; the first teammate they face gets it thrown.</summary>
        private static void RunArmed()
        {
            float now = SpecialRuntime.Now;
            for (int i = s_armed.Count - 1; i >= 0; i--)
            {
                Armed armed = s_armed[i];
                ChefState chef = armed.Chef;
                if (chef.Chef == null || chef.Controls == null || armed.Item == null || now > armed.Until || chef.Controls.m_bRespawning)
                {
                    s_armed.RemoveAt(i);
                    if (now <= armed.Until) ShareTimer(chef, Special.AssemblyLine, 0f);
                    continue;
                }
                ServerPlayerAttachmentCarrier hands = chef.Chef.GetComponent<ServerPlayerAttachmentCarrier>();
                if (hands == null || hands.InspectCarriedItem() != armed.Item)
                {
                    s_armed.RemoveAt(i);
                    ShareTimer(chef, Special.AssemblyLine, 0f);
                    continue;
                }
                // A ring under the chef while a turn towards a teammate would still pass it.
                SpecialFx.MarkAll(chef.Chef, Special.AssemblyLine, 0.5f, null);
                if (now < armed.From) continue;
                // Only a teammate free to catch it: empty hands, not chopping or washing.
                ChefState target = FacedTeammate(chef, chef.Position, chef.Forward, AssemblyRange * ThrowReach(chef), AssemblyArc, 1.2f, FreeToCatch);
                if (target == null) continue;
                s_armed.RemoveAt(i);
                ShareTimer(chef, Special.AssemblyLine, 0f);
                ThrowFor(chef, target, armed.Item);
            }
        }

        /// <summary>Host: the game's own server throw (checks, animation event); the throw postfix aims it at the teammate.</summary>
        private static void ThrowFor(ChefState chef, ChefState target, GameObject item)
        {
            PlayerControlsImpl_Default impl = chef.Controls.GetActiveControlsImpl() as PlayerControlsImpl_Default;
            if (impl == null || impl.m_serverImpl == null) return;
            s_aimChef = chef;
            s_aimTarget = target;
            try
            {
                impl.m_serverImpl.ReceiveThrowEvent(item);
            }
            finally
            {
                s_aimChef = null;
                s_aimTarget = null;
            }
        }

        // --- catching: Catcher's Mitt, Butterfingers, Juggler, aimed throws ------------------------

        /// <summary>Is this item flying to this chef (Homing Throw, Assembly Line)?</summary>
        internal static bool AimedAt(GameObject item, GameObject chef)
        {
            if (item == null || chef == null || s_flights.Count == 0) return false;
            Flight flight;
            return s_flights.TryGetValue(item.GetInstanceID(), out flight) && flight.Target != null &&
                   flight.Target.Chef == chef && !flight.Target.Has(Special.LoneWolf);
        }

        /// <summary>Is this item flying to a teammate right now (Homing Throw, Assembly Line)?</summary>
        internal static bool HomingOnSomeone(GameObject item)
        {
            if (item == null || s_flights.Count == 0) return false;
            Flight flight;
            return s_flights.TryGetValue(item.GetInstanceID(), out flight) && flight.Target != null && flight.Target.Chef != null;
        }

        /// <summary>Is something flying to this chef right now?</summary>
        internal static bool HasIncoming(GameObject chef)
        {
            if (chef == null || s_flights.Count == 0) return false;
            foreach (KeyValuePair<int, Flight> pair in s_flights)
            {
                ChefState target = pair.Value.Target;
                if (target != null && target.Chef == chef && !target.Has(Special.LoneWolf)) return true;
            }
            return false;
        }

        /// <summary>Host, same frame as the catch: only Catcher's Mitt made it possible.</summary>
        internal static void NoteAssist(GameObject catcher)
        {
            s_assistCatcher = catcher;
            s_assistFrame = Time.frameCount;
        }

        /// <summary>Host: a Butterfingers chef would have caught this. Says so once per throw.</summary>
        internal static void Oops(ChefState chef, GameObject item)
        {
            if (item == null) return;
            int key = item.GetInstanceID();
            float now = SpecialRuntime.Now, last;
            if (s_oops.TryGetValue(key, out last) && now - last < 2f) return;
            if (s_oops.Count > 64) s_oops.Clear();
            s_oops[key] = now;
            SpecialFx.TextAll(chef.Chef, Lang.Both("OOPS!", "ÁI CHÀ!"), Special.Butterfingers);
        }

        internal static void OnCaught(ServerAttachmentCatcher catcher, GameObject item)
        {
            ChefState state = SpecialRuntime.StateOf(catcher.gameObject);
            if (state == null) return;
            Caught(state, item, s_assistCatcher == catcher.gameObject && s_assistFrame == Time.frameCount);
        }

        /// <summary>Host: a teammate caught a STRONG ARM toss (Carrying's own catch).</summary>
        internal static void OnTossCaught(ChefState state, GameObject item)
        {
            if (state == null) return;
            Caught(state, item, false);
        }

        private static void Caught(ChefState state, GameObject item, bool mittAssist)
        {
            if (state.Has(Special.Juggler))
            {
                state.Set("Throwing.CaughtAt", SpecialRuntime.Now);
                ShareTimer(state, Special.Juggler, JugglerWindow);
            }
            if (mittAssist)
            {
                s_assistCatcher = null;
                SpecialFx.TextAll(state.Chef, Lang.Both("CAUGHT!", "BẮT ĐƯỢC!"), Special.CatchersMitt);
                SpecialFx.BurstAll(state.Position, Special.CatchersMitt);
            }
            Flight flight;
            if (item != null && s_flights.TryGetValue(item.GetInstanceID(), out flight))
            {
                if (flight.Target == state && flight.Mark != Special.None) SpecialFx.BurstAll(state.Position, flight.Mark);
                s_flights.Remove(item.GetInstanceID());
            }
        }

        // --- throws: Juggler, Lob, Homing Throw, Assembly Line aim, mitt rebound tracking ------------

        /// <summary>
        /// Host, right after the game set the thrown item's velocity. Works on that velocity rather
        /// than the thrower's settings, so it stacks with THROWING's force change. The thrower's
        /// own specials only.
        /// </summary>
        internal static void OnThrown(ServerAttachmentThrower thrower, GameObject item)
        {
            ChefState state = SpecialRuntime.StateOf(thrower.gameObject);
            if (state == null) return;
            IAttachment attachment = item.RequestInterface<IAttachment>();
            ServerThrowableItem throwable = item.GetComponent<ServerThrowableItem>();
            if (throwable == null) return;
            Launched(state, item, attachment, throwable);
        }

        /// <summary>
        /// Host, right after a STRONG ARM toss left the hands with its velocity: the same specials
        /// as a normal throw (Juggler, Homing Throw, Lob, Catcher's Mitt rebound), on the toss's velocity.
        /// </summary>
        internal static void OnTossed(ChefState state, GameObject item, IAttachment attachment)
        {
            if (state == null || item == null) return;
            try
            {
                Launched(state, item, attachment, null);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("StrongArm.Shape", error);
            }
        }

        /// <summary>A throw (<paramref name="throwable"/> set) or a toss (null) just left the hands.</summary>
        private static void Launched(ChefState state, GameObject item, IAttachment attachment, ServerThrowableItem throwable)
        {
            RigidbodyMotion motion = attachment != null ? attachment.AccessMotion() : null;
            if (motion == null) return;

            Vector3 origin = item.transform.position;
            Vector3 velocity = motion.GetVelocity();
            bool changed = false;
            Special mark = Special.None;

            if (state.Has(Special.Juggler) &&
                SpecialRuntime.Now - state.Get("Throwing.CaughtAt", -100f) <= JugglerWindow)
            {
                velocity *= JugglerVelocity;
                changed = true;
                mark = Special.Juggler;
                state.Set("Throwing.CaughtAt", -100f);
                ShareTimer(state, Special.Juggler, 0f);
                SpecialFx.TextAll(state.Chef, Lang.Both("JUGGLE!", "TUNG HỨNG!"), Special.Juggler);
            }

            bool lob = state.Has(Special.Lob);
            ChefState target = null;
            Special aim = Special.None;
            if (s_aimChef == state && s_aimTarget != null && s_aimTarget.Chef != null)
            {
                target = s_aimTarget;
                aim = Special.AssemblyLine;
            }
            else
            {
                Vector3 heading = new Vector3(velocity.x, 0f, velocity.z);
                if (state.Has(Special.HomingThrow))
                {
                    // The teammate the throw is heading for (the game throws along the chef's facing).
                    target = FacedTeammate(state, origin, heading, HomingRange * ThrowReach(state), HomingArc, 0.8f, null);
                    if (target != null) aim = Special.HomingThrow;
                }
                if (target == null && throwable == null && state.Has(Special.StrongArm))
                {
                    // STRONG ARM's own aim: a toss aimed at a teammate (not a Lone Wolf) flies to them.
                    target = FacedTeammate(state, origin, heading, StrongArmRange, HomingArc, 0.8f, NotLoneWolf);
                    if (target != null) aim = Special.StrongArm;
                }
            }

            if (target != null)
            {
                velocity = AimAt(origin, velocity, target.Position + Vector3.up * CatchHeight, lob);
                changed = true;
                mark = aim;
                SpecialFx.BeamAll(state.Position, target.Position, aim, 0.5f);
            }
            else if (lob)
            {
                // A little faster (about 30% further), then the same range on flat ground
                // (range ~ H * V), but launched at LobAngle.
                velocity *= LobVelocity;
                changed = true;
                if (mark == Special.None) mark = Special.Lob;
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
                    if (mark == Special.None) mark = Special.Lob;
                }
            }

            if (changed) motion.SetVelocity(velocity);

            ChefState aimed = AimedRebounder(state, origin, velocity);
            if (target == null && mark == Special.None && aimed == null && !SpecialRuntime.AnyHas(Special.CatchersMitt)) return;

            float flat = new Vector2(velocity.x, velocity.z).magnitude;
            s_flights[item.GetInstanceID()] = new Flight
            {
                Item = item, Throwable = throwable, Attachment = attachment, Thrower = state, Target = target,
                Mark = mark, Aimed = aimed, Started = SpecialRuntime.Now, LaunchHeight = origin.y,
                MaxHorizontal = Mathf.Max(2.2f * flat, 8f),
                Toss = throwable == null
            };
            if (mark != Special.None) SpecialFx.MarkAll(item, mark, FlightMarkSeconds, null);
        }

        private static float Gravity()
        {
            float g = -Physics.gravity.y;
            return g > 1f ? g : 9.81f;
        }

        /// <summary>
        /// A launch velocity from <paramref name="origin"/> that comes down on <paramref name="point"/>:
        /// about the thrown speed along the ground (the flight time kept between FlightMinTime and
        /// FlightMaxTime), the arc making up the rest, never flatter than the game's own throw (so a
        /// short pass still clears the counters). A lob keeps its steep launch.
        /// </summary>
        private static Vector3 AimAt(Vector3 origin, Vector3 velocity, Vector3 point, bool lob)
        {
            Vector3 flat = point - origin;
            flat.y = 0f;
            float distance = flat.magnitude;
            if (distance < 0.3f) return velocity;
            float g = Gravity();
            float speed = Mathf.Max(new Vector2(velocity.x, velocity.z).magnitude, 2f);
            float time = Mathf.Clamp(distance / speed, FlightMinTime, FlightMaxTime);
            // On flat ground a launch steepness (up / along) of k takes sqrt(2 d k / g) seconds.
            float steepness = lob ? Mathf.Tan(LobAngle * Mathf.Deg2Rad) : Mathf.Clamp(velocity.y / speed, 0.25f, 1f);
            time = Mathf.Max(time, Mathf.Min(2.2f, Mathf.Sqrt(2f * distance * steepness / g)));
            float rise = point.y - origin.y;
            float vertical = (rise + 0.5f * g * time * time) / time;
            Vector3 along = flat / distance * (distance / time);
            return new Vector3(along.x, vertical, along.z);
        }

        /// <summary>The Catcher's Mitt chef (not the thrower) the throw is heading for, if any.</summary>
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
                if (chef == thrower || chef.Chef == null || !chef.Has(Special.CatchersMitt)) continue;
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
                if (flight.Item == null || (flight.Throwable == null && !flight.Toss) || now - flight.Started > 6f)
                {
                    s_done.Add(pair.Key);
                    continue;
                }
                bool flying = flight.Toss ? TossFlying(flight, now) : flight.Throwable.IsFlying();
                // CATCHER'S MITT rebound: a throw (not flying to a teammate) dropping well under where it was
                // thrown from is going into a pit: it counts as landed while it still can be saved.
                if (flying && flight.Target == null && flight.Item.activeInHierarchy &&
                    flight.Item.transform.position.y < flight.LaunchHeight - ReboundFall && SpecialRuntime.AnyHas(Special.CatchersMitt))
                    flying = false;
                if (flying)
                {
                    if (flight.Target != null) Steer(flight, dt);
                    if (flight.Mark != Special.None) SpecialFx.MarkAll(flight.Item, flight.Mark, FlightMarkSeconds, null);
                    continue;
                }
                s_done.Add(pair.Key);
                Landed(flight);
            }
            for (int i = 0; i < s_done.Count; i++) s_flights.Remove(s_done[i]);
        }

        /// <summary>A toss is in the air until something takes it or it comes to rest.</summary>
        private static bool TossFlying(Flight flight, float now)
        {
            if (flight.Attachment == null || flight.Attachment.IsAttached() || !flight.Item.activeInHierarchy) return false;
            float age = now - flight.Started;
            if (age < 0.35f) return true;
            if (age > 2.5f) return false;
            RigidbodyMotion motion = flight.Attachment.AccessMotion();
            return motion != null && motion.GetVelocity().sqrMagnitude >= 0.25f;
        }

        /// <summary>
        /// Homing Throw / Assembly Line: keeps the item's ground velocity pointed at where the
        /// teammate is now, at the speed that brings it down on them (the arc is left alone).
        /// </summary>
        private static void Steer(Flight flight, float dt)
        {
            ChefState target = flight.Target;
            if (target.Chef == null || (target.Controls != null && target.Controls.m_bRespawning))
            {
                flight.Target = null;
                return;
            }
            RigidbodyMotion motion = flight.Attachment != null ? flight.Attachment.AccessMotion() : null;
            if (motion == null) return;
            Vector3 position = motion.GetPosition();
            Vector3 velocity = motion.GetVelocity();
            Vector3 point = target.Position + Vector3.up * CatchHeight;
            Vector3 flat = point - position;
            flat.y = 0f;
            float distance = flat.magnitude;
            if (distance < 0.6f) return;
            Vector3 horizontal = new Vector3(velocity.x, 0f, velocity.z);

            float g = Gravity();
            float rise = point.y - position.y;
            float discriminant = velocity.y * velocity.y - 2f * g * rise;
            float time = discriminant >= 0f ? (velocity.y + Mathf.Sqrt(discriminant)) / g : -1f;
            Vector3 wanted = time > 0.05f ? flat / time : flat / distance * horizontal.magnitude;
            if (wanted.magnitude > flight.MaxHorizontal) wanted = wanted.normalized * flight.MaxHorizontal;
            Vector3 turned = Vector3.MoveTowards(horizontal, wanted, SteerAcceleration * dt);
            motion.SetVelocity(new Vector3(turned.x, velocity.y, turned.z));
        }

        /// <summary>
        /// Catcher's Mitt rebound (once REBOUND): a throw nobody caught (the mitt chef's own too), landing near a mitt chef
        /// or dropping into a pit there, goes onto a counter.
        /// </summary>
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
                // The thrower's own throws too.
                if (chef.Chef == null || !chef.Has(Special.CatchersMitt)) continue;
                if (chef.Controls != null && chef.Controls.m_bRespawning) continue;
                float distance = FlatDistance(chef.Position, position);
                float limit = chef == flight.Aimed ? ReboundAimed : ReboundNear;
                if (distance <= limit && distance < best)
                {
                    keeper = chef;
                    best = distance;
                }
            }
            if (keeper == null) return;
            ServerAttachStation station = NearestFreeStation(keeper.Position, ReboundCounterRange, flight.Item, false);
            if (station == null) return;
            station.AddItem(flight.Item, Flat(station.transform.position - position), new PlacementContext(PlacementContext.Source.Game));
            Vector3 onCounter = flight.Item.transform.position;
            SpecialFx.BeamAll(position, onCounter, Special.CatchersMitt, 0.5f);
            SpecialFx.BurstAll(onCounter, Special.CatchersMitt);
            SpecialFx.TextAll(flight.Item, SpecialFx.NameBoth(Special.CatchersMitt, "!", "!"), Special.CatchersMitt);
        }

        // --- Catcher's Mitt magnet (once MAGNET) ---------------------------------------------------

        private static void UpdateMagnets(float dt)
        {
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            bool any = false;
            for (int i = 0; i < chefs.Count && !any; i++)
                any = chefs[i].Chef != null && chefs[i].Has(Special.CatchersMitt);
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
                if (chef.Chef == null || !chef.Has(Special.CatchersMitt)) continue;
                if (chef.Controls != null && chef.Controls.m_bRespawning) continue;
                Vector3 home = chef.Position;
                ServerPlayerAttachmentCarrier hands = chef.Chef.GetComponent<ServerPlayerAttachmentCarrier>();
                GameObject holding = hands != null ? hands.InspectCarriedItem() : null;
                // What it just put down or threw stays where it was put, for a moment.
                if (holding != null)
                {
                    chef.Set("Throwing.MagnetHeld", holding);
                    chef.Set("Throwing.MagnetHeldAt", now);
                }
                GameObject own = now - chef.Get("Throwing.MagnetHeldAt", -100f) < MagnetOwnSeconds
                    ? chef.Get<GameObject>("Throwing.MagnetHeld", null) : null;
                // Empty hands (not chopping or washing): what arrives goes straight into them.
                bool grab = hands != null && holding == null && chef.Controls != null && chef.Controls.GetCurrentlyInteracting() == null;
                for (int j = 0; j < s_loose.Length; j++)
                {
                    ServerPhysicalAttachment item = s_loose[j];
                    if (item == null || !item.isActiveAndEnabled || item.IsAttached()) continue;
                    if (own != null && item.gameObject == own) continue;
                    Rigidbody body = item.AccessRigidbody();
                    if (body == null || body.isKinematic) continue;
                    Vector3 offset = home - body.position;
                    if (Mathf.Abs(offset.y) > 0.8f) continue;
                    offset.y = 0f;
                    float distance = offset.magnitude;
                    if (distance > MagnetRange || (distance < MagnetStop && !grab)) continue;
                    ServerThrowableItem throwable = item.GetComponent<ServerThrowableItem>();
                    if (throwable != null && throwable.IsFlying()) continue;
                    // Food a teammate has put down by them to stage is theirs.
                    if (NearOtherChef(chef, body.position)) continue;
                    if (grab && distance <= MagnetGrab && MagnetTake(chef, hands, item.gameObject, offset))
                    {
                        grab = false;
                        continue;
                    }
                    if (distance < MagnetStop) continue;
                    RigidbodyMotion motion = item.AccessMotion();
                    if (motion == null) continue;
                    Vector3 velocity = motion.GetVelocity();
                    Vector3 pull = offset / distance * MagnetSpeed;
                    motion.SetVelocity(new Vector3(Mathf.Lerp(velocity.x, pull.x, blend), velocity.y,
                        Mathf.Lerp(velocity.z, pull.z, blend)));
                    // A ring under every item being pulled (looked up a few times a second, not every frame).
                    int key = item.GetInstanceID();
                    float next;
                    if (!s_magnetMarked.TryGetValue(key, out next) || now >= next)
                    {
                        if (s_magnetMarked.Count > 256) s_magnetMarked.Clear();
                        s_magnetMarked[key] = now + 0.2f;
                        SpecialFx.MarkAll(item.gameObject, Special.CatchersMitt, 0.5f, null);
                    }
                }
            }
        }

        /// <summary>Is another chef (not respawning) standing within MagnetOthers of this spot?</summary>
        private static bool NearOtherChef(ChefState chef, Vector3 at)
        {
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState other = chefs[i];
                if (other == chef || other.Chef == null) continue;
                if (other.Controls != null && other.Controls.m_bRespawning) continue;
                Vector3 offset = other.Position - at;
                if (Mathf.Abs(offset.y) > 1.5f) continue;
                offset.y = 0f;
                if (offset.magnitude < MagnetOthers) return true;
            }
            return false;
        }

        /// <summary>Host: a pulled item reached a Catcher's Mitt chef's empty hands: the game's own pickup of a loose item.</summary>
        private static bool MagnetTake(ChefState chef, ServerPlayerAttachmentCarrier hands, GameObject item, Vector3 offset)
        {
            if (chef.Has(Special.Vegetarian) && CarryingModule.IsRawMeat(item)) return false;
            IHandlePickup handler = PlayerControlsHelper.GetControllingPickupHandler_Server(item);
            if (handler == null || !handler.CanHandlePickup(hands)) return false;
            Vector3 from = item.transform.position;
            handler.HandlePickup(hands, Flat(offset));
            if (hands.InspectCarriedItem() != item) return false;
            SpecialFx.BeamAll(from, chef.Position, Special.CatchersMitt, 0.3f);
            SpecialFx.MarkAll(item, Special.CatchersMitt, 0.6f, null);
            return true;
        }

        // --- Shove, Sticky Fingers -------------------------------------------------------------------

        /// <summary>
        /// The dasher's own machine (the only one that times its dash): a Shove dash hit a chef.
        /// The host does it at once; a client asks the host, which checks it again.
        /// </summary>
        internal static void RequestShove(GameObject shover, PlayerControls victim)
        {
            ChefState state = SpecialRuntime.StateOf(shover);
            ChefState target = SpecialRuntime.StateOf(victim.gameObject);
            if (state == null || target == null || state == target || !state.Local || !state.Has(Special.Shove)) return;
            if (SameSideInVersus(state, target)) return;
            if (SpecialNet.IsServer)
            {
                ShoveHit(shover, victim);
                return;
            }
            if (!SpecialNet.Online) return;
            float now = SpecialRuntime.Now;
            if (now < state.Get("Throwing.ShoveNext", 0f)) return;
            state.Set("Throwing.ShoveNext", now + 0.3f);
            SpecialNet.SendToHost(new SpecialMessage
            {
                Topic = TopicShove, Chef = state.Id, I = (int)target.Id, Vector1 = state.Position
            });
        }

        /// <summary>Host: a client's Shove hit, checked against where the host has the two chefs.</summary>
        private static void OnShoveRequest(SpecialMessage message)
        {
            if (!SpecialRuntime.InLevel || !SpecialNet.IsServer) return;
            ChefState state = SpecialRuntime.StateById(message.Chef);
            ChefState victim = SpecialRuntime.StateById((uint)message.I);
            if (state == null || victim == null || state == victim || state.Chef == null || victim.Controls == null) return;
            if (!state.Has(Special.Shove) || (state.Controls != null && state.Controls.m_bRespawning)) return;
            if (FlatDistance(victim.Position, message.Vector1) > ShoveRange) return;
            ShoveHit(state.Chef, victim.Controls);
        }

        /// <summary>Versus: are these two on the same team (Shove and Pickpocket only hit rivals there)?</summary>
        internal static bool SameSideInVersus(ChefState a, ChefState b)
        {
            return OrdersModule.Versus && a != null && b != null && a.Team == b.Team;
        }

        /// <summary>
        /// Host: a Shove dash hit a chef: it lets go of what it holds (unless Sticky Fingers), at
        /// most once every few seconds for the same chef.
        /// </summary>
        internal static void ShoveHit(GameObject shover, PlayerControls victim)
        {
            if (victim == null || shover == null || victim.gameObject == shover || victim.m_bRespawning) return;
            if (SameSideInVersus(SpecialRuntime.StateOf(shover), SpecialRuntime.StateOf(victim.gameObject))) return;
            int key = victim.gameObject.GetInstanceID();
            float now = SpecialRuntime.Now, last;
            if (s_shoved.TryGetValue(key, out last) && now - last < 0.4f) return;
            s_shoved[key] = now;
            SpecialFx.BurstAll(victim.transform.position, Special.Shove);

            ServerPlayerAttachmentCarrier hands = victim.GetComponent<ServerPlayerAttachmentCarrier>();
            float dropped;
            bool recently = s_shoveDropped.TryGetValue(key, out dropped) && now - dropped < ShoveDropCooldown;
            if (hands == null || hands.InspectCarriedItem() == null || recently)
            {
                SpecialFx.TextAll(victim.gameObject, Lang.Both("SHOVED!", "BỊ XÔ!"), Special.Shove);
                return;
            }
            if (SpecialRuntime.Has(victim.gameObject, Special.StickyFingers))
            {
                SpecialFx.TextAll(victim.gameObject, Lang.Both("STICKY!", "DÍNH CHẶT!"), Special.StickyFingers);
                return;
            }
            GameObject item = hands.TakeItem();
            if (item == null) return;
            s_shoveDropped[key] = now;
            SpecialFx.TextAll(victim.gameObject, Lang.Both("DROPPED!", "RƠI ĐỒ!"), Special.Shove);
            IAttachment attachment = item.RequestInterface<IAttachment>();
            RigidbodyMotion motion = attachment != null ? attachment.AccessMotion() : null;
            if (motion == null) return;
            Vector3 away = victim.transform.position - shover.transform.position;
            away.y = 0f;
            away = away.sqrMagnitude > 0.01f ? away.normalized : victim.transform.forward;
            motion.SetVelocity(away * 3f + Vector3.up * 2f);
        }

        /// <summary>Host: a Sticky Fingers chef fell off holding something and keeps it.</summary>
        internal static void KeptOnFall(GameObject chef)
        {
            ServerPlayerAttachmentCarrier hands = chef.GetComponent<ServerPlayerAttachmentCarrier>();
            if (hands != null && hands.InspectCarriedItem() != null)
                SpecialFx.TextAll(chef, Lang.Both("KEPT IT!", "GIỮ ĐƯỢC!"), Special.StickyFingers);
        }

        // --- helpers -------------------------------------------------------------------------------

        /// <summary>
        /// The teammate (not respawning) within <paramref name="arc"/> degrees of
        /// <paramref name="direction"/> from <paramref name="from"/>, between
        /// <paramref name="minDistance"/> and <paramref name="range"/> metres, and passing
        /// <paramref name="accept"/> (null: any): the smallest angle wins.
        /// </summary>
        internal static ChefState FacedTeammate(ChefState chef, Vector3 from, Vector3 direction, float range, float arc,
            float minDistance, Func<ChefState, bool> accept)
        {
            direction.y = 0f;
            if (direction.sqrMagnitude < 1e-4f) return null;
            ChefState best = null;
            float bestAngle = arc;
            foreach (ChefState other in SpecialRuntime.Teammates(chef))
            {
                if (other.Chef == null) continue;
                if (other.Controls != null && other.Controls.m_bRespawning) continue;
                if (accept != null && !accept(other)) continue;
                Vector3 offset = other.Position - from;
                offset.y = 0f;
                float distance = offset.magnitude;
                if (distance > range || distance < minDistance) continue;
                float angle = Vector3.Angle(direction, offset);
                if (angle > bestAngle) continue;
                best = other;
                bestAngle = angle;
            }
            return best;
        }

        /// <summary>How far this chef's aimed throws reach, as a share of the base range (THROWING).</summary>
        private static float ThrowReach(ChefState chef)
        {
            return chef != null && chef.Chef != null ? Mathf.Max(0.1f, Plugin.Skills.Multiplier(chef.Chef, Skill.Throwing)) : 1f;
        }

        /// <summary>Assembly Line: a teammate who can catch now (empty hands, not chopping or washing).</summary>
        private static bool FreeToCatch(ChefState chef)
        {
            if (chef.Controls == null || chef.Controls.GetCurrentlyInteracting() != null) return false;
            ServerPlayerAttachmentCarrier hands = chef.Chef.GetComponent<ServerPlayerAttachmentCarrier>();
            return hands != null && hands.InspectCarriedItem() == null;
        }

        private static bool NotLoneWolf(ChefState chef)
        {
            return !chef.Has(Special.LoneWolf);
        }

        /// <summary>
        /// Host: the nearest empty counter (not a crate or a conveyor) that would take this item.
        /// <paramref name="boards"/>: only empty chopping boards.
        /// </summary>
        private static ServerAttachStation NearestFreeStation(Vector3 around, float range, GameObject sample, bool boards)
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
                if (boards && station.GetComponent<ServerWorkstation>() == null) continue;
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

    /// <summary>
    /// LONG ARMS: marks the owner's interaction scan (pick up, put down, chop, wash), and rings
    /// what the chef would use when only Long Arms reaches it (this machine only: it's the owner's).
    /// </summary>
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

        private static void Postfix(PlayerControls __instance, PlayerControls.InteractionObjects __result)
        {
            if (ThrowingModule.ReachScan == null || ThrowingModule.ReachScan != __instance.gameObject) return;
            try
            {
                ThrowingModule.ShowReach(__instance, __result);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("LongArms.Show", error);
            }
        }

        private static void Finalizer()
        {
            ThrowingModule.ReachScan = null;
        }
    }

    /// <summary>LONG ARMS (host, effects only): a pick up from out of normal reach.</summary>
    [HarmonyPatch(typeof(ServerPlayerControlsImpl_Default), "ReceivePickUpEvent")]
    internal static class LongArmsPickupPatch
    {
        private static void Prefix(ServerPlayerControlsImpl_Default __instance, GameObject _target, out Vector4 __state)
        {
            __state = Vector4.zero;
            if (!SpecialRuntime.InLevel || _target == null) return;
            try
            {
                if (!SpecialRuntime.Has(__instance.gameObject, Special.LongArms)) return;
                if (!ThrowingModule.BeyondReach(__instance.transform.position, _target)) return;
                Vector3 at = _target.transform.position;
                __state = new Vector4(at.x, at.y, at.z, 1f);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("LongArms.Pickup", error);
            }
        }

        private static void Postfix(ServerPlayerControlsImpl_Default __instance, Vector4 __state)
        {
            if (__state.w <= 0f) return;
            try
            {
                ServerPlayerAttachmentCarrier hands = __instance.GetComponent<ServerPlayerAttachmentCarrier>();
                if (hands != null && hands.InspectCarriedItem() != null)
                    ThrowingModule.ReachedFar(__instance.gameObject, new Vector3(__state.x, __state.y, __state.z), false, null);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("LongArms.PickupFx", error);
            }
        }
    }

    /// <summary>LONG ARMS (host, effects only): a put down out of normal reach.</summary>
    [HarmonyPatch(typeof(ServerPlayerControlsImpl_Default), "ReceivePlaceEvent")]
    internal static class LongArmsPlacePatch
    {
        private static void Prefix(ServerPlayerControlsImpl_Default __instance, GameObject _target, out Vector4 __state)
        {
            __state = Vector4.zero;
            if (!SpecialRuntime.InLevel || _target == null) return;
            try
            {
                if (!SpecialRuntime.Has(__instance.gameObject, Special.LongArms)) return;
                if (!ThrowingModule.BeyondReach(__instance.transform.position, _target)) return;
                Vector3 at = _target.transform.position;
                __state = new Vector4(at.x, at.y, at.z, 1f);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("LongArms.Place", error);
            }
        }

        private static void Postfix(ServerPlayerControlsImpl_Default __instance, Vector4 __state)
        {
            if (__state.w <= 0f) return;
            try
            {
                ServerPlayerAttachmentCarrier hands = __instance.GetComponent<ServerPlayerAttachmentCarrier>();
                if (hands != null && hands.InspectCarriedItem() == null)
                    ThrowingModule.ReachedFar(__instance.gameObject, new Vector3(__state.x, __state.y, __state.z), false, null);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("LongArms.PlaceFx", error);
            }
        }
    }

    /// <summary>LONG ARMS (host, effects only): chopping or washing out of normal reach starts.</summary>
    [HarmonyPatch(typeof(ServerPlayerControlsImpl_Default), "ReceiveInteractEvent")]
    internal static class LongArmsInteractPatch
    {
        private static void Postfix(ServerPlayerControlsImpl_Default __instance, GameObject _target)
        {
            if (!SpecialRuntime.InLevel || _target == null) return;
            try
            {
                if (!SpecialRuntime.Has(__instance.gameObject, Special.LongArms)) return;
                if (!ThrowingModule.BeyondReach(__instance.transform.position, _target)) return;
                ThrowingModule.ReachedFar(__instance.gameObject, _target.transform.position, true, _target);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("LongArms.InteractFx", error);
            }
        }
    }

    /// <summary>
    /// CATCHER'S MITT, and a Homing Throw / Assembly Line throw's target: the host's catch scan
    /// for this chef looks all the way round (see ArcScanPatch).
    /// </summary>
    [HarmonyPatch(typeof(PlayerControls), "ScanForCatch")]
    internal static class CatchScanPatch
    {
        private static void Prefix(PlayerControls __instance)
        {
            ThrowingModule.CatchScan = null;
            if (!SpecialRuntime.InLevel) return;
            try
            {
                GameObject chef = __instance.gameObject;
                if (SpecialRuntime.Has(chef, Special.CatchersMitt) || ThrowingModule.HasIncoming(chef))
                    ThrowingModule.CatchScan = chef;
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
    /// Catch scan: 4 m all the way round instead of 2 m in front.
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
                    // As far as Catcher's Mitt reaches (the catch check itself sets each one's distance).
                    detectionRadius = Mathf.Max(detectionRadius, 4f);
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
    /// The host's catch check, with the catcher's own specials. CATCHER'S MITT: any direction and a
    /// longer distance for this one call (restored in the finalizer); an aimed throw's target:
    /// any direction. BUTTERFINGERS: never catches ("OOPS!" when it would have).
    /// </summary>
    [HarmonyPatch(typeof(ServerAttachmentCatcher), "CanHandleCatch")]
    internal static class CatchCheckPatch
    {
        private static readonly FieldInfo CatcherField = AccessTools.Field(typeof(ServerAttachmentCatcher), "m_catcher");

        private static void Prefix(ServerAttachmentCatcher __instance, ICatchable _object, out Vector2 __state)
        {
            __state = new Vector2(-1f, -1f);
            if (!SpecialRuntime.InLevel || CatcherField == null) return;
            try
            {
                ChefState state = SpecialRuntime.StateOf(__instance.gameObject);
                if (state == null) return;
                bool mitt = state.Has(Special.CatchersMitt);
                bool aimed = !mitt && _object != null && ThrowingModule.AimedAt(_object.AccessGameObject(), __instance.gameObject);
                if (!mitt && !aimed) return;
                var catcher = CatcherField.GetValue(__instance) as AttachmentCatcher;
                if (catcher == null) return;
                __state = new Vector2(catcher.m_catchDistance, catcher.m_catchAngleMax);
                catcher.m_catchDistance = mitt ? Mathf.Max(catcher.m_catchDistance + 1.5f, 4f) : Mathf.Max(catcher.m_catchDistance, 2.5f);
                catcher.m_catchAngleMax = 180f;
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("CatchersMitt.Check", error);
            }
        }

        private static void Postfix(ServerAttachmentCatcher __instance, ICatchable _object, Vector2 __state, ref bool __result)
        {
            if (!__result || !SpecialRuntime.InLevel) return;
            try
            {
                ChefState state = SpecialRuntime.StateOf(__instance.gameObject);
                if (state == null) return;
                GameObject item = _object != null ? _object.AccessGameObject() : null;
                if (state.Has(Special.Butterfingers))
                {
                    __result = false;
                    ThrowingModule.Oops(state, item);
                    return;
                }
                if (__state.x < 0f || item == null || !state.Has(Special.CatchersMitt)) return;
                // Would the game's own check have taken it? If not, the mitt did.
                Vector3 offset = __instance.transform.position - item.transform.position;
                IAttachment attachment = item.RequestInterface<IAttachment>();
                RigidbodyMotion motion = attachment != null ? attachment.AccessMotion() : null;
                Vector3 velocity = motion != null ? motion.GetVelocity() : Vector3.zero;
                Vector3 forward = __instance.transform.forward;
                float angle = Vector2.Angle(new Vector2(velocity.x, velocity.z), -new Vector2(forward.x, forward.z));
                if (offset.sqrMagnitude > __state.x * __state.x || angle > __state.y) ThrowingModule.NoteAssist(__instance.gameObject);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Throwing.CatchCheck", error);
            }
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

    /// <summary>JUGGLER's window, CATCHER'S MITT's "CAUGHT!", an aimed throw arriving (host).</summary>
    [HarmonyPatch(typeof(ServerAttachmentCatcher), "HandleCatch")]
    internal static class CatchDonePatch
    {
        private static void Postfix(ServerAttachmentCatcher __instance, ICatchable _object)
        {
            if (!SpecialRuntime.InLevel) return;
            try
            {
                ThrowingModule.OnCaught(__instance, _object != null ? _object.AccessGameObject() : null);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Throwing.Catch", error);
            }
        }
    }

    /// <summary>JUGGLER, LOB, HOMING THROW, ASSEMBLY LINE aim and CATCHER'S MITT rebound tracking (host).</summary>
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

    /// <summary>WEAK ARM on the player's machine: letting go of the throw says why nothing flew.</summary>
    [HarmonyPatch(typeof(ClientPlayerControlsImpl_Default), "Update_Throw")]
    internal static class WeakArmNoticePatch
    {
        private static void Prefix(ClientPlayerControlsImpl_Default __instance, bool justReleased, bool isSuppressed)
        {
            if (!justReleased || isSuppressed || !SpecialRuntime.InLevel) return;
            try
            {
                ChefState state = SpecialRuntime.StateOf(__instance.gameObject);
                if (state == null || !state.Local || !state.Has(Special.WeakArm)) return;
                if (Time.unscaledTime - state.Get("WeakArm.ShownAt", -100f) < 1f) return;
                ClientPlayerAttachmentCarrier hands = __instance.GetComponent<ClientPlayerAttachmentCarrier>();
                GameObject item = hands != null ? hands.InspectCarriedItem() : null;
                if (item == null || item.RequestInterface<IClientThrowable>() == null) return;
                state.Set("WeakArm.ShownAt", Time.unscaledTime);
                SpecialFx.Text(__instance.gameObject, Lang.T("CAN'T THROW", "KHÔNG NÉM ĐƯỢC"), SpecialFx.ColourOf(Special.WeakArm));
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("WeakArm.Notice", error);
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
                if (!SpecialRuntime.Has(__instance.gameObject, Special.StickyFingers)) return true;
                ThrowingModule.KeptOnFall(__instance.gameObject);
                return false;
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("StickyFingers.Respawn", error);
                return true;
            }
        }
    }

    /// <summary>
    /// SHOVE: a dash by a chef with Shove knocks harder (whichever machine runs the bump; the
    /// multiplier is restored in the finalizer). The dash is only timed on the dasher's own
    /// machine, so that machine asks the host to make the chef it hit drop what it holds.
    /// </summary>
    [HarmonyPatch(typeof(ClientPlayerControlsImpl_Default), "OnDashCollision")]
    internal static class ShovePatch
    {
        private static readonly FieldInfo ControlsField =
            AccessTools.Field(typeof(ClientPlayerControlsImpl_Default), "m_controls");
        private static PlayerControls.ImpactData _changed;
        private static float _multiplier;

        private const float ShoveStrength = 1.4f;

        private static void Prefix(ClientPlayerControlsImpl_Default __instance, PlayerControls _otherPlayer, out bool __state)
        {
            __state = false;
            if (!SpecialRuntime.InLevel || ControlsField == null) return;
            try
            {
                if (!__instance.IsDashing()) return;
                var controls = ControlsField.GetValue(__instance) as PlayerControls;
                if (controls == null || controls.Movement == null || !SpecialRuntime.Has(controls.gameObject, Special.Shove)) return;
                // Versus: a teammate gets the usual bump.
                if (_otherPlayer != null && ThrowingModule.SameSideInVersus(SpecialRuntime.StateOf(controls.gameObject),
                        SpecialRuntime.StateOf(_otherPlayer.gameObject))) return;
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

        private static void Postfix(ClientPlayerControlsImpl_Default __instance, PlayerControls _otherPlayer, bool __state)
        {
            if (!__state || _otherPlayer == null) return;
            try
            {
                var controls = ControlsField.GetValue(__instance) as PlayerControls;
                if (controls != null) ThrowingModule.RequestShove(controls.gameObject, _otherPlayer);
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
