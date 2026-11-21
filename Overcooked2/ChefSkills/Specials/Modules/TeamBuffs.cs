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
    /// Who decides what:
    /// - Rally Cry / Frenzy: OnActivated on every machine (the server's confirmation), no extra messages.
    /// - Sneeze Bomb, Battery: the host picks the targets and tells every machine (own topics).
    /// - Adrenaline / Glass Cannon (knocks, slips, falls), Buddy System, Second Wind: the host sees
    ///   it happen and broadcasts a buff, so every machine applies the same thing.
    /// - Bodyguard: every machine works out who is guarded from the chefs' positions (a knock or
    ///   ice slide is simulated wherever the chef moves; the host blocks puddle slips).
    /// - Slow Starter: SpecialRuntime.LevelTime, the same on every machine.
    ///
    /// Lone Wolf: every effect here that comes from a teammate (Rally Cry, Bodyguard, Battery,
    /// Buddy System, Sneeze Bomb) skips a Lone Wolf teammate; buffs also go through AddBuffLocal,
    /// which refuses them.
    /// </summary>
    internal sealed class TeamBuffs : ISpecialModule
    {
        // Buffs of our own, so they never merge with another module's in a shared slot.
        internal const int FrenzyBuff = 7901;
        internal const int SecondWindBuff = 8101;
        internal const int GlassCannonBuff = 8501;

        private const ushort BatteryTopic = (ushort)((int)Special.Battery * 10);
        private const ushort SneezeTopic = (ushort)((int)Special.SneezeBomb * 10);

        private const float RallySeconds = 6f;
        private const float RallyStrength = 0.3f;
        private const float BodyguardRange = 3f;
        private const float FrenzyPhase = 8f;
        private const float SecondWindSeconds = 30f;
        private const float AdrenalineSeconds = 4f;
        private const float AdrenalineStrength = 0.4f;
        private const float GlassCannonSeconds = 15f;
        private const float SlowStarterSeconds = 60f;
        private const float SneezeRange = 2f;
        private const float SneezeSeconds = 1f;
        // One knock often comes as several impacts (dash collisions on consecutive frames).
        private const float KnockDebounce = 0.5f;

        private static readonly FieldInfo TimeLimitField = AccessTools.Field(typeof(ServerRoundTimer), "m_timeLimit");

        // Speed buffs Buddy System shares.
        private static readonly int[] SpeedBuffs = { BuffId.RunSpeed, BuffId.DashSpeed, BuffId.NoDashCooldown, BuffId.MoveBonus };

        // Bodyguard: chefs (GameObject instance IDs) guarded this frame.
        private static readonly HashSet<int> Guarded = new HashSet<int>();
        internal static bool AnyGuarded;
        // Host: some chef has Adrenaline or Glass Cannon, so knocks matter.
        internal static bool WatchKnocks;
        // A cannon landing nudge is under way (not a knock).
        internal static bool Landing;

        private static readonly Dictionary<ChefState, bool> WasRespawning = new Dictionary<ChefState, bool>();
        private static readonly Dictionary<ChefState, float> LastKnock = new Dictionary<ChefState, float>();
        private static readonly Dictionary<ChefState, float[]> SeenBuffs = new Dictionary<ChefState, float[]>();
        private static readonly List<KeyValuePair<ChefState, int>> Grants = new List<KeyValuePair<ChefState, int>>();
        private static readonly Dictionary<PlayerPhysicsSurfaceProperties, PlayerPhysicsSurfaceProperties> NoSlip =
            new Dictionary<PlayerPhysicsSurfaceProperties, PlayerPhysicsSurfaceProperties>();

        private static ServerKitchenFlowControllerBase s_flow;
        private static float s_nextFlowSearch;
        private static float s_nextSecondWind;

        public void Register()
        {
            SpecialRuntime.RegisterActive(Special.RallyCry, new ActiveHandler
            {
                Execute = RallyExecute, OnActivated = RallyActivated, Duration = RallySeconds
            });
            SpecialRuntime.RegisterActive(Special.Frenzy, new ActiveHandler
            {
                Execute = delegate(ChefState state) { return true; },
                OnActivated = FrenzyActivated,
                Duration = FrenzyPhase * 2f
            });
            SpecialRuntime.RegisterActive(Special.SneezeBomb, new ActiveHandler
            {
                Execute = SneezeExecute, Duration = SneezeSeconds
            });
            SpecialRuntime.LevelBonusProviders.Add(LevelBonus);
            SpecialRuntime.Tick += OnTick;
            SpecialRuntime.LevelStarted += Reset;
            SpecialRuntime.LevelEnded += Reset;
            SpecialRuntime.Activated += OnAnyActivated;
            SpecialNet.Register(BatteryTopic, OnBattery);
            SpecialNet.Register(SneezeTopic, OnSneeze);
        }

        private static void Reset()
        {
            Guarded.Clear();
            AnyGuarded = false;
            WatchKnocks = false;
            Landing = false;
            WasRespawning.Clear();
            LastKnock.Clear();
            SeenBuffs.Clear();
            Grants.Clear();
            foreach (PlayerPhysicsSurfaceProperties copy in NoSlip.Values)
                if (copy != null) Object.Destroy(copy);
            NoSlip.Clear();
            s_flow = null;
            s_nextFlowSearch = 0f;
            s_nextSecondWind = 0f;
        }

        // --- helpers ----------------------------------------------------------------------------

        /// <summary>Can an effect from <paramref name="source"/> reach <paramref name="target"/> (Lone Wolf)?</summary>
        internal static bool Affects(ChefState source, ChefState target)
        {
            if (target == null) return false;
            if (source == null || source == target || source.Team != target.Team) return true;
            return !target.Has(Special.LoneWolf);
        }

        private static float FlatSqrDistance(ChefState a, ChefState b)
        {
            Vector3 offset = a.Position - b.Position;
            offset.y = 0f;
            return offset.sqrMagnitude;
        }

        private static ChefState NearestTeammate(ChefState chef)
        {
            ChefState best = null;
            float bestDistance = float.MaxValue;
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState other = chefs[i];
                if (other == chef || other.Chef == null || other.Team != chef.Team) continue;
                float distance = FlatSqrDistance(chef, other);
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
            return AnyGuarded && chef != null && SpecialRuntime.InLevel && Guarded.Contains(chef.GetInstanceID());
        }

        // --- skill levels (every machine) -------------------------------------------------------

        private static int LevelBonus(ChefState state, Skill skill)
        {
            int bonus = 0;
            float now = SpecialRuntime.Now;
            Buff frenzy;
            if (state.Buffs.TryGetValue(FrenzyBuff, out frenzy) && frenzy.Until > now)
                bonus += frenzy.Until - now > FrenzyPhase ? 2 : -2;
            if (state.Has(Special.GlassCannon))
                bonus += SpecialRuntime.HasBuff(state, GlassCannonBuff) ? -1 : 1;
            if (state.Has(Special.SlowStarter))
                bonus += SpecialRuntime.LevelTime < SlowStarterSeconds ? -1 : 2;
            if ((skill == Skill.Running || skill == Skill.Dashing) && SpecialRuntime.HasBuff(state, SecondWindBuff))
                bonus += 3;
            return bonus;
        }

        // --- actives ----------------------------------------------------------------------------

        private static bool RallyExecute(ChefState state)
        {
            foreach (ChefState mate in SpecialRuntime.Teammates(state))
                if (Affects(state, mate)) return true;
            return false;
        }

        private static void RallyActivated(ChefState state)
        {
            // Every machine runs this on the server's confirmation, so no buff messages are needed.
            foreach (ChefState mate in SpecialRuntime.Teammates(state))
                SpecialRuntime.AddBuffLocal(mate, BuffId.RunSpeed, RallySeconds, RallyStrength, state);
        }

        private static void FrenzyActivated(ChefState state)
        {
            // One 16 s buff: +2 while more than 8 s are left, -2 after (see LevelBonus).
            SpecialRuntime.AddBuffLocal(state, FrenzyBuff, FrenzyPhase * 2f, 1f, state);
        }

        private static bool SneezeExecute(ChefState state)
        {
            int hit = 0;
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState other = chefs[i];
                if (other == state || other.Chef == null || !Affects(state, other)) continue;
                if (FlatSqrDistance(state, other) > SneezeRange * SneezeRange) continue;
                hit++;
                if (other.Id == 0) ApplySneeze(other, state);
                else SpecialNet.SendToAll(new SpecialMessage { Topic = SneezeTopic, Chef = other.Id, D = state.Id });
            }
            return hit > 0;
        }

        private static void OnSneeze(SpecialMessage message)
        {
            ApplySneeze(SpecialRuntime.StateById(message.Chef), SpecialRuntime.StateById((uint)message.D));
        }

        private static void ApplySneeze(ChefState target, ChefState source)
        {
            if (target == null || !Affects(source, target)) return;
            SpecialRuntime.AddBuffLocal(target, BuffId.Frozen, SneezeSeconds, 1f, source);
            // The game's own bump puff and sound, at the head.
            try
            {
                ClientPlayerControlsImpl_Default impl = target.Chef != null
                    ? target.Chef.GetComponent<ClientPlayerControlsImpl_Default>() : null;
                if (impl != null) impl.DoImpactEffect(Vector3.up);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("SneezeBomb.Effect", error);
            }
        }

        // --- Battery ----------------------------------------------------------------------------

        private static void OnAnyActivated(ChefState state, Special special)
        {
            // The host picks the teammate so every machine halves the same cooldown.
            if (!state.Has(Special.Battery) || !SpecialNet.IsServer) return;
            float now = SpecialRuntime.Now;
            ChefState best = null;
            float bestDistance = float.MaxValue;
            foreach (ChefState mate in SpecialRuntime.Teammates(state))
            {
                if (mate.Chef == null || mate.Active == Special.None || mate.CooldownUntil <= now) continue;
                if (!Affects(state, mate)) continue;
                float distance = FlatSqrDistance(state, mate);
                if (distance < bestDistance)
                {
                    best = mate;
                    bestDistance = distance;
                }
            }
            if (best == null) return;
            if (best.Id == 0) HalveCooldown(best);
            else SpecialNet.SendToAll(new SpecialMessage { Topic = BatteryTopic, Chef = best.Id, D = state.Id });
        }

        private static void OnBattery(SpecialMessage message)
        {
            HalveCooldown(SpecialRuntime.StateById(message.Chef));
        }

        private static void HalveCooldown(ChefState target)
        {
            if (target == null) return;
            float left = target.CooldownUntil - SpecialRuntime.Now;
            if (left > 0f) SpecialRuntime.ShortenCooldownLocal(target, left * 0.5f);
        }

        // --- every frame ------------------------------------------------------------------------

        private static void OnTick(float deltaTime)
        {
            UpdateGuards();
            if (SpecialNet.IsServer)
            {
                WatchRespawns();
                ShareBuddyBuffs();
                UpdateSecondWind();
            }
            else WatchKnocks = false;
            UpdateFrenzyStatus();
        }

        /// <summary>Bodyguard: who is within range of a teammate bodyguard (every machine).</summary>
        private static void UpdateGuards()
        {
            if (AnyGuarded) Guarded.Clear();
            AnyGuarded = false;
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState guard = chefs[i];
                if (guard.Chef == null || !guard.Has(Special.Bodyguard)) continue;
                if (guard.Controls != null && guard.Controls.m_bRespawning) continue;
                for (int j = 0; j < chefs.Count; j++)
                {
                    ChefState mate = chefs[j];
                    if (mate == guard || mate.Chef == null || mate.Team != guard.Team || !Affects(guard, mate)) continue;
                    if (FlatSqrDistance(guard, mate) > BodyguardRange * BodyguardRange) continue;
                    Guarded.Add(mate.Chef.GetInstanceID());
                    AnyGuarded = true;
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
                    SpecialRuntime.BroadcastBuff(chef, BuffId.RunSpeed, AdrenalineSeconds, AdrenalineStrength, chef);
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
            if (adrenaline) SpecialRuntime.BroadcastBuff(chef, BuffId.RunSpeed, AdrenalineSeconds, AdrenalineStrength, chef);
            if (glass) SpecialRuntime.BroadcastBuff(chef, GlassCannonBuff, GlassCannonSeconds, 1f, chef);
        }

        /// <summary>
        /// Host: Buddy System. Watches every chef's speed buffs; when one starts or is extended on a
        /// chef, the same buff (time left, strength) goes to that chef's buddy partners. What it
        /// gives is recorded at once (BroadcastBuff applies on the host synchronously), so a shared
        /// buff is never seen as new and passed back or on.
        /// </summary>
        private static void ShareBuddyBuffs()
        {
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            bool any = false;
            for (int i = 0; i < chefs.Count && !any; i++)
                any = chefs[i].Has(Special.BuddySystem);
            if (!any) return;

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
                ChefState partner = chef.Has(Special.BuddySystem) ? NearestTeammate(chef) : null;
                if (partner != null) Share(chef, partner, k);
                for (int i = 0; i < chefs.Count; i++)
                {
                    ChefState other = chefs[i];
                    if (other == chef || other == partner || other.Chef == null || other.Team != chef.Team) continue;
                    if (other.Has(Special.BuddySystem) && NearestTeammate(other) == chef) Share(chef, other, k);
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
            if (left <= 0.05f) return;
            SpecialRuntime.BroadcastBuff(to, id, left, buff.Strength, from);
            float[] seen;
            if (SeenBuffs.TryGetValue(to, out seen)) seen[index] = BuffUntil(to, id);
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
                    SpecialRuntime.BroadcastBuff(chefs[i], SecondWindBuff, 1.5f, 3f, chefs[i]);
        }

        /// <summary>Host: seconds left on the level's round timer; negative if there is none.</summary>
        private static float TimeLeft()
        {
            if (s_flow == null)
            {
                if (Time.unscaledTime < s_nextFlowSearch) return -1f;
                s_nextFlowSearch = Time.unscaledTime + 1f;
                s_flow = Object.FindObjectOfType<ServerKitchenFlowControllerBase>();
                if (s_flow == null) return -1f;
            }
            IServerRoundTimer timer = s_flow.RoundTimer;
            if (timer == null) return -1f;
            // Counts down from the limit (levels that add time).
            if (timer is ServerModifiableRoundTimer) return timer.TimeElapsed;
            ServerRoundTimer fixedTimer = timer as ServerRoundTimer;
            if (fixedTimer == null || TimeLimitField == null) return -1f; // unlimited
            float limit = (float)TimeLimitField.GetValue(fixedTimer);
            return limit > 0f ? limit - timer.TimeElapsed : -1f;
        }

        /// <summary>The local Frenzy chef sees which half it is in.</summary>
        private static void UpdateFrenzyStatus()
        {
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState chef = chefs[i];
                if (!chef.Local || chef.Active != Special.Frenzy) continue;
                Buff buff;
                float left = chef.Buffs.TryGetValue(FrenzyBuff, out buff) ? buff.Until - SpecialRuntime.Now : 0f;
                int key = left <= 0f ? 0 : (left > FrenzyPhase ? 100 : 200) + Mathf.CeilToInt(left > FrenzyPhase ? left - FrenzyPhase : left);
                if (chef.Get("Frenzy.Key", -1) == key) continue;
                chef.Set("Frenzy.Key", key);
                if (key == 0) chef.SetStatus("Frenzy", null);
                else if (key < 200) chef.SetStatus("Frenzy", "FRENZY +2 (" + (key - 100) + "s)");
                else chef.SetStatus("Frenzy", "CRASH -2 (" + (key - 200) + "s)");
            }
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
    /// Bodyguard: a guarded chef's knock-back never starts (every machine simulates movement, so
    /// every machine blocks it). Adrenaline / Glass Cannon: on the host, an impact that really
    /// started counts as a knock.
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
                return !TeamBuffs.IsGuarded(__instance.gameObject);
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
                if (TeamBuffs.IsGuarded(_object)) return false;
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
