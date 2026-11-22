using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ChefSkills.Specials
{
    /// <summary>
    /// Team buffs and timed self-buffs: Rally Cry, Bodyguard, Battery, Buddy System, Frenzy,
    /// Second Wind, Adrenaline, Glass Cannon, Slow Starter, Sneeze Bomb and Lone Wolf.
    ///
    /// Who decides what (everything that changes movement or skill levels is a buff, and every
    /// buff is the same on every machine):
    /// - Rally Cry / Frenzy: OnActivated on every machine (the server's confirmation), no extra messages.
    /// - Sneeze Bomb, Battery: the host picks the targets and tells every machine (own topics).
    /// - Bodyguard, Buddy System, Second Wind, Slow Starter: the host decides from its own view
    ///   (positions, the round timer) and keeps a short buff going with BroadcastBuff.
    /// - Adrenaline / Glass Cannon (knocks, slips, falls): the host sees it happen and broadcasts a buff.
    ///
    /// What players see is drawn on each machine from its own buff state (<see cref="DrawAuras"/>):
    /// a coloured ring under every buffed chef while the buff lasts, and a word when it starts.
    ///
    /// Lone Wolf: every effect here that comes from a teammate (Rally Cry, Bodyguard, Battery,
    /// Buddy System, Sneeze Bomb) skips a Lone Wolf teammate; buffs also go through AddBuffLocal,
    /// which refuses them.
    /// </summary>
    internal sealed class TeamBuffs : ISpecialModule
    {
        // Buffs of our own, so they never merge with another module's in a shared slot and each
        // machine can tell which ring to draw.
        internal const int RallyBuff = 7201;
        internal const int GuardedBuff = 7501;
        internal const int BuddyBuff = 7701;
        internal const int FrenzyBuff = 7901;
        internal const int SecondWindBuff = 8101;
        internal const int AdrenalineBuff = 8201;
        internal const int GlassCannonBuff = 8501;
        internal const int SlowRampBuff = 8601;
        internal const int SlowReadyBuff = 8602;

        private const ushort BatteryTopic = (ushort)((int)Special.Battery * 10);
        private const ushort BatteryChargeTopic = (ushort)((int)Special.Battery * 10 + 1);
        private const ushort SneezeTopic = (ushort)((int)Special.SneezeBomb * 10);

        private const float RallySeconds = 8f;
        private const float RallyStrength = 0.3f;
        private const float BodyguardRange = 4f;
        private const float BuddyRange = 4f;
        private const float BuddyStrength = 0.15f;
        private const float FrenzyHigh = 10f;
        private const float FrenzyCrash = 6f;
        private const float SecondWindSeconds = 30f;
        private const float AdrenalineSeconds = 5f;
        private const float AdrenalineStrength = 0.5f;
        private const float GlassCannonSeconds = 15f;
        private const float SlowStarterSeconds = 60f;
        private const float SneezeRange = 3f;
        private const float SneezeSeconds = 2f;
        // Battery: the share of the teammate's full cooldown it takes off.
        private const float BatteryShare = 0.5f;
        // Battery: the teammate you face counts first (from your press), within this far and arc.
        private const float BatteryFaceRange = 8f;
        private const float BatteryFaceArc = 60f;
        // One knock often comes as several impacts (dash collisions on consecutive frames).
        private const float KnockDebounce = 0.5f;
        // Host: how often the renewed buffs (guard, buddy) are sent, and how long each lasts.
        private const float AuraSendEvery = 0.35f;
        private const float AuraSendSeconds = 0.9f;
        // Rings: refreshed this often, each lasting a little longer.
        private const float RingEvery = 0.1f;
        private const float RingSeconds = 0.3f;

        private static readonly FieldInfo TimeLimitField = AccessTools.Field(typeof(ServerRoundTimer), "m_timeLimit");

        // Speed buffs Buddy System shares.
        private static readonly int[] SpeedBuffs =
        {
            BuffId.RunSpeed, BuffId.DashSpeed, BuffId.NoDashCooldown, BuffId.MoveBonus, RallyBuff, AdrenalineBuff
        };

        // Ring colours, all different (a mark is keyed by its colour).
        private static readonly Color RallyColour = SpecialFx.ColourOf(Special.RallyCry);
        private static readonly Color GuardColour = Color.Lerp(SpecialFx.ColourOf(Special.Bodyguard), Color.white, 0.45f);
        private static readonly Color BuddyColour = new Color(1f, 0.62f, 0.5f);
        private static readonly Color FrenzyColour = new Color(1f, 0.32f, 0.55f);
        private static readonly Color CrashColour = new Color(0.5f, 0.52f, 0.6f);
        private static readonly Color SecondWindColour = Color.Lerp(SpecialFx.ColourOf(Special.SecondWind), Color.white, 0.35f);
        private static readonly Color AdrenalineColour = new Color(1f, 0.85f, 0.25f);
        private static readonly Color CrackedColour = new Color(0.62f, 0.8f, 0.95f);
        private static readonly Color SlowColour = new Color(0.55f, 0.58f, 0.66f);
        private static readonly Color SneezeColour = SpecialFx.ColourOf(Special.SneezeBomb);

        // Bodyguard: some chef is guarded on this machine (cheap early-out for the patches).
        internal static bool AnyGuarded;
        // Host: some chef has Adrenaline or Glass Cannon, so knocks matter.
        internal static bool WatchKnocks;
        // A cannon landing nudge is under way (not a knock).
        internal static bool Landing;

        private static readonly Dictionary<ChefState, bool> WasRespawning = new Dictionary<ChefState, bool>();
        private static readonly Dictionary<ChefState, float> LastKnock = new Dictionary<ChefState, float>();
        private static readonly Dictionary<ChefState, float[]> SeenBuffs = new Dictionary<ChefState, float[]>();
        private static readonly List<KeyValuePair<ChefState, int>> Grants = new List<KeyValuePair<ChefState, int>>();
        // Host: Battery chefs holding a charge nobody could use yet.
        private static readonly HashSet<ChefState> BatteryCharged = new HashSet<ChefState>();
        // Bodyguard "BLOCKED!" words (instance ID -> unscaled time), so one knock shows one word.
        private static readonly Dictionary<int, float> LastBlocked = new Dictionary<int, float>();
        // Which of the buffs below each chef had last frame (this machine), to say when one starts or ends.
        private static readonly Dictionary<ChefState, int> LastFlags = new Dictionary<ChefState, int>();
        private const int FlagRally = 1, FlagAdrenaline = 2, FlagWind = 4, FlagCrash = 8, FlagCracked = 16, FlagSlow = 32;
        private static readonly Dictionary<PlayerPhysicsSurfaceProperties, PlayerPhysicsSurfaceProperties> NoSlip =
            new Dictionary<PlayerPhysicsSurfaceProperties, PlayerPhysicsSurfaceProperties>();

        private static ServerKitchenFlowControllerBase s_flow;
        private static float s_nextFlowSearch;
        private static float s_nextSecondWind;
        private static float s_nextAuraSend;
        private static float s_nextSlowSend;
        private static float s_nextRing;
        // Host: seconds the round timer has actually run (Slow Starter).
        private static float s_roundElapsed;
        private static float s_lastTimerValue;

        public void Register()
        {
            SpecialRuntime.RegisterActive(Special.RallyCry, new ActiveHandler
            {
                Execute = delegate(ChefState state) { return true; },
                OnActivated = RallyActivated,
                Duration = RallySeconds
            });
            SpecialRuntime.RegisterActive(Special.Frenzy, new ActiveHandler
            {
                Execute = delegate(ChefState state) { return true; },
                OnActivated = FrenzyActivated,
                Duration = FrenzyHigh + FrenzyCrash
            });
            SpecialRuntime.RegisterActive(Special.SneezeBomb, new ActiveHandler
            {
                Execute = SneezeExecute, OnActivated = SneezeActivated, Duration = SneezeSeconds
            });
            SpecialRuntime.LevelBonusProviders.Add(LevelBonus);
            SpecialRuntime.RunSpeedModifiers.Add(RunModifier);
            SpecialRuntime.DashCooldownModifiers.Add(DashCooldownModifier);
            SpecialRuntime.Tick += OnTick;
            SpecialRuntime.LevelStarted += Reset;
            SpecialRuntime.LevelEnded += Reset;
            SpecialRuntime.Activated += OnAnyActivated;
            SpecialNet.Register(BatteryTopic, OnBattery);
            SpecialNet.Register(BatteryChargeTopic, OnBatteryCharge);
            SpecialNet.Register(SneezeTopic, OnSneeze);
        }

        private static void Reset()
        {
            AnyGuarded = false;
            WatchKnocks = false;
            Landing = false;
            WasRespawning.Clear();
            LastKnock.Clear();
            SeenBuffs.Clear();
            Grants.Clear();
            BatteryCharged.Clear();
            LastBlocked.Clear();
            LastFlags.Clear();
            foreach (PlayerPhysicsSurfaceProperties copy in NoSlip.Values)
                if (copy != null) Object.Destroy(copy);
            NoSlip.Clear();
            s_flow = null;
            s_nextFlowSearch = 0f;
            s_nextSecondWind = 0f;
            s_nextAuraSend = 0f;
            s_nextSlowSend = 0f;
            s_nextRing = 0f;
            s_roundElapsed = 0f;
            s_lastTimerValue = -1f;
        }

        // --- helpers ----------------------------------------------------------------------------

        /// <summary>Can an effect from <paramref name="source"/> reach <paramref name="target"/> (Lone Wolf)?</summary>
        internal static bool Affects(ChefState source, ChefState target)
        {
            if (target == null) return false;
            if (source == null || source == target || source.Team != target.Team) return true;
            return !target.Has(Special.LoneWolf);
        }

        private static bool Respawning(ChefState chef)
        {
            return chef.Controls != null && chef.Controls.m_bRespawning;
        }

        private static float FlatSqrDistance(Vector3 a, Vector3 b)
        {
            Vector3 offset = a - b;
            offset.y = 0f;
            return offset.sqrMagnitude;
        }

        private static ChefState NearestTeammate(ChefState chef, float range)
        {
            ChefState best = null;
            float bestDistance = range * range;
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState other = chefs[i];
                if (other == chef || other.Chef == null || other.Team != chef.Team || Respawning(other)) continue;
                float distance = FlatSqrDistance(chef.Position, other.Position);
                if (distance < bestDistance)
                {
                    best = other;
                    bestDistance = distance;
                }
            }
            return best;
        }

        private static float BuffUntil(ChefState chef, int buff)
        {
            Buff value;
            return chef.Buffs.TryGetValue(buff, out value) && value.Until > SpecialRuntime.Now ? value.Until : 0f;
        }

        internal static bool IsGuarded(GameObject chef)
        {
            if (!AnyGuarded || chef == null || !SpecialRuntime.InLevel) return false;
            ChefState state = SpecialRuntime.StateOf(chef);
            return state != null && SpecialRuntime.HasBuff(state, GuardedBuff);
        }

        /// <summary>
        /// Bodyguard: "BLOCKED!" over a chef whose knock was stopped, once per knock. Knocks run on
        /// the host and the chef's owner only, so the host shows it to every machine.
        /// </summary>
        internal static void ShowBlocked(GameObject chef)
        {
            if (chef == null || !SpecialNet.IsServer) return;
            int key = chef.GetInstanceID();
            float last;
            if (LastBlocked.TryGetValue(key, out last) && Time.unscaledTime - last < 0.8f) return;
            LastBlocked[key] = Time.unscaledTime;
            SpecialFx.TextAll(chef, Lang.Both("BLOCKED!", "CHẶN!"), Special.Bodyguard);
            SpecialFx.BurstAll(chef.transform.position, Special.Bodyguard);
        }

        // --- what movement and skills read (every machine) --------------------------------------

        private static int LevelBonus(ChefState state, Skill skill)
        {
            int bonus = 0;
            float now = SpecialRuntime.Now;
            Buff frenzy;
            if (state.Buffs.TryGetValue(FrenzyBuff, out frenzy) && frenzy.Until > now)
                bonus += frenzy.Until - now > FrenzyCrash ? 2 : -1;
            if (state.Has(Special.GlassCannon))
                bonus += SpecialRuntime.HasBuff(state, GlassCannonBuff) ? -1 : 1;
            if (state.Has(Special.SlowStarter))
                bonus += SpecialRuntime.HasBuff(state, SlowReadyBuff) ? 2 : -1;
            if (SpecialRuntime.HasBuff(state, SecondWindBuff))
                bonus += skill == Skill.Running || skill == Skill.Dashing ? 3 : 1;
            return bonus;
        }

        private static float RunModifier(ChefState state)
        {
            if (state.Buffs.Count == 0) return 1f;
            return 1f + SpecialRuntime.BuffStrength(state, RallyBuff) + SpecialRuntime.BuffStrength(state, AdrenalineBuff) +
                SpecialRuntime.BuffStrength(state, BuddyBuff);
        }

        private static float DashCooldownModifier(ChefState state)
        {
            // Adrenaline: the dash recharges at once.
            return state.Buffs.Count != 0 && SpecialRuntime.HasBuff(state, AdrenalineBuff) ? 0f : 1f;
        }

        // --- Rally Cry, Frenzy (every machine, on the server's confirmation) ---------------------

        private static void RallyActivated(ChefState state)
        {
            SpecialRuntime.AddBuffLocal(state, RallyBuff, RallySeconds, RallyStrength, state);
            SpecialFx.Burst(state.Position, RallyColour, 3.5f, 0.7f);
            foreach (ChefState mate in SpecialRuntime.Teammates(state))
            {
                if (mate.Chef == null || !Affects(state, mate)) continue;
                SpecialRuntime.AddBuffLocal(mate, RallyBuff, RallySeconds, RallyStrength, state);
                SpecialFx.Beam(state.Position, mate.Position, RallyColour, 0.6f);
            }
        }

        private static void FrenzyActivated(ChefState state)
        {
            // One buff: +2 while more than FrenzyCrash seconds are left, -1 after (see LevelBonus).
            SpecialRuntime.AddBuffLocal(state, FrenzyBuff, FrenzyHigh + FrenzyCrash, 1f, state);
        }

        // --- Sneeze Bomb ------------------------------------------------------------------------

        private static bool SneezeExecute(ChefState state)
        {
            // Where the sneezer's own machine had it at the press (the host's view lags).
            Vector3 centre = state.RequestPosition;
            int hit = 0;
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState other = chefs[i];
                if (other == state || other.Chef == null || Respawning(other) || !Affects(state, other)) continue;
                if (FlatSqrDistance(centre, other.Position) > SneezeRange * SneezeRange) continue;
                hit++;
                if (other.Id == 0) ApplySneeze(other, state, SneezeSeconds);
                else SpecialNet.SendToAll(new SpecialMessage { Topic = SneezeTopic, Chef = other.Id, D = state.Id, A = SneezeSeconds });
            }
            return hit > 0 || state.Refuse(Lang.Both("NOBODY WITHIN 3 M", "KHÔNG CÓ AI TRONG 3 M"));
        }

        private static void SneezeActivated(ChefState state)
        {
            SpecialFx.Burst(state.Position, SneezeColour, SneezeRange, 0.6f);
        }

        private static void OnSneeze(SpecialMessage message)
        {
            ApplySneeze(SpecialRuntime.StateById(message.Chef), SpecialRuntime.StateById((uint)message.D),
                message.A > 0f ? message.A : SneezeSeconds);
        }

        private static void ApplySneeze(ChefState target, ChefState source, float seconds)
        {
            if (target == null || !Affects(source, target)) return;
            // An opponent's sneeze still reaches a Lone Wolf (AddBuffLocal refuses any other
            // chef's buff on one), so only a teammate is named as the source.
            ChefState from = source != null && source.Team == target.Team ? source : null;
            SpecialRuntime.AddBuffLocal(target, BuffId.Frozen, seconds, 1f, from);
            if (target.Chef == null) return;
            SpecialFx.Mark(target.Chef, SneezeColour, seconds, Lang.T("ACHOO!", "HẮT XÌ!"), 0f);
            if (source != null && source.Chef != null)
                SpecialFx.Beam(source.Position, target.Position, SneezeColour, 0.4f);
            // The game's own bump puff and sound, at the head.
            try
            {
                ClientPlayerControlsImpl_Default impl = target.Chef.GetComponent<ClientPlayerControlsImpl_Default>();
                if (impl != null) impl.DoImpactEffect(Vector3.up);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("SneezeBomb.Effect", error);
            }
        }

        // --- Battery ----------------------------------------------------------------------------

        private static bool Rechargeable(ChefState mate, float now)
        {
            // Not "once per level" actives (Spare Pan): half of forever is still forever.
            return mate.Chef != null && mate.Active != Special.None && mate.CooldownUntil - now > 1f &&
                mate.CooldownTotal > 0f && mate.CooldownTotal <= 600f;
        }

        /// <summary>
        /// Host, after any active is confirmed. A Battery chef's active charges a teammate's: the
        /// one it faced at the press if that one is recharging, else the one with the longest wait.
        /// If nobody is recharging the charge is kept and goes to the next teammate who uses theirs.
        /// </summary>
        private static void OnAnyActivated(ChefState state, Special special)
        {
            if (!SpecialNet.IsServer || !SpecialRuntime.AnyHas(Special.Battery)) return;
            float now = SpecialRuntime.Now;

            // A teammate just used theirs: a kept charge goes to them.
            if (BatteryCharged.Count > 0 && Rechargeable(state, now))
            {
                foreach (ChefState holder in SpecialRuntime.Teammates(state))
                {
                    if (!BatteryCharged.Contains(holder) || !Affects(holder, state)) continue;
                    SendBattery(holder, state);
                    SetCharge(holder, false);
                    break;
                }
            }

            if (!state.Has(Special.Battery)) return;
            ChefState best = null;
            float bestScore = float.MinValue;
            Vector3 from = state.RequestPosition, facing = state.RequestForward;
            facing.y = 0f;
            foreach (ChefState mate in SpecialRuntime.Teammates(state))
            {
                if (!Rechargeable(mate, now) || !Affects(state, mate)) continue;
                Vector3 offset = mate.Position - from;
                offset.y = 0f;
                float score = mate.CooldownUntil - now;
                if (offset.magnitude <= BatteryFaceRange && offset.sqrMagnitude > 0.0001f && facing.sqrMagnitude > 0.0001f &&
                    Vector3.Angle(facing, offset) <= BatteryFaceArc)
                    score += 10000f - offset.magnitude;
                if (score > bestScore)
                {
                    best = mate;
                    bestScore = score;
                }
            }
            if (best != null) SendBattery(state, best);
            else if (!BatteryCharged.Contains(state)) SetCharge(state, true);
        }

        private static void SendBattery(ChefState holder, ChefState target)
        {
            float left = target.CooldownUntil - SpecialRuntime.Now;
            float amount = Mathf.Min(left, target.CooldownTotal * BatteryShare);
            if (amount <= 0f) return;
            var message = new SpecialMessage { Topic = BatteryTopic, Chef = target.Id, D = holder.Id, A = amount };
            if (target.Id == 0) ApplyBattery(target, holder, amount);
            else SpecialNet.SendToAll(message);
        }

        private static void OnBattery(SpecialMessage message)
        {
            ApplyBattery(SpecialRuntime.StateById(message.Chef), SpecialRuntime.StateById((uint)message.D), message.A);
        }

        /// <summary>Every machine: the same number of seconds comes off the teammate's cooldown.</summary>
        private static void ApplyBattery(ChefState target, ChefState holder, float seconds)
        {
            if (target == null || seconds <= 0f) return;
            SpecialRuntime.ShortenCooldownLocal(target, seconds);
            if (target.Chef == null) return;
            Color colour = SpecialFx.ColourOf(Special.Battery);
            if (holder != null && holder.Chef != null) SpecialFx.Beam(holder.Position, target.Position, colour, 0.8f);
            SpecialFx.Burst(target.Position, colour, 1.2f, 0.5f);
            SpecialFx.Text(target.Chef, Lang.T("CHARGED -", "ĐƯỢC SẠC -") + Mathf.RoundToInt(seconds) + "s", colour);
        }

        private static void SetCharge(ChefState holder, bool charged)
        {
            if (charged) BatteryCharged.Add(holder);
            else BatteryCharged.Remove(holder);
            if (holder.Id == 0) holder.SetCount(Special.Battery, charged ? 1 : 0);
            else SpecialNet.SendToAll(new SpecialMessage { Topic = BatteryChargeTopic, Chef = holder.Id, I = charged ? 1 : 0 });
        }

        private static void OnBatteryCharge(SpecialMessage message)
        {
            ChefState holder = SpecialRuntime.StateById(message.Chef);
            if (holder == null) return;
            holder.SetCount(Special.Battery, message.I);
            if (message.I > 0 && holder.Chef != null)
                SpecialFx.Text(holder.Chef, Lang.T("CHARGE STORED", "ĐÃ TÍCH SẠC"), SpecialFx.ColourOf(Special.Battery));
        }

        // --- every frame ------------------------------------------------------------------------

        private static void OnTick(float deltaTime)
        {
            if (SpecialNet.IsServer)
            {
                WatchRespawns();
                SendAuras();
                ShareBuddyBuffs();
                UpdateSecondWind();
                UpdateSlowStarter(deltaTime);
            }
            else WatchKnocks = false;
            DrawAuras();
        }

        /// <summary>
        /// Host: Bodyguard and Buddy System, from the host's view of where everyone is. A short buff
        /// is renewed while it holds, so every machine agrees who is guarded or buddied.
        /// </summary>
        private static void SendAuras()
        {
            if (SpecialRuntime.Now < s_nextAuraSend) return;
            s_nextAuraSend = SpecialRuntime.Now + AuraSendEvery;
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState chef = chefs[i];
                if (chef.Chef == null || Respawning(chef)) continue;
                if (chef.Has(Special.Bodyguard))
                {
                    for (int j = 0; j < chefs.Count; j++)
                    {
                        ChefState mate = chefs[j];
                        if (mate == chef || mate.Chef == null || mate.Team != chef.Team || !Affects(chef, mate)) continue;
                        if (FlatSqrDistance(chef.Position, mate.Position) > BodyguardRange * BodyguardRange) continue;
                        SpecialRuntime.BroadcastBuff(mate, GuardedBuff, AuraSendSeconds, 1f, chef);
                    }
                }
                if (chef.Has(Special.BuddySystem))
                {
                    ChefState partner = NearestTeammate(chef, BuddyRange);
                    if (partner == null) continue;
                    SpecialRuntime.BroadcastBuff(chef, BuddyBuff, AuraSendSeconds, BuddyStrength, chef);
                    if (Affects(chef, partner))
                        SpecialRuntime.BroadcastBuff(partner, BuddyBuff, AuraSendSeconds, BuddyStrength, chef);
                }
            }
        }

        /// <summary>Host: falls (Glass Cannon) and respawns (Adrenaline), from PlayerControls.m_bRespawning.</summary>
        private static void WatchRespawns()
        {
            bool watch = false;
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState chef = chefs[i];
                if (!chef.Has(Special.Adrenaline) && !chef.Has(Special.GlassCannon)) continue;
                watch = true;
                if (chef.Controls == null) continue;
                bool now = chef.Controls.m_bRespawning;
                bool before;
                WasRespawning.TryGetValue(chef, out before);
                if (now == before) continue;
                WasRespawning[chef] = now;
                if (now)
                {
                    if (chef.Has(Special.GlassCannon))
                        SpecialRuntime.BroadcastBuff(chef, GlassCannonBuff, GlassCannonSeconds, 1f, chef);
                }
                else if (chef.Has(Special.Adrenaline))
                    SpecialRuntime.BroadcastBuff(chef, AdrenalineBuff, AdrenalineSeconds, AdrenalineStrength, chef);
            }
            WatchKnocks = watch;
        }

        /// <summary>Host: a chef was knocked back or slipped (Adrenaline, Glass Cannon).</summary>
        internal static void OnKnocked(ChefState chef)
        {
            if (chef == null || !SpecialNet.IsServer) return;
            bool adrenaline = chef.Has(Special.Adrenaline), glass = chef.Has(Special.GlassCannon);
            if (!adrenaline && !glass) return;
            float last;
            if (LastKnock.TryGetValue(chef, out last) && SpecialRuntime.Now - last < KnockDebounce) return;
            LastKnock[chef] = SpecialRuntime.Now;
            if (adrenaline) SpecialRuntime.BroadcastBuff(chef, AdrenalineBuff, AdrenalineSeconds, AdrenalineStrength, chef);
            if (glass) SpecialRuntime.BroadcastBuff(chef, GlassCannonBuff, GlassCannonSeconds, 1f, chef);
        }

        /// <summary>
        /// Host: Buddy System's sharing. Watches every chef's speed buffs; when one starts or is
        /// extended on a chef, the same buff (time left, strength) goes to that chef's buddy
        /// partners. What it gives is recorded at once (BroadcastBuff applies on the host
        /// synchronously), so a shared buff is never seen as new and passed back or on.
        /// </summary>
        private static void ShareBuddyBuffs()
        {
            if (!SpecialRuntime.AnyHas(Special.BuddySystem)) return;
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState chef = chefs[i];
                float[] seen;
                bool fresh = !SeenBuffs.TryGetValue(chef, out seen);
                if (fresh)
                {
                    seen = new float[SpeedBuffs.Length];
                    SeenBuffs[chef] = seen;
                }
                for (int k = 0; k < SpeedBuffs.Length; k++)
                {
                    float until = BuffUntil(chef, SpeedBuffs[k]);
                    if (!fresh && until > seen[k] + 0.01f) Grants.Add(new KeyValuePair<ChefState, int>(chef, k));
                    seen[k] = until;
                }
            }
            if (Grants.Count == 0) return;

            for (int g = 0; g < Grants.Count; g++)
            {
                ChefState chef = Grants[g].Key;
                int k = Grants[g].Value;
                if (chef.Chef == null) continue;
                // Your buddy is the teammate nearest you; you are the buddy of anyone with Buddy
                // System whose nearest teammate is you ("and vice versa").
                ChefState partner = chef.Has(Special.BuddySystem) ? NearestTeammate(chef, float.MaxValue / 4f) : null;
                if (partner != null) Share(chef, partner, k);
                for (int i = 0; i < chefs.Count; i++)
                {
                    ChefState other = chefs[i];
                    if (other == chef || other == partner || other.Chef == null || other.Team != chef.Team) continue;
                    if (other.Has(Special.BuddySystem) && NearestTeammate(other, float.MaxValue / 4f) == chef) Share(chef, other, k);
                }
            }
            Grants.Clear();
        }

        private static void Share(ChefState from, ChefState to, int index)
        {
            int id = SpeedBuffs[index];
            Buff buff;
            if (!from.Buffs.TryGetValue(id, out buff)) return;
            float left = buff.Until - SpecialRuntime.Now;
            if (left <= 0.05f || !Affects(from, to)) return;
            // Already has it at least as long (a Rally Cry both got): nothing to send.
            if (BuffUntil(to, id) >= buff.Until - 0.05f) return;
            SpecialRuntime.BroadcastBuff(to, id, left, buff.Strength, from);
            float[] seen;
            if (SeenBuffs.TryGetValue(to, out seen)) seen[index] = BuffUntil(to, id);
            if (to.Chef != null && from.Chef != null) SpecialFx.BeamAll(from.Position, to.Position, Special.BuddySystem, 0.6f);
        }

        /// <summary>
        /// Host: Second Wind. While the round timer shows 30 s or less, keeps a short buff going on
        /// every Second Wind chef (renewed twice a second), so added time or a pause is followed.
        /// </summary>
        private static void UpdateSecondWind()
        {
            if (SpecialRuntime.Now < s_nextSecondWind) return;
            s_nextSecondWind = SpecialRuntime.Now + 0.5f;
            if (!SpecialRuntime.AnyHas(Special.SecondWind)) return;
            float left = TimeLeft();
            if (left <= 0f || left > SecondWindSeconds) return;
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
                if (chefs[i].Has(Special.SecondWind))
                    SpecialRuntime.BroadcastBuff(chefs[i], SecondWindBuff, 1.5f, 1f, chefs[i]);
        }

        /// <summary>
        /// Host: Slow Starter. Counts the seconds the round timer has run (not the intro); while
        /// under 60 a ramp buff carries the time left (for the players' icons), after that a
        /// level-long buff gives the +2, so every machine switches at the same moment.
        /// </summary>
        private static void UpdateSlowStarter(float deltaTime)
        {
            if (!SpecialRuntime.AnyHas(Special.SlowStarter)) return;
            IServerRoundTimer timer = RoundTimer();
            if (timer == null) s_roundElapsed += deltaTime;
            else
            {
                float value = timer.TimeElapsed;
                if (s_lastTimerValue >= 0f)
                {
                    float step = timer is ServerModifiableRoundTimer ? s_lastTimerValue - value : value - s_lastTimerValue;
                    // Time added to the clock (or a reset) isn't time played.
                    if (step > 0f && step < 1f) s_roundElapsed += step;
                }
                s_lastTimerValue = value;
            }

            if (SpecialRuntime.Now < s_nextSlowSend) return;
            bool ready = s_roundElapsed >= SlowStarterSeconds;
            // While ramping, often (a short last ramp); once ready, now and then in case a chef was missed.
            s_nextSlowSend = SpecialRuntime.Now + (ready ? 5f : 1f);
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState chef = chefs[i];
                if (!chef.Has(Special.SlowStarter)) continue;
                if (ready) SpecialRuntime.BroadcastBuff(chef, SlowReadyBuff, 100000f, 1f, chef);
                else
                {
                    float left = SlowStarterSeconds - s_roundElapsed;
                    SpecialRuntime.BroadcastBuff(chef, SlowRampBuff, left, 1f, chef);
                    // The last stretch is shorter than the next send: be ready on time.
                    if (left < 1f) s_nextSlowSend = SpecialRuntime.Now + Mathf.Max(0.1f, left);
                }
            }
        }

        private static IServerRoundTimer RoundTimer()
        {
            if (s_flow == null)
            {
                if (Time.unscaledTime < s_nextFlowSearch) return null;
                s_nextFlowSearch = Time.unscaledTime + 1f;
                s_flow = Object.FindObjectOfType<ServerKitchenFlowControllerBase>();
                if (s_flow == null) return null;
            }
            return s_flow.RoundTimer;
        }

        /// <summary>Host: seconds left on the level's round timer; negative if there is none.</summary>
        private static float TimeLeft()
        {
            IServerRoundTimer timer = RoundTimer();
            if (timer == null) return -1f;
            // Counts down from the limit (levels that add time).
            if (timer is ServerModifiableRoundTimer) return timer.TimeElapsed;
            ServerRoundTimer fixedTimer = timer as ServerRoundTimer;
            if (fixedTimer == null || TimeLimitField == null) return -1f; // unlimited
            float limit = (float)TimeLimitField.GetValue(fixedTimer);
            return limit > 0f ? limit - timer.TimeElapsed : -1f;
        }

        // --- what players see (every machine, from its own buff state) --------------------------

        /// <summary>
        /// Every machine: a ring under each chef for each of this module's buffs it has, a word when
        /// one starts, and the local players' skill-bar timers and lines.
        /// </summary>
        private static void DrawAuras()
        {
            bool ring = Time.unscaledTime >= s_nextRing;
            if (ring) s_nextRing = Time.unscaledTime + RingEvery;
            bool anyGuarded = false;
            float now = SpecialRuntime.Now;
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState chef = chefs[i];
                GameObject go = chef.Chef;
                if (go == null) continue;
                bool guarded = SpecialRuntime.HasBuff(chef, GuardedBuff);
                anyGuarded |= guarded;
                bool hidden = Respawning(chef);

                Buff frenzy;
                float frenzyLeft = chef.Buffs.TryGetValue(FrenzyBuff, out frenzy) ? frenzy.Until - now : 0f;
                bool high = frenzyLeft > FrenzyCrash, crash = frenzyLeft > 0f && !high;
                bool rally = SpecialRuntime.HasBuff(chef, RallyBuff);
                bool buddy = SpecialRuntime.HasBuff(chef, BuddyBuff);
                bool wind = SpecialRuntime.HasBuff(chef, SecondWindBuff);
                bool adrenaline = SpecialRuntime.HasBuff(chef, AdrenalineBuff);
                bool cracked = chef.Has(Special.GlassCannon) && SpecialRuntime.HasBuff(chef, GlassCannonBuff);
                bool slow = chef.Has(Special.SlowStarter) && !SpecialRuntime.HasBuff(chef, SlowReadyBuff);

                // Words as each one starts (or ends, for the bad ones).
                int flags = (rally ? FlagRally : 0) | (adrenaline ? FlagAdrenaline : 0) | (wind ? FlagWind : 0) |
                    (crash ? FlagCrash : 0) | (cracked ? FlagCracked : 0) | (slow ? FlagSlow : 0);
                int before;
                if (!LastFlags.TryGetValue(chef, out before)) before = flags & FlagSlow;
                if (flags != before)
                {
                    LastFlags[chef] = flags;
                    int started = flags & ~before, ended = before & ~flags;
                    // (The chef who rallied already gets the special's name over its head.)
                    if ((started & FlagRally) != 0 && chef.Buffs[RallyBuff].Source != chef.Id)
                        SpecialFx.Text(go, Lang.T("RALLIED! +30%", "HĂNG HÁI! +30%"), RallyColour);
                    if ((started & FlagAdrenaline) != 0) Pop(go, SpecialCatalog.NameOf(Special.Adrenaline) + "!", AdrenalineColour);
                    if ((started & FlagWind) != 0) Pop(go, SpecialCatalog.NameOf(Special.SecondWind) + "!", SecondWindColour);
                    if ((started & FlagCrash) != 0) SpecialFx.Text(go, Lang.T("CRASH -1", "KIỆT SỨC -1"), CrashColour);
                    if ((started & FlagCracked) != 0) Pop(go, Lang.T("CRACKED! -1", "NỨT! -1"), CrackedColour);
                    else if ((ended & FlagCracked) != 0) SpecialFx.Text(go, Lang.T("MENDED +1", "LÀNH LẠI +1"), CrackedColour);
                    if ((ended & FlagSlow) != 0 && chef.Has(Special.SlowStarter))
                        Pop(go, Lang.T("WARMED UP! +2", "NÓNG MÁY! +2"), SpecialFx.ColourOf(Special.SlowStarter));
                }

                if (ring && !hidden)
                {
                    // Different sizes, so several at once read as rings inside each other.
                    if (guarded) SpecialFx.Mark(go, GuardColour, RingSeconds, null, 0.5f);
                    if (buddy) SpecialFx.Mark(go, BuddyColour, RingSeconds, null, 0.62f);
                    if (rally) SpecialFx.Mark(go, RallyColour, RingSeconds, null, 0.75f);
                    if (high) SpecialFx.Mark(go, FrenzyColour, RingSeconds, null, 0.88f);
                    if (crash || cracked) SpecialFx.Mark(go, cracked ? CrackedColour : CrashColour, RingSeconds, null, 0.88f);
                    if (adrenaline) SpecialFx.Mark(go, AdrenalineColour, RingSeconds, null, 1f);
                    if (wind) SpecialFx.Mark(go, SecondWindColour, RingSeconds, null, 1.1f);
                    if (slow) SpecialFx.Mark(go, SlowColour, RingSeconds, null, 0.42f);
                }

                if (chef.Local) UpdateHud(chef, now, frenzyLeft);
            }
            AnyGuarded = anyGuarded;
        }

        /// <summary>A word and a burst (something big just started).</summary>
        private static void Pop(GameObject go, string text, Color colour)
        {
            SpecialFx.Text(go, text, colour);
            SpecialFx.Burst(go.transform.position, colour, 1.6f, 0.5f);
        }

        /// <summary>The local player's skill bar: timers on the passives' icons and short lines.</summary>
        /// <summary>The chef has this buff now, and a teammate gave it.</summary>
        private static bool FromTeammate(ChefState chef, int buff)
        {
            Buff value;
            return chef.Buffs.TryGetValue(buff, out value) && value.Until > SpecialRuntime.Now && value.Source != 0 && value.Source != chef.Id;
        }

        private static void UpdateHud(ChefState chef, float now, float frenzyLeft)
        {
            if (chef.Has(Special.SlowStarter))
            {
                float until = BuffUntil(chef, SlowRampBuff);
                if (until > 0f && !SpecialRuntime.HasBuff(chef, SlowReadyBuff)) chef.SetTimer(Special.SlowStarter, until, SlowStarterSeconds);
            }
            if (chef.Has(Special.GlassCannon))
            {
                float until = BuffUntil(chef, GlassCannonBuff);
                if (until > 0f) chef.SetTimer(Special.GlassCannon, until, GlassCannonSeconds);
            }
            if (chef.Has(Special.Adrenaline))
            {
                float until = BuffUntil(chef, AdrenalineBuff);
                if (until > 0f) chef.SetTimer(Special.Adrenaline, until, AdrenalineSeconds);
            }
            if (chef.Has(Special.Bodyguard))
            {
                // How many teammates this chef is guarding right now.
                int guarding = 0;
                IList<ChefState> chefs = SpecialRuntime.Chefs;
                for (int i = 0; i < chefs.Count; i++)
                {
                    Buff buff;
                    if (chefs[i] != chef && chefs[i].Buffs.TryGetValue(GuardedBuff, out buff) && buff.Until > now && buff.Source == chef.Id)
                        guarding++;
                }
                chef.SetCount(Special.Bodyguard, guarding);
            }
            chef.SetStatus("SecondWind", SpecialRuntime.HasBuff(chef, SecondWindBuff) ? SpecialCatalog.NameOf(Special.SecondWind) + Lang.T(": ALL +1, MOVE +3", ": TẤT CẢ +1, CHẠY +3") : null, Special.SecondWind);
            // Buffs from a teammate's special show on this player's bar too, not only on theirs.
            int buddy = Mathf.RoundToInt(BuddyStrength * 100f);
            chef.SetStatus("Buddy", SpecialRuntime.HasBuff(chef, BuddyBuff) ? Lang.T("BUDDY +" + buddy + "% SPEED", "CẠ CỨNG +" + buddy + "% TỐC ĐỘ") : null, Special.BuddySystem);
            int rally = Mathf.RoundToInt(RallyStrength * 100f);
            chef.SetStatus("Rally", FromTeammate(chef, RallyBuff) ? SpecialCatalog.NameOf(Special.RallyCry) + Lang.T(" +" + rally + "% SPEED", " +" + rally + "% TỐC ĐỘ") : null, Special.RallyCry);
            chef.SetStatus("Guarded", FromTeammate(chef, GuardedBuff) ? Lang.T("GUARDED: NO KNOCKBACK", "ĐƯỢC BẢO VỆ: KHÔNG BỊ ĐẨY") : null, Special.Bodyguard);

            if (chef.Active != Special.Frenzy)
            {
                chef.SetStatus("Frenzy", null);
                return;
            }
            int key = frenzyLeft <= 0f ? 0 : (frenzyLeft > FrenzyCrash ? 100 : 200) +
                Mathf.CeilToInt(frenzyLeft > FrenzyCrash ? frenzyLeft - FrenzyCrash : frenzyLeft);
            if (chef.Get("Frenzy.Key", -1) == key) return;
            chef.Set("Frenzy.Key", key);
            if (key == 0) chef.SetStatus("Frenzy", null);
            else if (key < 200) chef.SetStatus("Frenzy", SpecialCatalog.NameOf(Special.Frenzy) + " +2 (" + (key - 100) + "s)", Special.Frenzy);
            else chef.SetStatus("Frenzy", Lang.T("CRASH -1 (", "KIỆT SỨC -1 (") + (key - 200) + "s)", Special.Frenzy);
        }

        /// <summary>A copy of a slippery surface without the slip (Bodyguard), made once per surface.</summary>
        internal static PlayerPhysicsSurfaceProperties Unslipped(PlayerPhysicsSurfaceProperties surface)
        {
            PlayerPhysicsSurfaceProperties copy;
            if (NoSlip.TryGetValue(surface, out copy) && copy != null) return copy;
            copy = ScriptableObject.CreateInstance<PlayerPhysicsSurfaceProperties>();
            copy.SpeedMultiplier = surface.SpeedMultiplier;
            copy.Slippiness = 0f;
            copy.Slidiness = surface.Slidiness;
            copy.UseOverrideGravityNormal = surface.UseOverrideGravityNormal;
            copy.OverrideGravityNormal = surface.OverrideGravityNormal;
            copy.GravityMultiplier = surface.GravityMultiplier;
            NoSlip[surface] = copy;
            return copy;
        }
    }

    /// <summary>
    /// Bodyguard: a guarded chef's knock-back never starts (every machine simulates movement and
    /// has the same guard buff, so every machine blocks it). Adrenaline / Glass Cannon: on the
    /// host, an impact that really started counts as a knock.
    /// </summary>
    [HarmonyPatch(typeof(ClientPlayerControlsImpl_Default), "ApplyImpact")]
    internal static class TeamBuffsImpactPatch
    {
        private static readonly FieldInfo ImpactTimerField =
            AccessTools.Field(typeof(ClientPlayerControlsImpl_Default), "m_impactTimer");

        private static bool Prefix(ClientPlayerControlsImpl_Default __instance)
        {
            try
            {
                if (!TeamBuffs.AnyGuarded || TeamBuffs.Landing || __instance == null) return true;
                if (!TeamBuffs.IsGuarded(__instance.gameObject)) return true;
                TeamBuffs.ShowBlocked(__instance.gameObject);
                return false;
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Bodyguard.ApplyImpact", error);
                return true;
            }
        }

        private static void Postfix(ClientPlayerControlsImpl_Default __instance, float _impactTime)
        {
            try
            {
                if (!TeamBuffs.WatchKnocks || TeamBuffs.Landing || _impactTime <= 0f || __instance == null) return;
                if (ImpactTimerField == null || !SpecialRuntime.InLevel) return;
                // Skipped by a prefix (Bodyguard, Immovable...): the timer wasn't set, so no knock.
                if ((float)ImpactTimerField.GetValue(__instance) != _impactTime) return;
                TeamBuffs.OnKnocked(SpecialRuntime.StateOf(__instance.gameObject));
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Adrenaline.ApplyImpact", error);
            }
        }
    }

    /// <summary>Landing from a cannon gives a small impact that isn't a knock.</summary>
    [HarmonyPatch(typeof(ClientCannonPlayerHandler), "Land")]
    internal static class TeamBuffsCannonLandPatch
    {
        private static void Prefix()
        {
            if (SpecialRuntime.InLevel) TeamBuffs.Landing = true;
        }

        private static void Finalizer()
        {
            TeamBuffs.Landing = false;
        }
    }

    /// <summary>Bodyguard: no sliding on ice for a guarded chef (every machine).</summary>
    [HarmonyPatch(typeof(ClientPlayerControlsImpl_Default), "ProgressVelocityWrtFriction")]
    internal static class TeamBuffsFrictionPatch
    {
        private static void Prefix(ClientPlayerControlsImpl_Default __instance, ref PlayerPhysicsSurfaceProperties _surface)
        {
            if (!TeamBuffs.AnyGuarded) return;
            try
            {
                if (ReferenceEquals(_surface, null) || _surface == null || _surface.Slippiness <= 0f) return;
                if (__instance == null || !TeamBuffs.IsGuarded(__instance.gameObject)) return;
                _surface = TeamBuffs.Unslipped(_surface);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Bodyguard.Friction", error);
            }
        }
    }

    /// <summary>
    /// Host: slipping on a puddle. Bodyguard stops it; a slip that happens counts as a knock for
    /// Adrenaline / Glass Cannon (the slip disables the chef's controls at once).
    /// </summary>
    [HarmonyPatch(typeof(ServerSlipCollider), "Slip", new Type[] { typeof(GameObject) })]
    internal static class TeamBuffsSlipPatch
    {
        private static bool Prefix(GameObject _object, out bool __state)
        {
            __state = false;
            try
            {
                if (_object == null || !SpecialRuntime.InLevel) return true;
                if (TeamBuffs.IsGuarded(_object))
                {
                    // Host only: everyone sees the slip that didn't happen.
                    ChefState state = SpecialRuntime.StateOf(_object);
                    if (state != null && state.Chef != null) SpecialFx.TextAll(state.Chef, Lang.Both("BLOCKED!", "CHẶN!"), Special.Bodyguard);
                    return false;
                }
                if (TeamBuffs.WatchKnocks)
                {
                    PlayerControls controls = _object.GetComponent<PlayerControls>();
                    __state = controls != null && controls.enabled;
                }
                return true;
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Bodyguard.Slip", error);
                return true;
            }
        }

        private static void Postfix(GameObject _object, bool __state)
        {
            if (!__state) return;
            try
            {
                PlayerControls controls = _object != null ? _object.GetComponent<PlayerControls>() : null;
                if (controls != null && !controls.enabled) TeamBuffs.OnKnocked(SpecialRuntime.StateOf(_object));
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Adrenaline.Slip", error);
            }
        }
    }
}
