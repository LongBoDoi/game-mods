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
    /// </summary>
    internal sealed class StationsModule : ISpecialModule
    {
        // --- tuning -------------------------------------------------------------------------------
        private const float MultitaskRate = 0.25f;
        private const float LuckyKnifeChance = 0.10f;
        private const float LuckyCharmChance = 0.15f;
        // LUCKY CHARM on a sink: washing has no strokes, so every this many seconds of scrubbing
        // counts as one stroke that may count double.
        private const float ScrubSeconds = 0.5f;
        private const float PrepCookSeconds = 20f;
        private const float InspiringRange = 3f;
        private const float InspiringPercent = 20f;
        private const float ZenStillSeconds = 1.5f;
        private const float ZenStillSpeed = 0.5f;
        private const int HexStrokes = 5;
        private const float BoardReach = 1.8f;
        private const float DashReach = 1.7f;
        private const float SinkReach = 2.2f;
        private const float FaceArc = 75f;
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
            public ServerWorkstation EchoCurrent, EchoOther;
            public float PrepReadyAt;
            public int TunnelMode;
            public int Dishes;
            public ClientPlayerControlsImpl_Default Impl;
            public readonly HashSet<int> DashHits = new HashSet<int>();
            public float ChopInterval;
            public string Status = "";
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

        // --- per-level state ----------------------------------------------------------------------

        private static readonly Dictionary<ServerWorkstation, BoardJob> s_multitask = new Dictionary<ServerWorkstation, BoardJob>();
        private static readonly List<ServerWorkstation> s_deadJobs = new List<ServerWorkstation>();
        private static readonly List<ButlerPlate> s_butler = new List<ButlerPlate>();
        private static readonly Dictionary<ServerWashingStation, float> s_scrub = new Dictionary<ServerWashingStation, float>();
        private static readonly Dictionary<ServerWashingStation, float> s_sinkSyncAt = new Dictionary<ServerWashingStation, float>();
        private static readonly Dictionary<int, GameObject> s_templates = new Dictionary<int, GameObject>();
        private static ServerWorkstation[] s_boards = new ServerWorkstation[0];
        private static ServerWashingStation[] s_sinks = new ServerWashingStation[0];
        private static float s_nextScan, s_nextFlags, s_nextStatus;
        private static GameObject s_templateHolder;

        // Read by the per-frame patches, refreshed twice a second.
        internal static bool Blockers;
        internal static bool AnyHex;
        internal static bool AnyDelivery;

        // --- setup --------------------------------------------------------------------------------

        public void Register()
        {
            SpecialRuntime.Tick += OnTick;
            SpecialRuntime.LevelStarted += Reset;
            SpecialRuntime.LevelEnded += Reset;
            SpecialRuntime.LevelEnded += ForgetTemplates;
            SpecialRuntime.LevelBonusProviders.Add(InspiringBonus);

            SpecialRuntime.RegisterActive(Special.PowerChop, new ActiveHandler { Execute = PowerChop });
            SpecialRuntime.RegisterActive(Special.WashBurst, new ActiveHandler { Execute = WashBurst });
            SpecialRuntime.RegisterActive(Special.PlateConjurer, new ActiveHandler { Execute = PlateConjurer });
            SpecialRuntime.RegisterActive(Special.SparePan, new ActiveHandler { Execute = SparePan });
            SpecialRuntime.RegisterActive(Special.Hex, new ActiveHandler { Execute = Hex });

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
            s_butler.Clear();
            s_scrub.Clear();
            s_sinkSyncAt.Clear();
            s_boards = new ServerWorkstation[0];
            s_sinks = new ServerWashingStation[0];
            s_nextScan = s_nextFlags = s_nextStatus = 0f;
            Blockers = AnyHex = AnyDelivery = false;
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

        // --- every frame --------------------------------------------------------------------------

        private static void OnTick(float dt)
        {
            if (!SpecialRuntime.InLevel) return;
            if (Now >= s_nextFlags)
            {
                s_nextFlags = Now + 0.5f;
                Blockers = SpecialRuntime.AnyHas(Special.Germaphobe) || SpecialRuntime.AnyHas(Special.TunnelVision);
                AnyDelivery = SpecialRuntime.AnyHas(Special.Dishwasher) || SpecialRuntime.AnyHas(Special.Butler);
            }
            if (Now >= s_nextStatus)
            {
                s_nextStatus = Now + 0.25f;
                UpdateStatus();
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
            if (multitask && s_multitask.Count > 0) MultitaskBoards(dt);
            if (dash) DashChopTick();
            if (multitask || zen || charm) SinkTick(dt, multitask);
            if (s_butler.Count > 0) ButlerTick(dt);
        }

        /// <summary>The text under a local chef's active: ZEN ready, HEXED, PREP COOK, TUNNEL VISION.</summary>
        private static void UpdateStatus()
        {
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState chef = chefs[i];
                if (!chef.Local) continue;
                Mine mine = Of(chef);
                string text = "";
                if (chef.Has(Special.Zen) && mine.ZenCharged) text = Join(text, "ZEN READY");
                if (mine.HexClient > 0) text = Join(text, "HEXED x" + mine.HexClient);
                if (chef.Has(Special.PrepCook))
                    text = Join(text, Now >= mine.PrepReadyAt ? "PREP COOK READY" : "PREP COOK " + Mathf.CeilToInt(mine.PrepReadyAt - Now) + "s");
                if (chef.Has(Special.TunnelVision) && mine.TunnelMode != 0)
                    text = Join(text, mine.TunnelMode == ModeChop ? "CHOP ONLY" : "WASH ONLY");
                if (text == mine.Status) continue;
                // Each module has its own line.
                chef.SetStatus("Stations", text);
                mine.Status = text;
            }
        }

        private static string Join(string a, string b)
        {
            return a.Length > 0 ? a + " / " + b : b;
        }

        // --- facing helpers -----------------------------------------------------------------------

        private static bool Faces(ChefState chef, Vector3 target, float range, float arc, out float distance)
        {
            Vector3 offset = target - chef.Position;
            offset.y = 0f;
            distance = offset.magnitude;
            if (distance > range) return false;
            if (distance < 0.05f) return true;
            Vector3 forward = chef.Forward;
            forward.y = 0f;
            return Vector3.Angle(forward, offset) <= arc;
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

        /// <summary>The loaded board (unfinished item) the chef faces, nearest first.</summary>
        private static ServerWorkstation FacedBoard(ChefState chef, IEnumerable<ServerWorkstation> boards, float range)
        {
            ServerWorkstation best = null;
            float bestDistance = float.MaxValue;
            foreach (ServerWorkstation board in boards)
            {
                if (board == null || !board.gameObject.activeInHierarchy || ItemOn(board) == null) continue;
                float distance;
                if (!Faces(chef, board.transform.position, range, FaceArc, out distance)) continue;
                if (distance < bestDistance)
                {
                    best = board;
                    bestDistance = distance;
                }
            }
            return best;
        }

        // --- chopping -----------------------------------------------------------------------------

        /// <summary>Server: finishes the item on a board at once (the host replaces it everywhere).</summary>
        private static void Finish(ServerWorkstation board, ServerWorkableItem item, ChefState chef)
        {
            ServerAttachStation station = BoardStation != null ? BoardStation.GetValue(board) as ServerAttachStation : null;
            if (station == null || item == null || item.HasFinished()) return;
            // The (station, worker, progress) overload clamps to the last stage and swaps the item;
            // ChopPatch is on the other overload, so no skill multiplier is involved.
            item.DoWork(station, chef.Chef, 99);
        }

        /// <summary>
        /// Server: one more chop stroke by this chef (through ChopPatch, so their CHOPPING counts),
        /// mirrored on every machine's progress bar.
        /// </summary>
        private static void ExtraStroke(ServerWorkstation board, ServerWorkableItem item, ChefState chef, ushort topic)
        {
            ServerAttachStation station = BoardStation != null ? BoardStation.GetValue(board) as ServerAttachStation : null;
            if (station == null || item == null || item.HasFinished() || chef.Chef == null) return;
            uint id = EntityIdOf(item.gameObject);
            item.DoWork(station, chef.Chef);
            if (item.HasFinished() || id == 0) return; // the clients follow the swap
            SpecialNet.SendToAll(new SpecialMessage { Topic = topic, Chef = chef.Id, I = (int)id, A = 1f });
            // A machine without our channel only learns the host's absolute progress.
            if (PartialLobby && WorkableSync != null) WorkableSync.Invoke(item, null);
        }

        /// <summary>Every machine: the host made extra strokes; add them to this machine's bar.</summary>
        private static void OnStrokes(SpecialMessage message)
        {
            GameObject obj = EntityById(message.I);
            ClientWorkableItem item = obj != null ? obj.GetComponent<ClientWorkableItem>() : null;
            if (item == null) return;
            ChefState chef = SpecialRuntime.StateById(message.Chef);
            GameObject worker = chef != null ? chef.Chef : null;
            ClientAttachStation station = item.GetComponentInParent<ClientAttachStation>();
            int strokes = Mathf.Max(1, Mathf.RoundToInt(message.A));
            for (int i = 0; i < strokes; i++)
            {
                if (item == null || item.HasFinished()) break;
                // Through ClientChopPatch, the same way the host's stroke went through ChopPatch.
                item.DoWork(station, worker);
            }
        }

        /// <summary>
        /// Server, a chef's chop stroke at a board: HEX swallows it, ZEN and LUCKY KNIFE finish the
        /// item. Returns false to skip the stroke; <paramref name="normal"/> when it went ahead.
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
            if (mine.HexServer > 0)
            {
                mine.HexServer--;
                return false;
            }
            if (mine.ZenCharged && chef.Has(Special.Zen))
            {
                SetZen(chef, false);
                Finish(board, item, chef);
                return false;
            }
            if (chef.Has(Special.LuckyKnife) && UnityEngine.Random.value < LuckyKnifeChance)
            {
                Finish(board, item, chef);
                return false;
            }
            normal = true;
            return true;
        }

        /// <summary>Server, after a normal stroke: LUCKY CHARM, ECHO KNIFE, and MULTITASK bookkeeping.</summary>
        internal static void AfterChop(ServerWorkstation board, Transform interacter)
        {
            ChefState chef = SpecialRuntime.StateOf(interacter.gameObject);
            if (chef == null) return;
            Mine mine = Of(chef);

            if (chef.Has(Special.LuckyCharm) && UnityEngine.Random.value < LuckyCharmChance)
                ExtraStroke(board, ItemOn(board), chef, TopicCharmChop);

            if (chef.Has(Special.EchoKnife))
            {
                if (mine.EchoCurrent != board)
                {
                    if (mine.EchoCurrent != null) mine.EchoOther = mine.EchoCurrent;
                    mine.EchoCurrent = board;
                }
                ServerWorkstation other = mine.EchoOther;
                if (other != null && other != board)
                {
                    ServerWorkableItem otherItem = ItemOn(other);
                    if (otherItem != null) ExtraStroke(other, otherItem, chef, TopicEcho);
                }
            }

            if (chef.Has(Special.Multitask))
            {
                ServerWorkableItem item = ItemOn(board);
                if (item == null) return;
                BoardJob job;
                if (!s_multitask.TryGetValue(board, out job) || job.Item != item)
                    s_multitask[board] = new BoardJob { Chef = chef, Item = item };
                else job.Chef = chef;
            }
        }

        /// <summary>Every machine, a chop stroke on its own progress bar: HEX swallows it here too.</summary>
        internal static bool BeforeClientChop(ClientWorkstation board, Transform interacter)
        {
            if (!AnyHex || interacter == null || ClientBoardItem == null) return true;
            ChefState chef = SpecialRuntime.StateOf(interacter.gameObject);
            if (chef == null) return true;
            Mine mine = Of(chef);
            if (mine.HexClient <= 0 || ClientBoardItem.GetValue(board) == null) return true;
            mine.HexClient--;
            return false;
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

        /// <summary>Server: MULTITASK boards nobody is at keep chopping at a quarter of the chef's rhythm.</summary>
        private static void MultitaskBoards(float dt)
        {
            s_deadJobs.Clear();
            foreach (KeyValuePair<ServerWorkstation, BoardJob> pair in s_multitask)
            {
                ServerWorkstation board = pair.Key;
                BoardJob job = pair.Value;
                ServerWorkableItem item = board != null ? ItemOn(board) : null;
                if (item == null || item != job.Item || job.Chef.Chef == null || !job.Chef.Has(Special.Multitask))
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
                ExtraStroke(board, item, job.Chef, TopicMultitask);
            }
            for (int i = 0; i < s_deadJobs.Count; i++) s_multitask.Remove(s_deadJobs[i]);
        }

        /// <summary>Server: DASH-CHOP gives each loaded board the dashing chef faces one stroke per dash.</summary>
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
                if (mine.Impl == null || !mine.Impl.IsDashing())
                {
                    if (mine.DashHits.Count > 0) mine.DashHits.Clear();
                    continue;
                }
                for (int b = 0; b < s_boards.Length; b++)
                {
                    ServerWorkstation board = s_boards[b];
                    ServerWorkableItem item = ItemOn(board);
                    if (item == null || !board.gameObject.activeInHierarchy) continue;
                    float distance;
                    if (!Faces(chef, board.transform.position, DashReach, FaceArc, out distance)) continue;
                    if (!mine.DashHits.Add(board.GetInstanceID())) continue;
                    ExtraStroke(board, item, chef, TopicDash);
                }
            }
        }

        // --- ZEN ----------------------------------------------------------------------------------

        /// <summary>Server: a ZEN chef standing still (and not working) for 1.5 s charges up.</summary>
        private static void ZenTick(float dt)
        {
            if (dt <= 0f) return;
            IList<ChefState> chefs = SpecialRuntime.Chefs;
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
                // Chopping or washing is standing still too, but doesn't count as calm.
                bool working = Now - mine.LastWork < 0.8f;
                mine.Still = still && !working ? mine.Still + dt : 0f;
                if (!mine.ZenCharged && mine.Still >= ZenStillSeconds) SetZen(chef, true);
            }
        }

        private static void SetZen(ChefState chef, bool charged)
        {
            Of(chef).ZenCharged = charged;
            // Every machine shows it (the player's own HUD line); the host's value is the real one.
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
                if (sink == null) continue;
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
                    // A plate this finishes is cleaned by the host's message; otherwise tell the bars.
                    if (timer <= config.m_cleanPlateTime) MirrorWashBonus(sink, bonus);
                }
            }
            s_scrub[sink] = scrub;
        }

        private static void MirrorWashBonus(ServerWashingStation sink, float bonus)
        {
            uint id = EntityIdOf(sink.gameObject);
            if (id != 0) SpecialNet.SendToAll(new SpecialMessage { Topic = TopicCharmWash, I = (int)id, A = bonus });
            if (PartialLobby && SinkSync != null) SinkSync.Invoke(sink, new object[] { true });
        }

        /// <summary>Every machine: add the host's LUCKY CHARM bonus to this machine's sink timer.</summary>
        private static void OnWashBonus(SpecialMessage message)
        {
            GameObject obj = EntityById(message.I);
            ClientWashingStation sink = obj != null ? obj.GetComponent<ClientWashingStation>() : null;
            if (sink == null || ClientSinkTimer == null) return;
            float timer = (float)ClientSinkTimer.GetValue(sink) + message.A;
            ClientSinkTimer.SetValue(sink, timer);
            var ui = ClientSinkUi != null ? ClientSinkUi.GetValue(sink) as ProgressUIController : null;
            var config = ClientSinkConfig != null ? ClientSinkConfig.GetValue(sink) as WashingStation : null;
            if (ui != null && config != null && config.m_cleanPlateTime > 0f)
                ui.SetProgress(Mathf.Clamp01(timer / config.m_cleanPlateTime));
        }

        /// <summary>Server: MULTITASK, the last washer walked away; keep washing at a quarter of their rate.</summary>
        private static void LeftAlone(ServerWashingStation sink, WashingStation config, ServerInteractable interactable,
            int plates, float dt)
        {
            GameObject last = SinkInteractor != null ? SinkInteractor.GetValue(sink) as GameObject : null;
            ChefState chef = SpecialRuntime.StateOf(last);
            if (chef == null || !chef.Has(Special.Multitask) || interactable == null || !interactable.enabled) return;
            ServerPlateReturnStation rack = SinkRack != null ? SinkRack.GetValue(sink) as ServerPlateReturnStation : null;
            // A full or blocked drying rack stops the sink, as it does for a washing chef.
            if (rack == null || !rack.CanReturnPlate()) return;

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
            if (SinkSync != null && (!s_sinkSyncAt.TryGetValue(sink, out next) || Now >= next))
            {
                s_sinkSyncAt[sink] = Now + 0.25f;
                SinkSync.Invoke(sink, new object[] { false });
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
        /// Server, a dish was just handed in by a chef: DISHWASHER sends every second plate to a
        /// sink's drying rack (clean), BUTLER sends the dirty plate into a sink.
        /// </summary>
        internal static void Delivered(PlateReturnController controller, PlatingStepData plateType, int before)
        {
            ChefState chef = SpecialRuntime.StateOf(LevelState.CurrentPlacer);
            if (chef == null) return;
            bool dishwasher = chef.Has(Special.Dishwasher), butler = chef.Has(Special.Butler);
            if (!dishwasher && !butler) return;
            object list = PendingPlates.GetValue(controller);
            if (!ReadListTypes(list)) return;
            int count = (int)s_listCount.GetValue(list, null);
            if (count != before + 1) return; // no plate was queued for this dish
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
                if (mine.Dishes % 2 == 0 && rack != null)
                {
                    // Same delay, but it comes back on the drying rack: a clean plate.
                    s_entryStation.SetValue(entry, rack);
                    return;
                }
            }
            if (butler && SinkAddPlates != null)
            {
                float timer = (float)s_entryTimer.GetValue(entry);
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
        }

        private static void SetPrep(ChefState chef, float seconds)
        {
            if (chef.Id == 0)
            {
                Of(chef).PrepReadyAt = Now + seconds;
                return;
            }
            SpecialNet.SendToAll(new SpecialMessage { Topic = TopicPrep, Chef = chef.Id, A = seconds });
        }

        private static void OnPrep(SpecialMessage message)
        {
            ChefState chef = SpecialRuntime.StateById(message.Chef);
            if (chef != null) Of(chef).PrepReadyAt = Now + message.A;
        }

        // --- INSPIRING ----------------------------------------------------------------------------

        /// <summary>
        /// Every machine: chopping and washing levels for a chef near an INSPIRING teammate, worth
        /// about +20% (whole levels). Never stacks; LONE WOLF chefs ignore it.
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
                Vector3 offset = other.Position - chef.Position;
                offset.y = 0f;
                if (offset.magnitude > InspiringRange) continue;
                float perLevel = (SkillStats.Multiplier(skill, 1) - 1f) * 100f;
                return perLevel > 0.5f ? Mathf.Max(1, Mathf.RoundToInt(InspiringPercent / perLevel)) : 0;
            }
            return 0;
        }

        // --- GERMAPHOBE and TUNNEL VISION ---------------------------------------------------------

        /// <summary>Every machine: may this chef work at this board or sink?</summary>
        internal static bool Blocked(GameObject station, GameObject interacter)
        {
            ChefState chef = SpecialRuntime.StateOf(interacter);
            if (chef == null) return false;
            bool germaphobe = chef.Has(Special.Germaphobe), tunnel = chef.Has(Special.TunnelVision);
            if (!germaphobe && !tunnel) return false;
            int mode = tunnel ? Of(chef).TunnelMode : 0;
            if (station.GetComponent<WashingStation>() != null) return germaphobe || mode == ModeChop;
            return mode == ModeWash && station.GetComponent<Workstation>() != null;
        }

        /// <summary>Server: a TUNNEL VISION chef's first board or sink decides what they can do.</summary>
        internal static void BeganInteract(GameObject station, GameObject interacter)
        {
            ChefState chef = SpecialRuntime.StateOf(interacter);
            if (chef == null || !chef.Has(Special.TunnelVision) || Of(chef).TunnelMode != 0) return;
            int mode = station.GetComponent<Workstation>() != null ? ModeChop
                : station.GetComponent<WashingStation>() != null ? ModeWash : 0;
            if (mode == 0) return;
            Of(chef).TunnelMode = mode;
            if (chef.Id != 0)
                SpecialNet.SendToAll(new SpecialMessage { Topic = TopicTunnel, Chef = chef.Id, I = mode });
        }

        private static void OnTunnel(SpecialMessage message)
        {
            ChefState chef = SpecialRuntime.StateById(message.Chef);
            if (chef != null && Of(chef).TunnelMode == 0) Of(chef).TunnelMode = message.I;
        }

        // --- HEX ----------------------------------------------------------------------------------

        private static bool Hex(ChefState state)
        {
            // The chef faced, a rival before a teammate.
            ChefState target = null;
            float best = float.MaxValue;
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState other = chefs[i];
                if (other == state || other.Chef == null) continue;
                float distance;
                if (!Faces(state, other.Position, 3f, 60f, out distance)) continue;
                float score = distance + (other.Team == state.Team ? 10f : 0f);
                if (score < best)
                {
                    best = score;
                    target = other;
                }
            }
            if (target == null) return false;
            if (target.Team == state.Team && target.Has(Special.LoneWolf)) return false;
            if (target.Id == 0) ApplyHex(target, HexStrokes);
            else SpecialNet.SendToAll(new SpecialMessage { Topic = TopicHex, Chef = target.Id, I = HexStrokes, D = state.Id });
            return true;
        }

        private static void ApplyHex(ChefState target, int strokes)
        {
            Mine mine = Of(target);
            // Separate counts for the host's real chopping and each machine's own progress bar
            // (the host runs both for the same stroke).
            mine.HexServer = strokes;
            mine.HexClient = strokes;
            AnyHex = true;
        }

        private static void OnHex(SpecialMessage message)
        {
            ChefState target = SpecialRuntime.StateById(message.Chef);
            if (target != null) ApplyHex(target, message.I);
        }

        // --- actives ------------------------------------------------------------------------------

        private static bool PowerChop(ChefState state)
        {
            ServerWorkstation board = FacedBoard(state, Object.FindObjectsOfType<ServerWorkstation>(), BoardReach);
            if (board == null) return false;
            Finish(board, ItemOn(board), state);
            return true;
        }

        private static bool WashBurst(ChefState state)
        {
            if (state.Has(Special.Germaphobe) || Of(state).TunnelMode == ModeChop || SinkPlates == null) return false;
            ServerWashingStation best = null;
            float bestDistance = float.MaxValue;
            foreach (ServerWashingStation sink in Object.FindObjectsOfType<ServerWashingStation>())
            {
                if (sink == null || !sink.gameObject.activeInHierarchy || (int)SinkPlates.GetValue(sink) <= 0) continue;
                ServerPlateReturnStation rack = SinkRack != null ? SinkRack.GetValue(sink) as ServerPlateReturnStation : null;
                if (rack == null || !rack.CanReturnPlate()) continue;
                float distance;
                if (!Faces(state, sink.transform.position, SinkReach, FaceArc, out distance) || distance >= bestDistance) continue;
                best = sink;
                bestDistance = distance;
            }
            if (best == null) return false;
            // The game's own "wash the whole stack" (clean plates onto the rack, clients told).
            best.WashAllPlates();
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
            if (state.Chef == null || RackStack == null || RackConfig == null) return false;
            ServerPlayerAttachmentCarrier carrier = state.Chef.GetComponent<ServerPlayerAttachmentCarrier>();
            if (carrier == null || carrier.InspectCarriedItem() != null) return false;

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
            if (best == null) return false;
            best.ReturnPlate();
            ServerCleanPlateStack stack = RackStack.GetValue(best) as ServerCleanPlateStack;
            if (stack == null) return true; // the plate is on the rack at least
            Vector3 forward = state.Forward;
            stack.HandlePickup(carrier, new Vector2(forward.x, forward.z).normalized);
            return true;
        }

        // --- SPARE PAN ----------------------------------------------------------------------------

        private static bool SparePan(ChefState state)
        {
            if (state.Chef == null || PartialLobby) return false; // every machine must make the same template
            ServerPlayerAttachmentCarrier carrier = state.Chef.GetComponent<ServerPlayerAttachmentCarrier>();

            // The empty pan or pot the chef faces (not one somebody carries).
            GameObject pan = null;
            float bestDistance = float.MaxValue;
            foreach (ServerCookingHandler handler in Object.FindObjectsOfType<ServerCookingHandler>())
            {
                if (handler == null || !handler.gameObject.activeInHierarchy) continue;
                GameObject obj = handler.gameObject;
                if (obj.GetComponent<PhysicalAttachment>() == null || obj.GetComponentInParent<PlayerControls>() != null) continue;
                IIngredientContents contents = obj.RequestInterface<IIngredientContents>();
                if (contents != null && contents.HasContents()) continue;
                float distance;
                if (!Faces(state, obj.transform.position, BoardReach, FaceArc, out distance) || distance >= bestDistance) continue;
                pan = obj;
                bestDistance = distance;
            }
            if (pan == null) return false;

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
                if (counter == null) return false;
            }

            GameObject template = EnsureTemplate(pan);
            if (template == null) return false;
            GameObject copy = NetworkUtils.ServerSpawnPrefab(pan, template, pan.transform.position + Vector3.up * 0.3f, pan.transform.rotation);
            if (copy == null) return false;
            if (toHands) carrier.CarryItem(copy);
            else counter.AddItem(copy, Vector2.up, new PlacementContext(PlacementContext.Source.Game));
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
            // The game never spawns anything from a pan, so a spawn from one is SPARE PAN's.
            // (Not gated on the level state: this client may still be on the skill screen.)
            if (spawner == null || spawner.GetComponent<CookingHandler>() == null) return;
            if (spawner.GetComponent<SpawnableEntityCollection>() != null) return;
            EnsureTemplate(spawner);
        }
    }

    // --- patches ----------------------------------------------------------------------------------

    /// <summary>The host's chop stroke (one per chop animation): HEX, ZEN, LUCKY KNIFE, then LUCKY CHARM, ECHO KNIFE, MULTITASK.</summary>
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

    /// <summary>Every machine's own chop stroke for its progress bar: HEX swallows it there too.</summary>
    [HarmonyPatch(typeof(ClientWorkstation), "OnChop")]
    internal static class StationsClientChopPatch
    {
        private static bool Prefix(ClientWorkstation __instance, Transform _interacter)
        {
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
                if (StationsModule.Blocked(__instance.gameObject, _interacter)) __result = false;
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Stations.ServerCanInteract", error);
            }
        }
    }

    /// <summary>The same on the player's own machine, so it doesn't even try.</summary>
    [HarmonyPatch(typeof(ClientInteractable), "CanInteract")]
    internal static class StationsClientCanInteractPatch
    {
        private static void Postfix(ClientInteractable __instance, GameObject _interacter, ref bool __result)
        {
            if (!__result || !StationsModule.Blockers || _interacter == null) return;
            try
            {
                if (StationsModule.Blocked(__instance.gameObject, _interacter)) __result = false;
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

    /// <summary>DISHWASHER and BUTLER: where the plate of a dish a chef handed in comes back.</summary>
    [HarmonyPatch(typeof(PlateReturnController), "FoodDelivered")]
    internal static class StationsDeliveryPatch
    {
        private static void Prefix(PlateReturnController __instance, out int __state)
        {
            __state = -1;
            if (!StationsModule.AnyDelivery || LevelState.CurrentPlacer == null) return;
            try
            {
                __state = StationsModule.PendingCount(__instance);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Stations.DeliveryPrefix", error);
            }
        }

        private static void Postfix(PlateReturnController __instance, PlatingStepData _plateType, int __state)
        {
            if (__state < 0) return;
            try
            {
                StationsModule.Delivered(__instance, _plateType, __state);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Stations.Delivered", error);
            }
        }
    }

    /// <summary>PREP COOK: an ingredient just taken from a crate.</summary>
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
}
