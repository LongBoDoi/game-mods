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
    /// The "stash" (Pack Mule's back, Pocket, Waiter's second dish) keeps real items out of play.
    /// On the host an item is taken from the chef's hand with the game's own TakeItem, attached to
    /// the chef (IAttachment.Attach, so it is kinematic, counts as held and follows the chef, even
    /// through respawns) without entering a carrier slot, then deactivated on every machine (the
    /// game keeps "free objects" inside containers inactive the same way; inactive items have no
    /// colliders, so nothing can touch them, and their UI hides). Giving it back is a normal Detach
    /// + CarryItem. The host tells every machine which item was stashed or given back
    /// (TopicStash); each machine hides or shows it and, for Pack Mule / Waiter, shows a mesh-only
    /// copy on the chef. Nothing in the item itself changes, so the game's own sync stays valid.
    /// The item's layers are restored when it comes back (a respawn re-layers the chef's children).
    /// </summary>
    internal sealed class CarryingModule : ISpecialModule
    {
        // Host -> all: Chef = chef, I = entity stashed (0 = none), A = kind (Special), D = entity given back (0 = none).
        internal const ushort TopicStash = (ushort)((int)Special.PackMule * 10 + 0);
        // Owner -> host: Chef = chef, I = held entity, Vector1 = facing.
        internal const ushort TopicToss = (ushort)((int)Special.StrongArm * 10 + 0);
        // Owner -> host: Chef = chef, I = teammate chef, Vector1 = chef's position at the press.
        internal const ushort TopicHandOff = (ushort)((int)Special.HandOff * 10 + 0);
        // Host -> all: Chef = chef whose hand-off was refused, Text = why.
        internal const ushort TopicHandOffDenied = (ushort)((int)Special.HandOff * 10 + 1);
        // Owner -> host: Chef = thief, I = chef it dashed into, Vector1 = thief's position.
        internal const ushort TopicPickpocket = (ushort)((int)Special.Pickpocket * 10 + 0);
        // Host -> all: Chef = chef that refused raw meat or fish, I = what it refused (entity, 0 = none).
        internal const ushort TopicVegetarian = (ushort)((int)Special.Vegetarian * 10 + 0);

        private const int PocketSize = 2;
        private const float TelekinesisRange = 6f;
        private const float TelekinesisArc = 30f;
        private const float TelekinesisWideArc = 60f;
        private const float HandOffRange = 3f;
        private const float HandOffArc = 30f;
        private const float PickpocketRange = 3f;
        private const float TossCatchRadius = 1.8f;
        private const float TossCatchRadiusWide = 2.5f;
        private const float TossForce = 1f;            // of the chef's own throw force (times THROWING)
        // A toss is put onto a station once its underside is this close to the station's top.
        private const float StationProbeUp = 0.3f;
        private const float StationProbeDown = 0.45f;
        private const float TossSeconds = 2.5f;
        private const float TickSeconds = 0.2f;

        private sealed class Stashed
        {
            public GameObject Item;
            public uint Entity;
            public uint Chef;
            public Special Kind;
            public GameObject Visual;
            public ServerLimitedQuantityItem Limited;
            public Transform[] Parts;
            public int[] Layers;
            public List<AnimatorSnapshot> Animators;
        }

        /// <summary>
        /// An animator's parameters at the moment its item was taken out of play. Unity resets an
        /// animator when its object is switched back on, so a cooked pot's soup (driven by a
        /// "progress" parameter) would come back out of the stash looking empty or raw.
        /// </summary>
        private sealed class AnimatorSnapshot
        {
            public Animator Animator;
            public AnimatorControllerParameter[] Parameters;
            public float[] Floats;
            public int[] Ints;
            public bool[] Bools;
        }

        private sealed class Flight
        {
            public GameObject Item;
            public ChefState Thrower;
            public Vector3 From;
            public float StartedAt;
            // The station it came down on was tried once (placed or not): don't try again.
            public bool StationTried;
        }

        // Every stashed item (every machine), in the order it was stashed.
        private static readonly List<Stashed> s_stashed = new List<Stashed>();
        private static readonly List<Stashed> s_scratch = new List<Stashed>();
        // Strong Arm tosses still in the air (host).
        private static readonly List<Flight> s_flights = new List<Flight>();
        private static readonly Dictionary<uint, float> s_deniedAt = new Dictionary<uint, float>();
        // Keeps a stashed item from being removed by the limited-quantity cleanup.
        private static readonly Generic<bool> s_invincible = AlwaysTrue;
        private static float s_nextTick;

        private static readonly Color Refused = new Color(1f, 0.3f, 0.3f);

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
            SpecialRuntime.RegisterActive(Special.Telekinesis, new ActiveHandler { Execute = ExecuteTelekinesis, Targets = TelekinesisTargets });

            SpecialNet.Register(TopicStash, OnStashMessage);
            SpecialNet.Register(TopicToss, OnTossRequest);
            SpecialNet.Register(TopicHandOff, OnHandOffRequest);
            SpecialNet.Register(TopicHandOffDenied, OnHandOffDenied);
            SpecialNet.Register(TopicPickpocket, OnPickpocketRequest);
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

        internal static bool IsEntity(GameObject obj)
        {
            return EntityOf(obj) != 0;
        }

        private static GameObject EntityObject(uint id)
        {
            if (id == 0) return null;
            EntitySerialisationEntry entry = EntitySerialisationRegistry.GetEntry(id);
            return entry != null ? entry.m_GameObject : null;
        }

        private static GameObject ChefObject(uint chef)
        {
            ChefState state = SpecialRuntime.StateById(chef);
            return state != null && state.Chef != null ? state.Chef : EntityObject(chef);
        }

        private static Vector2 FlatForward(Vector3 forward)
        {
            Vector2 xz = new Vector2(forward.x, forward.z);
            return xz.sqrMagnitude > 0.0001f ? xz.normalized : new Vector2(0f, 1f);
        }

        private static bool Respawning(ChefState state)
        {
            return state == null || state.Controls == null || state.Controls.m_bRespawning;
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

        /// <summary>A loose piece of food (what fits in a pocket).</summary>
        private static bool IsIngredient(GameObject item)
        {
            return item != null && item.GetComponent<IngredientPropertiesComponent>() != null && item.GetComponent<ServerPlate>() == null;
        }

        private static string CleanName(GameObject item)
        {
            if (item == null) return "";
            string name = item.name;
            int clone = name.IndexOf("(Clone)", StringComparison.Ordinal);
            if (clone > 0) name = name.Substring(0, clone);
            return name.Replace('_', ' ').Trim();
        }

        // --- stash records (every machine) -------------------------------------------------------

        private static Stashed RecordOf(GameObject item)
        {
            if (item == null) return null;
            for (int i = 0; i < s_stashed.Count; i++)
                if (s_stashed[i].Item == item) return s_stashed[i];
            return null;
        }

        /// <summary>This chef's stashed items of this kind still in play, oldest first.</summary>
        private static List<Stashed> StashOf(uint chef, Special kind)
        {
            var list = new List<Stashed>();
            for (int i = 0; i < s_stashed.Count; i++)
            {
                Stashed record = s_stashed[i];
                if (record.Chef == chef && record.Kind == kind && record.Item != null) list.Add(record);
            }
            return list;
        }

        private static void Forget(Stashed record)
        {
            if (record == null) return;
            DestroyVisual(record);
            s_stashed.Remove(record);
        }

        private static void DestroyVisual(Stashed record)
        {
            if (record != null && record.Visual != null) Object.Destroy(record.Visual);
            if (record != null) record.Visual = null;
        }

        /// <summary>Hides a stashed item on this machine and records it (idempotent).</summary>
        private static Stashed HideLocal(uint chef, GameObject item, Special kind)
        {
            Stashed record = RecordOf(item);
            if (record == null)
            {
                record = new Stashed { Item = item, Entity = EntityOf(item), Chef = chef, Kind = kind };
                s_stashed.Add(record);
            }
            else if (record.Chef != chef || record.Kind != kind)
            {
                DestroyVisual(record);
                record.Chef = chef;
                record.Kind = kind;
            }
            if (item.activeSelf)
            {
                // A respawn re-layers everything under the chef: remember the item's own layers.
                record.Parts = item.GetComponentsInChildren<Transform>(true);
                record.Layers = new int[record.Parts.Length];
                for (int i = 0; i < record.Parts.Length; i++) record.Layers[i] = record.Parts[i].gameObject.layer;
                record.Animators = SnapshotAnimators(item);
                // Copy the look while it is still visible, then take it out of play.
                if (record.Visual == null && kind != Special.Pocket) record.Visual = BuildVisual(ChefObject(chef), item, kind);
                item.SetActive(false);
            }
            return record;
        }

        /// <summary>Shows a stashed item again on this machine and forgets it (idempotent). Returns its chef (0 if none).</summary>
        private static uint RevealLocal(GameObject item)
        {
            if (item == null) return 0;
            Stashed record = RecordOf(item);
            uint chef = 0;
            List<AnimatorSnapshot> animators = null;
            if (record != null)
            {
                chef = record.Chef;
                animators = record.Animators;
                if (record.Parts != null)
                    for (int i = 0; i < record.Parts.Length; i++)
                        if (record.Parts[i] != null && record.Parts[i].gameObject.layer != record.Layers[i])
                            record.Parts[i].gameObject.layer = record.Layers[i];
                Forget(record);
            }
            if (!item.activeSelf)
            {
                item.SetActive(true);
                RestoreAnimators(animators);
            }
            return chef;
        }

        private static List<AnimatorSnapshot> SnapshotAnimators(GameObject item)
        {
            List<AnimatorSnapshot> list = null;
            try
            {
                foreach (Animator animator in item.GetComponentsInChildren<Animator>(true))
                {
                    if (animator == null || animator.runtimeAnimatorController == null || !animator.isActiveAndEnabled) continue;
                    AnimatorControllerParameter[] parameters = animator.parameters;
                    if (parameters == null || parameters.Length == 0) continue;
                    var snapshot = new AnimatorSnapshot
                    {
                        Animator = animator,
                        Parameters = parameters,
                        Floats = new float[parameters.Length],
                        Ints = new int[parameters.Length],
                        Bools = new bool[parameters.Length]
                    };
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        int hash = parameters[i].nameHash;
                        switch (parameters[i].type)
                        {
                            case AnimatorControllerParameterType.Float: snapshot.Floats[i] = animator.GetFloat(hash); break;
                            case AnimatorControllerParameterType.Int: snapshot.Ints[i] = animator.GetInteger(hash); break;
                            case AnimatorControllerParameterType.Bool: snapshot.Bools[i] = animator.GetBool(hash); break;
                        }
                    }
                    if (list == null) list = new List<AnimatorSnapshot>();
                    list.Add(snapshot);
                }
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("PackMule.Animators", error);
            }
            return list;
        }

        private static void RestoreAnimators(List<AnimatorSnapshot> list)
        {
            if (list == null) return;
            try
            {
                for (int s = 0; s < list.Count; s++)
                {
                    AnimatorSnapshot snapshot = list[s];
                    Animator animator = snapshot.Animator;
                    if (animator == null || !animator.isActiveAndEnabled || animator.runtimeAnimatorController == null) continue;
                    for (int i = 0; i < snapshot.Parameters.Length; i++)
                    {
                        int hash = snapshot.Parameters[i].nameHash;
                        switch (snapshot.Parameters[i].type)
                        {
                            case AnimatorControllerParameterType.Float: animator.SetFloat(hash, snapshot.Floats[i]); break;
                            case AnimatorControllerParameterType.Int: animator.SetInteger(hash, snapshot.Ints[i]); break;
                            case AnimatorControllerParameterType.Bool: animator.SetBool(hash, snapshot.Bools[i]); break;
                        }
                    }
                    // Jump straight to the pose those values give, rather than animating back to it.
                    animator.Update(1f);
                }
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("PackMule.Animators", error);
            }
        }

        /// <summary>The local player's skill bar: how many items are stored, and what.</summary>
        private static void UpdateHud(ChefState state)
        {
            if (state == null || !state.Local) return;
            string status = null;
            if (state.Has(Special.PackMule) || state.Has(Special.Pocket))
            {
                Special kind = state.Has(Special.Pocket) ? Special.Pocket : Special.PackMule;
                List<Stashed> list = StashOf(state.Id, kind);
                state.SetCount(kind, list.Count);
                if (list.Count > 0)
                {
                    var names = new List<string>();
                    for (int i = 0; i < list.Count; i++) names.Add(CleanName(list[i].Item));
                    status = (kind == Special.Pocket ? Lang.T("Pocket: ", "Túi: ") : Lang.T("Back: ", "Lưng: ")) + string.Join(", ", names.ToArray());
                }
            }
            if (state.Has(Special.Waiter)) state.SetCount(Special.Waiter, StashOf(state.Id, Special.Waiter).Count > 0 ? 2 : 0);
            state.SetStatus("Carrying", status, state.Has(Special.Pocket) ? Special.Pocket : Special.PackMule);
        }

        // --- stash: host side ---------------------------------------------------------------------

        /// <summary>Can this held item be stashed (a normal networked attachment)?</summary>
        private static bool Stashable(GameObject item)
        {
            if (item == null || EntityOf(item) == 0) return false;
            if (item.GetComponent<ServerPhysicalAttachment>() == null) return false;
            // A backpack lives in the Back slot; never in the hand, but be safe.
            return item.GetComponent<ServerBackpack>() == null;
        }

        /// <summary>Host: moves the chef's held item into its stash. Doesn't broadcast.</summary>
        private static Stashed ServerStashHeld(ChefState state, ServerPlayerAttachmentCarrier carrier, Special kind)
        {
            GameObject item = carrier.InspectCarriedItem();
            if (!Stashable(item)) return null;
            PlayerAttachmentCarrier parent = state.Chef.GetComponent<PlayerAttachmentCarrier>();
            IAttachment attachment = item.GetComponent<IAttachment>();
            if (parent == null || attachment == null) return null;

            carrier.TakeItem();
            // Attached to the chef but in no carrier slot: kinematic, "held", follows the chef.
            if (attachment.IsAttached()) attachment.Detach();
            attachment.Attach(parent);
            Stashed record = HideLocal(state.Id, item, kind);
            if (record.Limited == null)
            {
                record.Limited = item.GetComponent<ServerLimitedQuantityItem>();
                if (record.Limited != null) record.Limited.AddInvincibilityCondition(s_invincible);
            }
            return record;
        }

        /// <summary>
        /// Host: takes an item out of the stash into the chef's hand (or, with no carrier or a full
        /// hand, leaves it loose where it is). Doesn't broadcast. Returns its entity.
        /// </summary>
        private static uint ServerGiveBack(ServerPlayerAttachmentCarrier carrier, Stashed record)
        {
            GameObject item = record.Item;
            uint id = record.Entity;
            if (record.Limited != null) record.Limited.RemoveInvincibilityCondition(s_invincible);
            record.Limited = null;
            if (item == null)
            {
                Forget(record);
                return id;
            }
            RevealLocal(item);
            IAttachment attachment = item.GetComponent<IAttachment>();
            if (attachment != null && attachment.IsAttached()) attachment.Detach();
            // Same as the game's own pickup of a loose item (ServerCarryableItem.HandlePickup).
            if (carrier != null && carrier.InspectCarriedItem() == null) carrier.CarryItem(item);
            return id;
        }

        private static void BroadcastStash(uint chef, uint stashed, Special kind, uint revealed)
        {
            SpecialNet.SendToAll(new SpecialMessage
            {
                Topic = TopicStash, Chef = chef, I = (int)stashed, A = (int)kind, D = revealed
            });
        }

        /// <summary>Host: a chef is being removed (its player left): its stashed items drop where it stands.</summary>
        internal static void ServerReleaseChef(uint chef)
        {
            if (chef == 0 || s_stashed.Count == 0) return;
            s_scratch.Clear();
            for (int i = 0; i < s_stashed.Count; i++)
                if (s_stashed[i].Chef == chef) s_scratch.Add(s_stashed[i]);
            for (int i = 0; i < s_scratch.Count; i++)
            {
                Stashed record = s_scratch[i];
                uint revealed = ServerGiveBack(null, record);
                BroadcastStash(chef, 0, record.Kind, revealed);
            }
            s_scratch.Clear();
        }

        /// <summary>
        /// PACK MULE (one item of any kind, shown on the back) and POCKET (up to two ingredients,
        /// hidden): EMOTE stores what the hands hold, or takes the last stored item out into empty
        /// hands; when full, it swaps.
        /// </summary>
        private static bool ExecuteSwap(ChefState state)
        {
            ServerPlayerAttachmentCarrier carrier = ServerCarrier(state);
            if (carrier == null || state.Controls == null) return state.Refuse(Lang.Both("NOT READY", "CHƯA SẴN SÀNG"));
            if (state.Controls.m_bRespawning) return state.Refuse(Lang.Both("RESPAWNING", "ĐANG HỒI SINH"));
            // The active being used (a chef can have two; only one carry active, see the Carry group).
            Special kind = state.Using == Special.Pocket ? Special.Pocket : Special.PackMule;
            int capacity = kind == Special.Pocket ? PocketSize : 1;
            GameObject hand = carrier.InspectCarriedItem();
            List<Stashed> stored = StashOf(state.Id, kind);

            if (hand == null)
            {
                if (stored.Count == 0) return state.Refuse(kind == Special.Pocket ? Lang.Both("POCKET IS EMPTY", "TÚI ĐANG TRỐNG") : Lang.Both("NOTHING TO SWAP", "KHÔNG CÓ GÌ ĐỂ ĐỔI"));
                // Taking something into the hands mid-chop would confuse the station.
                if (Busy(state)) return state.Refuse(Lang.Both("HANDS BUSY", "TAY ĐANG BẬN"));
                uint given = ServerGiveBack(carrier, stored[stored.Count - 1]);
                BroadcastStash(state.Id, 0, kind, given);
                return true;
            }

            if (!Stashable(hand)) return state.Refuse(Lang.Both("CAN'T STORE THAT", "KHÔNG CẤT ĐƯỢC"));
            if (kind == Special.Pocket && !IsIngredient(hand)) return state.Refuse(Lang.Both("ONLY FOOD FITS", "CHỈ CẤT ĐƯỢC ĐỒ ĂN"));
            Stashed swapWith = stored.Count >= capacity ? stored[stored.Count - 1] : null;
            if (ServerStashHeld(state, carrier, kind) == null) return state.Refuse(Lang.Both("CAN'T STORE THAT", "KHÔNG CẤT ĐƯỢC"));
            uint revealed = swapWith != null ? ServerGiveBack(carrier, swapWith) : 0;
            BroadcastStash(state.Id, EntityOf(hand), kind, revealed);
            return true;
        }

        // --- stash: every machine -----------------------------------------------------------------

        private static void OnStashMessage(SpecialMessage message)
        {
            if (!SpecialRuntime.InLevel) return;
            Special kind = (Special)(int)message.A;
            uint revealed = (uint)message.D;
            GameObject shown = EntityObject(revealed);
            uint previousOwner = RevealLocal(shown);
            GameObject item = message.I != 0 ? EntityObject((uint)message.I) : null;
            if (item != null) HideLocal(message.Chef, item, kind);

            ChefState state = SpecialRuntime.StateById(message.Chef);
            UpdateHud(state);
            if (previousOwner != 0 && previousOwner != message.Chef) UpdateHud(SpecialRuntime.StateById(previousOwner));

            // What every player sees: the item that comes out gets a ring; a dish going up the
            // Waiter's arm says "x2" (actives already get a burst and their name from the framework).
            if (shown != null && shown.activeInHierarchy) SpecialFx.Mark(shown, kind, 1f, null);
            if (item != null && kind == Special.Waiter && state != null && state.Chef != null)
                SpecialFx.Text(state.Chef, "x2", SpecialFx.ColourOf(Special.Waiter));
        }

        /// <summary>
        /// A mesh-only copy of the item (no colliders, no scripts) on the chef's back (Pack Mule)
        /// or balanced above its hand (Waiter). Purely cosmetic and local.
        /// </summary>
        private static GameObject BuildVisual(GameObject chefObject, GameObject item, Special kind)
        {
            if (chefObject == null || item == null) return null;
            try
            {
                Transform chef = chefObject.transform;
                PlayerAttachmentCarrier carrier = chefObject.GetComponent<PlayerAttachmentCarrier>();
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

                // A copy of the whole item exactly as it looks now: every child (the food in a pot,
                // the dish on a plate, the contents' colour, burnt bits), skinned and animated
                // parts frozen in their current pose, the same materials. Copied under an inactive
                // holder so none of its scripts ever wake up (no entity, no physics, no sync), then
                // stripped down to what draws it.
                GameObject holder = new GameObject("ChefSkills Stash Holder");
                holder.SetActive(false);
                try
                {
                    GameObject copy = Object.Instantiate(item, holder.transform, false);
                    copy.name = "look";
                    StripToLook(copy);
                    Vector3 itemScale = item.transform.lossyScale, chefScale = chef.lossyScale;
                    copy.transform.SetParent(root.transform, false);
                    copy.transform.localPosition = Vector3.zero;
                    copy.transform.localRotation = Quaternion.identity;
                    // The item's own size in the hand, times the back's scale.
                    copy.transform.localScale = new Vector3(
                        itemScale.x / Mathf.Max(0.0001f, chefScale.x),
                        itemScale.y / Mathf.Max(0.0001f, chefScale.y),
                        itemScale.z / Mathf.Max(0.0001f, chefScale.z));
                }
                finally
                {
                    Object.Destroy(holder);
                }
                return root;
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("PackMule.Visual", error);
                return null;
            }
        }

        /// <summary>
        /// Removes everything but transforms, mesh filters and mesh/skinned renderers from a copy
        /// that has never been active. Dependent components come after what they need on an
        /// object, so each object is stripped back to front; a few passes catch the rest.
        /// </summary>
        private static void StripToLook(GameObject copy)
        {
            for (int pass = 0; pass < 4; pass++)
            {
                bool left = false;
                foreach (Transform part in copy.GetComponentsInChildren<Transform>(true))
                {
                    Component[] components = part.GetComponents<Component>();
                    for (int i = components.Length - 1; i >= 0; i--)
                    {
                        Component component = components[i];
                        if (component == null || component is Transform || component is MeshFilter ||
                            component is MeshRenderer || component is SkinnedMeshRenderer) continue;
                        // Scripts first: they are what the engine's own components are required by.
                        if (pass == 0 && !(component is MonoBehaviour)) { left = true; continue; }
                        try
                        {
                            Object.DestroyImmediate(component);
                        }
                        catch (Exception)
                        {
                        }
                        if (component != null) left = true;
                    }
                }
                if (!left) break;
            }
            // Anything that still couldn't be removed must never wake up: hide that part instead.
            foreach (Transform part in copy.GetComponentsInChildren<Transform>(true))
            {
                if (part == null) continue;
                Component[] components = part.GetComponents<Component>();
                for (int i = 0; i < components.Length; i++)
                {
                    Component component = components[i];
                    if (component == null || component is Transform || component is MeshFilter ||
                        component is MeshRenderer || component is SkinnedMeshRenderer) continue;
                    part.gameObject.SetActive(false);
                    break;
                }
            }
        }

        private static void Clear()
        {
            foreach (Stashed record in s_stashed) DestroyVisual(record);
            s_stashed.Clear();
            s_scratch.Clear();
            s_flights.Clear();
            s_deniedAt.Clear();
            s_nextTick = 0f;
            WaiterPatch.Reentered = false;
        }

        private static void OnTick(float deltaTime)
        {
            bool server = SpecialNet.IsServer;
            if (server && s_flights.Count > 0) UpdateFlights();
            if (s_stashed.Count == 0) return;
            if (SpecialRuntime.Now < s_nextTick) return;
            s_nextTick = SpecialRuntime.Now + TickSeconds;

            s_scratch.Clear();
            s_scratch.AddRange(s_stashed);
            for (int i = 0; i < s_scratch.Count; i++)
            {
                Stashed record = s_scratch[i];
                if (!s_stashed.Contains(record)) continue;
                ChefState state = SpecialRuntime.StateById(record.Chef);
                if (record.Item == null)
                {
                    // Destroyed (on the host too, so on every machine): nothing to give back.
                    Forget(record);
                    UpdateHud(state);
                    continue;
                }
                if (state == null || state.Chef == null)
                {
                    // The chef is gone: the host lets its items drop; clients wait for that.
                    if (server)
                    {
                        uint dropped = ServerGiveBack(null, record);
                        BroadcastStash(record.Chef, 0, record.Kind, dropped);
                    }
                    continue;
                }

                // The copy disappears with the chef while it respawns.
                bool respawning = Respawning(state);
                if (record.Visual != null && record.Visual.activeSelf == respawning) record.Visual.SetActive(!respawning);
                if (record.Kind == Special.Waiter && !respawning) SpecialFx.Mark(state.Chef, Special.Waiter, 0.5f, "x2");

                if (!server) continue;
                if (!record.Item.transform.IsChildOf(state.Chef.transform))
                {
                    // Something else took it: let it be a normal item again.
                    if (record.Limited != null) record.Limited.RemoveInvincibilityCondition(s_invincible);
                    record.Limited = null;
                    RevealLocal(record.Item);
                    BroadcastStash(record.Chef, 0, record.Kind, record.Entity);
                    continue;
                }
                // WAITER: once the hands are free, the second dish comes down into them.
                if (record.Kind == Special.Waiter && !Busy(state))
                {
                    ServerPlayerAttachmentCarrier carrier = ServerCarrier(state);
                    if (carrier != null && carrier.InspectCarriedItem() == null)
                    {
                        uint revealed = ServerGiveBack(carrier, record);
                        BroadcastStash(record.Chef, 0, Special.Waiter, revealed);
                    }
                }
            }
            s_scratch.Clear();
        }

        // --- WAITER -------------------------------------------------------------------------------

        /// <summary>
        /// Host, inside a failed "put down" of a dish onto another dish: the held dish goes up on
        /// the arm and the other one is picked up instead.
        /// </summary>
        internal static bool TryWaiterStack(ChefState state, PlayerControls controls, GameObject held, GameObject target)
        {
            ServerPlayerAttachmentCarrier carrier = ServerCarrier(state);
            if (carrier == null || carrier.InspectCarriedItem() != held || Respawning(state)) return false;
            if (StashOf(state.Id, Special.Waiter).Count > 0) return false;
            Vector2 direction = FlatForward(controls.transform.forward);
            IHandlePlacement placement = PlayerControlsHelper.GetControllingPlacementHandler_Server(target);
            if (placement != null && placement.CanHandlePlacement(carrier, direction, new PlacementContext(PlacementContext.Source.Player)))
                return false; // the game can do something with it: let it
            GameObject other = DishAt(target);
            if (other == null || other == held || !IsDish(other)) return false;
            IHandlePickup pickup = PlayerControlsHelper.GetControllingPickupHandler_Server(other);
            if (pickup == null) return false;

            Stashed record = ServerStashHeld(state, carrier, Special.Waiter);
            if (record == null) return false;
            if (pickup.CanHandlePickup(carrier)) pickup.HandlePickup(carrier, direction);
            if (carrier.InspectCarriedItem() == other)
            {
                BroadcastStash(state.Id, EntityOf(held), Special.Waiter, 0);
                return true;
            }
            // Couldn't take the other dish: put ours back in the hand as if nothing happened
            // (nothing was broadcast, and the game's carry messages cancel out).
            ServerGiveBack(carrier, record);
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

        /// <summary>
        /// Host, after a dish went onto the pass: hand in the stashed dish too, but only if it
        /// matches a live order (a wrong dish would cost the combo); otherwise it comes down into
        /// the hands.
        /// </summary>
        internal static void WaiterDeliverSecond(ChefState state, PlayerControls controls, GameObject target)
        {
            List<Stashed> stored = StashOf(state.Id, Special.Waiter);
            if (stored.Count == 0 || !IsDish(stored[0].Item)) return;
            ServerPlayerAttachmentCarrier carrier = ServerCarrier(state);
            if (carrier == null || carrier.InspectCarriedItem() != null) return;
            GameObject dish = stored[0].Item;
            bool wanted = WouldBeOrdered(dish, target);
            uint revealed = ServerGiveBack(carrier, stored[0]);
            BroadcastStash(state.Id, 0, Special.Waiter, revealed);
            if (carrier.InspectCarriedItem() != dish) return;
            if (!wanted)
            {
                SpecialFx.TextAll(state.Chef, Lang.Both("NOT ORDERED", "KHÔNG AI GỌI"), Special.Waiter);
                return;
            }
            PlayerControlsHelper.PlaceHeldItem_Server(controls, target);
            if (carrier.InspectCarriedItem() == null) SpecialFx.TextAll(state.Chef, Lang.Both("x2 SERVED!", "x2 ĐÃ GIAO!"), Special.Waiter);
        }

        /// <summary>
        /// Host: would this dish, handed in at this pass now, be taken as an order? The same rule
        /// as the game's (ServerOrderControllerBase.Matches) over the pass's team's live orders
        /// (not decoys), or CLOSE ENOUGH ready to take a dish of the level.
        /// </summary>
        private static bool WouldBeOrdered(GameObject dish, GameObject target)
        {
            ServerPlate plate = dish != null ? dish.GetComponent<ServerPlate>() : null;
            ServerPlateStation pass = target != null ? target.GetComponentInParent<ServerPlateStation>() : null;
            if (plate == null || pass == null || !OrdersModule.ServerFlowReady()) return false;
            TeamID team = OrdersModule.Versus && pass.GetTeamID() == TeamID.Two ? TeamID.Two : TeamID.One;
            List<OrderController.ServerOrderData> orders = OrdersModule.ServerOrders(OrdersModule.ServerController(team));
            if (orders == null || orders.Count == 0) return false;
            AssembledDefinitionNode composition = plate.GetOrderComposition();
            PlatingStepData plating = plate.GetPlatingStep();
            if (composition == null) return false;
            int t = OrdersModule.Idx(team);
            for (int i = 0; i < orders.Count; i++)
            {
                OrderController.ServerOrderData order = orders[i];
                if (order == null || order.RecipeListEntry == null) continue;
                OrderDefinitionNode recipe = order.RecipeListEntry.m_order;
                if (recipe == null || recipe.m_platingStep != plating) continue;
                if (OrdersModule.IsDecoy(t, order.ID.m_id)) continue;
                bool match = recipe is WildcardOrderNode
                    ? AssembledDefinitionNode.Matching(recipe, composition)
                    : AssembledDefinitionNode.Matching(composition, recipe);
                if (match) return true;
            }
            return OrdersModule.CloseEnoughReady(team) && OrdersModule.IsLevelRecipe(composition, plating);
        }

        // --- TELEKINESIS --------------------------------------------------------------------------

        private static bool ExecuteTelekinesis(ChefState state)
        {
            ServerPlayerAttachmentCarrier carrier = ServerCarrier(state);
            if (carrier == null || state.Controls == null) return state.Refuse(Lang.Both("NOT READY", "CHƯA SẴN SÀNG"));
            if (state.Controls.m_bRespawning) return state.Refuse(Lang.Both("RESPAWNING", "ĐANG HỒI SINH"));
            if (carrier.InspectCarriedItem() != null) return state.Refuse(Lang.Both("HANDS FULL", "TAY ĐANG CẦM ĐỒ"));
            if (Busy(state)) return state.Refuse(Lang.Both("HANDS BUSY", "TAY ĐANG BẬN"));
            // Aim from where the player saw their chef (the host's view of a remote one lags).
            Vector3 forward = state.RequestForward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) forward = state.Forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) return state.Refuse(Lang.Both("NOTHING TO PULL", "KHÔNG CÓ GÌ ĐỂ KÉO"));
            forward.Normalize();
            Vector3 origin = state.RequestPosition.sqrMagnitude > 0.0001f ? state.RequestPosition : state.Position;

            Vector3 from;
            IHandlePickup best = FindPull(state, carrier, origin, forward, TelekinesisArc, out from);
            if (best == null && !state.HasAimPoint) best = FindPull(state, carrier, origin, forward, TelekinesisWideArc, out from);
            if (best == null) return state.Refuse(Lang.Both("NOTHING TO PULL", "KHÔNG CÓ GÌ ĐỂ KÉO"));
            // The game's own pickup, just from further away: counter, floor or crate alike.
            best.HandlePickup(carrier, FlatForward(forward));
            GameObject pulled = carrier.InspectCarriedItem();
            if (pulled == null) return state.Refuse(Lang.Both("NOTHING TO PULL", "KHÔNG CÓ GÌ ĐỂ KÉO"));
            SpecialFx.BurstAll(from, Special.Telekinesis);
            SpecialFx.BeamAll(from, state.Position, Special.Telekinesis, 0.6f);
            SpecialFx.MarkAll(pulled, Special.Telekinesis, 0.8f, null);
            return true;
        }

        private static IHandlePickup FindPull(ChefState state, ServerPlayerAttachmentCarrier carrier, Vector3 origin,
            Vector3 forward, float arc, out Vector3 from)
        {
            from = origin;
            Collider[] colliders = Physics.OverlapSphere(origin + forward * (TelekinesisRange * 0.5f),
                TelekinesisRange * 0.5f + 0.4f, ~0, QueryTriggerInteraction.Collide);
            var seen = new HashSet<int>();
            IHandlePickup best = null;
            float bestScore = float.MaxValue;
            bool vegetarian = state.Has(Special.Vegetarian);
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider == null || !collider.enabled) continue;
                GameObject candidate = PickupObjectOf(collider.transform);
                if (candidate == null || !seen.Add(candidate.GetInstanceID())) continue;

                Vector3 offset = candidate.transform.position - origin;
                offset.y = 0f;
                float distance = offset.magnitude;
                float score;
                if (state.HasAimPoint)
                {
                    // The one the player chose: nearest the chosen spot, in reach whichever way.
                    float off = state.AimDistance(candidate.transform.position);
                    if (distance > TelekinesisRange + 0.6f || off > 0.9f) continue;
                    score = off;
                }
                else
                {
                    if (distance > TelekinesisRange) continue;
                    float angle = distance > 0.5f ? Vector3.Angle(forward, offset) : 0f;
                    if (angle > arc) continue;
                    score = distance + angle * 0.08f;
                }

                IHandlePickup handler = PlayerControlsHelper.GetControllingPickupHandler_Server(candidate);
                if (handler == null || !handler.CanHandlePickup(carrier)) continue;
                if (vegetarian && IsRawMeat(PickupItemOf(candidate))) continue;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = handler;
                    from = candidate.transform.position;
                }
            }
            return best;
        }

        /// <summary>
        /// The player's machine: what TELEKINESIS could pull (anything that can be picked up within
        /// reach: loose items, items on counters, crates), the one it picks anyway first.
        /// </summary>
        private static List<AimTarget> TelekinesisTargets(ChefState state)
        {
            var targets = new List<AimTarget>();
            ClientPlayerAttachmentCarrier carrier = state.Chef != null ? state.Chef.GetComponent<ClientPlayerAttachmentCarrier>() : null;
            if (carrier == null || carrier.InspectCarriedItem() != null) return targets;
            Vector3 origin = state.Position, forward = state.Forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.forward;
            var seen = new HashSet<int>();
            int best = -1;
            float bestScore = float.MaxValue, bestWide = float.MaxValue;
            int bestWideIndex = -1;
            foreach (Collider collider in Physics.OverlapSphere(origin, TelekinesisRange + 0.4f, ~0, QueryTriggerInteraction.Collide))
            {
                if (collider == null || !collider.enabled) continue;
                GameObject candidate = ClientPickupObjectOf(collider.transform);
                if (candidate == null || !seen.Add(candidate.GetInstanceID())) continue;
                Vector3 offset = candidate.transform.position - origin;
                offset.y = 0f;
                float distance = offset.magnitude;
                if (distance > TelekinesisRange) continue;
                IClientHandlePickup handler = PlayerControlsHelper.GetControllingPickupHandler_Client(candidate);
                if (handler == null || !handler.CanHandlePickup(carrier)) continue;
                targets.Add(AimTarget.ForObject(candidate));
                // The one it picks anyway: the same score as the host's, narrow arc before wide.
                float angle = distance > 0.5f ? Vector3.Angle(forward, offset) : 0f;
                float score = distance + angle * 0.08f;
                if (angle <= TelekinesisArc && score < bestScore)
                {
                    bestScore = score;
                    best = targets.Count - 1;
                }
                if (angle <= TelekinesisWideArc && score < bestWide)
                {
                    bestWide = score;
                    bestWideIndex = targets.Count - 1;
                }
            }
            if (best >= 0) SpecialAim.MoveFirst(targets, best);
            else if (bestWideIndex >= 0) SpecialAim.MoveFirst(targets, bestWideIndex);
            else SpecialAim.FacedFirst(targets, origin, forward, 180f);
            return targets;
        }

        private static GameObject ClientPickupObjectOf(Transform transform)
        {
            for (int depth = 0; transform != null && depth < 6; depth++, transform = transform.parent)
            {
                if (transform.GetComponent<PlayerControls>() != null) return null;
                if (transform.GetComponent<IClientHandlePickup>() != null) return transform.gameObject;
            }
            return null;
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
        /// filled wrap): it leaves the hands as a loose item flying at throw speed, steered to a
        /// teammate it was aimed at. A teammate it flies into (with empty hands) catches it;
        /// otherwise it lands where it lands.
        /// </summary>
        private static void OnTossRequest(SpecialMessage message)
        {
            if (!SpecialRuntime.InLevel || !SpecialNet.IsServer) return;
            ChefState state = SpecialRuntime.StateById(message.Chef);
            if (state == null || !state.Has(Special.StrongArm) || Respawning(state)) return;
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
            force *= Plugin.Skills.Multiplier(state.Chef, Skill.Throwing) * TossForce;
            Vector3 velocity = new Vector3(direction.x * force, Mathf.Tan(inclination * Mathf.Deg2Rad) * force, direction.y * force);

            Vector3 from = item.transform.position;
            carrier.TakeItem();
            attachment.AccessMotion().SetVelocity(velocity);
            s_flights.Add(new Flight { Item = item, Thrower = state, From = from, StartedAt = SpecialRuntime.Now });
            SpecialFx.MarkAll(item, Special.StrongArm, 1.2f, null);
            SpecialFx.BurstAll(state.Position, Special.StrongArm);
            // The thrower's other throwing specials (Juggler, Homing Throw, Lob, Rebound) shape a
            // toss exactly as they shape a normal throw. Without Homing Throw (or with nobody in
            // its reach), a teammate the toss was aimed at within 8 m gets it steered to them the
            // same way.
            ThrowingModule.OnTossed(state, item, attachment);
        }

        /// <summary>Host, every frame while something tossed is in the air: catches and landings.</summary>
        private static void UpdateFlights()
        {
            for (int i = s_flights.Count - 1; i >= 0; i--)
            {
                Flight flight = s_flights[i];
                GameObject item = flight.Item;
                IAttachment attachment = item != null ? item.GetComponent<IAttachment>() : null;
                if (item == null || attachment == null || attachment.IsAttached() || !item.activeInHierarchy)
                {
                    s_flights.RemoveAt(i);
                    continue;
                }
                float age = SpecialRuntime.Now - flight.StartedAt;
                if (age > 0.12f && TryCatch(flight, item))
                {
                    s_flights.RemoveAt(i);
                    continue;
                }
                if (age > 0.1f && !flight.StationTried && TryLandOnStation(flight, item, attachment))
                {
                    s_flights.RemoveAt(i);
                    continue;
                }
                RigidbodyMotion motion = attachment.AccessMotion();
                bool settled = age > 0.35f && motion != null && motion.GetVelocity().sqrMagnitude < 0.25f;
                if (settled || age > TossSeconds)
                {
                    SpecialFx.BurstAll(item.transform.position, Special.StrongArm);
                    s_flights.RemoveAt(i);
                }
            }
        }

        private static bool TryCatch(Flight flight, GameObject item)
        {
            if (flight.Thrower == null) return false;
            Vector3 at = item.transform.position;
            foreach (ChefState mate in SpecialRuntime.Teammates(flight.Thrower))
            {
                if (mate.Chef == null || Respawning(mate) || mate.Has(Special.Butterfingers)) continue;
                Vector3 offset = at - mate.Position;
                if (offset.y < -0.6f || offset.y > 2.4f) continue;
                offset.y = 0f;
                // Catcher's Mitt, and the teammate an aimed toss (Strong Arm's own aim or Homing
                // Throw) is flying to, reach further (as they do for a normal throw).
                bool wide = mate.Has(Special.CatchersMitt) || ThrowingModule.AimedAt(item, mate.Chef);
                if (offset.magnitude > (wide ? TossCatchRadiusWide : TossCatchRadius)) continue;
                ServerPlayerAttachmentCarrier carrier = ServerCarrier(mate);
                if (carrier == null || carrier.InspectCarriedItem() != null || Busy(mate)) continue;
                if (mate.Has(Special.Vegetarian) && IsRawMeat(item)) continue;
                IHandlePickup handler = PlayerControlsHelper.GetControllingPickupHandler_Server(item);
                if (handler == null || !handler.CanHandlePickup(carrier)) continue;
                handler.HandlePickup(carrier, FlatForward(-new Vector3(offset.x, 0f, offset.z)));
                if (carrier.InspectCarriedItem() != item) continue;
                SpecialFx.BeamAll(flight.From, mate.Position, Special.StrongArm, 0.6f);
                SpecialFx.TextAll(mate.Chef, Lang.Both("CAUGHT!", "BẮT ĐƯỢC!"), Special.StrongArm);
                // Juggler's window, the aimed throw's arrival burst.
                ThrowingModule.OnTossCaught(mate, item);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Host: a toss coming down onto a station (counter, cooker, mixer, sink, the pass...) is
        /// put there, as if the thrower had put it down by hand: the game's own placement (so a
        /// dish on the pass is handed in, a pot on a cooker cooks), and every effect that cares
        /// who put it there (COOKING, SERVING, whose pot, tips and deliveries) credits the thrower.
        /// </summary>
        private static bool TryLandOnStation(Flight flight, GameObject item, IAttachment attachment)
        {
            ChefState thrower = flight.Thrower;
            if (thrower == null || thrower.Chef == null) return false;
            RigidbodyMotion motion = attachment.AccessMotion();
            if (motion == null) return false;
            Vector3 velocity = motion.GetVelocity();
            // Still rising: it is going over whatever is below it. Flying to a teammate (Homing
            // Throw): it is meant for them, not for the counters next to them.
            if (velocity.y > 0.5f || ThrowingModule.HomingOnSomeone(item)) return false;

            Vector3 at = item.transform.position;
            RaycastHit[] hits = Physics.RaycastAll(at + Vector3.up * StationProbeUp, Vector3.down,
                StationProbeUp + StationProbeDown, ~0, QueryTriggerInteraction.Collide);
            if (hits == null || hits.Length == 0) return false;
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            for (int i = 0; i < hits.Length; i++)
            {
                Collider collider = hits[i].collider;
                if (collider == null || collider.transform.IsChildOf(item.transform)) continue;
                GameObject target = PlacementObjectOf(collider.transform);
                if (target == null)
                {
                    // Something solid that takes nothing (floor, wall, a chef): it lands there.
                    if (!collider.isTrigger) return false;
                    continue;
                }
                IHandlePlacement handler = PlayerControlsHelper.GetControllingPlacementHandler_Server(target);
                if (handler == null) continue;
                Vector2 direction = FlatForward(new Vector3(velocity.x, 0f, velocity.z));
                var hands = new TossCarrier(item, thrower.Chef);
                var context = new PlacementContext(PlacementContext.Source.Player);
                if (!handler.CanHandlePlacement(hands, direction, context)) return false;
                flight.StationTried = true;

                GameObject placer = LevelState.CurrentPlacer;
                LevelState.CurrentPlacer = thrower.Chef;
                try
                {
                    handler.HandlePlacement(hands, direction, context);
                }
                finally
                {
                    LevelState.CurrentPlacer = placer;
                }
                // The station may have used it without taking it (food poured onto a plate): then
                // it keeps flying, as a pot stays in the hands when a chef does that.
                if (hands.InspectCarriedItem() == item) return false;
                PlacementPatch.Credit(thrower.Chef, item, target);
                CookingPlacementPatch.Credit(thrower.Chef, item, target);
                Vector3 where = item != null && item.activeInHierarchy ? item.transform.position : at;
                SpecialFx.BeamAll(flight.From, where, Special.StrongArm, 0.5f);
                SpecialFx.BurstAll(where, Special.StrongArm);
                return true;
            }
            return false;
        }

        /// <summary>The object a placement onto this collider would be aimed at (null: none, or part of a chef).</summary>
        private static GameObject PlacementObjectOf(Transform transform)
        {
            for (int depth = 0; transform != null && depth < 6; depth++, transform = transform.parent)
            {
                if (transform.GetComponent<PlayerControls>() != null) return null;
                if (transform.GetComponent<IHandlePlacement>() != null || transform.GetComponent<ServerHandlePlacementReferral>() != null)
                    return transform.gameObject;
            }
            return null;
        }

        /// <summary>
        /// The "hands" a landing toss is put down from: it holds the flying item until a station
        /// takes it, and stands for the thrower (stations that ask who placed it get the thrower).
        /// </summary>
        private sealed class TossCarrier : ICarrier
        {
            private GameObject m_item;
            private readonly GameObject m_chef;

            public TossCarrier(GameObject item, GameObject chef)
            {
                m_item = item;
                m_chef = chef;
            }

            public GameObject InspectCarriedItem()
            {
                return m_item;
            }

            public GameObject TakeItem()
            {
                GameObject item = m_item;
                m_item = null;
                return item;
            }

            public void DestroyCarriedItem()
            {
                // As the game's own hands do: the item and every entity under it (a plate stack's plates).
                GameObject item = TakeItem();
                if (item != null) NetworkUtils.DestroyObjectsRecursive(item);
            }

            public void CarryItem(GameObject _object)
            {
                m_item = _object;
            }

            public GameObject AccessGameObject()
            {
                return m_chef;
            }

            public void RegisterCarriedItemChangeCallback(VoidGeneric<GameObject, GameObject> _callback)
            {
            }

            public void UnregisterCarriedItemChangeCallback(VoidGeneric<GameObject, GameObject> _callback)
            {
            }
        }

        /// <summary>
        /// Host: does this chef carry anything at all: in its hands, in its POCKET or on its PACK
        /// MULE back? (Medic only brings back chefs who fell empty-handed.)
        /// </summary>
        internal static bool CarriesAnything(ChefState state)
        {
            if (state == null || state.Chef == null) return false;
            ServerPlayerAttachmentCarrier carrier = ServerCarrier(state);
            if (carrier != null && carrier.InspectCarriedItem() != null) return true;
            // Anything stashed counts (Pocket, Pack Mule, and Waiter's second dish on the arm).
            for (int i = 0; i < s_stashed.Count; i++)
                if (s_stashed[i].Chef == state.Id && s_stashed[i].Item != null) return true;
            return false;
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
                DenyVegetarian(receiver, item);
                return false;
            }
            from.TakeItem();
            IAttachment attachment = item.GetComponent<IAttachment>();
            if (attachment != null && attachment.IsAttached()) attachment.Detach();
            to.CarryItem(item);
            return to.InspectCarriedItem() == item;
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
                if (other.Chef == null || Respawning(other)) continue;
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
            if (mate.Team != state.Team)
            {
                DenyHandOff(state, Lang.Both("CAN'T HAND OFF", "KHÔNG TRAO ĐƯỢC"));
                return;
            }
            if (Busy(state) || Busy(mate))
            {
                DenyHandOff(state, Busy(state) ? Lang.Both("HANDS BUSY", "TAY ĐANG BẬN") : Lang.Both("TEAMMATE BUSY", "ĐỒNG ĐỘI ĐANG BẬN"));
                return;
            }
            // Both views can lag here: measure from where the player saw their own chef.
            Vector3 offset = mate.Position - message.Vector1;
            offset.y = 0f;
            if (offset.magnitude > HandOffRange + 1.2f)
            {
                DenyHandOff(state, Lang.Both("TOO FAR", "QUÁ XA"));
                return;
            }
            ServerPlayerAttachmentCarrier mine = ServerCarrier(state), theirs = ServerCarrier(mate);
            if (mine == null || theirs == null)
            {
                DenyHandOff(state, Lang.Both("CAN'T HAND OFF", "KHÔNG TRAO ĐƯỢC"));
                return;
            }
            // Give only: a hand-off never takes from a teammate (that was too easy to do by accident).
            GameObject item = mine.InspectCarriedItem();
            if (item == null) return;
            if (theirs.InspectCarriedItem() != null || !Transfer(mine, theirs, mate))
            {
                DenyHandOff(state, Lang.Both("CAN'T HAND OFF", "KHÔNG TRAO ĐƯỢC"));
                return;
            }
            SpecialFx.BeamAll(state.Position, mate.Position, Special.HandOff, 0.5f);
            SpecialFx.MarkAll(item, Special.HandOff, 0.8f, null);
            SpecialFx.TextAll(mate.Chef, SpecialFx.NameBoth(Special.HandOff, "", ""), Special.HandOff);
        }

        private static void DenyHandOff(ChefState state, string reason)
        {
            SpecialNet.SendToAll(new SpecialMessage { Topic = TopicHandOffDenied, Chef = state.Id, Text = reason });
        }

        private static void OnHandOffDenied(SpecialMessage message)
        {
            ChefState state = SpecialRuntime.StateById(message.Chef);
            if (state != null && state.Local) SpecialHud.Deny(state, message.Text);
        }

        /// <summary>Owner machine: this chef's dash hit another chef.</summary>
        internal static void RequestPickpocket(ChefState state, ChefState victim)
        {
            float next = state.Get<float>("Pickpocket.Next", 0f);
            if (SpecialRuntime.Now < next) return;
            state.Set("Pickpocket.Next", SpecialRuntime.Now + 0.4f);
            SpecialNet.SendToHost(new SpecialMessage
            {
                Topic = TopicPickpocket, Chef = state.Id, I = (int)victim.Id, Vector1 = state.Position
            });
        }

        private static void OnPickpocketRequest(SpecialMessage message)
        {
            if (!SpecialRuntime.InLevel || !SpecialNet.IsServer) return;
            ChefState state = SpecialRuntime.StateById(message.Chef);
            ChefState victim = SpecialRuntime.StateById((uint)message.I);
            if (state == null || victim == null || state == victim || !state.Has(Special.Pickpocket)) return;
            // Versus: only the rival team's chefs (co-op party play still lets you rob anyone).
            if (OrdersModule.Versus && victim.Team == state.Team) return;
            if (Respawning(state) || Respawning(victim)) return;
            Vector3 offset = victim.Position - message.Vector1;
            offset.y = 0f;
            if (offset.magnitude > PickpocketRange) return;
            ServerPlayerAttachmentCarrier mine = ServerCarrier(state), theirs = ServerCarrier(victim);
            if (mine == null || theirs == null || mine.InspectCarriedItem() != null) return;
            GameObject item = theirs.InspectCarriedItem();
            if (item == null) return;
            if (victim.Has(Special.StickyFingers))
            {
                SpecialFx.TextAll(victim.Chef, SpecialFx.NameBoth(Special.StickyFingers, "!", "!"), Special.StickyFingers);
                return;
            }
            if (!Transfer(theirs, mine, state)) return;
            SpecialFx.BeamAll(victim.Position, state.Position, Special.Pickpocket, 0.6f);
            SpecialFx.MarkAll(item, Special.Pickpocket, 1f, null);
            SpecialFx.TextAll(victim.Chef, Lang.Both("PICKPOCKETED!", "BỊ MÓC TÚI!"), Special.Pickpocket);
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

        /// <summary>Host: a Vegetarian refused <paramref name="what"/> (an item, counter or crate; may be null).</summary>
        internal static void DenyVegetarian(ChefState state, GameObject what)
        {
            float last;
            if (s_deniedAt.TryGetValue(state.Id, out last) && SpecialRuntime.Now - last < 0.5f) return;
            s_deniedAt[state.Id] = SpecialRuntime.Now;
            SpecialNet.SendToAll(new SpecialMessage { Topic = TopicVegetarian, Chef = state.Id, I = (int)EntityOf(what) });
        }

        private static void OnVegetarianDenied(SpecialMessage message)
        {
            ChefState state = SpecialRuntime.StateById(message.Chef);
            if (state == null || state.Chef == null) return;
            SpecialFx.Text(state.Chef, Lang.T("NO MEAT!", "KHÔNG ĂN THỊT!"), SpecialFx.ColourOf(Special.Vegetarian));
            GameObject refused = EntityObject((uint)message.I);
            if (refused != null) SpecialFx.Mark(refused, Refused, 0.8f, null, 0f);
            if (state.Local) SpecialHud.Deny(state, SpecialCatalog.NameOf(Special.Vegetarian));
        }
    }

    /// <summary>
    /// WAITER, on the host. Putting a dish down onto another dish (which the game refuses) puts the
    /// held dish up on the arm and picks the other one up. Handing a dish in at the pass hands in
    /// the dish on the arm right after it, through the same placement, if it is ordered.
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
                if (!SpecialRuntime.AnyHas(Special.Waiter)) return true;
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
    /// HAND-OFF, on the player's own machine: PICK UP facing a teammate within reach gives them
    /// the held item (only when their hands are empty; it never takes). The host does the move.
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
                if (mate == null || mate.Controls == null || mate.Controls.GetCurrentlyInteracting() != null) return true;
                ClientPlayerAttachmentCarrier mine = controls.GetComponent<ClientPlayerAttachmentCarrier>();
                ClientPlayerAttachmentCarrier theirs = mate.Chef.GetComponent<ClientPlayerAttachmentCarrier>();
                if (mine == null || theirs == null) return true;
                // Nothing to give, or nowhere to put it: the usual PICK UP / put down.
                if (mine.InspectCarriedItem() == null || theirs.InspectCarriedItem() != null) return true;
                button.ClaimPressEvent();
                SpecialNet.SendToHost(new SpecialMessage
                {
                    Topic = CarryingModule.TopicHandOff, Chef = state.Id, I = (int)mate.Id, Vector1 = state.Position
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
                if (!justReleased || isSuppressed || ControlsField == null) return;
                PlayerControls controls = ControlsField.GetValue(__instance) as PlayerControls;
                ChefState state;
                GameObject item = Tossable(controls, true, out state);
                if (item != null) CarryingModule.RequestToss(state, item);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("StrongArm.Release", error);
            }
        }

        /// <summary>
        /// What this Strong Arm chef would toss on letting go of USE (an item the game itself won't
        /// throw), or null. <paramref name="request"/>: the player's own machine, about to ask the
        /// host for the toss. Otherwise (aiming): the player's own machine, or the host for a
        /// remote chef, since the host moves every chef from its player's input and must stand it
        /// still while it aims, as the game does for a normal throw.
        /// </summary>
        internal static GameObject Tossable(PlayerControls controls, bool request, out ChefState state)
        {
            state = null;
            if (!SpecialRuntime.InLevel || controls == null || controls.m_bRespawning) return null;
            if (!SpecialRuntime.AnyHas(Special.StrongArm)) return null;
            // A client whose host can't read our channel: the request would go nowhere.
            if (!SpecialNet.IsServer && !SpecialNet.Online) return null;
            state = SpecialRuntime.StateOf(controls.gameObject);
            if (state == null || !state.Has(Special.StrongArm)) return null;
            if (request ? !state.Local : !state.Local && !SpecialNet.IsServer) return null;
            ClientPlayerAttachmentCarrier carrier = controls.GetComponent<ClientPlayerAttachmentCarrier>();
            GameObject item = carrier != null ? carrier.InspectCarriedItem() : null;
            if (item == null || item.GetComponent<UsableItem>() != null) return null;
            if (item.GetComponent<ClientBackpack>() != null) return null;
            IClientThrowable throwable = item.GetComponent<IClientThrowable>();
            if (throwable != null)
            {
                Vector3 forward = controls.transform.forward;
                Vector2 direction = new Vector2(forward.x, forward.z).normalized;
                if (throwable.CanHandleThrow(controls.GetComponent<ClientAttachmentThrower>(), direction)) return null;
            }
            return item;
        }
    }

    /// <summary>
    /// STRONG ARM, on the player's own machine: holding USE with something only Strong Arm can
    /// throw aims it like a normal throw. The chef stands and turns, and the game's own throw
    /// arrow shows where it will go (the game draws it only for things it can throw itself).
    /// </summary>
    [HarmonyPatch(typeof(ClientPlayerControlsImpl_Default), "Update_Aim")]
    internal static class StrongArmAimPatch
    {
        private static readonly FieldInfo ControlsField =
            AccessTools.Field(typeof(ClientPlayerControlsImpl_Default), "m_controls");
        private static readonly FieldInfo AimingField =
            AccessTools.Field(typeof(ClientPlayerControlsImpl_Default), "m_aimingThrow");

        private static void Postfix(ClientPlayerControlsImpl_Default __instance, bool isUsePressed)
        {
            try
            {
                if (!isUsePressed || ControlsField == null || AimingField == null) return;
                if (!SpecialRuntime.InLevel || !SpecialRuntime.AnyHas(Special.StrongArm)) return;
                if ((bool)AimingField.GetValue(__instance)) return; // the game is already aiming a throw
                PlayerControls controls = ControlsField.GetValue(__instance) as PlayerControls;
                if (controls == null) return;
                ChefState state;
                if (StrongArmPatch.Tossable(controls, false, out state) == null) return;
                // Read by the game's movement on this same frame: stand still and turn, as when
                // aiming (on the host for a remote chef too, so both agree where it stands).
                AimingField.SetValue(__instance, true);
                // The arrow, like the game's, only for the player's own chef.
                if (state.Local && controls.GetDirectlyUnderPlayerControl() && controls.ThrowIndicator != null)
                    controls.ThrowIndicator.Show(true);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("StrongArm.Aim", error);
            }
        }
    }

    /// <summary>
    /// PICKPOCKET, on the dasher's own machine (only there is the dash timed: the host never sees a
    /// remote chef's dash as a dash): a dash that hits a chef asks the host to take its item.
    /// </summary>
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
                if (!SpecialRuntime.AnyHas(Special.Pickpocket)) return;
                if (!SpecialNet.IsServer && !SpecialNet.Online) return;
                // Read before the game's method ends the dash (a head-on bump resets both timers).
                if ((float)DashTimerField.GetValue(__instance) <= 0f) return;
                PlayerControls controls = ControlsField.GetValue(__instance) as PlayerControls;
                if (controls == null || controls.m_bRespawning || _otherPlayer.m_bRespawning) return;
                ChefState state = SpecialRuntime.StateOf(controls.gameObject);
                if (state == null || !state.Local || !state.Has(Special.Pickpocket)) return;
                ChefState victim = SpecialRuntime.StateOf(_otherPlayer.gameObject);
                if (victim == null || victim == state) return;
                if (OrdersModule.Versus && victim.Team == state.Team) return; // versus: rivals only
                // What this machine sees; the host checks again before moving anything.
                ClientPlayerAttachmentCarrier mine = controls.GetComponent<ClientPlayerAttachmentCarrier>();
                ClientPlayerAttachmentCarrier theirs = _otherPlayer.GetComponent<ClientPlayerAttachmentCarrier>();
                if (mine == null || theirs == null || mine.InspectCarriedItem() != null || theirs.InspectCarriedItem() == null) return;
                CarryingModule.RequestPickpocket(state, victim);
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
                if (!SpecialRuntime.InLevel || _target == null || !SpecialRuntime.AnyHas(Special.Vegetarian)) return true;
                ChefState state = SpecialRuntime.StateOf(__instance.gameObject);
                if (state == null || !state.Has(Special.Vegetarian)) return true;
                GameObject item = CarryingModule.PickupItemOf(_target);
                if (!CarryingModule.IsRawMeat(item)) return true;
                // Mark the real item when there is one (a crate's item is only a prefab: mark the crate).
                CarryingModule.DenyVegetarian(state, CarryingModule.IsEntity(item) ? item : _target);
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
                if (!__result || !SpecialRuntime.InLevel || _object == null || !SpecialRuntime.AnyHas(Special.Vegetarian)) return;
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

    /// <summary>
    /// A player left mid-level: before the game removes their chef (it waits until nothing is
    /// attached to it), whatever the chef had stashed drops where it stands, on every machine.
    /// </summary>
    [HarmonyPatch]
    internal static class CarryingLeavePatch
    {
        private static MethodBase s_target;

        private static bool Prepare()
        {
            if (s_target == null)
            {
                Type type = AccessTools.TypeByName("DisconnectionHandler");
                s_target = type != null ? AccessTools.Method(type, "DeleteChef", new[] { typeof(uint) }) : null;
            }
            return s_target != null;
        }

        private static MethodBase TargetMethod()
        {
            return s_target;
        }

        private static void Prefix(uint uEntityID)
        {
            try
            {
                if (SpecialRuntime.InLevel && SpecialNet.IsServer) CarryingModule.ServerReleaseChef(uEntityID);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Carrying.Leave", error);
            }
        }
    }
}
