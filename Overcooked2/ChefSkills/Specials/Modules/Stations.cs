using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Steamworks;
using Team17.Online.Multiplayer.Messaging;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ChefSkills.Specials
{
    /// <summary>
    /// 3. Prep stations (37-47), plus INSPIRING (73), ZEN (83), LUCKY CHARM (88), HEX (99) and the
    /// GERMAPHOBE (106) and TUNNEL VISION (111) quirks.
    ///
    /// The host decides everything that changes items: extra chop strokes, finished items, washed
    /// plates, spawned plates and pans. Chopping progress bars are drawn by every machine from
    /// the strokes it sees itself (nothing about strokes goes over the game's network), so every
    /// extra stroke the host makes is also sent as a relative "N more strokes on item X" message
    /// that each machine applies to its own bar; relative counts don't race with the strokes the
    /// machine is animating. Finished items need nothing: the host replaces the item and the
    /// clients follow. Sinks: the host's timer is pushed with the game's own sink messages, or
    /// as a relative bonus while someone is washing.
    ///
    /// What players see: the stroke messages also draw the effect on every machine (a ring on a
    /// board that chops by itself or echoes, "LUCKY x2", a dash-chop burst); host-only effects
    /// (a lucky finish, ZEN, a plate arriving) go out with SpecialFx's ...All calls; auras that
    /// follow state every machine already has (ZEN charged, HEXED, INSPIRING) are drawn locally.
    /// </summary>
    internal sealed class StationsModule : ISpecialModule
    {
        // --- tuning -------------------------------------------------------------------------------
        private const float MultitaskRate = 0.4f;
        // MULTITASK: boards and sinks working by themselves at once, per chef (the oldest goes).
        private const int MultitaskJobs = 2;
        private const float EchoChance = 0.6f;
        private const float EchoRange = 6f;
        private const float LuckyKnifeChance = 0.10f;
        private const float LuckyCharmChance = 0.25f;
        // LUCKY CHARM on a sink: washing has no strokes, so every this many seconds of scrubbing
        // counts as one stroke that may count double.
        private const float ScrubSeconds = 0.5f;
        private const float PrepCookSeconds = 15f;
        private const float InspiringRange = 3.5f;
        private const float InspiringPercent = 25f;
        private const float ZenStillSeconds = 1.5f;
        private const float ZenStillSpeed = 0.5f;
        private const int HexStrokes = 10;
        // HEX on a sink: this much of the hexed chef's washing does nothing.
        private const float HexWashSeconds = 3f;
        private const float HexSeconds = 30f;
        private const float HexRange = 5f;
        private const float HexArc = 70f;
        private const int DashStrokes = 3;
        // DASH-CHOP: a board takes a dash's strokes at most this often.
        private const float DashBoardSeconds = 2f;
        // DISHWASHER: every this many dishes a chef hands in, the plate comes back clean.
        private const int DishwasherEvery = 2;
        // BUTLER: the dirty plate reaches the sink this many times sooner.
        private const float ButlerSpeed = 2f;
        // TUNNEL VISION: sinks this close together are one station (a double sink).
        private const float TunnelSinkJoin = 1.5f;
        private const float BoardReach = 2f;
        private const float BoardTouch = 1.2f;
        private const float DashReach = 1.8f;
        private const float DashArc = 100f;
        private const float SinkReach = 2.2f;
        private const float FaceArc = 80f;
        private const float BlockedTextSeconds = 5f;
        private const int ModeChop = 1, ModeWash = 2;
        private const string Key = "Stations";

        // Network topics: (Special * 10 + n).
        private static readonly ushort TopicMultitask = T(Special.Multitask, 1);
        private static readonly ushort TopicEcho = T(Special.EchoKnife, 1);
        private static readonly ushort TopicDash = T(Special.DashChop, 1);
        private static readonly ushort TopicCharmChop = T(Special.LuckyCharm, 1);
        private static readonly ushort TopicCharmWash = T(Special.LuckyCharm, 2);
        private static readonly ushort TopicPrep = T(Special.PrepCook, 1);
        private static readonly ushort TopicZen = T(Special.Zen, 1);
        private static readonly ushort TopicHex = T(Special.Hex, 1);
        private static readonly ushort TopicTunnel = T(Special.TunnelVision, 1);

        // Private game state reached by name (validated by the checker).
        private static readonly FieldInfo BoardItem = AccessTools.Field(typeof(ServerWorkstation), "m_item");
        private static readonly FieldInfo BoardStation = AccessTools.Field(typeof(ServerWorkstation), "m_attachStation");
        private static readonly FieldInfo BoardInteracters = AccessTools.Field(typeof(ServerWorkstation), "m_interacters");
        private static readonly FieldInfo ClientBoardItem = AccessTools.Field(typeof(ClientWorkstation), "m_item");
        private static readonly MethodInfo WorkableSync = AccessTools.Method(typeof(ServerWorkableItem), "SynchroniseClientState");
        private static readonly FieldInfo SinkTimer = AccessTools.Field(typeof(ServerWashingStation), "m_cleaningTimer");
        private static readonly FieldInfo SinkPlates = AccessTools.Field(typeof(ServerWashingStation), "m_plateCount");
        private static readonly FieldInfo SinkInteractor = AccessTools.Field(typeof(ServerWashingStation), "m_interactor");
        private static readonly FieldInfo SinkInteractable = AccessTools.Field(typeof(ServerWashingStation), "m_interactable");
        private static readonly FieldInfo SinkConfig = AccessTools.Field(typeof(ServerWashingStation), "m_washingStation");
        private static readonly FieldInfo SinkRack = AccessTools.Field(typeof(ServerWashingStation), "m_plateReturnStation");
        private static readonly MethodInfo SinkCleaned = AccessTools.Method(typeof(ServerWashingStation), "SendCleanedPlate");
        private static readonly MethodInfo SinkSync = AccessTools.Method(typeof(ServerWashingStation), "SynchroniseInteractionState");
        private static readonly MethodInfo SinkAddPlates = AccessTools.Method(typeof(ServerWashingStation), "AddPlates");
        private static readonly FieldInfo Interacters = AccessTools.Field(typeof(ServerInteractable), "m_interacters");
        private static readonly FieldInfo ClientSinkTimer = AccessTools.Field(typeof(ClientWashingStation), "m_cleaningTimer");
        private static readonly FieldInfo ClientSinkUi = AccessTools.Field(typeof(ClientWashingStation), "m_progressUI");
        private static readonly FieldInfo ClientSinkConfig = AccessTools.Field(typeof(ClientWashingStation), "m_washingStation");
        private static readonly FieldInfo ClientSinkPlates = AccessTools.Field(typeof(ClientWashingStation), "m_plateCount");
        private static readonly FieldInfo ClientSinkWashing = AccessTools.Field(typeof(ClientWashingStation), "m_isWashing");
        private static readonly FieldInfo ClientSinkInteractable = AccessTools.Field(typeof(ClientWashingStation), "m_interactable");
        private static readonly FieldInfo ClientInteracters = AccessTools.Field(typeof(ClientInteractable), "m_interacters");
        private static readonly FieldInfo PlateOnPass = AccessTools.Field(typeof(ServerPlateStation), "m_plate");
        private static readonly FieldInfo RackStack = AccessTools.Field(typeof(ServerPlateReturnStation), "m_stack");
        private static readonly FieldInfo RackConfig = AccessTools.Field(typeof(ServerPlateReturnStation), "m_returnStation");
        private static readonly FieldInfo StackItems = AccessTools.Field(typeof(ServerPlateStackBase), "m_stack");
        private static readonly FieldInfo PendingPlates = AccessTools.Field(typeof(PlateReturnController), "m_platesToReturn");

        // PlateReturnController's pending-plate entries are a private class in a FastList<T>;
        // their members are looked up from the runtime types on first use.
        private static Type s_listType, s_entryType;
        private static PropertyInfo s_listCount;
        private static FieldInfo s_listItems, s_entryStation, s_entryTimer;
        private static MethodInfo s_listRemoveAt, s_listAdd;

        // --- per-chef state (in ChefState.Data, so it goes with the level) ------------------------

        private sealed class Mine
        {
            public Vector3 LastPosition;
            public bool HasPosition;
            public float Still;
            public float LastWork = -100f;
            public bool ZenCharged;
            public int HexServer, HexClient;
            // HEX: seconds of washing still swallowed, on the host's sinks and on this machine's bars.
            public float HexWashServer, HexWashClient;
            public float HexUntil;
            public ServerWorkstation EchoCurrent, EchoOther;
            public float PrepReadyAt;
            // TUNNEL VISION: the station this chef's player locked to (ModeChop = a board, ModeWash
            // = a sink), by its entity ID and where it stands; the same on every machine.
            public int TunnelMode;
            public uint TunnelStation;
            public Vector3 TunnelAt;
            public int Dishes;
            public ClientPlayerControlsImpl_Default Impl;
            public float ChopInterval;
            public float BlockedTextAt = -100f;
            // Host: MULTITASK's boards and sinks, oldest first.
            public readonly List<Component> Jobs = new List<Component>();
            // Host: the SPARE PAN copy this chef has out (entity ID; 0 = none).
            public uint Spare;
        }

        private sealed class BoardJob
        {
            public ChefState Chef;
            public ServerWorkableItem Item;
            public float Timer;
        }

        private sealed class ButlerPlate
        {
            public ServerWashingStation Sink;
            public float Timer;
            public object Entry;
            public PlateReturnController Controller;
        }

        /// <summary>A DISHWASHER plate on its way to a drying rack: shown as "CLEAN!" when it lands.</summary>
        private sealed class CleanPlate
        {
            public object Entry;
            public PlateReturnController Controller;
            public ServerPlateReturnStation Rack;
            public float GiveUpAt;
        }

        // --- per-level state ----------------------------------------------------------------------

        private static readonly Dictionary<ServerWorkstation, BoardJob> s_multitask = new Dictionary<ServerWorkstation, BoardJob>();
        // Server: when each MULTITASK job was last touched (a counter), so the cap per player can
        // drop the oldest across a solo player's two chefs.
        private static readonly Dictionary<Component, int> s_jobTouched = new Dictionary<Component, int>();
        private static int s_jobSerial;
        private static readonly List<ServerWorkstation> s_deadJobs = new List<ServerWorkstation>();
        private static readonly List<ButlerPlate> s_butler = new List<ButlerPlate>();
        private static readonly List<CleanPlate> s_clean = new List<CleanPlate>();
        private static readonly Dictionary<ServerWashingStation, float> s_scrub = new Dictionary<ServerWashingStation, float>();
        private static readonly Dictionary<ServerWashingStation, float> s_sinkSyncAt = new Dictionary<ServerWashingStation, float>();
        private static readonly Dictionary<int, GameObject> s_templates = new Dictionary<int, GameObject>();
        // DASH-CHOP: when each board can take a dash's strokes again.
        private static readonly Dictionary<ServerWorkstation, float> s_dashReadyAt = new Dictionary<ServerWorkstation, float>();
        // PLATE CONJURER: the conjured plates still in play (entity IDs); each is used once.
        private static readonly HashSet<uint> s_conjured = new HashSet<uint>();
        // SPARE PAN: copies just emptied onto a plate, removed on the next tick.
        private static readonly List<uint> s_spentSpares = new List<uint>();
        private static ServerWorkstation[] s_boards = new ServerWorkstation[0];
        private static ServerWashingStation[] s_sinks = new ServerWashingStation[0];
        private static float s_nextScan, s_nextFlags, s_nextStatus, s_nextAura;
        private static GameObject s_templateHolder;

        // Read by the per-frame patches, refreshed twice a second.
        internal static bool Blockers;
        internal static bool AnyHex;
        internal static bool AnySleepy;
        internal static bool AnyDelivery;

        /// <summary>The delivery patch has something to do: DISHWASHER, BUTLER or a conjured plate.</summary>
        internal static bool DeliveryHooked
        {
            get { return AnyDelivery || s_conjured.Count > 0; }
        }

        /// <summary>The sink patches have something to do: a dozing or hexed chef may be washing.</summary>
        internal static bool WashBlockers
        {
            get { return AnySleepy || AnyHex; }
        }

        /// <summary>The SPARE PAN patches: some copy is out.</summary>
        internal static bool AnySpare;

        // --- setup --------------------------------------------------------------------------------

        public void Register()
        {
            SpecialRuntime.Tick += OnTick;
            SpecialRuntime.LevelStarted += Reset;
            SpecialRuntime.LevelEnded += Reset;
            SpecialRuntime.LevelEnded += ForgetTemplates;
            SpecialRuntime.LevelBonusProviders.Add(InspiringBonus);

            SpecialRuntime.RegisterActive(Special.PowerChop, new ActiveHandler { Execute = PowerChop, Targets = BoardTargets });
            SpecialRuntime.RegisterActive(Special.WashBurst, new ActiveHandler { Execute = WashBurst, Targets = SinkTargets });
            SpecialRuntime.RegisterActive(Special.PlateConjurer, new ActiveHandler { Execute = PlateConjurer });
            SpecialRuntime.RegisterActive(Special.SparePan, new ActiveHandler { Execute = SparePan, Targets = PanTargets });
            SpecialRuntime.RegisterActive(Special.Hex, new ActiveHandler { Execute = Hex, Targets = HexTargets });

            SpecialNet.Register(TopicMultitask, OnStrokes);
            SpecialNet.Register(TopicEcho, OnStrokes);
            SpecialNet.Register(TopicDash, OnStrokes);
            SpecialNet.Register(TopicCharmChop, OnStrokes);
            SpecialNet.Register(TopicCharmWash, OnWashBonus);
            SpecialNet.Register(TopicPrep, OnPrep);
            SpecialNet.Register(TopicZen, OnZen);
            SpecialNet.Register(TopicHex, OnHex);
            SpecialNet.Register(TopicTunnel, OnTunnel);
        }

        private static ushort T(Special special, int n)
        {
            return (ushort)((int)special * 10 + n);
        }

        private static void Reset()
        {
            s_multitask.Clear();
            s_jobTouched.Clear();
            s_jobSerial = 0;
            s_butler.Clear();
            s_clean.Clear();
            s_scrub.Clear();
            s_sinkSyncAt.Clear();
            s_dashReadyAt.Clear();
            s_conjured.Clear();
            s_spentSpares.Clear();
            s_boards = new ServerWorkstation[0];
            s_sinks = new ServerWashingStation[0];
            s_nextScan = s_nextFlags = s_nextStatus = s_nextAura = 0f;
            Blockers = AnyHex = AnySleepy = AnyDelivery = AnySpare = false;
        }

        /// <summary>
        /// SPARE PAN templates live as long as their pans (the level's scene), not just from the
        /// level start: a client may make one before its own skill screen has closed.
        /// </summary>
        private static void ForgetTemplates()
        {
            s_templates.Clear();
            if (s_templateHolder != null) Object.Destroy(s_templateHolder);
            s_templateHolder = null;
        }

        private static Mine Of(ChefState chef)
        {
            object value;
            if (chef.Data.TryGetValue(Key, out value))
            {
                Mine existing = value as Mine;
                if (existing != null) return existing;
            }
            var mine = new Mine();
            chef.Data[Key] = mine;
            return mine;
        }

        private static float Now
        {
            get { return SpecialRuntime.Now; }
        }

        /// <summary>Online, but some machine can't read our channel (it lacks this mod version).</summary>
        private static bool PartialLobby
        {
            get
            {
                CSteamID lobby;
                bool host;
                return SkillSync.TryGetLobby(out lobby, out host) && !SkillSync.EveryoneHasMod;
            }
        }

        private static uint EntityIdOf(GameObject obj)
        {
            if (obj == null) return 0;
            EntitySerialisationEntry entry = EntitySerialisationRegistry.GetEntry(obj);
            return entry != null ? entry.m_Header.m_uEntityID : 0u;
        }

        private static GameObject EntityById(int id)
        {
            if (id <= 0) return null;
            EntitySerialisationEntry entry = EntitySerialisationRegistry.GetEntry((uint)id);
            return entry != null ? entry.m_GameObject : null;
        }

        private static Color ColourOf(Special special)
        {
            return SpecialFx.ColourOf(special);
        }

        // --- every frame --------------------------------------------------------------------------

        private static void OnTick(float dt)
        {
            if (!SpecialRuntime.InLevel) return;
            if (Now >= s_nextFlags)
            {
                s_nextFlags = Now + 0.5f;
                Blockers = SpecialRuntime.AnyHas(Special.Germaphobe) || SpecialRuntime.AnyHas(Special.TunnelVision);
                AnyDelivery = SpecialRuntime.AnyHas(Special.Dishwasher) || SpecialRuntime.AnyHas(Special.Butler);
                AnySleepy = SpecialRuntime.AnyHas(Special.Sleepy);
            }
            if (Now >= s_nextStatus)
            {
                s_nextStatus = Now + 0.25f;
                UpdateStatus();
            }
            if (Now >= s_nextAura)
            {
                s_nextAura = Now + 0.25f;
                DrawAuras();
            }
            if (!SpecialNet.IsServer) return;

            bool multitask = SpecialRuntime.AnyHas(Special.Multitask);
            bool dash = SpecialRuntime.AnyHas(Special.DashChop);
            bool zen = SpecialRuntime.AnyHas(Special.Zen);
            bool charm = SpecialRuntime.AnyHas(Special.LuckyCharm);
            if ((multitask || dash || zen || charm) && Now >= s_nextScan)
            {
                // Boards and sinks never come and go during a level; a slow refresh is plenty.
                s_nextScan = Now + 3f;
                s_boards = Object.FindObjectsOfType<ServerWorkstation>();
                s_sinks = Object.FindObjectsOfType<ServerWashingStation>();
            }
            if (zen) ZenTick(dt);
            if (multitask) PruneJobs();
            if (multitask && s_multitask.Count > 0) MultitaskBoards(dt);
            if (dash) DashChopTick();
            if (multitask || zen || charm) SinkTick(dt, multitask);
            if (s_butler.Count > 0) ButlerTick(dt);
            if (s_clean.Count > 0) CleanPlateTick();
            if (s_spentSpares.Count > 0) RemoveSpentSpares();
        }

        /// <summary>The text under a local chef's active: ZEN ready, HEXED, TUNNEL VISION's mode.</summary>
        private static void UpdateStatus()
        {
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState chef = chefs[i];
                if (!chef.Local) continue;
                Mine mine = Of(chef);
                // One chip each (SetStatus does nothing when a chip is unchanged).
                chef.SetStatus("Stations.Zen", chef.Has(Special.Zen) && mine.ZenCharged ? SpecialCatalog.NameOf(Special.Zen) + Lang.T(" READY", " SẴN SÀNG") : null, Special.Zen);
                chef.SetStatus("Stations.Hex", HexLeft(mine) > 0 ? Lang.T("HEXED x", "BỊ NGUYỀN x") + mine.HexClient : null, Special.Hex);
                chef.SetStatus("Stations.Tunnel", chef.Has(Special.TunnelVision) && mine.TunnelMode != 0 ? TunnelText(mine.TunnelMode) : null,
                    Special.TunnelVision);
            }
        }

        /// <summary>
        /// Every machine, four times a second: rings that follow state every machine knows. A
        /// charged ZEN chef, a HEXED chef, and every teammate an INSPIRING chef is lifting.
        /// </summary>
        private static void DrawAuras()
        {
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState chef = chefs[i];
                if (chef.Chef == null) continue;
                Mine mine = Of(chef);
                if (mine.ZenCharged && chef.Has(Special.Zen))
                    SpecialFx.Mark(chef.Chef, ColourOf(Special.Zen), 0.45f, null, 0.8f);
                if (HexLeft(mine) > 0)
                    SpecialFx.Mark(chef.Chef, ColourOf(Special.Hex), 0.45f, Lang.T("HEXED", "BỊ NGUYỀN"), 0.65f);
                if (!chef.Has(Special.Inspiring)) continue;
                for (int j = 0; j < chefs.Count; j++)
                {
                    ChefState mate = chefs[j];
                    if (mate == chef || mate.Chef == null || mate.Team != chef.Team || mate.Has(Special.LoneWolf)) continue;
                    if (SpecialRuntime.SamePlayer(mate, chef)) continue;
                    if (FlatDistance(mate.Position, chef.Position) > InspiringRange) continue;
                    SpecialFx.Mark(mate.Chef, ColourOf(Special.Inspiring), 0.45f, null, 0.55f);
                }
            }
        }

        // --- facing helpers -----------------------------------------------------------------------

        private static float FlatDistance(Vector3 a, Vector3 b)
        {
            Vector3 offset = a - b;
            offset.y = 0f;
            return offset.magnitude;
        }

        /// <summary>
        /// The target the active works on: the one the player chose (within a little of the chosen
        /// spot, and in reach whichever way the chef faces), or else one faced, as <see cref="Faces"/>.
        /// </summary>
        private static bool Reaches(ChefState state, Vector3 from, Vector3 forward, Vector3 target, float range, float arc, out float distance)
        {
            if (!state.HasAimPoint) return Faces(from, forward, target, range, arc, out distance);
            distance = FlatDistance(from, target);
            return distance <= range + AimSlack && state.AimDistance(target) <= AimPick;
        }

        // A chosen target counts within this much of where it was chosen, and a little out of reach.
        private const float AimPick = 0.9f;
        private const float AimSlack = 0.6f;

        // --- what the actives can be aimed at (the player's machine) -------------------------------

        /// <summary>Things within <paramref name="range"/> of the chef, the one faced first (else the nearest).</summary>
        private static List<AimTarget> Around<T>(ChefState state, float range, Func<T, bool> usable) where T : Component
        {
            var targets = new List<AimTarget>();
            Vector3 from = state.Position;
            foreach (T candidate in Object.FindObjectsOfType<T>())
            {
                if (candidate == null || !candidate.gameObject.activeInHierarchy) continue;
                if (FlatDistance(from, candidate.transform.position) > range || !usable(candidate)) continue;
                targets.Add(AimTarget.ForObject(candidate.gameObject));
            }
            return SpecialAim.FacedFirst(targets, from, state.Forward, FaceArc);
        }

        private static List<AimTarget> BoardTargets(ChefState state)
        {
            return Around<ClientWorkstation>(state, BoardReach, delegate(ClientWorkstation board)
            {
                ClientWorkableItem item = ClientBoardItem != null ? ClientBoardItem.GetValue(board) as ClientWorkableItem : null;
                return item != null && !item.HasFinished();
            });
        }

        private static List<AimTarget> SinkTargets(ChefState state)
        {
            return Around<ClientWashingStation>(state, SinkReach, delegate(ClientWashingStation sink)
            {
                return ClientSinkPlates == null || (int)ClientSinkPlates.GetValue(sink) > 0;
            });
        }

        private static List<AimTarget> PanTargets(ChefState state)
        {
            List<AimTarget> targets = Around<ClientCookingHandler>(state, BoardReach, delegate(ClientCookingHandler pan)
            {
                return EmptyLoose(pan.gameObject);
            });
            // Mixing bowls too (one that also cooks is already in).
            List<AimTarget> bowls = Around<ClientMixingHandler>(state, BoardReach, delegate(ClientMixingHandler bowl)
            {
                return bowl.GetComponent<ClientCookingHandler>() == null && EmptyLoose(bowl.gameObject);
            });
            if (bowls.Count == 0) return targets;
            targets.AddRange(bowls);
            return SpecialAim.FacedFirst(targets, state.Position, state.Forward, FaceArc);
        }

        /// <summary>A pan, pot or bowl nobody carries, with nothing in it (this machine's view).</summary>
        private static bool EmptyLoose(GameObject obj)
        {
            if (obj.GetComponent<PhysicalAttachment>() == null || obj.GetComponentInParent<PlayerControls>() != null) return false;
            ClientIngredientContainer contents = obj.GetComponent<ClientIngredientContainer>();
            return contents == null || !contents.HasContents();
        }

        private static List<AimTarget> HexTargets(ChefState state)
        {
            var targets = new List<AimTarget>();
            int faced = -1;
            float best = float.MaxValue;
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState other = chefs[i];
                if (other == state || other.Chef == null || FlatDistance(state.Position, other.Position) > HexRange) continue;
                if (other.Team == state.Team && other.Has(Special.LoneWolf)) continue;
                targets.Add(AimTarget.ForChef(other));
                // The one it picks anyway: faced, a rival before a teammate.
                float distance;
                if (!Faces(state.Position, state.Forward, other.Position, HexRange, HexArc, out distance)) continue;
                float score = distance + (other.Team == state.Team ? 10f : 0f);
                if (score < best)
                {
                    best = score;
                    faced = targets.Count - 1;
                }
            }
            if (faced >= 0) SpecialAim.MoveFirst(targets, faced);
            else SpecialAim.FacedFirst(targets, state.Position, state.Forward, HexArc);
            return targets;
        }

        private static bool Faces(Vector3 from, Vector3 forward, Vector3 target, float range, float arc, out float distance)
        {
            Vector3 offset = target - from;
            offset.y = 0f;
            distance = offset.magnitude;
            if (distance > range) return false;
            if (distance < 0.05f) return true;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f) return true;
            return Vector3.Angle(forward, offset) <= arc;
        }

        private static bool Faces(ChefState chef, Vector3 target, float range, float arc, out float distance)
        {
            return Faces(chef.Position, chef.Forward, target, range, arc, out distance);
        }

        /// <summary>Where an active aims from: the owner's own view of the chef at the press.</summary>
        private static Vector3 AimPosition(ChefState state)
        {
            return state.RequestPosition.sqrMagnitude > 1e-4f ? state.RequestPosition : state.Position;
        }

        private static Vector3 AimForward(ChefState state)
        {
            Vector3 forward = state.RequestForward;
            forward.y = 0f;
            return forward.sqrMagnitude > 1e-4f ? forward : state.Forward;
        }

        private static ServerWorkableItem ItemOn(ServerWorkstation board)
        {
            if (board == null || BoardItem == null) return null;
            ServerWorkableItem item = BoardItem.GetValue(board) as ServerWorkableItem;
            return item != null && !item.HasFinished() ? item : null;
        }

        private static bool Busy(ServerWorkstation board)
        {
            ICollection interacters = BoardInteracters != null ? BoardInteracters.GetValue(board) as ICollection : null;
            return interacters != null && interacters.Count > 0;
        }

        /// <summary>
        /// The loaded board (unfinished item) in front of this spot, nearest first; else one the
        /// chef is right next to, whichever way they face.
        /// </summary>
        private static ServerWorkstation FacedBoard(Vector3 from, Vector3 forward, IEnumerable<ServerWorkstation> boards, float range)
        {
            ServerWorkstation best = null, touching = null;
            float bestDistance = float.MaxValue, touchDistance = float.MaxValue;
            foreach (ServerWorkstation board in boards)
            {
                if (board == null || !board.gameObject.activeInHierarchy || ItemOn(board) == null) continue;
                float distance;
                bool faces = Faces(from, forward, board.transform.position, range, FaceArc, out distance);
                if (faces && distance < bestDistance)
                {
                    best = board;
                    bestDistance = distance;
                }
                if (distance <= BoardTouch && distance < touchDistance)
                {
                    touching = board;
                    touchDistance = distance;
                }
            }
            return best ?? touching;
        }

        // --- chopping -----------------------------------------------------------------------------

        /// <summary>Server: finishes the item on a board at once (the host replaces it everywhere).</summary>
        private static bool Finish(ServerWorkstation board, ServerWorkableItem item, ChefState chef)
        {
            ServerAttachStation station = BoardStation != null ? BoardStation.GetValue(board) as ServerAttachStation : null;
            if (station == null || item == null || item.HasFinished()) return false;
            // The (station, worker, progress) overload clamps to the last stage and swaps the item;
            // ChopPatch is on the other overload, so no skill multiplier is involved.
            item.DoWork(station, chef.Chef, 99);
            return true;
        }

        /// <summary>
        /// Server: <paramref name="strokes"/> more chop strokes by this chef (one for one: CHOPPING
        /// and its credit stay out of them, see ChopPatch), mirrored on every machine's progress
        /// bar, which also draws the effect (see <see cref="OnStrokes"/>). <paramref name="show"/>:
        /// seconds a ring stays.
        /// </summary>
        private static void ExtraStrokes(ServerWorkstation board, ServerWorkableItem item, ChefState chef, ushort topic,
            int strokes, float show)
        {
            ServerAttachStation station = BoardStation != null ? BoardStation.GetValue(board) as ServerAttachStation : null;
            if (station == null || item == null || item.HasFinished() || chef.Chef == null || strokes <= 0) return;
            uint id = EntityIdOf(item.gameObject);
            int done = 0;
            LevelState.RawStrokes++;
            try
            {
                for (int i = 0; i < strokes; i++)
                {
                    item.DoWork(station, chef.Chef);
                    done++;
                    if (item == null || item.HasFinished()) break;
                }
            }
            finally
            {
                LevelState.RawStrokes--;
            }
            if (id == 0) return;
            if (item == null || item.HasFinished())
            {
                // The clients follow the swap; only the effect goes out.
                SpecialNet.SendToAll(new SpecialMessage { Topic = topic, Chef = chef.Id, I = (int)id, A = 0f, B = show,
                    Vector2 = board.transform.position });
                return;
            }
            // The board's position goes in Vector2 (D..F): Vector1 would overwrite A and B.
            SpecialNet.SendToAll(new SpecialMessage { Topic = topic, Chef = chef.Id, I = (int)id, A = done, B = show,
                Vector2 = board.transform.position });
            // A machine without our channel only learns the host's absolute progress.
            if (PartialLobby && WorkableSync != null) WorkableSync.Invoke(item, null);
        }

        /// <summary>
        /// Every machine: the host made extra strokes; add them to this machine's bar (A = how
        /// many; 0 when they finished the item, which the host's swap shows) and show why.
        /// </summary>
        private static void OnStrokes(SpecialMessage message)
        {
            GameObject obj = EntityById(message.I);
            ClientWorkableItem item = obj != null ? obj.GetComponent<ClientWorkableItem>() : null;
            ClientAttachStation station = item != null ? item.GetComponentInParent<ClientAttachStation>() : null;
            ChefState chef = SpecialRuntime.StateById(message.Chef);
            GameObject worker = chef != null ? chef.Chef : null;
            int strokes = Mathf.RoundToInt(message.A);
            // One for one, the same way the host's strokes went past ChopPatch.
            LevelState.RawStrokes++;
            try
            {
                for (int i = 0; i < strokes && item != null; i++)
                {
                    if (item.HasFinished()) break;
                    item.DoWork(station, worker);
                }
            }
            finally
            {
                LevelState.RawStrokes--;
            }
            GameObject board = station != null ? station.gameObject : null;
            // A stroke that finished the item: the host has already swapped it for the chopped
            // one, so the old item may be gone or off the board. Find the board by where it is.
            if (board == null) board = BoardAt(message.Vector2);
            ShowStrokes(message, board != null ? board : obj);
        }

        /// <summary>The board standing at this spot on this machine (null if none within a metre).</summary>
        private static GameObject BoardAt(Vector3 at)
        {
            GameObject best = null;
            float bestDistance = 1f;
            foreach (ClientWorkstation candidate in Object.FindObjectsOfType<ClientWorkstation>())
            {
                float distance = Vector3.Distance(candidate.transform.position, at);
                if (distance >= bestDistance) continue;
                best = candidate.gameObject;
                bestDistance = distance;
            }
            return best;
        }

        /// <summary>This machine: the effect of the host's extra strokes on a board.</summary>
        private static void ShowStrokes(SpecialMessage message, GameObject board)
        {
            Vector3 at = board != null ? board.transform.position : message.Vector2;
            if (message.Topic == TopicMultitask)
            {
                if (board != null) SpecialFx.Mark(board, ColourOf(Special.Multitask), Mathf.Max(0.8f, message.B), null, 0f);
            }
            else if (message.Topic == TopicEcho)
            {
                if (board != null) SpecialFx.Mark(board, ColourOf(Special.EchoKnife), 0.9f, Lang.T("ECHO", "VỌNG"), 0f);
            }
            else if (message.Topic == TopicDash)
            {
                SpecialFx.Burst(at, ColourOf(Special.DashChop), 1.1f, 0.45f);
                if (board != null) SpecialFx.Text(board, SpecialCatalog.NameOf(Special.DashChop) + "!", ColourOf(Special.DashChop));
            }
            else if (message.Topic == TopicCharmChop)
            {
                if (board != null) SpecialFx.Text(board, Lang.T("LUCKY x2", "MAY MẮN x2"), ColourOf(Special.LuckyCharm));
            }
        }

        /// <summary>
        /// Server, a chef's chop stroke at a board: SLEEPY (dozing) and HEX swallow it, ZEN and
        /// LUCKY KNIFE finish the item. Returns false to skip the stroke; <paramref name="normal"/>
        /// when it went ahead.
        /// </summary>
        internal static bool BeforeChop(ServerWorkstation board, Transform interacter, out bool normal)
        {
            normal = false;
            if (!SpecialRuntime.InLevel || interacter == null) return true;
            ChefState chef = SpecialRuntime.StateOf(interacter.gameObject);
            if (chef == null) return true;
            ServerWorkableItem item = ItemOn(board);
            if (item == null) return true;
            Mine mine = Of(chef);
            mine.LastWork = Now;
            // Every machine's bar swallows the same stroke (BeforeClientChop).
            if (MovementSpecials.Dozing(chef)) return false;
            if (mine.HexServer > 0)
            {
                if (Now < mine.HexUntil)
                {
                    mine.HexServer--;
                    return false;
                }
                mine.HexServer = 0;
            }
            if (mine.ZenCharged && chef.Has(Special.Zen))
            {
                SetZen(chef, false);
                LevelState.SkipChop(interacter.gameObject);
                if (Finish(board, item, chef))
                {
                    SpecialFx.BurstAll(board.transform.position, Special.Zen);
                    SpecialFx.TextAll(board.gameObject, SpecialFx.NameBoth(Special.Zen, "!", "!"), Special.Zen);
                }
                return false;
            }
            if (chef.Has(Special.LuckyKnife) && UnityEngine.Random.value < LuckyKnifeChance)
            {
                // The bars counted this chop (their item finishes when the host swaps it), so
                // the host's CHOPPING credit counts it too and the two stay in step.
                LevelState.SkipChop(interacter.gameObject);
                if (Finish(board, item, chef))
                {
                    SpecialFx.BurstAll(board.transform.position, Special.LuckyKnife);
                    SpecialFx.TextAll(board.gameObject, Lang.Both("LUCKY!", "MAY MẮN!"), Special.LuckyKnife);
                }
                return false;
            }
            normal = true;
            return true;
        }

        /// <summary>Server, after a normal stroke: LUCKY CHARM, ECHO KNIFE, and MULTITASK bookkeeping.</summary>
        internal static void AfterChop(ServerWorkstation board, Transform interacter)
        {
            if (interacter == null || board == null) return;
            ChefState chef = SpecialRuntime.StateOf(interacter.gameObject);
            if (chef == null) return;
            Mine mine = Of(chef);

            if (chef.Has(Special.LuckyCharm) && UnityEngine.Random.value < LuckyCharmChance)
                ExtraStrokes(board, ItemOn(board), chef, TopicCharmChop, 1, 0f);

            if (chef.Has(Special.EchoKnife))
            {
                if (mine.EchoCurrent != board)
                {
                    if (mine.EchoCurrent != null) mine.EchoOther = mine.EchoCurrent;
                    mine.EchoCurrent = board;
                }
                // The host rolls; the echo goes out as strokes (TopicEcho), so every bar agrees.
                ServerWorkstation other = mine.EchoOther;
                if (other != null && other != board && other.gameObject.activeInHierarchy &&
                    FlatDistance(other.transform.position, chef.Position) <= EchoRange && UnityEngine.Random.value < EchoChance)
                {
                    ServerWorkableItem otherItem = ItemOn(other);
                    if (otherItem != null) ExtraStrokes(other, otherItem, chef, TopicEcho, 1, 0f);
                }
            }

            // The board belongs to whoever chopped it last: a chef without MULTITASK takes it over.
            ServerWorkableItem item = ItemOn(board);
            if (!chef.Has(Special.Multitask) || item == null)
            {
                s_multitask.Remove(board);
                return;
            }
            BoardJob job;
            if (!s_multitask.TryGetValue(board, out job) || job.Item != item)
                s_multitask[board] = new BoardJob { Chef = chef, Item = item };
            else job.Chef = chef;
            TouchJob(mine, board);
        }

        /// <summary>Every machine, a chop stroke on its own progress bar: SLEEPY and HEX swallow it here too.</summary>
        internal static bool BeforeClientChop(ClientWorkstation board, Transform interacter)
        {
            if ((!AnyHex && !AnySleepy) || interacter == null || ClientBoardItem == null) return true;
            ChefState chef = SpecialRuntime.StateOf(interacter.gameObject);
            if (chef == null || ClientBoardItem.GetValue(board) == null) return true;
            if (MovementSpecials.Dozing(chef)) return false;
            Mine mine = Of(chef);
            if (HexLeft(mine) <= 0) return true;
            mine.HexClient--;
            SpecialFx.Text(board.gameObject, mine.HexClient > 0 ? Lang.T("HEXED x", "BỊ NGUYỀN x") + mine.HexClient : Lang.T("HEX BROKEN", "GIẢI NGUYỀN"), ColourOf(Special.Hex));
            return false;
        }

        /// <summary>Strokes this machine's bars still swallow (0 once the hex has run out).</summary>
        private static int HexLeft(Mine mine)
        {
            if (mine.HexClient <= 0) return 0;
            if (Now < mine.HexUntil) return mine.HexClient;
            mine.HexClient = 0;
            return 0;
        }

        /// <summary>The chef's chop rhythm (seconds per stroke), from their chop animation.</summary>
        private static float ChopInterval(ChefState chef)
        {
            Mine mine = Of(chef);
            if (mine.ChopInterval <= 0f)
            {
                float duration = 0f, trigger;
                AnimationEventData data = chef.Chef != null ? chef.Chef.GetComponentInChildren<AnimationEventData>() : null;
                if (data == null || !data.GetTriggerData("Chop", "Impact", out duration, out trigger) || duration < 0.1f)
                    duration = 0.5f;
                mine.ChopInterval = duration;
            }
            return mine.ChopInterval;
        }

        /// <summary>Server: MULTITASK boards nobody is at keep chopping at 40% of the chef's rhythm.</summary>
        private static void MultitaskBoards(float dt)
        {
            s_deadJobs.Clear();
            foreach (KeyValuePair<ServerWorkstation, BoardJob> pair in s_multitask)
            {
                ServerWorkstation board = pair.Key;
                BoardJob job = pair.Value;
                ServerWorkableItem item = board != null ? ItemOn(board) : null;
                if (item == null || item != job.Item || job.Chef.Chef == null || !job.Chef.Has(Special.Multitask) ||
                    !board.gameObject.activeInHierarchy)
                {
                    s_deadJobs.Add(board);
                    continue;
                }
                if (Busy(board))
                {
                    job.Timer = 0f;
                    continue;
                }
                job.Timer += dt;
                float interval = ChopInterval(job.Chef) / MultitaskRate;
                if (job.Timer < interval) continue;
                job.Timer -= interval;
                ExtraStrokes(board, item, job.Chef, TopicMultitask, 1, interval + 0.5f);
            }
            for (int i = 0; i < s_deadJobs.Count; i++) s_multitask.Remove(s_deadJobs[i]);
        }

        /// <summary>Server: this board or sink is the chef's newest MULTITASK job.</summary>
        private static void TouchJob(Mine mine, Component station)
        {
            s_jobTouched[station] = ++s_jobSerial;
            int at = mine.Jobs.IndexOf(station);
            if (at == mine.Jobs.Count - 1 && at >= 0) return;
            if (at >= 0) mine.Jobs.RemoveAt(at);
            mine.Jobs.Add(station);
        }

        /// <summary>
        /// Server: each MULTITASK chef keeps at most two boards and sinks working by themselves
        /// (the one they stand at doesn't count), and so does each player (see CapJobsPerPlayer);
        /// a third takes over from the oldest. Jobs that ended or went to another chef leave the list.
        /// </summary>
        private static void PruneJobs()
        {
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState chef = chefs[i];
                if (!chef.Has(Special.Multitask)) continue;
                List<Component> jobs = Of(chef).Jobs;
                if (jobs.Count == 0) continue;
                int working = 0;
                for (int j = jobs.Count - 1; j >= 0; j--)
                {
                    bool alive, alone;
                    JobState(chef, jobs[j], out alive, out alone);
                    if (!alive) jobs.RemoveAt(j);
                    else if (alone) working++;
                }
                for (int j = 0; j < jobs.Count && working > MultitaskJobs; j++)
                {
                    bool alive, alone;
                    JobState(chef, jobs[j], out alive, out alone);
                    if (!alone) continue;
                    ServerWorkstation board = jobs[j] as ServerWorkstation;
                    if (board != null) s_multitask.Remove(board);
                    jobs.RemoveAt(j);
                    j--;
                    working--;
                }
            }
            CapJobsPerPlayer(chefs);
        }

        /// <summary>
        /// Server: a player alone with MULTITASK on both chefs still gets two jobs in all, not two
        /// each; the job touched longest ago gives way. (With one chef per player this changes nothing.)
        /// </summary>
        private static void CapJobsPerPlayer(IList<ChefState> chefs)
        {
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState chef = chefs[i];
                if (!chef.Has(Special.Multitask)) continue;
                // The player's first MULTITASK chef counts for all of them.
                bool counted = false;
                for (int k = 0; k < i && !counted; k++)
                    counted = chefs[k].Has(Special.Multitask) && SpecialRuntime.SamePlayer(chefs[k], chef);
                if (counted) continue;
                while (true)
                {
                    int working = 0, oldestAt = -1, oldestTouched = int.MaxValue;
                    ChefState oldestChef = null;
                    for (int k = i; k < chefs.Count; k++)
                    {
                        ChefState other = chefs[k];
                        if (!other.Has(Special.Multitask) || !SpecialRuntime.SamePlayer(other, chef)) continue;
                        List<Component> jobs = Of(other).Jobs;
                        for (int j = 0; j < jobs.Count; j++)
                        {
                            bool alive, alone;
                            JobState(other, jobs[j], out alive, out alone);
                            if (!alive || !alone) continue;
                            working++;
                            int touched;
                            if (!s_jobTouched.TryGetValue(jobs[j], out touched)) touched = 0;
                            if (touched < oldestTouched)
                            {
                                oldestTouched = touched;
                                oldestChef = other;
                                oldestAt = j;
                            }
                        }
                    }
                    if (working <= MultitaskJobs || oldestChef == null) break;
                    List<Component> list = Of(oldestChef).Jobs;
                    ServerWorkstation board = list[oldestAt] as ServerWorkstation;
                    if (board != null) s_multitask.Remove(board);
                    s_jobTouched.Remove(list[oldestAt]);
                    list.RemoveAt(oldestAt);
                }
            }
        }

        /// <summary>
        /// Server: is this still the chef's job (<paramref name="alive"/>), and is it working by
        /// itself right now (<paramref name="alone"/>: loaded and nobody at it)?
        /// </summary>
        private static void JobState(ChefState chef, Component station, out bool alive, out bool alone)
        {
            alive = alone = false;
            if (station == null || !station.gameObject.activeInHierarchy) return;
            ServerWorkstation board = station as ServerWorkstation;
            if (board != null)
            {
                BoardJob job;
                alive = s_multitask.TryGetValue(board, out job) && job.Chef == chef && ItemOn(board) == job.Item && job.Item != null;
                alone = alive && !Busy(board);
                return;
            }
            ServerWashingStation sink = station as ServerWashingStation;
            if (sink == null || SinkInteractor == null || SinkPlates == null) return;
            // A sink stays the chef's while they were the last to wash there.
            alive = SpecialRuntime.StateOf(SinkInteractor.GetValue(sink) as GameObject) == chef;
            if (!alive) return;
            ServerInteractable interactable;
            List<GameObject> washers = WashersOf(sink, out interactable);
            alone = (int)SinkPlates.GetValue(sink) > 0 && (washers == null || washers.Count == 0);
        }

        /// <summary>
        /// Server: DASH-CHOP gives each loaded board the dashing chef passes three strokes per
        /// dash, each board at most once every 2 s.
        /// </summary>
        private static void DashChopTick()
        {
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState chef = chefs[i];
                if (chef.Chef == null || !chef.Has(Special.DashChop)) continue;
                Mine mine = Of(chef);
                // The host runs every chef's movement (see MovementPatch), so this is its real dash.
                if (mine.Impl == null) mine.Impl = chef.Chef.GetComponent<ClientPlayerControlsImpl_Default>();
                if (mine.Impl == null || !mine.Impl.IsDashing()) continue;
                for (int b = 0; b < s_boards.Length; b++)
                {
                    ServerWorkstation board = s_boards[b];
                    if (board == null || !board.gameObject.activeInHierarchy) continue;
                    ServerWorkableItem item = ItemOn(board);
                    if (item == null) continue;
                    float distance;
                    // In front or beside: boards are passed, not dashed into.
                    if (!Faces(chef, board.transform.position, DashReach, DashArc, out distance)) continue;
                    // Also keeps one dash from hitting the same board twice.
                    float readyAt;
                    if (s_dashReadyAt.TryGetValue(board, out readyAt) && Now < readyAt) continue;
                    s_dashReadyAt[board] = Now + DashBoardSeconds;
                    ExtraStrokes(board, item, chef, TopicDash, DashStrokes, 0f);
                }
            }
        }

        // --- ZEN ----------------------------------------------------------------------------------

        /// <summary>Server: a ZEN chef a player steers, standing still (and not working) for 1.5 s, charges up.</summary>
        private static void ZenTick(float dt)
        {
            if (dt <= 0f) return;
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            // The charge is sent when it changes; again every 2 s, for a machine that started its
            // level after the change (its aura and the player's chip would stay wrong).
            bool resend = Now >= s_nextZenResend;
            if (resend) s_nextZenResend = Now + 2f;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState chef = chefs[i];
                if (chef.Chef == null || !chef.Has(Special.Zen)) continue;
                Mine mine = Of(chef);
                Vector3 position = chef.Position;
                Vector3 moved = position - mine.LastPosition;
                moved.y = 0f;
                bool still = mine.HasPosition && moved.magnitude / dt < ZenStillSpeed;
                mine.LastPosition = position;
                mine.HasPosition = true;
                // Chopping or washing is standing still too, but doesn't count as calm. Nor does a
                // chef nobody steers (the parked one of a player alone): calm is earned in play.
                bool working = Now - mine.LastWork < 0.8f;
                bool steered = SpecialRuntime.PlayerControlled(chef);
                mine.Still = still && !working && steered ? mine.Still + dt : 0f;
                if (!mine.ZenCharged && mine.Still >= ZenStillSeconds)
                {
                    SetZen(chef, true);
                    SpecialFx.TextAll(chef.Chef, SpecialFx.NameBoth(Special.Zen, "", ""), Special.Zen);
                }
                else if (resend) SetZen(chef, mine.ZenCharged);
            }
        }

        private static float s_nextZenResend;

        private static void SetZen(ChefState chef, bool charged)
        {
            Of(chef).ZenCharged = charged;
            // Every machine shows it (the aura and the player's own HUD line); the host's value is the real one.
            if (chef.Id != 0)
                SpecialNet.SendToAll(new SpecialMessage { Topic = TopicZen, Chef = chef.Id, I = charged ? 1 : 0 });
        }

        private static void OnZen(SpecialMessage message)
        {
            ChefState chef = SpecialRuntime.StateById(message.Chef);
            if (chef != null) Of(chef).ZenCharged = message.I == 1;
        }

        // --- sinks --------------------------------------------------------------------------------

        private static List<GameObject> WashersOf(ServerWashingStation sink, out ServerInteractable interactable)
        {
            interactable = SinkInteractable != null ? SinkInteractable.GetValue(sink) as ServerInteractable : null;
            return interactable != null && Interacters != null ? Interacters.GetValue(interactable) as List<GameObject> : null;
        }

        /// <summary>Server, every sink with plates: ZEN and LUCKY CHARM while washed, MULTITASK when left.</summary>
        private static void SinkTick(float dt, bool multitask)
        {
            if (SinkTimer == null || SinkPlates == null || SinkConfig == null) return;
            for (int s = 0; s < s_sinks.Length; s++)
            {
                ServerWashingStation sink = s_sinks[s];
                if (sink == null || !sink.gameObject.activeInHierarchy) continue;
                int plates = (int)SinkPlates.GetValue(sink);
                if (plates <= 0) continue;
                WashingStation config = SinkConfig.GetValue(sink) as WashingStation;
                if (config == null) continue;
                ServerInteractable interactable;
                List<GameObject> washers = WashersOf(sink, out interactable);
                if (washers != null && washers.Count > 0) Washed(sink, config, interactable, washers, dt);
                else if (multitask) LeftAlone(sink, config, interactable, plates, dt);
            }
        }

        private static void Washed(ServerWashingStation sink, WashingStation config, ServerInteractable interactable,
            List<GameObject> washers, float dt)
        {
            ChefState zen = null;
            bool lucky = false;
            for (int i = 0; i < washers.Count; i++)
            {
                ChefState chef = SpecialRuntime.StateOf(washers[i]);
                if (chef == null) continue;
                Mine mine = Of(chef);
                mine.LastWork = Now;
                if (chef.Has(Special.Multitask)) TouchJob(mine, sink);
                // A dozing or hexed chef isn't really washing (see StationsWashPatch).
                if (MovementSpecials.Dozing(chef) || HexWashing(mine, true)) continue;
                if (mine.ZenCharged && chef.Has(Special.Zen)) zen = chef;
                if (chef.Has(Special.LuckyCharm)) lucky = true;
            }
            if (zen != null)
            {
                // Over the plate time: the sink's own update finishes this plate next frame (and
                // WashPatch sees the timer go down, so it leaves it alone).
                SetZen(zen, false);
                SinkTimer.SetValue(sink, config.m_cleanPlateTime + 0.01f);
                s_scrub.Remove(sink);
                SpecialFx.BurstAll(sink.transform.position, Special.Zen);
                SpecialFx.TextAll(sink.gameObject, SpecialFx.NameBoth(Special.Zen, "!", "!"), Special.Zen);
                return;
            }
            if (!lucky) return;
            float scrub;
            s_scrub.TryGetValue(sink, out scrub);
            scrub += dt;
            if (scrub >= ScrubSeconds)
            {
                scrub -= ScrubSeconds;
                if (UnityEngine.Random.value < LuckyCharmChance)
                {
                    // That scrub counts twice: the same progress again, at the washers' rate.
                    int count = interactable != null && interactable.AllowMultipleInteractors() ? washers.Count : 1;
                    float bonus = ScrubSeconds * count * Plugin.Skills.AverageMultiplier(washers, null, Skill.Washing);
                    float timer = (float)SinkTimer.GetValue(sink) + bonus;
                    SinkTimer.SetValue(sink, timer);
                    // A plate this finishes is cleaned by the host's message; the bars only get the effect.
                    MirrorWashBonus(sink, bonus, timer > config.m_cleanPlateTime);
                }
            }
            s_scrub[sink] = scrub;
        }

        private static void MirrorWashBonus(ServerWashingStation sink, float bonus, bool finishes)
        {
            uint id = EntityIdOf(sink.gameObject);
            if (id != 0)
                SpecialNet.SendToAll(new SpecialMessage { Topic = TopicCharmWash, I = (int)id, A = bonus, B = finishes ? 1f : 0f });
            if (!finishes && PartialLobby && SinkSync != null) SinkSync.Invoke(sink, new object[] { true });
        }

        /// <summary>Every machine: add the host's LUCKY CHARM bonus to this machine's sink timer.</summary>
        private static void OnWashBonus(SpecialMessage message)
        {
            GameObject obj = EntityById(message.I);
            ClientWashingStation sink = obj != null ? obj.GetComponent<ClientWashingStation>() : null;
            if (sink == null) return;
            SpecialFx.Text(sink.gameObject, Lang.T("LUCKY x2", "MAY MẮN x2"), ColourOf(Special.LuckyCharm));
            if (message.B > 0.5f || ClientSinkTimer == null) return;
            float timer = (float)ClientSinkTimer.GetValue(sink) + message.A;
            ClientSinkTimer.SetValue(sink, timer);
            var ui = ClientSinkUi != null ? ClientSinkUi.GetValue(sink) as ProgressUIController : null;
            var config = ClientSinkConfig != null ? ClientSinkConfig.GetValue(sink) as WashingStation : null;
            if (ui != null && config != null && config.m_cleanPlateTime > 0f)
                ui.SetProgress(Mathf.Clamp01(timer / config.m_cleanPlateTime));
        }

        /// <summary>Server: MULTITASK, the last washer walked away; keep washing at 35% of their rate.</summary>
        private static void LeftAlone(ServerWashingStation sink, WashingStation config, ServerInteractable interactable,
            int plates, float dt)
        {
            GameObject last = SinkInteractor != null ? SinkInteractor.GetValue(sink) as GameObject : null;
            ChefState chef = SpecialRuntime.StateOf(last);
            if (chef == null || !chef.Has(Special.Multitask) || interactable == null || !interactable.enabled) return;
            // One of the chef's two jobs (PruneJobs dropped it if not).
            if (!Of(chef).Jobs.Contains(sink)) return;
            ServerPlateReturnStation rack = SinkRack != null ? SinkRack.GetValue(sink) as ServerPlateReturnStation : null;
            // A full or blocked drying rack stops the sink, as it does for a washing chef.
            if (rack == null || !rack.CanReturnPlate()) return;

            SpecialFx.MarkAll(sink.gameObject, Special.Multitask, 1f, null);
            float timer = (float)SinkTimer.GetValue(sink) + dt * MultitaskRate * Plugin.Skills.Multiplier(last, Skill.Washing);
            if (timer > config.m_cleanPlateTime)
            {
                // What the sink's own update does when a plate is clean (it only runs while washed).
                SinkTimer.SetValue(sink, 0f);
                SinkPlates.SetValue(sink, plates - 1);
                if (SinkCleaned != null) SinkCleaned.Invoke(sink, null);
                rack.ReturnPlate();
                if (plates - 1 == 0) interactable.enabled = false;
                s_sinkSyncAt.Remove(sink);
                return;
            }
            SinkTimer.SetValue(sink, timer);
            // Nobody washes here, so the clients' bars stand still: send them the host's timer
            // with the game's own "interaction state" message a few times a second.
            float next;
            if (SinkSync != null)
            {
                bool started = !s_sinkSyncAt.TryGetValue(sink, out next);
                if (started || Now >= next)
                {
                    s_sinkSyncAt[sink] = Now + 0.25f;
                    // A sink that ran empty hides its bar, and only "washing" shows it again: say
                    // so once (every machine shows the bar), then "not washing" so they don't
                    // run the timer themselves. Both go in order on the game's own channel.
                    if (started) SinkSync.Invoke(sink, new object[] { true });
                    SinkSync.Invoke(sink, new object[] { false });
                }
            }
        }

        // --- deliveries: DISHWASHER and BUTLER ----------------------------------------------------

        private static bool ReadListTypes(object list)
        {
            if (list == null) return false;
            Type type = list.GetType();
            if (type != s_listType)
            {
                s_listType = type;
                s_listCount = type.GetProperty("Count");
                s_listItems = type.GetField("_items");
                s_listRemoveAt = type.GetMethod("RemoveAt", new Type[] { typeof(int) });
                s_listAdd = type.GetMethod("Add");
            }
            return s_listCount != null && s_listItems != null && s_listRemoveAt != null && s_listAdd != null;
        }

        private static bool ReadEntryTypes(object entry)
        {
            if (entry == null) return false;
            Type type = entry.GetType();
            if (type != s_entryType)
            {
                s_entryType = type;
                s_entryStation = AccessTools.Field(type, "m_station");
                s_entryTimer = AccessTools.Field(type, "m_timer");
            }
            return s_entryStation != null && s_entryTimer != null;
        }

        /// <summary>How many plates the controller is waiting to return (-1 if unreadable).</summary>
        internal static int PendingCount(PlateReturnController controller)
        {
            if (PendingPlates == null) return -1;
            object list = PendingPlates.GetValue(controller);
            return ReadListTypes(list) ? (int)s_listCount.GetValue(list, null) : -1;
        }

        /// <summary>
        /// Server, a dish was just handed in: a PLATE CONJURER plate doesn't come back at all;
        /// for a chef's dish, DISHWASHER sends every third plate to a sink's drying rack (clean),
        /// BUTLER sends the dirty plate into a sink, twice as fast.
        /// </summary>
        internal static void Delivered(PlateReturnController controller, PlatingStepData plateType, ServerPlateStation pass, int before)
        {
            if (PendingPlates == null) return;
            object list = PendingPlates.GetValue(controller);
            if (!ReadListTypes(list)) return;
            int count = (int)s_listCount.GetValue(list, null);
            if (count != before + 1) return; // no plate was queued for this dish

            // Conjured plates are used once (whoever handed the dish in): drop its return first.
            if (s_conjured.Count > 0 && pass != null && PlateOnPass != null)
            {
                ServerPlate plate = PlateOnPass.GetValue(pass) as ServerPlate;
                uint plateId = plate != null ? EntityIdOf(plate.gameObject) : 0u;
                if (plateId != 0 && s_conjured.Remove(plateId))
                {
                    s_listRemoveAt.Invoke(list, new object[] { count - 1 });
                    return;
                }
            }

            ChefState chef = SpecialRuntime.StateOf(LevelState.CurrentPlacer);
            if (chef == null) return;
            bool dishwasher = chef.Has(Special.Dishwasher), butler = chef.Has(Special.Butler);
            if (!dishwasher && !butler) return;
            Array items = s_listItems.GetValue(list) as Array;
            object entry = items != null && items.Length >= count ? items.GetValue(count - 1) : null;
            if (!ReadEntryTypes(entry)) return;

            ServerWashingStation sink = NearestSink(plateType, chef.Position);
            if (sink == null) return; // plates come back clean anyway in kitchens without a sink
            if (dishwasher)
            {
                Mine mine = Of(chef);
                mine.Dishes++;
                ServerPlateReturnStation rack = SinkRack != null ? SinkRack.GetValue(sink) as ServerPlateReturnStation : null;
                if (mine.Dishes % DishwasherEvery == 0 && rack != null)
                {
                    // Same delay, but it comes back on the drying rack: a clean plate.
                    s_entryStation.SetValue(entry, rack);
                    s_clean.Add(new CleanPlate { Entry = entry, Controller = controller, Rack = rack, GiveUpAt = Now + 120f });
                    SpecialFx.TextAll(chef.Chef, Lang.Both("CLEAN PLATE", "ĐĨA SẠCH"), Special.Dishwasher);
                    return;
                }
            }
            if (butler && SinkAddPlates != null)
            {
                float timer = (float)s_entryTimer.GetValue(entry) / ButlerSpeed;
                s_listRemoveAt.Invoke(list, new object[] { count - 1 });
                s_butler.Add(new ButlerPlate { Sink = sink, Timer = timer, Entry = entry, Controller = controller });
            }
        }

        /// <summary>The sink whose drying rack gives back this kind of plate, nearest first.</summary>
        private static ServerWashingStation NearestSink(PlatingStepData plateType, Vector3 near)
        {
            ServerWashingStation best = null;
            float bestDistance = float.MaxValue;
            foreach (ServerWashingStation sink in Object.FindObjectsOfType<ServerWashingStation>())
            {
                if (sink == null || !sink.gameObject.activeInHierarchy) continue;
                ServerPlateReturnStation rack = SinkRack != null ? SinkRack.GetValue(sink) as ServerPlateReturnStation : null;
                if (rack == null || rack.GetPlatingStep() != plateType) continue;
                float distance = (sink.transform.position - near).sqrMagnitude;
                if (distance < bestDistance)
                {
                    best = sink;
                    bestDistance = distance;
                }
            }
            return best;
        }

        /// <summary>Server: BUTLER plates whose return time is up go into their sink.</summary>
        private static void ButlerTick(float dt)
        {
            for (int i = s_butler.Count - 1; i >= 0; i--)
            {
                ButlerPlate plate = s_butler[i];
                plate.Timer -= dt;
                if (plate.Timer > 0f) continue;
                s_butler.RemoveAt(i);
                if (plate.Sink != null && plate.Sink.gameObject.activeInHierarchy)
                {
                    // The game's own "plates added" path: the sink's count, cosmetics and clients.
                    SinkAddPlates.Invoke(plate.Sink, new object[] { 1 });
                    SpecialFx.BurstAll(plate.Sink.transform.position, Special.Butler);
                    SpecialFx.TextAll(plate.Sink.gameObject, Lang.Both("+1 PLATE", "+1 ĐĨA"), Special.Butler);
                    continue;
                }
                // The sink is gone: give the plate back to the normal return.
                object list = plate.Controller != null ? PendingPlates.GetValue(plate.Controller) : null;
                if (ReadListTypes(list) && ReadEntryTypes(plate.Entry))
                {
                    s_entryTimer.SetValue(plate.Entry, 0f);
                    s_listAdd.Invoke(list, new object[] { plate.Entry });
                }
            }
        }

        /// <summary>Server: a DISHWASHER plate has left the controller's queue, so it is on its rack.</summary>
        private static void CleanPlateTick()
        {
            for (int i = s_clean.Count - 1; i >= 0; i--)
            {
                CleanPlate plate = s_clean[i];
                bool pending = Now < plate.GiveUpAt && plate.Controller != null && Contains(plate.Controller, plate.Entry);
                if (pending) continue;
                s_clean.RemoveAt(i);
                if (Now >= plate.GiveUpAt || plate.Rack == null || !plate.Rack.gameObject.activeInHierarchy) continue;
                SpecialFx.BurstAll(plate.Rack.transform.position, Special.Dishwasher);
                SpecialFx.TextAll(plate.Rack.gameObject, Lang.Both("CLEAN!", "SẠCH!"), Special.Dishwasher);
            }
        }

        private static bool Contains(PlateReturnController controller, object entry)
        {
            object list = PendingPlates != null ? PendingPlates.GetValue(controller) : null;
            if (!ReadListTypes(list)) return false;
            int count = (int)s_listCount.GetValue(list, null);
            Array items = s_listItems.GetValue(list) as Array;
            if (items == null) return false;
            for (int i = 0; i < count && i < items.Length; i++)
                if (ReferenceEquals(items.GetValue(i), entry)) return true;
            return false;
        }

        // --- PREP COOK ----------------------------------------------------------------------------

        /// <summary>Server, a crate just handed an ingredient to a chef: maybe swap it for its chopped form.</summary>
        internal static void CratePickup(ICarrier carrier)
        {
            if (!SpecialRuntime.InLevel || carrier == null) return;
            ChefState chef = SpecialRuntime.StateOf(carrier.AccessGameObject());
            if (chef == null || !chef.Has(Special.PrepCook)) return;
            Mine mine = Of(chef);
            if (Now < mine.PrepReadyAt) return;
            GameObject item = carrier.InspectCarriedItem();
            if (item == null) return;
            WorkableItem workable = item.GetComponent<WorkableItem>();
            GameObject chopped = workable != null ? workable.GetNextPrefab() : null;
            // The item registered its chopped form as spawnable (on every machine) when it was
            // created, exactly as for a board finishing it.
            if (chopped == null || item.GetComponent<ServerWorkableItem>() == null) return;
            GameObject spawned = NetworkUtils.ServerSpawnPrefab(item, chopped);
            if (spawned == null) return;
            carrier.DestroyCarriedItem();
            carrier.CarryItem(spawned);
            SetPrep(chef, PrepCookSeconds);
            SpecialFx.BurstAll(chef.Position, Special.PrepCook);
            SpecialFx.TextAll(chef.Chef, Lang.Both("PREPPED!", "SƠ CHẾ XONG!"), Special.PrepCook);
        }

        private static void SetPrep(ChefState chef, float seconds)
        {
            if (chef.Id == 0)
            {
                ApplyPrep(chef, seconds);
                return;
            }
            SpecialNet.SendToAll(new SpecialMessage { Topic = TopicPrep, Chef = chef.Id, A = seconds });
        }

        private static void OnPrep(SpecialMessage message)
        {
            ChefState chef = SpecialRuntime.StateById(message.Chef);
            if (chef != null) ApplyPrep(chef, message.A);
        }

        /// <summary>Every machine: PREP COOK recharges; the player sees the sweep on its icon.</summary>
        private static void ApplyPrep(ChefState chef, float seconds)
        {
            Of(chef).PrepReadyAt = Now + seconds;
            chef.SetTimer(Special.PrepCook, Now + seconds, seconds);
        }

        // --- INSPIRING ----------------------------------------------------------------------------

        /// <summary>
        /// Every machine: chopping and washing levels for a chef near an INSPIRING teammate, worth
        /// about +25% (whole levels). Never stacks; LONE WOLF chefs ignore it, and a player alone
        /// can't inspire their own other chef.
        /// </summary>
        private static int InspiringBonus(ChefState chef, Skill skill)
        {
            if (skill != Skill.Chopping && skill != Skill.Washing) return 0;
            if (chef.Chef == null || chef.Has(Special.LoneWolf)) return 0;
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState other = chefs[i];
                if (other == chef || other.Chef == null || !other.Has(Special.Inspiring) || other.Team != chef.Team) continue;
                if (SpecialRuntime.SamePlayer(other, chef)) continue;
                if (FlatDistance(other.Position, chef.Position) > InspiringRange) continue;
                float perLevel = (SkillStats.Multiplier(skill, 1) - 1f) * 100f;
                return perLevel > 0.5f ? Mathf.Max(1, Mathf.RoundToInt(InspiringPercent / perLevel)) : 0;
            }
            return 0;
        }

        // --- GERMAPHOBE and TUNNEL VISION ---------------------------------------------------------

        /// <summary>
        /// Every machine: may this chef work at this board or sink? On the player's own machine
        /// (<paramref name="client"/>) a refusal also tells them why, now and then.
        /// </summary>
        internal static bool Blocked(GameObject station, GameObject interacter, bool client)
        {
            ChefState chef = SpecialRuntime.StateOf(interacter);
            if (chef == null) return false;
            bool germaphobe = chef.Has(Special.Germaphobe), tunnel = chef.Has(Special.TunnelVision);
            if (!germaphobe && !tunnel) return false;
            Mine mine = Of(chef);
            bool sink = station.GetComponent<WashingStation>() != null;
            bool board = !sink && station.GetComponent<Workstation>() != null;
            bool phobia = sink && germaphobe;
            bool blocked = phobia || (tunnel && mine.TunnelMode != 0 && (sink || board) && !TunnelAllows(mine, station, sink));
            if (blocked && client && chef.Local && chef.Chef != null)
            {
                if (Now - mine.BlockedTextAt >= BlockedTextSeconds)
                {
                    mine.BlockedTextAt = Now;
                    SpecialFx.Text(chef.Chef, phobia ? Lang.T("NO WASHING", "KHÔNG RỬA ĐÂU") : TunnelText(mine.TunnelMode),
                        ColourOf(phobia ? Special.Germaphobe : Special.TunnelVision));
                }
            }
            return blocked;
        }

        private static string TunnelText(int mode)
        {
            return mode == ModeChop ? Lang.T("THIS BOARD ONLY", "CHỈ THỚT NÀY") : Lang.T("THIS SINK ONLY", "CHỈ BỒN RỬA NÀY");
        }

        /// <summary>
        /// Every machine: is this the TUNNEL VISION station (its board; its sink, or one right
        /// next to it)? From entity IDs and positions, so every machine gives the same answer.
        /// </summary>
        private static bool TunnelAllows(Mine mine, GameObject station, bool sink)
        {
            if (mine.TunnelMode == 0) return true;
            if (sink != (mine.TunnelMode == ModeWash)) return false;
            uint key = StationKey(station);
            if (key != 0 && key == mine.TunnelStation) return true;
            Vector3 at = station.transform.position;
            if (sink) return FlatDistance(at, mine.TunnelAt) <= TunnelSinkJoin;
            // A board without an entity ID: by where it stands.
            return key == 0 && FlatDistance(at, mine.TunnelAt) < 0.1f;
        }

        /// <summary>
        /// A station's own network entity ID (0 if it has none): the same on every machine. Not
        /// a parent's, which other stations may share.
        /// </summary>
        private static uint StationKey(GameObject station)
        {
            uint id = EntityIdOf(station);
            return id != EntitySerialisationRegistry.kInvalidEntityID ? id : 0u;
        }

        /// <summary>
        /// Server: a TUNNEL VISION chef's first board or sink is the only station their player
        /// may chop or wash at (both chefs, for a player alone).
        /// </summary>
        internal static void BeganInteract(GameObject station, GameObject interacter)
        {
            ChefState chef = SpecialRuntime.StateOf(interacter);
            if (chef == null || !chef.Has(Special.TunnelVision) || Of(chef).TunnelMode != 0) return;
            int mode = station.GetComponent<Workstation>() != null ? ModeChop
                : station.GetComponent<WashingStation>() != null ? ModeWash : 0;
            if (mode == 0) return;
            uint key = StationKey(station);
            Vector3 at = station.transform.position;
            if (chef.Id != 0)
                SpecialNet.SendToAll(new SpecialMessage { Topic = TopicTunnel, Chef = chef.Id, I = (int)key, A = mode, Vector2 = at });
            else SetTunnel(chef, mode, key, at);
        }

        private static void OnTunnel(SpecialMessage message)
        {
            ChefState chef = SpecialRuntime.StateById(message.Chef);
            // I = the station's entity ID, A = the mode, Vector2 = where it stands.
            if (chef != null) SetTunnel(chef, Mathf.RoundToInt(message.A), (uint)message.I, message.Vector2);
        }

        /// <summary>Every machine: lock the chef and the other chefs of their player to this station.</summary>
        private static void SetTunnel(ChefState chef, int mode, uint key, Vector3 at)
        {
            if (Of(chef).TunnelMode != 0) return;
            var chefs = SpecialRuntime.SiblingsOf(chef);
            chefs.Insert(0, chef);
            for (int i = 0; i < chefs.Count; i++)
            {
                Mine mine = Of(chefs[i]);
                if (mine.TunnelMode != 0) continue;
                mine.TunnelMode = mode;
                mine.TunnelStation = key;
                mine.TunnelAt = at;
                if (chefs[i].Chef != null && chefs[i].Has(Special.TunnelVision))
                    SpecialFx.Text(chefs[i].Chef, TunnelText(mode), ColourOf(Special.TunnelVision));
            }
        }

        // --- HEX ----------------------------------------------------------------------------------

        private static bool Hex(ChefState state)
        {
            // The chef faced (from where the caster's own machine had them), a rival before a teammate.
            Vector3 from = AimPosition(state), forward = AimForward(state);
            ChefState target = null;
            float best = float.MaxValue;
            bool lonely = false;
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState other = chefs[i];
                if (other == state || other.Chef == null) continue;
                // The chef the player chose (in range, whichever way the caster faces).
                if (state.HasAimPoint && state.AimChef != null && other != state.AimChef) continue;
                float distance;
                bool inReach = state.HasAimPoint && state.AimChef != null
                    ? (distance = FlatDistance(from, other.Position)) <= HexRange + AimSlack
                    : Faces(from, forward, other.Position, HexRange, HexArc, out distance);
                if (!inReach) continue;
                if (other.Team == state.Team && other.Has(Special.LoneWolf))
                {
                    lonely = true;
                    continue;
                }
                float score = distance + (other.Team == state.Team ? 10f : 0f);
                if (score < best)
                {
                    best = score;
                    target = other;
                }
            }
            if (target == null) return state.Refuse(lonely ? SpecialFx.NameBoth(Special.LoneWolf, " IGNORES IT", " PHỚT LỜ") : Lang.Both("NO CHEF IN FRONT", "KHÔNG CÓ ĐẦU BẾP TRƯỚC MẶT"));
            // B = seconds of washing it swallows, C = when it started (the host's level time).
            if (target.Id == 0) ApplyHex(target, HexStrokes, HexWashSeconds, 0f);
            else SpecialNet.SendToAll(new SpecialMessage { Topic = TopicHex, Chef = target.Id, I = HexStrokes, D = state.Id,
                B = HexWashSeconds, C = SpecialRuntime.LevelTime });
            SpecialFx.BeamAll(from, target.Position, Special.Hex, 0.6f);
            return true;
        }

        /// <param name="late">How long ago the host cast it (a message's trip; at most half a second counts).</param>
        private static void ApplyHex(ChefState target, int strokes, float washSeconds, float late)
        {
            Mine mine = Of(target);
            // Separate counts for the host's real chopping and washing and each machine's own
            // progress bars (the host runs both for the same stroke or frame). Both run out after
            // a while, so a count that drifted apart can't linger.
            mine.HexServer = strokes;
            mine.HexClient = strokes;
            mine.HexWashServer = washSeconds;
            mine.HexWashClient = washSeconds;
            mine.HexUntil = Now + HexSeconds - Mathf.Clamp(late, 0f, 0.5f);
            AnyHex = true;
            if (target.Chef != null) SpecialFx.Text(target.Chef, Lang.T("HEXED!", "BỊ NGUYỀN!"), ColourOf(Special.Hex));
        }

        private static void OnHex(SpecialMessage message)
        {
            ChefState target = SpecialRuntime.StateById(message.Chef);
            if (target != null) ApplyHex(target, message.I, message.B, SpecialRuntime.LevelTime - message.C);
        }

        /// <summary>
        /// Is a hex still swallowing this chef's washing, on the host's sinks
        /// (<paramref name="server"/>) or this machine's bars?
        /// </summary>
        private static bool HexWashing(Mine mine, bool server)
        {
            float left = server ? mine.HexWashServer : mine.HexWashClient;
            if (left <= 0f) return false;
            if (Now < mine.HexUntil) return true;
            mine.HexWashServer = mine.HexWashClient = 0f;
            return false;
        }

        /// <summary>
        /// Every machine, a sink's frame, before its timer moves (<paramref name="server"/>: the
        /// host's real sink, else this machine's bar): a dozing SLEEPY chef doesn't wash, and a
        /// hexed one's first seconds of washing do nothing. Returns the share of the washers who
        /// really wash (0 = the sink stands still this frame).
        /// </summary>
        internal static float WashingShare(List<GameObject> washers, float dt, bool server)
        {
            if (washers == null || washers.Count == 0) return 1f;
            int blocked = 0;
            for (int i = 0; i < washers.Count; i++)
            {
                ChefState chef = SpecialRuntime.StateOf(washers[i]);
                if (chef == null) continue;
                if (MovementSpecials.Dozing(chef))
                {
                    blocked++;
                    continue;
                }
                Mine mine = Of(chef);
                if (!HexWashing(mine, server)) continue;
                blocked++;
                if (server) mine.HexWashServer -= dt;
                else mine.HexWashClient -= dt;
            }
            return blocked == 0 ? 1f : (washers.Count - blocked) / (float)washers.Count;
        }

        /// <summary>The host's sink about to wash this frame: its washers, or null if it won't wash.</summary>
        internal static List<GameObject> ServerWashers(ServerWashingStation sink)
        {
            if (SinkPlates == null || (int)SinkPlates.GetValue(sink) <= 0) return null;
            ServerInteractable interactable;
            List<GameObject> washers = WashersOf(sink, out interactable);
            return interactable != null && interactable.IsBeingInteractedWith() ? washers : null;
        }

        /// <summary>This machine's sink bar about to move this frame: its washers, or null if it won't.</summary>
        internal static List<GameObject> ClientWashers(ClientWashingStation sink)
        {
            if (ClientSinkPlates == null || ClientSinkWashing == null || ClientSinkInteractable == null || ClientInteracters == null) return null;
            if (!(bool)ClientSinkWashing.GetValue(sink) || (int)ClientSinkPlates.GetValue(sink) <= 0) return null;
            object interactable = ClientSinkInteractable.GetValue(sink);
            return interactable != null ? ClientInteracters.GetValue(interactable) as List<GameObject> : null;
        }

        /// <summary>This machine's sink bar: draw it again from its (held back) timer.</summary>
        internal static void RedrawSink(ClientWashingStation sink)
        {
            if (ClientSinkTimer == null) return;
            var ui = ClientSinkUi != null ? ClientSinkUi.GetValue(sink) as ProgressUIController : null;
            var config = ClientSinkConfig != null ? ClientSinkConfig.GetValue(sink) as WashingStation : null;
            if (ui != null && config != null && config.m_cleanPlateTime > 0f)
                ui.SetProgress(Mathf.Clamp01((float)ClientSinkTimer.GetValue(sink) / config.m_cleanPlateTime));
        }

        internal static float ServerSinkTimer(ServerWashingStation sink)
        {
            return SinkTimer != null ? (float)SinkTimer.GetValue(sink) : 0f;
        }

        internal static void SetServerSinkTimer(ServerWashingStation sink, float timer)
        {
            if (SinkTimer != null) SinkTimer.SetValue(sink, timer);
        }

        internal static float ClientSinkTimerOf(ClientWashingStation sink)
        {
            return ClientSinkTimer != null ? (float)ClientSinkTimer.GetValue(sink) : 0f;
        }

        internal static void SetClientSinkTimer(ClientWashingStation sink, float timer)
        {
            if (ClientSinkTimer != null) ClientSinkTimer.SetValue(sink, timer);
        }

        // --- actives ------------------------------------------------------------------------------

        private static bool PowerChop(ChefState state)
        {
            // TUNNEL VISION, as Wash Burst checks it the other way: a chef locked to a sink can't
            // get chopping back through a second active.
            bool tunnel = state.Has(Special.TunnelVision);
            if (tunnel && Of(state).TunnelMode == ModeWash) return state.Refuse(Lang.Both("YOU CAN'T CHOP", "BẠN KHÔNG THÁI ĐƯỢC"));
            ServerWorkstation board = state.HasAimPoint
                ? ChosenBoard(state, AimPosition(state), Object.FindObjectsOfType<ServerWorkstation>())
                : FacedBoard(AimPosition(state), AimForward(state), Object.FindObjectsOfType<ServerWorkstation>(), BoardReach);
            if (board == null) return state.Refuse(Lang.Both("NO FOOD ON A BOARD IN FRONT", "KHÔNG CÓ ĐỒ TRÊN THỚT TRƯỚC MẶT"));
            // Only the chef's own board.
            if (tunnel && !TunnelAllows(Of(state), board.gameObject, false)) return state.Refuse(Lang.Both("NOT YOUR BOARD", "KHÔNG PHẢI THỚT CỦA BẠN"));
            if (!Finish(board, ItemOn(board), state)) return state.Refuse(Lang.Both("NOTHING TO CHOP", "KHÔNG CÓ GÌ ĐỂ THÁI"));
            SpecialFx.BurstAll(board.transform.position, Special.PowerChop);
            SpecialFx.TextAll(board.gameObject, Lang.Both("CHOPPED!", "THÁI XONG!"), Special.PowerChop);
            return true;
        }

        /// <summary>The loaded board the player chose (nearest the chosen spot, in reach).</summary>
        private static ServerWorkstation ChosenBoard(ChefState state, Vector3 from, IEnumerable<ServerWorkstation> boards)
        {
            ServerWorkstation best = null;
            float bestDistance = float.MaxValue;
            foreach (ServerWorkstation board in boards)
            {
                if (board == null || !board.gameObject.activeInHierarchy || ItemOn(board) == null) continue;
                float distance;
                if (!Reaches(state, from, Vector3.zero, board.transform.position, BoardReach, FaceArc, out distance)) continue;
                float off = state.AimDistance(board.transform.position);
                if (off < bestDistance)
                {
                    best = board;
                    bestDistance = off;
                }
            }
            return best;
        }

        private static bool WashBurst(ChefState state)
        {
            if (state.Has(Special.Germaphobe) || (state.Has(Special.TunnelVision) && Of(state).TunnelMode == ModeChop)) return state.Refuse(Lang.Both("YOU CAN'T WASH", "BẠN KHÔNG RỬA ĐƯỢC"));
            if (SinkPlates == null) return state.Refuse(Lang.Both("NOT AVAILABLE", "KHÔNG DÙNG ĐƯỢC"));
            Vector3 from = AimPosition(state), forward = AimForward(state);
            ServerWashingStation best = null;
            float bestDistance = float.MaxValue;
            bool full = false;
            Mine mine = Of(state);
            bool tunnel = state.Has(Special.TunnelVision);
            foreach (ServerWashingStation sink in Object.FindObjectsOfType<ServerWashingStation>())
            {
                if (sink == null || !sink.gameObject.activeInHierarchy || (int)SinkPlates.GetValue(sink) <= 0) continue;
                // TUNNEL VISION: only the chef's own sink.
                if (tunnel && !TunnelAllows(mine, sink.gameObject, true)) continue;
                float distance;
                if (!Reaches(state, from, forward, sink.transform.position, SinkReach, FaceArc, out distance) || distance >= bestDistance) continue;
                ServerPlateReturnStation rack = SinkRack != null ? SinkRack.GetValue(sink) as ServerPlateReturnStation : null;
                if (rack == null || !rack.CanReturnPlate())
                {
                    full = true;
                    continue;
                }
                best = sink;
                bestDistance = distance;
            }
            if (best == null) return state.Refuse(full ? Lang.Both("DRYING RACK BLOCKED", "GIÁ PHƠI BỊ CHẶN") : Lang.Both("NO DIRTY PLATES IN FRONT", "KHÔNG CÓ ĐĨA BẨN TRƯỚC MẶT"));
            int plates = (int)SinkPlates.GetValue(best);
            ServerPlateReturnStation bestRack = SinkRack != null ? SinkRack.GetValue(best) as ServerPlateReturnStation : null;
            // The game's own "wash the whole stack" (clean plates onto the rack, clients told).
            best.WashAllPlates();
            SpecialFx.BurstAll(best.transform.position, Special.WashBurst);
            SpecialFx.TextAll(bestRack != null ? bestRack.gameObject : best.gameObject, Lang.Both("CLEAN x" + plates, "SẠCH x" + plates), Special.WashBurst);
            return true;
        }

        private static bool TopHasContents(ServerPlateStackBase stack)
        {
            ServerStack items = StackItems != null ? StackItems.GetValue(stack) as ServerStack : null;
            GameObject top = items != null ? items.InspectTopOfStack() : null;
            IIngredientContents contents = top != null ? top.RequestInterface<IIngredientContents>() : null;
            return contents != null && contents.HasContents();
        }

        private static bool PlateConjurer(ChefState state)
        {
            if (state.Chef == null || RackStack == null || RackConfig == null) return state.Refuse(Lang.Both("NOT AVAILABLE", "KHÔNG DÙNG ĐƯỢC"));
            ServerPlayerAttachmentCarrier carrier = state.Chef.GetComponent<ServerPlayerAttachmentCarrier>();
            if (carrier == null) return state.Refuse(Lang.Both("NOT AVAILABLE", "KHÔNG DÙNG ĐƯỢC"));
            if (carrier.InspectCarriedItem() != null) return state.Refuse(Lang.Both("HANDS FULL", "TAY ĐANG CẦM ĐỒ"));

            // A plate can only be spawned where the game spawns clean plates (a stack on a clean
            // return station or drying rack, registered on every machine); add one to the nearest
            // such stack and pick it up from there, exactly like a chef taking the top plate.
            ServerPlateReturnStation best = null;
            float bestDistance = float.MaxValue;
            foreach (ServerPlateReturnStation rack in Object.FindObjectsOfType<ServerPlateReturnStation>())
            {
                if (rack == null || !rack.gameObject.activeInHierarchy || !rack.CanReturnPlate()) continue;
                PlateReturnStation config = RackConfig.GetValue(rack) as PlateReturnStation;
                if (config == null || config.m_stackPrefab == null || config.m_stackPrefab.GetComponent<CleanPlateStack>() == null) continue;
                ServerPlateStackBase existing = RackStack.GetValue(rack) as ServerPlateStackBase;
                // A plate with food on top would be lifted instead of the new one.
                if (existing != null && TopHasContents(existing)) continue;
                float distance = (rack.transform.position - state.Position).sqrMagnitude;
                if (distance < bestDistance)
                {
                    best = rack;
                    bestDistance = distance;
                }
            }
            if (best == null) return state.Refuse(Lang.Both("NO FREE CLEAN PLATE RACK", "HẾT CHỖ ĐỂ ĐĨA SẠCH"));
            best.ReturnPlate();
            ServerCleanPlateStack stack = RackStack.GetValue(best) as ServerCleanPlateStack;
            SpecialFx.BurstAll(best.transform.position, Special.PlateConjurer);
            if (stack == null)
            {
                // The plate is on the rack at least.
                MarkConjured(TopOf(RackStack.GetValue(best) as ServerPlateStackBase));
                SpecialFx.TextAll(best.gameObject, Lang.Both("+1 PLATE", "+1 ĐĨA"), Special.PlateConjurer);
                return true;
            }
            Vector3 forward = state.Forward;
            GameObject top = TopOf(stack);
            stack.HandlePickup(carrier, new Vector2(forward.x, forward.z).normalized);
            GameObject held = carrier.InspectCarriedItem();
            MarkConjured(held != null && held.GetComponent<ServerPlate>() != null ? held : top);
            SpecialFx.BeamAll(best.transform.position, state.Position, Special.PlateConjurer, 0.5f);
            return true;
        }

        private static GameObject TopOf(ServerPlateStackBase stack)
        {
            ServerStack items = stack != null && StackItems != null ? StackItems.GetValue(stack) as ServerStack : null;
            return items != null ? items.InspectTopOfStack() : null;
        }

        /// <summary>Server: this plate was conjured; its dish's plate won't come back (see Delivered).</summary>
        private static void MarkConjured(GameObject plate)
        {
            uint id = EntityIdOf(plate);
            if (id != 0) s_conjured.Add(id);
        }

        // --- SPARE PAN ----------------------------------------------------------------------------

        private static bool SparePan(ChefState state)
        {
            if (state.Chef == null) return state.Refuse(Lang.Both("NOT AVAILABLE", "KHÔNG DÙNG ĐƯỢC"));
            // Every machine must make the same template.
            if (PartialLobby) return state.Refuse(Lang.Both("NEEDS CHEF SKILLS ON ALL PCS", "CẦN CHEF SKILLS TRÊN MỌI MÁY"));
            // One copy out at a time: it goes once emptied onto a plate (see SpareEmptied).
            Mine mine = Of(state);
            if (mine.Spare != 0 && EntityById((int)mine.Spare) != null)
                return state.Refuse(Lang.Both("SPARE STILL OUT", "CHẢO DỰ PHÒNG VẪN CÒN"));
            mine.Spare = 0;
            ServerPlayerAttachmentCarrier carrier = state.Chef.GetComponent<ServerPlayerAttachmentCarrier>();
            Vector3 from = AimPosition(state), forward = AimForward(state);

            // The empty pan, pot or mixing bowl the chef faces (not one somebody carries).
            var candidates = new List<GameObject>();
            foreach (ServerCookingHandler handler in Object.FindObjectsOfType<ServerCookingHandler>())
                if (handler != null) candidates.Add(handler.gameObject);
            foreach (ServerMixingHandler handler in Object.FindObjectsOfType<ServerMixingHandler>())
                if (handler != null && !candidates.Contains(handler.gameObject)) candidates.Add(handler.gameObject);
            GameObject pan = null;
            float bestDistance = float.MaxValue;
            bool full = false;
            foreach (GameObject obj in candidates)
            {
                if (obj == null || !obj.activeInHierarchy) continue;
                if (obj.GetComponent<PhysicalAttachment>() == null || obj.GetComponentInParent<PlayerControls>() != null) continue;
                float distance;
                if (!Reaches(state, from, forward, obj.transform.position, BoardReach, FaceArc, out distance) || distance >= bestDistance) continue;
                IIngredientContents contents = obj.RequestInterface<IIngredientContents>();
                if (contents != null && contents.HasContents())
                {
                    full = true;
                    continue;
                }
                pan = obj;
                bestDistance = distance;
            }
            if (pan == null) return state.Refuse(full ? Lang.Both("EMPTY THE PAN FIRST", "ĐỔ CHẢO RA TRƯỚC ĐÃ") : Lang.Both("NO PAN OR POT IN FRONT", "KHÔNG CÓ NỒI/CHẢO TRƯỚC MẶT"));

            // Where it goes: the chef's empty hands, else the nearest free counter.
            bool toHands = carrier != null && carrier.InspectCarriedItem() == null;
            ServerAttachStation counter = null;
            if (!toHands)
            {
                bestDistance = float.MaxValue;
                foreach (ServerAttachStation station in Object.FindObjectsOfType<ServerAttachStation>())
                {
                    if (station == null || !station.gameObject.activeInHierarchy || station.HasItem()) continue;
                    GameObject obj = station.gameObject;
                    if (obj.GetComponent<Workstation>() != null || obj.GetComponent<WashingStation>() != null ||
                        obj.GetComponent<PlateReturnStation>() != null || obj.GetComponent<ServerPlateStation>() != null ||
                        obj.GetComponentInParent<PlayerControls>() != null) continue;
                    if (!station.CanAttachToSelf(pan, new PlacementContext(PlacementContext.Source.Player))) continue;
                    float distance = (obj.transform.position - state.Position).sqrMagnitude;
                    if (distance > 2.5f * 2.5f || distance >= bestDistance) continue;
                    counter = station;
                    bestDistance = distance;
                }
                if (counter == null) return state.Refuse(Lang.Both("NO FREE COUNTER NEARBY", "KHÔNG CÓ BÀN TRỐNG GẦN ĐÂY"));
            }

            GameObject template = EnsureTemplate(pan);
            if (template == null) return state.Refuse(Lang.Both("CAN'T COPY THIS PAN", "KHÔNG CHÉP ĐƯỢC CHẢO NÀY"));
            GameObject copy = NetworkUtils.ServerSpawnPrefab(pan, template, pan.transform.position + Vector3.up * 0.3f, pan.transform.rotation);
            if (copy == null) return state.Refuse(Lang.Both("CAN'T COPY THIS PAN", "KHÔNG CHÉP ĐƯỢC CHẢO NÀY"));
            if (toHands) carrier.CarryItem(copy);
            else counter.AddItem(copy, Vector2.up, new PlacementContext(PlacementContext.Source.Game));
            Vector3 to = toHands ? state.Position : counter.transform.position;
            SpecialFx.BeamAll(pan.transform.position, to, Special.SparePan, 0.6f);
            SpecialFx.BurstAll(to, Special.SparePan);
            SpecialFx.TextAll(pan, Lang.Both("COPIED!", "ĐÃ CHÉP!"), Special.SparePan);
            mine.Spare = EntityIdOf(copy);
            AnySpare = true;
            Plugin.Log.LogInfo("SPARE PAN: đã sao chép " + pan.name + " (mã " + mine.Spare + ")");
            return true;
        }

        /// <summary>
        /// Every machine: a clean, inactive copy of this pan, registered as something the pan can
        /// spawn. Nothing else registers spawnables on a pan, so it is spawnable #0 everywhere.
        /// The host makes it when SPARE PAN is used; a client when the host's spawn arrives.
        /// </summary>
        private static GameObject EnsureTemplate(GameObject pan)
        {
            int key = pan.GetInstanceID();
            GameObject template;
            if (s_templates.TryGetValue(key, out template) && template != null) return template;
            if (s_templateHolder == null)
            {
                s_templateHolder = new GameObject("ChefSkills SparePan templates");
                s_templateHolder.SetActive(false);
            }
            // Made under an inactive parent: no Awake/Start runs on the copy.
            template = Object.Instantiate(pan, s_templateHolder.transform);
            template.name = pan.name;
            Sanitise(template);
            NetworkUtils.RegisterSpawnablePrefab(pan, template);
            s_templates[key] = template;
            return template;
        }

        /// <summary>Strips what the game added to the live pan at runtime, so the copy starts fresh.</summary>
        private static void Sanitise(GameObject template)
        {
            // Clients wrap the pan's meshes in a "... MeshLerper" child (ClientPhysicalAttachment);
            // unwrap it, the copy's own client side makes a new one.
            foreach (MeshLerper lerper in template.GetComponentsInChildren<MeshLerper>(true))
            {
                if (lerper == null) continue;
                Transform wrapper = lerper.transform;
                if (wrapper == template.transform) continue;
                Transform parent = wrapper.parent;
                while (wrapper.childCount > 0) wrapper.GetChild(0).SetParent(parent, true);
                Object.DestroyImmediate(wrapper.gameObject);
            }
            // Cosmetics the client components instantiated ("(Clone)"); they are made again.
            foreach (Transform child in template.GetComponentsInChildren<Transform>(true))
            {
                if (child == null || child == template.transform) continue;
                if (child.name.EndsWith("(Clone)")) Object.DestroyImmediate(child.gameObject);
            }
            // Network components added by the entity registry: the spawn adds fresh ones.
            for (int pass = 0; pass < 3; pass++)
            {
                bool left = false;
                foreach (Component component in template.GetComponentsInChildren<Component>(true))
                {
                    if (component == null) continue;
                    if (component is Synchroniser || component is SpawnableEntityCollection)
                    {
                        Object.DestroyImmediate(component);
                        left = true;
                    }
                }
                if (!left) break;
            }
        }

        /// <summary>
        /// A client is told to spawn something from a pan it has no template for yet: make it now,
        /// before the game looks for the pan's spawner (which the template registration adds).
        /// </summary>
        internal static void BeforeSpawn(SpawnEntityMessage message)
        {
            if (message == null || SpecialNet.IsServer) return;
            GameObject spawner = EntityById((int)message.m_SpawnerHeader.m_uEntityID);
            // The game never spawns anything from a pan or bowl, so a spawn from one is SPARE PAN's.
            // (Not gated on the level state: this client may still be on the skill screen.)
            if (spawner == null || (spawner.GetComponent<CookingHandler>() == null && spawner.GetComponent<MixingHandler>() == null)) return;
            if (spawner.GetComponent<SpawnableEntityCollection>() != null) return;
            EnsureTemplate(spawner);
            Plugin.Log.LogInfo("SPARE PAN: tạo mẫu chảo " + spawner.name + " trên máy này");
        }

        /// <summary>
        /// Server: a pan, pot or bowl just handed its contents on (onto a plate, or a bowl into a
        /// pan or tray). A SPARE PAN copy left empty goes on the next tick.
        /// </summary>
        internal static void SpareEmptied(GameObject container)
        {
            uint id = EntityIdOf(container);
            if (id == 0 || OwnerOfSpare(id) == null || s_spentSpares.Contains(id)) return;
            IIngredientContents contents = container.RequestInterface<IIngredientContents>();
            if (contents != null && contents.HasContents()) return;
            s_spentSpares.Add(id);
        }

        private static ChefState OwnerOfSpare(uint id)
        {
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
                if (Of(chefs[i]).Spare == id) return chefs[i];
            return null;
        }

        /// <summary>Server: removes the emptied SPARE PAN copies, from a chef's hands or a counter.</summary>
        private static void RemoveSpentSpares()
        {
            for (int i = 0; i < s_spentSpares.Count; i++)
            {
                uint id = s_spentSpares[i];
                ChefState owner = OwnerOfSpare(id);
                GameObject copy = EntityById((int)id);
                if (owner == null || copy == null) continue;
                // Filled again in the meantime: it stays until emptied again.
                IIngredientContents contents = copy.RequestInterface<IIngredientContents>();
                if (contents != null && contents.HasContents()) continue;
                Of(owner).Spare = 0;
                Vector3 at = copy.transform.position;
                ServerPlayerAttachmentCarrier carrier = copy.GetComponentInParent<ServerPlayerAttachmentCarrier>();
                ServerAttachStation station = copy.GetComponentInParent<ServerAttachStation>();
                if (carrier != null && carrier.InspectCarriedItem() == copy) carrier.DestroyCarriedItem();
                else
                {
                    if (station != null && station.InspectItem() == copy) station.TakeItem();
                    NetworkUtils.DestroyObject(copy);
                }
                SpecialFx.BurstAll(at, Special.SparePan);
            }
            s_spentSpares.Clear();
        }
    }

    // --- patches ----------------------------------------------------------------------------------

    /// <summary>The host's chop stroke (one per chop animation): SLEEPY, HEX, ZEN, LUCKY KNIFE, then LUCKY CHARM, ECHO KNIFE, MULTITASK.</summary>
    [HarmonyPatch(typeof(ServerWorkstation), "OnChop")]
    internal static class StationsChopPatch
    {
        private static bool Prefix(ServerWorkstation __instance, Transform _interacter, out bool __state)
        {
            __state = false;
            try
            {
                return StationsModule.BeforeChop(__instance, _interacter, out __state);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Stations.BeforeChop", error);
                return true;
            }
        }

        private static void Postfix(ServerWorkstation __instance, Transform _interacter, bool __state)
        {
            if (!__state) return;
            try
            {
                StationsModule.AfterChop(__instance, _interacter);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Stations.AfterChop", error);
            }
        }
    }

    /// <summary>Every machine's own chop stroke for its progress bar: SLEEPY and HEX swallow it there too.</summary>
    [HarmonyPatch(typeof(ClientWorkstation), "OnChop")]
    internal static class StationsClientChopPatch
    {
        private static bool Prefix(ClientWorkstation __instance, Transform _interacter)
        {
            if (!StationsModule.AnyHex && !StationsModule.AnySleepy) return true;
            try
            {
                return StationsModule.BeforeClientChop(__instance, _interacter);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Stations.BeforeClientChop", error);
                return true;
            }
        }
    }

    /// <summary>GERMAPHOBE and TUNNEL VISION on the host: the chef can't use that sink or board.</summary>
    [HarmonyPatch(typeof(ServerInteractable), "CanInteract")]
    internal static class StationsServerCanInteractPatch
    {
        private static void Postfix(ServerInteractable __instance, GameObject _interacter, ref bool __result)
        {
            if (!__result || !StationsModule.Blockers || _interacter == null) return;
            try
            {
                if (StationsModule.Blocked(__instance.gameObject, _interacter, false)) __result = false;
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Stations.ServerCanInteract", error);
            }
        }
    }

    /// <summary>The same on the player's own machine, so it doesn't even try (and says why).</summary>
    [HarmonyPatch(typeof(ClientInteractable), "CanInteract")]
    internal static class StationsClientCanInteractPatch
    {
        private static void Postfix(ClientInteractable __instance, GameObject _interacter, ref bool __result)
        {
            if (!__result || !StationsModule.Blockers || _interacter == null) return;
            try
            {
                if (StationsModule.Blocked(__instance.gameObject, _interacter, true)) __result = false;
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Stations.ClientCanInteract", error);
            }
        }
    }

    /// <summary>TUNNEL VISION: the first board or sink a chef starts working at.</summary>
    [HarmonyPatch(typeof(ServerInteractable), "BeginInteract")]
    internal static class StationsBeginInteractPatch
    {
        private static void Postfix(ServerInteractable __instance, GameObject _interacter)
        {
            if (!StationsModule.Blockers || _interacter == null) return;
            try
            {
                StationsModule.BeganInteract(__instance.gameObject, _interacter);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Stations.BeginInteract", error);
            }
        }
    }

    /// <summary>
    /// DISHWASHER and BUTLER: where the plate of a dish a chef handed in comes back. PLATE
    /// CONJURER: a conjured plate doesn't come back.
    /// </summary>
    [HarmonyPatch(typeof(PlateReturnController), "FoodDelivered")]
    internal static class StationsDeliveryPatch
    {
        private static void Prefix(PlateReturnController __instance, out int __state)
        {
            __state = -1;
            if (!StationsModule.DeliveryHooked) return;
            try
            {
                __state = StationsModule.PendingCount(__instance);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Stations.DeliveryPrefix", error);
            }
        }

        private static void Postfix(PlateReturnController __instance, PlatingStepData _plateType, ServerPlateStation _station, int __state)
        {
            if (__state < 0) return;
            try
            {
                StationsModule.Delivered(__instance, _plateType, _station, __state);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Stations.Delivered", error);
            }
        }
    }

    /// <summary>PREP COOK: an ingredient just taken from a crate (before Throwing's crate patch, which has Low priority).</summary>
    [HarmonyPatch(typeof(ServerPickupItemSpawner), "HandlePickup")]
    internal static class StationsCratePatch
    {
        private static void Postfix(ICarrier _carrier)
        {
            try
            {
                StationsModule.CratePickup(_carrier);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Stations.CratePickup", error);
            }
        }
    }

    /// <summary>SPARE PAN on a client: the pan's copy arrives (pans spawn as physical attachments).</summary>
    [HarmonyPatch(typeof(ClientSynchronisationReceiver), "OnSpawnPhysicalAttachmentMessageReceived")]
    internal static class StationsSpawnPhysicalPatch
    {
        private static void Prefix(Serialisable message)
        {
            try
            {
                SpawnPhysicalAttachmentMessage spawn = message as SpawnPhysicalAttachmentMessage;
                if (spawn != null) StationsModule.BeforeSpawn(spawn.m_SpawnEntityData);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Stations.BeforeSpawnPhysical", error);
            }
        }
    }

    /// <summary>The same for a plain spawn, in case a pan ever comes that way.</summary>
    [HarmonyPatch(typeof(ClientSynchronisationReceiver), "OnSpawnEntityMessageReceived")]
    internal static class StationsSpawnEntityPatch
    {
        private static void Prefix(Serialisable message)
        {
            try
            {
                StationsModule.BeforeSpawn(message as SpawnEntityMessage);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Stations.BeforeSpawnEntity", error);
            }
        }
    }

    /// <summary>
    /// SLEEPY and HEX on the host's sinks: a dozing chef doesn't wash, a hexed chef's first
    /// seconds of washing do nothing. The frame's progress is taken back afterwards (whatever
    /// WashPatch made of it), so the order of the two patches doesn't matter.
    /// </summary>
    [HarmonyPatch(typeof(ServerWashingStation), "UpdateSynchronising")]
    internal static class StationsWashPatch
    {
        // __state: x = the timer before the frame (-1: untouched), y = the share of it that counts.
        private static void Prefix(ServerWashingStation __instance, out Vector2 __state)
        {
            __state = new Vector2(-1f, 1f);
            if (!StationsModule.WashBlockers || !SpecialRuntime.InLevel) return;
            try
            {
                List<GameObject> washers = StationsModule.ServerWashers(__instance);
                if (washers == null || washers.Count == 0) return;
                float share = StationsModule.WashingShare(washers, TimeManager.GetDeltaTime(__instance.gameObject), true);
                if (share < 1f) __state = new Vector2(StationsModule.ServerSinkTimer(__instance), share);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Stations.WashPrefix", error);
            }
        }

        private static void Postfix(ServerWashingStation __instance, Vector2 __state)
        {
            if (__state.x < 0f) return;
            try
            {
                float after = StationsModule.ServerSinkTimer(__instance);
                // Only a frame that washed (not one that finished a plate and reset the timer).
                if (after <= __state.x) return;
                StationsModule.SetServerSinkTimer(__instance, __state.x + (after - __state.x) * __state.y);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Stations.WashPostfix", error);
            }
        }
    }

    /// <summary>The same on every machine's sink bar, which runs the timer itself between the host's messages.</summary>
    [HarmonyPatch(typeof(ClientWashingStation), "UpdateSynchronising")]
    internal static class StationsClientWashPatch
    {
        private static void Prefix(ClientWashingStation __instance, out Vector2 __state)
        {
            __state = new Vector2(-1f, 1f);
            if (!StationsModule.WashBlockers || !SpecialRuntime.InLevel) return;
            try
            {
                List<GameObject> washers = StationsModule.ClientWashers(__instance);
                if (washers == null || washers.Count == 0) return;
                float share = StationsModule.WashingShare(washers, TimeManager.GetDeltaTime(__instance.gameObject), false);
                if (share < 1f) __state = new Vector2(StationsModule.ClientSinkTimerOf(__instance), share);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Stations.ClientWashPrefix", error);
            }
        }

        private static void Postfix(ClientWashingStation __instance, Vector2 __state)
        {
            if (__state.x < 0f) return;
            try
            {
                float after = StationsModule.ClientSinkTimerOf(__instance);
                if (after <= __state.x) return;
                StationsModule.SetClientSinkTimer(__instance, __state.x + (after - __state.x) * __state.y);
                // The game (and ClientWashPatch) drew this frame's bar from the timer before this.
                StationsModule.RedrawSink(__instance);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Stations.ClientWashPostfix", error);
            }
        }
    }

    /// <summary>SPARE PAN: a pan or pot handed its food on; an emptied copy goes.</summary>
    [HarmonyPatch(typeof(ServerCookableContainer), "TransferToContainer")]
    internal static class StationsSparePanPatch
    {
        private static void Postfix(ServerCookableContainer __instance)
        {
            if (!StationsModule.AnySpare || __instance == null) return;
            try
            {
                StationsModule.SpareEmptied(__instance.gameObject);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Stations.SparePanEmptied", error);
            }
        }
    }

    /// <summary>SPARE PAN: the same for a mixing bowl.</summary>
    [HarmonyPatch(typeof(ServerMixableContainer), "TransferToContainer")]
    internal static class StationsSpareBowlPatch
    {
        private static void Postfix(ServerMixableContainer __instance)
        {
            if (!StationsModule.AnySpare || __instance == null) return;
            try
            {
                StationsModule.SpareEmptied(__instance.gameObject);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Stations.SpareBowlEmptied", error);
            }
        }
    }
}
