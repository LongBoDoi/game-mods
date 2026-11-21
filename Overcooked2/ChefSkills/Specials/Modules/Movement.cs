using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ChefSkills.Specials
{
    /// <summary>
    /// Movement specials: Sprinter, Double Dash, Charge Dash, Momentum, Slipstream, Sure-Footed,
    /// Immovable, Ghost, Conveyor Rider, Big Chef, Tiny Chef, Overdrive, Weatherproof, and the
    /// quirks Heavy Boots, Clumsy and Sleepy.
    ///
    /// Who decides what (movement is simulated on the host and on the chef's own machine, which
    /// must agree):
    /// - Sprinter, Momentum, Charge Dash: only the player's own machine sees its held buttons and
    ///   input, so it decides and tells every machine (own topics); every machine then applies the
    ///   same speed through the framework's modifier lists.
    /// - Double Dash, Heavy Boots, Charge Dash's "no dash while holding": only the dash decision
    ///   matters, and with DashSync that is made on the player's machine (the host follows).
    /// - Slipstream, Sleepy: the host decides (it has every chef's true position / one clock) and
    ///   broadcasts a buff. Clumsy: the host drops the item when it sees the dash start.
    /// - Sure-Footed, Weatherproof, Conveyor Rider, Ghost, Immovable, Big/Tiny Chef: plain rules
    ///   every machine applies the same way where the game simulates them.
    /// - Overdrive: OnActivated (every machine) starts the framework's NoDashCooldown buff.
    /// </summary>
    internal sealed class MovementSpecials : ISpecialModule
    {
        private const string DataKey = "Movement";

        // Own topics: (int)Special * 10 + n.
        private const ushort SprintTopic = (ushort)((int)Special.Sprinter * 10);
        private const ushort ChargeTopic = (ushort)((int)Special.ChargeDash * 10);
        private const ushort MomentumTopic = (ushort)((int)Special.Momentum * 10);

        // Own buff, so it never merges with another module's run-speed buff.
        private const int SlipstreamBuff = (int)Special.Slipstream * 100 + 1;

        private const float SprintBonus = 1.5f;
        private const float SprintStamina = 3f;
        private const float SprintRegen = 0.75f;      // stamina seconds per second while not sprinting
        private const float SprintMinStart = 0.5f;

        private const int DashCharges = 2;

        private const float ChargeFullSeconds = 1f;
        private const float ChargeMaxPower = 2f;
        private const float ReleaseWindow = 0.3f;

        private const float MomentumStep = 0.05f;      // per second of running
        private const float MomentumMax = 0.30f;
        private const float MomentumGrace = 0.3f;      // a stop shorter than this (a turn) keeps it

        private const float SlipstreamRange = 3f;
        private const float SlipstreamWidth = 1.2f;
        private const float SlipstreamBonus = 0.2f;
        private const float SlipstreamEvery = 0.3f;

        private const float BigScale = 1.2f, TinyScale = 0.8f;
        private const float BigSpeed = 0.9f, TinySpeed = 1.12f;
        internal const float KnockFactor = 1.5f;

        private const float OverdriveSeconds = 5f;
        private const float SleepyEvery = 30f, SleepySeconds = 1f;

        private const float CacheEvery = 0.5f;

        /// <summary>Per-chef state of this module, kept in ChefState.Data (cleared with the level).</summary>
        internal sealed class MoveData
        {
            // Sprinter
            public float Stamina = SprintStamina;
            public bool Sprinting, Exhausted;
            public float SprintUntil;
            // Double Dash (owner machine)
            public float Charges = DashCharges;
            // Charge Dash
            public bool Charging;
            public float Charge, ReleaseUntil;
            public float PendingPower, PendingUntil, Power = 1f, PowerUntil, LastDashAt = -100f;
            // Momentum
            public float RunTime, StopTime, SentBonus, Bonus;
            // Sleepy (host)
            public float NextYawn;
            // HUD
        }

        // Caches refreshed every CacheEvery seconds (patches run per object per frame).
        internal static readonly HashSet<int> NoWind = new HashSet<int>();
        internal static readonly Dictionary<int, float> SurfaceFactor = new Dictionary<int, float>();
        internal static bool AnySureFooted, AnyDashHook;
        private static float s_nextCache, s_nextSlipstream;

        private static readonly Dictionary<PlayerPhysicsSurfaceProperties, PlayerPhysicsSurfaceProperties> s_unslipped =
            new Dictionary<PlayerPhysicsSurfaceProperties, PlayerPhysicsSurfaceProperties>();

        // Host: chefs whose dash just started while Clumsy (dropped in the next Tick).
        private static readonly List<ChefState> s_clumsyDrops = new List<ChefState>();

        public void Register()
        {
            SpecialRuntime.RunSpeedModifiers.Add(RunFactor);
            SpecialRuntime.DashSpeedModifiers.Add(DashFactor);
            SpecialRuntime.DashCooldownModifiers.Add(CooldownFactor);
            SpecialRuntime.DashAllowed.Add(CanDash);

            SpecialRuntime.RegisterActive(Special.Overdrive, new ActiveHandler
            {
                Execute = delegate(ChefState state) { return state.Chef != null; },
                OnActivated = delegate(ChefState state)
                {
                    // OnActivated runs on every machine, so a local buff is the same everywhere.
                    SpecialRuntime.AddBuffLocal(state, BuffId.NoDashCooldown, OverdriveSeconds, 1f, state);
                },
                Duration = OverdriveSeconds
            });

            SpecialNet.Register(SprintTopic, OnSprintMessage);
            SpecialNet.Register(ChargeTopic, OnChargeMessage);
            SpecialNet.Register(MomentumTopic, OnMomentumMessage);

            SpecialRuntime.Tick += OnTick;
            SpecialRuntime.LevelStarted += OnLevelStarted;
            SpecialRuntime.LevelEnded += OnLevelEnded;
        }

        // --- state ------------------------------------------------------------------------------

        internal static MoveData Data(ChefState state)
        {
            MoveData data = state.Get<MoveData>(DataKey, null);
            if (data == null)
            {
                data = new MoveData();
                state.Set(DataKey, data);
            }
            return data;
        }

        private static void OnLevelStarted()
        {
            ClearCaches();
            RefreshCaches();
        }

        private static void OnLevelEnded()
        {
            ClearCaches();
        }

        private static void ClearCaches()
        {
            NoWind.Clear();
            SurfaceFactor.Clear();
            AnySureFooted = false;
            AnyDashHook = false;
            s_nextCache = 0f;
            s_nextSlipstream = 0f;
            s_clumsyDrops.Clear();
            foreach (PlayerPhysicsSurfaceProperties copy in s_unslipped.Values)
                if (copy != null) Object.Destroy(copy);
            s_unslipped.Clear();
        }

        private static void RefreshCaches()
        {
            s_nextCache = SpecialRuntime.Now + CacheEvery;
            NoWind.Clear();
            SurfaceFactor.Clear();
            AnySureFooted = false;
            AnyDashHook = false;
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState state = chefs[i];
                if (state.Chef == null) continue;
                int key = state.Chef.GetInstanceID();
                bool calm = state.Has(Special.SureFooted) || state.Has(Special.Weatherproof);
                if (calm) NoWind.Add(key);
                // Conveyor Rider is the explicit choice, so it wins over the "no drag" ones.
                if (state.Has(Special.ConveyorRider)) SurfaceFactor[key] = 2f;
                else if (calm) SurfaceFactor[key] = 0f;
                if (state.Has(Special.SureFooted)) AnySureFooted = true;
                if (state.Has(Special.DoubleDash) || state.Has(Special.ChargeDash) || state.Has(Special.Clumsy))
                    AnyDashHook = true;
            }
            ApplyGhost(chefs);
        }

        // --- what movement reads (every machine, every frame) ------------------------------------

        private static float RunFactor(ChefState state)
        {
            float factor = 1f;
            if (state.Has(Special.BigChef)) factor *= BigSpeed;
            if (state.Has(Special.TinyChef)) factor *= TinySpeed;
            factor *= 1f + SpecialRuntime.BuffStrength(state, SlipstreamBuff);
            if (state.Has(Special.Sprinter) || state.Has(Special.Momentum))
            {
                MoveData data = Data(state);
                if (state.Has(Special.Sprinter) && SpecialRuntime.Now < data.SprintUntil) factor *= SprintBonus;
                if (state.Has(Special.Momentum)) factor *= 1f + data.Bonus;
            }
            return factor;
        }

        private static float DashFactor(ChefState state)
        {
            if (!state.Has(Special.ChargeDash)) return 1f;
            MoveData data = Data(state);
            return SpecialRuntime.Now < data.PowerUntil ? data.Power : 1f;
        }

        private static float CooldownFactor(ChefState state)
        {
            // Only the machine that decides this chef's dashes (its own) counts charges; the host
            // follows a client's dashes without any cooldown anyway.
            if (!state.Local || !state.Has(Special.DoubleDash)) return 1f;
            // A charge left: dash at once. None: wait until one has recharged.
            return Data(state).Charges >= 1f ? 0f : 1e3f;
        }

        private static bool CanDash(ChefState state)
        {
            if (state.Has(Special.HeavyBoots) || state.Has(Special.Sprinter)) return false;
            // Charge Dash: the press only charges; the dash comes on release (see OnTick).
            if (state.Local && state.Has(Special.ChargeDash)) return SpecialRuntime.Now < Data(state).ReleaseUntil;
            return true;
        }

        // --- messages ---------------------------------------------------------------------------

        /// <summary>To every machine (this one at once); a chef without a network ID only here.</summary>
        private static void Send(ChefState state, ushort topic, float a)
        {
            var message = new SpecialMessage { Topic = topic, Chef = state.Id, A = a };
            if (state.Id != 0)
            {
                SpecialNet.SendToAll(message);
                return;
            }
            if (topic == SprintTopic) ApplySprint(state, a);
            else if (topic == ChargeTopic) ApplyCharge(state, a);
            else if (topic == MomentumTopic) Data(state).Bonus = a;
        }

        private static void OnSprintMessage(SpecialMessage message)
        {
            ChefState state = SpecialRuntime.StateById(message.Chef);
            if (state != null) ApplySprint(state, message.A);
        }

        private static void ApplySprint(ChefState state, float seconds)
        {
            // The sprint carries its own end (the stamina left), so a lost "stop" can't last.
            Data(state).SprintUntil = seconds > 0f ? SpecialRuntime.Now + seconds + 0.1f : 0f;
        }

        private static void OnChargeMessage(SpecialMessage message)
        {
            ChefState state = SpecialRuntime.StateById(message.Chef);
            if (state != null) ApplyCharge(state, message.A);
        }

        private static void ApplyCharge(ChefState state, float power)
        {
            MoveData data = Data(state);
            float now = SpecialRuntime.Now;
            float dashTime = DashTime(state);
            if (now - data.LastDashAt < 0.3f)
            {
                // The dash (from DashSync's pulse) got here first: power up the rest of it.
                data.Power = power;
                data.PowerUntil = data.LastDashAt + dashTime;
            }
            else
            {
                // Kept for the next dash this chef starts.
                data.PendingPower = power;
                data.PendingUntil = now + 1f;
            }
        }

        private static void OnMomentumMessage(SpecialMessage message)
        {
            ChefState state = SpecialRuntime.StateById(message.Chef);
            if (state != null) Data(state).Bonus = Mathf.Clamp(message.A, 0f, MomentumMax);
        }

        private static float DashTime(ChefState state)
        {
            return state.Controls != null && state.Controls.Movement != null ? state.Controls.Movement.DashTime : 1f;
        }

        // --- dashes (from the Update_Movement patch) ---------------------------------------------

        /// <summary>A dash just started for this chef on this machine (Update_Movement).</summary>
        internal static void OnDashStarted(ChefState state)
        {
            MoveData data = Data(state);
            float now = SpecialRuntime.Now;
            data.LastDashAt = now;

            if (state.Has(Special.ChargeDash))
            {
                data.ReleaseUntil = 0f;
                if (now < data.PendingUntil)
                {
                    data.Power = data.PendingPower;
                    data.PowerUntil = now + DashTime(state);
                    data.PendingUntil = 0f;
                }
            }
            if (state.Local && state.Has(Special.DoubleDash) && !SpecialRuntime.HasBuff(state, BuffId.NoDashCooldown))
                data.Charges = Mathf.Max(0f, data.Charges - 1f);
            if (SpecialNet.IsServer && state.Has(Special.Clumsy) && !s_clumsyDrops.Contains(state))
                s_clumsyDrops.Add(state);
        }

        /// <summary>Charge Dash: this machine's chef was released and should dash now.</summary>
        internal static bool WantsReleaseDash(ChefState state)
        {
            return state.Local && state.Has(Special.ChargeDash) && SpecialRuntime.Now < Data(state).ReleaseUntil;
        }

        // --- every frame ------------------------------------------------------------------------

        private static void OnTick(float dt)
        {
            if (SpecialRuntime.Now >= s_nextCache) RefreshCaches();
            bool server = SpecialNet.IsServer;
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState state = chefs[i];
                if (state.Chef == null || state.Controls == null) continue;
                try
                {
                    TickChef(state, dt, server);
                }
                catch (Exception error)
                {
                    SpecialRuntime.Fail("Movement.Tick", error);
                }
            }
            if (server)
            {
                DropClumsy();
                if (SpecialRuntime.Now >= s_nextSlipstream)
                {
                    s_nextSlipstream = SpecialRuntime.Now + SlipstreamEvery;
                    Slipstream(chefs);
                }
            }
        }

        private static void TickChef(ChefState state, float dt, bool server)
        {
            if (state.Has(Special.BigChef)) KeepScale(state, BigScale);
            else if (state.Has(Special.TinyChef)) KeepScale(state, TinyScale);

            if (server && state.Has(Special.Sleepy)) Sleepy(state);
            if (!state.Local) return;

            string status = null;
            if (state.Has(Special.Sprinter)) status = Join(status, Sprinter(state, dt));
            if (state.Has(Special.DoubleDash)) status = Join(status, DoubleDash(state, dt));
            if (state.Has(Special.ChargeDash)) status = Join(status, ChargeDash(state, dt));
            if (state.Has(Special.Momentum)) status = Join(status, Momentum(state, dt));
            if (state.Has(Special.Sleepy) && SpecialRuntime.HasBuff(state, BuffId.Frozen)) status = Join(status, "Yawning...");

            state.SetStatus("Movement", status);
        }

        private static string Join(string a, string b)
        {
            if (string.IsNullOrEmpty(b)) return a;
            return string.IsNullOrEmpty(a) ? b : a + "  " + b;
        }

        private static bool Controllable(ChefState state)
        {
            PlayerControls controls = state.Controls;
            return controls.enabled && controls.ControlScheme != null && !controls.m_bRespawning &&
                !SpecialRuntime.HasBuff(state, BuffId.Frozen);
        }

        private static bool DashHeld(ChefState state)
        {
            PlayerControls.ControlSchemeData scheme = state.Controls.ControlScheme;
            // IsDown on the game's gated button doesn't claim any press.
            return scheme != null && scheme.m_dashButton != null && scheme.m_dashButton.IsDown();
        }

        /// <summary>Owner: hold DASH to sprint while there is stamina; tells every machine.</summary>
        private static string Sprinter(ChefState state, float dt)
        {
            MoveData data = Data(state);
            bool down = DashHeld(state);
            bool can = Controllable(state);
            if (data.Sprinting)
            {
                data.Stamina -= dt;
                if (!down || !can || data.Stamina <= 0f)
                {
                    data.Sprinting = false;
                    if (data.Stamina <= 0f)
                    {
                        data.Stamina = 0f;
                        // Ran out while holding: let go first.
                        data.Exhausted = down;
                    }
                    Send(state, SprintTopic, 0f);
                }
            }
            else
            {
                if (!down) data.Exhausted = false;
                data.Stamina = Mathf.Min(SprintStamina, data.Stamina + dt * SprintRegen);
                if (down && can && !data.Exhausted && data.Stamina >= SprintMinStart)
                {
                    data.Sprinting = true;
                    Send(state, SprintTopic, data.Stamina);
                }
            }
            return (data.Sprinting ? "SPRINT " : "Stamina ") + data.Stamina.ToString("0.0") + "s";
        }

        /// <summary>Owner: charges recharge one per normal dash recharge time.</summary>
        private static string DoubleDash(ChefState state, float dt)
        {
            MoveData data = Data(state);
            PlayerControls.MovementData movement = state.Controls.Movement;
            if (movement != null)
            {
                // Outside Update_Movement this is the game's own value (MovementPatch restores it).
                float recharge = movement.DashCooldown / Mathf.Max(0.1f, Plugin.Skills.Multiplier(state.Chef, Skill.Dashing));
                data.Charges = Mathf.Min(DashCharges, data.Charges + dt / Mathf.Max(0.05f, recharge));
            }
            return "Dash " + Mathf.FloorToInt(data.Charges) + "/" + DashCharges;
        }

        /// <summary>Owner: holding DASH charges; letting go dashes (the power goes to every machine).</summary>
        private static string ChargeDash(ChefState state, float dt)
        {
            MoveData data = Data(state);
            bool down = DashHeld(state);
            if (!Controllable(state))
            {
                data.Charging = false;
                data.Charge = 0f;
            }
            else if (down)
            {
                data.Charging = true;
                data.Charge += dt;
            }
            else if (data.Charging)
            {
                data.Charging = false;
                float power = 1f + Mathf.Clamp01(data.Charge / ChargeFullSeconds) * (ChargeMaxPower - 1f);
                data.Charge = 0f;
                // The dash itself happens in Update_Movement (a press is fed to the game there).
                data.ReleaseUntil = SpecialRuntime.Now + ReleaseWindow;
                if (power > 1.05f) Send(state, ChargeTopic, power);
            }
            if (!data.Charging) return null;
            return "Charge " + Mathf.RoundToInt(Mathf.Clamp01(data.Charge / ChargeFullSeconds) * 100f) + "%";
        }

        /// <summary>Owner: +5% per second of running without stopping or interacting.</summary>
        private static string Momentum(ChefState state, float dt)
        {
            MoveData data = Data(state);
            PlayerControls controls = state.Controls;
            PlayerControls.ControlSchemeData scheme = controls.ControlScheme;
            bool interacting = controls.GetCurrentlyInteracting() != null ||
                (scheme != null && ((scheme.m_worksurfaceUseButton != null && scheme.m_worksurfaceUseButton.IsDown()) ||
                                    (scheme.m_pickupButton != null && scheme.m_pickupButton.IsDown())));
            float x = scheme != null && scheme.m_moveX != null ? scheme.m_moveX.GetValue() : 0f;
            float y = scheme != null && scheme.m_moveY != null ? scheme.m_moveY.GetValue() : 0f;
            bool running = Controllable(state) && x * x + y * y > 0.25f && controls.GetUnclampedMovementSpeed() > 0.4f;

            if (interacting || !Controllable(state))
            {
                data.RunTime = 0f;
                data.StopTime = 0f;
            }
            else if (running)
            {
                data.RunTime += dt;
                data.StopTime = 0f;
            }
            else
            {
                data.StopTime += dt;
                if (data.StopTime > MomentumGrace) data.RunTime = 0f;
            }

            float bonus = Mathf.Min(MomentumMax, MomentumStep * Mathf.Floor(data.RunTime));
            if (!Mathf.Approximately(bonus, data.SentBonus))
            {
                data.SentBonus = bonus;
                Send(state, MomentumTopic, bonus);
            }
            return data.Bonus > 0f ? "Momentum +" + Mathf.RoundToInt(data.Bonus * 100f) + "%" : null;
        }

        /// <summary>Host: every 30 s the chef yawns and can't move for a second (a broadcast buff).</summary>
        private static void Sleepy(ChefState state)
        {
            MoveData data = Data(state);
            float now = SpecialRuntime.Now;
            if (data.NextYawn <= 0f)
            {
                data.NextYawn = now + SleepyEvery;
                return;
            }
            if (now < data.NextYawn) return;
            data.NextYawn = now + SleepyEvery;
            SpecialRuntime.BroadcastBuff(state, BuffId.Frozen, SleepySeconds, 1f, state);
        }

        /// <summary>Host: teammates running close behind a Slipstream chef get +20% for a moment.</summary>
        private static void Slipstream(IList<ChefState> chefs)
        {
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState leader = chefs[i];
                if (!leader.Has(Special.Slipstream) || leader.Chef == null || leader.Controls == null) continue;
                if (leader.Controls.GetUnclampedMovementSpeed() < 0.5f) continue;
                Vector3 forward = leader.Forward;
                forward.y = 0f;
                if (forward.sqrMagnitude < 0.01f) continue;
                forward.Normalize();
                foreach (ChefState mate in SpecialRuntime.Teammates(leader))
                {
                    if (mate.Chef == null || mate.Controls == null) continue;
                    if (mate.Controls.GetUnclampedMovementSpeed() < 0.5f) continue;
                    Vector3 offset = mate.Position - leader.Position;
                    offset.y = 0f;
                    float along = Vector3.Dot(offset, forward);
                    if (along > -0.2f || along < -SlipstreamRange) continue;
                    if ((offset - forward * along).magnitude > SlipstreamWidth) continue;
                    Vector3 mateForward = mate.Forward;
                    mateForward.y = 0f;
                    if (Vector3.Dot(mateForward.normalized, forward) < 0.5f) continue;
                    SpecialRuntime.BroadcastBuff(mate, SlipstreamBuff, SlipstreamEvery * 2f, SlipstreamBonus, leader);
                }
            }
        }

        /// <summary>Host: a Clumsy chef that dashed drops what it holds.</summary>
        private static void DropClumsy()
        {
            if (s_clumsyDrops.Count == 0) return;
            for (int i = 0; i < s_clumsyDrops.Count; i++)
            {
                ChefState state = s_clumsyDrops[i];
                try
                {
                    if (state.Chef == null) continue;
                    ServerPlayerAttachmentCarrier carrier = state.Chef.GetComponent<ServerPlayerAttachmentCarrier>();
                    if (carrier != null && carrier.InspectCarriedItem() != null) carrier.TakeItem();
                }
                catch (Exception error)
                {
                    SpecialRuntime.Fail("Clumsy.Drop", error);
                }
            }
            s_clumsyDrops.Clear();
        }

        /// <summary>Big/Tiny Chef: the whole chef (collider included) is scaled, on every machine.</summary>
        private static void KeepScale(ChefState state, float scale)
        {
            // The respawn shrinks the chef and then resets its scale to one: set it again after.
            if (state.Controls.m_bRespawning) return;
            Transform transform = state.Chef.transform;
            if (Mathf.Abs(transform.localScale.x - scale) > 0.01f) transform.localScale = new Vector3(scale, scale, scale);
        }

        /// <summary>
        /// Ghost: the chef's collider ignores every other chef's. Applied again regularly because
        /// disabling a collider (respawn, cannon) forgets it. Every machine does it.
        /// </summary>
        private static void ApplyGhost(IList<ChefState> chefs)
        {
            for (int i = 0; i < chefs.Count; i++)
            {
                if (!chefs[i].Has(Special.Ghost) || chefs[i].Chef == null) continue;
                Collider[] mine = chefs[i].Chef.GetComponents<Collider>();
                for (int j = 0; j < chefs.Count; j++)
                {
                    if (j == i || chefs[j].Chef == null) continue;
                    Collider[] theirs = chefs[j].Chef.GetComponents<Collider>();
                    foreach (Collider a in mine)
                    {
                        if (!Usable(a)) continue;
                        foreach (Collider b in theirs)
                            if (Usable(b)) Physics.IgnoreCollision(a, b, true);
                    }
                }
            }
        }

        private static bool Usable(Collider collider)
        {
            return collider != null && collider.enabled && !collider.isTrigger && collider.gameObject.activeInHierarchy;
        }

        /// <summary>Sure-Footed: the same surface without slipping or sliding (cached copy).</summary>
        internal static PlayerPhysicsSurfaceProperties Unslipped(PlayerPhysicsSurfaceProperties surface)
        {
            PlayerPhysicsSurfaceProperties copy;
            if (s_unslipped.TryGetValue(surface, out copy) && copy != null) return copy;
            copy = ScriptableObject.CreateInstance<PlayerPhysicsSurfaceProperties>();
            copy.SpeedMultiplier = surface.SpeedMultiplier;
            copy.Slippiness = 0f;
            copy.Slidiness = 0f;
            copy.UseOverrideGravityNormal = surface.UseOverrideGravityNormal;
            copy.OverrideGravityNormal = surface.OverrideGravityNormal;
            copy.GravityMultiplier = surface.GravityMultiplier;
            s_unslipped[surface] = copy;
            return copy;
        }
    }

    /// <summary>A dash button that reads as one fresh press (Charge Dash's dash on release).</summary>
    internal sealed class MovementPressButton : LogicalButtonBase
    {
        private static readonly FieldInfo PressClaimedField = AccessTools.Field(typeof(LogicalButtonBase), "m_pressClaimed");
        private ILogicalButton _real;

        internal void Arm(ILogicalButton real)
        {
            _real = real;
            if (PressClaimedField != null) PressClaimedField.SetValue(this, false);
        }

        public override bool IsDown()
        {
            return true;
        }

        public override void GetLogicTreeData(out AcyclicGraph<ILogicalElement, LogicalLinkInfo> _graph,
            out AcyclicGraph<ILogicalElement, LogicalLinkInfo>.Node _head)
        {
            _real.GetLogicTreeData(out _graph, out _head);
        }
    }

    /// <summary>
    /// Sees each dash a chef starts on this machine (the dash timer only goes up when one starts):
    /// Double Dash spends a charge, Charge Dash powers it up, Clumsy (host) drops the held item.
    /// Charge Dash: on release, the game is shown one fresh DASH press so it starts the dash
    /// itself (with its own checks, and DashSync then reports it to the host).
    /// </summary>
    [HarmonyPatch(typeof(ClientPlayerControlsImpl_Default), "Update_Movement")]
    internal static class MovementSpecialsDashPatch
    {
        private static readonly FieldInfo DashTimerField =
            AccessTools.Field(typeof(ClientPlayerControlsImpl_Default), "m_dashTimer");
        private static readonly MovementPressButton Press = new MovementPressButton();
        private static PlayerControls.ControlSchemeData _scheme;
        private static ILogicalButton _real;

        private static void Prefix(ClientPlayerControlsImpl_Default __instance, out float __state)
        {
            __state = float.NaN;
            if (!SpecialRuntime.InLevel || !MovementSpecials.AnyDashHook || DashTimerField == null) return;
            try
            {
                ChefState state = SpecialRuntime.StateOf(__instance.gameObject);
                if (state == null || !(state.Has(Special.DoubleDash) || state.Has(Special.ChargeDash) || state.Has(Special.Clumsy)))
                    return;
                __state = (float)DashTimerField.GetValue(__instance);

                if (MovementSpecials.WantsReleaseDash(state) && state.Controls != null)
                {
                    PlayerControls.ControlSchemeData scheme = state.Controls.ControlScheme;
                    if (scheme != null && scheme.m_dashButton != null && !(scheme.m_dashButton is MovementPressButton))
                    {
                        _scheme = scheme;
                        _real = scheme.m_dashButton;
                        Press.Arm(_real);
                        scheme.m_dashButton = Press;
                    }
                }
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Movement.DashPrefix", error);
            }
        }

        private static void Postfix(ClientPlayerControlsImpl_Default __instance, float __state)
        {
            if (float.IsNaN(__state)) return;
            try
            {
                if ((float)DashTimerField.GetValue(__instance) <= __state) return;
                ChefState state = SpecialRuntime.StateOf(__instance.gameObject);
                if (state != null) MovementSpecials.OnDashStarted(state);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Movement.DashPostfix", error);
            }
        }

        [HarmonyFinalizer]
        private static void Finalizer()
        {
            if (_scheme == null) return;
            if (_scheme.m_dashButton == Press) _scheme.m_dashButton = _real;
            _scheme = null;
            _real = null;
        }
    }

    /// <summary>
    /// Knock context: an impact applied while one of these runs is a knock (thrown item, fire,
    /// dash bump); other impacts (a cannon landing) are left alone.
    /// </summary>
    internal static class MovementKnock
    {
        internal static bool Knockback;
        internal static GameObject Dasher, Bumped;
    }

    /// <summary>Thrown items and fire hazards knock through here (owner machine and host).</summary>
    [HarmonyPatch(typeof(ClientPlayerControlsImpl_Default), "DoKnockback")]
    internal static class MovementSpecialsKnockbackPatch
    {
        private static void Prefix()
        {
            if (SpecialRuntime.InLevel) MovementKnock.Knockback = true;
        }

        private static void Finalizer()
        {
            MovementKnock.Knockback = false;
        }
    }

    /// <summary>Dash bumps knock through here (the dasher's OnDashCollision knocks the other chef).</summary>
    [HarmonyPatch(typeof(ClientPlayerControlsImpl_Default), "OnDashCollision")]
    internal static class MovementSpecialsDashBumpPatch
    {
        private static void Prefix(ClientPlayerControlsImpl_Default __instance, PlayerControls _otherPlayer)
        {
            if (!SpecialRuntime.InLevel) return;
            try
            {
                MovementKnock.Dasher = __instance != null ? __instance.gameObject : null;
                MovementKnock.Bumped = _otherPlayer != null ? _otherPlayer.gameObject : null;
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Movement.DashBump", error);
            }
        }

        private static void Finalizer()
        {
            MovementKnock.Dasher = null;
            MovementKnock.Bumped = null;
        }
    }

    /// <summary>
    /// Immovable / Big Chef: no knock starts. Tiny Chef: knocks send it further. Big Chef's dash
    /// bumps send others further. Every machine that simulates the knock applies the same rule.
    /// </summary>
    [HarmonyPatch(typeof(ClientPlayerControlsImpl_Default), "ApplyImpact")]
    internal static class MovementSpecialsImpactPatch
    {
        private static bool Prefix(ClientPlayerControlsImpl_Default __instance, ref Vector2 _xzImpactVelocity)
        {
            if (!SpecialRuntime.InLevel || __instance == null) return true;
            if (!MovementKnock.Knockback && MovementKnock.Dasher == null) return true;
            try
            {
                GameObject chef = __instance.gameObject;
                ChefState state = SpecialRuntime.StateOf(chef);
                if (state != null && (state.Has(Special.Immovable) || state.Has(Special.BigChef))) return false;
                float factor = 1f;
                if (state != null && state.Has(Special.TinyChef)) factor *= MovementSpecials.KnockFactor;
                if (MovementKnock.Dasher != null)
                {
                    // In a head-on bump the dasher is knocked too; then the other chef is the knocker.
                    GameObject knocker = chef == MovementKnock.Dasher ? MovementKnock.Bumped : MovementKnock.Dasher;
                    if (knocker != null && knocker != chef && SpecialRuntime.Has(knocker, Special.BigChef))
                        factor *= MovementSpecials.KnockFactor;
                }
                if (factor != 1f) _xzImpactVelocity *= factor;
                return true;
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Movement.ApplyImpact", error);
                return true;
            }
        }
    }

    /// <summary>Sure-Footed: no slipping or sliding on ice and slopes (every machine).</summary>
    [HarmonyPatch(typeof(ClientPlayerControlsImpl_Default), "ProgressVelocityWrtFriction")]
    internal static class MovementSpecialsFrictionPatch
    {
        private static void Prefix(ClientPlayerControlsImpl_Default __instance, ref PlayerPhysicsSurfaceProperties _surface)
        {
            if (!MovementSpecials.AnySureFooted || !SpecialRuntime.InLevel) return;
            try
            {
                if (ReferenceEquals(_surface, null) || _surface == null) return;
                if (_surface.Slippiness <= 0f && _surface.Slidiness <= 0f) return;
                if (__instance == null || !SpecialRuntime.Has(__instance.gameObject, Special.SureFooted)) return;
                _surface = MovementSpecials.Unslipped(_surface);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("SureFooted.Friction", error);
            }
        }
    }

    /// <summary>Sure-Footed: puddles don't make the chef slip (host, where slips are decided).</summary>
    [HarmonyPatch(typeof(ServerSlipCollider), "Slip", new Type[] { typeof(GameObject) })]
    internal static class MovementSpecialsSlipPatch
    {
        private static bool Prefix(GameObject _object)
        {
            if (!MovementSpecials.AnySureFooted || !SpecialRuntime.InLevel || _object == null) return true;
            try
            {
                return !SpecialRuntime.Has(_object, Special.SureFooted) || _object.GetComponent<PlayerControls>() == null;
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("SureFooted.Slip", error);
                return true;
            }
        }
    }

    /// <summary>Sure-Footed / Weatherproof: wind doesn't push the chef (every machine).</summary>
    [HarmonyPatch(typeof(WindAccumulator), "Update")]
    internal static class MovementSpecialsWindPatch
    {
        private static readonly FieldInfo TotalForceField = AccessTools.Field(typeof(WindAccumulator), "m_totalForce");

        private static void Postfix(WindAccumulator __instance)
        {
            if (MovementSpecials.NoWind.Count == 0 || !SpecialRuntime.InLevel || TotalForceField == null) return;
            try
            {
                if (__instance != null && MovementSpecials.NoWind.Contains(__instance.gameObject.GetInstanceID()))
                    TotalForceField.SetValue(__instance, Vector3.zero);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Weatherproof.Wind", error);
            }
        }
    }

    /// <summary>
    /// Moving floors (travelators, the only IMovingSurface): Conveyor Rider is carried twice as
    /// fast; Sure-Footed / Weatherproof aren't carried. Every machine.
    /// </summary>
    [HarmonyPatch(typeof(SurfaceMovable), "Update")]
    internal static class MovementSpecialsSurfacePatch
    {
        private static readonly FieldInfo VelocityField = AccessTools.Field(typeof(SurfaceMovable), "m_surfaceVelocity");

        private static void Postfix(SurfaceMovable __instance)
        {
            if (MovementSpecials.SurfaceFactor.Count == 0 || !SpecialRuntime.InLevel || VelocityField == null) return;
            try
            {
                float factor;
                if (__instance == null || !MovementSpecials.SurfaceFactor.TryGetValue(__instance.gameObject.GetInstanceID(), out factor))
                    return;
                Vector3 velocity = (Vector3)VelocityField.GetValue(__instance);
                if (velocity.sqrMagnitude > 0f) VelocityField.SetValue(__instance, velocity * factor);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("ConveyorRider.Surface", error);
            }
        }
    }
}
