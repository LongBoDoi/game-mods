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
    /// Immovable, Ghost, Conveyor Rider, Big Chef, Tiny Chef, Overdrive, and the quirks Heavy
    /// Boots, Clumsy and Sleepy. (Weatherproof is retired: Sure-Footed took its walkway rule,
    /// Immovable and Conveyor Rider took the wind.)
    ///
    /// Who decides what (movement is simulated on the host and on the chef's own machine, which
    /// must agree, or the player's chef is pulled back to where the host has it):
    /// - Sprinter, Momentum, Charge Dash: only the player's own machine sees its held buttons (the
    ///   host only gets one short DASH pulse per dash, see DashSync), so it decides and tells every
    ///   machine (own topics); every machine then applies the same speed through the framework's
    ///   modifier lists.
    /// - Double Dash, Heavy Boots, Charge Dash's "no dash while holding": only the dash decision
    ///   matters, and with DashSync that is made on the player's machine (the host follows).
    /// - Slipstream, Sleepy: the host decides (it has every chef's true position / one clock) and
    ///   tells every machine (a buff / own topic). Clumsy: the host drops the item when it sees the
    ///   dash start.
    /// - Sure-Footed, Conveyor Rider, Ghost, Immovable, Big/Tiny Chef: plain rules every machine
    ///   applies the same way where the game simulates them. Sleepy's doze (no chopping or
    ///   washing either) is read from the host's yawn message, so every machine agrees on it.
    /// - Overdrive: OnActivated (every machine) starts the framework's NoDashCooldown buff and a
    ///   run-speed buff of its own.
    ///
    /// Visuals: effects every machine can see for itself (buffs, the messages above) are drawn
    /// locally by every machine; things only the host sees (knocks, drops, puddles) use the
    /// SpecialFx ...All calls from host-only code.
    /// </summary>
    internal sealed class MovementSpecials : ISpecialModule
    {
        private const string DataKey = "Movement";

        // Own topics: (int)Special * 10 + n.
        /// <summary>A = seconds of sprint left (0 = stopped).</summary>
        private const ushort SprintTopic = (ushort)((int)Special.Sprinter * 10);
        /// <summary>A = -1 started charging, 0 let go without power, >= 1 released with this power.</summary>
        private const ushort ChargeTopic = (ushort)((int)Special.ChargeDash * 10);
        /// <summary>A = the speed bonus now.</summary>
        private const ushort MomentumTopic = (ushort)((int)Special.Momentum * 10);
        /// <summary>Host: A = seconds the chef dozes off now (0 = none), B = seconds until the next.</summary>
        private const ushort SleepyTopic = (ushort)((int)Special.Sleepy * 10);
        /// <summary>Owner: the chef is on ice or a slope that Sure-Footed is holding (glow on every machine).</summary>
        private const ushort SureFootedTopic = (ushort)((int)Special.SureFooted * 10);

        // Own buffs, so they never merge with another module's run-speed buff.
        private const int SlipstreamBuff = (int)Special.Slipstream * 100 + 1;
        private const int SlipstreamLeadBuff = (int)Special.Slipstream * 100 + 2;
        private const int OverdriveBuff = (int)Special.Overdrive * 100 + 1;

        // Sprint = 1 + this x the DASHING multiplier (Sprinter can't dash, so DASHING pays off here).
        private const float SprintBonus = 0.35f;
        private const float SprintStamina = 4f;
        private const float SprintRegen = 1f;         // stamina seconds per second while not sprinting
        private const float SprintMinStart = 0.5f;

        private const int DashCharges = 2;
        private const float DoubleDashRefill = 0.75f; // a charge takes this share of a normal recharge

        private const float ChargeFullSeconds = 1f;
        private const float ChargeMaxPower = 2f;
        private const float ChargeShowAfter = 0.12f;  // a tap isn't shown as charging
        private const float ReleaseWindow = 0.6f;     // the dash on release waits this long for the recharge

        private const float MomentumBuild = 0.1f;     // per second of running
        private const float MomentumMax = 0.30f;
        private const float MomentumStepSize = 0.05f; // what is sent (and applied) moves in these steps
        private const float MomentumGrace = 0.4f;     // a stop shorter than this (a turn, a grab) keeps it
        private const float MomentumDecay = 0.3f;     // per second once stopped

        private const float SlipstreamRange = 5f;
        private const float SlipstreamWidth = 3f;       // how far to either side of the leader's line
        private const float SlipstreamBonus = 0.2f;     // the teammate behind
        private const float SlipstreamLeadBonus = 0.1f; // the leader, while someone is in its stream
        private const float SlipstreamOpposite = -0.3f; // a teammate facing further back than this is heading the other way
        private const float SlipstreamEvery = 0.25f;
        private const float SlipstreamLinger = 2f;

        private const float BigScale = 1.2f, TinyScale = 0.8f;
        private const float BigSpeed = 0.95f, TinySpeed = 1.1f;
        private const float BigDash = 1.25f;            // Big Chef's dashes go this much further
        internal const float KnockFactor = 1.5f;

        private const float OverdriveSeconds = 6f;
        private const float OverdriveSpeed = 0.2f;

        private const float SleepyEvery = 30f, SleepySeconds = 2f, SleepyWarn = 2f, SleepyResync = 10f;

        private const float GhostOverlap = 0.6f;

        private const float CacheEvery = 0.5f;
        // Rings that follow a state are re-marked every frame for this long (so they fade soon after).
        private const float RingSeconds = 0.15f;

        // Wind / moving floor rules (see Filter).
        internal const int RuleBlock = 0, RuleTailwind = 1, RuleRider = 2;

        private static readonly Color SprintColour = new Color(0.45f, 1f, 1f);
        private static readonly Color ChargeFullColour = new Color(1f, 0.55f, 0.15f);
        private static readonly Color MomentumColour = new Color(0.25f, 0.55f, 1f);
        private static readonly Color SlipColour = new Color(0.55f, 1f, 0.85f);
        private static readonly Color GhostColour = new Color(0.86f, 0.9f, 1f);

        /// <summary>Per-chef state of this module, kept in ChefState.Data (cleared with the level).</summary>
        internal sealed class MoveData
        {
            // Sprinter (owner: stamina; every machine: SprintUntil)
            public float Stamina = SprintStamina;
            public bool Sprinting, Exhausted, SprintWasDown;
            public float SprintUntil;
            // Double Dash (owner machine)
            public float Charges = DashCharges;
            public float LastOwnDashAt = -100f;
            // Charge Dash (owner: charging; every machine: power, what is shown)
            public bool Charging, ChargeAnnounced;
            public float Charge, ReleaseUntil;
            public float PendingPower, PendingUntil, Power = 1f, PowerUntil, LastDashAt = -100f;
            public bool LastDashPowered;
            public float ChargeSeenAt = -1f;
            // Momentum (owner: Raw; every machine: Bonus)
            public float Raw, StopTime, SentBonus, Bonus;
            // Slipstream / Ghost (what this machine showed last frame)
            public bool Slipping, GhostNear;
            // Sleepy (host: NextYawn, NextSync; every machine: YawnAt, DozeUntil)
            public float NextYawn, NextSync, YawnAt, DozeUntil;
            public bool Warned;
            // Heavy Boots (owner)
            public bool HeavyWasDown;
        }

        // Caches refreshed every CacheEvery seconds (patches run per object per frame).
        // Chef GameObject instance ID -> rule for wind / for moving floors.
        internal static readonly Dictionary<int, int> WindRule = new Dictionary<int, int>();
        internal static readonly Dictionary<int, int> SurfaceRule = new Dictionary<int, int>();
        internal static bool AnySureFooted, AnyDashHook;
        private static bool s_anyGhost;
        private static float s_nextCache, s_nextSlipstream;

        private static readonly Dictionary<PlayerPhysicsSurfaceProperties, PlayerPhysicsSurfaceProperties> s_unslipped =
            new Dictionary<PlayerPhysicsSurfaceProperties, PlayerPhysicsSurfaceProperties>();

        // Glow(): when each chef+special was last marked (real time).
        private static readonly Dictionary<int, float> s_glowAt = new Dictionary<int, float>();

        // Host: chefs whose dash just started while Clumsy (dropped in the next Tick).
        private static readonly List<ChefState> s_clumsyDrops = new List<ChefState>();

        public void Register()
        {
            SpecialRuntime.RunSpeedModifiers.Add(RunFactor);
            SpecialRuntime.DashSpeedModifiers.Add(DashFactor);
            // Charge Dash's release is the whole point of that special, so the speed cap doesn't
            // cut its "up to twice the distance".
            SpecialRuntime.UncappedDashSpeedModifiers.Add(ChargeFactor);
            SpecialRuntime.DashCooldownModifiers.Add(CooldownFactor);
            SpecialRuntime.DashAllowed.Add(CanDash);

            SpecialRuntime.RegisterActive(Special.Overdrive, new ActiveHandler
            {
                Execute = delegate(ChefState state)
                {
                    if (state.Chef == null || state.Controls == null) return false;
                    if (state.Controls.m_bRespawning) return state.Refuse(Lang.Both("RESPAWNING", "ĐANG HỒI SINH"));
                    return true;
                },
                OnActivated = delegate(ChefState state)
                {
                    // OnActivated runs on every machine, so local buffs are the same everywhere.
                    SpecialRuntime.AddBuffLocal(state, BuffId.NoDashCooldown, OverdriveSeconds, 1f, state);
                    SpecialRuntime.AddBuffLocal(state, OverdriveBuff, OverdriveSeconds, OverdriveSpeed, state);
                },
                Duration = OverdriveSeconds
            });

            SpecialNet.Register(SprintTopic, OnMessage);
            SpecialNet.Register(ChargeTopic, OnMessage);
            SpecialNet.Register(MomentumTopic, OnMessage);
            SpecialNet.Register(SleepyTopic, OnMessage);
            SpecialNet.Register(SureFootedTopic, OnMessage);

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
            WindRule.Clear();
            SurfaceRule.Clear();
            AnySureFooted = false;
            AnyDashHook = false;
            s_anyGhost = false;
            s_nextCache = 0f;
            s_nextSlipstream = 0f;
            s_clumsyDrops.Clear();
            s_glowAt.Clear();
            foreach (PlayerPhysicsSurfaceProperties copy in s_unslipped.Values)
                if (copy != null) Object.Destroy(copy);
            s_unslipped.Clear();
        }

        private static void RefreshCaches()
        {
            s_nextCache = SpecialRuntime.Now + CacheEvery;
            WindRule.Clear();
            SurfaceRule.Clear();
            AnySureFooted = false;
            AnyDashHook = false;
            s_anyGhost = false;
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState state = chefs[i];
                if (state.Chef == null) continue;
                int key = state.Chef.GetInstanceID();
                bool sure = state.Has(Special.SureFooted), rider = state.Has(Special.ConveyorRider);
                // Wind: Conveyor Rider rides it, Immovable isn't moved by it. Riding is the more
                // explicit choice, so it is checked first. Sure-Footed is about footing only.
                if (rider) WindRule[key] = RuleRider;
                else if (state.Has(Special.Immovable)) WindRule[key] = RuleBlock;
                // Moving floors: Conveyor Rider twice as fast; Sure-Footed still carried the way
                // it faces, never held back.
                if (rider) SurfaceRule[key] = RuleRider;
                else if (sure) SurfaceRule[key] = RuleTailwind;
                if (sure) AnySureFooted = true;
                if (state.Has(Special.Ghost)) s_anyGhost = true;
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
            factor *= 1f + SpecialRuntime.BuffStrength(state, SlipstreamLeadBuff);
            factor *= 1f + SpecialRuntime.BuffStrength(state, OverdriveBuff);
            if (state.Has(Special.Sprinter) || state.Has(Special.Momentum))
            {
                MoveData data = Data(state);
                if (state.Has(Special.Sprinter) && SpecialRuntime.Now < data.SprintUntil) factor *= SprintFactor(state);
                if (state.Has(Special.Momentum)) factor *= 1f + data.Bonus;
            }
            return factor;
        }

        /// <summary>
        /// Sprinter's speed while sprinting: +35%, scaled by the DASHING multiplier (the chef's
        /// level, which every machine knows, so every machine gets the same).
        /// </summary>
        private static float SprintFactor(ChefState state)
        {
            float dashing = state.Chef != null ? Plugin.Skills.Multiplier(state.Chef, Skill.Dashing) : 1f;
            return 1f + SprintBonus * Mathf.Max(0f, dashing);
        }

        /// <summary>Dash speed changes inside the special speed cap (Big Chef).</summary>
        private static float DashFactor(ChefState state)
        {
            return state.Has(Special.BigChef) ? BigDash : 1f;
        }

        /// <summary>Charge Dash's released dash (outside the cap, see Register).</summary>
        private static float ChargeFactor(ChefState state)
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
        private static void Send(ChefState state, ushort topic, float a, float b)
        {
            var message = new SpecialMessage { Topic = topic, Chef = state.Id, A = a, B = b };
            if (state.Id != 0) SpecialNet.SendToAll(message);
            else Handle(state, message);
        }

        private static void OnMessage(SpecialMessage message)
        {
            ChefState state = SpecialRuntime.StateById(message.Chef);
            if (state != null && state.Chef != null) Handle(state, message);
        }

        /// <summary>Every machine (the sender's included).</summary>
        private static void Handle(ChefState state, SpecialMessage message)
        {
            MoveData data = Data(state);
            float now = SpecialRuntime.Now;
            switch (message.Topic)
            {
                case SprintTopic:
                {
                    if (!state.Has(Special.Sprinter)) return;
                    bool was = now < data.SprintUntil;
                    // The sprint carries its own end (the stamina left), so a lost "stop" can't last.
                    data.SprintUntil = message.A > 0f ? now + message.A + 0.1f : 0f;
                    if (!was && message.A > 0f) SpecialFx.Burst(state.Position, SprintColour, 0.9f, 0.35f);
                    break;
                }
                case ChargeTopic:
                {
                    if (!state.Has(Special.ChargeDash)) return;
                    if (message.A < 0f)
                    {
                        data.ChargeSeenAt = now;
                        return;
                    }
                    data.ChargeSeenAt = -1f;
                    if (message.A < 1f) return;
                    ApplyCharge(state, data, Mathf.Clamp(message.A, 1f, ChargeMaxPower));
                    if (message.A > 1.05f)
                    {
                        bool full = message.A >= ChargeMaxPower - 0.01f;
                        SpecialFx.Text(state.Chef, full ? Lang.T("MAX POWER!", "LỰC TỐI ĐA!") : Lang.T("POWER x", "LỰC x") + message.A.ToString("0.0"),
                            full ? ChargeFullColour : SpecialFx.ColourOf(Special.ChargeDash));
                        SpecialFx.Burst(state.Position, full ? ChargeFullColour : SpecialFx.ColourOf(Special.ChargeDash),
                            0.8f + 0.6f * (message.A - 1f), 0.45f);
                    }
                    break;
                }
                case MomentumTopic:
                {
                    if (!state.Has(Special.Momentum)) return;
                    float old = data.Bonus;
                    data.Bonus = Mathf.Clamp(message.A, 0f, MomentumMax);
                    if (data.Bonus >= MomentumMax - 0.001f && old < MomentumMax - 0.001f)
                    {
                        SpecialFx.Text(state.Chef, SpecialCatalog.NameOf(Special.Momentum) + " +" + Mathf.RoundToInt(MomentumMax * 100f) + "%", MomentumColour);
                        SpecialFx.Burst(state.Position, MomentumColour, 1.1f, 0.4f);
                    }
                    break;
                }
                case SureFootedTopic:
                {
                    // B = 1: the host saw wind held off (A = the special) that only it feels.
                    Special which = message.B > 0.5f ? (Special)Mathf.RoundToInt(message.A) : Special.SureFooted;
                    if (state.Has(which)) Glow(state.Chef, which);
                    return;
                }
                case SleepyTopic:
                {
                    if (!state.Has(Special.Sleepy)) return;
                    float yawnAt = now + Mathf.Max(0f, message.B);
                    // A re-sync of the same yawn keeps its warning shown.
                    if (Mathf.Abs(yawnAt - data.YawnAt) > 1f || message.A > 0f) data.Warned = now >= yawnAt - SleepyWarn;
                    data.YawnAt = yawnAt;
                    if (message.A > 0f)
                    {
                        data.DozeUntil = now + message.A;
                        SpecialRuntime.AddBuffLocal(state, BuffId.Frozen, message.A, 1f, state);
                        SpecialFx.Mark(state.Chef, SpecialFx.ColourOf(Special.Sleepy), message.A, "Zzz...", 0f);
                        SpecialFx.Burst(state.Position, SpecialFx.ColourOf(Special.Sleepy), 1f, 0.5f);
                    }
                    break;
                }
            }
        }

        private static void ApplyCharge(ChefState state, MoveData data, float power)
        {
            float now = SpecialRuntime.Now;
            // The owner sends this before its dash starts. Elsewhere the dash (DashSync's pulse,
            // on the game's own channel) may have got here first: then power up the rest of it,
            // unless that dash already had a power (it was an earlier one).
            if (!state.Local && now - data.LastDashAt < 0.25f && !data.LastDashPowered)
            {
                data.Power = power;
                data.PowerUntil = data.LastDashAt + DashTime(state);
                data.LastDashPowered = true;
            }
            else
            {
                // Kept for the next dash this chef starts (the one on release).
                data.PendingPower = power;
                data.PendingUntil = now + 1f;
            }
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
                data.LastDashPowered = now < data.PendingUntil;
                if (data.LastDashPowered)
                {
                    data.Power = data.PendingPower;
                    data.PowerUntil = now + DashTime(state);
                    data.PendingUntil = 0f;
                }
            }
            if (state.Local && state.Has(Special.DoubleDash) && !SpecialRuntime.HasBuff(state, BuffId.NoDashCooldown))
            {
                // A second dash soon after the first: say so on every machine (only the owner's
                // machine gets here, so the ...All calls aren't doubled).
                bool chained = data.Charges < DashCharges - 0.5f && now - data.LastOwnDashAt < 1f;
                data.Charges = Mathf.Max(0f, data.Charges - 1f);
                if (chained)
                {
                    SpecialFx.TextAll(state.Chef, SpecialFx.NameBoth(Special.DoubleDash, "!", "!"), Special.DoubleDash);
                    SpecialFx.BurstAll(state.Position, Special.DoubleDash);
                }
            }
            if (state.Local) data.LastOwnDashAt = now;
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
            try
            {
                if (s_anyGhost) GhostEffects(chefs);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Ghost.Fx", error);
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

            MoveData data = Data(state);
            float now = SpecialRuntime.Now;
            if (server && state.Has(Special.Sleepy)) SleepyHost(state, data, now);
            ShowEffects(state, data, now);
            if (!state.Local) return;

            string status = null;
            if (state.Has(Special.Sprinter)) status = Join(status, Sprinter(state, data, dt, now));
            if (state.Has(Special.DoubleDash)) DoubleDash(state, data, dt, now);
            if (state.Has(Special.ChargeDash)) ChargeDash(state, data, dt, now);
            if (state.Has(Special.Momentum)) Momentum(state, data, dt);
            if (state.Has(Special.HeavyBoots)) HeavyBoots(state, data);
            if (state.Has(Special.Sleepy) && data.YawnAt > now) state.SetTimer(Special.Sleepy, data.YawnAt, SleepyEvery);
            state.SetStatus("Movement", status, Special.Sprinter);
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

        /// <summary>Every machine: rings under chefs for what every machine knows about them.</summary>
        private static void ShowEffects(ChefState state, MoveData data, float now)
        {
            GameObject chef = state.Chef;

            if (state.Has(Special.Sprinter) && now < data.SprintUntil)
                SpecialFx.Mark(chef, SprintColour, RingSeconds, null, 0.85f);

            if (state.Has(Special.ChargeDash))
            {
                // The owner knows the true charge; the others count from when it was announced.
                float level = -1f;
                if (state.Local && data.Charging && data.Charge >= ChargeShowAfter) level = data.Charge / ChargeFullSeconds;
                else if (!state.Local && data.ChargeSeenAt >= 0f)
                {
                    float held = now - data.ChargeSeenAt;
                    if (held > 4f) data.ChargeSeenAt = -1f;
                    else level = (held + ChargeShowAfter) / ChargeFullSeconds;
                }
                if (level >= 0f)
                {
                    int step = Mathf.Clamp(Mathf.FloorToInt(Mathf.Clamp01(level) * 4f + 0.001f), 0, 4);
                    Color colour = Color.Lerp(SpecialFx.ColourOf(Special.ChargeDash), ChargeFullColour, step / 4f);
                    SpecialFx.Mark(chef, colour, RingSeconds, step >= 4 ? Lang.T("MAX", "TỐI ĐA") : null, 0.5f + 0.15f * step);
                }
            }

            if (state.Has(Special.Momentum) && data.Bonus >= 0.099f)
            {
                int step = Mathf.Clamp(Mathf.RoundToInt(data.Bonus * 10f), 1, 3);
                Color colour = Color.Lerp(MomentumColour, Color.white, 0.2f * (step - 1));
                SpecialFx.Mark(chef, colour, RingSeconds, null, 0.55f + 0.1f * step);
            }

            Buff slip;
            bool slipping = state.Buffs.TryGetValue(SlipstreamBuff, out slip) && slip.Until > now;
            if (slipping)
            {
                SpecialFx.Mark(chef, SlipColour, RingSeconds, null, 0.75f);
                if (!data.Slipping)
                {
                    SpecialFx.Text(chef, SpecialCatalog.NameOf(Special.Slipstream), SlipColour);
                    ChefState leader = SpecialRuntime.StateById(slip.Source);
                    if (leader != null && leader.Chef != null)
                    {
                        SpecialFx.Beam(leader.Position, state.Position, SlipColour, 0.6f);
                        SpecialFx.Mark(leader.Chef, SpecialFx.ColourOf(Special.Slipstream), 0.8f, null, 0f);
                    }
                }
            }
            data.Slipping = slipping;
            // The leader's own +10% while someone drafts it.
            if (SpecialRuntime.HasBuff(state, SlipstreamLeadBuff))
                SpecialFx.Mark(chef, SpecialFx.ColourOf(Special.Slipstream), RingSeconds, null, 0.65f);

            if (SpecialRuntime.HasBuff(state, OverdriveBuff))
                SpecialFx.Mark(chef, SpecialFx.ColourOf(Special.Overdrive), RingSeconds, null, 0.95f);

            if (state.Has(Special.Sleepy) && data.YawnAt > 0f && !data.Warned && now >= data.YawnAt - SleepyWarn)
            {
                data.Warned = true;
                SpecialFx.Text(chef, Lang.T("*yawn*", "*ngáp*"), SpecialFx.ColourOf(Special.Sleepy));
            }
        }

        /// <summary>Owner: hold DASH to sprint while there is stamina; tells every machine.</summary>
        private static string Sprinter(ChefState state, MoveData data, float dt, float now)
        {
            bool down = DashHeld(state);
            bool pressed = down && !data.SprintWasDown;
            data.SprintWasDown = down;
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
                        SpecialFx.Text(state.Chef, Lang.T("OUT OF BREATH", "HẾT HƠI"), SpecialFx.ColourOf(Special.Sprinter));
                    }
                    Send(state, SprintTopic, 0f, 0f);
                }
            }
            else
            {
                if (!down) data.Exhausted = false;
                data.Stamina = Mathf.Min(SprintStamina, data.Stamina + dt * SprintRegen);
                if (down && can && !data.Exhausted && data.Stamina >= SprintMinStart)
                {
                    data.Sprinting = true;
                    Send(state, SprintTopic, data.Stamina, 0f);
                }
                else if (pressed && can && data.Stamina < SprintMinStart)
                    SpecialFx.Text(state.Chef, Lang.T("TIRED", "MỆT"), SpecialFx.ColourOf(Special.Sprinter));
            }
            // The icon empties with the stamina and fills back up as it recovers.
            float total = SprintStamina / SprintRegen;
            state.SetTimer(Special.Sprinter, data.Stamina < SprintStamina - 0.01f ? now + (SprintStamina - data.Stamina) / SprintRegen : 0f, total);
            return data.Exhausted ? Lang.T("Let go of DASH", "Thả nút LƯỚT ra") : null;
        }

        /// <summary>Owner: charges recharge one per 3/4 of the normal dash recharge time.</summary>
        private static void DoubleDash(ChefState state, MoveData data, float dt, float now)
        {
            PlayerControls.MovementData movement = state.Controls.Movement;
            if (movement == null) return;
            // Outside Update_Movement this is the game's own value (MovementPatch restores it).
            // A little quicker than one dash, so two charges are more than a burst in the long run.
            float recharge = Mathf.Max(0.05f, movement.DashCooldown * DoubleDashRefill /
                Mathf.Max(0.1f, Plugin.Skills.Multiplier(state.Chef, Skill.Dashing)));
            data.Charges = Mathf.Min(DashCharges, data.Charges + dt / recharge);
            int whole = Mathf.FloorToInt(data.Charges + 0.0001f);
            state.SetCount(Special.DoubleDash, whole);
            state.SetTimer(Special.DoubleDash, whole < DashCharges ? now + (whole + 1 - data.Charges) * recharge : 0f, recharge);
        }

        /// <summary>Owner: holding DASH charges; letting go dashes (the power goes to every machine).</summary>
        private static void ChargeDash(ChefState state, MoveData data, float dt, float now)
        {
            bool down = DashHeld(state);
            if (!Controllable(state))
            {
                if (data.Charging && data.ChargeAnnounced) Send(state, ChargeTopic, 0f, 0f);
                data.Charging = false;
                data.ChargeAnnounced = false;
                data.Charge = 0f;
            }
            else if (down)
            {
                if (!data.Charging)
                {
                    data.Charging = true;
                    data.Charge = 0f;
                }
                data.Charge += dt;
                if (!data.ChargeAnnounced && data.Charge >= ChargeShowAfter)
                {
                    data.ChargeAnnounced = true;
                    Send(state, ChargeTopic, -1f, 0f);
                }
            }
            else if (data.Charging)
            {
                data.Charging = false;
                float power = 1f + Mathf.Clamp01(data.Charge / ChargeFullSeconds) * (ChargeMaxPower - 1f);
                data.Charge = 0f;
                // The dash itself happens in Update_Movement (a press is fed to the game there).
                // The power goes out first, so it is usually waiting on the host before the dash.
                data.ReleaseUntil = now + ReleaseWindow;
                if (power > 1.05f) Send(state, ChargeTopic, power, 0f);
                else if (data.ChargeAnnounced) Send(state, ChargeTopic, 0f, 0f);
                data.ChargeAnnounced = false;
            }
            // The icon fills up as the charge builds (bright = full).
            if (data.Charging)
            {
                float level = Mathf.Clamp01(data.Charge / ChargeFullSeconds);
                state.SetTimer(Special.ChargeDash, now + (1f - level) * ChargeFullSeconds, ChargeFullSeconds);
            }
            else state.SetTimer(Special.ChargeDash, 0f, ChargeFullSeconds);
        }

        /// <summary>Owner: running builds +10% per second up to +30%; stopping loses it, working resets it.</summary>
        private static void Momentum(ChefState state, MoveData data, float dt)
        {
            PlayerControls controls = state.Controls;
            PlayerControls.ControlSchemeData scheme = controls.ControlScheme;
            float x = scheme != null && scheme.m_moveX != null ? scheme.m_moveX.GetValue() : 0f;
            float y = scheme != null && scheme.m_moveY != null ? scheme.m_moveY.GetValue() : 0f;
            bool running = Controllable(state) && x * x + y * y > 0.25f && controls.GetUnclampedMovementSpeed() > 0.4f;

            if (controls.m_bRespawning || controls.GetCurrentlyInteracting() != null)
            {
                // Chopping, washing, respawning: start again.
                data.Raw = 0f;
                data.StopTime = 0f;
            }
            else if (running)
            {
                data.Raw = Mathf.Min(MomentumMax, data.Raw + MomentumBuild * dt);
                data.StopTime = 0f;
            }
            else
            {
                data.StopTime += dt;
                if (data.StopTime > MomentumGrace) data.Raw = Mathf.Max(0f, data.Raw - MomentumDecay * dt);
            }

            float bonus = Mathf.Min(MomentumMax, MomentumStepSize * Mathf.Floor(data.Raw / MomentumStepSize + 0.001f));
            if (!Mathf.Approximately(bonus, data.SentBonus))
            {
                data.SentBonus = bonus;
                Send(state, MomentumTopic, bonus, 0f);
            }
            state.SetCount(Special.Momentum, Mathf.RoundToInt(data.Bonus * 100f));
        }

        /// <summary>Owner: a DASH press that does nothing says why.</summary>
        private static void HeavyBoots(ChefState state, MoveData data)
        {
            bool down = DashHeld(state);
            if (down && !data.HeavyWasDown && Controllable(state))
                SpecialFx.Text(state.Chef, Lang.T("TOO HEAVY TO DASH", "QUÁ NẶNG ĐỂ LƯỚT"), SpecialFx.ColourOf(Special.HeavyBoots));
            data.HeavyWasDown = down;
        }

        /// <summary>
        /// SLEEPY: is this chef dozing right now (can't move, chop or wash)? Every machine, the
        /// same answer (from the host's yawn message). Read by the chopping and washing patches.
        /// Its own flag, not BuffId.Frozen: Sneeze, Grease, Swap and Show-Off freeze chefs too,
        /// and those mustn't stop chopping.
        /// </summary>
        internal static bool Dozing(ChefState state)
        {
            if (state == null || !state.Has(Special.Sleepy)) return false;
            MoveData data = state.Get<MoveData>(DataKey, null);
            return data != null && SpecialRuntime.Now < data.DozeUntil;
        }

        /// <summary>Host: every 30 s the chef dozes off and can't move, chop or wash for 2 s (every machine is told).</summary>
        private static void SleepyHost(ChefState state, MoveData data, float now)
        {
            if (data.NextYawn <= 0f)
            {
                data.NextYawn = now + SleepyEvery;
                data.NextSync = now + SleepyResync;
                Send(state, SleepyTopic, 0f, SleepyEvery);
                return;
            }
            if (now >= data.NextYawn)
            {
                data.NextYawn = now + SleepyEvery;
                data.NextSync = now + SleepyResync;
                Send(state, SleepyTopic, SleepySeconds, SleepyEvery);
                return;
            }
            if (now >= data.NextSync)
            {
                // Now and then, so a machine that joined the level late still knows when.
                data.NextSync = now + SleepyResync;
                Send(state, SleepyTopic, 0f, data.NextYawn - now);
            }
        }

        /// <summary>
        /// Host: teammates running close behind a Slipstream chef get +20% for a moment, and the
        /// leader +10% while anyone is in its stream. Any teammate counts unless it is heading
        /// back the other way (a turn or a diagonal still drafts).
        /// </summary>
        private static void Slipstream(IList<ChefState> chefs)
        {
            float now = SpecialRuntime.Now;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState leader = chefs[i];
                if (!leader.Has(Special.Slipstream) || leader.Chef == null || leader.Controls == null) continue;
                if (leader.Controls.GetUnclampedMovementSpeed() < 0.5f) continue;
                Vector3 forward = leader.Forward;
                forward.y = 0f;
                if (forward.sqrMagnitude < 0.01f) continue;
                forward.Normalize();
                bool drafted = false;
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
                    if (mateForward.sqrMagnitude < 0.01f || Vector3.Dot(mateForward.normalized, forward) < SlipstreamOpposite) continue;
                    drafted = true;
                    if (Fresh(mate, SlipstreamBuff, now)) continue;
                    SpecialRuntime.BroadcastBuff(mate, SlipstreamBuff, SlipstreamLinger, SlipstreamBonus, leader);
                }
                if (drafted && !Fresh(leader, SlipstreamLeadBuff, now))
                    SpecialRuntime.BroadcastBuff(leader, SlipstreamLeadBuff, SlipstreamLinger, SlipstreamLeadBonus, leader);
            }
        }

        /// <summary>Slipstream: still plenty left from the last one, so no need to send it again yet.</summary>
        private static bool Fresh(ChefState chef, int buff, float now)
        {
            Buff current;
            return chef.Buffs.TryGetValue(buff, out current) && current.Until - now > SlipstreamLinger - 0.45f;
        }

        /// <summary>
        /// Clumsy only drops plates: a clean one, one with food on it, or a stack of dirty ones
        /// (the game's Plate / plate stack components). Choppers and cooks don't pay for it.
        /// </summary>
        private static bool IsPlate(GameObject item)
        {
            return item != null && (item.GetComponent<Plate>() != null || item.GetComponent<PlateStackBase>() != null);
        }

        /// <summary>Host: a Clumsy chef that dashed drops the plate it holds.</summary>
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
                    GameObject item = carrier != null ? carrier.InspectCarriedItem() : null;
                    if (!IsPlate(item)) continue;
                    // The game's own drop (PlayerControlsHelper.DropHeldItem) is just this.
                    carrier.TakeItem();
                    SpecialFx.TextAll(state.Chef, Lang.Both("OOPS!", "ÁI CHÀ!"), Special.Clumsy);
                    if (item != null) SpecialFx.BurstAll(item.transform.position, Special.Clumsy);
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
            if (!s_anyGhost) return;
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

        /// <summary>Every machine: a ghost overlapping another chef glows (and says so as it goes in).</summary>
        private static void GhostEffects(IList<ChefState> chefs)
        {
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState ghost = chefs[i];
                if (!ghost.Has(Special.Ghost) || ghost.Chef == null) continue;
                bool near = false;
                Vector3 at = ghost.Position;
                for (int j = 0; j < chefs.Count && !near; j++)
                {
                    if (j == i || chefs[j].Chef == null) continue;
                    Vector3 offset = chefs[j].Position - at;
                    if (Mathf.Abs(offset.y) > 1.2f) continue;
                    offset.y = 0f;
                    near = offset.sqrMagnitude < GhostOverlap * GhostOverlap;
                }
                MoveData data = Data(ghost);
                if (near)
                {
                    SpecialFx.Mark(ghost.Chef, GhostColour, RingSeconds, null, 0.8f);
                    if (!data.GhostNear) SpecialFx.Text(ghost.Chef, SpecialCatalog.NameOf(Special.Ghost), SpecialFx.ColourOf(Special.Ghost));
                }
                data.GhostNear = near;
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

        /// <summary>
        /// What of a push (wind, a moving floor) a chef keeps under a rule: none (Immovable's
        /// wind), only the part along where it faces (Sure-Footed's walkways), or twice a push
        /// that helps and none of one that holds it back (Conveyor Rider). Facing is the same on
        /// every machine (the host takes it from the player's input), so every machine computes
        /// the same.
        /// </summary>
        internal static Vector3 Filter(Transform chef, Vector3 push, int rule)
        {
            if (rule == RuleBlock) return Vector3.zero;
            Vector3 forward = chef.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f) return rule == RuleRider ? push * 2f : Vector3.zero;
            forward.Normalize();
            float along = Vector3.Dot(push, forward);
            if (rule == RuleTailwind) return along > 0f ? forward * along : Vector3.zero;
            return along >= 0f ? push * 2f : push - forward * along;
        }

        internal static Special SpecialOf(int rule)
        {
            // Each rule now belongs to one special (the ring and the name shown).
            if (rule == RuleRider) return Special.ConveyorRider;
            if (rule == RuleTailwind) return Special.SureFooted;
            return Special.Immovable;
        }

        /// <summary>
        /// Local, from code every machine runs (physics patches): a ring under the chef while a
        /// passive is working on it, with the special's name when it starts working again.
        /// Throttled, so it can be called every physics step.
        /// </summary>
        /// <summary>
        /// Owner's machine: Sure-Footed is holding the chef on a slippery floor. Movement runs only
        /// on the host and the owner, so the owner tells every machine to glow (a few times a second).
        /// </summary>
        internal static void SureFootedHolding(GameObject chef)
        {
            ChefState state = SpecialRuntime.StateOf(chef);
            if (state == null || !state.Local) return;
            float now = Time.unscaledTime;
            if (now - state.Get("SureFooted.SentAt", -100f) < 0.3f) return;
            state.Set("SureFooted.SentAt", now);
            Send(state, SureFootedTopic, 0f, 0f);
        }

        /// <summary>
        /// Wind or a moving floor was held off (or sped up) a chef: glows here, and the host tells
        /// everyone. Wind from bellows and water guns only exists on the host, and a moving floor
        /// may not register under another player's chef on a third machine.
        /// </summary>
        internal static void WindHeld(GameObject chef, Special special)
        {
            Glow(chef, special);
            if (!SpecialNet.IsServer || !SpecialNet.Online) return;
            ChefState state = SpecialRuntime.StateOf(chef);
            if (state == null || state.Id == 0) return;
            float now = Time.unscaledTime;
            string key = "Wind.SentAt" + (int)special;
            if (now - state.Get(key, -100f) < 0.3f) return;
            state.Set(key, now);
            Send(state, SureFootedTopic, (int)special, 1f);
        }

        internal static void Glow(GameObject chef, Special special)
        {
            if (chef == null) return;
            int key = unchecked(chef.GetInstanceID() * 131 + (int)special);
            float now = Time.unscaledTime;
            float last;
            bool had = s_glowAt.TryGetValue(key, out last);
            if (had && now - last < 0.2f) return;
            s_glowAt[key] = now;
            Color colour = Color.Lerp(SpecialFx.ColourOf(special), Color.white, 0.25f);
            SpecialFx.Mark(chef, colour, 0.45f, null, 0.7f);
            if (!had || now - last > 1.5f) SpecialFx.Text(chef, SpecialCatalog.NameOf(special), SpecialFx.ColourOf(special));
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
                    // The same ControlSchemeData object the game's Update_Movement reads.
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
    /// Immovable / Big Chef: no knock starts (a Glass Cannon still cracks). Tiny Chef: knocks
    /// send it further. Big Chef's dash bumps send others further. Every machine that simulates
    /// the knock applies the same rule; the host (which sees every knock: its own simulation, and
    /// the players' reports of theirs) shows what happened on every machine.
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
                bool server = SpecialNet.IsServer;
                if (state != null && (state.Has(Special.Immovable) || state.Has(Special.BigChef)))
                {
                    if (server && _xzImpactVelocity.sqrMagnitude > 0.01f)
                    {
                        Special why = state.Has(Special.Immovable) ? Special.Immovable : Special.BigChef;
                        SpecialFx.TextAll(chef, Lang.Both("BLOCKED!", "CHẶN!"), why);
                        SpecialFx.MarkAll(chef, why, 0.6f, null);
                        // Glass Cannon still cracks on a knock it was spared. TeamBuffs' postfix
                        // doesn't see a skipped impact, and OnKnocked has its own debounce, so
                        // this never counts twice.
                        if (state.Has(Special.GlassCannon)) TeamBuffs.OnKnocked(state);
                    }
                    return false;
                }
                float factor = 1f;
                Special cause = Special.None;
                if (state != null && state.Has(Special.TinyChef))
                {
                    factor *= MovementSpecials.KnockFactor;
                    cause = Special.TinyChef;
                }
                if (MovementKnock.Dasher != null)
                {
                    // In a head-on bump the dasher is knocked too; then the other chef is the knocker.
                    GameObject knocker = chef == MovementKnock.Dasher ? MovementKnock.Bumped : MovementKnock.Dasher;
                    if (knocker != null && knocker != chef && SpecialRuntime.Has(knocker, Special.BigChef))
                    {
                        factor *= MovementSpecials.KnockFactor;
                        cause = Special.BigChef;
                    }
                }
                if (factor != 1f)
                {
                    _xzImpactVelocity *= factor;
                    if (server && cause != Special.None)
                        SpecialFx.TextAll(chef, cause == Special.BigChef ? Lang.Both("BIG BUMP!", "HÚC MẠNH!") : Lang.Both("WHOA!", "ỐI!"), cause);
                }
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
                MovementSpecials.SureFootedHolding(__instance.gameObject);
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
                if (!SpecialRuntime.Has(_object, Special.SureFooted) || _object.GetComponent<PlayerControls>() == null) return true;
                SpecialFx.TextAll(_object, Lang.Both("NO SLIP!", "KHÔNG TRƯỢT!"), Special.SureFooted);
                SpecialFx.MarkAll(_object, Special.SureFooted, 0.6f, null);
                return false;
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("SureFooted.Slip", error);
                return true;
            }
        }
    }

    /// <summary>
    /// Wind on a chef: none with Immovable, twice a helping wind (and none of a holding one) with
    /// Conveyor Rider (every machine).
    /// </summary>
    [HarmonyPatch(typeof(WindAccumulator), "Update")]
    internal static class MovementSpecialsWindPatch
    {
        private static readonly FieldInfo TotalForceField = AccessTools.Field(typeof(WindAccumulator), "m_totalForce");

        private static void Postfix(WindAccumulator __instance)
        {
            if (MovementSpecials.WindRule.Count == 0 || !SpecialRuntime.InLevel || TotalForceField == null) return;
            try
            {
                int rule;
                if (__instance == null || !MovementSpecials.WindRule.TryGetValue(__instance.gameObject.GetInstanceID(), out rule))
                    return;
                Vector3 force = (Vector3)TotalForceField.GetValue(__instance);
                if (force.sqrMagnitude < 1e-4f) return;
                Vector3 kept = MovementSpecials.Filter(__instance.transform, force, rule);
                TotalForceField.SetValue(__instance, kept);
                if ((kept - force).sqrMagnitude > 0.04f) MovementSpecials.WindHeld(__instance.gameObject, MovementSpecials.SpecialOf(rule));
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Movement.Wind", error);
            }
        }
    }

    /// <summary>
    /// Moving floors (travelators, the only IMovingSurface): Conveyor Rider is carried twice as
    /// fast and never held back; Sure-Footed is carried the way it faces and never held back.
    /// Every machine.
    /// </summary>
    [HarmonyPatch(typeof(SurfaceMovable), "Update")]
    internal static class MovementSpecialsSurfacePatch
    {
        private static readonly FieldInfo VelocityField = AccessTools.Field(typeof(SurfaceMovable), "m_surfaceVelocity");

        private static void Postfix(SurfaceMovable __instance)
        {
            if (MovementSpecials.SurfaceRule.Count == 0 || !SpecialRuntime.InLevel || VelocityField == null) return;
            try
            {
                int rule;
                if (__instance == null || !MovementSpecials.SurfaceRule.TryGetValue(__instance.gameObject.GetInstanceID(), out rule))
                    return;
                Vector3 velocity = (Vector3)VelocityField.GetValue(__instance);
                if (velocity.sqrMagnitude < 1e-4f) return;
                Vector3 kept = MovementSpecials.Filter(__instance.transform, velocity, rule);
                VelocityField.SetValue(__instance, kept);
                // Glows only while it changes something (a Sure-Footed chef going with the floor
                // is just carried, as anyone is).
                if ((kept - velocity).sqrMagnitude > 0.04f) MovementSpecials.WindHeld(__instance.gameObject, MovementSpecials.SpecialOf(rule));
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("ConveyorRider.Surface", error);
            }
        }
    }
}
