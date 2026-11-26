using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Team17.Online.Multiplayer.Messaging;
using UnityEngine;

namespace ChefSkills.Specials
{
    /// <summary>
    /// Moving a chef somewhere else, safely, on the host and on the chef's own machine.
    ///
    /// The host is authoritative: <see cref="ServerMove"/> puts the chef's transform and rigidbody
    /// there, and its position goes out to everyone with the chef's usual sync. A remote player's
    /// machine would only follow once that arrives (its correction snaps it when it is more than
    /// 2.5 m off), so the player's own machine also moves its chef at once: either ahead of the host
    /// (<see cref="PredictMove"/>, from an active's Predict), or as soon as the host says it moved it
    /// (<see cref="TopicMoved"/>: a teammate's Swap or Summon, a Safety Net catch). The move goes
    /// through the chef's PositionRecorder, which keeps the jump out of the lag compensation, and the
    /// correction is held back until the host reports the chef at the new place (or gives up), so
    /// the chef isn't pulled back while the host hasn't moved it yet.
    ///
    /// Actives aim from where the player's own machine had the chef at the press
    /// (ChefState.RequestPosition/RequestForward), so the host and the owner pick the same spot.
    /// The owner still sends the host the exact spot it predicted; the host uses it if close to its
    /// own pick and valid (<see cref="Resolve"/>), so both end up in exactly the same place.
    /// </summary>
    internal static class Warp
    {
        private static readonly FieldInfo RecorderField =
            AccessTools.Field(typeof(ClientChefSynchroniser), "m_PositionRecorder");
        private static readonly FieldInfo StampField =
            AccessTools.Field(typeof(ClientChefSynchroniser), "m_ReceivedTimeStamp");
        private static readonly MethodInfo LocalParentMethod =
            AccessTools.Method(typeof(ClientChefSynchroniser), "GetLocalCurrentParent");

        // How long the owner holds its correction after a predicted move, at most.
        private const float WindowSeconds = 1.2f;
        // How long the host waits for the owner's own pick before using its own.
        private const float ProposalWaitSeconds = 0.4f;
        // Spacing of the floor cells looked at around a landing spot (less than a chef is wide, so
        // no wall fits between two neighbouring free cells).
        private const float CellSize = 0.4f;
        // The kitchen's outline (counters, chefs, spawn points) is widened by this much.
        private const float KitchenMargin = 0.3f;

        private sealed class Window
        {
            public Vector3 From, Target;
            public float Until;
        }

        private sealed class Pending
        {
            public ChefState Chef;
            public Special Special;
            public Vector3 HostTarget;
            public float Trust;
            public float Until;
            public Func<Vector3, bool> Valid;
            public Action<Vector3> Apply;
        }

        private sealed class Hop
        {
            public Transform Root, Model;
            public Vector3 Offset;
            public float Start, Duration, Height;
            // A flight: the model goes from From to To in the world, whatever its chef does
            // meanwhile (on the other machines the chef itself only gets there with the game's sync),
            // leaving rings on the floor behind it.
            public bool Flight;
            // This machine put the chef at To itself (the host, and the chef's own machine): the
            // flight ends on the chef wherever it has gone since, never on a spot it has left.
            public bool Settled;
            public PlayerControls Controls;
            public float SettleAt = -1f;
            public Vector3 From, To;
            public Color Trail;
            public float NextTrail;
        }

        private sealed class Predicted
        {
            public Vector3 Target;
            public float At;
        }

        // Owner machine: chef GameObject instance ID -> its correction is held.
        private static readonly Dictionary<int, Window> s_windows = new Dictionary<int, Window>();
        // Owner machine: chef GameObject instance ID -> where it last moved its chef itself.
        private static readonly Dictionary<int, Predicted> s_predicted = new Dictionary<int, Predicted>();
        // Host: chef entity ID -> a move waiting for the owner's pick.
        private static readonly Dictionary<uint, Pending> s_pending = new Dictionary<uint, Pending>();
        private static readonly List<Hop> s_hops = new List<Hop>();

        internal static readonly ushort TopicProposal = (ushort)((int)Special.Vault * 10 + 0);
        /// <summary>Host -> all: the host moved a remote chef (Vector1 = feet, D = keep running, E = hop).</summary>
        internal static readonly ushort TopicMoved = (ushort)((int)Special.Vault * 10 + 1);
        /// <summary>
        /// Host -> all: the host flew a chef somewhere (Swap, Summon, a rescue): Vector1 = where to,
        /// Vector2 = where from, G = seconds, I = special &lt;&lt; 10 | arc height in cm.
        /// </summary>
        internal static readonly ushort TopicFlight = (ushort)((int)Special.Vault * 10 + 2);

        private static bool s_masks;
        internal static int GroundMask, BlockMask, SolidMask, ArcMask, PlayersLayer = -1, WorktopLayer = -1;

        internal static void InitMasks()
        {
            if (s_masks) return;
            s_masks = true;
            GroundMask = LayerMask.GetMask("Ground", "SlopedGround");
            // What a chef can't stand in: the game's own capsule check (CapsuleCollisionHelper),
            // but other chefs count too.
            BlockMask = ~Layers("KillPlane", "Ground", "SlopedGround", "Ignore Raycast", "PlayerTriggerZone",
                "Attachments", "HeldAttachments", "PushedObjectBounds", "AttachedBackpack", "UI");
            // What a chef can't hop or see past: anything solid but chefs, items (on counters or
            // carried) and the invisible helpers stations keep items in with.
            SolidMask = ~Layers("KillPlane", "Ground", "SlopedGround", "Ignore Raycast", "PlayerTriggerZone",
                "Attachments", "HeldAttachments", "PushedObjectBounds", "AttachedBackpack", "UI", "Players",
                "PlayersRespawn", "CookingStationBlock", "PlateStationBlock");
            ArcMask = SolidMask | GroundMask;
            PlayersLayer = LayerMask.NameToLayer("Players");
            WorktopLayer = LayerMask.NameToLayer("Worktops");
        }

        private static int Layers(params string[] names)
        {
            int mask = 0;
            for (int i = 0; i < names.Length; i++)
            {
                int layer = LayerMask.NameToLayer(names[i]);
                if (layer >= 0) mask |= 1 << layer;
            }
            return mask;
        }

        internal static void Clear()
        {
            s_windows.Clear();
            s_predicted.Clear();
            s_pending.Clear();
            for (int i = 0; i < s_hops.Count; i++)
                if (s_hops[i].Model != null) s_hops[i].Model.localPosition = Vector3.zero;
            s_hops.Clear();
            s_kitchenAt = -100f;
        }

        // --- geometry --------------------------------------------------------------------------

        /// <summary>Flat facing of a chef.</summary>
        internal static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-4f ? v.normalized : Vector3.forward;
        }

        internal static float FlatDistance(Vector3 a, Vector3 b)
        {
            a.y = b.y = 0f;
            return Vector3.Distance(a, b);
        }

        /// <summary>Floor under <paramref name="p"/> within <paramref name="tolerance"/> of height <paramref name="refY"/>.</summary>
        internal static bool Floor(Vector3 p, float refY, float tolerance, out Vector3 floor)
        {
            InitMasks();
            floor = p;
            RaycastHit hit;
            if (!Physics.Raycast(new Vector3(p.x, refY + 1.5f, p.z), Vector3.down, out hit, 1.5f + tolerance + 0.5f,
                    GroundMask, QueryTriggerInteraction.Ignore))
                return false;
            if (Mathf.Abs(hit.point.y - refY) > tolerance) return false;
            floor = hit.point;
            return true;
        }

        private static void Capsule(ChefState chef, Vector3 feet, out Vector3 bottom, out Vector3 top, out float radius)
        {
            float height = 1.4f, centre = 0.7f;
            radius = 0.35f;
            CapsuleCollider capsule = chef != null && chef.Chef != null ? chef.Chef.GetComponent<CapsuleCollider>() : null;
            if (capsule != null)
            {
                float scale = chef.Chef.transform.lossyScale.y;
                radius = capsule.radius * Mathf.Max(chef.Chef.transform.lossyScale.x, 0.01f);
                height = capsule.height * scale;
                centre = capsule.center.y * scale;
            }
            radius *= 0.95f;
            float half = Mathf.Max(0f, height * 0.5f - radius);
            // Lifted a little so the floor's edge never counts.
            bottom = feet + Vector3.up * Mathf.Max(centre - half, radius + 0.05f);
            top = feet + Vector3.up * Mathf.Max(centre + half, radius + 0.06f);
        }

        /// <summary>Nothing solid where the chef would stand at <paramref name="feet"/> (other chefs count).</summary>
        internal static bool Free(ChefState chef, Vector3 feet, GameObject alsoIgnore)
        {
            InitMasks();
            Vector3 bottom, top;
            float radius;
            Capsule(chef, feet, out bottom, out top, out radius);
            Collider[] hits = Physics.OverlapCapsule(bottom, top, radius, BlockMask, QueryTriggerInteraction.Ignore);
            Transform self = chef != null && chef.Chef != null ? chef.Chef.transform : null;
            Transform other = alsoIgnore != null ? alsoIgnore.transform : null;
            for (int i = 0; i < hits.Length; i++)
            {
                if (hits[i] == null) continue;
                Transform t = hits[i].transform;
                if (self != null && t.IsChildOf(self)) continue;
                if (other != null && t.IsChildOf(other)) continue;
                return false;
            }
            return true;
        }

        /// <summary>
        /// A chef standing at <paramref name="feet"/> would be in a volume that kills or respawns it
        /// (pit, water, the level's outer bounds), whatever layer the volume is on.
        /// </summary>
        internal static bool Deadly(Vector3 feet)
        {
            InitMasks();
            // Only where the chef's body would be, above the floor: every level has a kill plane (a
            // RespawnCollider) a little under its floor, so reaching below the feet found it under
            // every spot and refused them all.
            Collider[] hits = Physics.OverlapCapsule(feet + Vector3.up * 0.3f, feet + Vector3.up * 1.2f, 0.25f, ~0,
                QueryTriggerInteraction.Collide);
            for (int i = 0; i < hits.Length; i++)
            {
                if (hits[i] == null) continue;
                RespawnCollider volume = hits[i].GetComponent<RespawnCollider>();
                if (volume == null) volume = hits[i].GetComponentInParent<RespawnCollider>();
                if (volume == null || !volume.enabled) continue;
                if (PlayersLayer >= 0 && (volume.m_respawnFilter.value & (1 << PlayersLayer)) == 0) continue;                return true;
            }
            return false;
        }

        /// <summary>The nearest solid thing along a ray that isn't a chef (or anything a chef carries).</summary>
        internal static bool SolidRay(Vector3 from, Vector3 direction, float range, int mask, out RaycastHit nearest)
        {
            nearest = new RaycastHit();
            if (range <= 0f) return false;
            RaycastHit[] hits = Physics.RaycastAll(from, direction, range, mask, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            bool found = false;
            for (int i = 0; i < hits.Length; i++)
            {
                Collider collider = hits[i].collider;
                if (collider == null || hits[i].distance >= best) continue;
                if (IsChefPart(collider)) continue;
                best = hits[i].distance;
                nearest = hits[i];
                found = true;
            }
            return found;
        }

        internal static bool SolidRay(Vector3 from, Vector3 direction, float range, int mask)
        {
            RaycastHit hit;
            return SolidRay(from, direction, range, mask, out hit);
        }

        /// <summary>Something solid (not a chef) right at <paramref name="p"/>.</summary>
        internal static bool InsideSolid(Vector3 p)
        {
            InitMasks();
            Collider[] hits = Physics.OverlapSphere(p, 0.05f, SolidMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
                if (hits[i] != null && !IsChefPart(hits[i])) return true;
            return false;
        }

        internal static bool IsChefPart(Collider collider)
        {
            return collider.GetComponentInParent<PlayerControls>() != null;
        }

        /// <summary>A counter: a worktop, or anything items can be put on.</summary>
        internal static bool IsCounter(Collider collider)
        {
            if (collider == null) return false;
            InitMasks();
            if (WorktopLayer >= 0 && collider.gameObject.layer == WorktopLayer) return true;
            return collider.GetComponentInParent<AttachStation>() != null;
        }

        /// <summary>A free, safe, in-kitchen floor spot right where <paramref name="feet"/> is.</summary>
        internal static bool Landable(ChefState chef, Vector3 feet)
        {
            Vector3 floor;
            return Floor(feet, feet.y, 0.35f, out floor) && Free(chef, floor, null) && !Deadly(floor) && InKitchen(floor);
        }

        /// <summary>A free spot next to <paramref name="centre"/> for <paramref name="mover"/>.</summary>
        internal static bool SpotNear(Vector3 centre, Vector3 forward, ChefState mover, out Vector3 spot)
        {
            forward = Flat(forward);
            Vector3 right = new Vector3(forward.z, 0f, -forward.x);
            Vector3[] offsets =
            {
                forward, right, -right, -forward,
                (forward + right) * 0.8f, (forward - right) * 0.8f, (-forward + right) * 0.8f, (-forward - right) * 0.8f,
                forward * 1.6f, right * 1.6f, -right * 1.6f, -forward * 1.6f
            };
            for (int i = 0; i < offsets.Length; i++)
            {
                Vector3 floor;
                if (Floor(centre + offsets[i], centre.y, 0.6f, out floor) && Free(mover, floor, null) && !Deadly(floor))
                {
                    spot = floor;
                    return true;
                }
            }
            spot = centre;
            return false;
        }

        // --- the playable kitchen --------------------------------------------------------------

        private static float s_kitchenAt = -100f;
        private static bool s_kitchenKnown;
        private static float s_minX, s_maxX, s_minZ, s_maxZ;

        /// <summary>
        /// Inside the playable kitchen: within the level's bounds (when it has them) and within the
        /// outline of its counters and stations, the chefs and their spawn points. Keeps teleports
        /// off the scenery floor behind the kitchen's outer walls.
        /// </summary>
        internal static bool InKitchen(Vector3 p)
        {
            float now = Time.unscaledTime;
            if (now - s_kitchenAt > 1f || now < s_kitchenAt)
            {
                s_kitchenAt = now;
                s_kitchenKnown = MeasureKitchen();
            }
            if (s_levelBounds && !LevelBounds.ActiveBoundsContain(p + Vector3.up * 0.5f)) return false;
            if (!s_kitchenKnown) return true;
            return p.x >= s_minX && p.x <= s_maxX && p.z >= s_minZ && p.z <= s_maxZ;
        }

        // The level's own bounds are only trusted while every chef standing in the kitchen is inside them.
        private static bool s_levelBounds;

        private static bool MeasureKitchen()
        {
            bool stations = false, any = false;
            s_levelBounds = true;
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
            {
                if (chefs[i].Chef == null || chefs[i].Controls == null || chefs[i].Controls.m_bRespawning) continue;
                Vector3 p = chefs[i].Position;
                if (chefs[i].Controls.GroundCollider != null && !LevelBounds.ActiveBoundsContain(p + Vector3.up * 0.5f))
                    s_levelBounds = false;
                Grow(p, 0.5f, ref minX, ref maxX, ref minZ, ref maxZ);
                minY = Mathf.Min(minY, p.y);
                maxY = Mathf.Max(maxY, p.y);
                any = true;
            }
            foreach (PlayerRespawnBehaviour spawn in UnityEngine.Object.FindObjectsOfType<PlayerRespawnBehaviour>())
            {
                Vector3 p = spawn.m_startParent != null ? spawn.m_startParent.TransformPoint(spawn.m_startLocation) : spawn.m_startLocation;
                Grow(p, 0.5f, ref minX, ref maxX, ref minZ, ref maxZ);
                any = true;
            }
            foreach (AttachStation station in UnityEngine.Object.FindObjectsOfType<AttachStation>())
            {
                Vector3 p = station.transform.position;
                // Stations parked far below or above the kitchen don't count.
                if (minY <= maxY && (p.y < minY - 5f || p.y > maxY + 5f)) continue;
                // A counter is a tile (1.2 m) wide.
                Grow(p, 0.6f, ref minX, ref maxX, ref minZ, ref maxZ);
                stations = true;
            }
            if (!stations || !any) return false;
            s_minX = minX - KitchenMargin;
            s_maxX = maxX + KitchenMargin;
            s_minZ = minZ - KitchenMargin;
            s_maxZ = maxZ + KitchenMargin;
            return true;
        }

        private static void Grow(Vector3 p, float half, ref float minX, ref float maxX, ref float minZ, ref float maxZ)
        {
            minX = Mathf.Min(minX, p.x - half);
            maxX = Mathf.Max(maxX, p.x + half);
            minZ = Mathf.Min(minZ, p.z - half);
            maxZ = Mathf.Max(maxZ, p.z + half);
        }

        /// <summary>
        /// The spot at <paramref name="feet"/> opens onto some floor (at least <paramref name="need"/>
        /// free cells within <paramref name="radius"/> m, joined to it): not a closed-in pocket between
        /// counters and walls the chef couldn't get out of. Cells are remembered in <paramref name="memo"/>
        /// for the whole search.
        /// </summary>
        internal static bool Roomy(ChefState chef, Vector3 feet, float refY, Dictionary<long, bool> memo, int need, float radius)
        {
            int cx = Mathf.FloorToInt(feet.x / CellSize), cz = Mathf.FloorToInt(feet.z / CellSize);
            int reach = Mathf.CeilToInt(radius / CellSize);
            var queue = new Queue<long>();
            var seen = new HashSet<long>();
            // Seeds: the free cells around the spot itself (close enough that nothing fits between).
            for (int dx = 0; dx <= 1; dx++)
                for (int dz = 0; dz <= 1; dz++)
                {
                    long key = Key(cx + dx, cz + dz);
                    if (seen.Add(key) && CellFree(chef, cx + dx, cz + dz, refY, memo)) queue.Enqueue(key);
                }
            int count = 0;
            while (queue.Count > 0)
            {
                long key = queue.Dequeue();
                if (++count >= need) return true;
                int x = (int)(key >> 32), z = (int)(key & 0xFFFFFFFFL);
                for (int n = 0; n < 4; n++)
                {
                    int nx = x + (n == 0 ? 1 : n == 1 ? -1 : 0), nz = z + (n == 2 ? 1 : n == 3 ? -1 : 0);
                    if (Mathf.Abs(nx - cx) > reach || Mathf.Abs(nz - cz) > reach) continue;
                    long next = Key(nx, nz);
                    if (!seen.Add(next)) continue;
                    if (CellFree(chef, nx, nz, refY, memo)) queue.Enqueue(next);
                }
            }
            return false;
        }

        private static long Key(int x, int z)
        {
            return ((long)x << 32) | (uint)z;
        }

        private static bool CellFree(ChefState chef, int x, int z, float refY, Dictionary<long, bool> memo)
        {
            long key = Key(x, z);
            bool free;
            if (memo.TryGetValue(key, out free)) return free;
            Vector3 floor;
            free = Floor(new Vector3(x * CellSize, refY, z * CellSize), refY, 0.5f, out floor) && Free(chef, floor, null);
            memo[key] = free;
            return free;
        }

        // --- who can be moved ------------------------------------------------------------------

        /// <summary>Why the chef can't be moved now (null: it can).</summary>
        internal static string Blocked(ChefState chef)
        {
            if (chef == null || chef.Chef == null || chef.Controls == null || !chef.Chef.activeInHierarchy) return Lang.Both("CAN'T MOVE NOW", "GIỜ CHƯA ĐI ĐƯỢC");
            PlayerControls controls = chef.Controls;
            if (controls.m_bRespawning) return Lang.Both("RESPAWNING", "ĐANG HỒI SINH");
            if (!controls.enabled) return Lang.Both("CAN'T MOVE NOW", "GIỜ CHƯA ĐI ĐƯỢC");
            // Suppression (respawn pause, menus) is only known on the chef's own machine.
            if (chef.Local && controls.IsSuppressed()) return Lang.Both("CAN'T MOVE NOW", "GIỜ CHƯA ĐI ĐƯỢC");
            if (controls.GetCurrentlyInteracting() != null) return Lang.Both("BUSY", "ĐANG BẬN");
            // On the host a chef is only kinematic while something else moves it (cannon, portal).
            if (SpecialNet.IsServer)
            {
                Rigidbody body = chef.Chef.GetComponent<Rigidbody>();
                if (body == null || body.isKinematic) return Lang.Both("CAN'T MOVE NOW", "GIỜ CHƯA ĐI ĐƯỢC");
            }
            return null;
        }

        internal static bool Movable(ChefState chef)
        {
            return Blocked(chef) == null;
        }

        // --- moving ----------------------------------------------------------------------------

        /// <summary>Host: put the chef at <paramref name="feet"/> (and tell its owner at once).</summary>
        internal static void ServerMove(ChefState chef, Vector3 feet, bool keepRun, float hop)
        {
            if (chef == null || chef.Chef == null) return;
            Vector3 from = Place(chef, feet, keepRun);
            if (hop > 0f) StartHop(chef, from, feet, hop);
            // To the owner (who follows at once) and, for a hop, to everyone else (who draw the arc).
            if (chef.Id != 0 && SpecialNet.Online && (!chef.Local || hop > 0f))
                SpecialNet.SendToAll(new SpecialMessage
                {
                    Topic = TopicMoved, Chef = chef.Id, Vector1 = feet, D = keepRun ? 1f : 0f, E = hop
                });
        }

        /// <summary>Host: the chef's transform and rigidbody at <paramref name="feet"/>; returns where it was.</summary>
        private static Vector3 Place(ChefState chef, Vector3 feet, bool keepRun)
        {
            Transform root = chef.Chef.transform;
            Vector3 from = root.position;
            Rigidbody body = chef.Chef.GetComponent<Rigidbody>();
            Vector3 velocity = body != null ? body.velocity : Vector3.zero;
            root.position = feet;
            if (body != null)
            {
                body.position = feet;
                body.velocity = keepRun ? new Vector3(velocity.x, 0f, velocity.z) : Vector3.zero;
            }
            GroundCast ground = chef.Chef.GetComponent<GroundCast>();
            if (ground != null) ground.ForceUpdateNow();
            return from;
        }

        /// <summary>
        /// Host: move the chef to <paramref name="feet"/>, seen on every machine as a flight from
        /// where it was, over <paramref name="seconds"/>, along an arc <paramref name="height"/>
        /// high, trailing rings in the special's colour. For a chef moved by someone else (Swap,
        /// Summon) or rescued, so nobody loses track of where it went.
        /// </summary>
        internal static void ServerFlight(ChefState chef, Vector3 feet, Special special, float seconds, float height)
        {
            if (chef == null || chef.Chef == null) return;
            Vector3 from = Place(chef, feet, false);
            StartFlight(chef, from, feet, seconds, height, SpecialFx.ColourOf(special), true);
            if (chef.Id != 0 && SpecialNet.Online)
                SpecialNet.SendToAll(new SpecialMessage
                {
                    Topic = TopicFlight, Chef = chef.Id, Vector1 = feet, Vector2 = from, G = seconds,
                    I = ((int)special << 10) | Mathf.Clamp(Mathf.RoundToInt(height * 100f), 0, 1023)
                });
        }

        /// <summary>Clients: the host flew a chef. Its owner moves it there too; everyone draws the flight.</summary>
        internal static void OnFlight(SpecialMessage message)
        {
            if (SpecialNet.IsServer) return;
            ChefState chef = SpecialRuntime.StateById(message.Chef);
            if (chef == null || chef.Chef == null || chef.Controls == null || chef.Controls.m_bRespawning) return;
            Vector3 feet = message.Vector1;
            // The chef's own machine flies it from where its player sees it (the host's view of it
            // is a little behind), and moves it there at once like the host did.
            Vector3 from = message.Vector2;
            if (chef.Local)
            {
                from = chef.Position;
                if (Vector3.Distance(chef.Position, feet) > 0.3f) PredictMove(chef, feet, false, 0f);
            }
            var special = (Special)(message.I >> 10);
            StartFlight(chef, from, feet, message.G, (message.I & 1023) / 100f, SpecialFx.ColourOf(special), chef.Local);
        }

        private static void StartFlight(ChefState chef, Vector3 from, Vector3 to, float seconds, float height, Color trail, bool settled)
        {
            Transform model = chef.Chef.transform.Find("Chef");
            if (model == null) return;
            for (int i = s_hops.Count - 1; i >= 0; i--)
                if (s_hops[i].Model == model)
                {
                    model.localPosition = Vector3.zero;
                    s_hops.RemoveAt(i);
                }
            s_hops.Add(new Hop
            {
                Root = chef.Chef.transform, Model = model, Start = SpecialRuntime.Now, Duration = Mathf.Max(0.1f, seconds),
                Height = height, Flight = true, Settled = settled, Controls = chef.Controls, From = from, To = to, Trail = trail
            });
            SpecialFx.Burst(from, trail, 1.1f, 0.45f);
        }

        /// <summary>Owner machine (not the host): move its own chef now, ahead of the host.</summary>
        internal static void PredictMove(ChefState chef, Vector3 feet, bool keepRun, float hop)
        {
            if (chef == null || chef.Chef == null) return;
            Transform root = chef.Chef.transform;
            Vector3 from = root.position;
            Rigidbody body = chef.Chef.GetComponent<Rigidbody>();
            Vector3 velocity = body != null ? body.velocity : Vector3.zero;
            ClientChefSynchroniser sync = chef.Chef.GetComponent<ClientChefSynchroniser>();
            PositionRecorder recorder = sync != null && RecorderField != null ? RecorderField.GetValue(sync) as PositionRecorder : null;
            if (recorder != null)
            {
                Transform parent = LocalParentMethod != null ? LocalParentMethod.Invoke(sync, null) as Transform : null;
                // Moves the transform without the jump counting as movement in the lag compensation.
                recorder.Teleport(feet, parent);
            }
            else root.position = feet;
            if (body != null)
            {
                body.position = feet;
                body.velocity = keepRun ? new Vector3(velocity.x, 0f, velocity.z) : Vector3.zero;
            }
            GroundCast ground = chef.Chef.GetComponent<GroundCast>();
            if (ground != null) ground.ForceUpdateNow();
            int key = chef.Chef.GetInstanceID();
            if (sync != null)
                s_windows[key] = new Window { From = from, Target = feet, Until = SpecialRuntime.Now + WindowSeconds };
            s_predicted[key] = new Predicted { Target = feet, At = SpecialRuntime.Now };
            if (hop > 0f) StartHop(chef, from, feet, hop);
        }

        /// <summary>Owner machine: the host refused, so let the correction take the chef back.</summary>
        internal static void CancelPrediction(ChefState chef)
        {
            if (chef == null || chef.Chef == null) return;
            s_windows.Remove(chef.Chef.GetInstanceID());
            s_predicted.Remove(chef.Chef.GetInstanceID());
        }

        /// <summary>
        /// Clients: the host moved a chef. Its owner follows now unless it already did; the other
        /// machines see the chef glide there (the game's own sync), so they only add the hop.
        /// </summary>
        internal static void OnMoved(SpecialMessage message)
        {
            if (SpecialNet.IsServer) return;
            ChefState chef = SpecialRuntime.StateById(message.Chef);
            if (chef != null && !chef.Local && chef.Chef != null && message.E > 0f)
            {
                // No sideways part: the chef itself is still on its way to the new spot here.
                StartHop(chef, chef.Position, chef.Position, message.E);
                return;
            }
            if (chef == null || !chef.Local || chef.Chef == null || chef.Controls == null || chef.Controls.m_bRespawning) return;
            Vector3 feet = message.Vector1;
            Predicted predicted;
            if (s_predicted.TryGetValue(chef.Chef.GetInstanceID(), out predicted) &&
                SpecialRuntime.Now - predicted.At < 2.5f && Vector3.Distance(predicted.Target, feet) < 1.2f)
                return;
            if (Vector3.Distance(chef.Position, feet) < 0.8f) return;
            PredictMove(chef, feet, message.D > 0.5f, message.E);
        }

        /// <summary>
        /// Owner machine, from ClientChefSynchroniser.RunCorrection: true while a predicted move
        /// isn't confirmed by the host yet (the correction is skipped, but its movement history is
        /// still recorded so the lag compensation stays right afterwards).
        /// </summary>
        internal static bool HoldCorrection(ClientChefSynchroniser sync)
        {
            if (s_windows.Count == 0) return false;
            int key = sync.gameObject.GetInstanceID();
            Window window;
            if (!s_windows.TryGetValue(key, out window)) return false;
            Vector3 server = sync.GetServerGlobalPosition();
            float toTarget = Vector3.Distance(server, window.Target);
            float jump = Vector3.Distance(window.From, window.Target);
            if (SpecialRuntime.Now > window.Until || toTarget < Mathf.Min(1.2f, jump * 0.5f))
            {
                s_windows.Remove(key);
                return false;
            }
            PositionRecorder recorder = RecorderField != null ? RecorderField.GetValue(sync) as PositionRecorder : null;
            if (recorder != null && StampField != null)
            {
                Transform parent = LocalParentMethod != null ? LocalParentMethod.Invoke(sync, null) as Transform : null;
                recorder.TakeSample(TimeManager.GetFixedDeltaTime(sync.gameObject), parent, (float)StampField.GetValue(sync));
            }
            return true;
        }

        // --- agreeing on the spot --------------------------------------------------------------

        /// <summary>
        /// Host: do <paramref name="apply"/> with the spot to use. For the host's own chefs that is
        /// <paramref name="hostTarget"/> at once; for a remote chef it waits a moment for the spot
        /// its owner predicted, and uses that one if within <paramref name="trust"/> metres of the
        /// host's and <paramref name="valid"/> (null = any).
        /// </summary>
        internal static void Resolve(ChefState chef, Special special, Vector3 hostTarget, float trust,
            Func<Vector3, bool> valid, Action<Vector3> apply)
        {
            if (chef.Local || chef.Id == 0 || !SpecialNet.Online)
            {
                apply(hostTarget);
                return;
            }
            Pending old;
            if (s_pending.TryGetValue(chef.Id, out old)) Run(old, old.HostTarget);
            s_pending[chef.Id] = new Pending
            {
                Chef = chef, Special = special, HostTarget = hostTarget, Trust = trust, Valid = valid, Apply = apply,
                Until = SpecialRuntime.Now + ProposalWaitSeconds
            };
        }

        /// <summary>Owner machine: tell the host the spot it picked (or that it found none).</summary>
        internal static void Propose(ChefState chef, Special special, bool found, Vector3 target)
        {
            if (SpecialNet.IsServer) return;
            SpecialNet.SendToHost(new SpecialMessage
            {
                Topic = TopicProposal, Chef = chef.Id, I = (int)special, Vector1 = target, D = found ? 1f : 0f
            });
        }

        internal static void OnProposal(SpecialMessage message)
        {
            if (!SpecialNet.IsServer) return;
            Pending pending;
            if (!s_pending.TryGetValue(message.Chef, out pending) || (int)pending.Special != message.I) return;
            s_pending.Remove(message.Chef);
            Vector3 target = pending.HostTarget;
            Vector3 proposed = message.Vector1;
            if (message.D > 0.5f && Vector3.Distance(proposed, pending.HostTarget) <= pending.Trust &&
                (pending.Valid == null || pending.Valid(proposed)))
                target = proposed;
            Run(pending, target);
        }

        private static void Run(Pending pending, Vector3 target)
        {
            try
            {
                // The chef may have fallen or been destroyed meanwhile.
                if (pending.Chef.Chef == null || (pending.Chef.Controls != null && pending.Chef.Controls.m_bRespawning)) return;
                pending.Apply(target);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail(pending.Special + ".Apply", error);
            }
        }

        // --- a little visual hop ---------------------------------------------------------------

        private static void StartHop(ChefState chef, Vector3 from, Vector3 to, float height)
        {
            Transform model = chef.Chef.transform.Find("Chef");
            if (model == null) return;
            for (int i = s_hops.Count - 1; i >= 0; i--)
                if (s_hops[i].Model == model) s_hops.RemoveAt(i);
            s_hops.Add(new Hop
            {
                Root = chef.Chef.transform, Model = model, Offset = from - to, Start = SpecialRuntime.Now,
                Duration = 0.3f, Height = height
            });
        }

        // Other machines: after landing, the model eases from the landing spot onto the chef (still
        // arriving with the game's sync) over this long, rather than waiting there or jumping.
        private const float FlightSettleSeconds = 0.25f;
        private const float FlightWaitSeconds = 0.5f;

        private static void Fly(Hop hop, float t, float now)
        {
            // Fell again or respawning: the model goes back on the chef at once.
            if (hop.Controls != null && hop.Controls.m_bRespawning)
            {
                hop.Model.localPosition = Vector3.zero;
                hop.Model = null;
                return;
            }
            if (t < 1f)
            {
                // Eased: off quickly, slowing into the landing. Where the chef was put by this
                // machine, it lands on the chef as it is now (it may have moved on).
                float e = 1f - (1f - t) * (1f - t);
                Vector3 end = hop.Settled ? hop.Root.position : hop.To;
                Vector3 floor = Vector3.Lerp(hop.From, end, e);
                hop.Model.position = floor + Vector3.up * (hop.Height * 4f * t * (1f - t));
                if (now >= hop.NextTrail)
                {
                    hop.NextTrail = now + 0.06f;
                    SpecialFx.Burst(floor, hop.Trail, 0.45f, 0.35f);
                }
                return;
            }
            if (!hop.Settled)
            {
                float landed = (t - 1f) * hop.Duration;
                // Waits on the spot while the chef is still on its way (briefly: a chef that ran
                // on meanwhile never gets there), then eases onto it.
                if (hop.SettleAt < 0f && (Warp.FlatDistance(hop.Root.position, hop.To) <= 0.6f || landed >= FlightWaitSeconds))
                    hop.SettleAt = now;
                if (hop.SettleAt < 0f)
                {
                    hop.Model.position = hop.To;
                    return;
                }
                float settle = (now - hop.SettleAt) / FlightSettleSeconds;
                if (settle < 1f)
                {
                    hop.Model.position = Vector3.Lerp(hop.To, hop.Root.position, settle * settle);
                    return;
                }
            }
            hop.Model.localPosition = Vector3.zero;
            SpecialFx.Burst(hop.Settled ? hop.Root.position : hop.To, hop.Trail, 1.3f, 0.5f);
            hop.Model = null;
        }

        /// <summary>Every frame, every machine.</summary>
        internal static void Update()
        {
            float now = SpecialRuntime.Now;
            for (int i = s_hops.Count - 1; i >= 0; i--)
            {
                Hop hop = s_hops[i];
                if (hop.Model == null || hop.Root == null)
                {
                    s_hops.RemoveAt(i);
                    continue;
                }
                float t = (now - hop.Start) / hop.Duration;
                if (hop.Flight)
                {
                    Fly(hop, t, now);
                    if (hop.Model == null) s_hops.RemoveAt(i);
                    continue;
                }
                if (t >= 1f)
                {
                    hop.Model.localPosition = Vector3.zero;
                    s_hops.RemoveAt(i);
                    continue;
                }
                // Only the model moves (the chef itself is already there): from the old spot to
                // the new one along an arc.
                hop.Model.position = hop.Root.position + hop.Offset * (1f - t) + Vector3.up * (hop.Height * 4f * t * (1f - t));
            }

            if (s_pending.Count > 0 && SpecialNet.IsServer)
            {
                List<uint> due = null;
                foreach (KeyValuePair<uint, Pending> pair in s_pending)
                    if (now >= pair.Value.Until)
                    {
                        if (due == null) due = new List<uint>();
                        due.Add(pair.Key);
                    }
                if (due != null)
                    foreach (uint id in due)
                    {
                        Pending pending = s_pending[id];
                        s_pending.Remove(id);
                        Run(pending, pending.HostTarget);
                    }
            }

            if (s_windows.Count > 0)
            {
                List<int> stale = null;
                foreach (KeyValuePair<int, Window> pair in s_windows)
                    if (now > pair.Value.Until + 1f)
                    {
                        if (stale == null) stale = new List<int>();
                        stale.Add(pair.Key);
                    }
                if (stale != null)
                    foreach (int key in stale) s_windows.Remove(key);
            }
        }
    }

    /// <summary>
    /// Specials that move chefs around: Vault, Blink, Recall, Swap, Summon, Grapple (actives),
    /// Quick Respawn, Safety Net, Hover, Medic, Transit Pro (Special.PortalPro, which also does
    /// what the retired Cannoneer and Driver did), Lifeguard, Sticky Fingers' respawn time
    /// (passives) and Fragile (quirk).
    /// </summary>
    internal sealed class TeleportSpecials : ISpecialModule
    {
        private const float BlinkRange = 6f;
        private const float BlinkMinimum = 1f;
        private const float VaultReach = 1.6f;
        private const float VaultRange = 4f;

        private const float GrappleRange = 7f;
        private const float RecallSeconds = 20f;
        private const float SafetyNetCooldown = 30f;
        // Safety Net and Lifeguard: how long the chef flies back up.
        private const float RescueFlightSeconds = 0.4f;
        private const float LifeguardRange = 7f;
        private const float HoverRecharge = 10f;
        private const float HoverSeconds = 0.6f;
        // Respawn times, in seconds; each only ever shortens the level's own respawn time.
        private const float QuickRespawnTime = 1f;
        // A teammate of a Medic who fell empty-handed.
        private const float MedicRespawnTime = 1f;
        // The Medic itself.
        private const float MedicOwnRespawnTime = 2.5f;
        // Sticky Fingers, falling while holding something.
        private const float StickyRespawnTime = 3f;
        // Fragile adds this, after everything else.
        private const float FragileExtraTime = 5f;
        // Transit Pro: steering speed, and how long the burst after a teleporter, a cannon or a
        // steered platform lasts.
        internal const float DriverSpeed = 1.5f;
        internal const float TransitSeconds = 4f;

        private static readonly float[] VaultAngles = { 0f, 15f, -15f, 30f, -30f };
        private static readonly float[] GrappleAngles = { 0f, 8f, -8f, 16f, -16f };

        private static readonly ushort TopicRecall = (ushort)((int)Special.Recall * 10 + 0);
        private static readonly ushort TopicSafetyNet = (ushort)((int)Special.SafetyNet * 10 + 0);
        private static readonly ushort TopicHover = (ushort)((int)Special.Hover * 10 + 0);
        /// <summary>
        /// Host -> all: a chef fell (or was hit) and starts respawning. I = 1 if it carried anything
        /// (hands, Pocket, Pack Mule back), | 2 if Vector1 is its Quick Respawn spot.
        /// </summary>
        private static readonly ushort TopicFell = (ushort)((int)Special.Medic * 10 + 0);

        private const int RecallPlanted = 1, RecallReturned = 2;

        private const string FlagKey = "Recall.Flag", FlagUntilKey = "Recall.Until", FlagModeKey = "Recall.Mode";
        private const string HoverUntilKey = "Hover.Until", HoverReadyKey = "Hover.Ready";
        private const string NetReadyKey = "SafetyNet.Ready", CaughtKey = "Teleport.Caught";
        private const string LatestKey = "SafetyNet.Latest", SafeKey = "SafetyNet.Safe";

        private struct SafePoint
        {
            public Transform Parent;
            public Vector3 Local, World;
            public float Time;
            public bool Set;

            public Vector3 Position
            {
                get { return Parent != null ? Parent.TransformPoint(Local) : World; }
            }
        }

        private sealed class SpawnOverride
        {
            public PlayerRespawnBehaviour Behaviour;
            public Vector3 Location, Spot;
            public Transform Parent;
            public float Until;
        }

        /// <summary>What the host saw as a chef fell: the respawn rules depend on it.</summary>
        private sealed class Fall
        {
            public bool Carrying, Holding, HasSpot;
            public Vector3 Spot;
            public float At;
        }

        private sealed class Flag
        {
            public GameObject Marker;
            public Transform Parent;
            public Vector3 Local;

            public Vector3 Position
            {
                get { return Parent != null ? Parent.TransformPoint(Local) : Local; }
            }
        }

        private static float s_nextSafeSample;

        // Every machine: respawn times as the level had them, respawns sent to a medic or shortened
        // by a special, spawn points moved, planted Recall flags.
        private static readonly Dictionary<int, float> s_baseRespawn = new Dictionary<int, float>();
        private static readonly Dictionary<int, ChefState> s_medicFor = new Dictionary<int, ChefState>();
        private static readonly Dictionary<int, Special> s_quickFor = new Dictionary<int, Special>();
        private static readonly List<SpawnOverride> s_overrides = new List<SpawnOverride>();
        private static readonly Dictionary<ChefState, Flag> s_flags = new Dictionary<ChefState, Flag>();
        // Every machine: the host's word on each chef's latest fall (TopicFell).
        private static readonly Dictionary<ChefState, Fall> s_falls = new Dictionary<ChefState, Fall>();

        public void Register()
        {
            SpecialRuntime.RegisterActive(Special.Vault, new ActiveHandler { Execute = VaultExecute, Predict = VaultPredict, Targets = VaultTargets });
            SpecialRuntime.RegisterActive(Special.Blink, new ActiveHandler { Execute = BlinkExecute, Predict = BlinkPredict, Targets = BlinkTargets });
            SpecialRuntime.RegisterActive(Special.Grapple, new ActiveHandler { Execute = GrappleExecute, Predict = GrapplePredict, Targets = GrappleTargets });
            SpecialRuntime.RegisterActive(Special.Recall, new ActiveHandler
            {
                Execute = RecallExecute, Predict = RecallPredict, OnActivated = RecallActivated
            });
            SpecialRuntime.RegisterActive(Special.Swap, new ActiveHandler { Execute = SwapExecute, Targets = MateTargets });
            SpecialRuntime.RegisterActive(Special.Summon, new ActiveHandler { Execute = SummonExecute, Targets = MateTargets });

            SpecialNet.Register(Warp.TopicProposal, Warp.OnProposal);
            SpecialNet.Register(Warp.TopicMoved, Warp.OnMoved);
            SpecialNet.Register(Warp.TopicFlight, Warp.OnFlight);
            SpecialNet.Register(TopicRecall, OnRecallMessage);
            SpecialNet.Register(TopicSafetyNet, OnSafetyNetMessage);
            SpecialNet.Register(TopicHover, OnHoverMessage);
            SpecialNet.Register(TopicFell, OnFellMessage);
            SpecialNet.Register(SpecialNet.Topic.Denied, OnDenied);

            SpecialRuntime.Tick += OnTick;
            SpecialRuntime.LevelEnded += OnLevelEnded;
            SpecialRuntime.LevelStarted += OnLevelEnded;
        }

        private static void OnLevelEnded()
        {
            Warp.Clear();
            s_baseRespawn.Clear();
            s_medicFor.Clear();
            s_quickFor.Clear();
            s_overrides.Clear();
            foreach (KeyValuePair<ChefState, Flag> pair in s_flags)
                if (pair.Value.Marker != null) UnityEngine.Object.Destroy(pair.Value.Marker);
            s_flags.Clear();
            s_falls.Clear();
            s_unsure.Clear();
            s_transfers.Clear();
            s_landings.Clear();
            s_nextSafeSample = 0f;
            CannonState.Clear();
            PortalState.Clear();
        }

        private static void OnDenied(SpecialMessage message)
        {
            ChefState state = SpecialRuntime.StateById(message.Chef);
            if (state == null || !state.Local) return;
            Special special = (Special)message.I;
            if (special == Special.Vault || special == Special.Blink || special == Special.Grapple || special == Special.Recall)
                Warp.CancelPrediction(state);
        }

        private static void OnTick(float dt)
        {
            Warp.Update();
            if (SpecialNet.IsServer && (s_transfers.Count > 0 || s_landings.Count > 0))
            {
                RunTransfers(SpecialRuntime.Now);
                RunLandings(SpecialRuntime.Now);
            }
            float now = SpecialRuntime.Now;

            // Recall flags run out on every machine.
            if (s_flags.Count > 0)
            {
                List<ChefState> gone = null;
                foreach (KeyValuePair<ChefState, Flag> pair in s_flags)
                    if (pair.Key.Chef == null || now > pair.Key.Get(FlagUntilKey, 0f))
                    {
                        if (gone == null) gone = new List<ChefState>();
                        gone.Add(pair.Key);
                    }
                if (gone != null)
                    foreach (ChefState chef in gone)
                    {
                        Flag flag = s_flags[chef];
                        SpecialFx.Burst(flag.Position, SpecialFx.ColourOf(Special.Recall), 0.8f, 0.4f);
                        DropFlag(chef);
                    }
            }

            for (int i = s_overrides.Count - 1; i >= 0; i--)
                if (now > s_overrides[i].Until)
                {
                    RestoreSpawn(s_overrides[i]);
                    s_overrides.RemoveAt(i);
                }

            if (SpecialNet.IsServer && now >= s_nextSafeSample &&
                (SpecialRuntime.AnyHas(Special.SafetyNet) || SpecialRuntime.AnyHas(Special.Lifeguard) ||
                 SpecialRuntime.AnyHas(Special.QuickRespawn)))
            {
                s_nextSafeSample = now + 0.1f;
                SampleSafePoints(now);
            }
        }

        // --- Vault -----------------------------------------------------------------------------
        // Over whatever is in front, whatever its height (a counter, a crate, a wall, a tall unit):
        // look a little to either side of the facing too, ignore chefs and the items sitting on
        // counters, and land on the first free, safe floor in the kitchen beyond it.

        /// <summary>The nearest solid thing ahead, from knee to above head height, within <paramref name="reach"/>.</summary>
        private static bool Obstacle(Vector3 origin, Vector3 direction, float reach, out float distance)
        {
            distance = float.MaxValue;
            bool found = false;
            float[] heights = { 0.3f, 0.6f, 0.9f, 1.4f, 1.9f };
            for (int i = 0; i < heights.Length; i++)
            {
                RaycastHit hit;
                if (Warp.SolidRay(origin + Vector3.up * heights[i], direction, reach, Warp.SolidMask, out hit) && hit.distance < distance)
                {
                    distance = hit.distance;
                    found = true;
                }
            }
            return found;
        }

        private static bool FindVault(ChefState chef, Vector3 origin, Vector3 forward, out Vector3 landing, out string reason)
        {
            Warp.InitMasks();
            landing = origin;
            reason = Lang.Both("NOTHING TO VAULT OVER", "KHÔNG CÓ GÌ ĐỂ NHẢY QUA");
            forward = Warp.Flat(forward);
            var memo = new Dictionary<long, bool>();
            for (int a = 0; a < VaultAngles.Length; a++)
            {
                Vector3 direction = Quaternion.Euler(0f, VaultAngles[a], 0f) * forward;
                float near;
                if (!Obstacle(origin, direction, VaultReach, out near)) continue;
                reason = Lang.Both("NO FLOOR BEHIND IT", "PHÍA SAU KHÔNG CÓ SÀN");
                for (float d = near + 0.45f; d <= VaultRange + 0.01f; d += 0.15f)
                {
                    Vector3 floor;
                    if (!Warp.Floor(origin + direction * d, origin.y, 0.6f, out floor)) continue;
                    if (!Warp.Free(chef, floor, null) || Warp.Deadly(floor) || !Warp.InKitchen(floor)) continue;
                    // Not into a closed-in pocket the chef couldn't get out of.
                    if (!Warp.Roomy(chef, floor, origin.y, memo, 4, 1.2f)) continue;
                    landing = floor;
                    return true;
                }
            }
            return false;
        }

        private static bool VaultExecute(ChefState chef)
        {
            string why = Warp.Blocked(chef);
            if (why != null) return chef.Refuse(why);
            Vector3 from = chef.RequestPosition, landing;
            string reason;
            if (!FindVault(chef, from, chef.RequestForward, out landing, out reason)) return chef.Refuse(reason);
            Warp.Resolve(chef, Special.Vault, landing, 1.5f, delegate(Vector3 p) { return Warp.Landable(chef, p); },
                delegate(Vector3 p)
                {
                    Warp.ServerMove(chef, p, false, 0.9f);
                    SpecialFx.BeamAll(from, p, Special.Vault, 0.45f);
                    SpecialFx.BurstAll(p, Special.Vault);
                });
            return true;
        }

        /// <summary>Where a vault could land, each way round the chef (the way it faces first).</summary>
        private static List<AimTarget> VaultTargets(ChefState chef)
        {
            if (!Warp.Movable(chef)) return null;
            Vector3 from = chef.Position;
            return SpecialAim.Directions(chef, delegate(Vector3 direction)
            {
                Vector3 landing;
                string reason;
                return FindVault(chef, from, direction, out landing, out reason) ? (Vector3?)landing : null;
            });
        }

        private static void VaultPredict(ChefState chef)
        {
            Vector3 landing = chef.Position;
            string reason;
            bool found = Warp.Movable(chef) && FindVault(chef, chef.RequestPosition, chef.RequestForward, out landing, out reason);
            if (!found) landing = chef.Position;
            else Warp.PredictMove(chef, landing, false, 0.9f);
            Warp.Propose(chef, Special.Vault, found, landing);
        }

        // --- Blink -----------------------------------------------------------------------------
        // Straight through anything (counters, walls, crates, chefs): the farthest free floor up to
        // 6 m ahead at about the same height, never in a pit or water, never outside the kitchen and
        // never into a closed-in pocket.

        private static bool FindBlink(ChefState chef, Vector3 origin, Vector3 forward, out Vector3 landing)
        {
            string reason;
            return FindBlink(chef, origin, forward, out landing, out reason);
        }

        private static bool FindBlink(ChefState chef, Vector3 origin, Vector3 forward, out Vector3 landing, out string reason, bool log = true)
        {
            Warp.InitMasks();
            forward = Warp.Flat(forward);
            Vector3 right = new Vector3(forward.z, 0f, -forward.x);
            var memo = new Dictionary<long, bool>();
            // Why each spot tried was turned down, for the refusal and the log.
            int noFloor = 0, blocked = 0, deadly = 0, outside = 0, pocket = 0;
            reason = Lang.Both("NO FLOOR TO BLINK TO", "KHÔNG CÓ SÀN ĐỂ ĐÁP");
            for (float d = BlinkRange; d >= BlinkMinimum - 0.01f; d -= 0.25f)
            {
                for (int s = 0; s < 3; s++)
                {
                    float side = s == 0 ? 0f : s == 1 ? 0.35f : -0.35f;
                    Vector3 floor;
                    if (!Warp.Floor(origin + forward * d + right * side, origin.y, 0.5f, out floor)) { noFloor++; continue; }
                    if (!Warp.Free(chef, floor, null)) { blocked++; continue; }
                    if (Warp.Deadly(floor)) { deadly++; continue; }
                    if (!Warp.InKitchen(floor)) { outside++; continue; }
                    if (!Warp.Roomy(chef, floor, origin.y, memo, 6, 1.6f)) { pocket++; continue; }
                    landing = floor;
                    return true;
                }
            }
            landing = origin;
            if (log) Plugin.Log.LogInfo(string.Format(
                "BLINK: không có chỗ đáp (không có sàn {0}, bị chắn {1}, vùng chết {2}, ngoài bếp {3}, chỗ kẹt {4})",
                noFloor, blocked, deadly, outside, pocket));
            int most = Mathf.Max(Mathf.Max(blocked, deadly), Mathf.Max(outside, pocket));
            if (most > 0)
                reason = most == blocked ? Lang.Both("NO ROOM TO LAND", "KHÔNG CÓ CHỖ ĐÁP") : most == deadly ? Lang.Both("ONLY PITS AHEAD", "PHÍA TRƯỚC TOÀN HỐ")
                    : most == outside ? Lang.Both("EDGE OF THE KITCHEN", "MÉP BẾP RỒI") : Lang.Both("NO ROOM TO LAND", "KHÔNG CÓ CHỖ ĐÁP");
            return false;
        }

        private static bool BlinkExecute(ChefState chef)
        {
            string why = Warp.Blocked(chef);
            if (why != null) return chef.Refuse(why);
            Vector3 from = chef.RequestPosition, landing;
            string reason;
            if (!FindBlink(chef, from, chef.RequestForward, out landing, out reason)) return chef.Refuse(reason);
            Warp.Resolve(chef, Special.Blink, landing, 2.5f, delegate(Vector3 p) { return Warp.Landable(chef, p); },
                delegate(Vector3 p)
                {
                    Warp.ServerMove(chef, p, true, 0f);
                    SpecialFx.BeamAll(from, p, Special.Blink, 0.6f);
                    SpecialFx.BurstAll(from, Special.Blink);
                    SpecialFx.BurstAll(p, Special.Blink);
                });
            return true;
        }

        private static List<AimTarget> BlinkTargets(ChefState chef)
        {
            if (!Warp.Movable(chef)) return null;
            Vector3 from = chef.Position;
            return SpecialAim.Directions(chef, delegate(Vector3 direction)
            {
                Vector3 landing;
                string reason;
                return FindBlink(chef, from, direction, out landing, out reason, false) ? (Vector3?)landing : null;
            });
        }

        private static void BlinkPredict(ChefState chef)
        {
            Vector3 landing = chef.Position;
            bool found = Warp.Movable(chef) && FindBlink(chef, chef.RequestPosition, chef.RequestForward, out landing);
            if (!found) landing = chef.Position;
            else Warp.PredictMove(chef, landing, true, 0f);
            Warp.Propose(chef, Special.Blink, found, landing);
        }

        // --- Grapple ---------------------------------------------------------------------------

        /// <summary>The floor just in front of the counter the chef faces, up to 7 m away.</summary>
        private static bool FindGrapple(ChefState chef, Vector3 origin, Vector3 forward, out Vector3 landing,
            out Vector3 anchor, out string reason)
        {
            Warp.InitMasks();
            landing = anchor = origin;
            reason = Lang.Both("NO COUNTER WITHIN 7 M", "KHÔNG CÓ BÀN TRONG 7 M");
            forward = Warp.Flat(forward);
            for (int a = 0; a < GrappleAngles.Length; a++)
            {
                Vector3 direction = Quaternion.Euler(0f, GrappleAngles[a], 0f) * forward;
                RaycastHit hit = new RaycastHit();
                bool found = false;
                float[] heights = { 0.5f, 0.85f };
                for (int h = 0; h < heights.Length; h++)
                {
                    RaycastHit candidate;
                    if (Warp.SolidRay(origin + Vector3.up * heights[h], direction, GrappleRange, Warp.SolidMask, out candidate) &&
                        (!found || candidate.distance < hit.distance))
                    {
                        hit = candidate;
                        found = true;
                    }
                }
                if (!found || !Warp.IsCounter(hit.collider)) continue;
                if (hit.distance < 1.3f)
                {
                    reason = Lang.Both("ALREADY AT THE COUNTER", "ĐÃ Ở SÁT BÀN");
                    continue;
                }
                // Back off from the counter until there's floor to stand on (gaps in between are flown over).
                for (float d = hit.distance - 0.45f; d >= 0.9f; d -= 0.15f)
                {
                    Vector3 floor;
                    if (Warp.Floor(origin + direction * d, origin.y, 1f, out floor) && Warp.Free(chef, floor, null) &&
                        !Warp.Deadly(floor) && Warp.InKitchen(floor))
                    {
                        landing = floor;
                        anchor = hit.point;
                        return true;
                    }
                }
                reason = Lang.Both("NO FLOOR BY THE COUNTER", "CẠNH BÀN KHÔNG CÓ SÀN");
            }
            return false;
        }

        private static bool GrappleExecute(ChefState chef)
        {
            string why = Warp.Blocked(chef);
            if (why != null) return chef.Refuse(why);
            Vector3 from = chef.RequestPosition, landing, anchor;
            string reason;
            if (!FindGrapple(chef, from, chef.RequestForward, out landing, out anchor, out reason)) return chef.Refuse(reason);
            Warp.Resolve(chef, Special.Grapple, landing, 1.5f, delegate(Vector3 p) { return Warp.Landable(chef, p); },
                delegate(Vector3 p)
                {
                    Warp.ServerMove(chef, p, false, 0.35f);
                    SpecialFx.BeamAll(from + Vector3.up * 0.9f, anchor, Special.Grapple, 0.5f);
                    SpecialFx.BurstAll(p, Special.Grapple);
                });
            return true;
        }

        private static List<AimTarget> GrappleTargets(ChefState chef)
        {
            if (!Warp.Movable(chef)) return null;
            Vector3 from = chef.Position;
            return SpecialAim.Directions(chef, delegate(Vector3 direction)
            {
                Vector3 landing, anchor;
                string reason;
                return FindGrapple(chef, from, direction, out landing, out anchor, out reason) ? (Vector3?)landing : null;
            });
        }

        private static void GrapplePredict(ChefState chef)
        {
            Vector3 landing = chef.Position, anchor;
            string reason;
            bool found = Warp.Movable(chef) && FindGrapple(chef, chef.RequestPosition, chef.RequestForward, out landing, out anchor, out reason);
            if (!found) landing = chef.Position;
            else Warp.PredictMove(chef, landing, false, 0.35f);
            Warp.Propose(chef, Special.Grapple, found, landing);
        }

        // --- Recall ----------------------------------------------------------------------------
        // The host decides plant or return and tells everyone first (TopicRecall), so that when the
        // activation is confirmed every machine knows which it was: a plant puts the cooldown back
        // to 0.5 s (the button shows the flag's time running out instead), so the real cooldown
        // only starts with the return. The flag sticks to the floor it was planted on (rafts,
        // moving platforms) and is marked with a ring on every machine.

        private static bool HasFlag(ChefState chef, float spare)
        {
            return s_flags.ContainsKey(chef) && chef.Get(FlagUntilKey, 0f) > SpecialRuntime.Now + spare;
        }

        private static Vector3 FlagPosition(ChefState chef)
        {
            Flag flag;
            return s_flags.TryGetValue(chef, out flag) ? flag.Position : chef.Get(FlagKey, chef.Position);
        }

        /// <summary>Where a return to the flag puts the chef: the flag, or next to it if that's taken.</summary>
        private static bool ReturnSpot(ChefState chef, Vector3 forward, out Vector3 spot)
        {
            Vector3 flag = FlagPosition(chef), floor;
            if (Warp.Floor(flag, flag.y, 0.5f, out floor) && Warp.Free(chef, floor, null) && !Warp.Deadly(floor))
            {
                spot = floor;
                return true;
            }
            return Warp.SpotNear(flag, forward, chef, out spot);
        }

        private static bool RecallExecute(ChefState chef)
        {
            string why = Warp.Blocked(chef);
            if (why != null) return chef.Refuse(why);
            if (HasFlag(chef, 0f))
            {
                Vector3 spot;
                if (!ReturnSpot(chef, chef.RequestForward, out spot)) return chef.Refuse(Lang.Both("FLAG SPOT IS BLOCKED", "CHỖ CẮM CỜ BỊ CHẶN"));
                SendRecall(chef, RecallReturned, spot, chef.RequestPosition);
                Warp.ServerMove(chef, spot, false, 0f);
                return true;
            }
            // The owner's own position at the press: no need to wait for its proposal.
            Vector3 here = chef.RequestPosition, ground;
            if (!Warp.Floor(here, here.y, 0.4f, out ground) || Warp.Deadly(ground)) return chef.Refuse(Lang.Both("NOT ON SOLID FLOOR", "KHÔNG ĐỨNG TRÊN SÀN VỮNG"));
            SendRecall(chef, RecallPlanted, ground, ground);
            return true;
        }

        private static void RecallPredict(ChefState chef)
        {
            // Only predict a return that the host will surely still see as one.
            if (!HasFlag(chef, 0.4f) || !Warp.Movable(chef)) return;
            Vector3 spot;
            if (ReturnSpot(chef, chef.RequestForward, out spot)) Warp.PredictMove(chef, spot, false, 0f);
        }

        private static void SendRecall(ChefState chef, int what, Vector3 position, Vector3 from)
        {
            if (chef.Id == 0)
            {
                // Offline without an entity ID: just this machine.
                ApplyRecall(chef, what, position, from);
                return;
            }
            SpecialNet.SendToAll(new SpecialMessage { Topic = TopicRecall, Chef = chef.Id, I = what, Vector1 = position, Vector2 = from });
        }

        private static void OnRecallMessage(SpecialMessage message)
        {
            ChefState chef = SpecialRuntime.StateById(message.Chef);
            if (chef != null) ApplyRecall(chef, message.I, message.Vector1, message.Vector2);
        }

        private static void ApplyRecall(ChefState chef, int what, Vector3 position, Vector3 from)
        {
            Color colour = SpecialFx.ColourOf(Special.Recall);
            switch (what)
            {
                case RecallPlanted:
                {
                    DropFlag(chef);
                    var flag = new Flag { Local = position };
                    // Stick to the floor it is on, so it rides along on a moving platform.
                    RaycastHit hit;
                    Warp.InitMasks();
                    if (Physics.Raycast(position + Vector3.up * 0.5f, Vector3.down, out hit, 1.2f, Warp.GroundMask, QueryTriggerInteraction.Ignore))
                    {
                        flag.Parent = hit.collider.transform;
                        flag.Local = flag.Parent.InverseTransformPoint(position);
                    }
                    flag.Marker = new GameObject("ChefSkills_RecallFlag");
                    if (flag.Parent != null) flag.Marker.transform.SetParent(flag.Parent, false);
                    flag.Marker.transform.position = position;
                    s_flags[chef] = flag;
                    chef.Set(FlagKey, position);
                    chef.Set(FlagUntilKey, SpecialRuntime.Now + RecallSeconds);
                    chef.Set(FlagModeKey, RecallPlanted);
                    SpecialFx.Mark(flag.Marker, colour, RecallSeconds, Lang.T("FLAG", "CỜ"), 0.55f);
                    SpecialFx.Burst(position, colour, 1f, 0.45f);
                    break;
                }
                case RecallReturned:
                    DropFlag(chef);
                    chef.Set(FlagModeKey, RecallReturned);
                    SpecialFx.Beam(from, position, colour, 0.55f);
                    SpecialFx.Burst(from, colour, 1.2f, 0.5f);
                    SpecialFx.Burst(position, colour, 1.4f, 0.55f);
                    break;
            }
        }

        private static void DropFlag(ChefState chef)
        {
            Flag flag;
            if (s_flags.TryGetValue(chef, out flag))
            {
                s_flags.Remove(chef);
                if (flag.Marker != null)
                {
                    SpecialFx.Unmark(flag.Marker);
                    UnityEngine.Object.Destroy(flag.Marker);
                }
            }
            chef.Data.Remove(FlagKey);
            chef.Data.Remove(FlagUntilKey);
        }

        private static void RecallActivated(ChefState chef)
        {
            if (chef.Get(FlagModeKey, 0) != RecallPlanted) return;
            // Planting is free: only a short pause so a double tap doesn't return at once. The
            // button shows the flag's time running out instead (its "effect").
            float now = SpecialRuntime.Now;
            ActiveTimer timing = chef.TimingOf(Special.Recall);
            if (timing == null) return;
            timing.CooldownUntil = now + 0.5f;
            timing.CooldownTotal = 0.5f;
            timing.ActiveUntil = Mathf.Max(now + 0.5f, chef.Get(FlagUntilKey, now + RecallSeconds));
            chef.Set(FlagModeKey, 0);
        }

        // --- Swap and Summon -------------------------------------------------------------------
        // Nobody is moved without warning. While the caster holds the skill key, the teammate they
        // have chosen is ringed with "SWAP?" on every screen. Once used, both chefs stand still for
        // a moment while a beam joins them and "SWAP!" shows over the teammate (the wind-up); then
        // they fly to their new places in an arc, leaving rings behind, and land with a burst and
        // "SWAPPED!" over the moved teammate. The host checks again before the flight, so a
        // teammate who fell or got busy meanwhile isn't moved.

        private const float WindUpSeconds = 0.25f;
        private const float FlightSeconds = 0.3f;
        private const float FlightHeight = 1.1f;

        private sealed class Transfer
        {
            public ChefState Caster, Mate;
            public Special Special;
            public Vector3 Forward;
            public float At;
        }

        // Host: swaps and summons in their wind-up.
        private static readonly List<Transfer> s_transfers = new List<Transfer>();

        /// <summary>Teammates to swap with or summon, the one faced (or the nearest) first.</summary>
        private static List<AimTarget> MateTargets(ChefState chef)
        {
            var targets = new List<AimTarget>();
            // Only what this machine knows for sure about another player's chef (the host checks the rest).
            foreach (ChefState other in SpecialRuntime.Teammates(chef))
                if (other.Chef != null && other.Chef.activeInHierarchy && other.Controls != null && !other.Controls.m_bRespawning &&
                    !other.Has(Special.LoneWolf))
                    targets.Add(AimTarget.ForChef(other));
            return SpecialAim.FacedFirst(targets, chef.Position, chef.Forward, 35f);
        }

        private static bool CanBeMoved(ChefState mate)
        {
            if (mate == null || mate.Chef == null || mate.Controls == null || !mate.Chef.activeInHierarchy) return false;
            // Lone Wolf: teammates' specials don't move them (the host decides, whatever was aimed at).
            if (mate.Has(Special.LoneWolf)) return false;
            if (mate.Controls.m_bRespawning || !mate.Controls.enabled) return false;
            if (SpecialNet.IsServer)
            {
                Rigidbody body = mate.Chef.GetComponent<Rigidbody>();
                if (body == null || body.isKinematic) return false;
            }
            return true;
        }

        /// <summary>
        /// Host: the teammate to swap with or summon, or null. <paramref name="refusal"/> then says
        /// why (null: no teammate at all).
        /// </summary>
        private static ChefState PickTeammate(ChefState chef, Vector3 position, Vector3 forward, out string refusal)
        {
            ChefState faced = null, nearest = null;
            float facedDistance = float.MaxValue, nearestDistance = float.MaxValue;
            forward = Warp.Flat(forward);
            refusal = Lang.Both("NO TEAMMATE", "KHÔNG CÓ ĐỒNG ĐỘI");
            string cantMove = Lang.Both("TEAMMATE CAN'T MOVE NOW", "ĐỒNG ĐỘI CHƯA ĐI ĐƯỢC");
            string lonely = SpecialFx.NameBoth(Special.LoneWolf, " IGNORES IT", " PHỚT LỜ");
            // The teammate the player chose, if they did.
            if (chef.HasAimPoint && chef.AimChef != null)
            {
                ChefState chosen = chef.AimChef;
                refusal = chosen.Has(Special.LoneWolf) ? lonely : cantMove;
                return chosen != chef && chosen.Team == chef.Team && CanBeMoved(chosen) ? chosen : null;
            }
            bool onlyLoners = true;
            foreach (ChefState other in SpecialRuntime.Teammates(chef))
            {
                if (other.Chef == null) continue;
                if (!other.Has(Special.LoneWolf)) onlyLoners = false;
                refusal = onlyLoners ? lonely : cantMove;
                if (!CanBeMoved(other)) continue;
                Vector3 offset = other.Position - position;
                offset.y = 0f;
                float distance = offset.magnitude;
                if (distance < nearestDistance)
                {
                    nearest = other;
                    nearestDistance = distance;
                }
                if (distance > 0.01f && Vector3.Angle(forward, offset) <= 35f && distance < facedDistance)
                {
                    faced = other;
                    facedDistance = distance;
                }
            }
            return faced ?? nearest;
        }

        private static bool SwapExecute(ChefState chef)
        {
            string why = Warp.Blocked(chef);
            if (why != null) return chef.Refuse(why);
            string refusal;
            ChefState mate = PickTeammate(chef, chef.RequestPosition, chef.RequestForward, out refusal);
            if (mate == null) return chef.Refuse(refusal);
            Vector3 mine = chef.RequestPosition, theirs = mate.Position;
            if (Warp.FlatDistance(mine, theirs) < 0.6f) return chef.Refuse(Lang.Both("TEAMMATE TOO CLOSE", "ĐỒNG ĐỘI Ở QUÁ GẦN"));
            WindUp(chef, mate, Special.Swap);
            return true;
        }

        /// <summary>Host: the warning before a swap or summon (both chefs stand still, a beam joins them).</summary>
        private static void WindUp(ChefState chef, ChefState mate, Special special)
        {
            float still = WindUpSeconds + FlightSeconds + 0.1f;
            // From nobody, so Lone Wolf doesn't shrug it off: it is the move itself.
            SpecialRuntime.BroadcastBuff(chef, BuffId.Frozen, still, 1f, null);
            SpecialRuntime.BroadcastBuff(mate, BuffId.Frozen, still, 1f, null);
            SpecialFx.BeamAll(chef.RequestPosition, mate.Position, special, WindUpSeconds + 0.1f);
            SpecialFx.MarkAll(mate.Chef, special, WindUpSeconds + 0.15f, SpecialFx.NameBoth(special, "!", "!"));
            if (special == Special.Swap) SpecialFx.MarkAll(chef.Chef, special, WindUpSeconds + 0.15f, null);
            for (int i = s_transfers.Count - 1; i >= 0; i--)
                if (s_transfers[i].Caster == chef || s_transfers[i].Mate == mate) s_transfers.RemoveAt(i);
            s_transfers.Add(new Transfer
            {
                Caster = chef, Mate = mate, Special = special, Forward = chef.RequestForward,
                At = SpecialRuntime.Now + WindUpSeconds
            });
        }

        /// <summary>Host, every frame: swaps and summons whose wind-up is over fly.</summary>
        private static void RunTransfers(float now)
        {
            for (int i = s_transfers.Count - 1; i >= 0; i--)
            {
                Transfer transfer = s_transfers[i];
                if (now < transfer.At) continue;
                s_transfers.RemoveAt(i);
                try
                {
                    Fly(transfer);
                }
                catch (Exception error)
                {
                    SpecialRuntime.Fail(transfer.Special + ".Fly", error);
                }
            }
        }

        private static void Fly(Transfer transfer)
        {
            ChefState chef = transfer.Caster, mate = transfer.Mate;
            Special special = transfer.Special;
            if (chef.Chef == null || mate.Chef == null) return;
            if (Warp.Blocked(chef) != null || !CanBeMoved(mate))
            {
                SpecialFx.TextAll(chef.Chef, Lang.Both("MISSED!", "TRƯỢT RỒI!"), special);
                return;
            }
            if (special == Special.Swap)
            {
                Vector3 mine = chef.Position, theirs = mate.Position;
                Warp.ServerFlight(chef, theirs, special, FlightSeconds, FlightHeight);
                Warp.ServerFlight(mate, mine, special, FlightSeconds, FlightHeight);
                SpecialFx.BeamAll(mine, theirs, special, FlightSeconds + 0.2f);
                Landed(mate, mine, Lang.Both("SWAPPED!", "ĐÃ ĐỔI CHỖ!"), special);
                return;
            }
            Vector3 from = mate.Position, spot;
            if (!Warp.SpotNear(chef.Position, transfer.Forward, mate, out spot))
            {
                SpecialFx.TextAll(chef.Chef, Lang.Both("NO ROOM NEXT TO YOU", "CẠNH BẠN HẾT CHỖ"), special);
                return;
            }
            Warp.ServerFlight(mate, spot, special, FlightSeconds, FlightHeight);
            SpecialFx.BeamAll(from, spot, special, FlightSeconds + 0.2f);
            Landed(mate, spot, Lang.Both("SUMMONED!", "ĐÃ TRIỆU HỒI!"), special);
        }

        /// <summary>Host: once the moved teammate lands, a ring under them and a word over them.</summary>
        private static void Landed(ChefState mate, Vector3 spot, string word, Special special)
        {
            s_landings.Add(new Landing { Mate = mate, Spot = spot, Word = word, Special = special, At = SpecialRuntime.Now + FlightSeconds });
        }

        private sealed class Landing
        {
            public ChefState Mate;
            public Vector3 Spot;
            public string Word;
            public Special Special;
            public float At;
        }

        private static readonly List<Landing> s_landings = new List<Landing>();

        private static void RunLandings(float now)
        {
            for (int i = s_landings.Count - 1; i >= 0; i--)
            {
                Landing landing = s_landings[i];
                if (now < landing.At) continue;
                s_landings.RemoveAt(i);
                SpecialFx.TextAllAt(landing.Spot + Vector3.up * 2.1f, landing.Word, landing.Special);
                if (landing.Mate.Chef != null) SpecialFx.MarkAll(landing.Mate.Chef, landing.Special, 1.2f, null);
            }
        }

        private static bool SummonExecute(ChefState chef)
        {
            string why = Warp.Blocked(chef);
            if (why != null) return chef.Refuse(why);
            string refusal;
            ChefState mate = PickTeammate(chef, chef.RequestPosition, chef.RequestForward, out refusal);
            if (mate == null) return chef.Refuse(refusal);
            Vector3 from = mate.Position;
            if (Warp.FlatDistance(from, chef.RequestPosition) < 1.6f) return chef.Refuse(Lang.Both("TEAMMATE ALREADY HERE", "ĐỒNG ĐỘI ĐÃ Ở ĐÂY"));
            Vector3 spot;
            if (!Warp.SpotNear(chef.RequestPosition, chef.RequestForward, mate, out spot)) return chef.Refuse(Lang.Both("NO ROOM NEXT TO YOU", "CẠNH BẠN HẾT CHỖ"));
            WindUp(chef, mate, Special.Summon);
            return true;
        }

        // --- Safety Net and Lifeguard (host) ----------------------------------------------------

        private static void SampleSafePoints(float now)
        {
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState chef = chefs[i];
                if (chef.Controls == null || chef.Chef == null) continue;
                PlayerControls controls = chef.Controls;
                if (!controls.enabled || controls.m_bRespawning) continue;
                Collider ground = controls.GroundCollider;
                if (ground == null) continue;
                Vector3 position = chef.Position;
                SafePoint latest = chef.Get(LatestKey, new SafePoint());
                // Keep the point from a moment ago: the latest one may be right at the edge.
                if (latest.Set && now - latest.Time >= 0.3f) chef.Set(SafeKey, latest);
                if (!latest.Set || now - latest.Time >= 0.3f)
                {
                    Transform parent = ground.transform;
                    var point = new SafePoint
                    {
                        Parent = parent, Local = parent.InverseTransformPoint(position), World = position, Time = now, Set = true
                    };
                    chef.Set(LatestKey, point);
                    if (!chef.Data.ContainsKey(SafeKey)) chef.Set(SafeKey, point);
                }
            }
        }

        /// <summary>Firm footing: floor under the spot and a little way around it, nothing in the way.</summary>
        private static bool Firm(ChefState chef, Vector3 p, out Vector3 floor)
        {
            if (!Warp.Floor(p, p.y, 0.4f, out floor)) return false;
            Vector3 around;
            if (!Warp.Floor(floor + new Vector3(0.3f, 0f, 0f), floor.y, 0.3f, out around) ||
                !Warp.Floor(floor + new Vector3(-0.3f, 0f, 0f), floor.y, 0.3f, out around) ||
                !Warp.Floor(floor + new Vector3(0f, 0f, 0.3f), floor.y, 0.3f, out around) ||
                !Warp.Floor(floor + new Vector3(0f, 0f, -0.3f), floor.y, 0.3f, out around))
                return false;
            return Warp.Free(chef, floor, null) && !Warp.Deadly(floor);
        }

        /// <summary>Where to put a falling chef back: where it last stood firmly, near where it stepped off.</summary>
        private static bool SafeSpot(ChefState chef, out Vector3 spot)
        {
            spot = chef.Position;
            SafePoint latest = chef.Get(LatestKey, new SafePoint());
            SafePoint safe = chef.Get(SafeKey, new SafePoint());
            if (!latest.Set && !safe.Set) return false;
            Vector3 a = latest.Set ? latest.Position : safe.Position;
            Vector3 b = safe.Set ? safe.Position : a;
            // From the latest point back towards the older one.
            for (int i = 0; i <= 4; i++)
                if (Firm(chef, Vector3.Lerp(a, b, i / 4f), out spot)) return true;
            // Nothing firm all round (a narrow bridge): any floor, from the older point forwards.
            Vector3 floor;
            for (int i = 4; i >= 0; i--)
            {
                Vector3 p = Vector3.Lerp(a, b, i / 4f);
                if (Warp.Floor(p, p.y, 0.4f, out floor) && Warp.Free(chef, floor, null) && !Warp.Deadly(floor))
                {
                    spot = floor;
                    return true;
                }
            }
            if (Warp.Floor(b, b.y, 0.5f, out floor) && Warp.SpotNear(floor, Vector3.forward, chef, out spot)) return true;
            return false;
        }

        private static ChefState FindLifeguard(ChefState chef, Vector3 back)
        {
            ChefState best = null;
            float bestDistance = float.MaxValue;
            // Lone Wolf: no teammate's rescue.
            if (chef.Has(Special.LoneWolf)) return null;
            foreach (ChefState mate in SpecialRuntime.Teammates(chef))
            {
                if (!mate.Has(Special.Lifeguard) || mate.Chef == null || mate.Controls == null || mate.Controls.m_bRespawning) continue;
                float distance = Mathf.Min(Warp.FlatDistance(mate.Position, chef.Position), Warp.FlatDistance(mate.Position, back));
                if (distance <= LifeguardRange && distance < bestDistance)
                {
                    best = mate;
                    bestDistance = distance;
                }
            }
            return best;
        }

        private static readonly FieldInfo RespawnColliderField =
            AccessTools.Field(typeof(ServerRespawnCollider), "m_RespawnCollider");

        /// <summary>Host: a chef fell into a pit or water. True if Safety Net or a Lifeguard caught it.</summary>
        internal static bool TryCatchFall(ServerRespawnCollider volume, GameObject fallen)
        {
            PlayerControls controls = fallen.GetComponent<PlayerControls>();
            if (controls == null || controls.m_bRespawning) return false;
            RespawnCollider.RespawnType type = volume.Type;
            if (type != RespawnCollider.RespawnType.FallDeath && type != RespawnCollider.RespawnType.Drowning) return false;
            RespawnCollider settings = RespawnColliderField != null ? RespawnColliderField.GetValue(volume) as RespawnCollider : null;
            if (settings == null || (settings.m_respawnFilter.value & (1 << fallen.layer)) == 0) return false;
            ChefState chef = SpecialRuntime.StateOf(fallen);
            if (chef == null) return false;
            float now = SpecialRuntime.Now;
            float caught = chef.Get(CaughtKey, -100f);
            // The same fall touching a second volume in the same moment: already caught.
            if (now - caught < 0.25f) return true;
            // Never twice in a row: a spot that drops the chef again lets it respawn normally.
            if (now - caught < 2f) return false;

            bool net = chef.Has(Special.SafetyNet) && now >= chef.Get(NetReadyKey, 0f);
            bool lifeguardNear = false;
            if (!net)
            {
                if (chef.Has(Special.LoneWolf)) return false;
                foreach (ChefState mate in SpecialRuntime.Teammates(chef))
                    if (mate.Has(Special.Lifeguard))
                    {
                        lifeguardNear = true;
                        break;
                    }
                if (!lifeguardNear) return false;
            }
            Vector3 back;
            if (!SafeSpot(chef, out back)) return false;
            ChefState guard = net ? null : FindLifeguard(chef, back);
            if (!net && guard == null) return false;

            Vector3 fellAt = chef.Position;
            chef.Set(CaughtKey, now);
            // Flown back up from where it fell, so everyone (the chef too) sees where it went; it
            // can't run off while it is in the air.
            SpecialRuntime.BroadcastBuff(chef, BuffId.Frozen, RescueFlightSeconds + 0.1f, 1f, null);
            Warp.ServerFlight(chef, back, net ? Special.SafetyNet : Special.Lifeguard, RescueFlightSeconds, 1.4f);
            if (net)
            {
                chef.Set(NetReadyKey, now + SafetyNetCooldown);
                chef.SetTimer(Special.SafetyNet, now + SafetyNetCooldown, SafetyNetCooldown);
                if (chef.Id != 0)
                    SpecialNet.SendToAll(new SpecialMessage { Topic = TopicSafetyNet, Chef = chef.Id, A = SafetyNetCooldown });
                SpecialFx.BeamAll(fellAt, back, Special.SafetyNet, 0.5f);
                SpecialFx.BurstAll(back, Special.SafetyNet);
                SpecialFx.TextAllAt(back + Vector3.up * 2.1f, Lang.Both("SAVED!", "CỨU KỊP!"), Special.SafetyNet);
            }
            else
            {
                SpecialFx.BeamAll(guard.Position, back, Special.Lifeguard, 0.6f);
                SpecialFx.BurstAll(back, Special.Lifeguard);
                SpecialFx.MarkAll(guard.Chef, Special.Lifeguard, 1.2f, SpecialFx.NameBoth(Special.Lifeguard, "", ""));
                SpecialFx.TextAllAt(back + Vector3.up * 2.1f, Lang.Both("SAVED!", "CỨU KỊP!"), Special.Lifeguard);
            }
            return true;
        }

        private static void OnSafetyNetMessage(SpecialMessage message)
        {
            ChefState chef = SpecialRuntime.StateById(message.Chef);
            if (chef == null || SpecialNet.IsServer) return;
            chef.Set(NetReadyKey, SpecialRuntime.Now + message.A);
            chef.SetTimer(Special.SafetyNet, SpecialRuntime.Now + message.A, SafetyNetCooldown);
        }

        // --- respawn time and place (every machine runs the respawn itself) ---------------------
        // Each machine runs a chef's respawn on its own, but a client only lets the chef back in
        // once the host has respawned it (the game waits for the host's resume), and the host's
        // place wins. So the host decides. As the chef falls, before the game takes what it holds,
        // the host tells everyone whether it carried anything (TopicFell): Medic, Sticky Fingers
        // and Quick Respawn depend on it. A machine that hasn't heard yet when the game's respawn
        // starts takes the shortest time the respawn could have, so it never keeps the chef away
        // longer than the host does, and follows the place once the word arrives.

        private static readonly FieldInfo BehaviourField =
            AccessTools.Field(typeof(ClientPlayerRespawnBehaviour), "m_PlayerRespawnBehaviour");

        // Every machine: respawns that started before the host's word on the fall arrived
        // (respawn behaviour instance ID -> when the respawn ends).
        private static readonly Dictionary<int, float> s_unsure = new Dictionary<int, float>();

        // How old the host's word on a fall may be when the game's respawn starts.
        private const float FallFreshSeconds = 1.5f;

        internal static bool AnyRespawnSpecial()
        {
            return SpecialRuntime.AnyHas(Special.QuickRespawn) || SpecialRuntime.AnyHas(Special.Medic) ||
                SpecialRuntime.AnyHas(Special.Fragile) || SpecialRuntime.AnyHas(Special.StickyFingers) || s_baseRespawn.Count > 0;
        }

        internal static bool AnySpawnPending()
        {
            return s_medicFor.Count > 0 || s_quickFor.Count > 0;
        }

        /// <summary>Does any respawn rule depend on how a chef fell?</summary>
        internal static bool AnyFallSpecial()
        {
            return SpecialRuntime.AnyHas(Special.QuickRespawn) || SpecialRuntime.AnyHas(Special.Medic) ||
                SpecialRuntime.AnyHas(Special.StickyFingers);
        }

        /// <summary>Host: something in the chef's hands (not counting Pocket or a Pack Mule back).</summary>
        private static bool HoldsInHands(ChefState chef)
        {
            ServerPlayerAttachmentCarrier hands = chef.Chef != null ? chef.Chef.GetComponent<ServerPlayerAttachmentCarrier>() : null;
            return hands != null && hands.InspectCarriedItem() != null;
        }

        /// <summary>
        /// Host: a chef starts respawning, before the game takes what it holds. Tells every machine
        /// what it carried and, for Quick Respawn, the last spot it stood on safely.
        /// </summary>
        internal static void OnFell(GameObject fallen)
        {
            if (!SpecialNet.IsServer) return;
            ChefState chef = SpecialRuntime.StateOf(fallen);
            if (chef == null) return;
            bool holding = HoldsInHands(chef);
            bool carrying = holding || CarryingModule.CarriesAnything(chef);
            Vector3 spot = Vector3.zero;
            bool hasSpot = chef.Has(Special.QuickRespawn) && SafeSpot(chef, out spot);
            if (chef.Id == 0)
            {
                // Offline without an entity ID: just this machine.
                RememberFall(chef, new Fall { Carrying = carrying, Holding = holding, HasSpot = hasSpot, Spot = spot });
                return;
            }
            SpecialNet.SendToAll(new SpecialMessage
            {
                Topic = TopicFell, Chef = chef.Id, Vector1 = spot,
                I = (carrying ? 1 : 0) | (hasSpot ? 2 : 0) | (holding ? 4 : 0)
            });
        }

        private static void OnFellMessage(SpecialMessage message)
        {
            ChefState chef = SpecialRuntime.StateById(message.Chef);
            if (chef == null) return;
            RememberFall(chef, new Fall
            {
                Carrying = (message.I & 1) != 0, HasSpot = (message.I & 2) != 0, Holding = (message.I & 4) != 0, Spot = message.Vector1
            });
        }

        private static void RememberFall(ChefState chef, Fall fall)
        {
            fall.At = SpecialRuntime.Now;
            if (chef.Chef == null || chef.Controls == null || !chef.Controls.m_bRespawning)
            {
                // Before the game's respawn starts here (the usual order): it reads this.
                s_falls[chef] = fall;
                return;
            }
            // The game's respawn started first: the time is already set (the shortest it could
            // be), but the chef can still come back in the right place.
            PlayerRespawnBehaviour behaviour = chef.Chef.GetComponent<PlayerRespawnBehaviour>();
            float until;
            if (behaviour == null || !s_unsure.TryGetValue(behaviour.GetInstanceID(), out until)) return;
            s_unsure.Remove(behaviour.GetInstanceID());
            PlaceRespawn(chef, behaviour, fall, Mathf.Max(0f, until - SpecialRuntime.Now));
        }

        /// <summary>
        /// How long a respawn takes: the level's own time, shortened by the specials that apply
        /// (the shortest wins), then Fragile's 5 s on top.
        /// </summary>
        private static float RespawnTime(ChefState chef, float baseTime, bool carrying, bool holding)
        {
            float time = baseTime;
            if (chef.Has(Special.QuickRespawn)) time = Mathf.Min(time, QuickRespawnTime);
            // A Medic only brings back teammates who fell empty-handed.
            if (!carrying && FindMedic(chef) != null) time = Mathf.Min(time, MedicRespawnTime);
            if (chef.Has(Special.Medic)) time = Mathf.Min(time, MedicOwnRespawnTime);
            if (holding && chef.Has(Special.StickyFingers)) time = Mathf.Min(time, StickyRespawnTime);
            if (chef.Has(Special.Fragile)) time += FragileExtraTime;
            return time;
        }

        /// <summary>A chef starts respawning on this machine: set how long it takes.</summary>
        internal static void OnRespawnBegin(ClientPlayerRespawnBehaviour client)
        {
            PlayerRespawnBehaviour behaviour = BehaviourField != null ? BehaviourField.GetValue(client) as PlayerRespawnBehaviour : null;
            if (behaviour == null) return;
            int key = behaviour.GetInstanceID();
            for (int i = s_overrides.Count - 1; i >= 0; i--)
                if (s_overrides[i].Behaviour == behaviour)
                {
                    RestoreSpawn(s_overrides[i]);
                    s_overrides.RemoveAt(i);
                }
            float baseTime;
            if (!s_baseRespawn.TryGetValue(key, out baseTime))
            {
                baseTime = behaviour.m_respawnTime;
                s_baseRespawn[key] = baseTime;
            }
            s_medicFor.Remove(key);
            s_quickFor.Remove(key);
            s_unsure.Remove(key);
            float time = baseTime;
            ChefState chef = SpecialRuntime.StateOf(client.gameObject);
            if (chef != null)
            {
                float now = SpecialRuntime.Now;
                Fall fall;
                if (s_falls.TryGetValue(chef, out fall)) s_falls.Remove(chef);
                if (fall != null && now - fall.At > FallFreshSeconds) fall = null;
                // The host always knows (it told everyone just before); this is only a fallback.
                if (fall == null && SpecialNet.IsServer)
                {
                    bool holding = HoldsInHands(chef);
                    fall = new Fall { Carrying = holding || CarryingModule.CarriesAnything(chef), Holding = holding, At = now };
                }
                if (fall != null) time = RespawnTime(chef, baseTime, fall.Carrying, fall.Holding);
                else
                {
                    time = Mathf.Min(RespawnTime(chef, baseTime, false, false),
                        Mathf.Min(RespawnTime(chef, baseTime, true, false), RespawnTime(chef, baseTime, true, true)));
                    s_unsure[key] = now + time;
                }
                if (chef.Has(Special.Fragile))
                {
                    chef.SetTimer(Special.Fragile, now + time, time);
                    SpecialFx.Text(chef.Chef, SpecialCatalog.NameOf(Special.Fragile), SpecialFx.ColourOf(Special.Fragile));
                }
                if (chef.Has(Special.QuickRespawn)) chef.SetTimer(Special.QuickRespawn, now + time, time);
                if (fall != null) PlaceRespawn(chef, behaviour, fall, time);
            }
            behaviour.m_respawnTime = time;
        }

        /// <summary>Where the chef will come back, once this machine knows how it fell.</summary>
        private static void PlaceRespawn(ChefState chef, PlayerRespawnBehaviour behaviour, Fall fall, float time)
        {
            int key = behaviour.GetInstanceID();
            ChefState medic = fall.Carrying ? null : FindMedic(chef);
            if (medic != null)
            {
                s_medicFor[key] = medic;
                // Everyone sees where the chef will come back.
                SpecialFx.Mark(medic.Chef, Special.Medic, time + 0.4f, SpecialCatalog.NameOf(Special.Medic));
                medic.SetTimer(Special.Medic, SpecialRuntime.Now + time, time);
            }
            if (!chef.Has(Special.QuickRespawn)) return;
            s_quickFor[key] = Special.QuickRespawn;
            // The countdown shows over the safe spot; the host looks for it again as the chef comes back.
            if (medic == null && fall.HasSpot) MoveSpawn(behaviour, fall.Spot, time + 4f);
        }

        private static ChefState FindMedic(ChefState chef)
        {
            // Lone Wolf: no teammate's rescue.
            if (chef.Has(Special.LoneWolf)) return null;
            ChefState best = null;
            foreach (ChefState mate in SpecialRuntime.Teammates(chef))
            {
                if (!mate.Has(Special.Medic) || mate.Chef == null || mate.Controls == null || mate.Controls.m_bRespawning) continue;
                // The same pick on every machine.
                if (best == null || mate.Id < best.Id) best = mate;
            }
            return best;
        }

        /// <summary>
        /// A chef is about to reappear on this machine: next to its medic if it has one, else at
        /// its last safe spot for Quick Respawn.
        /// </summary>
        internal static void OnSpawn(ClientPlayerRespawnBehaviour client)
        {
            PlayerRespawnBehaviour behaviour = BehaviourField != null ? BehaviourField.GetValue(client) as PlayerRespawnBehaviour : null;
            if (behaviour == null) return;
            int key = behaviour.GetInstanceID();
            s_unsure.Remove(key);
            bool quick = s_quickFor.ContainsKey(key);
            s_quickFor.Remove(key);
            ChefState chef = SpecialRuntime.StateOf(client.gameObject);
            ChefState medic;
            if (s_medicFor.TryGetValue(key, out medic))
            {
                s_medicFor.Remove(key);
                Vector3 spot;
                // World axes rather than the medic's facing, so every machine picks the same spot.
                if (medic != null && medic.Chef != null && medic.Controls != null && !medic.Controls.m_bRespawning &&
                    Warp.SpotNear(medic.Position, Vector3.forward, chef, out spot))
                {
                    // The spawn routine reads it right after this; put back a few seconds later.
                    MoveSpawn(behaviour, spot, 4f);
                    // Each machine picks from its own view of where the medic is, but the chef ends
                    // up at the host's spot: the host shows it there, for everyone.
                    if (SpecialNet.IsServer)
                    {
                        SpecialFx.BeamAll(medic.Position, spot, Special.Medic, 0.6f);
                        SpecialFx.BurstAll(spot, Special.Medic);
                        SpecialFx.TextAllAt(spot + Vector3.up * 2.1f, SpecialFx.NameBoth(Special.Medic, "!", "!"), Special.Medic);
                    }
                    return;
                }
            }
            if (quick)
            {
                // The host looks again now (the floor may have moved, or someone stands there);
                // the others keep the spot it named as the chef fell, and the host's place wins.
                Vector3 safe;
                if (SpecialNet.IsServer && chef != null && SafeSpot(chef, out safe)) MoveSpawn(behaviour, safe, 4f);
                Vector3 at = behaviour.m_startParent != null
                    ? behaviour.m_startParent.TransformPoint(behaviour.m_startLocation)
                    : behaviour.m_startLocation;
                Color colour = SpecialFx.ColourOf(Special.QuickRespawn);
                SpecialFx.Burst(at, colour, 1.4f, 0.6f);
                SpecialFx.Text(at + Vector3.up * 2.1f, SpecialCatalog.NameOf(Special.QuickRespawn) + "!", colour);
            }
        }

        /// <summary>
        /// This respawn brings the chef back at <paramref name="spot"/>; the level's own respawn
        /// point is put back <paramref name="seconds"/> later.
        /// </summary>
        private static void MoveSpawn(PlayerRespawnBehaviour behaviour, Vector3 spot, float seconds)
        {
            SpawnOverride entry = null;
            for (int i = 0; i < s_overrides.Count; i++)
                if (s_overrides[i].Behaviour == behaviour)
                {
                    entry = s_overrides[i];
                    break;
                }
            if (entry == null)
            {
                entry = new SpawnOverride { Behaviour = behaviour, Location = behaviour.m_startLocation, Parent = behaviour.m_startParent };
                s_overrides.Add(entry);
            }
            else if (behaviour.m_startParent != null || behaviour.m_startLocation != entry.Spot)
            {
                // The level moved its respawn point meanwhile: that is the one to put back.
                entry.Location = behaviour.m_startLocation;
                entry.Parent = behaviour.m_startParent;
            }
            entry.Spot = spot;
            entry.Until = Mathf.Max(entry.Until, SpecialRuntime.Now + seconds);
            behaviour.m_startParent = null;
            behaviour.m_startLocation = spot;
        }

        private static void RestoreSpawn(SpawnOverride entry)
        {
            PlayerRespawnBehaviour behaviour = entry.Behaviour;
            // Unless the level moved the respawn point meanwhile.
            if (behaviour == null || behaviour.m_startParent != null || behaviour.m_startLocation != entry.Spot) return;
            behaviour.m_startParent = entry.Parent;
            behaviour.m_startLocation = entry.Location;
        }

        // --- Hover (every machine that simulates the chef: the host, and the owner) -------------

        /// <summary>From the movement update: is the chef hovering this frame?</summary>
        internal static bool UpdateHover(PlayerControls controls, bool dashing)
        {
            ChefState chef = SpecialRuntime.StateOf(controls.gameObject);
            if (chef == null || !chef.Has(Special.Hover)) return false;
            Rigidbody body = controls.GetComponent<Rigidbody>();
            if (body == null || body.isKinematic) return false;
            float now = SpecialRuntime.Now;
            float until = chef.Get(HoverUntilKey, 0f);
            if (controls.m_bRespawning || controls.GroundCollider != null)
            {
                if (until > 0f) chef.Set(HoverUntilKey, 0f);
                return false;
            }
            if (now < until) return true;
            if (until > 0f)
            {
                // Ran out over the gap: falls as usual.
                chef.Set(HoverUntilKey, 0f);
                return false;
            }
            if (!dashing || now < chef.Get(HoverReadyKey, 0f)) return false;
            // Only over a real gap, not a seam or a step.
            Warp.InitMasks();
            if (Physics.Raycast(controls.transform.position + Vector3.up * 0.2f, Vector3.down, 1.2f, Warp.GroundMask,
                    QueryTriggerInteraction.Ignore))
                return false;
            chef.Set(HoverUntilKey, now + HoverSeconds);
            chef.Set(HoverReadyKey, now + HoverRecharge);
            if (chef.Local) chef.SetTimer(Special.Hover, now + HoverRecharge, HoverRecharge);
            HoverFx(chef);
            return true;
        }

        /// <summary>
        /// Shows a hover starting: the owner draws its own at once; the host tells everyone else
        /// (the owner skips that, it already drew it).
        /// </summary>
        private static void HoverFx(ChefState chef)
        {
            if (!SpecialNet.IsServer)
            {
                if (chef.Local) DrawHover(chef);
                return;
            }
            if (chef.Id == 0 || !SpecialNet.Online)
            {
                DrawHover(chef);
                return;
            }
            SpecialNet.SendToAll(new SpecialMessage { Topic = TopicHover, Chef = chef.Id });
        }

        private static void OnHoverMessage(SpecialMessage message)
        {
            ChefState chef = SpecialRuntime.StateById(message.Chef);
            if (chef == null || chef.Chef == null || (chef.Local && !SpecialNet.IsServer)) return;
            DrawHover(chef);
        }

        private static void DrawHover(ChefState chef)
        {
            Color colour = SpecialFx.ColourOf(Special.Hover);
            SpecialFx.Burst(chef.Position, colour, 1.2f, 0.5f);
            SpecialFx.Mark(chef.Chef, colour, HoverSeconds + 0.3f, null, 0.75f);
            SpecialFx.Text(chef.Chef, SpecialCatalog.NameOf(Special.Hover) + "!", colour);
        }

        /// <summary>After the movement update: no falling, and keep going across once the dash ends.</summary>
        internal static void HoverCarry(PlayerControls controls, bool dashing)
        {
            Rigidbody body = controls.GetComponent<Rigidbody>();
            if (body == null) return;
            Vector3 velocity = body.velocity;
            if (velocity.y < 0f) velocity.y = 0f;
            if (!dashing && controls.Movement != null)
            {
                Vector3 forward = Warp.Flat(controls.transform.forward);
                float along = velocity.x * forward.x + velocity.z * forward.z;
                float minimum = controls.Movement.RunSpeed;
                if (along < minimum)
                {
                    velocity.x += forward.x * (minimum - along);
                    velocity.z += forward.z * (minimum - along);
                }
            }
            body.velocity = velocity;
        }

        // --- Transit Pro (host: portals, cannons and vehicles are all run by the host) ----------
        // Out of a teleporter, out of a cannon flight, or off a platform or cart it steered: a
        // burst of speed with dashes that don't recharge. It also does what Cannoneer (no fuse)
        // and Driver (steering 50% faster) did, now that those two are retired.

        /// <summary>Host: the Transit Pro chef steering with these controls, or null.</summary>
        internal static ChefState DriverOf(PlayerControls.ControlSchemeData scheme)
        {
            if (scheme == null) return null;
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
                if (chefs[i].Controls != null && chefs[i].Controls.ControlScheme == scheme)
                    return chefs[i].Has(Special.PortalPro) ? chefs[i] : null;
            return null;
        }

        /// <summary>Host: a ring under a Transit Pro chef while their vehicle is moving (throttled).</summary>
        internal static void MarkDriver(ChefState driver)
        {
            // MarkAll holds back repeats per chef, so two drivers at once both keep their ring.
            if (driver.Chef == null) return;
            SpecialFx.MarkAll(driver.Chef, Special.PortalPro, 1f, null);
        }

        /// <summary>Host: the burst of speed, on every machine.</summary>
        internal static void TransitBoost(ChefState chef)
        {
            if (chef == null || chef.Chef == null) return;
            SpecialRuntime.BroadcastBuff(chef, BuffId.NoDashCooldown, TransitSeconds, 1f, chef);
            SpecialRuntime.BroadcastBuff(chef, BuffId.RunSpeed, TransitSeconds, 0.4f, chef);
            SpecialFx.MarkAll(chef.Chef, Special.PortalPro, TransitSeconds, null);
            SpecialFx.TextAll(chef.Chef, Lang.Both("SPEED UP!", "TĂNG TỐC!"), Special.PortalPro);
        }
    }

    /// <summary>Owner machine: no correction while a predicted move waits for the host.</summary>
    [HarmonyPatch(typeof(ClientChefSynchroniser), "RunCorrection")]
    internal static class WarpCorrectionPatch
    {
        private static bool Prefix(ClientChefSynchroniser __instance)
        {
            try
            {
                if (!SpecialRuntime.InLevel) return true;
                return !Warp.HoldCorrection(__instance);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Teleport.RunCorrection", error);
                return true;
            }
        }
    }

    /// <summary>Host: Safety Net and Lifeguard catch a chef falling into a pit or water.</summary>
    [HarmonyPatch(typeof(ServerRespawnCollider), "ObjectAdded")]
    internal static class SafetyNetPatch
    {
        private static bool Prefix(ServerRespawnCollider __instance, GameObject _gameObject)
        {
            try
            {
                if (!SpecialRuntime.InLevel || _gameObject == null) return true;
                if (!SpecialRuntime.AnyHas(Special.SafetyNet) && !SpecialRuntime.AnyHas(Special.Lifeguard)) return true;
                return !TeleportSpecials.TryCatchFall(__instance, _gameObject);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("SafetyNet.ObjectAdded", error);
                return true;
            }
        }
    }

    /// <summary>
    /// Host: a chef starts respawning. Called before the coroutine's body runs, so before the game
    /// takes what the chef holds (DropOrDestroyHeldItems) and before it tells the machines.
    /// </summary>
    [HarmonyPatch(typeof(ServerPlayerRespawnBehaviour), "RespawnCoroutine")]
    internal static class RespawnFallPatch
    {
        private static void Prefix(ServerPlayerRespawnBehaviour __instance)
        {
            try
            {
                if (!SpecialRuntime.InLevel || !TeleportSpecials.AnyFallSpecial()) return;
                TeleportSpecials.OnFell(__instance.gameObject);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Medic.RespawnCoroutine", error);
            }
        }
    }

    /// <summary>Every machine: Quick Respawn, Medic, Sticky Fingers and Fragile change the respawn time.</summary>
    [HarmonyPatch(typeof(ClientPlayerRespawnBehaviour), "ApplyServerEvent")]
    internal static class RespawnTimePatch
    {
        private static void Prefix(ClientPlayerRespawnBehaviour __instance, Serialisable serialisable)
        {
            try
            {
                if (!SpecialRuntime.InLevel || !TeleportSpecials.AnyRespawnSpecial()) return;
                RespawnMessage message = serialisable as RespawnMessage;
                if (message == null || message.m_Phase != RespawnMessage.Phase.Begin || __instance.IsRespawning) return;
                TeleportSpecials.OnRespawnBegin(__instance);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("QuickRespawn.ApplyServerEvent", error);
            }
        }
    }

    /// <summary>Every machine: a chef with a Medic teammate reappears next to the medic.</summary>
    [HarmonyPatch(typeof(ClientPlayerRespawnBehaviour), "RunSpawnAtSpawnPoint")]
    internal static class MedicSpawnPatch
    {
        private static void Prefix(ClientPlayerRespawnBehaviour __instance)
        {
            try
            {
                if (!SpecialRuntime.InLevel || !TeleportSpecials.AnySpawnPending()) return;
                TeleportSpecials.OnSpawn(__instance);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Medic.RunSpawnAtSpawnPoint", error);
            }
        }
    }

    /// <summary>Hover: decides each movement update whether the chef is carried over a gap.</summary>
    [HarmonyPatch(typeof(ClientPlayerControlsImpl_Default), "Update_Movement")]
    internal static class HoverMovePatch
    {
        private static readonly FieldInfo ControlsField =
            AccessTools.Field(typeof(ClientPlayerControlsImpl_Default), "m_controls");
        private static readonly FieldInfo DashTimerField =
            AccessTools.Field(typeof(ClientPlayerControlsImpl_Default), "m_dashTimer");

        // The movement update running now is hovering (one chef at a time, main thread).
        internal static ClientPlayerControlsImpl_Default Hovering;
        private static PlayerControls _controls;

        private static void Prefix(ClientPlayerControlsImpl_Default __instance)
        {
            Hovering = null;
            _controls = null;
            try
            {
                if (!SpecialRuntime.InLevel || ControlsField == null || DashTimerField == null) return;
                // Every chef, every frame: skip the reflection unless someone can hover.
                if (!SpecialRuntime.AnyHas(Special.Hover)) return;
                PlayerControls controls = ControlsField.GetValue(__instance) as PlayerControls;
                if (controls == null) return;
                bool dashing = (float)DashTimerField.GetValue(__instance) > 0f;
                if (TeleportSpecials.UpdateHover(controls, dashing))
                {
                    Hovering = __instance;
                    _controls = controls;
                }
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Hover.Update_Movement", error);
            }
        }

        private static void Postfix(ClientPlayerControlsImpl_Default __instance)
        {
            if (Hovering != __instance || _controls == null) return;
            try
            {
                TeleportSpecials.HoverCarry(_controls, (float)DashTimerField.GetValue(__instance) > 0f);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Hover.Carry", error);
            }
        }

        [HarmonyFinalizer]
        private static void Finalizer()
        {
            Hovering = null;
            _controls = null;
        }
    }

    /// <summary>Hover: no gravity while carried over the gap.</summary>
    [HarmonyPatch(typeof(ClientPlayerControlsImpl_Default), "ApplyGravityForce")]
    internal static class HoverGravityPatch
    {
        private static bool Prefix(ClientPlayerControlsImpl_Default __instance)
        {
            // Only ever set for a Hover chef in a level (HoverMovePatch).
            return HoverMovePatch.Hovering == null || HoverMovePatch.Hovering != __instance;
        }
    }

    /// <summary>
    /// Transit Pro (host): which chef each portal exit is bringing out, and the steered platforms
    /// a Transit Pro chef has moved since taking the controls (instance IDs: host only).
    /// </summary>
    internal static class PortalState
    {
        internal static readonly Dictionary<int, GameObject> Arriving = new Dictionary<int, GameObject>();
        internal static readonly HashSet<int> Steered = new HashSet<int>();

        internal static void Clear()
        {
            Arriving.Clear();
            Steered.Clear();
        }
    }

    [HarmonyPatch(typeof(ServerTeleportalPlayerReceiver), "TeleportRoutine")]
    internal static class PortalArrivePatch
    {
        private static void Prefix(ServerTeleportalPlayerReceiver __instance, ITeleportable _object)
        {
            try
            {
                if (!SpecialRuntime.InLevel || !SpecialRuntime.AnyHas(Special.PortalPro)) return;
                Component component = _object as Component;
                if (component == null) return;
                PortalState.Arriving[__instance.GetInstanceID()] = component.gameObject;
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("PortalPro.TeleportRoutine", error);
            }
        }
    }

    /// <summary>Transit Pro: out of the portal, a 4 s burst of speed with dashes that don't recharge.</summary>
    [HarmonyPatch(typeof(ServerTeleportalPlayerReceiver), "OnAnimationFinished")]
    internal static class PortalExitPatch
    {
        private static void Postfix(ServerTeleportalPlayerReceiver __instance, string _animName)
        {
            try
            {
                if (!SpecialRuntime.InLevel || _animName != "Receive" || PortalState.Arriving.Count == 0) return;
                GameObject chefObject;
                int key = __instance.GetInstanceID();
                if (!PortalState.Arriving.TryGetValue(key, out chefObject)) return;
                PortalState.Arriving.Remove(key);
                ChefState chef = SpecialRuntime.StateOf(chefObject);
                if (chef == null || !chef.Has(Special.PortalPro)) return;
                TeleportSpecials.TransitBoost(chef);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("PortalPro.OnAnimationFinished", error);
            }
        }
    }

    /// <summary>Transit Pro: cannons the host fires early, whose late launch trigger is then ignored.</summary>
    internal static class CannonState
    {
        internal static readonly Dictionary<int, float> Swallow = new Dictionary<int, float>();
        internal static bool Firing;

        internal static void Clear()
        {
            Swallow.Clear();
            Firing = false;
        }
    }

    /// <summary>
    /// Transit Pro (host; was Cannoneer): when the button is pressed with a Transit Pro chef
    /// loaded and the cannon ready, it fires at once instead of after the fuse.
    /// </summary>
    [HarmonyPatch(typeof(ServerCannon), "ButtonInteractCallback")]
    internal static class CannonFirePatch
    {
        private static readonly FieldInfo LoadedField = AccessTools.Field(typeof(ServerCannon), "m_loadedObject");
        private static readonly FieldInfo FlyingField = AccessTools.Field(typeof(ServerCannon), "m_flying");
        private static readonly FieldInfo ReadyField = AccessTools.Field(typeof(ServerCannon), "m_readyToLaunch");
        private static readonly FieldInfo CannonField = AccessTools.Field(typeof(ServerCannon), "m_cannon");

        private static void Postfix(ServerCannon __instance)
        {
            try
            {
                if (!SpecialRuntime.InLevel || !SpecialRuntime.AnyHas(Special.PortalPro)) return;
                if (LoadedField == null || FlyingField == null || ReadyField == null || CannonField == null) return;
                GameObject loaded = LoadedField.GetValue(__instance) as GameObject;
                if (loaded == null || !SpecialRuntime.Has(loaded, Special.PortalPro)) return;
                if ((bool)FlyingField.GetValue(__instance) || !(bool)ReadyField.GetValue(__instance)) return;
                Cannon cannon = CannonField.GetValue(__instance) as Cannon;
                if (cannon == null || string.IsNullOrEmpty(cannon.m_launchTrigger)) return;
                CannonState.Firing = true;
                try
                {
                    __instance.OnTrigger(cannon.m_launchTrigger);
                }
                finally
                {
                    CannonState.Firing = false;
                }
                if ((bool)FlyingField.GetValue(__instance))
                {
                    CannonState.Swallow[__instance.GetInstanceID()] = SpecialRuntime.Now + 4f;
                    SpecialFx.BurstAll(__instance.transform.position, Special.PortalPro);
                    SpecialFx.TextAllAt(__instance.transform.position + Vector3.up * 2.2f, Lang.Both("FIRE!", "BẮN!"), Special.PortalPro);
                }
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("TransitPro.ButtonInteractCallback", error);
            }
        }
    }

    /// <summary>
    /// Transit Pro (host): a chef the cannon flew lands (EndCannonRoutine also ends a cannon left
    /// without firing; only a landing finds the cannon still flying).
    /// </summary>
    [HarmonyPatch(typeof(ServerCannon), "EndCannonRoutine")]
    internal static class CannonLandPatch
    {
        private static readonly FieldInfo FlyingField = AccessTools.Field(typeof(ServerCannon), "m_flying");

        private static void Prefix(ServerCannon __instance, GameObject _obj)
        {
            try
            {
                if (!SpecialRuntime.InLevel || _obj == null || FlyingField == null || !SpecialRuntime.AnyHas(Special.PortalPro)) return;
                if (!(bool)FlyingField.GetValue(__instance)) return;
                ChefState chef = SpecialRuntime.StateOf(_obj);
                if (chef == null || !chef.Has(Special.PortalPro)) return;
                TeleportSpecials.TransitBoost(chef);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("TransitPro.EndCannonRoutine", error);
            }
        }
    }

    /// <summary>Transit Pro (host): the fuse's own launch trigger, arriving later, is dropped once.</summary>
    [HarmonyPatch(typeof(ServerCannon), "OnTrigger")]
    internal static class CannonTriggerPatch
    {
        private static readonly FieldInfo CannonField = AccessTools.Field(typeof(ServerCannon), "m_cannon");

        private static bool Prefix(ServerCannon __instance, string _trigger)
        {
            try
            {
                if (!SpecialRuntime.InLevel || CannonState.Firing || CannonState.Swallow.Count == 0 || CannonField == null) return true;
                int key = __instance.GetInstanceID();
                float until;
                if (!CannonState.Swallow.TryGetValue(key, out until)) return true;
                Cannon cannon = CannonField.GetValue(__instance) as Cannon;
                if (cannon == null || _trigger != cannon.m_launchTrigger) return true;
                CannonState.Swallow.Remove(key);
                return SpecialRuntime.Now > until;
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("TransitPro.OnTrigger", error);
                return true;
            }
        }
    }

    /// <summary>
    /// Transit Pro (host; was Driver): carts and platforms steered by a Transit Pro chef move 50%
    /// faster, and are remembered as moved, for the burst when the chef lets go of them.
    /// </summary>
    [HarmonyPatch(typeof(ServerPilotMovement), "Update_Movement")]
    internal static class DriverPatch
    {
        private static readonly FieldInfo SchemeField = AccessTools.Field(typeof(ServerPilotMovement), "m_controlScheme");
        private static readonly FieldInfo PilotField = AccessTools.Field(typeof(ServerPilotMovement), "m_pilotMovement");

        private static PilotMovement _changed;
        private static float _speed;

        private static void Prefix(ServerPilotMovement __instance)
        {
            _changed = null;
            try
            {
                if (!SpecialRuntime.InLevel || SchemeField == null || PilotField == null) return;
                if (!SpecialRuntime.AnyHas(Special.PortalPro)) return;
                var scheme = SchemeField.GetValue(__instance) as PlayerControls.ControlSchemeData;
                ChefState driver = TeleportSpecials.DriverOf(scheme);
                if (driver == null) return;
                PilotMovement pilot = PilotField.GetValue(__instance) as PilotMovement;
                if (pilot == null) return;
                _changed = pilot;
                _speed = pilot.MoveSpeed;
                pilot.MoveSpeed *= TeleportSpecials.DriverSpeed;
                if (pilot.HasMoved())
                {
                    TeleportSpecials.MarkDriver(driver);
                    PortalState.Steered.Add(__instance.GetInstanceID());
                }
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("TransitPro.Update_Movement", error);
            }
        }

        [HarmonyFinalizer]
        private static void Finalizer()
        {
            if (_changed == null) return;
            _changed.MoveSpeed = _speed;
            _changed = null;
        }
    }

    /// <summary>
    /// Transit Pro (host): a chef lets go of a cart or platform it steered (and moved): the burst
    /// of speed. Rotating controls (a cannon's aim) aren't steered platforms and never count.
    /// </summary>
    [HarmonyPatch(typeof(ServerPilotMovement), "AssignPlayer")]
    internal static class TransitSteerPatch
    {
        private static readonly FieldInfo SchemeField = AccessTools.Field(typeof(ServerPilotMovement), "m_controlScheme");

        private static void Prefix(ServerPilotMovement __instance, PlayerControls.ControlSchemeData _controlScheme)
        {
            try
            {
                if (!SpecialRuntime.InLevel || SchemeField == null || PortalState.Steered.Count == 0) return;
                var previous = SchemeField.GetValue(__instance) as PlayerControls.ControlSchemeData;
                if (previous == _controlScheme) return;
                // Whoever takes the controls next starts afresh.
                if (!PortalState.Steered.Remove(__instance.GetInstanceID()) || previous == null) return;
                TeleportSpecials.TransitBoost(TeleportSpecials.DriverOf(previous));
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("TransitPro.AssignPlayer", error);
            }
        }
    }
}
