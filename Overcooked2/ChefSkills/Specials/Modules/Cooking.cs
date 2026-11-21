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
    /// ticks); the game's own entity sync then shows it on every client. The few things every
    /// machine does itself are physics/visual: FIREPROOF's collisions, HOSE HANDS' spray effect
    /// and GREASE TRAIL's slide.
    /// </summary>
    internal sealed class CookingModule : ISpecialModule
    {
        // --- tuning -------------------------------------------------------------------------------
        private const float HotHandsRate = 0.5f;
        private const float ColdHandsPause = 6f;
        private const float OverclockSeconds = 10f;
        private const float OverclockScale = 2f;
        private const float SizzleScale = 2f;
        private const float FireBlastRadius = 3f;
        private const float SlowSpreadRadius = 4f;
        internal const float SlowSpreadScale = 0.5f;
        private const float GreaseSeconds = 3f;
        private const float GreasePointLife = 3f;
        private const float GreasePointEvery = 0.12f;
        private const float GreaseRadius = 0.55f;
        private const float GreaseFreeze = 1.1f;
        private const float GreaseImmune = 2.5f;
        // "Perfectly cooked / mixed": just past done (Cooked needs progress > time), far from the
        // 1.3x over-doing warning.
        private const float DoneCap = 1.03f;

        // Network topics: (Special * 10 + n).
        private static readonly ushort TopicHose = (ushort)((int)Special.BuiltInExtinguisher * 10 + 1);
        private static readonly ushort TopicSlip = (ushort)((int)Special.GreaseTrail * 10 + 1);

        // Private game state reached by name (validated by the checker).
        private static readonly FieldInfo CookingDataField = AccessTools.Field(typeof(ServerCookingHandler), "m_ServerData");

        // --- per-level state ----------------------------------------------------------------------

        // Chefs with each special, refreshed twice a second (the per-frame paths read these).
        private static readonly List<ChefState> s_hot = new List<ChefState>();
        private static readonly List<ChefState> s_cold = new List<ChefState>();
        private static readonly List<ChefState> s_fireproof = new List<ChefState>();
        private static readonly List<ChefState> s_slow = new List<ChefState>();
        private static readonly List<ChefState> s_hose = new List<ChefState>();
        private static readonly List<ChefState> s_grease = new List<ChefState>();
        private static readonly List<ChefState> s_pyro = new List<ChefState>();
        internal static bool AnySizzle, AnySteady, AnyMarshal, AnyCold, AnySlow, AnyFireproof;
        private static float s_nextRefresh;
        private static float s_nextFireproof;

        // Pot (instance ID) -> SpecialRuntime.Now until which its burning is paused (COLD HANDS).
        internal static readonly Dictionary<int, float> ColdUntil = new Dictionary<int, float>();
        // Cooker or mixer (instance ID) -> Now until which it runs twice as fast (OVERCLOCK).
        internal static readonly Dictionary<int, float> OverclockUntil = new Dictionary<int, float>();
        // Cooker (instance ID) -> the chef who last put a pot on it or food into its pot (FIRE MARSHAL).
        internal static readonly Dictionary<int, GameObject> CookerUser = new Dictionary<int, GameObject>();
        // Flammable (instance ID) -> its cooker, or null (cached: fire code asks every frame).
        private static readonly Dictionary<int, ServerCookingStation> s_cookerOf = new Dictionary<int, ServerCookingStation>();

        // Set while a cooker / mixer updates, read by the Cook / Mix prefixes it calls.
        internal static float StationCookScale = 1f;
        internal static float StationMixScale = 1f;

        private struct GreasePoint
        {
            public Vector3 Position;
            public float Until;
            public uint Owner;
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
            SpecialRuntime.RegisterActive(Special.Rescue, new ActiveHandler { Execute = ExecuteRescue });
            SpecialRuntime.RegisterActive(Special.Overclock, new ActiveHandler { Execute = ExecuteOverclock, Duration = OverclockSeconds });
            SpecialRuntime.RegisterActive(Special.FireBlast, new ActiveHandler { Execute = ExecuteFireBlast });
            SpecialRuntime.RegisterActive(Special.GreaseTrail, new ActiveHandler { Execute = ExecuteGrease, Duration = GreaseSeconds });
            SpecialRuntime.Tick += OnTick;
            SpecialRuntime.LevelStarted += Reset;
            SpecialRuntime.LevelEnded += Reset;
            SpecialNet.Register(TopicHose, OnHoseMessage);
            SpecialNet.Register(TopicSlip, OnSlipMessage);
        }

        private static void Reset()
        {
            foreach (Hose hose in s_hoses.Values) SetHoseVisual(hose, false);
            s_hoses.Clear();
            s_hot.Clear(); s_cold.Clear(); s_fireproof.Clear(); s_slow.Clear();
            s_hose.Clear(); s_grease.Clear(); s_pyro.Clear();
            AnySizzle = AnySteady = AnyMarshal = AnyCold = AnySlow = AnyFireproof = false;
            ColdUntil.Clear();
            OverclockUntil.Clear();
            CookerUser.Clear();
            s_cookerOf.Clear();
            s_greasePoints.Clear();
            s_extinguisher = null;
            s_nextExtinguisherSearch = 0f;
            s_nextRefresh = 0f;
            StationCookScale = 1f;
            StationMixScale = 1f;
        }

        private static void RefreshLists()
        {
            s_nextRefresh = Time.unscaledTime + 0.5f;
            s_hot.Clear(); s_cold.Clear(); s_fireproof.Clear(); s_slow.Clear();
            s_hose.Clear(); s_grease.Clear(); s_pyro.Clear();
            AnySizzle = AnySteady = AnyMarshal = false;
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState state = chefs[i];
                if (state.Chef == null) continue;
                if (state.Has(Special.HotHands)) s_hot.Add(state);
                if (state.Has(Special.ColdHands)) s_cold.Add(state);
                if (state.Has(Special.Fireproof)) s_fireproof.Add(state);
                if (state.Has(Special.SlowSpread)) s_slow.Add(state);
                if (state.Has(Special.BuiltInExtinguisher)) s_hose.Add(state);
                if (state.Has(Special.GreaseTrail)) s_grease.Add(state);
                if (state.Has(Special.Pyrophobe)) s_pyro.Add(state);
                if (state.Has(Special.SizzleMaster)) AnySizzle = true;
                if (state.Has(Special.SteadyMixer)) AnySteady = true;
                if (state.Has(Special.FireMarshal)) AnyMarshal = true;
            }
            AnyCold = s_cold.Count > 0;
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
                if (s_cold.Count > 0) TickColdHands();
                if (s_grease.Count > 0 || s_greasePoints.Count > 0) TickGrease();
                if (OverclockUntil.Count > 0) Expire(OverclockUntil);
                if (ColdUntil.Count > 0 && !AnyCold) ColdUntil.Clear();
            }
            if (AnyFireproof && Time.unscaledTime >= s_nextFireproof) TickFireproof();
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

        // HOT HANDS (host): a pot that had started cooking keeps cooking in the chef's hands at half
        // speed, up to perfectly done and never further. SetCookingProgress is the game's own setter,
        // so the client progress bars follow through the normal cooking-state sync.
        private static void TickHotHands(float dt)
        {
            for (int i = 0; i < s_hot.Count; i++)
            {
                GameObject held = ServerHeld(s_hot[i]);
                if (held == null) continue;
                ServerCookingHandler cooking = held.GetComponent<ServerCookingHandler>();
                if (cooking == null) continue;
                float progress = cooking.GetCookingProgress();
                float time = cooking.AccessCookingTime;
                float cap = time * DoneCap;
                if (time <= 0f || progress <= 0f || progress >= cap) continue;
                ServerIngredientContainer contents = held.GetComponent<ServerIngredientContainer>();
                if (contents != null && !contents.HasContents()) continue;
                cooking.SetCookingProgress(Mathf.Min(cap, progress + dt * HotHandsRate));
            }
        }

        // COLD HANDS (host): every pick-up or put-down of a pot by the chef pauses its burning.
        private static void TickColdHands()
        {
            float until = SpecialRuntime.Now + ColdHandsPause;
            for (int i = 0; i < s_cold.Count; i++)
            {
                ChefState state = s_cold[i];
                GameObject held = ServerHeld(state);
                GameObject before = state.Get<GameObject>("ColdHands.held", null);
                if (held == before) continue;
                state.Set("ColdHands.held", held);
                if (before != null && before.GetComponent<ServerCookingHandler>() != null) ColdUntil[before.GetInstanceID()] = until;
                if (held != null && held.GetComponent<ServerCookingHandler>() != null) ColdUntil[held.GetInstanceID()] = until;
            }
            if (ColdUntil.Count > 16) Expire(ColdUntil);
        }

        // --- the Cook / Mix adjustments (host) ----------------------------------------------------

        internal static bool CookActive
        {
            get { return StationCookScale != 1f || AnySizzle || (AnyCold && ColdUntil.Count > 0); }
        }

        internal static float AdjustCook(ServerCookingHandler handler, float delta)
        {
            delta *= StationCookScale;
            GameObject pot = handler.gameObject;
            if (AnySizzle && SpecialRuntime.Has(LevelState.OwnerOf(pot), Special.SizzleMaster)) delta *= SizzleScale;
            float until;
            if (AnyCold && ColdUntil.TryGetValue(pot.GetInstanceID(), out until) && until > SpecialRuntime.Now)
                delta = Cap(handler.GetCookingProgress(), delta, handler.AccessCookingTime * DoneCap);
            return delta;
        }

        internal static float AdjustMix(ServerMixingHandler handler, float delta)
        {
            delta *= StationMixScale;
            if (AnySteady && SpecialRuntime.Has(LevelState.OwnerOf(handler.gameObject), Special.SteadyMixer))
                delta = Cap(handler.GetMixingProgress(), delta, handler.AccessMixingTime * DoneCap);
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

        /// <summary>FIRE MARSHAL: this flammable is a cooker its last user protects.</summary>
        internal static bool IsProtectedCooker(ServerFlammable flammable)
        {
            ServerCookingStation cooker = CookerOf(flammable);
            if (cooker == null) return false;
            GameObject user;
            if (!CookerUser.TryGetValue(cooker.gameObject.GetInstanceID(), out user) || user == null)
            {
                // Nobody placed anything here this level: whoever filled or placed the pot on it.
                ServerAttachStation station = cooker.GetComponent<ServerAttachStation>();
                user = station != null && station.HasItem() ? LevelState.OwnerOf(station.InspectItem()) : null;
            }
            return SpecialRuntime.Has(user, Special.FireMarshal);
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

        /// <summary>FIRE MARSHAL bookkeeping: the cooker a placement landed on or into.</summary>
        internal static void NotePlacement(GameObject chef, GameObject target, GameObject placed)
        {
            ServerCookingStation cooker = null;
            if (placed != null) cooker = placed.GetComponentInParent<ServerCookingStation>();
            if (cooker == null && target != null) cooker = target.GetComponentInParent<ServerCookingStation>();
            if (cooker != null) CookerUser[cooker.gameObject.GetInstanceID()] = chef;
        }

        // --- finding what the chef faces (host) ---------------------------------------------------

        private static float FacingScore(ChefState state, Component candidate, float range)
        {
            Vector3 offset = candidate.transform.position - state.Position;
            offset.y = 0f;
            float distance = offset.magnitude;
            if (distance > range) return float.MaxValue;
            float angle = distance < 0.3f ? 0f : Vector3.Angle(state.Forward, offset);
            if (angle > 75f) return float.MaxValue;
            return distance + angle / 45f;
        }

        private static Collider[] Ahead(ChefState state)
        {
            Vector3 centre = state.Position + state.Forward * 1.0f + Vector3.up * 0.5f;
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

        // RESCUE: a burnt pot (in hand first, else the one faced) goes back to just done.
        private static bool ExecuteRescue(ChefState state)
        {
            ServerCookingHandler target = null;
            GameObject held = ServerHeld(state);
            if (held != null)
            {
                ServerCookingHandler pot = held.GetComponent<ServerCookingHandler>();
                if (pot != null && IsRuined(pot)) target = pot;
            }
            if (target == null)
            {
                float best = float.MaxValue;
                bool bestBurnt = false;
                foreach (Collider collider in Ahead(state))
                {
                    ServerCookingHandler pot = PotOf(collider.gameObject);
                    if (pot == null || pot.gameObject == held || !IsRuined(pot)) continue;
                    float score = FacingScore(state, pot, 2.2f);
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
            if (target == null) return false;
            Rescue(target);
            return true;
        }

        private static void Rescue(ServerCookingHandler pot)
        {
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
            ColdUntil.Remove(pot.gameObject.GetInstanceID());
        }

        // OVERCLOCK: the faced cooker or mixer runs twice as fast for 10 s.
        private static bool ExecuteOverclock(ChefState state)
        {
            Component target = null;
            float best = float.MaxValue;
            foreach (Collider collider in Ahead(state))
            {
                Component station = collider.GetComponentInParent<ServerCookingStation>();
                if (station == null) station = collider.GetComponentInParent<ServerMixingStation>();
                if (station == null) continue;
                float score = FacingScore(state, station, 2.2f);
                if (score < best)
                {
                    best = score;
                    target = station;
                }
            }
            if (target == null) return false;
            OverclockUntil[target.gameObject.GetInstanceID()] = SpecialRuntime.Now + OverclockSeconds;
            return true;
        }

        // FIRE BLAST: every fire within 3 m goes out now.
        private static bool ExecuteFireBlast(ChefState state)
        {
            var near = new List<ServerFlammable>();
            float range = FireBlastRadius * FireBlastRadius;
            foreach (ServerFlammable fire in ServerFlammable.GetAllOnFire())
            {
                if (fire == null) continue;
                Vector3 offset = fire.transform.position - state.Position;
                offset.y = 0f;
                if (offset.sqrMagnitude <= range) near.Add(fire);
            }
            // FightFire (not Extinguish) also holds off re-ignition from neighbours for a moment.
            for (int i = 0; i < near.Count; i++)
                if (near[i] != null && near[i].OnFire()) near[i].FightFire(0.001f, 1f, true);
            return near.Count > 0;
        }

        // GREASE TRAIL: the trail is laid by the host's tick while the active lasts.
        private static bool ExecuteGrease(ChefState state)
        {
            state.Set("GreaseTrail.next", 0f);
            return state.Chef != null;
        }

        // --- GREASE TRAIL (host) ------------------------------------------------------------------

        private static void TickGrease()
        {
            float now = SpecialRuntime.Now;
            for (int i = 0; i < s_grease.Count; i++)
            {
                ChefState state = s_grease[i];
                if (state.Chef == null || state.ActiveUntil <= now) continue;
                if (now < state.Get("GreaseTrail.next", 0f)) continue;
                state.Set("GreaseTrail.next", now + GreasePointEvery);
                s_greasePoints.Add(new GreasePoint { Position = state.Position, Until = now + GreasePointLife, Owner = state.Id });
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
        }

        // --- FIREPROOF (every machine) ------------------------------------------------------------

        // Fire hazards are solid capsules (enabled while burning) that knock chefs back on contact.
        // Every machine that moves the chef must let it pass, so each ignores those collisions
        // locally for fireproof chefs, a few times a second (re-enabling a collider resets it).
        private static void TickFireproof()
        {
            s_nextFireproof = Time.unscaledTime + 0.1f;
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
                    if (hit[i] != null) hit[i].FightFire(time, dt, true);
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
                if (held != null && held.GetComponent<FireExtinguishSpray>() != null) SpecialHud.Deny(state);
            }
        }
    }

    // ==== patches ======================================================================================

    /// <summary>
    /// SIZZLE MASTER x2, OVERCLOCK x2 (cooker), COLD HANDS pause. Runs after COOKING / SLOW BURN
    /// (low priority) so its caps see the final delta; all multipliers compose.
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

    /// <summary>OVERCLOCK x2 (mixer) and STEADY MIXER's cap, after COOKING / SLOW BURN.</summary>
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

    /// <summary>OVERCLOCK: marks the cooker being updated so Cook runs at double speed.</summary>
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
                    CookingModule.StationCookScale = 2f;
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

    /// <summary>OVERCLOCK: marks the mixer being updated so Mix runs at double speed.</summary>
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
                    CookingModule.StationMixScale = 2f;
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

    /// <summary>FIRE MARSHAL: remembers who last put a pot on a cooker or food into its pot.</summary>
    [HarmonyPatch(typeof(PlayerControlsHelper), "PlaceHeldItem_Server")]
    internal static class FireMarshalPlacementPatch
    {
        private static void Prefix(PlayerControls _control, out GameObject __state)
        {
            __state = null;
            if (!CookingModule.AnyMarshal || _control == null) return;
            try
            {
                ServerPlayerAttachmentCarrier carrier = _control.GetComponent<ServerPlayerAttachmentCarrier>();
                __state = carrier != null ? carrier.InspectCarriedItem() : null;
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("FireMarshal.Place", error);
            }
        }

        private static void Postfix(PlayerControls _control, GameObject _target, GameObject __state)
        {
            if (!CookingModule.AnyMarshal || _control == null || __state == null) return;
            try
            {
                ServerPlayerAttachmentCarrier carrier = _control.GetComponent<ServerPlayerAttachmentCarrier>();
                if (carrier != null && carrier.InspectCarriedItem() == __state) return; // nothing was placed
                // The item may be gone (food tipped into a pot): fall back to the target.
                CookingModule.NotePlacement(_control.gameObject, _target, __state);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("FireMarshal.Placed", error);
            }
        }
    }

    /// <summary>FIRE MARSHAL: a protected cooker never ignites (its food still burns).</summary>
    [HarmonyPatch(typeof(ServerFlammable), "Ignite")]
    internal static class FireMarshalIgnitePatch
    {
        private static bool Prefix(ServerFlammable __instance)
        {
            if (!CookingModule.AnyMarshal || !SpecialRuntime.InLevel) return true;
            try
            {
                return !CookingModule.IsProtectedCooker(__instance);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("FireMarshal.Ignite", error);
                return true;
            }
        }
    }

    /// <summary>
    /// Fire catching on from a neighbour: FIRE MARSHAL cookers don't heat up at all, and near a
    /// SLOW SPREAD chef it builds up at half speed. (A fire that is already burning and regrowing
    /// after being fought is left alone.)
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
                if (CookingModule.AnyMarshal && CookingModule.IsProtectedCooker(__instance)) return false;
                if (CookingModule.AnySlow && CookingModule.NearSlowSpread(__instance.transform.position))
                    _deltaTime *= CookingModule.SlowSpreadScale;
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
            try
            {
                return !(__instance is ServerFireExtinguishSpray && SpecialRuntime.Has(__instance.Carrier, Special.Pyrophobe));
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Pyrophobe.Spray", error);
                return true;
            }
        }
    }
}
