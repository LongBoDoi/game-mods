using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ChefSkills.Specials
{
    /// <summary>
    /// 4. Cooking and fire (48-58), plus GREASE TRAIL (97) and the PYROPHOBE quirk (105).
    ///
    /// Everything that changes food or fire runs on the host (Server* components and server-only
    /// ticks); the game's own entity sync then shows it on every client, and the host sends the
    /// coloured marks (SpecialFx ...All) so every player sees what a special is doing. The few
    /// things every machine does itself are physics/visual: FIREPROOF's collisions, HOSE HANDS'
    /// spray, GREASE TRAIL's puddles and slide, the SLOW SPREAD aura.
    ///
    /// Whose pot is it (SIZZLE MASTER, STEADY MIXER, COLD HANDS, FIRE MARSHAL): this module keeps
    /// its own record on the host. Picking a pot, pan or bowl up (or putting it down) makes it the
    /// carrier's alone; putting food into it (by hand or by a throw that lands in it) adds the
    /// filler. So a pot is "yours" from when you carry or fill it until another chef picks it up:
    /// a teammate dropping an onion into your sizzling pot doesn't switch it off, and your
    /// specials never ride along on a pot you never touched.
    /// </summary>
    internal sealed class CookingModule : ISpecialModule
    {
        // --- tuning -------------------------------------------------------------------------------
        // HOT HANDS: cooking seconds per second in the chef's hands (x COOKING skill; SIZZLE MASTER
        // doesn't add to it, or a carried pot would cook faster than one on the heat).
        private const float HotHandsRate = 1.25f;
        // COLD HANDS: seconds a pot waits at "done" before it starts to burn.
        private const float ColdHandsPause = 8f;
        // RESCUE: seconds a rescued pot stays at "done" (time to get it onto a plate).
        private const float RescueHold = 10f;
        private const float OverclockSeconds = 10f;
        internal const float OverclockScale = 3f;
        private const float SizzleScale = 2f;
        // The most the specials together (OVERCLOCK x SIZZLE MASTER) speed one pot up; the COOKING
        // skill comes on top of this.
        private const float MaxSpecialCookScale = 4f;
        // FIRE MARSHAL: food past done in its pots burns this much as fast.
        private const float MarshalBurnScale = 0.5f;
        private const float SteadyScale = 1.75f;
        private const float FireBlastRadius = 6f;
        private const float FireproofReach = 1.3f;
        private const float FireproofStampTime = 2f;
        // SLOW SPREAD: fire can't catch on from a neighbour within this reach of the chef.
        private const float SlowSpreadRadius = 7f;
        private const float GreaseSeconds = 4f;
        private const float GreasePointLife = 6f;
        private const float GreasePointEvery = 0.12f;
        private const float GreaseFxEvery = 0.2f;
        private const float GreaseRadius = 0.7f;
        private const float GreaseFreeze = 1.1f;
        private const float GreaseImmune = 2.5f;
        // "Perfectly cooked / mixed": just past done (Cooked needs progress > time), far from the
        // 1.3x over-doing warning.
        private const float DoneCap = 1.03f;
        // Marks re-sent while something holds: every PulseEvery s, each lasting PulseSeconds.
        private const float PulseEvery = 0.65f;
        private const float PulseSeconds = 1.2f;

        // Network topics: (Special * 10 + n).
        private static readonly ushort TopicHose = (ushort)((int)Special.BuiltInExtinguisher * 10 + 1);
        private static readonly ushort TopicSlip = (ushort)((int)Special.GreaseTrail * 10 + 1);
        private static readonly ushort TopicBlast = (ushort)((int)Special.FireBlast * 10 + 1);
        private static readonly ushort TopicCold = (ushort)((int)Special.ColdHands * 10 + 1);

        // Private game state reached by name (validated by the checker).
        private static readonly FieldInfo CookingDataField = AccessTools.Field(typeof(ServerCookingHandler), "m_ServerData");

        // --- per-level state ----------------------------------------------------------------------

        // Chefs with each special, refreshed twice a second (the per-frame paths read these).
        private static readonly List<ChefState> s_hot = new List<ChefState>();
        private static readonly List<ChefState> s_fireproof = new List<ChefState>();
        private static readonly List<ChefState> s_slow = new List<ChefState>();
        private static readonly List<ChefState> s_hose = new List<ChefState>();
        private static readonly List<ChefState> s_grease = new List<ChefState>();
        private static readonly List<ChefState> s_pyro = new List<ChefState>();
        internal static bool AnySizzle, AnySteady, AnyMarshal, AnyCold, AnySlow, AnyFireproof;
        private static float s_nextRefresh;
        private static float s_nextFireproof;
        private static float s_nextAura;

        // Host: pot/pan/bowl (instance ID) -> the chefs it belongs to: whoever last carried it,
        // plus everyone who has put food into it since.
        private static readonly Dictionary<int, List<ChefState>> s_handlers = new Dictionary<int, List<ChefState>>();

        // Host: pot (instance ID) -> a hold at "done" (COLD HANDS, RESCUE).
        private struct Hold
        {
            public float Until;
            public Special By;
            /// <summary>COLD HANDS: the chef whose pause it is (their skill bar shows it).</summary>
            public uint Chef;
        }
        private static readonly Dictionary<int, Hold> s_holds = new Dictionary<int, Hold>();
        // Host: pots whose COLD HANDS pause has been used since they were last handled.
        private static readonly HashSet<int> s_coldSpent = new HashSet<int>();

        // Cooker or mixer (instance ID) -> Now until which it runs faster (OVERCLOCK).
        internal static readonly Dictionary<int, float> OverclockUntil = new Dictionary<int, float>();
        // Flammable (instance ID) -> its cooker, or null (cached: fire code asks every frame).
        private static readonly Dictionary<int, ServerCookingStation> s_cookerOf = new Dictionary<int, ServerCookingStation>();
        // Throttle for the marks the host keeps re-sending (object + special -> next send).
        private static readonly Dictionary<long, float> s_pulseAt = new Dictionary<long, float>();

        // Set while a cooker / mixer updates, read by the Cook / Mix prefixes it calls.
        internal static float StationCookScale = 1f;
        internal static float StationMixScale = 1f;

        private struct GreasePoint
        {
            public Vector3 Position;
            public float Until;
            public uint Owner;
            public TeamID Team;
        }
        private static readonly List<GreasePoint> s_greasePoints = new List<GreasePoint>();

        private sealed class Hose
        {
            public ChefState State;
            public bool On;
            public GameObject Effect;
            public GameLoopingAudioTag Audio;
            public bool AudioOn;
            public float NoFireSince = -1f;
            public float NextBeam;
        }
        private static readonly Dictionary<uint, Hose> s_hoses = new Dictionary<uint, Hose>();
        private static FireExtinguishSpray s_extinguisher;
        private static float s_nextExtinguisherSearch;
        private static float s_sprayDistance = 4f;
        private static Type s_particleType;
        private static bool s_particleTypeSearched;

        // --- registration -------------------------------------------------------------------------

        public void Register()
        {
            SpecialRuntime.RegisterActive(Special.Rescue, new ActiveHandler { Execute = ExecuteRescue, Targets = RescueTargets });
            SpecialRuntime.RegisterActive(Special.Overclock, new ActiveHandler { Execute = ExecuteOverclock, Duration = OverclockSeconds, Targets = OverclockTargets });
            SpecialRuntime.RegisterActive(Special.FireBlast, new ActiveHandler { Execute = ExecuteFireBlast });
            SpecialRuntime.RegisterActive(Special.GreaseTrail, new ActiveHandler { Execute = ExecuteGrease, Duration = GreaseSeconds });
            SpecialRuntime.Tick += OnTick;
            SpecialRuntime.LevelStarted += Reset;
            SpecialRuntime.LevelEnded += Reset;
            SpecialNet.Register(TopicHose, OnHoseMessage);
            SpecialNet.Register(TopicSlip, OnSlipMessage);
            SpecialNet.Register(TopicBlast, OnBlastMessage);
            SpecialNet.Register(TopicCold, OnColdMessage);
        }

        private static void Reset()
        {
            foreach (Hose hose in s_hoses.Values) SetHoseVisual(hose, false);
            s_hoses.Clear();
            s_hot.Clear(); s_fireproof.Clear(); s_slow.Clear();
            s_hose.Clear(); s_grease.Clear(); s_pyro.Clear();
            AnySizzle = AnySteady = AnyMarshal = AnyCold = AnySlow = AnyFireproof = false;
            s_handlers.Clear();
            s_holds.Clear();
            s_coldSpent.Clear();
            OverclockUntil.Clear();
            s_cookerOf.Clear();
            s_pulseAt.Clear();
            s_greasePoints.Clear();
            s_extinguisher = null;
            s_nextExtinguisherSearch = 0f;
            s_nextRefresh = 0f;
            s_nextAura = 0f;
            StationCookScale = 1f;
            StationMixScale = 1f;
        }

        private static void RefreshLists()
        {
            s_nextRefresh = Time.unscaledTime + 0.5f;
            s_hot.Clear(); s_fireproof.Clear(); s_slow.Clear();
            s_hose.Clear(); s_grease.Clear(); s_pyro.Clear();
            AnySizzle = AnySteady = AnyMarshal = AnyCold = false;
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState state = chefs[i];
                if (state.Chef == null) continue;
                if (state.Has(Special.HotHands)) s_hot.Add(state);
                if (state.Has(Special.Fireproof)) s_fireproof.Add(state);
                if (state.Has(Special.SlowSpread)) s_slow.Add(state);
                if (state.Has(Special.BuiltInExtinguisher)) s_hose.Add(state);
                if (state.Has(Special.GreaseTrail)) s_grease.Add(state);
                if (state.Has(Special.Pyrophobe)) s_pyro.Add(state);
                if (state.Has(Special.ColdHands)) AnyCold = true;
                if (state.Has(Special.SizzleMaster)) AnySizzle = true;
                if (state.Has(Special.SteadyMixer)) AnySteady = true;
                if (state.Has(Special.FireMarshal)) AnyMarshal = true;
            }
            AnySlow = s_slow.Count > 0;
            AnyFireproof = s_fireproof.Count > 0;
        }

        // --- every frame --------------------------------------------------------------------------

        private static void OnTick(float dt)
        {
            if (Time.unscaledTime >= s_nextRefresh) RefreshLists();
            bool server = SpecialNet.IsServer;

            if (server)
            {
                if (s_hot.Count > 0) TickHotHands(dt);
                if (s_grease.Count > 0 || s_greasePoints.Count > 0) TickGrease();
                if (s_fireproof.Count > 0) TickFireproofStamp(dt);
                if (OverclockUntil.Count > 0) Expire(OverclockUntil);
                if (s_holds.Count > 16) ExpireHolds();
            }
            if (AnyFireproof && Time.unscaledTime >= s_nextFireproof) TickFireproof();
            if (AnySlow && Time.unscaledTime >= s_nextAura) TickSlowAura();
            if (s_grease.Count > 0) TickGreaseFx();
            if (s_hose.Count > 0 || s_hoses.Count > 0) TickHose(dt, server);
            if (s_pyro.Count > 0) TickPyrophobe();
        }

        private static void Expire(Dictionary<int, float> map)
        {
            List<int> gone = null;
            foreach (KeyValuePair<int, float> pair in map)
                if (pair.Value <= SpecialRuntime.Now)
                {
                    if (gone == null) gone = new List<int>();
                    gone.Add(pair.Key);
                }
            if (gone != null) for (int i = 0; i < gone.Count; i++) map.Remove(gone[i]);
        }

        private static void ExpireHolds()
        {
            List<int> gone = null;
            foreach (KeyValuePair<int, Hold> pair in s_holds)
                if (pair.Value.Until <= SpecialRuntime.Now)
                {
                    if (gone == null) gone = new List<int>();
                    gone.Add(pair.Key);
                }
            if (gone != null) for (int i = 0; i < gone.Count; i++) s_holds.Remove(gone[i]);
        }

        private static GameObject ServerHeld(ChefState state)
        {
            if (state.Chef == null) return null;
            ServerPlayerAttachmentCarrier carrier = state.Chef.GetComponent<ServerPlayerAttachmentCarrier>();
            return carrier != null ? carrier.InspectCarriedItem() : null;
        }

        private static GameObject ClientHeld(ChefState state)
        {
            if (state.Chef == null) return null;
            ClientPlayerAttachmentCarrier carrier = state.Chef.GetComponent<ClientPlayerAttachmentCarrier>();
            return carrier != null ? carrier.InspectCarriedItem() : null;
        }

        /// <summary>
        /// Host: keeps a coloured ring (and word) on <paramref name="target"/> on every machine while
        /// this is called, e.g. every frame a pot cooks faster. Cheap to call every frame.
        /// </summary>
        private static void Pulse(GameObject target, Special special, string label)
        {
            if (target == null) return;
            long key = ((long)target.GetInstanceID() << 8) ^ (long)special;
            float now = Time.unscaledTime;
            float next;
            if (s_pulseAt.TryGetValue(key, out next) && now < next) return;
            if (s_pulseAt.Count > 512) s_pulseAt.Clear();
            s_pulseAt[key] = now + PulseEvery;
            SpecialFx.MarkAll(target, special, PulseSeconds, label);
        }

        private static Vector3 Flat(Vector3 direction)
        {
            direction.y = 0f;
            return direction.sqrMagnitude > 1e-4f ? direction.normalized : Vector3.forward;
        }

        // --- whose pot is it (host) ---------------------------------------------------------------

        /// <summary>The first chef this pot belongs to who has the special (null if none).</summary>
        private static ChefState OwnerWith(GameObject container, Special special)
        {
            List<ChefState> handlers;
            if (container == null || !s_handlers.TryGetValue(container.GetInstanceID(), out handlers)) return null;
            for (int i = 0; i < handlers.Count; i++)
                if (handlers[i] != null && handlers[i].Chef != null && handlers[i].Has(special)) return handlers[i];
            return null;
        }

        private static readonly Special[] ContainerSpecials =
            { Special.SizzleMaster, Special.SteadyMixer, Special.ColdHands, Special.FireMarshal };

        /// <summary>
        /// A chef carried (<paramref name="carried"/> true: picked up, put down, caught) or filled a
        /// pot, pan or bowl.
        /// </summary>
        internal static void NoteHandled(GameObject chef, GameObject container, bool carried)
        {
            if (chef == null || container == null || !SpecialRuntime.InLevel) return;
            ChefState state = SpecialRuntime.StateOf(chef);
            if (state == null) return;
            int key = container.GetInstanceID();
            List<ChefState> handlers;
            if (!s_handlers.TryGetValue(key, out handlers))
            {
                handlers = new List<ChefState>(2);
                s_handlers[key] = handlers;
            }
            bool already = handlers.Contains(state);
            if (carried)
            {
                handlers.Clear();
                handlers.Add(state);
            }
            else if (!already) handlers.Add(state);

            // Handling it again gives COLD HANDS a fresh pause.
            s_coldSpent.Remove(key);
            Hold hold;
            if (s_holds.TryGetValue(key, out hold) && hold.By == Special.ColdHands)
            {
                s_holds.Remove(key);
                // Picked up before the pause ran out: its sweep on the skill bar stops too.
                if (hold.Until > SpecialRuntime.Now && hold.Chef != 0)
                    SpecialNet.SendToAll(new SpecialMessage { Topic = TopicCold, Chef = hold.Chef, A = 0f });
            }

            if (already) return;
            // Everyone sees the pot become a special chef's.
            for (int i = 0; i < ContainerSpecials.Length; i++)
                if (state.Has(ContainerSpecials[i]))
                {
                    SpecialFx.MarkAll(container, ContainerSpecials[i], 0.9f, null);
                    break;
                }
        }

        // --- HOT HANDS (host) ---------------------------------------------------------------------

        // A pot that had started cooking keeps cooking in the chef's hands, up to perfectly done and
        // never further. SetCookingProgress is the game's own setter, so the client progress bars
        // follow through the normal cooking-state sync.
        private static void TickHotHands(float dt)
        {
            for (int i = 0; i < s_hot.Count; i++)
            {
                ChefState state = s_hot[i];
                GameObject held = ServerHeld(state);
                if (held == null) continue;
                ServerCookingHandler cooking = held.GetComponent<ServerCookingHandler>();
                if (cooking == null) continue;
                float progress = cooking.GetCookingProgress();
                float time = cooking.AccessCookingTime;
                float cap = time * DoneCap;
                if (time <= 0f || progress <= 0f || progress >= cap) continue;
                ServerIngredientContainer contents = held.GetComponent<ServerIngredientContainer>();
                if (contents != null && !contents.HasContents()) continue;
                float rate = HotHandsRate * Plugin.Skills.Multiplier(state.Chef, Skill.Cooking);
                float next = Mathf.Min(cap, progress + dt * rate);
                cooking.SetCookingProgress(next);
                Pulse(held, Special.HotHands, Lang.Both("HOT", "NÓNG"));
                if (next >= cap) SpecialFx.TextAll(held, Lang.Both("DONE!", "CHÍN!"), Special.HotHands);
            }
        }

        // --- the Cook / Mix adjustments (host) ----------------------------------------------------

        internal static bool CookActive
        {
            get { return StationCookScale != 1f || AnySizzle || AnyCold || AnyMarshal || s_holds.Count > 0; }
        }

        internal static float AdjustCook(ServerCookingHandler handler, float delta)
        {
            // The specials' speed-ups (OVERCLOCK's cooker, SIZZLE MASTER's pot) multiply, up to
            // MaxSpecialCookScale together. COOKING / SLOW BURN is already in delta (that patch runs
            // first), so the skill stays outside the cap.
            float scale = StationCookScale;
            GameObject pot = handler.gameObject;
            float time = handler.AccessCookingTime;
            float progress = handler.GetCookingProgress();
            if (time <= 0f || progress > 2f * time) return delta * scale; // burnt: Cook does nothing
            int key = pot.GetInstanceID();
            float cap = time * DoneCap;

            bool sizzle = false;
            if (AnySizzle && OwnerWith(pot, Special.SizzleMaster) != null)
            {
                scale *= SizzleScale;
                sizzle = true;
            }
            delta *= Mathf.Min(scale, MaxSpecialCookScale);

            // A hold at "done": RESCUE's, or COLD HANDS' (starts when the pot reaches done).
            Hold hold;
            bool holding = s_holds.TryGetValue(key, out hold) && hold.Until > SpecialRuntime.Now;
            if (!holding && AnyCold && delta > 0f && progress + delta >= cap && !s_coldSpent.Contains(key))
            {
                ChefState cold = OwnerWith(pot, Special.ColdHands);
                if (cold != null)
                {
                    hold = new Hold { Until = SpecialRuntime.Now + ColdHandsPause, By = Special.ColdHands, Chef = cold.Id };
                    s_holds[key] = hold;
                    s_coldSpent.Add(key);
                    holding = true;
                    // The cold chef's own skill bar shows the pause running down.
                    SpecialNet.SendToAll(new SpecialMessage { Topic = TopicCold, Chef = cold.Id, A = ColdHandsPause });
                }
            }
            if (holding)
            {
                float held = Cap(progress, delta, cap);
                if (held < delta) Pulse(pot, hold.By, hold.By == Special.Rescue ? null : Lang.Both("COOL", "NGUỘI"));
                delta = held;
            }

            if (sizzle && delta > 0f) Pulse(pot, Special.SizzleMaster, "x2");
            // FIRE MARSHAL: food past done burns half as fast (cooking up to done is untouched), and
            // a pot over-doing on a cooker it protects says so.
            if (AnyMarshal && delta > 0f && progress + delta > time && OwnerWith(pot, Special.FireMarshal) != null)
            {
                float cooking = Mathf.Max(0f, time - progress);
                delta = cooking + (delta - cooking) * MarshalBurnScale;
                if (!holding && progress > 1.3f * time && pot.GetComponentInParent<ServerCookingStation>() != null)
                    Pulse(pot, Special.FireMarshal, Lang.Both("NO FIRE", "KHÔNG CHÁY"));
            }
            return delta;
        }

        internal static float AdjustMix(ServerMixingHandler handler, float delta)
        {
            float station = StationMixScale;
            if (AnySteady && delta > 0f && OwnerWith(handler.gameObject, Special.SteadyMixer) != null)
            {
                float progress = handler.GetMixingProgress();
                float cap = handler.AccessMixingTime * DoneCap;
                // Overclock and Steady Mixer together stay within the same x4 as a pot's specials.
                delta = Cap(progress, delta * Mathf.Min(station * SteadyScale, MaxSpecialCookScale), cap);
                Pulse(handler.gameObject, Special.SteadyMixer, Lang.Both("STEADY", "ĐỀU TAY"));
                if (progress < cap && progress + delta >= cap) SpecialFx.TextAll(handler.gameObject, Lang.Both("PERFECT!", "HOÀN HẢO!"), Special.SteadyMixer);
            }
            else delta *= station;
            return delta;
        }

        /// <summary>The delta that takes progress no further than cap (it may already be past it).</summary>
        private static float Cap(float progress, float delta, float cap)
        {
            if (delta <= 0f) return delta;
            return Mathf.Clamp(cap - progress, 0f, delta);
        }

        // --- fire (host) --------------------------------------------------------------------------

        private static ServerCookingStation CookerOf(ServerFlammable flammable)
        {
            int key = flammable.GetInstanceID();
            ServerCookingStation cooker;
            if (!s_cookerOf.TryGetValue(key, out cooker))
            {
                cooker = flammable.GetComponent<ServerCookingStation>();
                s_cookerOf[key] = cooker;
            }
            return cooker;
        }

        /// <summary>FIRE MARSHAL: this flammable is a cooker holding a Fire Marshal's pot.</summary>
        internal static bool IsProtectedCooker(ServerFlammable flammable)
        {
            ServerCookingStation cooker = CookerOf(flammable);
            if (cooker == null) return false;
            ServerAttachStation station = cooker.GetComponent<ServerAttachStation>();
            if (station == null || !station.HasItem()) return false;
            return OwnerWith(station.InspectItem(), Special.FireMarshal) != null;
        }

        /// <summary>SLOW SPREAD: a chef with it stands within reach of this position.</summary>
        internal static bool NearSlowSpread(Vector3 position)
        {
            float range = SlowSpreadRadius * SlowSpreadRadius;
            for (int i = 0; i < s_slow.Count; i++)
            {
                if (s_slow[i].Chef == null) continue;
                Vector3 offset = s_slow[i].Position - position;
                offset.y = 0f;
                if (offset.sqrMagnitude <= range) return true;
            }
            return false;
        }

        internal static void MarkProtected(ServerFlammable flammable, bool ignition)
        {
            Pulse(flammable.gameObject, Special.FireMarshal, Lang.Both("SAFE", "AN TOÀN"));
            if (ignition) SpecialFx.TextAll(flammable.gameObject, Lang.Both("NO FIRE!", "KHÔNG CHÁY!"), Special.FireMarshal);
        }

        internal static void MarkFirebreak(ServerFlammable flammable)
        {
            Pulse(flammable.gameObject, Special.SlowSpread, Lang.Both("NO SPREAD", "KHÔNG LAN"));
        }

        // FIREPROOF (host): fire right next to a fireproof chef is stamped out.
        private static readonly List<ServerFlammable> s_stamp = new List<ServerFlammable>();

        private static void TickFireproofStamp(float dt)
        {
            if (dt <= 0f) return;
            s_stamp.Clear();
            float reach = FireproofReach * FireproofReach;
            foreach (ServerFlammable fire in ServerFlammable.GetAllOnFire())
            {
                if (fire == null) continue;
                Vector3 at = fire.transform.position;
                for (int i = 0; i < s_fireproof.Count; i++)
                {
                    if (s_fireproof[i].Chef == null) continue;
                    Vector3 offset = at - s_fireproof[i].Position;
                    if (Mathf.Abs(offset.y) > 1.5f) continue;
                    offset.y = 0f;
                    if (offset.sqrMagnitude > reach) continue;
                    s_stamp.Add(fire);
                    break;
                }
            }
            // Collected first: putting a fire out changes the list being read.
            for (int i = 0; i < s_stamp.Count; i++)
            {
                ServerFlammable fire = s_stamp[i];
                if (fire == null || !fire.OnFire()) continue;
                fire.FightFire(FireproofStampTime, dt, true);
                if (fire.OnFire()) Pulse(fire.gameObject, Special.Fireproof, null);
                else SpecialFx.TextAll(fire.gameObject, Lang.Both("STAMPED OUT!", "DẬP TẮT!"), Special.Fireproof);
            }
            s_stamp.Clear();
        }

        // --- finding what the chef faces (host) ---------------------------------------------------

        private static float FacingScore(Vector3 from, Vector3 forward, Component candidate, float range)
        {
            Vector3 offset = candidate.transform.position - from;
            offset.y = 0f;
            float distance = offset.magnitude;
            if (distance > range) return float.MaxValue;
            float angle = distance < 0.3f ? 0f : Vector3.Angle(forward, offset);
            if (angle > 75f) return float.MaxValue;
            return distance + angle / 45f;
        }

        // A chosen pot or station counts within this much of where it was chosen; the pot in hand
        // when the chosen spot is this close to the chef.
        private const float AimPick = 0.9f;
        private const float HeldPick = 0.8f;

        /// <summary>What to look through: around the target the player chose, else ahead of the chef.</summary>
        private static Collider[] Candidates(ChefState state, Vector3 from, Vector3 forward)
        {
            return state.HasAimPoint ? Physics.OverlapSphere(state.AimPoint + Vector3.up * 0.5f, 1.1f) : Ahead(from, forward);
        }

        /// <summary>
        /// Lower is better: for a chosen target how far it is from the chosen spot (in reach,
        /// whichever way the chef faces), else <see cref="FacingScore"/>. MaxValue: not this one.
        /// </summary>
        private static float Score(ChefState state, Vector3 from, Vector3 forward, Component candidate, float range)
        {
            if (!state.HasAimPoint) return FacingScore(from, forward, candidate, range);
            Vector3 offset = candidate.transform.position - from;
            offset.y = 0f;
            float off = state.AimDistance(candidate.transform.position);
            return offset.magnitude <= range + 0.6f && off <= AimPick ? off : float.MaxValue;
        }

        // --- what RESCUE and OVERCLOCK can be aimed at (the player's machine) ----------------------

        private static bool Ruined(ClientCookingHandler pot)
        {
            float time = pot.AccessCookingTime;
            return pot.IsBurning() || (time > 0f && pot.GetCookingProgress() > time * 1.3f);
        }

        private static List<AimTarget> RescueTargets(ChefState state)
        {
            var targets = new List<AimTarget>();
            ClientPlayerAttachmentCarrier carrier = state.Chef != null ? state.Chef.GetComponent<ClientPlayerAttachmentCarrier>() : null;
            GameObject held = carrier != null ? carrier.InspectCarriedItem() : null;
            ClientCookingHandler heldPot = held != null ? held.GetComponent<ClientCookingHandler>() : null;
            foreach (ClientCookingHandler pot in Object.FindObjectsOfType<ClientCookingHandler>())
            {
                if (pot == null || pot == heldPot || !pot.gameObject.activeInHierarchy || !Ruined(pot)) continue;
                Vector3 offset = pot.transform.position - state.Position;
                offset.y = 0f;
                if (offset.magnitude <= 2.4f) targets.Add(AimTarget.ForObject(pot.gameObject));
            }
            SpecialAim.FacedFirst(targets, state.Position, state.Forward, 75f);
            // The pot in hand comes first, as when it isn't aimed.
            if (heldPot != null && Ruined(heldPot)) targets.Insert(0, AimTarget.ForObject(held));
            return targets;
        }

        private static List<AimTarget> OverclockTargets(ChefState state)
        {
            var targets = new List<AimTarget>();
            var seen = new HashSet<GameObject>();
            foreach (CookingStation station in Object.FindObjectsOfType<CookingStation>()) Add(targets, seen, state, station);
            foreach (MixingStation station in Object.FindObjectsOfType<MixingStation>()) Add(targets, seen, state, station);
            return SpecialAim.FacedFirst(targets, state.Position, state.Forward, 75f);
        }

        private static void Add(List<AimTarget> targets, HashSet<GameObject> seen, ChefState state, Component station)
        {
            if (station == null || !station.gameObject.activeInHierarchy || !seen.Add(station.gameObject)) return;
            Vector3 offset = station.transform.position - state.Position;
            offset.y = 0f;
            if (offset.magnitude <= 2.4f) targets.Add(AimTarget.ForObject(station.gameObject));
        }

        private static Collider[] Ahead(Vector3 from, Vector3 forward)
        {
            Vector3 centre = from + forward * 1.0f + Vector3.up * 0.5f;
            return Physics.OverlapSphere(centre, 1.3f);
        }

        private static ServerCookingHandler PotOf(GameObject obj)
        {
            if (obj == null) return null;
            ServerCookingHandler pot = obj.GetComponentInParent<ServerCookingHandler>();
            if (pot != null) return pot;
            ServerAttachStation station = obj.GetComponentInParent<ServerAttachStation>();
            if (station != null && station.HasItem() && station.InspectItem() != null)
                return station.InspectItem().GetComponent<ServerCookingHandler>();
            return null;
        }

        private static bool IsRuined(ServerCookingHandler pot)
        {
            float time = pot.AccessCookingTime;
            // Burnt, or flashing on its way there.
            return time > 0f && pot.GetCookingProgress() > time * 1.3f;
        }

        // --- actives (host) -----------------------------------------------------------------------

        // RESCUE: a burnt pot (in hand first, else the one faced) goes back to just done, holds
        // there 10 s, and the fire on its cooker goes out.
        private static bool ExecuteRescue(ChefState state)
        {
            Vector3 from = state.RequestPosition, forward = Flat(state.RequestForward);
            ServerCookingHandler target = null;
            GameObject held = ServerHeld(state);
            // The pot in hand, unless the player chose one further away.
            if (held != null && (!state.HasAimPoint || state.AimDistance(from) <= HeldPick))
            {
                ServerCookingHandler pot = held.GetComponent<ServerCookingHandler>();
                if (pot != null && IsRuined(pot)) target = pot;
            }
            if (target == null)
            {
                float best = float.MaxValue;
                bool bestBurnt = false;
                foreach (Collider collider in Candidates(state, from, forward))
                {
                    ServerCookingHandler pot = PotOf(collider.gameObject);
                    if (pot == null || pot.gameObject == held || !IsRuined(pot)) continue;
                    float score = Score(state, from, forward, pot, 2.4f);
                    if (score == float.MaxValue) continue;
                    bool burnt = pot.IsBurning();
                    // A burnt pot beats one that is only over-doing.
                    if ((burnt && !bestBurnt) || (burnt == bestBurnt && score < best))
                    {
                        target = pot;
                        best = score;
                        bestBurnt = burnt;
                    }
                }
            }
            if (target == null) return state.Refuse(Lang.Both("NO BURNT POT IN REACH", "KHÔNG CÓ NỒI CHÁY GẦN ĐÂY"));
            Rescue(target);
            return true;
        }

        private static void Rescue(ServerCookingHandler pot)
        {
            // Its cooker's fire goes out first: the pot's state change below turns the cooker back
            // on only if it isn't burning.
            ServerCookingStation cooker = pot.GetComponentInParent<ServerCookingStation>();
            ServerFlammable flame = cooker != null ? cooker.GetComponent<ServerFlammable>() : null;
            if (flame != null && flame.OnFire()) flame.FightFire(0.001f, 1f, true);

            float done = pot.AccessCookingTime * DoneCap;
            var data = CookingDataField != null ? CookingDataField.GetValue(pot) as CookingStateMessage : null;
            if (data != null)
            {
                // SetCookingProgress keeps a Ruined state once over the done mark; start it from
                // Progressing so it moves to Completed and fires one state change (which turns the
                // cooker under it back on through the order-composition callback).
                data.m_cookingState = CookingUIController.State.Progressing;
                pot.SetCookingProgress(done);
            }
            else
            {
                pot.SetCookingProgress(0f);
                pot.SetCookingProgress(done);
            }
            int key = pot.gameObject.GetInstanceID();
            s_holds[key] = new Hold { Until = SpecialRuntime.Now + RescueHold, By = Special.Rescue };

            SpecialFx.MarkAll(pot.gameObject, Special.Rescue, RescueHold, null);
            SpecialFx.TextAll(pot.gameObject, Lang.Both("SAVED!", "CỨU KỊP!"), Special.Rescue);
        }

        // OVERCLOCK: the faced cooker or mixer runs three times as fast for 10 s.
        private static bool ExecuteOverclock(ChefState state)
        {
            Vector3 from = state.RequestPosition, forward = Flat(state.RequestForward);
            Component target = null;
            float best = float.MaxValue;
            foreach (Collider collider in Candidates(state, from, forward))
            {
                Component station = collider.GetComponentInParent<ServerCookingStation>();
                if (station == null) station = collider.GetComponentInParent<ServerMixingStation>();
                if (station == null) continue;
                float score = Score(state, from, forward, station, 2.4f);
                if (score < best)
                {
                    best = score;
                    target = station;
                }
            }
            if (target == null) return state.Refuse(Lang.Both("NO COOKER OR MIXER IN FRONT", "KHÔNG CÓ BẾP HAY MÁY TRỘN TRƯỚC MẶT"));
            OverclockUntil[target.gameObject.GetInstanceID()] = SpecialRuntime.Now + OverclockSeconds;
            SpecialFx.BeamAll(from, target.transform.position, Special.Overclock, 0.5f);
            Pulse(target.gameObject, Special.Overclock, "x3");
            return true;
        }

        internal static void MarkOverclocked(Component station)
        {
            Pulse(station.gameObject, Special.Overclock, "x3");
        }

        // FIRE BLAST: every fire within 6 m goes out now.
        private static bool ExecuteFireBlast(ChefState state)
        {
            Vector3 centre = state.RequestPosition;
            var near = new List<ServerFlammable>();
            float range = FireBlastRadius * FireBlastRadius;
            foreach (ServerFlammable fire in ServerFlammable.GetAllOnFire())
            {
                if (fire == null) continue;
                Vector3 offset = fire.transform.position - centre;
                offset.y = 0f;
                if (offset.sqrMagnitude <= range) near.Add(fire);
            }
            if (near.Count == 0) return state.Refuse(Lang.Both("NO FIRE WITHIN 6 M", "KHÔNG CÓ LỬA TRONG 6 M"));
            // FightFire (not Extinguish) also holds off re-ignition from neighbours for a moment.
            int count = 0;
            for (int i = 0; i < near.Count; i++)
            {
                if (near[i] == null || !near[i].OnFire()) continue;
                near[i].FightFire(0.001f, 1f, true);
                count++;
                Vector3 at = near[i].transform.position;
                SpecialFx.BurstAll(at, Special.FireBlast);
                SpecialFx.TextAllAt(at + Vector3.up * 1.4f, Lang.Both("OUT!", "TẮT!"), Special.FireBlast);
            }
            SpecialNet.SendToAll(new SpecialMessage { Topic = TopicBlast, Chef = state.Id, Vector1 = centre, I = count });
            return true;
        }

        // Every machine: the blast's reach.
        private static void OnBlastMessage(SpecialMessage message)
        {
            Color colour = SpecialFx.ColourOf(Special.FireBlast);
            SpecialFx.Burst(message.Vector1, colour, FireBlastRadius, 0.7f);
            SpecialFx.Burst(message.Vector1, Color.Lerp(colour, Color.white, 0.5f), FireBlastRadius * 0.6f, 0.5f);
        }

        // Every machine: the COLD HANDS chef's skill bar shows its pause running (A = 0: it ended early).
        private static void OnColdMessage(SpecialMessage message)
        {
            ChefState state = SpecialRuntime.StateById(message.Chef);
            if (state == null || !state.Local) return;
            if (message.A <= 0f)
            {
                // The pause ended early.
                state.Timers.Remove(Special.ColdHands);
                return;
            }
            state.SetTimer(Special.ColdHands, SpecialRuntime.Now + message.A, message.A);
        }

        // GREASE TRAIL: the trail is laid by the host's tick while the active lasts.
        private static bool ExecuteGrease(ChefState state)
        {
            if (state.Chef == null) return state.Refuse(Lang.Both("NO CHEF", "KHÔNG CÓ ĐẦU BẾP"));
            state.Set("GreaseTrail.next", 0f);
            return true;
        }

        // --- GREASE TRAIL -------------------------------------------------------------------------

        // Host: lays the trail and slips whoever steps on it.
        private static void TickGrease()
        {
            float now = SpecialRuntime.Now;
            for (int i = 0; i < s_grease.Count; i++)
            {
                ChefState state = s_grease[i];
                // Grease Trail's own slot: another active's effect (Frenzy, Decoy...) never stretches it.
                if (state.Chef == null || state.RunningUntil(Special.GreaseTrail) <= now) continue;
                if (now < state.Get("GreaseTrail.next", 0f)) continue;
                state.Set("GreaseTrail.next", now + GreasePointEvery);
                s_greasePoints.Add(new GreasePoint { Position = state.Position, Until = now + GreasePointLife, Owner = state.Id, Team = state.Team });
            }
            for (int i = s_greasePoints.Count - 1; i >= 0; i--)
                if (s_greasePoints[i].Until <= now) s_greasePoints.RemoveAt(i);
            if (s_greasePoints.Count == 0) return;

            IList<ChefState> chefs = SpecialRuntime.Chefs;
            float range = GreaseRadius * GreaseRadius;
            for (int c = 0; c < chefs.Count; c++)
            {
                ChefState victim = chefs[c];
                if (victim.Chef == null || victim.Has(Special.SureFooted)) continue;
                if (now < victim.Get("GreaseTrail.immune", 0f)) continue;
                Vector3 position = victim.Position;
                for (int p = 0; p < s_greasePoints.Count; p++)
                {
                    GreasePoint point = s_greasePoints[p];
                    if (point.Owner == victim.Id) continue;
                    // Versus: your own team runs over your grease. Co-op party: it slips everyone.
                    if (OrdersModule.Versus && point.Team == victim.Team) continue;
                    Vector3 offset = position - point.Position;
                    if (Mathf.Abs(offset.y) > 1f) continue;
                    offset.y = 0f;
                    if (offset.sqrMagnitude > range) continue;
                    victim.Set("GreaseTrail.immune", now + GreaseImmune);
                    // No source: a teammate's grease must still work on a LONE WOLF.
                    SpecialRuntime.BroadcastBuff(victim, BuffId.Frozen, GreaseFreeze, 1f, null);
                    Vector3 slide = victim.Forward;
                    slide.y = 0f;
                    slide = slide.normalized * 5f;
                    SpecialNet.SendToAll(new SpecialMessage { Topic = TopicSlip, Chef = victim.Id, Vector1 = slide });
                    break;
                }
            }
        }

        // Every machine: greasy puddles where the chef runs while the active lasts (the host's trail
        // points are laid at the same pace from the same chef, so they match what is drawn).
        private static void TickGreaseFx()
        {
            float now = SpecialRuntime.Now;
            Color colour = SpecialFx.ColourOf(Special.GreaseTrail);
            for (int i = 0; i < s_grease.Count; i++)
            {
                ChefState state = s_grease[i];
                if (state.Chef == null || state.RunningUntil(Special.GreaseTrail) <= now) continue;
                if (now < state.Get("GreaseTrail.fx", 0f)) continue;
                state.Set("GreaseTrail.fx", now + GreaseFxEvery);
                SpecialFx.Burst(state.Position, colour, GreaseRadius + 0.15f, GreasePointLife);
            }
        }

        // Every machine: the victim skids (both the host and the player's machine simulate it).
        private static void OnSlipMessage(SpecialMessage message)
        {
            ChefState victim = SpecialRuntime.StateById(message.Chef);
            if (victim == null || victim.Chef == null || victim.Controls == null) return;
            var impl = victim.Controls.GetActiveControlsImpl() as PlayerControlsImpl_Default;
            if (impl != null && impl.m_clientImpl != null)
            {
                Vector3 slide = message.Vector1;
                impl.m_clientImpl.ApplyImpact(new Vector2(slide.x, slide.z), 0.45f);
            }
            GameUtils.TriggerAudio(GameOneShotAudioTag.PlayerSlip, victim.Chef.layer);
            Color colour = SpecialFx.ColourOf(Special.GreaseTrail);
            SpecialFx.Mark(victim.Chef, colour, GreaseFreeze, null, 0f);
            SpecialFx.Text(victim.Chef, Lang.T("SLIPPED!", "TRƯỢT CHÂN!"), colour);
        }

        // --- FIREPROOF (every machine) ------------------------------------------------------------

        // Fire hazards are solid capsules (enabled while burning) that knock chefs back on contact.
        // Every machine that moves the chef must let it pass, so each ignores those collisions
        // locally for fireproof chefs, a few times a second (re-enabling a collider resets it).
        // A fireproof chef next to a fire glows, so players see why it can stand there.
        private static void TickFireproof()
        {
            s_nextFireproof = Time.unscaledTime + 0.1f;
            float reach = (FireproofReach + 0.3f) * (FireproofReach + 0.3f);
            for (int i = 0; i < s_fireproof.Count; i++)
            {
                ChefState state = s_fireproof[i];
                if (state.Chef == null) continue;
                Collider[] own = null;
                foreach (Collider collider in Physics.OverlapSphere(state.Position, 3f))
                {
                    if (collider == null || collider.isTrigger) continue;
                    if (collider.GetComponentInParent<FireHazard>() == null) continue;
                    if (own == null) own = state.Chef.GetComponents<Collider>();
                    for (int j = 0; j < own.Length; j++)
                        if (own[j] != null) Physics.IgnoreCollision(own[j], collider, true);
                }
                Vector3 position = state.Position;
                foreach (ClientFlammable fire in ClientFlammable.GetAllOnFire())
                {
                    if (fire == null) continue;
                    Vector3 offset = fire.transform.position - position;
                    if (Mathf.Abs(offset.y) > 1.5f) continue;
                    offset.y = 0f;
                    if (offset.sqrMagnitude > reach) continue;
                    SpecialFx.Mark(state.Chef, Special.Fireproof, 0.4f, null);
                    break;
                }
            }
        }

        // --- SLOW SPREAD (every machine) ----------------------------------------------------------

        // While a fire is near, the chef's reach shows as a wide ring around them.
        private static void TickSlowAura()
        {
            s_nextAura = Time.unscaledTime + 0.25f;
            Color colour = Color.Lerp(SpecialFx.ColourOf(Special.SlowSpread), Color.white, 0.4f);
            float near = (SlowSpreadRadius + 1.5f) * (SlowSpreadRadius + 1.5f);
            for (int i = 0; i < s_slow.Count; i++)
            {
                ChefState state = s_slow[i];
                if (state.Chef == null) continue;
                Vector3 position = state.Position;
                foreach (ClientFlammable fire in ClientFlammable.GetAllOnFire())
                {
                    if (fire == null) continue;
                    Vector3 offset = fire.transform.position - position;
                    offset.y = 0f;
                    if (offset.sqrMagnitude > near) continue;
                    SpecialFx.Mark(state.Chef, colour, 0.5f, null, SlowSpreadRadius);
                    break;
                }
            }
        }

        // --- HOSE HANDS ---------------------------------------------------------------------------

        private static FireExtinguishSpray Extinguisher()
        {
            if (s_extinguisher == null && Time.unscaledTime >= s_nextExtinguisherSearch)
            {
                s_nextExtinguisherSearch = Time.unscaledTime + 3f;
                s_extinguisher = Object.FindObjectOfType<FireExtinguishSpray>();
            }
            // The host shortens m_sprayDistance to obstacles while the real one sprays; keep the most.
            if (s_extinguisher != null) s_sprayDistance = Mathf.Max(s_sprayDistance, s_extinguisher.m_sprayDistance);
            return s_extinguisher;
        }

        private static float SprayAngle()
        {
            FireExtinguishSpray spray = Extinguisher();
            return spray != null ? spray.m_sprayAngleInDegrees : 15f;
        }

        /// <summary>The extinguisher's cone (ServerSprayingUtensil.IsInSpray) from the chef.</summary>
        private static bool InCone(Transform chef, Vector3 target, float distance, float angle)
        {
            Vector3 offset = target - chef.position;
            float reach = distance + 0.6f;
            if (offset.sqrMagnitude >= reach * reach) return false;
            float along = Vector3.Dot(offset, chef.forward);
            if (along <= 0f || along >= reach) return false;
            float radius = along * Mathf.Sin(Mathf.Deg2Rad * angle * 0.5f) + 0.6f;
            return (target - (chef.position + chef.forward * along)).sqrMagnitude < radius * radius;
        }

        private static void TickHose(float dt, bool server)
        {
            float distance = s_sprayDistance;
            float angle = SprayAngle();

            // The player's own machine decides when the hose is on and tells everyone.
            for (int i = 0; i < s_hose.Count; i++)
            {
                ChefState state = s_hose[i];
                if (!state.Local || state.Chef == null || state.Controls == null) continue;
                Hose hose = HoseOf(state);
                bool want = WantsHose(state, hose, distance, angle);
                if (want != hose.On)
                    SpecialNet.SendToAll(new SpecialMessage { Topic = TopicHose, Chef = state.Id, I = want ? 1 : 0 });
            }

            // Every machine: without the extinguisher's own effect (no extinguisher in the level),
            // a jet of colour shows the spray.
            Color colour = SpecialFx.ColourOf(Special.BuiltInExtinguisher);
            foreach (Hose hose in s_hoses.Values)
            {
                if (!hose.On || hose.Effect != null || hose.State.Chef == null || Time.unscaledTime < hose.NextBeam) continue;
                hose.NextBeam = Time.unscaledTime + 0.12f;
                Transform chef = hose.State.Chef.transform;
                Vector3 from = chef.position + Vector3.up * 0.6f;
                SpecialFx.Beam(from, from + chef.forward * distance, colour, 0.2f);
            }

            if (!server) return;
            // The host puts out what is in the spray, at the extinguisher's rate for this chef.
            List<ServerFlammable> hit = null;
            foreach (Hose hose in s_hoses.Values)
            {
                if (!hose.On || hose.State.Chef == null || ServerHeld(hose.State) != null) continue;
                FireExtinguishSpray spray = Extinguisher();
                float time = spray != null ? spray.m_exinguishTime : 0.5f;
                time /= Mathf.Max(0.1f, Plugin.Skills.Multiplier(hose.State.Chef, Skill.Firefighting));
                if (hit == null) hit = new List<ServerFlammable>();
                hit.Clear();
                Transform chef = hose.State.Chef.transform;
                foreach (ServerFlammable fire in ServerFlammable.GetAllOnFire())
                    if (fire != null && InCone(chef, fire.transform.position, distance, angle)) hit.Add(fire);
                for (int i = 0; i < hit.Count; i++)
                {
                    if (hit[i] == null) continue;
                    hit[i].FightFire(time, dt, true);
                    if (hit[i].OnFire()) Pulse(hit[i].gameObject, Special.BuiltInExtinguisher, null);
                }
            }
        }

        private static Hose HoseOf(ChefState state)
        {
            Hose hose;
            if (!s_hoses.TryGetValue(state.Id, out hose))
            {
                hose = new Hose { State = state };
                s_hoses[state.Id] = hose;
            }
            hose.State = state;
            return hose;
        }

        // USE held, empty hands, free to act, and a fire in the cone (so chopping or washing with
        // empty hands far from any fire never sprays). A short grace avoids flicker at the edge.
        private static bool WantsHose(ChefState state, Hose hose, float distance, float angle)
        {
            PlayerControls controls = state.Controls;
            PlayerControls.ControlSchemeData scheme = controls.ControlScheme;
            if (scheme == null || scheme.m_worksurfaceUseButton == null || !scheme.m_worksurfaceUseButton.IsDown()) return false;
            if (!controls.enabled || controls.IsSuppressed() || ClientHeld(state) != null) return false;
            bool fire = false;
            Transform chef = state.Chef.transform;
            foreach (ClientFlammable flammable in ClientFlammable.GetAllOnFire())
                if (flammable != null && InCone(chef, flammable.transform.position, distance, angle))
                {
                    fire = true;
                    break;
                }
            if (fire)
            {
                hose.NoFireSince = -1f;
                return true;
            }
            if (!hose.On) return false;
            if (hose.NoFireSince < 0f) hose.NoFireSince = SpecialRuntime.Now;
            return SpecialRuntime.Now - hose.NoFireSince < 0.4f;
        }

        private static void OnHoseMessage(SpecialMessage message)
        {
            ChefState state = SpecialRuntime.StateById(message.Chef);
            if (state == null || state.Chef == null || !state.Has(Special.BuiltInExtinguisher)) return;
            Hose hose = HoseOf(state);
            bool on = message.I != 0;
            if (hose.On == on) return;
            hose.On = on;
            hose.NoFireSince = -1f;
            SetHoseVisual(hose, on);
        }

        // Every machine: the extinguisher's own spray effect and sound on the chef, who stands still
        // while spraying like with the real one.
        private static void SetHoseVisual(Hose hose, bool on)
        {
            ChefState state = hose.State;
            try
            {
                if (state != null && state.Controls != null) state.Controls.SetMovementScale(on ? 0f : 1f);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("HoseHands.Movement", error);
            }
            if (on)
            {
                FireExtinguishSpray spray = Extinguisher();
                if (spray == null || state == null || state.Chef == null) return;
                try
                {
                    if (hose.Effect == null && spray.m_sprayEffectPrefab != null)
                    {
                        hose.Effect = Object.Instantiate(spray.m_sprayEffectPrefab, state.Chef.transform);
                        hose.Effect.transform.localPosition = new Vector3(0f, 0.7f, 0.45f);
                        hose.Effect.transform.localRotation = Quaternion.identity;
                    }
                    if (!hose.AudioOn)
                    {
                        hose.Audio = spray.m_audioTag;
                        GameUtils.StartAudio(hose.Audio, hose, state.Chef.layer);
                        hose.AudioOn = true;
                    }
                }
                catch (Exception error)
                {
                    SpecialRuntime.Fail("HoseHands.Effect", error);
                }
                return;
            }
            try
            {
                if (hose.AudioOn) GameUtils.StopAudio(hose.Audio, hose);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("HoseHands.Audio", error);
            }
            hose.AudioOn = false;
            if (hose.Effect != null) StopEffect(hose.Effect);
            hose.Effect = null;
        }

        // Lets the particles fade (ParticleSystem.Stop, by reflection: its module isn't referenced).
        private static void StopEffect(GameObject effect)
        {
            try
            {
                if (!s_particleTypeSearched)
                {
                    s_particleTypeSearched = true;
                    s_particleType = Type.GetType("UnityEngine.ParticleSystem, UnityEngine.ParticleSystemModule") ??
                        Type.GetType("UnityEngine.ParticleSystem, UnityEngine");
                }
                MethodInfo stop = s_particleType != null ? s_particleType.GetMethod("Stop", Type.EmptyTypes) : null;
                if (stop != null)
                {
                    effect.transform.SetParent(null, true);
                    foreach (Component system in effect.GetComponentsInChildren(s_particleType))
                        stop.Invoke(system, null);
                    Object.Destroy(effect, 3f);
                    return;
                }
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("HoseHands.StopEffect", error);
            }
            Object.Destroy(effect);
        }

        // --- PYROPHOBE (player's machine) ---------------------------------------------------------

        // The host refuses the spray (see PyrophobeSprayPatch); here the player hears a "no".
        private static void TickPyrophobe()
        {
            for (int i = 0; i < s_pyro.Count; i++)
            {
                ChefState state = s_pyro[i];
                if (!state.Local || state.Controls == null) continue;
                PlayerControls.ControlSchemeData scheme = state.Controls.ControlScheme;
                bool down = scheme != null && scheme.m_worksurfaceUseButton != null && scheme.m_worksurfaceUseButton.IsDown();
                bool was = state.Get("Pyrophobe.down", false);
                state.Set("Pyrophobe.down", down);
                if (!down || was) continue;
                GameObject held = ClientHeld(state);
                if (held != null && held.GetComponent<FireExtinguishSpray>() != null) SpecialHud.Deny(state, Lang.T("TOO SCARED OF FIRE", "SỢ LỬA LẮM"));
            }
        }
    }

    // ==== patches ======================================================================================

    /// <summary>
    /// SIZZLE MASTER x2, OVERCLOCK (cooker), COLD HANDS / RESCUE holds, FIRE MARSHAL's slower
    /// burning. Runs after COOKING / SLOW BURN (low priority) so its caps see the final delta; the
    /// specials' multipliers compose up to x4 together, with the skill on top.
    /// </summary>
    [HarmonyPatch(typeof(ServerCookingHandler), "Cook")]
    internal static class CookingCookPatch
    {
        [HarmonyPriority(Priority.Low)]
        private static void Prefix(ServerCookingHandler __instance, ref float _cookingDeltatTime)
        {
            if (!CookingModule.CookActive || !SpecialRuntime.InLevel) return;
            try
            {
                _cookingDeltatTime = CookingModule.AdjustCook(__instance, _cookingDeltatTime);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Cooking.Cook", error);
            }
        }
    }

    /// <summary>OVERCLOCK (mixer) and STEADY MIXER, after COOKING / SLOW BURN.</summary>
    [HarmonyPatch(typeof(ServerMixingHandler), "Mix")]
    internal static class CookingMixPatch
    {
        [HarmonyPriority(Priority.Low)]
        private static void Prefix(ServerMixingHandler __instance, ref float _deltaTime)
        {
            if ((CookingModule.StationMixScale == 1f && !CookingModule.AnySteady) || !SpecialRuntime.InLevel) return;
            try
            {
                _deltaTime = CookingModule.AdjustMix(__instance, _deltaTime);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Cooking.Mix", error);
            }
        }
    }

    /// <summary>OVERCLOCK: marks the cooker being updated so Cook runs faster.</summary>
    [HarmonyPatch(typeof(ServerCookingStation), "UpdateSynchronising")]
    internal static class OverclockCookerPatch
    {
        private static void Prefix(ServerCookingStation __instance)
        {
            if (CookingModule.OverclockUntil.Count == 0) return;
            try
            {
                float until;
                if (CookingModule.OverclockUntil.TryGetValue(__instance.gameObject.GetInstanceID(), out until) &&
                    until > SpecialRuntime.Now)
                {
                    CookingModule.StationCookScale = CookingModule.OverclockScale;
                    CookingModule.MarkOverclocked(__instance);
                }
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Overclock.Cooker", error);
            }
        }

        private static void Finalizer()
        {
            CookingModule.StationCookScale = 1f;
        }
    }

    /// <summary>OVERCLOCK: marks the mixer being updated so Mix runs faster.</summary>
    [HarmonyPatch(typeof(ServerMixingStation), "UpdateSynchronising")]
    internal static class OverclockMixerPatch
    {
        private static void Prefix(ServerMixingStation __instance)
        {
            if (CookingModule.OverclockUntil.Count == 0) return;
            try
            {
                float until;
                if (CookingModule.OverclockUntil.TryGetValue(__instance.gameObject.GetInstanceID(), out until) &&
                    until > SpecialRuntime.Now)
                {
                    CookingModule.StationMixScale = CookingModule.OverclockScale;
                    CookingModule.MarkOverclocked(__instance);
                }
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Overclock.Mixer", error);
            }
        }

        private static void Finalizer()
        {
            CookingModule.StationMixScale = 1f;
        }
    }

    /// <summary>Whose pot: a chef picks up (or catches) a pot, pan or bowl.</summary>
    [HarmonyPatch(typeof(ServerPlayerAttachmentCarrier), "CarryItem")]
    internal static class CookingCarryPatch
    {
        private static void Postfix(ServerPlayerAttachmentCarrier __instance, GameObject _object)
        {
            if (!SpecialRuntime.InLevel || _object == null || __instance == null) return;
            try
            {
                if (_object.GetComponent<ServerCookingHandler>() != null || _object.GetComponent<ServerMixingHandler>() != null)
                    CookingModule.NoteHandled(__instance.gameObject, _object, true);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Cooking.Carry", error);
            }
        }
    }

    /// <summary>Whose pot: a chef puts a pot, pan or bowl down, or puts food into one.</summary>
    [HarmonyPatch(typeof(PlayerControlsHelper), "PlaceHeldItem_Server")]
    internal static class CookingPlacementPatch
    {
        private static void Prefix(PlayerControls _control, out GameObject __state)
        {
            __state = null;
            if (_control == null || !SpecialRuntime.InLevel) return;
            try
            {
                ServerPlayerAttachmentCarrier carrier = _control.GetComponent<ServerPlayerAttachmentCarrier>();
                __state = carrier != null ? carrier.InspectCarriedItem() : null;
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Cooking.Place", error);
            }
        }

        private static void Postfix(PlayerControls _control, GameObject _target, GameObject __state)
        {
            if (_control == null || __state == null || !SpecialRuntime.InLevel) return;
            try
            {
                ServerPlayerAttachmentCarrier carrier = _control.GetComponent<ServerPlayerAttachmentCarrier>();
                if (carrier != null && carrier.InspectCarriedItem() == __state) return; // nothing was placed
                Credit(_control.gameObject, __state, _target);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Cooking.Placed", error);
            }
        }

        /// <summary>
        /// A chef put <paramref name="placed"/> onto or into <paramref name="target"/> (by hand, or a
        /// Strong Arm toss that came down there).
        /// </summary>
        internal static void Credit(GameObject chef, GameObject placed, GameObject target)
        {
            if (chef == null || placed == null || !SpecialRuntime.InLevel) return;
            if (placed.GetComponent<ServerCookingHandler>() != null || placed.GetComponent<ServerMixingHandler>() != null)
            {
                CookingModule.NoteHandled(chef, placed, true);
                return;
            }
            // Food went into the pot faced (or the pot on the station faced).
            GameObject container = ContainerOf(target);
            if (container != null) CookingModule.NoteHandled(chef, container, false);
        }

        private static GameObject ContainerOf(GameObject target)
        {
            if (target == null) return null;
            if (target.GetComponent<ServerCookingHandler>() != null || target.GetComponent<ServerMixingHandler>() != null) return target;
            ServerAttachStation station = target.GetComponent<ServerAttachStation>();
            if (station == null || !station.HasItem()) return null;
            GameObject item = station.InspectItem();
            if (item == null) return null;
            return item.GetComponent<ServerCookingHandler>() != null || item.GetComponent<ServerMixingHandler>() != null ? item : null;
        }
    }

    /// <summary>Whose pot: thrown food lands in a pot, pan or bowl (the thrower filled it).</summary>
    [HarmonyPatch(typeof(ServerIngredientCatcher), "HandleCatch")]
    internal static class CookingCatchPatch
    {
        private static void Prefix(ServerIngredientCatcher __instance, ICatchable _object)
        {
            if (!SpecialRuntime.InLevel || __instance == null || _object == null) return;
            try
            {
                GameObject container = __instance.gameObject;
                if (container.GetComponent<ServerCookingHandler>() == null && container.GetComponent<ServerMixingHandler>() == null) return;
                GameObject food = _object.AccessGameObject();
                IThrowable throwable = food != null ? food.RequestInterface<IThrowable>() : null;
                IThrower thrower = throwable != null ? throwable.GetThrower() : null;
                MonoBehaviour behaviour = thrower as MonoBehaviour;
                if (behaviour != null) CookingModule.NoteHandled(behaviour.gameObject, container, false);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Cooking.Catch", error);
            }
        }
    }

    /// <summary>FIRE MARSHAL: a protected cooker never ignites (its food still burns, half as fast).</summary>
    [HarmonyPatch(typeof(ServerFlammable), "Ignite")]
    internal static class FireMarshalIgnitePatch
    {
        private static bool Prefix(ServerFlammable __instance)
        {
            if (!CookingModule.AnyMarshal || !SpecialRuntime.InLevel) return true;
            try
            {
                if (__instance.OnFire() || !CookingModule.IsProtectedCooker(__instance)) return true;
                CookingModule.MarkProtected(__instance, true);
                return false;
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("FireMarshal.Ignite", error);
                return true;
            }
        }
    }

    /// <summary>
    /// Fire catching on from a neighbour: FIRE MARSHAL cookers don't heat up at all, and nothing
    /// near a SLOW SPREAD chef does either. (A fire that is already burning and
    /// regrowing after being fought is left alone.)
    /// </summary>
    [HarmonyPatch(typeof(ServerFlammable), "OnFireIncrease")]
    internal static class SlowSpreadPatch
    {
        private static bool Prefix(ServerFlammable __instance, ref float _deltaTime)
        {
            if ((!CookingModule.AnyMarshal && !CookingModule.AnySlow) || !SpecialRuntime.InLevel) return true;
            try
            {
                if (__instance.OnFire()) return true;
                if (CookingModule.AnyMarshal && CookingModule.IsProtectedCooker(__instance))
                {
                    CookingModule.MarkProtected(__instance, false);
                    return false;
                }
                if (CookingModule.AnySlow && _deltaTime > 0f && CookingModule.NearSlowSpread(__instance.transform.position))
                {
                    CookingModule.MarkFirebreak(__instance);
                    return false;
                }
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("SlowSpread.Increase", error);
            }
            return true;
        }
    }

    /// <summary>FIREPROOF: no knock-back from a fire hazard (backup to the ignored collisions).</summary>
    [HarmonyPatch(typeof(ClientPlayerControlsImpl_Default), "OnFireHazardCollision")]
    internal static class FireproofKnockbackPatch
    {
        private static bool Prefix(ClientPlayerControlsImpl_Default __instance)
        {
            if (!CookingModule.AnyFireproof) return true;
            try
            {
                return !SpecialRuntime.Has(__instance.gameObject, Special.Fireproof);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Fireproof.Knockback", error);
                return true;
            }
        }
    }

    /// <summary>PYROPHOBE: the host never lets a fire extinguisher spray in this chef's hands.</summary>
    [HarmonyPatch(typeof(ServerSprayingUtensil), "StartSpray")]
    internal static class PyrophobeSprayPatch
    {
        private static bool Prefix(ServerSprayingUtensil __instance)
        {
            if (!SpecialRuntime.InLevel) return true;
            try
            {
                if (!(__instance is ServerFireExtinguishSpray)) return true;
                GameObject carrier = __instance.Carrier;
                if (!SpecialRuntime.Has(carrier, Special.Pyrophobe)) return true;
                SpecialFx.TextAll(carrier, Lang.Both("TOO SCARED!", "SỢ QUÁ!"), Special.Pyrophobe);
                return false;
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Pyrophobe.Spray", error);
                return true;
            }
        }
    }
}
