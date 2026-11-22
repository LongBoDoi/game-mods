using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ChefSkills
{
    /// <summary>
    /// A group of special skills. Every non-abstract class implementing this in the assembly is
    /// created once when the plugin loads and its <see cref="Register"/> called, where it hooks
    /// into <see cref="SpecialRuntime"/> (actives, speed modifiers, tick, net topics). Harmony
    /// patches in the same file are applied by the plugin's PatchAll as usual.
    /// </summary>
    internal interface ISpecialModule
    {
        void Register();
    }

    /// <summary>What an active special does. Only <see cref="Execute"/> is required.</summary>
    internal sealed class ActiveHandler
    {
        /// <summary>
        /// Server only: do it. False when there's nothing to use it on (no cooldown is started and
        /// the player hears a "no"). Called with the activating chef's state.
        /// </summary>
        public Func<ChefState, bool> Execute;

        /// <summary>
        /// Optional, the player's own machine when it isn't the server: what to show or move at
        /// once, before the server answers (its effect must match what Execute will do).
        /// </summary>
        public Action<ChefState> Predict;

        /// <summary>Optional, every machine once the server has confirmed it (buffs, effects).</summary>
        public Action<ChefState> OnActivated;

        /// <summary>Seconds the effect lasts (shown on the HUD; ChefState.ActiveUntil).</summary>
        public float Duration;
    }

    /// <summary>A timed effect on a chef, the same on every machine.</summary>
    internal struct Buff
    {
        public float Until;
        public float Strength;
        public uint Source;
    }

    /// <summary>Buff IDs the framework applies itself. Modules may use any other number.</summary>
    internal static class BuffId
    {
        /// <summary>Run speed +Strength (0.3 = +30%). Applied in movement on every machine.</summary>
        internal const int RunSpeed = 1;
        /// <summary>Dash speed +Strength.</summary>
        internal const int DashSpeed = 2;
        /// <summary>No dash recharge while it lasts.</summary>
        internal const int NoDashCooldown = 3;
        /// <summary>Every skill level +Strength (may be negative).</summary>
        internal const int SkillBonus = 4;
        /// <summary>Can't move or dash while it lasts (sneezing, yawning, posing).</summary>
        internal const int Frozen = 5;
        /// <summary>Running and Dashing levels +Strength.</summary>
        internal const int MoveBonus = 6;
    }

    /// <summary>One chef in the kitchen, as the specials see it, on this machine.</summary>
    internal sealed class ChefState
    {
        public GameObject Chef;
        public PlayerControls Controls;
        public PlayerIDProvider Provider;
        public uint Id;
        /// <summary>Its player is on this machine.</summary>
        public bool Local;
        /// <summary>Its loadout (never null), refreshed twice a second.</summary>
        public int[] Loadout = SpecialCatalog.Empty();

        /// <summary>Active special: when it can be used again, and its full cooldown (SpecialRuntime.Now).</summary>
        public float CooldownUntil;
        public float CooldownTotal;
        public float ActiveUntil;
        internal bool EmoteWasDown;
        internal float PendingUntil;

        /// <summary>
        /// Where the player's own machine had the chef, and which way it faced, when the active was
        /// pressed. The host's view of a remote chef lags its owner's by a few frames, so an
        /// Execute that aims (vault, blink, the teammate you face...) should aim from these. On the
        /// host's own chefs they are simply the chef's position and facing at the press.
        /// </summary>
        public Vector3 RequestPosition;
        public Vector3 RequestForward = Vector3.forward;

        /// <summary>Set by Execute (with <see cref="Refuse"/>) when it returns false: shown to the player.</summary>
        internal string RefuseReason;

        /// <summary>
        /// Execute: say why it can't be used now (e.g. "NO COUNTER IN FRONT"); returns false so it
        /// reads <c>return state.Refuse("...");</c>. The player sees it over their skill bar.
        /// </summary>
        public bool Refuse(string reason)
        {
            RefuseReason = reason;
            return false;
        }

        /// <summary>
        /// Timers the skill bar shows on a special's icon (a passive's recharge, a charge
        /// building up...): special -> (until, total) in <see cref="SpecialRuntime.Now"/> seconds.
        /// </summary>
        internal readonly Dictionary<Special, Vector2> Timers = new Dictionary<Special, Vector2>();

        /// <summary>
        /// Shows a recharge sweep on this special's icon in the skill bar until <paramref name="until"/>
        /// (SpecialRuntime.Now), out of <paramref name="total"/> seconds. Local chefs only matter.
        /// </summary>
        public void SetTimer(Special special, float until, float total)
        {
            Timers[special] = new Vector2(until, Mathf.Max(0.01f, total));
        }

        /// <summary>A number on the special's icon (charges, stacks, stored items); 0 hides it.</summary>
        internal readonly Dictionary<Special, int> Counts = new Dictionary<Special, int>();

        public void SetCount(Special special, int count)
        {
            if (count == 0) Counts.Remove(special);
            else Counts[special] = count;
        }

        public readonly Dictionary<int, Buff> Buffs = new Dictionary<int, Buff>();

        /// <summary>Scratch space for modules, cleared with the level. Key by your special's name.</summary>
        public readonly Dictionary<string, object> Data = new Dictionary<string, object>();

        // Status chips over the skill bar (local chefs only), one per module key: text and the
        // special it belongs to (its colour and icon letters).
        private readonly SortedDictionary<string, string> _statuses = new SortedDictionary<string, string>();
        private readonly Dictionary<string, Special> _statusSpecials = new Dictionary<string, Special>();
        private string _status;

        /// <summary>
        /// Sets (or clears, with null/empty) this module's status chip over the skill bar. Each
        /// module uses its own key, so modules never overwrite each other.
        /// </summary>
        public void SetStatus(string module, string text)
        {
            SetStatus(module, text, Special.None);
        }

        /// <summary>The same, with the special the chip belongs to (shown in its colour, with its icon).</summary>
        public void SetStatus(string module, string text, Special special)
        {
            string current;
            bool had = _statuses.TryGetValue(module, out current);
            if (string.IsNullOrEmpty(text))
            {
                if (!had) return;
                _statuses.Remove(module);
                _statusSpecials.Remove(module);
            }
            else
            {
                _statusSpecials[module] = special;
                if (had && current == text) return;
                _statuses[module] = text;
            }
            _status = _statuses.Count > 0 ? string.Join("\n", new List<string>(_statuses.Values).ToArray()) : null;
        }

        /// <summary>Every module's chip: key, text (in key order).</summary>
        public IEnumerable<KeyValuePair<string, string>> Statuses
        {
            get { return _statuses; }
        }

        /// <summary>The special a module's chip belongs to (None when not given).</summary>
        public Special StatusSpecial(string module)
        {
            Special special;
            return _statusSpecials.TryGetValue(module, out special) ? special : Special.None;
        }

        /// <summary>Every module's line, joined (null when none).</summary>
        public string Status
        {
            get { return _status; }
        }

        public Special Active
        {
            get { return (Special)Loadout[(int)SpecialSlot.Active]; }
        }

        public bool Has(Special special)
        {
            for (int i = 0; i < Loadout.Length; i++)
                if (Loadout[i] == (int)special) return true;
            return false;
        }

        public TeamID Team
        {
            get { return Provider != null ? Provider.GetTeam() : TeamID.One; }
        }

        public Vector3 Position
        {
            get { return Chef != null ? Chef.transform.position : Vector3.zero; }
        }

        public Vector3 Forward
        {
            get { return Chef != null ? Chef.transform.forward : Vector3.forward; }
        }

        public T Get<T>(string key, T fallback)
        {
            object value;
            return Data.TryGetValue(key, out value) && value is T ? (T)value : fallback;
        }

        public void Set(string key, object value)
        {
            Data[key] = value;
        }
    }

    /// <summary>
    /// Runs the special skills during a level on every machine: keeps track of the chefs and their
    /// loadouts, turns a local player's EMOTE press into their active (done by the server, then
    /// confirmed to every machine), times cooldowns and buffs, feeds speed changes to movement
    /// (which every machine simulates, see MovementPatch), and gives the modules a tick.
    ///
    /// Time here is <see cref="Now"/>, which stops while the game is paused.
    /// </summary>
    internal static class SpecialRuntime
    {
        private const float RefreshSeconds = 0.5f;
        private const float PendingSeconds = 0.6f;
        // A client's view of its cooldown can run a little ahead of the host's.
        private const float CooldownSlack = 0.3f;

        private static readonly List<ChefState> s_chefs = new List<ChefState>();
        private static readonly Dictionary<int, ChefState> s_byObject = new Dictionary<int, ChefState>();
        private static readonly Dictionary<Special, ActiveHandler> s_actives = new Dictionary<Special, ActiveHandler>();
        private static readonly HashSet<string> s_failed = new HashSet<string>();
        private static float s_nextRefresh;
        private static bool s_inLevel;
        private static float s_now;
        private static float s_levelStartedAt;
        private static SpecialHud s_hud;

        /// <summary>Changes to run speed: multipliers (1 = none), every machine, every frame.</summary>
        internal static readonly List<Func<ChefState, float>> RunSpeedModifiers = new List<Func<ChefState, float>>();
        /// <summary>Changes to dash speed (so distance).</summary>
        internal static readonly List<Func<ChefState, float>> DashSpeedModifiers = new List<Func<ChefState, float>>();
        /// <summary>Changes to the dash recharge time (0 = none).</summary>
        internal static readonly List<Func<ChefState, float>> DashCooldownModifiers = new List<Func<ChefState, float>>();
        /// <summary>False from any of these: the chef can't start a dash now.</summary>
        internal static readonly List<Func<ChefState, bool>> DashAllowed = new List<Func<ChefState, bool>>();
        /// <summary>Extra skill levels (may be negative).</summary>
        internal static readonly List<Func<ChefState, Skill, int>> LevelBonusProviders = new List<Func<ChefState, Skill, int>>();

        /// <summary>Every frame during a level, every machine (check SpecialNet.IsServer for server work).</summary>
        internal static event Action<float> Tick;
        /// <summary>Every frame during a level, every machine, even while the game is paused (UI work).</summary>
        internal static event Action Frame;
        /// <summary>Once the skill screen has closed and the level is about to start.</summary>
        internal static event Action LevelStarted;
        /// <summary>The level was left (or a new one is loading).</summary>
        internal static event Action LevelEnded;
        /// <summary>Every machine: a chef used its active (after the server confirmed it).</summary>
        internal static event Action<ChefState, Special> Activated;

        /// <summary>Seconds of play: stops while the game is paused.</summary>
        internal static float Now
        {
            get { return s_now; }
        }

        /// <summary>Seconds since the level started (after the skill screen).</summary>
        internal static float LevelTime
        {
            get { return s_now - s_levelStartedAt; }
        }

        internal static bool InLevel
        {
            get { return s_inLevel; }
        }

        internal static IList<ChefState> Chefs
        {
            get { return s_chefs; }
        }

        internal static bool IsServer
        {
            get { return SpecialNet.IsServer; }
        }

        // --- setup ------------------------------------------------------------------------------

        /// <summary>Creates every module and lets it register (from Plugin.Awake).</summary>
        internal static void RegisterModules()
        {
            SpecialNet.Register(SpecialNet.Topic.ActivateRequest, OnActivateRequest);
            SpecialNet.Register(SpecialNet.Topic.Activated, OnActivated);
            SpecialNet.Register(SpecialNet.Topic.Denied, OnDenied);
            SpecialNet.Register(SpecialNet.Topic.Buff, OnBuff);
            SpecialFx.Register();

            int count = 0;
            foreach (Type type in typeof(SpecialRuntime).Assembly.GetTypes())
            {
                if (type.IsAbstract || type.IsInterface || !typeof(ISpecialModule).IsAssignableFrom(type)) continue;
                try
                {
                    ((ISpecialModule)Activator.CreateInstance(type)).Register();
                    count++;
                }
                catch (Exception error)
                {
                    Plugin.Log.LogError("Không bật được nhóm kỹ năng đặc biệt " + type.Name + ": " + error);
                }
            }
            var missing = new List<string>();
            foreach (Special special in Enum.GetValues(typeof(Special)))
            {
                SpecialInfo info = SpecialCatalog.Info(special);
                if (info != null && info.Kind == SpecialKind.Active && !s_actives.ContainsKey(special))
                    missing.Add(info.NameEn);
            }
            Plugin.Log.LogInfo("Kỹ năng đặc biệt: " + count + " nhóm" +
                (missing.Count > 0 ? "; chưa có cách dùng: " + string.Join(", ", missing.ToArray()) : ""));
        }

        internal static void RegisterActive(Special special, ActiveHandler handler)
        {
            s_actives[special] = handler;
        }

        /// <summary>A new level is loading (before the skill screen): forget the last one.</summary>
        internal static void NewLevel()
        {
            if (s_inLevel) Raise(LevelEnded, "LevelEnded");
            s_inLevel = false;
            s_chefs.Clear();
            s_byObject.Clear();
            if (s_hud != null) s_hud.Destroy();
            s_hud = null;
            SpecialFx.Clear();
        }

        /// <summary>The skill screen has closed: loadouts are set, the level starts.</summary>
        internal static void StartLevel()
        {
            s_inLevel = Plugin.Enabled.Value;
            if (!s_inLevel) return;
            s_levelStartedAt = s_now;
            s_nextRefresh = 0f;
            Refresh();
            Raise(LevelStarted, "LevelStarted");
            foreach (ChefState state in s_chefs)
                if (state.Local && state.Loadout[0] + state.Loadout[1] + state.Loadout[2] + state.Loadout[3] != 0)
                {
                    s_hud = new SpecialHud();
                    break;
                }
        }

        // --- lookups ----------------------------------------------------------------------------

        /// <summary>The state of the chef this object is (or is part of); null if none.</summary>
        internal static ChefState StateOf(GameObject chef)
        {
            if (chef == null || !s_inLevel) return null;
            ChefState state;
            if (s_byObject.TryGetValue(chef.GetInstanceID(), out state)) return state;
            PlayerControls controls = chef.GetComponentInParent<PlayerControls>();
            if (controls != null && s_byObject.TryGetValue(controls.gameObject.GetInstanceID(), out state)) return state;
            return null;
        }

        internal static ChefState StateById(uint id)
        {
            if (id == 0) return null;
            for (int i = 0; i < s_chefs.Count; i++)
                if (s_chefs[i].Id == id) return s_chefs[i];
            return null;
        }

        /// <summary>Does this chef have this special or quirk (cached, cheap)?</summary>
        internal static bool Has(GameObject chef, Special special)
        {
            ChefState state = StateOf(chef);
            return state != null && state.Has(special);
        }

        /// <summary>Does any chef in the kitchen have it (team-wide specials never stack)?</summary>
        internal static bool AnyHas(Special special)
        {
            if (!s_inLevel) return false;
            for (int i = 0; i < s_chefs.Count; i++)
                if (s_chefs[i].Has(special)) return true;
            return false;
        }

        /// <summary>Every chef with this special.</summary>
        internal static List<ChefState> With(Special special)
        {
            var list = new List<ChefState>();
            if (!s_inLevel) return list;
            for (int i = 0; i < s_chefs.Count; i++)
                if (s_chefs[i].Has(special)) list.Add(s_chefs[i]);
            return list;
        }

        /// <summary>Chefs on the same team as this one, not counting it.</summary>
        internal static List<ChefState> Teammates(ChefState chef)
        {
            var list = new List<ChefState>();
            if (chef == null) return list;
            for (int i = 0; i < s_chefs.Count; i++)
                if (s_chefs[i] != chef && s_chefs[i].Team == chef.Team) list.Add(s_chefs[i]);
            return list;
        }

        /// <summary>
        /// The teammate this chef is facing (within <paramref name="arc"/> degrees and
        /// <paramref name="range"/>), else the nearest one within range; null if none.
        /// </summary>
        internal static ChefState FacedTeammate(ChefState chef, float range, float arc)
        {
            ChefState faced = null, nearest = null;
            float facedDistance = float.MaxValue, nearestDistance = float.MaxValue;
            foreach (ChefState other in Teammates(chef))
            {
                Vector3 offset = other.Position - chef.Position;
                offset.y = 0f;
                float distance = offset.magnitude;
                if (distance > range || other.Chef == null) continue;
                if (distance < nearestDistance) { nearest = other; nearestDistance = distance; }
                if (distance > 0.01f && Vector3.Angle(chef.Forward, offset) <= arc && distance < facedDistance)
                {
                    faced = other;
                    facedDistance = distance;
                }
            }
            return faced ?? nearest;
        }

        // --- buffs ------------------------------------------------------------------------------

        /// <summary>
        /// Starts a buff on this machine only. Use <see cref="BroadcastBuff"/> for anything that
        /// changes movement or skills, which every machine must agree on.
        /// </summary>
        internal static void AddBuffLocal(ChefState target, int buff, float duration, float strength, ChefState source)
        {
            if (target == null) return;
            // Lone Wolf: nothing from a teammate.
            if (source != null && source != target && source.Team == target.Team && target.Has(Special.LoneWolf)) return;
            Buff current;
            float until = s_now + duration;
            if (target.Buffs.TryGetValue(buff, out current) && current.Until > s_now)
            {
                // Refreshing keeps the longer time and the stronger effect.
                if (Mathf.Abs(current.Strength) > Mathf.Abs(strength)) strength = current.Strength;
                if (current.Until > until) until = current.Until;
            }
            target.Buffs[buff] = new Buff { Until = until, Strength = strength, Source = source != null ? source.Id : 0 };
        }

        /// <summary>Starts a buff on every machine (from any machine; it goes through the host).</summary>
        internal static void BroadcastBuff(ChefState target, int buff, float duration, float strength, ChefState source)
        {
            if (target == null) return;
            if (target.Id == 0)
            {
                AddBuffLocal(target, buff, duration, strength, source);
                return;
            }
            SpecialNet.SendToAll(new SpecialMessage
            {
                Topic = SpecialNet.Topic.Buff, Chef = target.Id, I = buff, A = duration, B = strength,
                D = source != null ? source.Id : 0
            });
        }

        internal static bool HasBuff(ChefState chef, int buff)
        {
            Buff value;
            return chef != null && chef.Buffs.TryGetValue(buff, out value) && value.Until > s_now;
        }

        /// <summary>The strength of an active buff, or 0.</summary>
        internal static float BuffStrength(ChefState chef, int buff)
        {
            Buff value;
            return chef != null && chef.Buffs.TryGetValue(buff, out value) && value.Until > s_now ? value.Strength : 0f;
        }

        internal static void EndBuff(ChefState chef, int buff)
        {
            if (chef != null) chef.Buffs.Remove(buff);
        }

        private static void OnBuff(SpecialMessage message)
        {
            ChefState target = StateById(message.Chef);
            AddBuffLocal(target, message.I, message.A, message.B, StateById((uint)message.D));
        }

        // --- what movement and skills read ------------------------------------------------------

        internal static float RunMultiplier(GameObject chef)
        {
            ChefState state = StateOf(chef);
            if (state == null) return 1f;
            if (HasBuff(state, BuffId.Frozen)) return 0f;
            float value = 1f + BuffStrength(state, BuffId.RunSpeed);
            value *= Product(RunSpeedModifiers, state, "RunSpeed");
            return Mathf.Clamp(value, 0f, 4f);
        }

        internal static float DashMultiplier(GameObject chef)
        {
            ChefState state = StateOf(chef);
            if (state == null) return 1f;
            float value = 1f + BuffStrength(state, BuffId.DashSpeed);
            value *= Product(DashSpeedModifiers, state, "DashSpeed");
            return Mathf.Clamp(value, 0.1f, 4f);
        }

        internal static float DashCooldownMultiplier(GameObject chef)
        {
            ChefState state = StateOf(chef);
            if (state == null) return 1f;
            if (HasBuff(state, BuffId.NoDashCooldown)) return 0f;
            return Mathf.Max(0f, Product(DashCooldownModifiers, state, "DashCooldown"));
        }

        internal static bool CanDash(GameObject chef)
        {
            ChefState state = StateOf(chef);
            if (state == null) return true;
            if (HasBuff(state, BuffId.Frozen)) return false;
            for (int i = 0; i < DashAllowed.Count; i++)
            {
                try
                {
                    if (!DashAllowed[i](state)) return false;
                }
                catch (Exception error)
                {
                    Fail("DashAllowed", error);
                }
            }
            return true;
        }

        /// <summary>Extra levels for a skill from buffs and specials (0 outside a level).</summary>
        internal static int LevelBonus(GameObject chef, Skill skill)
        {
            if (!s_inLevel) return 0;
            ChefState state = StateOf(chef);
            if (state == null) return 0;
            int bonus = Mathf.RoundToInt(BuffStrength(state, BuffId.SkillBonus));
            if (skill == Skill.Running || skill == Skill.Dashing)
                bonus += Mathf.RoundToInt(BuffStrength(state, BuffId.MoveBonus));
            for (int i = 0; i < LevelBonusProviders.Count; i++)
            {
                try
                {
                    bonus += LevelBonusProviders[i](state, skill);
                }
                catch (Exception error)
                {
                    Fail("LevelBonus", error);
                }
            }
            return bonus;
        }

        private static float Product(List<Func<ChefState, float>> list, ChefState state, string what)
        {
            float value = 1f;
            for (int i = 0; i < list.Count; i++)
            {
                try
                {
                    value *= list[i](state);
                }
                catch (Exception error)
                {
                    Fail(what, error);
                }
            }
            return value;
        }

        // --- every frame ------------------------------------------------------------------------

        /// <summary>Called every frame by the plugin.</summary>
        internal static void Update()
        {
            if (!s_inLevel) return;
            if (!TimeManager.IsPaused(TimeManager.PauseLayer.Main)) s_now += Time.deltaTime;

            if (Time.unscaledTime >= s_nextRefresh) Refresh();
            if (!s_inLevel) return;

            for (int i = s_chefs.Count - 1; i >= 0; i--)
            {
                ChefState state = s_chefs[i];
                if (state.Chef == null)
                {
                    s_chefs.RemoveAt(i);
                    continue;
                }
                if (state.Local) ReadActivation(state);
            }

            if (!TimeManager.IsPaused(TimeManager.PauseLayer.Main)) Raise(Tick, Time.deltaTime, "Tick");
            if (s_hud != null) s_hud.Update(s_chefs);
            if (Frame != null)
            {
                try
                {
                    Frame();
                }
                catch (Exception error)
                {
                    Fail("Frame", error);
                }
            }
            try
            {
                SpecialFx.Update();
            }
            catch (Exception error)
            {
                Fail("Fx", error);
            }
        }

        /// <summary>Finds the chefs (new ones, gone ones) and refreshes their loadouts.</summary>
        private static void Refresh()
        {
            s_nextRefresh = Time.unscaledTime + RefreshSeconds;
            PlayerControls[] all = Object.FindObjectsOfType<PlayerControls>();
            if (all.Length == 0 && s_chefs.Count > 0)
            {
                // The kitchen is gone: the level was left.
                NewLevel();
                return;
            }
            // Online with someone who lacks this version of Chef Skills: no specials for anybody.
            // Their machine couldn't follow them (no channel, no patches), so they'd only drift apart.
            bool allowed = SkillSync.PlayersWithoutMod().Count == 0;
            var seen = new HashSet<int>();
            foreach (PlayerControls controls in all)
            {
                GameObject chef = controls.gameObject;
                int key = chef.GetInstanceID();
                seen.Add(key);
                ChefState state;
                if (!s_byObject.TryGetValue(key, out state))
                {
                    state = new ChefState { Chef = chef, Controls = controls, Provider = chef.GetComponent<PlayerIDProvider>() };
                    s_byObject[key] = state;
                    s_chefs.Add(state);
                }
                if (state.Id == 0) state.Id = SkillManager.EntityId(chef);
                state.Local = state.Provider != null && state.Provider.IsLocallyControlled();
                int[] loadout = allowed ? Plugin.Skills.SpecialsOf(chef) : null;
                state.Loadout = loadout != null ? (int[])loadout.Clone() : SpecialCatalog.Empty();
            }
            for (int i = s_chefs.Count - 1; i >= 0; i--)
            {
                if (seen.Contains(s_chefs[i].Chef != null ? s_chefs[i].Chef.GetInstanceID() : 0)) continue;
                if (s_chefs[i].Chef != null) s_byObject.Remove(s_chefs[i].Chef.GetInstanceID());
                s_chefs.RemoveAt(i);
            }
        }

        // --- actives ----------------------------------------------------------------------------

        /// <summary>A local player pressed EMOTE: use their active.</summary>
        private static void ReadActivation(ChefState state)
        {
            PlayerControls controls = state.Controls;
            bool down = SpecialInput.IsDown(state);
            bool pressed = down && !state.EmoteWasDown;
            state.EmoteWasDown = down;
            if (!pressed || state.Active == Special.None) return;
            if (!controls.enabled || controls.IsSuppressed() || HasBuff(state, BuffId.Frozen) || SpecialHud.GameMenuOpen()) return;
            if (s_now < state.CooldownUntil || s_now < state.PendingUntil)
            {
                SpecialHud.Deny(state, s_now < state.PendingUntil ? null : Lang.Both("NOT READY", "CHƯA HỒI CHIÊU XONG"));
                return;
            }

            state.RequestPosition = state.Position;
            state.RequestForward = state.Forward;
            if (IsServer)
            {
                ServerActivate(state, state.Active);
                return;
            }
            state.PendingUntil = s_now + PendingSeconds;
            SpecialNet.SendToHost(new SpecialMessage
            {
                Topic = SpecialNet.Topic.ActivateRequest, Chef = state.Id, I = (int)state.Active,
                Vector1 = state.Position, Vector2 = state.Forward
            });
            ActiveHandler handler;
            if (s_actives.TryGetValue(state.Active, out handler) && handler.Predict != null)
            {
                try
                {
                    handler.Predict(state);
                }
                catch (Exception error)
                {
                    Fail(state.Active + ".Predict", error);
                }
            }
        }

        private static void OnActivateRequest(SpecialMessage message)
        {
            ChefState state = StateById(message.Chef);
            if (state == null) return;
            if ((int)state.Active != message.I)
            {
                // The loadouts disagree (one machine hasn't refreshed yet): say so rather than
                // leaving the player's press hanging.
                SpecialNet.SendToAll(new SpecialMessage { Topic = SpecialNet.Topic.Denied, Chef = state.Id, I = message.I, Text = Lang.Both("NOT SYNCED YET", "CHƯA ĐỒNG BỘ XONG") });
                return;
            }
            state.RequestPosition = message.Vector1;
            state.RequestForward = message.Vector2.sqrMagnitude > 0.01f ? message.Vector2 : state.Forward;
            ServerActivate(state, state.Active);
        }

        /// <summary>Server: use a chef's active if it's ready and has something to work on.</summary>
        internal static void ServerActivate(ChefState state, Special special)
        {
            ActiveHandler handler;
            bool ok = false;
            state.RefuseReason = null;
            if (s_now + CooldownSlack < state.CooldownUntil) state.RefuseReason = Lang.Both("NOT READY", "CHƯA HỒI CHIÊU XONG");
            else if (!s_actives.TryGetValue(special, out handler)) state.RefuseReason = Lang.Both("NOT AVAILABLE", "KHÔNG DÙNG ĐƯỢC");
            else
            {
                try
                {
                    ok = handler.Execute(state);
                }
                catch (Exception error)
                {
                    Fail(special + ".Execute", error);
                    state.RefuseReason = Lang.Both("ERROR (SEE LOG)", "LỖI (XEM LOG)");
                }
            }
            if (!ok)
            {
                string reason = state.RefuseReason ?? Lang.Both("NOTHING TO USE IT ON", "KHÔNG CÓ GÌ ĐỂ DÙNG");
                Plugin.Log.LogInfo("Kỹ năng " + SpecialCatalog.EnglishNameOf(special) + " không dùng được: " + reason.Split(Lang.Split)[0]);
                SpecialNet.SendToAll(new SpecialMessage { Topic = SpecialNet.Topic.Denied, Chef = state.Id, I = (int)special, Text = reason });
                return;
            }
            SpecialInfo info = SpecialCatalog.Info(special);
            float cooldown = info != null ? info.Cooldown : 10f;
            float duration = s_actives[special].Duration;
            SpecialNet.SendToAll(new SpecialMessage
            {
                Topic = SpecialNet.Topic.Activated, Chef = state.Id, I = (int)special, A = cooldown, B = duration
            });
        }

        private static void OnActivated(SpecialMessage message)
        {
            ChefState state = StateById(message.Chef);
            if (state == null) return;
            Special special = (Special)message.I;
            state.PendingUntil = 0f;
            state.CooldownUntil = s_now + message.A;
            state.CooldownTotal = message.A;
            state.ActiveUntil = s_now + message.B;
            ActiveHandler handler;
            if (s_actives.TryGetValue(special, out handler) && handler.OnActivated != null)
            {
                try
                {
                    handler.OnActivated(state);
                }
                catch (Exception error)
                {
                    Fail(special + ".OnActivated", error);
                }
            }
            if (Activated != null)
            {
                foreach (Action<ChefState, Special> listener in Activated.GetInvocationList())
                {
                    try
                    {
                        listener(state, special);
                    }
                    catch (Exception error)
                    {
                        Fail("Activated", error);
                    }
                }
            }
            if (state.Local) SpecialHud.Used(state);
            // Everyone sees that a special went off, and whose.
            SpecialFx.Burst(state.Position, SpecialFx.ColourOf(special), 1.6f, 0.55f);
            SpecialFx.Text(state.Chef, SpecialCatalog.NameOf(special) + "!", SpecialFx.ColourOf(special));
        }

        private static void OnDenied(SpecialMessage message)
        {
            ChefState state = StateById(message.Chef);
            if (state == null) return;
            state.PendingUntil = 0f;
            if (state.Local) SpecialHud.Deny(state, message.Text);
        }

        /// <summary>Shortens a chef's active cooldown by <paramref name="seconds"/> (every machine).</summary>
        internal static void ShortenCooldownLocal(ChefState state, float seconds)
        {
            if (state != null) state.CooldownUntil = Mathf.Max(s_now, state.CooldownUntil - seconds);
        }

        // --- safety -----------------------------------------------------------------------------

        private static void Raise(Action action, string what)
        {
            if (action == null) return;
            foreach (Action listener in action.GetInvocationList())
            {
                try
                {
                    listener();
                }
                catch (Exception error)
                {
                    Fail(what, error);
                }
            }
        }

        private static void Raise(Action<float> action, float value, string what)
        {
            if (action == null) return;
            foreach (Action<float> listener in action.GetInvocationList())
            {
                try
                {
                    listener(value);
                }
                catch (Exception error)
                {
                    Fail(what + " " + listener.Method.DeclaringType.Name, error);
                }
            }
        }

        /// <summary>Logs a module error once (per place), so one broken special can't flood the log.</summary>
        internal static void Fail(string where, Exception error)
        {
            if (s_failed.Add(where)) Plugin.Log.LogWarning("Lỗi kỹ năng đặc biệt (" + where + "): " + error);
        }
    }
}
