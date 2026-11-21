using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Team17.Online.Multiplayer.Messaging;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ChefSkills.Specials
{
    /// <summary>
    /// Carrying specials: PACK MULE, POCKET, WAITER, TELEKINESIS, STRONG ARM, HAND-OFF, PICKPOCKET
    /// and the VEGETARIAN quirk.
    ///
    /// The "stash" (Pack Mule's back, Pocket, Waiter's second dish) keeps one real item per chef
    /// out of play. On the host the item is taken from the chef's hand with the game's own
    /// TakeItem, attached to the chef (IAttachment.Attach, so it is kinematic, counts as held and
    /// follows the chef, even through respawns) without entering a carrier slot, then deactivated
    /// on every machine (the game itself keeps "free objects" inside containers inactive the same
    /// way; inactive items have no colliders, so nothing can touch them, and their UI hides). Giving
    /// it back is a normal Detach + CarryItem. The host tells every machine which item is stashed
    /// (TopicStash), and each machine hides it and, for Pack Mule / Waiter, shows a mesh-only copy
    /// on the chef. Nothing in the item itself changes, so the game's sync stays valid.
    /// </summary>
    internal sealed class CarryingModule : ISpecialModule
    {
        // Host -> all: Chef = chef, I = stashed entity (0 = none), A = kind (Special), D = entity given back.
        internal const ushort TopicStash = (ushort)((int)Special.PackMule * 10 + 0);
        // Owner -> host: Chef = chef, I = held entity, Vector1 = facing.
        internal const ushort TopicToss = (ushort)((int)Special.StrongArm * 10 + 0);
        // Owner -> host: Chef = chef, I = teammate chef.
        internal const ushort TopicHandOff = (ushort)((int)Special.HandOff * 10 + 0);
        // Host -> all: Chef = chef that refused raw meat or fish (its player hears a "no").
        internal const ushort TopicVegetarian = (ushort)((int)Special.Vegetarian * 10 + 0);

        private const float TelekinesisRange = 6f;
        private const float TelekinesisArc = 30f;
        private const float HandOffRange = 1.5f;
        private const float HandOffArc = 50f;
        private const float TickSeconds = 0.2f;

        private sealed class Stash
        {
            public GameObject Item;
            public uint Entity;
            public Special Kind;
            public GameObject Visual;
            public ServerLimitedQuantityItem Limited;
        }

        // Chef entity ID -> what it has stashed (every machine).
        private static readonly Dictionary<uint, Stash> s_stash = new Dictionary<uint, Stash>();
        private static readonly List<uint> s_keys = new List<uint>();
        private static readonly Dictionary<uint, float> s_deniedAt = new Dictionary<uint, float>();
        // Keeps a stashed item from being removed by the limited-quantity cleanup.
        private static readonly Generic<bool> s_invincible = AlwaysTrue;
        private static float s_nextTick;

        private static readonly string[] MeatWords =
        {
            "meat", "fish", "prawn", "shrimp", "chicken", "beef", "steak", "sausage", "bacon", "pork",
            "turkey", "lamb", "salmon", "tuna", "crab", "lobster", "squid", "octopus", "duck", "mince"
        };

        public void Register()
        {
            ActiveHandler swap = new ActiveHandler { Execute = ExecuteSwap };
            SpecialRuntime.RegisterActive(Special.PackMule, swap);
            SpecialRuntime.RegisterActive(Special.Pocket, swap);
            SpecialRuntime.RegisterActive(Special.Telekinesis, new ActiveHandler { Execute = ExecuteTelekinesis });

            SpecialNet.Register(TopicStash, OnStashMessage);
            SpecialNet.Register(TopicToss, OnTossRequest);
            SpecialNet.Register(TopicHandOff, OnHandOffRequest);
            SpecialNet.Register(TopicVegetarian, OnVegetarianDenied);

            SpecialRuntime.Tick += OnTick;
            SpecialRuntime.LevelStarted += Clear;
            SpecialRuntime.LevelEnded += Clear;
        }

        private static bool AlwaysTrue()
        {
            return true;
        }

        // --- small helpers ------------------------------------------------------------------------

        internal static ServerPlayerAttachmentCarrier ServerCarrier(ChefState state)
        {
            return state != null && state.Chef != null ? state.Chef.GetComponent<ServerPlayerAttachmentCarrier>() : null;
        }

        private static uint EntityOf(GameObject obj)
        {
            if (obj == null) return 0;
            uint id = EntitySerialisationRegistry.GetId(obj);
            return id == EntitySerialisationRegistry.kInvalidEntityID ? 0 : id;
        }

        private static GameObject EntityObject(uint id)
        {
            if (id == 0) return null;
            EntitySerialisationEntry entry = EntitySerialisationRegistry.GetEntry(id);
            return entry != null ? entry.m_GameObject : null;
        }

        private static Vector2 FlatForward(Vector3 forward)
        {
            Vector2 xz = new Vector2(forward.x, forward.z);
            return xz.sqrMagnitude > 0.0001f ? xz.normalized : new Vector2(0f, 1f);
        }

        private static bool Busy(ChefState state)
        {
            PlayerControls controls = state.Controls;
            return controls == null || controls.m_bRespawning || controls.GetCurrentlyInteracting() != null;
        }

        /// <summary>A plate with food on it.</summary>
        internal static bool IsDish(GameObject item)
        {
            if (item == null || item.GetComponent<ServerPlate>() == null) return false;
            ServerIngredientContainer container = item.GetComponent<ServerIngredientContainer>();
            return container != null && container.GetContentsCount() > 0;
        }

        private static string CleanName(GameObject item)
        {
            if (item == null) return "";
            string name = item.name;
            int clone = name.IndexOf("(Clone)", StringComparison.Ordinal);
            if (clone > 0) name = name.Substring(0, clone);
            return name.Replace('_', ' ').Trim();
        }

        // --- stash: host side ---------------------------------------------------------------------

        private static Stash StashOf(ChefState state)
        {
            Stash stash;
            if (state == null || !s_stash.TryGetValue(state.Id, out stash)) return null;
            if (stash.Item == null)
            {
                DropRecord(state.Id);
                return null;
            }
            return stash;
        }

        /// <summary>Can this held item be stashed (a normal networked attachment)?</summary>
        private static bool Stashable(GameObject item)
        {
            if (item == null || EntityOf(item) == 0) return false;
            if (item.GetComponent<ServerPhysicalAttachment>() == null) return false;
            // A backpack lives in the Back slot; never in the hand, but be safe.
            ServerBackpack backpack = item.GetComponent<ServerBackpack>();
            return backpack == null;
        }

        /// <summary>Host: moves the chef's held item into its stash. Doesn't broadcast.</summary>
        private static bool ServerStashHeld(ChefState state, ServerPlayerAttachmentCarrier carrier, Special kind)
        {
            GameObject item = carrier.InspectCarriedItem();
            if (!Stashable(item)) return false;
            PlayerAttachmentCarrier parent = state.Chef.GetComponent<PlayerAttachmentCarrier>();
            IAttachment attachment = item.GetComponent<IAttachment>();
            if (parent == null || attachment == null) return false;

            carrier.TakeItem();
            // Attached to the chef but in no carrier slot: kinematic, "held", follows the chef.
            if (attachment.IsAttached()) attachment.Detach();
            attachment.Attach(parent);
            ServerLimitedQuantityItem limited = item.GetComponent<ServerLimitedQuantityItem>();
            if (limited != null) limited.AddInvincibilityCondition(s_invincible);
            HideLocal(state, item, kind);
            Stash stash;
            if (s_stash.TryGetValue(state.Id, out stash)) stash.Limited = limited;
            return true;
        }

        /// <summary>Host: puts the stashed item back in the chef's hand (or drops it if the hand is full).</summary>
        private static uint ServerGiveBack(ChefState state, ServerPlayerAttachmentCarrier carrier, Stash stash)
        {
            GameObject item = stash.Item;
            uint id = stash.Entity;
            if (stash.Limited != null) stash.Limited.RemoveInvincibilityCondition(s_invincible);
            DestroyVisual(stash);
            RevealLocal(item);
            if (item == null) return id;
            IAttachment attachment = item.GetComponent<IAttachment>();
            if (attachment != null && attachment.IsAttached()) attachment.Detach();
            // Same as the game's own pickup of a loose item (ServerCarryableItem.HandlePickup).
            if (carrier != null && carrier.InspectCarriedItem() == null) carrier.CarryItem(item);
            return id;
        }

        private static void BroadcastStash(ChefState state, uint stashed, Special kind, uint revealed)
        {
            SpecialNet.SendToAll(new SpecialMessage
            {
                Topic = TopicStash, Chef = state.Id, I = (int)stashed, A = (int)kind, D = revealed
            });
        }

        /// <summary>PACK MULE / POCKET: swap the held item and the stashed one.</summary>
        private static bool ExecuteSwap(ChefState state)
        {
            ServerPlayerAttachmentCarrier carrier = ServerCarrier(state);
            if (carrier == null || state.Controls == null || state.Controls.m_bRespawning) return false;
            Special kind = state.Active == Special.Pocket ? Special.Pocket : Special.PackMule;
            GameObject hand = carrier.InspectCarriedItem();
            Stash previous = StashOf(state);
            if (hand == null && previous == null) return false;
            if (hand != null && !Stashable(hand)) return false;
            // Taking something into the hands mid-chop would confuse the station.
            if (hand == null && Busy(state)) return false;

            if (previous != null) s_stash.Remove(state.Id);
            uint stashed = 0;
            if (hand != null)
            {
                if (!ServerStashHeld(state, carrier, kind))
                {
                    if (previous != null) s_stash[state.Id] = previous;
                    return false;
                }
                stashed = EntityOf(hand);
            }
            uint revealed = previous != null ? ServerGiveBack(state, carrier, previous) : 0;
            BroadcastStash(state, stashed, kind, revealed);
            return true;
        }

        // --- stash: every machine -----------------------------------------------------------------

        private static void OnStashMessage(SpecialMessage message)
        {
            ChefState state = SpecialRuntime.StateById(message.Chef);
            uint revealed = (uint)message.D;
            if (revealed != 0) RevealLocal(EntityObject(revealed));
            if (state == null) return;
            GameObject item = EntityObject((uint)message.I);
            if (message.I != 0 && item != null) HideLocal(state, item, (Special)(int)message.A);
            else if (message.I == 0) DropRecord(state.Id);
            UpdateStatus(state);
        }

        /// <summary>Hides a stashed item on this machine and records it (idempotent).</summary>
        private static void HideLocal(ChefState state, GameObject item, Special kind)
        {
            Stash stash;
            if (s_stash.TryGetValue(state.Id, out stash) && stash.Item != item)
            {
                // Replaced without a reveal (shouldn't happen): forget the old visual only.
                DestroyVisual(stash);
                stash = null;
            }
            if (stash == null)
            {
                stash = new Stash { Item = item, Entity = EntityOf(item), Kind = kind };
                s_stash[state.Id] = stash;
            }
            stash.Kind = kind;
            if (item.activeSelf)
            {
                // Copy the look while it is still visible, then take it out of play.
                if (stash.Visual == null && kind != Special.Pocket) stash.Visual = BuildVisual(state, item, kind);
                item.SetActive(false);
            }
            UpdateStatus(state);
        }

        /// <summary>Shows a stashed item again on this machine and forgets it (idempotent).</summary>
        private static void RevealLocal(GameObject item)
        {
            if (item == null) return;
            s_keys.Clear();
            foreach (KeyValuePair<uint, Stash> pair in s_stash)
                if (pair.Value.Item == item) s_keys.Add(pair.Key);
            for (int i = 0; i < s_keys.Count; i++)
            {
                DropRecord(s_keys[i]);
                UpdateStatus(SpecialRuntime.StateById(s_keys[i]));
            }
            if (!item.activeSelf) item.SetActive(true);
        }

        private static void DropRecord(uint chef)
        {
            Stash stash;
            if (!s_stash.TryGetValue(chef, out stash)) return;
            DestroyVisual(stash);
            s_stash.Remove(chef);
        }

        private static void DestroyVisual(Stash stash)
        {
            if (stash != null && stash.Visual != null) Object.Destroy(stash.Visual);
            if (stash != null) stash.Visual = null;
        }

        private static void UpdateStatus(ChefState state)
        {
            if (state == null) return;
            Special active = state.Active;
            if (active != Special.PackMule && active != Special.Pocket) return;
            Stash stash;
            state.SetStatus("Carrying", s_stash.TryGetValue(state.Id, out stash) && stash.Item != null
                ? (active == Special.Pocket ? "Pocket: " : "Back: ") + CleanName(stash.Item)
                : null);
        }

        /// <summary>
        /// A mesh-only copy of the item (no colliders, no scripts) on the chef's back (Pack Mule)
        /// or balanced above its hand (Waiter). Purely cosmetic and local.
        /// </summary>
        private static GameObject BuildVisual(ChefState state, GameObject item, Special kind)
        {
            try
            {
                Transform chef = state.Chef.transform;
                PlayerAttachmentCarrier carrier = state.Chef.GetComponent<PlayerAttachmentCarrier>();
                Vector3 local;
                float scale;
                if (kind == Special.Waiter)
                {
                    Transform hand = carrier != null ? carrier.GetAttachPoint(PlayerAttachTarget.Default) : null;
                    local = hand != null ? chef.InverseTransformPoint(hand.position + Vector3.up * 0.3f) : new Vector3(0f, 1.3f, 0.5f);
                    scale = 1f;
                }
                else
                {
                    Transform back = carrier != null ? carrier.GetAttachPoint(PlayerAttachTarget.Back) : null;
                    local = back != null ? chef.InverseTransformPoint(back.position) + new Vector3(0f, 0.1f, -0.1f) : new Vector3(0f, 0.9f, -0.45f);
                    scale = 0.8f;
                }

                GameObject root = new GameObject("ChefSkills Stash");
                root.transform.SetParent(chef, false);
                root.transform.localPosition = local;
                root.transform.localRotation = Quaternion.identity;
                root.transform.localScale = Vector3.one * scale;

                Transform reference = item.transform;
                Vector3 referenceScale = reference.lossyScale;
                foreach (MeshRenderer renderer in item.GetComponentsInChildren<MeshRenderer>(false))
                {
                    if (renderer == null || !renderer.enabled) continue;
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null) continue;
                    GameObject part = new GameObject("part");
                    part.layer = renderer.gameObject.layer;
                    part.transform.SetParent(root.transform, false);
                    part.transform.localPosition = reference.InverseTransformPoint(renderer.transform.position);
                    part.transform.localRotation = Quaternion.Inverse(reference.rotation) * renderer.transform.rotation;
                    Vector3 lossy = renderer.transform.lossyScale;
                    part.transform.localScale = new Vector3(
                        lossy.x / Mathf.Max(0.0001f, referenceScale.x),
                        lossy.y / Mathf.Max(0.0001f, referenceScale.y),
                        lossy.z / Mathf.Max(0.0001f, referenceScale.z));
                    part.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                    part.AddComponent<MeshRenderer>().sharedMaterials = renderer.sharedMaterials;
                }
                return root;
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("PackMule.Visual", error);
                return null;
            }
        }

        private static void Clear()
        {
            foreach (Stash stash in s_stash.Values) DestroyVisual(stash);
            s_stash.Clear();
            s_deniedAt.Clear();
            s_nextTick = 0f;
            WaiterPatch.Reentered = false;
        }

        private static void OnTick(float deltaTime)
        {
            if (s_stash.Count == 0) return;
            if (SpecialRuntime.Now < s_nextTick) return;
            s_nextTick = SpecialRuntime.Now + TickSeconds;
            bool server = SpecialNet.IsServer;

            s_keys.Clear();
            s_keys.AddRange(s_stash.Keys);
            for (int i = 0; i < s_keys.Count; i++)
            {
                uint chef = s_keys[i];
                Stash stash = s_stash[chef];
                ChefState state = SpecialRuntime.StateById(chef);
                if (state == null || state.Chef == null || stash.Item == null)
                {
                    // The chef left or the item was destroyed: nothing to give back.
                    DropRecord(chef);
                    if (server && state != null) BroadcastStash(state, 0, stash.Kind, 0);
                    continue;
                }

                // The copy disappears with the chef while it respawns.
                bool respawning = state.Controls != null && state.Controls.m_bRespawning;
                if (stash.Visual != null && stash.Visual.activeSelf == respawning) stash.Visual.SetActive(!respawning);

                if (!server) continue;
                if (!stash.Item.transform.IsChildOf(state.Chef.transform))
                {
                    // Something else took it: let it be a normal item again.
                    RevealLocal(stash.Item);
                    BroadcastStash(state, 0, stash.Kind, stash.Entity);
                    continue;
                }
                // WAITER: once the hands are free, the second dish comes down into them.
                if (stash.Kind == Special.Waiter && !Busy(state))
                {
                    ServerPlayerAttachmentCarrier carrier = ServerCarrier(state);
                    if (carrier != null && carrier.InspectCarriedItem() == null)
                    {
                        s_stash.Remove(chef);
                        uint revealed = ServerGiveBack(state, carrier, stash);
                        BroadcastStash(state, 0, Special.Waiter, revealed);
                    }
                }
            }
        }

        // --- WAITER -------------------------------------------------------------------------------

        /// <summary>
        /// Host, inside a failed "put down" of a dish onto another dish: the held dish goes up on
        /// the arm and the other one is picked up instead.
        /// </summary>
        internal static bool TryWaiterStack(ChefState state, PlayerControls controls, GameObject held, GameObject target)
        {
            ServerPlayerAttachmentCarrier carrier = ServerCarrier(state);
            if (carrier == null || carrier.InspectCarriedItem() != held || StashOf(state) != null) return false;
            Vector2 direction = FlatForward(controls.transform.forward);
            IHandlePlacement placement = PlayerControlsHelper.GetControllingPlacementHandler_Server(target);
            if (placement != null && placement.CanHandlePlacement(carrier, direction, new PlacementContext(PlacementContext.Source.Player)))
                return false; // the game can do something with it: let it
            GameObject other = DishAt(target);
            if (other == null || other == held || !IsDish(other)) return false;
            IHandlePickup pickup = PlayerControlsHelper.GetControllingPickupHandler_Server(other);
            if (pickup == null) return false;

            if (!ServerStashHeld(state, carrier, Special.Waiter)) return false;
            if (pickup.CanHandlePickup(carrier)) pickup.HandlePickup(carrier, direction);
            if (carrier.InspectCarriedItem() == other)
            {
                BroadcastStash(state, EntityOf(held), Special.Waiter, 0);
                return true;
            }
            // Couldn't take the other dish: put ours back in the hand as if nothing happened.
            Stash stash = StashOf(state);
            if (stash != null)
            {
                s_stash.Remove(state.Id);
                ServerGiveBack(state, carrier, stash);
            }
            return false;
        }

        private static GameObject DishAt(GameObject target)
        {
            if (target.GetComponent<ServerPlate>() != null) return target;
            ServerAttachStation station = target.GetComponent<ServerAttachStation>();
            if (station != null && station.HasItem())
            {
                GameObject item = station.InspectItem();
                if (item != null && item.GetComponent<ServerPlate>() != null) return item;
            }
            return null;
        }

        /// <summary>Host, after a dish went onto the pass: hand in the stashed dish too.</summary>
        internal static void WaiterDeliverSecond(ChefState state, PlayerControls controls, GameObject target)
        {
            Stash stash = StashOf(state);
            if (stash == null || !IsDish(stash.Item)) return;
            ServerPlayerAttachmentCarrier carrier = ServerCarrier(state);
            if (carrier == null || carrier.InspectCarriedItem() != null) return;
            s_stash.Remove(state.Id);
            uint revealed = ServerGiveBack(state, carrier, stash);
            BroadcastStash(state, 0, stash.Kind, revealed);
            if (carrier.InspectCarriedItem() == stash.Item)
                PlayerControlsHelper.PlaceHeldItem_Server(controls, target);
        }

        // --- TELEKINESIS --------------------------------------------------------------------------

        private static bool ExecuteTelekinesis(ChefState state)
        {
            ServerPlayerAttachmentCarrier carrier = ServerCarrier(state);
            if (carrier == null || carrier.InspectCarriedItem() != null || Busy(state)) return false;
            Vector3 forward = state.Forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) return false;
            forward.Normalize();
            Vector3 origin = state.Position;

            Collider[] colliders = Physics.OverlapSphere(origin + forward * (TelekinesisRange * 0.5f),
                TelekinesisRange * 0.5f + 0.4f, ~0, QueryTriggerInteraction.Collide);
            var seen = new HashSet<int>();
            IHandlePickup best = null;
            float bestScore = float.MaxValue;
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider == null || !collider.enabled) continue;
                GameObject candidate = PickupObjectOf(collider.transform);
                if (candidate == null || !seen.Add(candidate.GetInstanceID())) continue;

                Vector3 offset = candidate.transform.position - origin;
                offset.y = 0f;
                float distance = offset.magnitude;
                if (distance > TelekinesisRange) continue;
                float angle = distance > 0.5f ? Vector3.Angle(forward, offset) : 0f;
                if (angle > TelekinesisArc) continue;

                IHandlePickup handler = PlayerControlsHelper.GetControllingPickupHandler_Server(candidate);
                if (handler == null || !handler.CanHandlePickup(carrier)) continue;
                if (state.Has(Special.Vegetarian) && IsRawMeat(PickupItemOf(candidate))) continue;
                float score = distance + angle * 0.05f;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = handler;
                }
            }
            if (best == null) return false;
            // The game's own pickup, just from further away: counter, floor or crate alike.
            best.HandlePickup(carrier, FlatForward(forward));
            return carrier.InspectCarriedItem() != null;
        }

        /// <summary>The object a pickup would be aimed at for this collider (null: none, or part of a chef).</summary>
        private static GameObject PickupObjectOf(Transform transform)
        {
            for (int depth = 0; transform != null && depth < 6; depth++, transform = transform.parent)
            {
                if (transform.GetComponent<PlayerControls>() != null) return null;
                if (transform.GetComponent<IHandlePickup>() != null) return transform.gameObject;
            }
            return null;
        }

        // --- STRONG ARM ---------------------------------------------------------------------------

        internal static void RequestToss(ChefState state, GameObject item)
        {
            float next = state.Get<float>("StrongArm.Next", 0f);
            if (SpecialRuntime.Now < next) return;
            state.Set("StrongArm.Next", SpecialRuntime.Now + 0.3f);
            SpecialNet.SendToHost(new SpecialMessage
            {
                Topic = TopicToss, Chef = state.Id, I = (int)EntityOf(item), Vector1 = state.Forward
            });
        }

        /// <summary>
        /// Host: a Strong Arm chef "throws" something the game won't throw (pot, pan, plate, dish,
        /// filled wrap): it leaves the hands as a normal dropped item flying at throw speed. It is a
        /// plain physics object (not a thrown one), so nobody catches it and it lands where it lands.
        /// </summary>
        private static void OnTossRequest(SpecialMessage message)
        {
            if (!SpecialRuntime.InLevel || !SpecialNet.IsServer) return;
            ChefState state = SpecialRuntime.StateById(message.Chef);
            if (state == null || !state.Has(Special.StrongArm) || state.Controls == null || state.Controls.m_bRespawning) return;
            ServerPlayerAttachmentCarrier carrier = ServerCarrier(state);
            GameObject item = carrier != null ? carrier.InspectCarriedItem() : null;
            if (item == null || EntityOf(item) != (uint)message.I) return;
            if (item.GetComponent<UsableItem>() != null) return;
            IAttachment attachment = item.GetComponent<IAttachment>();
            if (attachment == null || attachment.AccessMotion() == null) return;

            Vector2 direction = FlatForward(message.Vector1);
            ServerThrowableItem throwable = item.GetComponent<ServerThrowableItem>();
            IThrower thrower = state.Chef.GetComponent<ServerAttachmentThrower>();
            if (throwable != null && throwable.CanHandleThrow(thrower, direction)) return; // a normal throw
            if (PlayerControlsHelper.IsHeldItemInsideStaticCollision(state.Controls)) return;

            AttachmentThrower config = state.Chef.GetComponent<AttachmentThrower>();
            float force = config != null && config.m_throwForce > 0f ? config.m_throwForce : 10f;
            float inclination = config != null ? config.m_throwInclination : 20f;
            force *= Plugin.Skills.Multiplier(state.Chef, Skill.Throwing) * 0.85f;
            Vector3 velocity = new Vector3(direction.x * force, Mathf.Tan(inclination * Mathf.Deg2Rad) * force, direction.y * force);

            carrier.TakeItem();
            attachment.AccessMotion().SetVelocity(velocity);
        }

        // --- HAND-OFF and PICKPOCKET --------------------------------------------------------------

        /// <summary>Host: moves the held item of one chef into the empty hands of another.</summary>
        internal static bool Transfer(ServerPlayerAttachmentCarrier from, ServerPlayerAttachmentCarrier to, ChefState receiver)
        {
            if (from == null || to == null || from == to) return false;
            GameObject item = from.InspectCarriedItem();
            if (item == null || to.InspectCarriedItem() != null) return false;
            if (receiver != null && receiver.Has(Special.Vegetarian) && IsRawMeat(item))
            {
                DenyVegetarian(receiver);
                return false;
            }
            from.TakeItem();
            IAttachment attachment = item.GetComponent<IAttachment>();
            if (attachment != null && attachment.IsAttached()) attachment.Detach();
            to.CarryItem(item);
            return true;
        }

        /// <summary>Owner machine: the teammate this chef faces closely, or null.</summary>
        internal static ChefState HandOffTarget(ChefState state)
        {
            ChefState best = null;
            float bestDistance = float.MaxValue;
            Vector3 forward = state.Forward;
            forward.y = 0f;
            foreach (ChefState other in SpecialRuntime.Teammates(state))
            {
                if (other.Chef == null) continue;
                Vector3 offset = other.Position - state.Position;
                offset.y = 0f;
                float distance = offset.magnitude;
                if (distance > HandOffRange || distance < 0.01f) continue;
                if (Vector3.Angle(forward, offset) > HandOffArc) continue;
                if (distance < bestDistance)
                {
                    best = other;
                    bestDistance = distance;
                }
            }
            return best;
        }

        private static void OnHandOffRequest(SpecialMessage message)
        {
            if (!SpecialRuntime.InLevel || !SpecialNet.IsServer) return;
            ChefState state = SpecialRuntime.StateById(message.Chef);
            ChefState mate = SpecialRuntime.StateById((uint)message.I);
            if (state == null || mate == null || state == mate || !state.Has(Special.HandOff)) return;
            if (mate.Team != state.Team || Busy(state) || Busy(mate)) return;
            Vector3 offset = mate.Position - state.Position;
            offset.y = 0f;
            if (offset.magnitude > HandOffRange + 0.7f) return;
            ServerPlayerAttachmentCarrier mine = ServerCarrier(state), theirs = ServerCarrier(mate);
            if (mine == null || theirs == null) return;
            if (mine.InspectCarriedItem() == null) Transfer(theirs, mine, state);
            else if (theirs.InspectCarriedItem() == null) Transfer(mine, theirs, mate);
        }

        // --- VEGETARIAN ---------------------------------------------------------------------------

        /// <summary>A loose raw meat or fish ingredient (or a crate's prefab of one).</summary>
        internal static bool IsRawMeat(GameObject item)
        {
            if (item == null) return false;
            IngredientPropertiesComponent properties = item.GetComponent<IngredientPropertiesComponent>();
            if (properties == null) return false;
            try
            {
                // Something that cooks on its own (a grilled skewer) isn't raw once it's done.
                ServerCookingHandler cooking = item.GetComponent<ServerCookingHandler>();
                if (cooking != null && cooking.AccessCookingTime > 0f && cooking.GetCookingProgress() >= cooking.AccessCookingTime)
                    return false;
            }
            catch (Exception)
            {
                // A prefab's cooking handler has no state yet: it's raw.
            }
            string name = item.name;
            try
            {
                IngredientAssembledNode node = properties.GetOrderComposition() as IngredientAssembledNode;
                if (node != null && node.m_ingriedientOrderNode != null) name += " " + node.m_ingriedientOrderNode.name;
            }
            catch (Exception)
            {
            }
            name = name.ToLowerInvariant();
            for (int i = 0; i < MeatWords.Length; i++)
                if (name.IndexOf(MeatWords[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>What picking up this object would put in the hands (item, counter's item, crate's prefab).</summary>
        internal static GameObject PickupItemOf(GameObject target)
        {
            for (int hop = 0; target != null && hop < 2; hop++)
            {
                if (target.GetComponent<IngredientPropertiesComponent>() != null) return target;
                ServerAttachStation station = target.GetComponent<ServerAttachStation>();
                if (station != null) return station.HasItem() ? station.InspectItem() : null;
                ServerPickupItemSpawner crate = target.GetComponent<ServerPickupItemSpawner>();
                if (crate != null) return crate.GetItemPrefab();
                IHandlePickup handler = PlayerControlsHelper.GetControllingPickupHandler_Server(target);
                MonoBehaviour behaviour = handler as MonoBehaviour;
                if (behaviour == null || behaviour.gameObject == target) return target;
                target = behaviour.gameObject;
            }
            return target;
        }

        internal static void DenyVegetarian(ChefState state)
        {
            float last;
            if (s_deniedAt.TryGetValue(state.Id, out last) && SpecialRuntime.Now - last < 0.5f) return;
            s_deniedAt[state.Id] = SpecialRuntime.Now;
            SpecialNet.SendToAll(new SpecialMessage { Topic = TopicVegetarian, Chef = state.Id });
        }

        private static void OnVegetarianDenied(SpecialMessage message)
        {
            ChefState state = SpecialRuntime.StateById(message.Chef);
            if (state != null && state.Local) SpecialHud.Deny(state);
        }
    }

    /// <summary>
    /// WAITER, on the host. Putting a dish down onto another dish (which the game refuses) puts the
    /// held dish up on the arm and picks the other one up. Handing a dish in at the pass hands in
    /// the dish on the arm right after it, through the same placement.
    /// </summary>
    [HarmonyPatch(typeof(PlayerControlsHelper), "PlaceHeldItem_Server")]
    internal static class WaiterPatch
    {
        internal static bool Reentered;

        private static bool Prefix(PlayerControls _control, GameObject _target, out GameObject __state)
        {
            __state = null;
            try
            {
                if (!SpecialRuntime.InLevel || Reentered || _control == null || _target == null) return true;
                ChefState state = SpecialRuntime.StateOf(_control.gameObject);
                if (state == null || !state.Has(Special.Waiter)) return true;
                ServerPlayerAttachmentCarrier carrier = CarryingModule.ServerCarrier(state);
                GameObject held = carrier != null ? carrier.InspectCarriedItem() : null;
                __state = held;
                if (!CarryingModule.IsDish(held)) return true;
                Reentered = true;
                try
                {
                    return !CarryingModule.TryWaiterStack(state, _control, held, _target);
                }
                finally
                {
                    Reentered = false;
                }
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Waiter.Stack", error);
                return true;
            }
        }

        private static void Postfix(PlayerControls _control, GameObject _target, GameObject __state)
        {
            try
            {
                if (!SpecialRuntime.InLevel || Reentered || __state == null || _control == null || _target == null) return;
                if (__state.GetComponent<ServerPlate>() == null) return;
                if (_target.GetComponentInParent<ServerPlateStation>() == null) return;
                ChefState state = SpecialRuntime.StateOf(_control.gameObject);
                if (state == null || !state.Has(Special.Waiter)) return;
                ServerPlayerAttachmentCarrier carrier = CarryingModule.ServerCarrier(state);
                if (carrier == null || carrier.InspectCarriedItem() != null) return; // nothing was handed in
                Reentered = true;
                try
                {
                    CarryingModule.WaiterDeliverSecond(state, _control, _target);
                }
                finally
                {
                    Reentered = false;
                }
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Waiter.Deliver", error);
            }
        }
    }

    /// <summary>
    /// HAND-OFF, on the player's own machine: PICK UP facing a teammate within reach gives or
    /// takes the item (only when exactly one of the two has something). The host does the move.
    /// </summary>
    [HarmonyPatch(typeof(ClientPlayerControlsImpl_Default), "Update_Carry")]
    internal static class HandOffPatch
    {
        private static readonly FieldInfo ControlsField =
            AccessTools.Field(typeof(ClientPlayerControlsImpl_Default), "m_controls");

        private static bool Prefix(ClientPlayerControlsImpl_Default __instance)
        {
            try
            {
                if (!SpecialRuntime.InLevel || ControlsField == null || !SpecialRuntime.AnyHas(Special.HandOff)) return true;
                // A client whose host can't read our channel: the request would go nowhere.
                if (!SpecialNet.IsServer && !SpecialNet.Online) return true;
                PlayerControls controls = ControlsField.GetValue(__instance) as PlayerControls;
                if (controls == null || controls.ControlScheme == null || controls.ControlScheme.m_pickupButton == null) return true;
                ChefState state = SpecialRuntime.StateOf(controls.gameObject);
                if (state == null || !state.Local || !state.Has(Special.HandOff) || controls.m_bRespawning) return true;
                // Peek only: JustPressed would claim the press and the game's own Update_Carry
                // would never see it. Claimed below only when the hand-off replaces it.
                ILogicalButton button = controls.ControlScheme.m_pickupButton;
                if (!button.HasUnclaimedPressEvent()) return true;
                ChefState mate = CarryingModule.HandOffTarget(state);
                if (mate == null) return true;
                ClientPlayerAttachmentCarrier mine = controls.GetComponent<ClientPlayerAttachmentCarrier>();
                ClientPlayerAttachmentCarrier theirs = mate.Chef.GetComponent<ClientPlayerAttachmentCarrier>();
                if (mine == null || theirs == null) return true;
                bool mineEmpty = mine.InspectCarriedItem() == null;
                bool theirsEmpty = theirs.InspectCarriedItem() == null;
                if (mineEmpty == theirsEmpty) return true; // nothing to hand over: the usual PICK UP
                button.ClaimPressEvent();
                SpecialNet.SendToHost(new SpecialMessage
                {
                    Topic = CarryingModule.TopicHandOff, Chef = state.Id, I = (int)mate.Id
                });
                return false;
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("HandOff.Press", error);
                return true;
            }
        }
    }

    /// <summary>
    /// STRONG ARM, on the player's own machine: releasing USE with something the game won't throw
    /// asks the host to toss it (see CarryingModule.OnTossRequest).
    /// </summary>
    [HarmonyPatch(typeof(ClientPlayerControlsImpl_Default), "Update_Throw")]
    internal static class StrongArmPatch
    {
        private static readonly FieldInfo ControlsField =
            AccessTools.Field(typeof(ClientPlayerControlsImpl_Default), "m_controls");

        private static void Prefix(ClientPlayerControlsImpl_Default __instance, bool justReleased, bool isSuppressed)
        {
            try
            {
                if (!SpecialRuntime.InLevel || !justReleased || isSuppressed || ControlsField == null) return;
                PlayerControls controls = ControlsField.GetValue(__instance) as PlayerControls;
                if (controls == null || controls.m_bRespawning) return;
                ChefState state = SpecialRuntime.StateOf(controls.gameObject);
                if (state == null || !state.Local || !state.Has(Special.StrongArm)) return;
                ClientPlayerAttachmentCarrier carrier = controls.GetComponent<ClientPlayerAttachmentCarrier>();
                GameObject item = carrier != null ? carrier.InspectCarriedItem() : null;
                if (item == null || item.GetComponent<UsableItem>() != null) return;
                IClientThrowable throwable = item.GetComponent<IClientThrowable>();
                if (throwable != null)
                {
                    Vector3 forward = controls.transform.forward;
                    Vector2 direction = new Vector2(forward.x, forward.z).normalized;
                    if (throwable.CanHandleThrow(controls.GetComponent<ClientAttachmentThrower>(), direction)) return;
                }
                CarryingModule.RequestToss(state, item);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("StrongArm.Release", error);
            }
        }
    }

    /// <summary>PICKPOCKET, on the host: a dash that hits a chef takes its item into empty hands.</summary>
    [HarmonyPatch(typeof(ClientPlayerControlsImpl_Default), "OnDashCollision")]
    internal static class PickpocketPatch
    {
        private static readonly FieldInfo ControlsField =
            AccessTools.Field(typeof(ClientPlayerControlsImpl_Default), "m_controls");
        private static readonly FieldInfo DashTimerField =
            AccessTools.Field(typeof(ClientPlayerControlsImpl_Default), "m_dashTimer");

        private static void Prefix(ClientPlayerControlsImpl_Default __instance, PlayerControls _otherPlayer)
        {
            try
            {
                if (!SpecialRuntime.InLevel || _otherPlayer == null || ControlsField == null || DashTimerField == null) return;
                if (!SpecialNet.IsServer) return;
                // Read before the game's method ends the dash (a head-on bump resets both timers).
                if ((float)DashTimerField.GetValue(__instance) <= 0f) return;
                PlayerControls controls = ControlsField.GetValue(__instance) as PlayerControls;
                if (controls == null || controls.m_bRespawning || _otherPlayer.m_bRespawning) return;
                ChefState state = SpecialRuntime.StateOf(controls.gameObject);
                if (state == null || !state.Has(Special.Pickpocket)) return;
                CarryingModule.Transfer(_otherPlayer.GetComponent<ServerPlayerAttachmentCarrier>(),
                    controls.GetComponent<ServerPlayerAttachmentCarrier>(), state);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Pickpocket.Dash", error);
            }
        }
    }

    /// <summary>VEGETARIAN, on the host: a pickup of raw meat or fish (loose, on a counter, from a crate) does nothing.</summary>
    [HarmonyPatch(typeof(ServerPlayerControlsImpl_Default), "ReceivePickUpEvent")]
    internal static class VegetarianPickupPatch
    {
        private static bool Prefix(ServerPlayerControlsImpl_Default __instance, GameObject _target)
        {
            try
            {
                if (!SpecialRuntime.InLevel || _target == null) return true;
                ChefState state = SpecialRuntime.StateOf(__instance.gameObject);
                if (state == null || !state.Has(Special.Vegetarian)) return true;
                if (!CarryingModule.IsRawMeat(CarryingModule.PickupItemOf(_target))) return true;
                CarryingModule.DenyVegetarian(state);
                return false;
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Vegetarian.Pickup", error);
                return true;
            }
        }
    }

    /// <summary>VEGETARIAN, on the host: raw meat or fish thrown at the chef isn't caught.</summary>
    [HarmonyPatch(typeof(ServerAttachmentCatcher), "CanHandleCatch")]
    internal static class VegetarianCatchPatch
    {
        private static void Postfix(ServerAttachmentCatcher __instance, ICatchable _object, ref bool __result)
        {
            try
            {
                if (!__result || !SpecialRuntime.InLevel || _object == null) return;
                ChefState state = SpecialRuntime.StateOf(__instance.gameObject);
                if (state == null || !state.Has(Special.Vegetarian)) return;
                if (CarryingModule.IsRawMeat(_object.AccessGameObject())) __result = false;
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Vegetarian.Catch", error);
            }
        }
    }
}
