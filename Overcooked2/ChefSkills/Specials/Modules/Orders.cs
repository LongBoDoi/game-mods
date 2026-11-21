using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using OrderController;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace ChefSkills.Specials
{
    /// <summary>
    /// Orders and score: tips, order timers, the order list and delivery streaks.
    ///
    /// The host decides every score change inside the game's own delivery and expiry paths (so the
    /// KitchenFlowMessage it sends already carries the changed score and tip), and adds/removes
    /// orders through the server order controller. The few things the game doesn't sync (an order
    /// taken off the list, the fortune-teller preview, the bounty, the hot streak) go over
    /// SpecialNet.
    /// </summary>
    internal sealed class OrdersModule : ISpecialModule
    {
        // --- tuning -----------------------------------------------------------------------------
        internal const float CharmerTip = 1.5f;
        internal const float MaitreDLifetime = 1.15f;
        internal const float ClutchWindow = 0.15f;
        internal const float FreshSeconds = 15f;
        internal const float FreshTip = 1.4f;
        internal const float CloseEnoughCooldown = 60f;
        internal const float GoldenHourSeconds = 30f;
        internal const float CoffeeSpeed = 0.2f;
        internal const float CoffeeSeconds = 5f;
        internal const float PressurePerOrder = 0.05f;
        internal const int PressureMaxOrders = 5;
        internal const float StreakStep = 0.03f;
        internal const int StreakMax = 5;
        internal const float DecoySeconds = 15f;
        internal const float PoseSeconds = 1f;
        internal const float TimeOutSeconds = 6f;
        // Reroll: the new order is added a moment after the removal reached the clients, so a
        // full order bar never has one widget too many.
        internal const float RerollAddDelay = 0.5f;
        // The game sends 6 bits of tip; the score itself carries the full amount.
        private const int MaxTipShown = 63;

        // --- topics -----------------------------------------------------------------------------
        private static readonly ushort TopicRemoveOrder = (ushort)((int)Special.Reroll * 10 + 1);
        private static readonly ushort TopicFortune = (ushort)((int)Special.FortuneTeller * 10 + 1);
        private static readonly ushort TopicBounty = (ushort)((int)Special.BountyHunter * 10 + 1);
        private static readonly ushort TopicStreak = (ushort)((int)Special.HotStreak * 10 + 1);

        // --- private game members ---------------------------------------------------------------
        internal static readonly FieldInfo ServerActiveOrders = AccessTools.Field(typeof(ServerOrderControllerBase), "m_activeOrders");
        internal static readonly FieldInfo ServerRoundData = AccessTools.Field(typeof(ServerOrderControllerBase), "m_roundData");
        internal static readonly FieldInfo ServerRoundInstance = AccessTools.Field(typeof(ServerOrderControllerBase), "m_roundInstanceData");
        internal static readonly FieldInfo ServerMaxOrders = AccessTools.Field(typeof(ServerOrderControllerBase), "m_maxOrdersAllowed");
        internal static readonly MethodInfo ServerAddEntry =
            AccessTools.Method(typeof(ServerOrderControllerBase), "AddNewOrder", new Type[] { typeof(RecipeList.Entry) });
        internal static readonly FieldInfo ClientActiveOrders = AccessTools.Field(typeof(ClientOrderControllerBase), "m_activeOrders");
        internal static readonly FieldInfo ClientGui = AccessTools.Field(typeof(ClientOrderControllerBase), "m_gui");
        internal static readonly FieldInfo StationPlate = AccessTools.Field(typeof(ServerPlateStation), "m_plate");
        internal static readonly FieldInfo TimerLimit = AccessTools.Field(typeof(ServerRoundTimer), "m_timeLimit");
        // ClientOrderControllerBase.ActiveOrder is a protected nested class.
        private static readonly Type ActiveOrderType = AccessTools.Inner(typeof(ClientOrderControllerBase), "ActiveOrder");
        private static readonly FieldInfo ActiveOrderId = ActiveOrderType != null ? AccessTools.Field(ActiveOrderType, "ID") : null;
        private static readonly FieldInfo ActiveOrderEntry = ActiveOrderType != null ? AccessTools.Field(ActiveOrderType, "RecipeListEntry") : null;
        private static readonly FieldInfo ActiveOrderToken = ActiveOrderType != null ? AccessTools.Field(ActiveOrderType, "UIToken") : null;

        // --- level state (cleared with the level) -----------------------------------------------
        private static ServerKitchenFlowControllerBase s_serverFlow;
        private static ClientKitchenFlowControllerBase s_clientFlow;
        private static bool s_versus;
        private static float s_nextFind;

        // Per team (index 0 = team one / campaign, 1 = team two).
        private static readonly int[] s_waiting = new int[2];
        private static readonly int[] s_streak = new int[2];
        private static readonly bool[] s_secondChanceUsed = new bool[2];
        private static readonly bool[] s_comboKeeperUsed = new bool[2];
        private static readonly float[] s_closeEnoughReadyAt = new float[2];
        private static readonly RecipeList.Entry[] s_fortuneNext = new RecipeList.Entry[2];
        private static readonly string[] s_fortuneName = new string[2];
        private static readonly uint[] s_bountyServer = new uint[2];
        private static readonly int[] s_bountyShown = new int[2];
        private static readonly List<Decoy>[] s_decoys = { new List<Decoy>(), new List<Decoy>() };
        private static readonly List<PendingAdd> s_pendingAdds = new List<PendingAdd>();

        // Fresh Delivery (host): cooking handler -> when its food finished; plate -> the earliest
        // finish time of the cooked food put on it.
        internal static readonly Dictionary<int, float> CookedAt = new Dictionary<int, float>();
        internal static readonly Dictionary<int, float> PlateCookedAt = new Dictionary<int, float>();

        // The delivery being handled right now on the host.
        internal static bool CtxActive;
        internal static bool CtxSuccess;
        internal static bool CtxDecoy;
        internal static ChefState CtxPlacer;
        internal static float CtxCookedAt = -1f;
        internal static int LastBaseScore;

        // The expiry being handled right now on the host (score before the game's deduction).
        internal static bool ExpiryActive;
        internal static int ExpiryDeductions, ExpiryMultiplier, ExpiryCombo;
        internal static bool ExpiryMaintained;

        // A decoy being added: its lifetime, and no Fortune Teller swap.
        internal static float LifetimeOverride = -1f;
        internal static bool AddingDecoy;

        private static float s_nextBountyCheck, s_nextBountySend, s_nextStatus;

        private struct Decoy
        {
            public uint Id;
            public float Until;
            public bool Expired;
        }

        private struct PendingAdd
        {
            public ServerOrderControllerBase Controller;
            public float At;
        }

        public void Register()
        {
            SpecialRuntime.LevelStarted += OnLevelStarted;
            SpecialRuntime.LevelEnded += Reset;
            SpecialRuntime.Tick += OnTick;

            SpecialRuntime.RunSpeedModifiers.Add(RunSpeed);
            SpecialRuntime.DashSpeedModifiers.Add(DashSpeed);
            SpecialRuntime.LevelBonusProviders.Add(SkillLevels);

            SpecialNet.Register(TopicRemoveOrder, OnRemoveOrder);
            SpecialNet.Register(TopicFortune, OnFortune);
            SpecialNet.Register(TopicBounty, OnBounty);
            SpecialNet.Register(TopicStreak, OnStreak);

            SpecialRuntime.RegisterActive(Special.Reroll, new ActiveHandler { Execute = ExecuteReroll });
            SpecialRuntime.RegisterActive(Special.TimeOut, new ActiveHandler { Execute = ExecuteTimeOut, Duration = TimeOutSeconds });
            SpecialRuntime.RegisterActive(Special.DecoyOrder, new ActiveHandler { Execute = ExecuteDecoy, Duration = DecoySeconds });
        }

        private static void Reset()
        {
            s_serverFlow = null;
            s_clientFlow = null;
            s_versus = false;
            s_nextFind = 0f;
            for (int i = 0; i < 2; i++)
            {
                s_waiting[i] = 0;
                s_streak[i] = 0;
                s_secondChanceUsed[i] = false;
                s_comboKeeperUsed[i] = false;
                s_closeEnoughReadyAt[i] = 0f;
                s_fortuneNext[i] = null;
                s_fortuneName[i] = null;
                s_bountyServer[i] = 0;
                s_bountyShown[i] = -1;
                s_decoys[i].Clear();
            }
            s_pendingAdds.Clear();
            CookedAt.Clear();
            PlateCookedAt.Clear();
            CtxActive = false;
            CtxPlacer = null;
            ExpiryActive = false;
            LifetimeOverride = -1f;
            AddingDecoy = false;
            s_nextBountyCheck = s_nextBountySend = s_nextStatus = 0f;
        }

        private static void OnLevelStarted()
        {
            Reset();
            // Before the round starts, so the first orders already see the teams.
            FindFlows();
        }

        // --- teams and flow controllers ----------------------------------------------------------

        /// <summary>FindFlows, at most once a second (patches call this when a flow is missing).</summary>
        private static void FindFlowsSoon()
        {
            if (Time.unscaledTime < s_nextFind) return;
            s_nextFind = Time.unscaledTime + 1f;
            FindFlows();
        }

        private static void FindFlows()
        {
            if (s_clientFlow == null) s_clientFlow = Object.FindObjectOfType<ClientKitchenFlowControllerBase>();
            if (s_serverFlow == null && SpecialNet.IsServer) s_serverFlow = Object.FindObjectOfType<ServerKitchenFlowControllerBase>();
            s_versus = s_serverFlow is ServerCompetitiveFlowController || s_clientFlow is ClientCompetitiveFlowController;
        }

        internal static bool Versus
        {
            get { return s_versus; }
        }

        /// <summary>The scoring team a chef plays for (campaign: always team one).</summary>
        internal static TeamID TeamOf(ChefState chef)
        {
            return s_versus && chef.Team == TeamID.Two ? TeamID.Two : TeamID.One;
        }

        internal static int Idx(TeamID team)
        {
            return s_versus && team == TeamID.Two ? 1 : 0;
        }

        internal static TeamID TeamAt(int index)
        {
            return index == 1 ? TeamID.Two : TeamID.One;
        }

        /// <summary>Team-wide specials: does anyone on this team have it (never stacks)?</summary>
        internal static bool TeamHas(Special special, TeamID team)
        {
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
                if (chefs[i].Has(special) && TeamOf(chefs[i]) == team) return true;
            return false;
        }

        internal static ServerOrderControllerBase ServerController(TeamID team)
        {
            if (s_serverFlow == null) return null;
            ServerTeamMonitor monitor = s_serverFlow.GetMonitorForTeam(s_versus ? team : TeamID.One);
            return monitor != null ? monitor.OrdersController : null;
        }

        internal static TeamID ServerTeamOf(ServerOrderControllerBase controller)
        {
            if (s_versus && s_serverFlow != null)
            {
                ServerTeamMonitor two = s_serverFlow.GetMonitorForTeam(TeamID.Two);
                if (two != null && two.OrdersController == controller) return TeamID.Two;
            }
            return TeamID.One;
        }

        internal static TeamID ServerTeamOfMonitor(ServerTeamMonitor monitor)
        {
            if (s_versus && s_serverFlow != null && s_serverFlow.GetMonitorForTeam(TeamID.Two) == monitor) return TeamID.Two;
            return TeamID.One;
        }

        internal static List<ServerOrderData> ServerOrders(ServerOrderControllerBase controller)
        {
            return controller != null && ServerActiveOrders != null ? ServerActiveOrders.GetValue(controller) as List<ServerOrderData> : null;
        }

        internal static bool ServerFull(ServerOrderControllerBase controller)
        {
            List<ServerOrderData> orders = ServerOrders(controller);
            if (orders == null) return true;
            int max = ServerMaxOrders != null ? (int)ServerMaxOrders.GetValue(controller) : 5;
            return orders.Count >= max;
        }

        internal static ClientOrderControllerBase ClientController(TeamID team)
        {
            if (s_clientFlow == null) return null;
            ClientTeamMonitor monitor = s_clientFlow.GetMonitorForTeam(s_versus ? team : TeamID.One);
            return monitor != null ? monitor.OrdersController : null;
        }

        internal static TeamID ClientTeamOf(ClientOrderControllerBase controller)
        {
            if (s_versus && s_clientFlow != null)
            {
                ClientTeamMonitor two = s_clientFlow.GetMonitorForTeam(TeamID.Two);
                if (two != null && two.OrdersController == controller) return TeamID.Two;
            }
            return TeamID.One;
        }

        private static IList ClientOrders(TeamID team)
        {
            ClientOrderControllerBase controller = ClientController(team);
            return controller != null && ClientActiveOrders != null ? ClientActiveOrders.GetValue(controller) as IList : null;
        }

        /// <summary>Seconds left in the round on the host (MaxValue when it has no limit).</summary>
        internal static float TimeLeft(ServerKitchenFlowControllerBase flow)
        {
            IServerRoundTimer timer = flow != null ? flow.RoundTimer : null;
            if (timer is ServerModifiableRoundTimer) return timer.TimeElapsed; // counts down
            if (timer is ServerRoundTimer && TimerLimit != null)
                return (float)TimerLimit.GetValue(timer) - timer.TimeElapsed;
            return float.MaxValue;
        }

        internal static string OrderName(OrderDefinitionNode node)
        {
            if (node == null || string.IsNullOrEmpty(node.name)) return "?";
            var words = new List<string>();
            foreach (string word in node.name.Replace('-', '_').Split('_', ' '))
            {
                string lower = word.ToLowerInvariant();
                if (word.Length == 0 || lower == "order" || lower == "recipe" || lower == "node" || lower == "def") continue;
                words.Add(word.ToUpperInvariant());
            }
            return words.Count > 0 ? string.Join(" ", words.ToArray()) : node.name.ToUpperInvariant();
        }

        // --- speed and skills (every machine) -----------------------------------------------------

        private static float RunSpeed(ChefState chef)
        {
            float value = 1f;
            if (chef.Has(Special.UnderPressure))
                value *= 1f + PressurePerOrder * Mathf.Min(s_waiting[Idx(TeamOf(chef))], PressureMaxOrders);
            if (chef.Has(Special.HotStreak))
                value *= 1f + StreakStep * Mathf.Min(s_streak[Idx(TeamOf(chef))], StreakMax);
            return value;
        }

        private static float DashSpeed(ChefState chef)
        {
            if (!chef.Has(Special.HotStreak)) return 1f;
            return 1f + StreakStep * Mathf.Min(s_streak[Idx(TeamOf(chef))], StreakMax);
        }

        // Skills come in whole levels (~12-20% each): at the full streak every other skill gets +1.
        private static int SkillLevels(ChefState chef, Skill skill)
        {
            if (skill == Skill.Running || skill == Skill.Dashing || !chef.Has(Special.HotStreak)) return 0;
            return s_streak[Idx(TeamOf(chef))] >= StreakMax ? 1 : 0;
        }

        // --- every frame ------------------------------------------------------------------------

        private static void OnTick(float dt)
        {
            if ((s_clientFlow == null || (SpecialNet.IsServer && s_serverFlow == null)) && Time.unscaledTime >= s_nextFind)
            {
                s_nextFind = Time.unscaledTime + 1f;
                FindFlows();
            }

            if (SpecialRuntime.AnyHas(Special.UnderPressure))
            {
                for (int i = 0; i < 2; i++)
                {
                    IList orders = i == 0 || s_versus ? ClientOrders(TeamAt(i)) : null;
                    s_waiting[i] = orders != null ? orders.Count : 0;
                }
            }

            if (SpecialNet.IsServer) ServerTick();

            float now = SpecialRuntime.Now;
            if (now >= s_nextStatus)
            {
                s_nextStatus = now + 0.25f;
                UpdateStatus();
            }
        }

        private static void ServerTick()
        {
            float now = SpecialRuntime.Now;

            // Reroll: the replacement order.
            for (int i = s_pendingAdds.Count - 1; i >= 0; i--)
            {
                if (now < s_pendingAdds[i].At) continue;
                ServerOrderControllerBase controller = s_pendingAdds[i].Controller;
                s_pendingAdds.RemoveAt(i);
                // The game may already have filled the gap itself.
                if (controller != null && !ServerFull(controller)) controller.AddNewOrder();
            }

            // Decoys leave when they run out (or after a while, if timers were stopped).
            for (int t = 0; t < 2; t++)
            {
                List<Decoy> decoys = s_decoys[t];
                for (int i = decoys.Count - 1; i >= 0; i--)
                {
                    if (!decoys[i].Expired && now < decoys[i].Until) continue;
                    uint id = decoys[i].Id;
                    decoys.RemoveAt(i);
                    ServerRemoveOrder(TeamAt(t), id);
                }
            }

            // Bounty Hunter: keep one live order marked per team.
            if (now >= s_nextBountyCheck && SpecialRuntime.AnyHas(Special.BountyHunter))
            {
                s_nextBountyCheck = now + 0.5f;
                bool resend = now >= s_nextBountySend;
                if (resend) s_nextBountySend = now + 5f;
                for (int t = 0; t < (s_versus ? 2 : 1); t++)
                {
                    TeamID team = TeamAt(t);
                    if (!TeamHas(Special.BountyHunter, team)) continue;
                    List<ServerOrderData> orders = ServerOrders(ServerController(team));
                    if (orders == null) continue;
                    uint current = s_bountyServer[t];
                    bool alive = false;
                    for (int i = 0; i < orders.Count && current != 0; i++)
                        if (orders[i].ID.m_id == current) alive = true;
                    if (!alive)
                    {
                        var choices = new List<uint>();
                        for (int i = 0; i < orders.Count; i++)
                            if (!IsDecoy(t, orders[i].ID.m_id)) choices.Add(orders[i].ID.m_id);
                        uint picked = choices.Count > 0 ? choices[Random.Range(0, choices.Count)] : 0;
                        if (picked != current)
                        {
                            s_bountyServer[t] = picked;
                            resend = true;
                        }
                    }
                    if (resend) SendBounty(t);
                }
            }
        }

        private static void SendBounty(int t)
        {
            uint id = s_bountyServer[t];
            SpecialNet.SendToAll(new SpecialMessage
            {
                Topic = TopicBounty, A = t, I = id != 0 ? (int)(id & 0xFF) : -1
            });
        }

        internal static void SendStreak(int t)
        {
            SpecialNet.SendToAll(new SpecialMessage { Topic = TopicStreak, A = t, I = s_streak[t] });
        }

        /// <summary>Host: takes an order off a team's list everywhere (no score change).</summary>
        internal static void ServerRemoveOrder(TeamID team, uint id)
        {
            List<ServerOrderData> orders = ServerOrders(ServerController(team));
            if (orders == null) return;
            bool found = false;
            for (int i = 0; i < orders.Count; i++)
                if (orders[i].ID.m_id == id) found = true;
            if (!found) return;
            ServerController(team).RemoveOrder(new OrderID(id));
            SpecialNet.SendToAll(new SpecialMessage { Topic = TopicRemoveOrder, A = Idx(team), I = (int)(id & 0xFF) });
        }

        internal static bool IsDecoy(int t, uint id)
        {
            List<Decoy> decoys = s_decoys[t];
            for (int i = 0; i < decoys.Count; i++)
                if (decoys[i].Id == id) return true;
            return false;
        }

        internal static bool TakeDecoy(int t, uint id)
        {
            List<Decoy> decoys = s_decoys[t];
            for (int i = 0; i < decoys.Count; i++)
            {
                if (decoys[i].Id != id) continue;
                decoys.RemoveAt(i);
                return true;
            }
            return false;
        }

        internal static void MarkDecoyExpired(int t, uint id)
        {
            List<Decoy> decoys = s_decoys[t];
            for (int i = 0; i < decoys.Count; i++)
            {
                if (decoys[i].Id != id) continue;
                Decoy decoy = decoys[i];
                decoy.Expired = true;
                decoys[i] = decoy;
            }
        }

        // --- status lines (every machine, local chefs) -------------------------------------------

        private static void UpdateStatus()
        {
            bool fortune = SpecialRuntime.AnyHas(Special.FortuneTeller);
            bool bounty = SpecialRuntime.AnyHas(Special.BountyHunter);
            bool streak = SpecialRuntime.AnyHas(Special.HotStreak);
            if (!fortune && !bounty && !streak) return;
            foreach (ChefState chef in SpecialRuntime.Chefs)
            {
                if (!chef.Local) continue;
                int t = Idx(TeamOf(chef));
                var parts = new List<string>();
                if (chef.Has(Special.FortuneTeller) && s_fortuneName[t] != null) parts.Add("NEXT: " + s_fortuneName[t]);
                if (bounty && s_bountyShown[t] >= 0 && TeamHas(Special.BountyHunter, TeamOf(chef)))
                {
                    string where = BountyText(TeamOf(chef), s_bountyShown[t]);
                    if (where != null) parts.Add(where);
                }
                if (chef.Has(Special.HotStreak) && s_streak[t] > 0)
                    parts.Add("STREAK +" + (Mathf.Min(s_streak[t], StreakMax) * Mathf.RoundToInt(StreakStep * 100f)) + "%");
                string text = parts.Count > 0 ? string.Join("  ", parts.ToArray()) : null;

                chef.SetStatus("Orders", text);
            }
        }

        private static string BountyText(TeamID team, int shortId)
        {
            IList orders = ClientOrders(team);
            if (orders == null || ActiveOrderId == null) return null;
            for (int i = 0; i < orders.Count; i++)
            {
                object order = orders[i];
                OrderID id = (OrderID)ActiveOrderId.GetValue(order);
                if ((int)(id.m_id & 0xFF) != shortId) continue;
                RecipeList.Entry entry = ActiveOrderEntry != null ? ActiveOrderEntry.GetValue(order) as RecipeList.Entry : null;
                return "BOUNTY: #" + (i + 1) + " " + OrderName(entry != null ? entry.m_order : null);
            }
            return null;
        }

        // --- messages (every machine) ------------------------------------------------------------

        private static int TeamIndex(SpecialMessage message)
        {
            return Mathf.Clamp(Mathf.RoundToInt(message.A), 0, 1);
        }

        private static void OnRemoveOrder(SpecialMessage message)
        {
            if (!SpecialRuntime.InLevel) return;
            if (s_clientFlow == null) FindFlowsSoon();
            ClientOrderControllerBase controller = ClientController(TeamAt(TeamIndex(message)));
            if (controller == null || ClientActiveOrders == null || ClientGui == null || ActiveOrderId == null) return;
            IList orders = ClientActiveOrders.GetValue(controller) as IList;
            RecipeFlowGUI gui = ClientGui.GetValue(controller) as RecipeFlowGUI;
            if (orders == null) return;
            for (int i = 0; i < orders.Count; i++)
            {
                OrderID id = (OrderID)ActiveOrderId.GetValue(orders[i]);
                if ((int)(id.m_id & 0xFF) != message.I) continue;
                if (gui != null && ActiveOrderToken != null)
                    gui.RemoveElement((RecipeFlowGUI.ElementToken)ActiveOrderToken.GetValue(orders[i]), new RecipeFailureAnimation());
                orders.RemoveAt(i);
                return;
            }
        }

        private static void OnFortune(SpecialMessage message)
        {
            int t = TeamIndex(message);
            s_fortuneName[t] = message.I >= 0 ? OrderName(GameUtils.GetOrderDefinitionNode(message.I)) : null;
            s_nextStatus = 0f;
        }

        private static void OnBounty(SpecialMessage message)
        {
            s_bountyShown[TeamIndex(message)] = message.I;
            s_nextStatus = 0f;
        }

        private static void OnStreak(SpecialMessage message)
        {
            s_streak[TeamIndex(message)] = Mathf.Max(0, message.I);
            s_nextStatus = 0f;
        }

        internal static void SendFortune(int t)
        {
            RecipeList.Entry next = s_fortuneNext[t];
            SpecialNet.SendToAll(new SpecialMessage
            {
                Topic = TopicFortune, A = t, I = next != null && next.m_order != null ? next.m_order.m_uID : -1
            });
        }

        internal static void SetFortune(int t, RecipeList.Entry next)
        {
            s_fortuneNext[t] = next;
            SendFortune(t);
        }

        internal static RecipeList.Entry FortuneNext(int t)
        {
            return s_fortuneNext[t];
        }

        // --- actives (host) ----------------------------------------------------------------------

        private static bool ExecuteReroll(ChefState chef)
        {
            if (s_serverFlow == null) FindFlowsSoon();
            TeamID team = TeamOf(chef);
            ServerOrderControllerBase controller = ServerController(team);
            List<ServerOrderData> orders = ServerOrders(controller);
            if (orders == null || orders.Count == 0) return false;
            uint newest = orders[orders.Count - 1].ID.m_id;
            TakeDecoy(Idx(team), newest);
            ServerRemoveOrder(team, newest);
            s_pendingAdds.Add(new PendingAdd { Controller = controller, At = SpecialRuntime.Now + RerollAddDelay });
            return true;
        }

        private static bool ExecuteTimeOut(ChefState chef)
        {
            if (s_serverFlow == null) FindFlowsSoon();
            List<ServerOrderData> orders = ServerOrders(ServerController(TeamOf(chef)));
            // Order timers read the chef's ActiveUntil (set on every machine once confirmed).
            return orders != null && orders.Count > 0;
        }

        internal static bool TimeOutActive(TeamID team)
        {
            float now = SpecialRuntime.Now;
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState chef = chefs[i];
                if (chef.Active == Special.TimeOut && chef.ActiveUntil > now && TeamOf(chef) == team) return true;
            }
            return false;
        }

        private static bool ExecuteDecoy(ChefState chef)
        {
            if (s_serverFlow == null) FindFlowsSoon();
            if (!s_versus || ServerAddEntry == null) return false;
            TeamID rival = TeamOf(chef) == TeamID.One ? TeamID.Two : TeamID.One;
            ServerOrderControllerBase controller = ServerController(rival);
            // A full order bar has no table left for another widget.
            if (controller == null || ServerFull(controller)) return false;
            RoundDataBase round = ServerRoundData != null ? ServerRoundData.GetValue(controller) as RoundDataBase : null;
            RoundInstanceDataBase instance = ServerRoundInstance != null ? ServerRoundInstance.GetValue(controller) as RoundInstanceDataBase : null;
            if (round == null || instance == null) return false;
            RecipeList.Entry[] entries = round.GetNextRecipe(instance);
            if (entries == null || entries.Length == 0 || entries[0] == null) return false;

            ServerOrderData added;
            AddingDecoy = true;
            LifetimeOverride = DecoySeconds;
            try
            {
                added = ServerAddEntry.Invoke(controller, new object[] { entries[0] }) as ServerOrderData;
            }
            finally
            {
                AddingDecoy = false;
                LifetimeOverride = -1f;
            }
            if (added == null) return false;
            s_decoys[Idx(rival)].Add(new Decoy { Id = added.ID.m_id, Until = SpecialRuntime.Now + DecoySeconds + 5f });
            return true;
        }

        // --- delivery (host) ----------------------------------------------------------------------

        internal static bool AnyDeliverySpecial()
        {
            return SpecialRuntime.AnyHas(Special.Charmer) || SpecialRuntime.AnyHas(Special.Clutch) ||
                SpecialRuntime.AnyHas(Special.Gambler) || SpecialRuntime.AnyHas(Special.FreshDelivery) ||
                SpecialRuntime.AnyHas(Special.GoldenHour) || SpecialRuntime.AnyHas(Special.CoffeeRun) ||
                SpecialRuntime.AnyHas(Special.ShowOff) || SpecialRuntime.AnyHas(Special.HotStreak) ||
                SpecialRuntime.AnyHas(Special.BountyHunter) || SpecialRuntime.AnyHas(Special.DecoyOrder);
        }

        /// <summary>
        /// Host, inside SendDeliverySuccess: the tip and score of this delivery after the specials.
        /// The game has already added base score and tip; the difference is added here, before
        /// the score goes out.
        /// </summary>
        internal static void AdjustDelivery(ServerKitchenFlowControllerBase flow, TeamID teamId, OrderID orderId,
            float timeProp, ref int tip)
        {
            ServerTeamMonitor monitor = flow.GetMonitorForTeam(teamId);
            if (monitor == null) return;
            TeamMonitor.TeamScoreStats score = monitor.Score;
            TeamID team = s_versus && teamId == TeamID.Two ? TeamID.Two : TeamID.One;
            int t = Idx(team);
            int baseScore = LastBaseScore;

            // A decoy was never a real order: it scores nothing.
            if (TakeDecoy(t, orderId.m_id))
            {
                CtxDecoy = true;
                score.TotalBaseScore = Mathf.Max(0, score.TotalBaseScore - baseScore);
                score.TotalTipsScore = Mathf.Max(0, score.TotalTipsScore - tip);
                score.TotalSuccessfulDeliveries = Mathf.Max(0, score.TotalSuccessfulDeliveries - 1);
                tip = 0;
                return;
            }

            float tipFactor = 1f, scoreFactor = 1f;
            ChefState placer = CtxActive ? CtxPlacer : null;
            if (placer != null)
            {
                if (placer.Has(Special.Charmer)) tipFactor *= CharmerTip;
                if (placer.Has(Special.FreshDelivery) && CtxCookedAt >= 0f && SpecialRuntime.Now - CtxCookedAt <= FreshSeconds)
                    tipFactor *= FreshTip;
                if (placer.Has(Special.GoldenHour) && TimeLeft(flow) <= GoldenHourSeconds) tipFactor *= 2f;
                if (placer.Has(Special.Gambler)) tipFactor *= Random.Range(0, 4);
                if (placer.Has(Special.Clutch) && timeProp <= ClutchWindow) scoreFactor *= 2f;
            }
            if (s_bountyServer[t] != 0 && s_bountyServer[t] == orderId.m_id && TeamHas(Special.BountyHunter, team))
            {
                scoreFactor *= 2f;
                s_bountyServer[t] = 0;
                s_nextBountyCheck = 0f; // mark another one at once
            }
            if (Mathf.Approximately(tipFactor, 1f) && Mathf.Approximately(scoreFactor, 1f)) return;

            int newTip = Mathf.Max(0, Mathf.RoundToInt(tip * tipFactor * scoreFactor));
            int extraBase = Mathf.RoundToInt(baseScore * (scoreFactor - 1f));
            score.TotalBaseScore += extraBase;
            score.TotalTipsScore = Mathf.Max(0, score.TotalTipsScore + newTip - tip);
            tip = Mathf.Clamp(newTip, 0, MaxTipShown);
        }

        /// <summary>Host, after a hand-in: buffs for the chef and the team's hot streak.</summary>
        internal static void AfterDelivery(TeamID stationTeam)
        {
            ChefState placer = CtxPlacer;
            if (placer != null)
            {
                if (CtxSuccess && !CtxDecoy && placer.Has(Special.CoffeeRun))
                    SpecialRuntime.BroadcastBuff(placer, BuffId.RunSpeed, CoffeeSeconds, CoffeeSpeed, placer);
                if (placer.Has(Special.ShowOff))
                    SpecialRuntime.BroadcastBuff(placer, BuffId.Frozen, PoseSeconds, 1f, placer);
            }
            if (CtxDecoy || !SpecialRuntime.AnyHas(Special.HotStreak)) return;
            int t = Idx(s_versus && stationTeam == TeamID.Two ? TeamID.Two : TeamID.One);
            int before = s_streak[t];
            s_streak[t] = CtxSuccess ? Mathf.Min(before + 1, 99) : 0;
            if (s_streak[t] != before) SendStreak(t);
        }

        /// <summary>Host: an order of this team expired (after the game took its points).</summary>
        internal static void AfterExpiry(TeamMonitor.TeamScoreStats score, TeamID team)
        {
            int t = Idx(team);
            if (ExpiryActive)
            {
                if (!s_secondChanceUsed[t] && TeamHas(Special.SecondChance, team))
                {
                    s_secondChanceUsed[t] = true;
                    score.TotalTimeExpireDeductions = ExpiryDeductions;
                }
                if (!s_comboKeeperUsed[t] && (ExpiryMultiplier > 0 || ExpiryCombo > 0) && TeamHas(Special.ComboKeeper, team))
                {
                    s_comboKeeperUsed[t] = true;
                    score.TotalMultiplier = ExpiryMultiplier;
                    score.TotalCombo = ExpiryCombo;
                    score.ComboMaintained = ExpiryMaintained;
                }
            }
            if (s_streak[t] != 0)
            {
                s_streak[t] = 0;
                SendStreak(t);
            }
        }

        internal static bool CloseEnoughReady(TeamID team)
        {
            return SpecialRuntime.Now >= s_closeEnoughReadyAt[Idx(team)] && TeamHas(Special.CloseEnough, team);
        }

        internal static void CloseEnoughUsed(TeamID team)
        {
            s_closeEnoughReadyAt[Idx(team)] = SpecialRuntime.Now + CloseEnoughCooldown;
        }

        /// <summary>Is this a dish of the level's (correct plate and contents), whatever was ordered?</summary>
        internal static bool IsLevelRecipe(AssembledDefinitionNode dish, PlatingStepData plate)
        {
            if (s_clientFlow == null) FindFlowsSoon();
            if (s_clientFlow == null || dish == null) return false;
            OrderDefinitionNode[] recipes = s_clientFlow.GetCachedRecipeList();
            if (recipes == null) return false;
            for (int i = 0; i < recipes.Length; i++)
            {
                OrderDefinitionNode recipe = recipes[i];
                if (recipe == null || recipe.m_platingStep != plate) continue;
                // Same rule as ServerOrderControllerBase.Matches.
                bool match = recipe is WildcardOrderNode
                    ? AssembledDefinitionNode.Matching(recipe, dish)
                    : AssembledDefinitionNode.Matching(dish, recipe);
                if (match) return true;
            }
            return false;
        }

        internal static bool ServerFlowReady()
        {
            if (s_serverFlow == null) FindFlowsSoon();
            return s_serverFlow != null;
        }
    }

    // === Harmony patches (host unless noted) =======================================================

    /// <summary>MAITRE D': new orders live 15% longer. Also gives a decoy its short life.</summary>
    [HarmonyPatch(typeof(ServerFixedTimeOrderController), "GetNextOrderLifetime")]
    internal static class OrdersLifetimePatch
    {
        private static void Postfix(ServerFixedTimeOrderController __instance, ref float __result)
        {
            try
            {
                if (OrdersModule.LifetimeOverride > 0f)
                {
                    __result = OrdersModule.LifetimeOverride;
                    return;
                }
                if (!SpecialRuntime.InLevel || !SpecialRuntime.AnyHas(Special.MaitreD)) return;
                if (!OrdersModule.ServerFlowReady()) return;
                if (OrdersModule.TeamHas(Special.MaitreD, OrdersModule.ServerTeamOf(__instance)))
                    __result *= OrdersModule.MaitreDLifetime;
            }
            catch (Exception e)
            {
                SpecialRuntime.Fail("MaitreD.Lifetime", e);
            }
        }
    }

    /// <summary>
    /// FORTUNE TELLER: orders are issued one roll late, so the next one is always known. Only for
    /// plain/scripted round data (phased levels count their recipes, so rolling ahead would end a
    /// phase early).
    /// </summary>
    [HarmonyPatch(typeof(ServerOrderControllerBase), "AddNewOrder", new Type[] { typeof(RecipeList.Entry) })]
    internal static class OrdersFortunePatch
    {
        private static void Prefix(ServerOrderControllerBase __instance, ref RecipeList.Entry _entry)
        {
            try
            {
                if (OrdersModule.AddingDecoy || !SpecialRuntime.InLevel || !SpecialNet.IsServer) return;
                if (!SpecialRuntime.AnyHas(Special.FortuneTeller) || !OrdersModule.ServerFlowReady()) return;
                TeamID team = OrdersModule.ServerTeamOf(__instance);
                if (!OrdersModule.TeamHas(Special.FortuneTeller, team)) return;
                RoundDataBase round = OrdersModule.ServerRoundData != null ? OrdersModule.ServerRoundData.GetValue(__instance) as RoundDataBase : null;
                if (!(round is RoundData) || round is DynamicRoundData) return;
                int t = OrdersModule.Idx(team);
                RecipeList.Entry known = OrdersModule.FortuneNext(t);
                if (known != null)
                {
                    // Issue the order foretold last time; this roll becomes the next one.
                    RecipeList.Entry rolled = _entry;
                    _entry = known;
                    OrdersModule.SetFortune(t, rolled);
                    return;
                }
                RoundInstanceDataBase instance = OrdersModule.ServerRoundInstance != null
                    ? OrdersModule.ServerRoundInstance.GetValue(__instance) as RoundInstanceDataBase : null;
                if (instance == null) return;
                RecipeList.Entry[] ahead = round.GetNextRecipe(instance);
                OrdersModule.SetFortune(t, ahead != null && ahead.Length > 0 ? ahead[0] : null);
            }
            catch (Exception e)
            {
                SpecialRuntime.Fail("FortuneTeller.AddNewOrder", e);
            }
        }
    }

    /// <summary>
    /// TIME OUT (host): the team's order timers (and next-order timer) stand still. The game's own
    /// EnableOrderExpiration switch is turned off for that one Update call; a controller left off
    /// by an exception is switched back on at its next call.
    /// </summary>
    [HarmonyPatch(typeof(ServerOrderControllerBase), "Update")]
    internal static class OrdersServerTimeOutPatch
    {
        private static readonly HashSet<ServerOrderControllerBase> Stopped = new HashSet<ServerOrderControllerBase>();

        private static void Prefix(ServerOrderControllerBase __instance)
        {
            try
            {
                if (Stopped.Count > 0 && Stopped.Remove(__instance)) __instance.EnableOrderExpiration = true;
                if (!SpecialRuntime.InLevel || !SpecialRuntime.AnyHas(Special.TimeOut) || !OrdersModule.ServerFlowReady()) return;
                if (!__instance.EnableOrderExpiration || !OrdersModule.TimeOutActive(OrdersModule.ServerTeamOf(__instance))) return;
                __instance.EnableOrderExpiration = false;
                Stopped.Add(__instance);
            }
            catch (Exception e)
            {
                SpecialRuntime.Fail("TimeOut.ServerUpdate", e);
            }
        }

        private static void Postfix(ServerOrderControllerBase __instance)
        {
            if (Stopped.Count > 0 && Stopped.Remove(__instance)) __instance.EnableOrderExpiration = true;
        }
    }

    /// <summary>TIME OUT (every machine): the order bar's timers stand still too.</summary>
    [HarmonyPatch(typeof(ClientOrderControllerBase), "Update")]
    internal static class OrdersClientTimeOutPatch
    {
        private static readonly HashSet<ClientOrderControllerBase> Stopped = new HashSet<ClientOrderControllerBase>();

        private static void Prefix(ClientOrderControllerBase __instance)
        {
            try
            {
                if (Stopped.Count > 0 && Stopped.Remove(__instance)) __instance.EnableOrderExpiration = true;
                if (!SpecialRuntime.InLevel || !SpecialRuntime.AnyHas(Special.TimeOut)) return;
                if (!__instance.EnableOrderExpiration || !OrdersModule.TimeOutActive(OrdersModule.ClientTeamOf(__instance))) return;
                __instance.EnableOrderExpiration = false;
                Stopped.Add(__instance);
            }
            catch (Exception e)
            {
                SpecialRuntime.Fail("TimeOut.ClientUpdate", e);
            }
        }

        private static void Postfix(ClientOrderControllerBase __instance)
        {
            if (Stopped.Count > 0 && Stopped.Remove(__instance)) __instance.EnableOrderExpiration = true;
        }
    }

    /// <summary>
    /// Every hand-in at the pass: remembers who handed it in (LevelState.CurrentPlacer, set while
    /// the host handles a placement) and the plate's cooking time, then gives the chef's buffs.
    /// </summary>
    [HarmonyPatch(typeof(ServerKitchenFlowControllerBase), "OnFoodDelivered")]
    internal static class OrdersDeliveredPatch
    {
        private static void Prefix(ServerPlateStation _station)
        {
            OrdersModule.CtxActive = false;
            try
            {
                if (!SpecialRuntime.InLevel || !OrdersModule.AnyDeliverySpecial()) return;
                OrdersModule.CtxActive = true;
                OrdersModule.CtxSuccess = false;
                OrdersModule.CtxDecoy = false;
                OrdersModule.CtxPlacer = SpecialRuntime.StateOf(LevelState.CurrentPlacer);
                OrdersModule.CtxCookedAt = -1f;
                ServerPlate plate = _station != null && OrdersModule.StationPlate != null
                    ? OrdersModule.StationPlate.GetValue(_station) as ServerPlate : null;
                if (plate != null)
                {
                    int key = plate.gameObject.GetInstanceID();
                    float cookedAt;
                    if (OrdersModule.PlateCookedAt.TryGetValue(key, out cookedAt)) OrdersModule.CtxCookedAt = cookedAt;
                    OrdersModule.PlateCookedAt.Remove(key);
                }
            }
            catch (Exception e)
            {
                SpecialRuntime.Fail("Orders.DeliveredPrefix", e);
            }
        }

        private static void Postfix(ServerPlateStation _station)
        {
            try
            {
                if (!OrdersModule.CtxActive) return;
                if (OrdersModule.AnyDeliverySpecial())
                    OrdersModule.AfterDelivery(_station != null ? _station.GetTeamID() : TeamID.One);
            }
            catch (Exception e)
            {
                SpecialRuntime.Fail("Orders.DeliveredPostfix", e);
            }
            finally
            {
                OrdersModule.CtxActive = false;
                OrdersModule.CtxPlacer = null;
            }
        }
    }

    /// <summary>Notes the delivered recipe's base score (the game adds it just before sending).</summary>
    [HarmonyPatch(typeof(ServerKitchenFlowControllerBase), "OnSuccessfulDelivery")]
    internal static class OrdersSuccessPatch
    {
        private static void Prefix(RecipeList.Entry _entry)
        {
            try
            {
                OrdersModule.LastBaseScore = _entry != null ? _entry.m_scoreForMeal : 0;
                if (OrdersModule.CtxActive) OrdersModule.CtxSuccess = true;
            }
            catch (Exception e)
            {
                SpecialRuntime.Fail("Orders.Success", e);
            }
        }
    }

    /// <summary>
    /// CHARMER, GAMBLER, FRESH DELIVERY, GOLDEN HOUR (tip), CLUTCH, BOUNTY HUNTER (score x2) and
    /// DECOY (nothing): changes the delivery's score and tip right before the host sends them, so
    /// every machine's score and tip pop-up show the result.
    /// </summary>
    [HarmonyPatch(typeof(ServerKitchenFlowControllerBase), "SendDeliverySuccess")]
    internal static class OrdersTipPatch
    {
        private static void Prefix(ServerKitchenFlowControllerBase __instance, TeamID _teamID, OrderID _orderID,
            float _timePropRemainingPercentage, ref int _tip)
        {
            try
            {
                if (!SpecialRuntime.InLevel || !OrdersModule.AnyDeliverySpecial()) return;
                if (!OrdersModule.ServerFlowReady()) return;
                OrdersModule.AdjustDelivery(__instance, _teamID, _orderID, _timePropRemainingPercentage, ref _tip);
            }
            catch (Exception e)
            {
                SpecialRuntime.Fail("Orders.Tip", e);
            }
        }
    }

    /// <summary>
    /// Expiry: a decoy just leaves (no penalty); otherwise remembers the score before the game's
    /// deduction for SECOND CHANCE / COMBO KEEPER.
    /// </summary>
    [HarmonyPatch(typeof(ServerKitchenFlowControllerBase), "OnOrderExpired")]
    internal static class OrdersExpiredPatch
    {
        private static bool Prefix(ServerKitchenFlowControllerBase __instance, TeamID _teamID, OrderID _orderID)
        {
            OrdersModule.ExpiryActive = false;
            try
            {
                if (!SpecialRuntime.InLevel || !OrdersModule.ServerFlowReady()) return true;
                int t = OrdersModule.Idx(_teamID);
                if (OrdersModule.IsDecoy(t, _orderID.m_id))
                {
                    // Taken off the list from the tick (not inside the controller's order loop).
                    OrdersModule.MarkDecoyExpired(t, _orderID.m_id);
                    return false;
                }
                if (!SpecialRuntime.AnyHas(Special.SecondChance) && !SpecialRuntime.AnyHas(Special.ComboKeeper)) return true;
                ServerTeamMonitor monitor = __instance.GetMonitorForTeam(_teamID);
                if (monitor == null) return true;
                OrdersModule.ExpiryActive = true;
                OrdersModule.ExpiryDeductions = monitor.Score.TotalTimeExpireDeductions;
                OrdersModule.ExpiryMultiplier = monitor.Score.TotalMultiplier;
                OrdersModule.ExpiryCombo = monitor.Score.TotalCombo;
                OrdersModule.ExpiryMaintained = monitor.Score.ComboMaintained;
            }
            catch (Exception e)
            {
                SpecialRuntime.Fail("Orders.Expired", e);
            }
            return true;
        }

        private static void Postfix()
        {
            OrdersModule.ExpiryActive = false;
        }
    }

    /// <summary>SECOND CHANCE / COMBO KEEPER / HOT STREAK: fix the score before the expiry is sent.</summary>
    [HarmonyPatch(typeof(ServerKitchenFlowControllerBase), "SendOrderExpired")]
    internal static class OrdersExpirySendPatch
    {
        private static void Prefix(ServerKitchenFlowControllerBase __instance, TeamID _teamID)
        {
            try
            {
                if (!SpecialRuntime.InLevel) return;
                if (!SpecialRuntime.AnyHas(Special.SecondChance) && !SpecialRuntime.AnyHas(Special.ComboKeeper) &&
                    !SpecialRuntime.AnyHas(Special.HotStreak)) return;
                ServerTeamMonitor monitor = __instance.GetMonitorForTeam(_teamID);
                if (monitor == null) return;
                OrdersModule.AfterExpiry(monitor.Score, OrdersModule.Versus && _teamID == TeamID.Two ? TeamID.Two : TeamID.One);
            }
            catch (Exception e)
            {
                SpecialRuntime.Fail("Orders.ExpirySend", e);
            }
        }
    }

    /// <summary>
    /// CLOSE ENOUGH: a properly plated dish of the level that matches no order counts as the
    /// oldest order (once per 60 s per team).
    /// </summary>
    [HarmonyPatch(typeof(ServerTeamMonitor), "OnFoodDelivered")]
    internal static class OrdersCloseEnoughPatch
    {
        private static void Postfix(ServerTeamMonitor __instance, AssembledDefinitionNode _definition, PlatingStepData _plateType,
            ref OrderID o_orderID, ref RecipeList.Entry o_entry, ref float o_timePropRemainingPercentage, ref bool o_wasCombo,
            ref bool __result)
        {
            try
            {
                if (__result || !SpecialRuntime.InLevel || !SpecialRuntime.AnyHas(Special.CloseEnough)) return;
                if (!OrdersModule.ServerFlowReady()) return;
                TeamID team = OrdersModule.ServerTeamOfMonitor(__instance);
                if (!OrdersModule.CloseEnoughReady(team)) return;
                ServerOrderControllerBase controller = __instance.OrdersController;
                List<ServerOrderData> orders = OrdersModule.ServerOrders(controller);
                if (orders == null || orders.Count == 0) return;
                if (!OrdersModule.IsLevelRecipe(_definition, _plateType)) return;

                ServerOrderData oldest = null;
                int t = OrdersModule.Idx(team);
                for (int i = 0; i < orders.Count && oldest == null; i++)
                    if (!OrdersModule.IsDecoy(t, orders[i].ID.m_id)) oldest = orders[i];
                if (oldest == null) return;

                // What ServerTeamMonitor.OnFoodDelivered does for a match.
                o_orderID = oldest.ID;
                o_entry = oldest.RecipeListEntry;
                o_timePropRemainingPercentage = oldest.Lifetime > 0f ? Mathf.Clamp01(oldest.Remaining / oldest.Lifetime) : 0f;
                o_wasCombo = controller.IsComboOrder(oldest.ID, __instance.Score.TotalCombo == 0);
                controller.RemoveOrder(oldest.ID);
                __result = true;
                OrdersModule.CloseEnoughUsed(team);
            }
            catch (Exception e)
            {
                SpecialRuntime.Fail("CloseEnough.Delivered", e);
            }
        }
    }

    /// <summary>FRESH DELIVERY: when each pot/pan's food finished cooking.</summary>
    [HarmonyPatch(typeof(ServerCookingHandler), "SetCookingProgress")]
    internal static class OrdersCookedPatch
    {
        private static void Postfix(ServerCookingHandler __instance, float _cookingProgress)
        {
            try
            {
                if (!SpecialRuntime.InLevel || !SpecialRuntime.AnyHas(Special.FreshDelivery)) return;
                int key = __instance.gameObject.GetInstanceID();
                float cookTime = __instance.AccessCookingTime;
                if (cookTime > 0f && _cookingProgress >= cookTime)
                {
                    if (!OrdersModule.CookedAt.ContainsKey(key)) OrdersModule.CookedAt[key] = SpecialRuntime.Now;
                }
                else OrdersModule.CookedAt.Remove(key);
            }
            catch (Exception e)
            {
                SpecialRuntime.Fail("FreshDelivery.Cooked", e);
            }
        }
    }

    /// <summary>FRESH DELIVERY: cooked food going from its pot/pan onto a plate keeps its finish time.</summary>
    [HarmonyPatch(typeof(ServerCookableContainer), "TransferToContainer")]
    internal static class OrdersPlatedPatch
    {
        private static void Prefix(ServerCookableContainer __instance, IIngredientContents _plateContainer)
        {
            try
            {
                if (!SpecialRuntime.InLevel || !SpecialRuntime.AnyHas(Special.FreshDelivery)) return;
                MonoBehaviour target = _plateContainer as MonoBehaviour;
                if (target == null) return;
                ServerCookingHandler handler = __instance.GetComponent<ServerCookingHandler>();
                float cookedAt;
                if (handler == null || !OrdersModule.CookedAt.TryGetValue(handler.gameObject.GetInstanceID(), out cookedAt)) return;
                int key = target.gameObject.GetInstanceID();
                float existing;
                // A dish is as fresh as its oldest cooked part.
                if (!OrdersModule.PlateCookedAt.TryGetValue(key, out existing) || cookedAt < existing)
                    OrdersModule.PlateCookedAt[key] = cookedAt;
            }
            catch (Exception e)
            {
                SpecialRuntime.Fail("FreshDelivery.Plated", e);
            }
        }
    }
}
