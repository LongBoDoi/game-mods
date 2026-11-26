using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using OrderController;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace ChefSkills.Specials
{
    /// <summary>
    /// Orders and score: tips, order timers, the order list and delivery streaks.
    ///
    /// The host decides every score change inside the game's own delivery and expiry paths (so the
    /// KitchenFlowMessage it sends already carries the changed score and tip), and adds orders
    /// through the server order controller (the game's OrderAdded message reaches every machine).
    /// The few things the game doesn't sync (an order taken off the list, the fortune-teller
    /// preview, the bounty, the hot streak, team recharges) go over SpecialNet.
    ///
    /// What players see: every bonus a delivery got floats up over the pass that took it
    /// ("CHARMER TIP +35%", "BOUNTY! x1.5"...), the bounty order is framed in gold on the order bar,
    /// chefs glow while Golden Hour / a full Hot Streak / Coffee Run holds, the passes are ringed
    /// while Time Out stops the clock, and team recharges sweep over the skill icons.
    /// </summary>
    internal sealed class OrdersModule : ISpecialModule
    {
        // --- tuning -----------------------------------------------------------------------------
        internal const float CharmerTip = 1.35f;
        internal const float MaitreDLifetime = 1.15f;
        // Fortune Teller: the foretold order's extra time (that order only; never on top of Maitre D').
        internal const float FortuneLifetime = 1.1f;
        internal const float ClutchWindow = 0.2f;
        internal const float ClutchScore = 1.5f;
        internal const float BountyScore = 1.5f;
        // Bounty Hunter: after a claim, the team's next bounty is marked this much later.
        internal const float BountyPause = 15f;
        internal const float FreshSeconds = 25f;
        internal const float FreshTip = 1.5f;
        internal const float CloseEnoughCooldown = 45f;
        internal const float ComboKeeperCooldown = 25f;
        internal const float SecondChanceCooldown = 30f;
        internal const float GoldenHourSeconds = 60f;
        internal const float CoffeeSpeed = 0.25f;
        internal const float CoffeeSeconds = 10f;
        // Under Pressure: only the orders beyond the first two count.
        internal const int PressureFreeOrders = 2;
        internal const float PressurePerOrder = 0.07f;
        internal const int PressureMaxOrders = 3;
        internal const float StreakStep = 0.03f;
        // Hot Streak: speed grows up to this many orders; the +1 to skills needs StreakSkills.
        internal const int StreakMax = 5;
        internal const int StreakSkills = 8;
        internal const float DecoySeconds = 25f;
        // Gambler's tip rolls (host), and the caps on what all the delivery specials multiply to.
        private static readonly float[] GamblerRolls = { 0f, 1f, 2f, 2.5f };
        internal const float MaxTipFactor = 2.5f;
        internal const float MaxScoreFactor = 2f;
        internal const float PoseSeconds = 1f;
        internal const float TimeOutSeconds = 10f;
        // Reroll on a full order bar: the new order is added a moment after the removal reached
        // the clients, so their bar never has one widget too many (no table left for it).
        internal const float RerollAddDelay = 0.5f;
        // An order younger than this may not have reached every client yet: Reroll leaves it.
        private const float RerollMinAge = 1f;
        // The game sends 6 bits of tip; the score itself carries the full amount.
        private const int MaxTipShown = 63;

        // --- topics -----------------------------------------------------------------------------
        private static readonly ushort TopicRemoveOrder = (ushort)((int)Special.Reroll * 10 + 1);
        private static readonly ushort TopicFortune = (ushort)((int)Special.FortuneTeller * 10 + 1);
        private static readonly ushort TopicBounty = (ushort)((int)Special.BountyHunter * 10 + 1);
        private static readonly ushort TopicStreak = (ushort)((int)Special.HotStreak * 10 + 1);
        private static readonly ushort TopicCloseEnough = (ushort)((int)Special.CloseEnough * 10 + 1);
        private static readonly ushort TopicComboKeeper = (ushort)((int)Special.ComboKeeper * 10 + 1);
        private static readonly ushort TopicSecondChance = (ushort)((int)Special.SecondChance * 10 + 1);

        // --- private game members ---------------------------------------------------------------
        internal static readonly FieldInfo ServerActiveOrders = AccessTools.Field(typeof(ServerOrderControllerBase), "m_activeOrders");
        internal static readonly FieldInfo ServerRoundData = AccessTools.Field(typeof(ServerOrderControllerBase), "m_roundData");
        internal static readonly FieldInfo ServerRoundInstance = AccessTools.Field(typeof(ServerOrderControllerBase), "m_roundInstanceData");
        internal static readonly FieldInfo ServerMaxOrders = AccessTools.Field(typeof(ServerOrderControllerBase), "m_maxOrdersAllowed");
        internal static readonly FieldInfo ServerTimerUntilOrder = AccessTools.Field(typeof(ServerOrderControllerBase), "m_timerUntilOrder");
        internal static readonly MethodInfo ServerAddEntry =
            AccessTools.Method(typeof(ServerOrderControllerBase), "AddNewOrder", new Type[] { typeof(RecipeList.Entry) });
        internal static readonly FieldInfo ClientActiveOrders = AccessTools.Field(typeof(ClientOrderControllerBase), "m_activeOrders");
        internal static readonly FieldInfo ClientGui = AccessTools.Field(typeof(ClientOrderControllerBase), "m_gui");
        internal static readonly FieldInfo StationPlate = AccessTools.Field(typeof(ServerPlateStation), "m_plate");
        internal static readonly FieldInfo TimerLimit = AccessTools.Field(typeof(ServerRoundTimer), "m_timeLimit");
        internal static readonly FieldInfo ClientTimerLimit = AccessTools.Field(typeof(ClientRoundTimer), "m_timeLimit");
        internal static readonly FieldInfo WidgetTiles = AccessTools.Field(typeof(RecipeWidgetUIController), "m_recipeTiles");
        private static readonly FieldInfo GuiWidgets = AccessTools.Field(typeof(RecipeFlowGUI), "m_widgets");
        private static readonly FieldInfo GuiDyingWidgets = AccessTools.Field(typeof(RecipeFlowGUI), "m_dyingWidgets");
        private static readonly FieldInfo GuiPrefab = AccessTools.Field(typeof(RecipeFlowGUI), "m_recipeWidgetPrefab");
        private static readonly FieldInfo GuiSpacing = AccessTools.Field(typeof(RecipeFlowGUI), "m_distanceBetweenOrders");
        private static readonly FieldInfo GuiEdge = AccessTools.Field(typeof(RecipeFlowGUI), "m_distanceFromEndOfScreen");
        // ClientOrderControllerBase.ActiveOrder is a protected nested class.
        private static readonly Type ActiveOrderType = AccessTools.Inner(typeof(ClientOrderControllerBase), "ActiveOrder");
        private static readonly FieldInfo ActiveOrderId = ActiveOrderType != null ? AccessTools.Field(ActiveOrderType, "ID") : null;
        private static readonly FieldInfo ActiveOrderEntry = ActiveOrderType != null ? AccessTools.Field(ActiveOrderType, "RecipeListEntry") : null;
        private static readonly FieldInfo ActiveOrderToken = ActiveOrderType != null ? AccessTools.Field(ActiveOrderType, "UIToken") : null;

        // --- level state (cleared with the level) -----------------------------------------------
        private static ServerKitchenFlowControllerBase s_serverFlow;
        private static ClientKitchenFlowControllerBase s_clientFlow;
        private static PlateStation[] s_passes = new PlateStation[0];
        private static bool s_versus;
        private static float s_nextFind;

        // Per team (index 0 = team one / campaign, 1 = team two).
        private static readonly int[] s_waiting = new int[2];
        private static readonly int[] s_streak = new int[2];
        private static readonly HashSet<uint>[] s_secondChanceOrders = { new HashSet<uint>(), new HashSet<uint>() };
        // Team recharges: host decides with them, every machine shows them (SpecialNet).
        private static readonly float[] s_comboKeeperReadyAt = new float[2];
        private static readonly float[] s_closeEnoughReadyAt = new float[2];
        private static readonly float[] s_secondChanceReadyAt = new float[2];
        // Bounty Hunter (host): no new bounty for this team until then (the pause after a claim).
        private static readonly float[] s_bountyPauseUntil = new float[2];
        private static readonly RecipeList.Entry[] s_fortuneNext = new RecipeList.Entry[2];
        private static readonly uint[] s_bountyServer = new uint[2];
        private static readonly int[] s_bountyShown = new int[2];
        private static readonly List<Decoy>[] s_decoys = { new List<Decoy>(), new List<Decoy>() };
        // Reroll on a full bar (host): replacements owed, when, which recipe to avoid, who asked.
        private static readonly int[] s_rerollOwed = new int[2];
        private static readonly float[] s_rerollAt = new float[2];
        private static readonly OrderDefinitionNode[] s_rerollAvoid = new OrderDefinitionNode[2];
        private static readonly ChefState[] s_rerollChef = new ChefState[2];
        // The gold frame on the bounty order's widget (every machine).
        private static readonly GameObject[] s_bountyFrame = new GameObject[2];
        private static readonly Component[] s_bountyWidget = new Component[2];
        private static readonly CardMark[] s_bountyMark = new CardMark[2];
        // Fortune Teller: the foretold order (its uID, -1 = none) and its ghost card on the bar.
        private static readonly int[] s_fortuneUid = { -1, -1 };
        private static readonly GameObject[] s_ghost = new GameObject[2];
        private static readonly int[] s_ghostUid = { -1, -1 };
        private static readonly CardMark[] s_ghostMark = new CardMark[2];
        private static readonly float[] s_maitreTextAt = new float[2];

        // Fresh Delivery (host): cooking handler -> when its food finished; plate -> the earliest
        // finish time of the cooked food put on it.
        internal static readonly Dictionary<int, float> CookedAt = new Dictionary<int, float>();
        internal static readonly Dictionary<int, float> PlateCookedAt = new Dictionary<int, float>();

        // The delivery being handled right now on the host.
        internal static bool CtxActive;
        internal static bool CtxSuccess;
        internal static bool CtxDecoy;
        internal static bool CtxCloseEnough;
        internal static ChefState CtxPlacer;
        internal static float CtxCookedAt = -1f;
        internal static int LastBaseScore;

        // The expiry being handled right now on the host (score before the game's deduction).
        internal static bool ExpiryActive;
        internal static int ExpiryDeductions, ExpiryMultiplier, ExpiryCombo;
        internal static bool ExpiryMaintained;

        // The wrong dish being handled right now on the host (tip streak before the game reset it).
        internal static bool FailActive;
        internal static int FailMultiplier, FailCombo;
        internal static bool FailMaintained;
        // Combo Keeper kept the streaks through this wrong dish: Hot Streak keeps going too.
        internal static bool KeepStreak;

        // An order being added by a special: its lifetime, and no Fortune Teller swap.
        internal static float LifetimeOverride = -1f;
        internal static bool SkipFortune;
        internal static bool AddingDecoy;
        // The order being added right now is the one Fortune Teller foretold (its extra time).
        internal static bool FortuneBoost;

        private static float s_nextBountyCheck, s_nextBountySend, s_nextStatus, s_nextResend;

        private struct Decoy
        {
            public uint Id;
            public float Until;
            public bool Expired;
        }

        public void Register()
        {
            SpecialRuntime.LevelStarted += OnLevelStarted;
            SpecialRuntime.LevelEnded += Reset;
            SpecialRuntime.Tick += OnTick;
            SpecialRuntime.Frame += OnFrame;

            SpecialRuntime.RunSpeedModifiers.Add(RunSpeed);
            SpecialRuntime.DashSpeedModifiers.Add(DashSpeed);
            SpecialRuntime.LevelBonusProviders.Add(SkillLevels);

            SpecialNet.Register(TopicRemoveOrder, OnRemoveOrder);
            SpecialNet.Register(TopicFortune, OnFortune);
            SpecialNet.Register(TopicBounty, OnBounty);
            SpecialNet.Register(TopicStreak, OnStreak);
            SpecialNet.Register(TopicCloseEnough, OnTeamRecharge);
            SpecialNet.Register(TopicComboKeeper, OnTeamRecharge);
            SpecialNet.Register(TopicSecondChance, OnTeamRecharge);

            SpecialRuntime.RegisterActive(Special.Reroll, new ActiveHandler { Execute = ExecuteReroll });
            SpecialRuntime.RegisterActive(Special.TimeOut, new ActiveHandler
            {
                Execute = ExecuteTimeOut, OnActivated = OnTimeOutActivated, Duration = TimeOutSeconds
            });
            SpecialRuntime.RegisterActive(Special.DecoyOrder, new ActiveHandler { Execute = ExecuteDecoy, Duration = DecoySeconds });
        }

        private static void Reset()
        {
            s_serverFlow = null;
            s_clientFlow = null;
            s_passes = new PlateStation[0];
            s_versus = false;
            s_nextFind = 0f;
            for (int i = 0; i < 2; i++)
            {
                s_waiting[i] = 0;
                s_streak[i] = 0;
                s_secondChanceOrders[i].Clear();
                s_comboKeeperReadyAt[i] = 0f;
                s_closeEnoughReadyAt[i] = 0f;
                s_secondChanceReadyAt[i] = 0f;
                s_bountyPauseUntil[i] = 0f;
                s_fortuneNext[i] = null;
                s_bountyServer[i] = 0;
                s_bountyShown[i] = -1;
                s_decoys[i].Clear();
                s_rerollOwed[i] = 0;
                s_rerollAt[i] = 0f;
                s_rerollAvoid[i] = null;
                s_rerollChef[i] = null;
                if (s_bountyFrame[i] != null) Object.Destroy(s_bountyFrame[i]);
                s_bountyFrame[i] = null;
                s_bountyWidget[i] = null;
                s_bountyMark[i] = null;
                s_fortuneUid[i] = -1;
                DestroyGhost(i);
                s_maitreTextAt[i] = 0f;
            }
            CookedAt.Clear();
            PlateCookedAt.Clear();
            CtxActive = false;
            CtxCloseEnough = false;
            CtxPlacer = null;
            ExpiryActive = false;
            FailActive = false;
            KeepStreak = false;
            LifetimeOverride = -1f;
            SkipFortune = false;
            AddingDecoy = false;
            FortuneBoost = false;
            s_nextBountyCheck = s_nextBountySend = s_nextStatus = s_nextResend = 0f;
            OrdersServerTimeOutPatch.Reset();
            OrdersClientTimeOutPatch.Reset();
        }

        private static void OnLevelStarted()
        {
            Reset();
            // Before the round starts, so the first orders already see the teams.
            FindFlows();
        }

        // --- teams, flow controllers and passes --------------------------------------------------

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
            if (s_passes.Length == 0) s_passes = Object.FindObjectsOfType<PlateStation>();
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

        /// <summary>The chefs of a team with this special.</summary>
        private static List<ChefState> TeamWith(Special special, TeamID team)
        {
            var list = new List<ChefState>();
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
                if (chefs[i].Has(special) && TeamOf(chefs[i]) == team) list.Add(chefs[i]);
            return list;
        }

        /// <summary>A team's serving passes (campaign: all of them).</summary>
        private static List<PlateStation> PassesOf(TeamID team)
        {
            var list = new List<PlateStation>();
            if (s_passes.Length == 0 || s_passes[0] == null) s_passes = Object.FindObjectsOfType<PlateStation>();
            for (int i = 0; i < s_passes.Length; i++)
            {
                PlateStation pass = s_passes[i];
                if (pass == null) continue;
                if (s_versus && (pass.m_teamId == TeamID.Two ? TeamID.Two : TeamID.One) != team) continue;
                list.Add(pass);
            }
            return list;
        }

        /// <summary>Where a team's messages float up: over its first pass, else over one of its chefs.</summary>
        private static Vector3 PassPoint(TeamID team)
        {
            List<PlateStation> passes = PassesOf(team);
            if (passes.Count > 0) return passes[0].transform.position + Vector3.up * 1.7f;
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
                if (TeamOf(chefs[i]) == team && chefs[i].Chef != null) return chefs[i].Position + Vector3.up * 2.3f;
            return Vector3.up * 2f;
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

        internal static int ServerMax(ServerOrderControllerBase controller)
        {
            return controller != null && ServerMaxOrders != null ? (int)ServerMaxOrders.GetValue(controller) : 5;
        }

        internal static bool ServerFull(ServerOrderControllerBase controller)
        {
            List<ServerOrderData> orders = ServerOrders(controller);
            if (orders == null) return true;
            return orders.Count >= ServerMax(controller);
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

        /// <summary>Seconds left in the round as this machine's clock shows it (MaxValue: no limit).</summary>
        private static float ClientTimeLeft()
        {
            IClientRoundTimer timer = s_clientFlow != null ? s_clientFlow.RoundTimer : null;
            if (timer is ClientModifiableRoundTimer) return timer.TimeElapsed; // counts down
            if (timer is ClientRoundTimer && ClientTimerLimit != null)
                return (float)ClientTimerLimit.GetValue(timer) - timer.TimeElapsed;
            return float.MaxValue;
        }

        private static bool InGoldenHour(float timeLeft)
        {
            return timeLeft > 0f && timeLeft <= GoldenHourSeconds;
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

        /// <summary>Under Pressure: the team's waiting orders beyond the first two (capped).</summary>
        private static int PressureOrders(ChefState chef)
        {
            return Mathf.Clamp(s_waiting[Idx(TeamOf(chef))] - PressureFreeOrders, 0, PressureMaxOrders);
        }

        private static int StreakOf(ChefState chef)
        {
            return Mathf.Min(s_streak[Idx(TeamOf(chef))], StreakMax);
        }

        private static float RunSpeed(ChefState chef)
        {
            float value = 1f;
            if (chef.Has(Special.UnderPressure)) value *= 1f + PressurePerOrder * PressureOrders(chef);
            if (chef.Has(Special.HotStreak)) value *= 1f + StreakStep * StreakOf(chef);
            return value;
        }

        private static float DashSpeed(ChefState chef)
        {
            if (!chef.Has(Special.HotStreak)) return 1f;
            return 1f + StreakStep * StreakOf(chef);
        }

        // Skills come in whole levels (~12-20% each): at a streak of 8 every other skill gets +1.
        private static int SkillLevels(ChefState chef, Skill skill)
        {
            if (skill == Skill.Running || skill == Skill.Dashing || !chef.Has(Special.HotStreak)) return 0;
            return s_streak[Idx(TeamOf(chef))] >= StreakSkills ? 1 : 0;
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

            // Reroll on a full bar: the replacement, once the removal has reached everyone. Until
            // then the game's own next-order timer is held, so it can't fill the gap first.
            for (int t = 0; t < 2; t++)
            {
                if (s_rerollOwed[t] <= 0) continue;
                ServerOrderControllerBase controller = ServerController(TeamAt(t));
                if (controller == null)
                {
                    s_rerollOwed[t] = 0;
                    continue;
                }
                if (now < s_rerollAt[t])
                {
                    if (ServerTimerUntilOrder != null && (float)ServerTimerUntilOrder.GetValue(controller) < 1f)
                        ServerTimerUntilOrder.SetValue(controller, 1f);
                    continue;
                }
                while (s_rerollOwed[t] > 0)
                {
                    s_rerollOwed[t]--;
                    if (!ServerFull(controller)) AddReplacement(controller, TeamAt(t), s_rerollAvoid[t], s_rerollChef[t]);
                }
                s_rerollChef[t] = null;
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

            // Fortune and streak are only sent when they change. Send them again now and then, so a
            // machine that started its level after a change (its start wipes them) catches up:
            // the streak changes everyone's movement, so every machine must agree on it.
            if (now >= s_nextResend)
            {
                s_nextResend = now + 5f;
                for (int t = 0; t < (s_versus ? 2 : 1); t++)
                {
                    if (TeamHas(Special.FortuneTeller, TeamAt(t)) && s_fortuneNext[t] != null) SendFortune(t);
                    if (TeamHas(Special.HotStreak, TeamAt(t))) SendStreak(t);
                }
            }

            // Bounty Hunter: keep one live order marked per team (none during a team's pause after
            // a claim; an expired or removed bounty is re-marked at once).
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
                    bool sendThis = resend;
                    if (!alive)
                    {
                        var choices = new List<uint>();
                        for (int i = 0; i < orders.Count && now >= s_bountyPauseUntil[t]; i++)
                            if (!IsDecoy(t, orders[i].ID.m_id)) choices.Add(orders[i].ID.m_id);
                        uint picked = choices.Count > 0 ? choices[Random.Range(0, choices.Count)] : 0;
                        if (picked != current)
                        {
                            s_bountyServer[t] = picked;
                            sendThis = true;
                        }
                    }
                    if (sendThis) SendBounty(t);
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

        /// <summary>Host: starts a team recharge everywhere (sweep on the special's icon).</summary>
        private static void SendTeamRecharge(Special special, int t, float seconds)
        {
            ushort topic;
            if (special == Special.CloseEnough)
            {
                s_closeEnoughReadyAt[t] = SpecialRuntime.Now + seconds;
                topic = TopicCloseEnough;
            }
            else if (special == Special.SecondChance)
            {
                s_secondChanceReadyAt[t] = SpecialRuntime.Now + seconds;
                topic = TopicSecondChance;
            }
            else
            {
                s_comboKeeperReadyAt[t] = SpecialRuntime.Now + seconds;
                topic = TopicComboKeeper;
            }
            SpecialNet.SendToAll(new SpecialMessage { Topic = topic, A = t, B = seconds, I = (int)special });
        }

        /// <summary>Host: takes an order off a team's list everywhere (no score change).</summary>
        internal static void ServerRemoveOrder(TeamID team, uint id)
        {
            ServerOrderControllerBase controller = ServerController(team);
            List<ServerOrderData> orders = ServerOrders(controller);
            if (orders == null) return;
            bool found = false;
            for (int i = 0; i < orders.Count; i++)
                if (orders[i].ID.m_id == id) found = true;
            if (!found) return;
            controller.RemoveOrder(new OrderID(id));
            // Clients know orders by the 8 bits OrderID sends.
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

        // --- status, icons and auras (every machine) --------------------------------------------

        private static void UpdateStatus()
        {
            bool fortune = SpecialRuntime.AnyHas(Special.FortuneTeller);
            bool bounty = SpecialRuntime.AnyHas(Special.BountyHunter);
            bool streak = SpecialRuntime.AnyHas(Special.HotStreak);
            bool pressure = SpecialRuntime.AnyHas(Special.UnderPressure);
            bool golden = SpecialRuntime.AnyHas(Special.GoldenHour);
            if (!fortune && !bounty && !streak && !pressure && !golden) return;

            float now = SpecialRuntime.Now;
            float timeLeft = golden ? ClientTimeLeft() : float.MaxValue;
            foreach (ChefState chef in SpecialRuntime.Chefs)
            {
                if (chef.Chef == null) continue;
                int t = Idx(TeamOf(chef));

                // Auras every machine draws from what it knows (all of it synced).
                bool goldenNow = chef.Has(Special.GoldenHour) && InGoldenHour(timeLeft);
                if (goldenNow)
                {
                    SpecialFx.Mark(chef.Chef, Special.GoldenHour, 0.6f, null);
                    if (!chef.Get("Orders.Golden", false))
                    {
                        chef.Set("Orders.Golden", true);
                        SpecialFx.Text(chef.Chef, SpecialCatalog.NameOf(Special.GoldenHour) + Lang.T("! TIPS x2", "! BOA x2"), SpecialFx.ColourOf(Special.GoldenHour));
                        SpecialFx.Burst(chef.Position, Special.GoldenHour);
                    }
                }
                if (chef.Has(Special.HotStreak) && s_streak[t] >= StreakSkills)
                    SpecialFx.Mark(chef.Chef, Special.HotStreak, 0.6f, null);
                if (chef.Has(Special.UnderPressure) && PressureOrders(chef) >= PressureMaxOrders)
                    SpecialFx.Mark(chef.Chef, Special.UnderPressure, 0.6f, null);

                if (!chef.Local) continue;
                if (goldenNow) chef.SetTimer(Special.GoldenHour, now + timeLeft, GoldenHourSeconds);
                if (chef.Has(Special.HotStreak)) chef.SetCount(Special.HotStreak, Mathf.Min(s_streak[t], 99));
                if (chef.Has(Special.UnderPressure))
                    chef.SetCount(Special.UnderPressure, Mathf.RoundToInt(PressurePerOrder * 100f) * PressureOrders(chef));

                var parts = new List<string>();
                if (chef.Has(Special.HotStreak) && s_streak[t] > 0)
                    parts.Add(Lang.T("STREAK +", "CHUỖI +") + (StreakOf(chef) * Mathf.RoundToInt(StreakStep * 100f)) + "%");
                chef.SetStatus("Orders", parts.Count > 0 ? string.Join("  ", parts.ToArray()) : null, Special.HotStreak);
            }
        }

        // --- order bar marks (every machine, every frame, also while paused) ---------------------

        /// <summary>
        /// What this mod draws round one order card. They are the bar's children, not the card's
        /// (the card recolours every Image under it while it animates): a soft halo behind all the
        /// cards, and in front of them a rounded outline hugging the card and a tag on its bottom edge.
        /// </summary>
        private sealed class CardMark
        {
            public RectTransform Back, Front, Halo, Frame, Tag;
            public Image HaloImage, FrameImage, TagImage;
            public Text TagText;
            public Component Widget;
            public Color Colour;
            public bool Strong;
            public float Born;
        }

        /// <summary>Destroys the mark's back half along with its front.</summary>
        private sealed class MarkOwner : MonoBehaviour
        {
            public GameObject Other;

            private void OnDestroy()
            {
                if (Other != null) Destroy(Other);
            }
        }

        private static void OnFrame()
        {
            if (!SpecialRuntime.InLevel) return;
            bool menu = SpecialHud.GameMenuOpen();
            try
            {
                UpdateBountyFrames(menu);
            }
            catch (Exception e)
            {
                SpecialRuntime.Fail("BountyHunter.Frame", e);
            }
            try
            {
                UpdateFortuneGhosts(menu);
            }
            catch (Exception e)
            {
                SpecialRuntime.Fail("FortuneTeller.Ghost", e);
            }
        }

        /// <summary>Every machine: the bounty order's card gets a pulsing gold frame, an x1.5 badge and a BOUNTY ribbon.</summary>
        private static void UpdateBountyFrames(bool menu)
        {
            for (int t = 0; t < 2; t++)
            {
                Component widget = t < (s_versus ? 2 : 1) && s_bountyShown[t] >= 0 ? BountyWidget(TeamAt(t), s_bountyShown[t]) : null;
                if (widget != s_bountyWidget[t] || (widget != null && s_bountyFrame[t] == null))
                {
                    if (s_bountyFrame[t] != null) Object.Destroy(s_bountyFrame[t]);
                    s_bountyFrame[t] = null;
                    s_bountyMark[t] = null;
                    s_bountyWidget[t] = null;
                    if (widget == null) continue;
                    CardMark mark = MakeMark(widget, "ChefSkillsBounty", SpecialFx.ColourOf(Special.BountyHunter), true,
                        Lang.T("BOUNTY  x1.5", "THƯỞNG  x1.5"), new Color(0.22f, 0.12f, 0.02f, 1f));
                    if (mark == null) continue; // its tiles aren't built yet: next frame
                    s_bountyMark[t] = mark;
                    s_bountyFrame[t] = mark.Front.gameObject;
                    s_bountyWidget[t] = widget;
                }
                if (s_bountyMark[t] != null) PlaceMark(s_bountyMark[t], menu);
            }
        }

        // --- Fortune Teller's ghost card ----------------------------------------------------------

        /// <summary>
        /// Every machine: the foretold order shows as a see-through card after the last order on
        /// the bar, marked NEXT ORDER. In versus only the team that has it sees it.
        /// </summary>
        private static void UpdateFortuneGhosts(bool menu)
        {
            for (int t = 0; t < 2; t++)
            {
                int uid = t < (s_versus ? 2 : 1) && (!s_versus || LocalOnTeam(TeamAt(t))) ? s_fortuneUid[t] : -1;
                RecipeFlowGUI gui = uid >= 0 ? GuiOf(TeamAt(t)) : null;
                if (gui == null)
                {
                    DestroyGhost(t);
                    continue;
                }
                if (s_ghost[t] == null || s_ghostUid[t] != uid || s_ghost[t].transform.parent != gui.transform)
                {
                    DestroyGhost(t);
                    if (!MakeGhost(t, gui, uid)) continue;
                }
                PlaceGhost(t, gui, menu);
            }
        }

        private static bool LocalOnTeam(TeamID team)
        {
            foreach (ChefState chef in SpecialRuntime.Chefs)
                if (chef.Local && TeamOf(chef) == team) return true;
            return false;
        }

        private static RecipeFlowGUI GuiOf(TeamID team)
        {
            ClientOrderControllerBase controller = ClientController(team);
            return controller != null && ClientGui != null ? ClientGui.GetValue(controller) as RecipeFlowGUI : null;
        }

        private static void DestroyGhost(int t)
        {
            if (s_ghost[t] != null) Object.Destroy(s_ghost[t]);
            s_ghost[t] = null;
            s_ghostUid[t] = -1;
            DestroyMark(s_ghostMark[t]);
            s_ghostMark[t] = null;
        }

        private static bool MakeGhost(int t, RecipeFlowGUI gui, int uid)
        {
            if (GuiPrefab == null) return false;
            OrderDefinitionNode node = GameUtils.GetOrderDefinitionNode(uid);
            var prefab = GuiPrefab.GetValue(gui) as RecipeWidgetUIController;
            if (node == null || prefab == null) return false;
            GameObject go = GameUtils.InstantiateUIController(prefab.gameObject, gui.transform as RectTransform);
            go.name = "ChefSkillsFortune";
            RecipeWidgetUIController widget = go.GetComponent<RecipeWidgetUIController>();
            if (widget == null)
            {
                Object.Destroy(go);
                return false;
            }
            widget.SetupFromOrderDefinition(node, 0);
            CanvasGroup group = go.AddComponent<CanvasGroup>();
            group.alpha = 0.68f;
            group.interactable = false;
            group.blocksRaycasts = false;
            s_ghost[t] = go;
            s_ghostUid[t] = uid;
            return true;
        }

        private static void PlaceGhost(int t, RecipeFlowGUI gui, bool menu)
        {
            GameObject go = s_ghost[t];
            var widget = go.GetComponent<RecipeWidgetUIController>();
            if (widget == null) return;
            widget.SetTimePropRemaining(1f);

            // After the last card on the bar, spaced the way the bar spaces its cards.
            float spacing = GuiSpacing != null ? (float)GuiSpacing.GetValue(gui) : 5f;
            float right = (GuiEdge != null ? (float)GuiEdge.GetValue(gui) : 5f) - spacing;
            AddRightEdges(GuiWidgets != null ? GuiWidgets.GetValue(gui) as IList : null, ref right);
            AddRightEdges(GuiDyingWidgets != null ? GuiDyingWidgets.GetValue(gui) as IList : null, ref right);
            RectTransformExtension place = go.GetComponent<RectTransformExtension>() ?? go.AddComponent<RectTransformExtension>();
            var at = new Vector2(right + spacing, 0f);
            if (place.PixelOffset != at)
            {
                place.AnchorOffset = Vector2.zero;
                place.PixelOffset = at;
            }

            var group = go.GetComponent<CanvasGroup>();
            if (group != null) group.alpha = 0.68f + 0.07f * Mathf.Sin(Time.unscaledTime * 2.5f);

            if (s_ghostMark[t] == null)
                s_ghostMark[t] = MakeMark(widget, "ChefSkillsFortuneMark", SpecialFx.ColourOf(Special.FortuneTeller), false, Lang.T("NEXT ORDER", "MÓN TIẾP THEO"), new Color(0.06f, 0.06f, 0.08f, 1f));
            if (s_ghostMark[t] != null) PlaceMark(s_ghostMark[t], menu);
        }

        private static void AddRightEdges(IList widgets, ref float right)
        {
            if (widgets == null) return;
            foreach (object item in widgets)
            {
                var data = item as RecipeFlowGUI.RecipeWidgetData;
                if (data == null || data.m_widget == null) continue;
                RectTransformExtension place = data.m_widget.GetComponent<RectTransformExtension>();
                if (place == null) continue;
                right = Mathf.Max(right, place.PixelOffset.x + data.m_widget.GetBounds().width);
            }
        }

        // --- drawing a mark ---------------------------------------------------------------------

        /// <summary>
        /// The card's tiles in <paramref name="space"/>'s local coordinates, and the height of its
        /// top tile (the dish). The card moves and scales as the bar animates: read every frame.
        /// </summary>
        private static bool CardBounds(Component widget, Transform space, out Vector2 min, out Vector2 max, out float headHeight)
        {
            min = new Vector2(float.MaxValue, float.MaxValue);
            max = new Vector2(float.MinValue, float.MinValue);
            headHeight = 0f;
            IList tiles = WidgetTiles != null ? WidgetTiles.GetValue(widget) as IList : null;
            if (tiles == null || tiles.Count == 0) return false;
            var corners = new Vector3[4];
            for (int i = 0; i < tiles.Count; i++)
            {
                var tile = tiles[i] as Component;
                RectTransform rect = tile != null ? tile.transform as RectTransform : null;
                if (rect == null) continue;
                rect.GetWorldCorners(corners);
                float top = float.MinValue, bottom = float.MaxValue;
                for (int c = 0; c < 4; c++)
                {
                    Vector3 p = space.InverseTransformPoint(corners[c]);
                    min = Vector2.Min(min, p);
                    max = Vector2.Max(max, p);
                    top = Mathf.Max(top, p.y);
                    bottom = Mathf.Min(bottom, p.y);
                }
                if (i == tiles.Count - 1) headHeight = top - bottom;
            }
            if (max.x <= min.x || max.y <= min.y) return false;
            if (headHeight <= 1f) headHeight = (max.y - min.y) * 0.5f;
            return true;
        }

        /// <summary>
        /// Builds a mark round a card on the bar (null until the card's tiles exist). Strong: a
        /// pulsing halo too (the bounty); otherwise just the outline and the tag (the ghost card).
        /// </summary>
        private static CardMark MakeMark(Component widget, string name, Color colour, bool strong, string tag, Color tagText)
        {
            Transform bar = widget.transform.parent;
            Vector2 min, max;
            float head;
            if (bar == null || !CardBounds(widget, bar, out min, out max, out head)) return null;
            var mark = new CardMark { Widget = widget, Colour = colour, Strong = strong, Born = Time.unscaledTime };
            int layer = widget.gameObject.layer;

            mark.Back = MarkRect(name + "Back", bar, layer);
            mark.Back.SetAsFirstSibling();
            mark.Halo = MarkRect("Halo", mark.Back, layer);
            mark.HaloImage = MarkImage(mark.Halo, SpecialFx.RoundHalo, colour);
            mark.Back.gameObject.SetActive(strong);

            mark.Front = MarkRect(name, bar, layer);
            CanvasGroup group = mark.Front.gameObject.AddComponent<CanvasGroup>();
            group.interactable = false;
            group.blocksRaycasts = false;
            mark.Front.gameObject.AddComponent<MarkOwner>().Other = mark.Back.gameObject;
            mark.Frame = MarkRect("Frame", mark.Front, layer);
            mark.FrameImage = MarkImage(mark.Frame, SpecialFx.RoundFrame, colour);
            mark.Tag = MarkRect("Tag", mark.Front, layer);
            mark.TagImage = MarkImage(mark.Tag, SpecialFx.RoundBox, colour);
            mark.TagText = MarkText(mark.Tag, tag, tagText, layer);
            PlaceMark(mark, false);
            return mark;
        }

        private static void DestroyMark(CardMark mark)
        {
            if (mark == null) return;
            if (mark.Front != null) Object.Destroy(mark.Front.gameObject);
            if (mark.Back != null) Object.Destroy(mark.Back.gameObject);
        }

        private static void PlaceMark(CardMark mark, bool menu)
        {
            if (mark.Front == null || mark.Widget == null) return;
            bool show = !menu;
            if (mark.Front.gameObject.activeSelf != show) mark.Front.gameObject.SetActive(show);
            bool back = show && mark.Strong;
            if (mark.Back != null && mark.Back.gameObject.activeSelf != back) mark.Back.gameObject.SetActive(back);
            if (!show) return;

            var bar = mark.Widget.transform.parent as RectTransform;
            Vector2 min, max;
            float head;
            if (bar == null || !CardBounds(mark.Widget, bar, out min, out max, out head)) return;
            // Behind every card, and in front of every card (cards added later go on top).
            if (mark.Back != null && mark.Back.GetSiblingIndex() != 0) mark.Back.SetAsFirstSibling();
            if (mark.Front.GetSiblingIndex() != bar.childCount - 1) mark.Front.SetAsLastSibling();

            float now = Time.unscaledTime;
            float pulse = 0.5f + 0.5f * Mathf.Sin(now * 4f);
            // Pops in when the card is first marked.
            float age = now - mark.Born;
            float pop = age < 0.45f ? 1f + 1.2f * (1f - age / 0.45f) * (1f - age / 0.45f) : 1f;

            float hug = 3f;
            SetBox(mark.Frame, bar, min - Vector2.one * hug, max + Vector2.one * hug);
            Color frame = Color.Lerp(mark.Colour, Color.white, (mark.Strong ? 0.35f : 0.15f) * pulse);
            frame.a = mark.Strong ? 1f : 0.55f + 0.35f * pulse;
            mark.FrameImage.color = frame;

            if (mark.Strong)
            {
                // The halo's glow is its 24 px border, so the box grows by that much plus a little.
                float spread = 24f * (1f + 0.25f * pulse) * pop;
                SetBox(mark.Halo, bar, min - Vector2.one * spread, max + Vector2.one * spread);
                Color halo = mark.Colour;
                halo.a = 0.55f + 0.35f * pulse;
                mark.HaloImage.color = halo;
            }

            // The tag hangs just under the card (clear of its icons), centred, never wider than the card.
            float height = Mathf.Clamp(head * 0.34f, 22f, 34f);
            mark.TagText.fontSize = Mathf.RoundToInt(height * 0.62f);
            float width = Mathf.Min(mark.TagText.preferredWidth + height, max.x - min.x + 2f * hug);
            float scale = (1f + 0.04f * pulse) * (age < 0.45f ? pop * 0.5f + 0.5f : 1f);
            var centre = new Vector2((min.x + max.x) * 0.5f, min.y - hug - 4f - height * 0.5f);
            SetBox(mark.Tag, bar, centre - new Vector2(width, height) * 0.5f, centre + new Vector2(width, height) * 0.5f);
            mark.Tag.localScale = new Vector3(scale, scale, 1f);
        }

        /// <summary>Puts <paramref name="rect"/> (a child of <paramref name="parent"/>, or of a full-size child of it) over a box in the parent's local space.</summary>
        private static void SetBox(RectTransform rect, RectTransform parent, Vector2 min, Vector2 max)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = (min + max) * 0.5f - parent.rect.center;
            rect.sizeDelta = max - min;
        }

        /// <summary>A rect filling its parent exactly (so its children can use the parent's space).</summary>
        private static RectTransform MarkRect(string name, Transform parent, int layer)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = layer;
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.localScale = Vector3.one;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            return rect;
        }

        private static Image MarkImage(RectTransform rect, Sprite sprite, Color colour)
        {
            Image image = rect.gameObject.AddComponent<Image>();
            image.raycastTarget = false;
            image.sprite = sprite;
            image.type = Image.Type.Sliced;
            image.color = colour;
            return image;
        }

        private static Text MarkText(RectTransform parent, string text, Color colour, int layer)
        {
            RectTransform rect = MarkRect("Text", parent, layer);
            Text label = rect.gameObject.AddComponent<Text>();
            label.raycastTarget = false;
            label.supportRichText = false;
            label.font = SpecialFx.Font;
            label.alignment = TextAnchor.MiddleCenter;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.color = colour;
            label.text = Lang.Pick(text);
            return label;
        }

        private static Component BountyWidget(TeamID team, int shortId)
        {
            ClientOrderControllerBase controller = ClientController(team);
            if (controller == null || ClientActiveOrders == null || ClientGui == null || ActiveOrderId == null || ActiveOrderToken == null)
                return null;
            IList orders = ClientActiveOrders.GetValue(controller) as IList;
            RecipeFlowGUI gui = ClientGui.GetValue(controller) as RecipeFlowGUI;
            if (orders == null || gui == null) return null;
            for (int i = 0; i < orders.Count; i++)
            {
                OrderID id = (OrderID)ActiveOrderId.GetValue(orders[i]);
                if ((int)(id.m_id & 0xFF) != shortId) continue;
                RecipeFlowGUI.RecipeWidgetData data = gui.GetData((RecipeFlowGUI.ElementToken)ActiveOrderToken.GetValue(orders[i]));
                return data != null ? data.m_widget : null;
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
                // The failure animation also frees the widget's table on the bar.
                if (gui != null && ActiveOrderToken != null)
                    gui.RemoveElement((RecipeFlowGUI.ElementToken)ActiveOrderToken.GetValue(orders[i]), new RecipeFailureAnimation());
                orders.RemoveAt(i);
                return;
            }
        }

        private static void OnFortune(SpecialMessage message)
        {
            int t = TeamIndex(message);
            s_fortuneUid[t] = message.I;
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

        private static void OnTeamRecharge(SpecialMessage message)
        {
            int t = TeamIndex(message);
            var special = (Special)message.I;
            float until = SpecialRuntime.Now + message.B;
            if (special == Special.CloseEnough) s_closeEnoughReadyAt[t] = until;
            else if (special == Special.ComboKeeper) s_comboKeeperReadyAt[t] = until;
            else if (special == Special.SecondChance) s_secondChanceReadyAt[t] = until;
            else return;
            foreach (ChefState chef in SpecialRuntime.Chefs)
                if (chef.Local && chef.Has(special) && Idx(TeamOf(chef)) == t) chef.SetTimer(special, until, message.B);
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
            int t = Idx(team);
            ServerOrderControllerBase controller = ServerController(team);
            List<ServerOrderData> orders = ServerOrders(controller);
            if (orders == null) return chef.Refuse(Lang.Both("NOT READY YET", "CHƯA SẴN SÀNG"));
            if (orders.Count == 0) return chef.Refuse(Lang.Both("NO ORDERS", "KHÔNG CÓ MÓN"));
            // The oldest order (first on the bar): a fresh order takes its place, so the team loses
            // nothing. One just added may still be on its way to the clients; the list is in the
            // order they came in, so the oldest is safe once any order has been out for a moment
            // (an expired order's timer starts over, so its own age can't tell).
            ServerOrderData target = null;
            for (int i = 0; i < orders.Count && target == null; i++)
                if (orders[i].Lifetime - orders[i].Remaining >= RerollMinAge) target = orders[0];
            if (target == null) return chef.Refuse(Lang.Both("WAIT FOR THE ORDER", "ĐỢI CÓ MÓN ĐÃ"));

            bool wasFull = ServerFull(controller);
            OrderDefinitionNode old = target.RecipeListEntry != null ? target.RecipeListEntry.m_order : null;
            uint id = target.ID.m_id;
            TakeDecoy(t, id);
            ServerRemoveOrder(team, id);
            SpecialFx.TextAllAt(PassPoint(team), SpecialFx.NameBoth(Special.Reroll, ": " + OrderName(old), ": " + OrderName(old)), Special.Reroll);
            if (!wasFull)
            {
                // Room on every bar: the new order can go out at once.
                AddReplacement(controller, team, old, chef);
                return true;
            }
            s_rerollOwed[t]++;
            s_rerollAt[t] = SpecialRuntime.Now + RerollAddDelay;
            s_rerollAvoid[t] = old;
            s_rerollChef[t] = chef;
            return true;
        }

        /// <summary>
        /// Host: a new order in place of a rerolled one, a different dish when the level's round
        /// data rolls freely. Scripted and boss rounds count their recipes, so they get the game's
        /// own next order. The Fortune Teller's foretold order stays next.
        /// </summary>
        private static void AddReplacement(ServerOrderControllerBase controller, TeamID team, OrderDefinitionNode avoid, ChefState chef)
        {
            List<ServerOrderData> orders = ServerOrders(controller);
            int before = orders != null ? orders.Count : 0;
            RoundDataBase round = ServerRoundData != null ? ServerRoundData.GetValue(controller) as RoundDataBase : null;
            RoundInstanceDataBase instance = ServerRoundInstance != null ? ServerRoundInstance.GetValue(controller) as RoundInstanceDataBase : null;
            bool freeRoll = round != null && instance != null && ServerAddEntry != null &&
                (round.GetType() == typeof(RoundData) || round.GetType() == typeof(DynamicRoundData));
            if (freeRoll)
            {
                RecipeList.Entry pick = null;
                for (int i = 0; i < 6; i++)
                {
                    RecipeList.Entry[] rolled = round.GetNextRecipe(instance);
                    if (rolled == null || rolled.Length == 0 || rolled[0] == null) continue;
                    pick = rolled[0];
                    if (avoid == null || pick.m_order != avoid) break;
                }
                if (pick == null) return;
                SkipFortune = true;
                try
                {
                    ServerAddEntry.Invoke(controller, new object[] { pick });
                }
                finally
                {
                    SkipFortune = false;
                }
            }
            else controller.AddNewOrder();

            orders = ServerOrders(controller);
            if (orders != null && orders.Count > before && chef != null && chef.Chef != null)
            {
                RecipeList.Entry added = orders[orders.Count - 1].RecipeListEntry;
                SpecialFx.TextAll(chef.Chef, Lang.Both("NEW: " + OrderName(added != null ? added.m_order : null), "MỚI: " + OrderName(added != null ? added.m_order : null)), Special.Reroll);
            }
        }

        private static bool ExecuteTimeOut(ChefState chef)
        {
            if (s_serverFlow == null) FindFlowsSoon();
            List<ServerOrderData> orders = ServerOrders(ServerController(TeamOf(chef)));
            if (orders == null) return chef.Refuse(Lang.Both("NOT READY YET", "CHƯA SẴN SÀNG"));
            if (orders.Count == 0) return chef.Refuse(Lang.Both("NO ORDERS", "KHÔNG CÓ MÓN"));
            if (TimeOutActive(TeamOf(chef))) return chef.Refuse(Lang.Both("CLOCK ALREADY STOPPED", "ĐỒNG HỒ ĐÃ DỪNG RỒI"));
            // Order timers read the chef's ActiveUntil (set on every machine once confirmed).
            return true;
        }

        /// <summary>Every machine, once confirmed: the team's passes are ringed while the clock stands still.</summary>
        private static void OnTimeOutActivated(ChefState chef)
        {
            if (s_passes.Length == 0) FindFlows();
            foreach (PlateStation pass in PassesOf(TeamOf(chef)))
            {
                SpecialFx.Mark(pass.gameObject, Special.TimeOut, TimeOutSeconds, SpecialCatalog.NameOf(Special.TimeOut));
                SpecialFx.Burst(pass.transform.position, Special.TimeOut);
            }
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
            if (!s_versus) return chef.Refuse(Lang.Both("VERSUS ONLY", "CHỈ KHI ĐỐI KHÁNG"));
            if (ServerAddEntry == null) return chef.Refuse(Lang.Both("NOT AVAILABLE", "KHÔNG DÙNG ĐƯỢC"));
            TeamID rival = TeamOf(chef) == TeamID.One ? TeamID.Two : TeamID.One;
            ServerOrderControllerBase controller = ServerController(rival);
            if (controller == null) return chef.Refuse(Lang.Both("NOT READY YET", "CHƯA SẴN SÀNG"));
            // A full order bar has no table left for another widget.
            if (ServerFull(controller)) return chef.Refuse(Lang.Both("RIVAL ORDERS FULL", "ĐƠN ĐỐI THỦ ĐÃ ĐẦY"));
            RoundDataBase round = ServerRoundData != null ? ServerRoundData.GetValue(controller) as RoundDataBase : null;
            RoundInstanceDataBase instance = ServerRoundInstance != null ? ServerRoundInstance.GetValue(controller) as RoundInstanceDataBase : null;
            if (round == null || instance == null) return chef.Refuse(Lang.Both("NOT AVAILABLE", "KHÔNG DÙNG ĐƯỢC"));
            RecipeList.Entry[] entries = round.GetNextRecipe(instance);
            if (entries == null || entries.Length == 0 || entries[0] == null) return chef.Refuse(Lang.Both("NO ORDERS LEFT", "HẾT MÓN RỒI"));

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
            if (added == null) return chef.Refuse(Lang.Both("NOT AVAILABLE", "KHÔNG DÙNG ĐƯỢC"));
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

        /// <summary>Host: a word over the pass, stacked so several bonuses on one dish don't overlap.</summary>
        private static void PassText(Vector3 at, ref int line, string text, Special special)
        {
            SpecialFx.TextAllAt(at + Vector3.up * (1.7f + 0.55f * line), text, special);
            line++;
        }

        /// <summary>
        /// Host, inside SendDeliverySuccess: the tip and score of this delivery after the specials.
        /// The game has already added base score and tip; the difference is added here, before
        /// the score goes out. Every bonus that changed something floats up over the pass.
        /// </summary>
        internal static void AdjustDelivery(ServerKitchenFlowControllerBase flow, TeamID teamId, ServerPlateStation station,
            OrderID orderId, float timeProp, ref int tip)
        {
            ServerTeamMonitor monitor = flow.GetMonitorForTeam(teamId);
            if (monitor == null) return;
            TeamMonitor.TeamScoreStats score = monitor.Score;
            TeamID team = s_versus && teamId == TeamID.Two ? TeamID.Two : TeamID.One;
            int t = Idx(team);
            int baseScore = LastBaseScore;
            Vector3 at = station != null ? station.transform.position : PassPoint(team) - Vector3.up * 1.7f;
            int line = 0;

            if (CtxCloseEnough) PassText(at, ref line, SpecialFx.NameBoth(Special.CloseEnough, "!", "!"), Special.CloseEnough);

            // A decoy was never a real order: it scores nothing.
            if (TakeDecoy(t, orderId.m_id))
            {
                CtxDecoy = true;
                score.TotalBaseScore = Mathf.Max(0, score.TotalBaseScore - baseScore);
                score.TotalTipsScore = Mathf.Max(0, score.TotalTipsScore - tip);
                score.TotalSuccessfulDeliveries = Mathf.Max(0, score.TotalSuccessfulDeliveries - 1);
                tip = 0;
                PassText(at, ref line, Lang.Both("DECOY! NO POINTS", "ĐƠN GIẢ! KHÔNG CÓ ĐIỂM"), Special.DecoyOrder);
                SpecialFx.BurstAll(at, Special.DecoyOrder);
                return;
            }

            float tipFactor = 1f, scoreFactor = 1f;
            Special first = Special.None;
            ChefState placer = CtxActive ? CtxPlacer : null;
            if (placer != null)
            {
                bool hasTip = tip > 0;
                if (placer.Has(Special.Charmer))
                {
                    tipFactor *= CharmerTip;
                    if (hasTip) PassText(at, ref line, SpecialFx.NameBoth(Special.Charmer, " TIP +" + Mathf.RoundToInt((CharmerTip - 1f) * 100f) + "%", ": BOA +" + Mathf.RoundToInt((CharmerTip - 1f) * 100f) + "%"), Special.Charmer);
                    if (hasTip && first == Special.None) first = Special.Charmer;
                }
                if (placer.Has(Special.FreshDelivery) && CtxCookedAt >= 0f && SpecialRuntime.Now - CtxCookedAt <= FreshSeconds)
                {
                    tipFactor *= FreshTip;
                    if (hasTip) PassText(at, ref line, Lang.Both("FRESH TIP +" + Mathf.RoundToInt((FreshTip - 1f) * 100f) + "%", "BOA NÓNG HỔI +" + Mathf.RoundToInt((FreshTip - 1f) * 100f) + "%"), Special.FreshDelivery);
                    if (hasTip && first == Special.None) first = Special.FreshDelivery;
                }
                if (placer.Has(Special.GoldenHour) && InGoldenHour(TimeLeft(flow)))
                {
                    tipFactor *= 2f;
                    if (hasTip) PassText(at, ref line, SpecialFx.NameBoth(Special.GoldenHour, " TIP x2", ": BOA x2"), Special.GoldenHour);
                    if (hasTip && first == Special.None) first = Special.GoldenHour;
                }
                if (placer.Has(Special.Gambler))
                {
                    float roll = GamblerRolls[Random.Range(0, GamblerRolls.Length)];
                    tipFactor *= roll;
                    string shown = roll.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
                    if (hasTip) PassText(at, ref line, roll <= 0f ? SpecialFx.NameBoth(Special.Gambler, ": TIP LOST", ": MẤT BOA") : SpecialFx.NameBoth(Special.Gambler, " TIP x" + shown, ": BOA x" + shown), Special.Gambler);
                    if (hasTip && first == Special.None) first = Special.Gambler;
                }
                // A Close Enough hand-in was never the right dish: no Clutch or Bounty on top.
                if (placer.Has(Special.Clutch) && timeProp <= ClutchWindow && !CtxCloseEnough)
                {
                    scoreFactor *= ClutchScore;
                    PassText(at, ref line, SpecialFx.NameBoth(Special.Clutch, "! x1.5", "! x1.5"), Special.Clutch);
                    if (first == Special.None) first = Special.Clutch;
                }
            }
            if (s_bountyServer[t] != 0 && s_bountyServer[t] == orderId.m_id && !CtxCloseEnough && TeamHas(Special.BountyHunter, team))
            {
                scoreFactor *= BountyScore;
                // The next bounty comes after a pause (this team only), and every bar drops the frame now.
                s_bountyServer[t] = 0;
                s_bountyPauseUntil[t] = SpecialRuntime.Now + BountyPause;
                SendBounty(t);
                PassText(at, ref line, Lang.Both("BOUNTY! x1.5", "TIỀN THƯỞNG! x1.5"), Special.BountyHunter);
                if (first == Special.None) first = Special.BountyHunter;
            }
            if (first != Special.None) SpecialFx.BurstAll(at, first);
            if (placer != null && first != Special.None && placer.Chef != null) SpecialFx.BurstAll(placer.Position, first);
            // However the specials pile up, one dish gets at most x2.5 tip and x2 score.
            tipFactor = Mathf.Min(tipFactor, MaxTipFactor);
            scoreFactor = Mathf.Min(scoreFactor, MaxScoreFactor);
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
            if (placer != null && placer.Chef != null)
            {
                if (CtxSuccess && !CtxDecoy && placer.Has(Special.CoffeeRun))
                {
                    SpecialRuntime.BroadcastBuff(placer, BuffId.RunSpeed, CoffeeSeconds, CoffeeSpeed, placer);
                    SpecialFx.MarkAll(placer.Chef, Special.CoffeeRun, CoffeeSeconds, null);
                    SpecialFx.TextAll(placer.Chef, Lang.Both("COFFEE! +" + Mathf.RoundToInt(CoffeeSpeed * 100f) + "% SPEED", "CÀ PHÊ! +" + Mathf.RoundToInt(CoffeeSpeed * 100f) + "% TỐC ĐỘ"), Special.CoffeeRun);
                }
                if (placer.Has(Special.ShowOff))
                {
                    SpecialRuntime.BroadcastBuff(placer, BuffId.Frozen, PoseSeconds, 1f, placer);
                    SpecialFx.TextAll(placer.Chef, "TA-DA!", Special.ShowOff);
                }
            }
            if (CtxDecoy || !SpecialRuntime.AnyHas(Special.HotStreak)) return;
            TeamID team = s_versus && stationTeam == TeamID.Two ? TeamID.Two : TeamID.One;
            int t = Idx(team);
            int before = s_streak[t];
            // A wrong dish breaks the streak, unless Combo Keeper just kept the streaks.
            s_streak[t] = CtxSuccess ? Mathf.Min(before + 1, 99) : KeepStreak ? before : 0;
            if (s_streak[t] == before) return;
            SendStreak(t);
            StreakText(team, before, s_streak[t]);
        }

        /// <summary>Host: the streak's change over the team's Hot Streak chefs.</summary>
        private static void StreakText(TeamID team, int before, int after)
        {
            foreach (ChefState chef in TeamWith(Special.HotStreak, team))
            {
                if (chef.Chef == null) continue;
                if (after == 0)
                {
                    if (before > 0) SpecialFx.TextAll(chef.Chef, Lang.Both("STREAK LOST", "MẤT CHUỖI"), Special.HotStreak);
                }
                else if (after == StreakSkills)
                {
                    SpecialFx.TextAll(chef.Chef, Lang.Both("STREAK " + StreakSkills + "! +1 SKILLS", "CHUỖI " + StreakSkills + "! KỸ NĂNG +1"), Special.HotStreak);
                    SpecialFx.BurstAll(chef.Position, Special.HotStreak);
                }
                else if (after == StreakMax)
                    SpecialFx.TextAll(chef.Chef, Lang.Both("STREAK " + after + ": MAX SPEED", "CHUỖI " + after + ": TỐC ĐỘ TỐI ĐA"), Special.HotStreak);
                else if (after < StreakSkills) SpecialFx.TextAll(chef.Chef, Lang.Both("STREAK " + after, "CHUỖI " + after), Special.HotStreak);
            }
        }

        /// <summary>Host: an order of this team expired (after the game took its points).</summary>
        internal static void AfterExpiry(TeamMonitor.TeamScoreStats score, TeamID team, OrderID orderId)
        {
            int t = Idx(team);
            // Every expiry is a team mistake (Glass Cannon, Adrenaline), forgiven or not.
            TeamBuffs.OnTeamMistake(team);
            bool forgiven = false, kept = false;
            if (ExpiryActive)
            {
                int line = 0;
                Vector3 at = PassPoint(team) - Vector3.up * 1.7f;
                // An order's first expiry costs nothing (no points, no streaks), once per 30 s per
                // team. A later expiry of the same order is never forgiven, recharge or not.
                if (TeamHas(Special.SecondChance, team) && s_secondChanceOrders[t].Add(orderId.m_id) &&
                    SpecialRuntime.Now >= s_secondChanceReadyAt[t])
                {
                    forgiven = true;
                    score.TotalTimeExpireDeductions = ExpiryDeductions;
                    score.TotalMultiplier = ExpiryMultiplier;
                    score.TotalCombo = ExpiryCombo;
                    score.ComboMaintained = ExpiryMaintained;
                    SendTeamRecharge(Special.SecondChance, t, SecondChanceCooldown);
                    PassText(at, ref line, SpecialFx.NameBoth(Special.SecondChance, ": NO PENALTY", ": KHÔNG BỊ PHẠT"), Special.SecondChance);
                    SpecialFx.BurstAll(at, Special.SecondChance);
                }
                // Combo Keeper never fires on an expiry Second Chance already forgave.
                if (!forgiven && ComboWorthKeeping(team, ExpiryMultiplier) && ComboKeeperReady(team))
                {
                    kept = true;
                    score.TotalMultiplier = ExpiryMultiplier;
                    score.TotalCombo = ExpiryCombo;
                    score.ComboMaintained = ExpiryMaintained;
                    SendTeamRecharge(Special.ComboKeeper, t, ComboKeeperCooldown);
                    PassText(at, ref line, Lang.Both("COMBO KEPT!", "GIỮ ĐƯỢC CHUỖI!"), Special.ComboKeeper);
                }
            }
            if (s_streak[t] != 0 && !forgiven && !kept)
            {
                int before = s_streak[t];
                s_streak[t] = 0;
                SendStreak(t);
                StreakText(team, before, 0);
            }
        }

        /// <summary>
        /// Host, inside SendDeliveryFailed: a wrong dish is a team mistake (Glass Cannon,
        /// Adrenaline); Combo Keeper keeps the tip streak (and Hot Streak) through it.
        /// </summary>
        internal static void AfterFailedDelivery(TeamMonitor.TeamScoreStats score, TeamID team, ServerPlateStation station)
        {
            if (!FailActive) return;
            TeamBuffs.OnTeamMistake(team);
            if (!ComboWorthKeeping(team, FailMultiplier) || !ComboKeeperReady(team)) return;
            score.TotalMultiplier = FailMultiplier;
            score.TotalCombo = FailCombo;
            score.ComboMaintained = FailMaintained;
            KeepStreak = true; // read by AfterDelivery, once the hand-in is done
            SendTeamRecharge(Special.ComboKeeper, Idx(team), ComboKeeperCooldown);
            Vector3 at = station != null ? station.transform.position : PassPoint(team) - Vector3.up * 1.7f;
            int line = 0;
            PassText(at, ref line, Lang.Both("COMBO KEPT!", "GIỮ ĐƯỢC CHUỖI!"), Special.ComboKeeper);
            SpecialFx.BurstAll(at, Special.ComboKeeper);
        }

        /// <summary>
        /// Combo Keeper only spends its charge when there is something to keep: a tip multiplier
        /// above x1 (the game counts 0 and 1 as x1), or a Hot Streak on a team that has one.
        /// </summary>
        private static bool ComboWorthKeeping(TeamID team, int multiplier)
        {
            return multiplier > 1 || (s_streak[Idx(team)] > 0 && TeamHas(Special.HotStreak, team));
        }

        private static bool ComboKeeperReady(TeamID team)
        {
            return SpecialRuntime.Now >= s_comboKeeperReadyAt[Idx(team)] && TeamHas(Special.ComboKeeper, team);
        }

        internal static bool CloseEnoughReady(TeamID team)
        {
            return SpecialRuntime.Now >= s_closeEnoughReadyAt[Idx(team)] && TeamHas(Special.CloseEnough, team);
        }

        internal static void CloseEnoughUsed(TeamID team)
        {
            CtxCloseEnough = true;
            SendTeamRecharge(Special.CloseEnough, Idx(team), CloseEnoughCooldown);
        }

        /// <summary>Host: Maître d' and Fortune Teller (and other new-order effects) as an order comes in.</summary>
        internal static void OrderAdded(TeamID teamId, OrderID orderId)
        {
            if (AddingDecoy) return;
            TeamID team = s_versus && teamId == TeamID.Two ? TeamID.Two : TeamID.One;
            // The same rule as OrdersLifetimePatch: Maitre D' wins, the foretold order never gets both.
            bool maitre = SpecialRuntime.AnyHas(Special.MaitreD) && TeamHas(Special.MaitreD, team);
            bool fortune = FortuneBoost && FortuneLifetime > (maitre ? MaitreDLifetime : 1f);
            if (!maitre && !fortune) return;
            // Orders that come in together (the round's first two) get one word.
            int t = Idx(team);
            if (Time.unscaledTime < s_maitreTextAt[t]) return;
            s_maitreTextAt[t] = Time.unscaledTime + 1.5f;
            if (fortune)
            {
                int extra = Mathf.RoundToInt((FortuneLifetime - 1f) * 100f);
                SpecialFx.TextAllAt(PassPoint(team), Lang.Both("FORETOLD ORDER +" + extra + "% TIME", "MÓN ĐÃ BÁO TRƯỚC +" + extra + "% THỜI GIAN"), Special.FortuneTeller);
                return;
            }
            SpecialFx.TextAllAt(PassPoint(team), Lang.Both("NEW ORDER +" + Mathf.RoundToInt((MaitreDLifetime - 1f) * 100f) + "% TIME", "MÓN MỚI +" + Mathf.RoundToInt((MaitreDLifetime - 1f) * 100f) + "% THỜI GIAN"), Special.MaitreD);
        }

        /// <summary>Host: Fresh Delivery's ring on a plate while its food is fresh.</summary>
        internal static void MarkFresh(GameObject plate, float cookedAt)
        {
            float left = FreshSeconds - (SpecialRuntime.Now - cookedAt);
            if (plate == null || left <= 0.5f) return;
            SpecialFx.MarkAll(plate, Special.FreshDelivery, left, Lang.Both("FRESH", "NÓNG HỔI"));
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

    /// <summary>
    /// MAITRE D': new orders live 15% longer. FORTUNE TELLER: the foretold order lives 10% longer
    /// (the larger of the two, never both). Also gives a decoy its short life.
    /// </summary>
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
                bool fortune = OrdersModule.FortuneBoost;
                if (!SpecialRuntime.InLevel || (!fortune && !SpecialRuntime.AnyHas(Special.MaitreD))) return;
                if (!OrdersModule.ServerFlowReady()) return;
                float factor = 1f;
                if (SpecialRuntime.AnyHas(Special.MaitreD) && OrdersModule.TeamHas(Special.MaitreD, OrdersModule.ServerTeamOf(__instance)))
                    factor = OrdersModule.MaitreDLifetime;
                if (fortune) factor = Mathf.Max(factor, OrdersModule.FortuneLifetime);
                __result *= factor;
            }
            catch (Exception e)
            {
                SpecialRuntime.Fail("MaitreD.Lifetime", e);
            }
        }
    }

    /// <summary>MAITRE D' / FORTUNE TELLER: a word over the pass as each longer-lived order comes in.</summary>
    [HarmonyPatch(typeof(ServerKitchenFlowControllerBase), "OnOrderAdded")]
    internal static class OrdersAddedPatch
    {
        private static void Postfix(TeamID _teamID, OrderID _orderID)
        {
            try
            {
                if (!SpecialRuntime.InLevel || (!OrdersModule.FortuneBoost && !SpecialRuntime.AnyHas(Special.MaitreD))) return;
                OrdersModule.OrderAdded(_teamID, _orderID);
            }
            catch (Exception e)
            {
                SpecialRuntime.Fail("MaitreD.Added", e);
            }
        }
    }

    /// <summary>
    /// FORTUNE TELLER: orders are issued one roll late, so the next one is always known. Only for
    /// plain/scripted round data (phased levels count their recipes, so rolling ahead would end a
    /// phase early). The foretold order, once issued, gets its extra time (OrdersLifetimePatch).
    /// </summary>
    [HarmonyPatch(typeof(ServerOrderControllerBase), "AddNewOrder", new Type[] { typeof(RecipeList.Entry) })]
    internal static class OrdersFortunePatch
    {
        private static void Prefix(ServerOrderControllerBase __instance, ref RecipeList.Entry _entry)
        {
            OrdersModule.FortuneBoost = false;
            try
            {
                if (OrdersModule.AddingDecoy || OrdersModule.SkipFortune || !SpecialRuntime.InLevel || !SpecialNet.IsServer) return;
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
                    OrdersModule.FortuneBoost = true;
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

        private static void Postfix()
        {
            OrdersModule.FortuneBoost = false;
        }
    }

    /// <summary>
    /// TIME OUT (host): the team's order timers (and next-order timer) stand still. The game's own
    /// EnableOrderExpiration switch is turned off for that one Update call; a controller left off
    /// by an exception is switched back on at the next call (and forgotten with the level).
    /// </summary>
    [HarmonyPatch(typeof(ServerOrderControllerBase), "Update")]
    internal static class OrdersServerTimeOutPatch
    {
        private static ServerOrderControllerBase s_stopped;

        internal static void Reset()
        {
            s_stopped = null;
        }

        private static void Prefix(ServerOrderControllerBase __instance)
        {
            try
            {
                if (s_stopped != null)
                {
                    s_stopped.EnableOrderExpiration = true;
                    s_stopped = null;
                }
                if (!SpecialRuntime.InLevel || !SpecialRuntime.AnyHas(Special.TimeOut) || !OrdersModule.ServerFlowReady()) return;
                if (!__instance.EnableOrderExpiration || !OrdersModule.TimeOutActive(OrdersModule.ServerTeamOf(__instance))) return;
                __instance.EnableOrderExpiration = false;
                s_stopped = __instance;
            }
            catch (Exception e)
            {
                SpecialRuntime.Fail("TimeOut.ServerUpdate", e);
            }
        }

        private static void Postfix(ServerOrderControllerBase __instance)
        {
            if (s_stopped == null || s_stopped != __instance) return;
            __instance.EnableOrderExpiration = true;
            s_stopped = null;
        }
    }

    /// <summary>TIME OUT (every machine): the order bar's timers stand still too.</summary>
    [HarmonyPatch(typeof(ClientOrderControllerBase), "Update")]
    internal static class OrdersClientTimeOutPatch
    {
        private static ClientOrderControllerBase s_stopped;

        internal static void Reset()
        {
            s_stopped = null;
        }

        private static void Prefix(ClientOrderControllerBase __instance)
        {
            try
            {
                if (s_stopped != null)
                {
                    s_stopped.EnableOrderExpiration = true;
                    s_stopped = null;
                }
                if (!SpecialRuntime.InLevel || !SpecialRuntime.AnyHas(Special.TimeOut)) return;
                if (!__instance.EnableOrderExpiration || !OrdersModule.TimeOutActive(OrdersModule.ClientTeamOf(__instance))) return;
                __instance.EnableOrderExpiration = false;
                s_stopped = __instance;
            }
            catch (Exception e)
            {
                SpecialRuntime.Fail("TimeOut.ClientUpdate", e);
            }
        }

        private static void Postfix(ClientOrderControllerBase __instance)
        {
            if (s_stopped == null || s_stopped != __instance) return;
            __instance.EnableOrderExpiration = true;
            s_stopped = null;
        }
    }

    /// <summary>
    /// Every hand-in at the pass: remembers who handed it in (LevelState.CurrentPlacer, set while
    /// the host handles a placement; null for conveyors) and the plate's cooking time, then gives
    /// the chef's buffs.
    /// </summary>
    [HarmonyPatch(typeof(ServerKitchenFlowControllerBase), "OnFoodDelivered")]
    internal static class OrdersDeliveredPatch
    {
        private static void Prefix(ServerPlateStation _station)
        {
            OrdersModule.CtxActive = false;
            OrdersModule.CtxCloseEnough = false;
            OrdersModule.KeepStreak = false;
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
                OrdersModule.CtxCloseEnough = false;
                OrdersModule.KeepStreak = false;
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
    /// CHARMER, GAMBLER, FRESH DELIVERY, GOLDEN HOUR (tip), CLUTCH, BOUNTY HUNTER (score x1.5) and
    /// DECOY (nothing): changes the delivery's score and tip right before the host sends them, so
    /// every machine's score and tip pop-up show the result.
    /// </summary>
    [HarmonyPatch(typeof(ServerKitchenFlowControllerBase), "SendDeliverySuccess")]
    internal static class OrdersTipPatch
    {
        private static void Prefix(ServerKitchenFlowControllerBase __instance, TeamID _teamID, ServerPlateStation _plateStation,
            OrderID _orderID, float _timePropRemainingPercentage, ref int _tip)
        {
            try
            {
                if (!SpecialRuntime.InLevel || (!OrdersModule.CtxCloseEnough && !OrdersModule.AnyDeliverySpecial())) return;
                if (!OrdersModule.ServerFlowReady()) return;
                OrdersModule.AdjustDelivery(__instance, _teamID, _plateStation, _orderID, _timePropRemainingPercentage, ref _tip);
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

    /// <summary>
    /// SECOND CHANCE / COMBO KEEPER / HOT STREAK: fix the score before the expiry is sent.
    /// GLASS CANNON / ADRENALINE: the expiry is a team mistake.
    /// </summary>
    [HarmonyPatch(typeof(ServerKitchenFlowControllerBase), "SendOrderExpired")]
    internal static class OrdersExpirySendPatch
    {
        private static void Prefix(ServerKitchenFlowControllerBase __instance, TeamID _teamID, OrderID _orderID)
        {
            try
            {
                if (!SpecialRuntime.InLevel) return;
                if (!SpecialRuntime.AnyHas(Special.SecondChance) && !SpecialRuntime.AnyHas(Special.ComboKeeper) &&
                    !SpecialRuntime.AnyHas(Special.HotStreak) && !SpecialRuntime.AnyHas(Special.GlassCannon) &&
                    !SpecialRuntime.AnyHas(Special.Adrenaline)) return;
                ServerTeamMonitor monitor = __instance.GetMonitorForTeam(_teamID);
                if (monitor == null) return;
                OrdersModule.AfterExpiry(monitor.Score, OrdersModule.Versus && _teamID == TeamID.Two ? TeamID.Two : TeamID.One, _orderID);
            }
            catch (Exception e)
            {
                SpecialRuntime.Fail("Orders.ExpirySend", e);
            }
        }
    }

    /// <summary>
    /// COMBO KEEPER: remembers the tip streak before a wrong dish resets it. GLASS CANNON /
    /// ADRENALINE: marks the wrong dish as a team mistake for the send patch.
    /// </summary>
    [HarmonyPatch(typeof(ServerKitchenFlowControllerBase), "OnFailedDelivery")]
    internal static class OrdersFailedPatch
    {
        private static void Prefix(ServerKitchenFlowControllerBase __instance, ServerPlateStation _plateStation)
        {
            OrdersModule.FailActive = false;
            try
            {
                if (!SpecialRuntime.InLevel || _plateStation == null) return;
                if (!SpecialRuntime.AnyHas(Special.ComboKeeper) && !SpecialRuntime.AnyHas(Special.GlassCannon) &&
                    !SpecialRuntime.AnyHas(Special.Adrenaline)) return;
                ServerTeamMonitor monitor = __instance.GetMonitorForTeam(_plateStation.GetTeamID());
                if (monitor == null) return;
                OrdersModule.FailActive = true;
                OrdersModule.FailMultiplier = monitor.Score.TotalMultiplier;
                OrdersModule.FailCombo = monitor.Score.TotalCombo;
                OrdersModule.FailMaintained = monitor.Score.ComboMaintained;
            }
            catch (Exception e)
            {
                SpecialRuntime.Fail("ComboKeeper.Failed", e);
            }
        }

        private static void Postfix()
        {
            OrdersModule.FailActive = false;
        }
    }

    /// <summary>COMBO KEEPER: puts the tip streak back before the wrong dish is sent (and the team-mistake hook).</summary>
    [HarmonyPatch(typeof(ServerKitchenFlowControllerBase), "SendDeliveryFailed")]
    internal static class OrdersFailedSendPatch
    {
        private static void Prefix(ServerKitchenFlowControllerBase __instance, TeamID _teamID, ServerPlateStation _plateStation)
        {
            try
            {
                if (!OrdersModule.FailActive || !SpecialRuntime.InLevel) return;
                ServerTeamMonitor monitor = __instance.GetMonitorForTeam(_teamID);
                if (monitor == null) return;
                OrdersModule.AfterFailedDelivery(monitor.Score, OrdersModule.Versus && _teamID == TeamID.Two ? TeamID.Two : TeamID.One,
                    _plateStation);
            }
            catch (Exception e)
            {
                SpecialRuntime.Fail("ComboKeeper.FailedSend", e);
            }
        }
    }

    /// <summary>
    /// CLOSE ENOUGH: a properly plated dish of the level that matches no order counts as the
    /// oldest order (once per 45 s per team).
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

                // What ServerTeamMonitor.OnFoodDelivered does for a match (the DeliverySuccess the
                // game then sends takes the order off every client's bar).
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

    /// <summary>
    /// FRESH DELIVERY: cooked food going from its pot/pan onto a plate keeps its finish time, and
    /// the plate is ringed on every machine for as long as the dish stays fresh.
    /// </summary>
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
                // A dish is as fresh as its oldest cooked part; an entry long past fresh is taken
                // to be from an earlier dish this plate lost (binned), not part of this one.
                float now = SpecialRuntime.Now;
                if (!OrdersModule.PlateCookedAt.TryGetValue(key, out existing) || cookedAt < existing ||
                    now - existing > OrdersModule.FreshSeconds * 2f)
                    OrdersModule.PlateCookedAt[key] = cookedAt;
                if (target.GetComponent<ServerPlate>() != null)
                    OrdersModule.MarkFresh(target.gameObject, OrdersModule.PlateCookedAt[key]);
            }
            catch (Exception e)
            {
                SpecialRuntime.Fail("FreshDelivery.Plated", e);
            }
        }
    }
}
