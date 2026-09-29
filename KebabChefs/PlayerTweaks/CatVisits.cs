using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace PlayerTweaks;

/// <summary>
/// Decides when the thief cat visits, instead of letting the game roll for it.
///
/// The game keeps the whole schedule on one component, <c>ThiefCatManager</c>, and every
/// lever is a plain field on it: <c>thiefCatProbability</c> is the daily roll,
/// <c>minimumDayToSpawnThiefCat</c> and <c>dayCooldownAfterThiefCat</c> are the gates in
/// front of it, <c>thiefCatSpawnHour</c> is when it turns up, and
/// <c>isThiefCatGoingToSpawn</c> is the verdict the roll leaves behind. So this does not
/// have to invent a cat: it lets the game roll, then overwrites the verdict.
///
/// Overwriting the verdict rather than only the probability is what makes "today, yes"
/// work at all - by the time you have opened the panel the roll has already happened, and
/// a probability is a number for tomorrow. The probability and the two gates are written
/// as well, so nothing that re-reads them later can veto a day we have said yes to; the
/// values they had are captured the first time the manager is seen and put back when the
/// option is switched off.
///
/// The one thing this never does is re-arm a day the cat has already visited. The spawn
/// clears the verdict, so writing it back would put a second cat in the restaurant - hence
/// the decision is taken once per day (and on an explicit press), never on a timer.
///
/// Co-op: the cat is a NetworkObject, so only the host can spawn one, which makes the whole
/// schedule host-side by construction - a client writing the verdict would be writing to a
/// field nobody reads, or spawning a cat the other machine never hears about. This follows
/// the plate-cabinet shape instead: the host owns the schedule and repeats it, a client's
/// panel sends requests and shows nothing it has not been told. See <see cref="CatVisitsNet"/>.
/// </summary>
public static class CatVisits
{
    /// <summary>How the visit is decided, once the game's own roll has been thrown away.</summary>
    public enum Mode
    {
        /// <summary>Every day, from the first.</summary>
        EveryDay = 0,
        /// <summary>Never. The cat stays away for good.</summary>
        Never = 1,
        /// <summary>Every N days, counted from the cat's last visit.</summary>
        EveryNDays = 2,
        /// <summary>Never on its own - only when you say so, for today.</summary>
        Manual = 3,
    }

    /// <summary>Hour written into <c>thiefCatSpawnHour</c> when the option leaves it alone.</summary>
    public const float GameHour = -1f;

    private static readonly HashSet<string> Logged = new();

    // ------------------------------------------------------------------ state

    private static ThiefCatManager _manager;
    private static RestaurantManager _restaurant;
    private static DayNightSystem _clock;
    private static float _nextScan;

    /// <summary>The game's own numbers, so switching the option off really does hand them back.</summary>
    private static bool _captured;
    private static float _origProbability, _origHour;
    private static int _origMinDay, _origCooldown;

    /// <summary>The day the verdict was last written. Guards against writing it twice.</summary>
    private static int _appliedDay = int.MinValue;

    /// <summary>Today's answer when the player has given one by hand; null = follow the mode.</summary>
    private static bool? _manualToday;

    /// <summary>The cat has already been here today, so the verdict must not be re-armed.</summary>
    private static bool _visitedToday;

    private static int _visits;
    private static bool _installed;
    private static string _failure;

    /// <summary>Whether a cat is in the restaurant now, refreshed only while the panel is open.</summary>
    private static float _catCheckAt;
    private static bool _catHere;

    // --------------------------------------------------------------- read-out

    /// <summary>True when this machine is only watching, and the host decides.</summary>
    public static bool FollowsHost => CatVisitsNet.FollowsHost;

    /// <summary>
    /// Whether the panel should show the host's schedule rather than this machine's own.
    ///
    /// Not simply "am I a client": a change still on its way to the host has to show as
    /// made, or the button the player just pressed would sit on the old value for a round
    /// trip and read as broken.
    /// </summary>
    private static bool ShowHosts =>
        FollowsHost && CatVisitsNet.HostKnown && !CatVisitsNet.SchedulePending;

    public static Mode CurrentMode =>
        ShowHosts ? CatVisitsNet.RemoteMode : Sanitise(Plugin.CatVisitMode.Value);

    public static int CurrentInterval =>
        ShowHosts ? Mathf.Max(1, CatVisitsNet.RemoteInterval)
                  : Mathf.Max(1, Plugin.CatVisitInterval.Value);

    public static float CurrentHour =>
        ShowHosts ? CatVisitsNet.RemoteHour : Plugin.CatVisitHour.Value;

    /// <summary>Whether the cat is coming today, as far as this machine can tell.</summary>
    public static bool ComingToday
    {
        get
        {
            if (FollowsHost) return CatVisitsNet.HostKnown && CatVisitsNet.RemoteComing;

            try { var m = Manager(); return m != null && m.isThiefCatGoingToSpawn; }
            catch { return false; }
        }
    }

    /// <summary>Whether the cat has already been today.</summary>
    public static bool VisitedToday => FollowsHost ? CatVisitsNet.RemoteVisited : _visitedToday;

    /// <summary>A cat standing in the restaurant right now. Only refreshed while the panel is open.</summary>
    public static bool CatPresent => _catHere;

    /// <summary>The in-game clock, for the panel to show next to the arrival hour.</summary>
    public static string ClockText
    {
        get
        {
            try
            {
                var clock = Clock();
                if (clock == null) return null;
                return clock.GetHourText();
            }
            catch { return null; }
        }
    }

    /// <summary>The hour the cat is set to arrive, spelled out; null when the game's own is left alone.</summary>
    public static string ArrivalText
    {
        get
        {
            float h = CurrentHour;
            if (h < 0f) return null;

            int hh = Mathf.Clamp(Mathf.FloorToInt(h), 0, 23);
            int mm = Mathf.Clamp(Mathf.RoundToInt((h - hh) * 60f), 0, 59);
            return $"{hh:00}:{mm:00}";
        }
    }

    public static string Status
    {
        get
        {
            if (!Plugin.CatVisitsEnabled.Value) return "chưa hoạt động - mèo ghé ngẫu nhiên như game gốc";
            if (!_installed) return "lỗi: không tìm được ThiefCatManager của game";
            if (_failure != null) return $"lỗi: {_failure}";

            if (FollowsHost)
            {
                if (CatVisitsNet.HostSilent) return "chủ phòng chưa cài mod - lịch mèo do máy chủ quyết định";
                if (!CatVisitsNet.HostKnown) return "đang chờ chủ phòng";
                if (!CatVisitsNet.RemoteEnabled) return "chủ phòng có mod nhưng đang tắt mục này";
                if (CatVisitsNet.RequestPending) return "đã gửi yêu cầu, đang chờ chủ phòng";
                if (CatVisitsNet.RequestFailed) return "chủ phòng không trả lời yêu cầu vừa rồi";
            }

            if (Manager() == null && !FollowsHost) return "đang chờ vào nhà hàng";

            if (CatPresent) return "mèo đang ở trong nhà hàng";
            if (VisitedToday) return "mèo đã ghé hôm nay rồi";

            if (!ComingToday) return "hôm nay mèo không ghé";

            string at = ArrivalText;
            return at == null ? "hôm nay mèo sẽ ghé" : $"hôm nay mèo sẽ ghé lúc {at}";
        }
    }

    // ------------------------------------------------------------------ install

    /// <summary>
    /// Three patches, and only one of them changes anything.
    ///
    /// <c>OnNewDayStarted</c> is where the game throws its dice, so a postfix there is the
    /// first moment the verdict exists to overwrite. <c>SpawnThiefCat</c> and
    /// <c>CallThiefCatCheat</c> are watched rather than steered: the first is how we learn
    /// the cat has been, the second is the game's own summon and tells us in the log what it
    /// actually does, which the stubs cannot.
    /// </summary>
    public static void Install(BepInEx.Logging.ManualLogSource log)
    {
        try
        {
            var harmony = new HarmonyLib.Harmony(Plugin.Guid + ".catvisits");

            var newDay = HarmonyLib.AccessTools.Method(typeof(ThiefCatManager), "OnNewDayStarted");
            if (newDay == null)
            {
                log.LogWarning("Cat visits: ThiefCatManager.OnNewDayStarted not found; the schedule is off.");
                return;
            }

            harmony.Patch(newDay, postfix: new HarmonyLib.HarmonyMethod(
                HarmonyLib.AccessTools.Method(typeof(CatVisits), nameof(AfterNewDay))));

            var spawn = HarmonyLib.AccessTools.Method(typeof(ThiefCatManager), "SpawnThiefCat");
            if (spawn != null)
                harmony.Patch(spawn, postfix: new HarmonyLib.HarmonyMethod(
                    HarmonyLib.AccessTools.Method(typeof(CatVisits), nameof(AfterSpawn))));
            else
                log.LogWarning("Cat visits: ThiefCatManager.SpawnThiefCat not found; " +
                               "a visit will not be noticed and the day may be re-armed.");

            var cheat = HarmonyLib.AccessTools.Method(
                typeof(ThiefCatManager), nameof(ThiefCatManager.CallThiefCatCheat));
            if (cheat != null)
                harmony.Patch(cheat, postfix: new HarmonyLib.HarmonyMethod(
                    HarmonyLib.AccessTools.Method(typeof(CatVisits), nameof(AfterCheat))));

            _installed = true;
            log.LogInfo("Cat visits: patched ThiefCatManager; the visit schedule is ours to set.");
        }
        catch (Exception e)
        {
            log.LogWarning($"Cat visits: install failed: {e.GetType().Name}: {e.Message}");
        }
    }

    // ------------------------------------------------------------------ patches

    /// <summary>
    /// The game has just rolled for today. Overwrite the answer.
    ///
    /// Deliberately does not clear the player's "today, yes" - the day number is what clears
    /// that, and this fires close enough to the day rolling over that it is not safe to
    /// assume which of the two happened first.
    /// </summary>
    private static void AfterNewDay(ThiefCatManager __instance)
    {
        if (__instance == null) return;

        _manager = __instance;
        _visitedToday = false;

        if (!Plugin.CatVisitsEnabled.Value) return;
        if (!IsHostSide()) return;

        Decide(__instance, "ngày mới");
    }

    /// <summary>
    /// The cat has arrived. Two jobs: stop the day being re-armed, and put the arrival hour
    /// back to the one the player chose after a summon moved it forward.
    /// </summary>
    private static void AfterSpawn(ThiefCatManager __instance)
    {
        _visitedToday = true;
        _visits++;

        if (__instance == null) return;
        _manager = __instance;

        try
        {
            if (Plugin.CatVisitsEnabled.Value && IsHostSide()) ApplyHour(__instance);
        }
        catch { }

        // Once per session, and the line worth having: it says the spawn really is host-side,
        // which is the assumption the whole co-op design rests on and the stubs cannot answer.
        if (_visits == 1)
            Plugin.Logger.LogInfo(
                $"Cat visits: the cat spawned on this machine as {Role()}, day {Today()}, " +
                $"game clock {ClockText ?? "?"}, arrival hour set to {HourOf(__instance)}.");
    }

    /// <summary>
    /// The game's own summon. Watched only, to find out from the log whether it puts a cat in
    /// the restaurant there and then or merely arms the day.
    /// </summary>
    private static void AfterCheat(ThiefCatManager __instance)
    {
        if (__instance == null) return;

        LogOnce($"Cat visits: CallThiefCatCheat ran as {Role()}; " +
                $"the day flag is now {SafeComing(__instance)} and there " +
                $"{(CountCats() > 0 ? "is a cat in the scene" : "is no cat in the scene yet")}.");
    }

    // --------------------------------------------------------------------- tick

    /// <summary>
    /// Two jobs a frame, both cheap: notice the day has turned over, and let the summon key
    /// fire. Everything else here is on a timer.
    /// </summary>
    public static void Tick(bool panelOpen)
    {
        CatVisitsNet.Tick();

        if (!_installed) return;

        if (!Plugin.CatVisitsEnabled.Value)
        {
            // Switched off mid-session: hand the game its own numbers back rather than leave
            // it running on ours.
            if (_captured) Restore();
            return;
        }

        try
        {
            // Not while the panel is open: the digits and letters there are being typed into
            // fields, and the panel has its own button for this.
            if (!panelOpen && SummonKeyPressed()) SummonNow();

            if (panelOpen && Time.unscaledTime >= _catCheckAt)
            {
                _catCheckAt = Time.unscaledTime + 1f;
                _catHere = CountCats() > 0;
            }

            if (FollowsHost) return;

            var m = Manager();
            if (m == null) return;

            Capture(m);

            int day = Today();
            if (day == int.MinValue || day == _appliedDay) return;

            // A new day the postfix did not cover - the option switched on mid-shift, or a
            // save loaded. The player's hand-set answer belonged to the day that has gone.
            _appliedDay = day;
            _manualToday = null;
            _visitedToday = false;

            Decide(m, "đầu ngày");
        }
        catch (Exception e)
        {
            _failure = $"{e.GetType().Name}: {e.Message}";
            LogOnce($"Cat visits: tick failed: {_failure}");
        }
    }

    // ------------------------------------------------------------------ deciding

    /// <summary>What the mode says about today, before the player has had a say.</summary>
    private static bool ScheduleSays(ThiefCatManager m, int day)
    {
        switch (Sanitise(Plugin.CatVisitMode.Value))
        {
            case Mode.EveryDay: return true;
            case Mode.Never: return false;
            case Mode.Manual: return false;

            case Mode.EveryNDays:
            {
                int every = Mathf.Max(1, Plugin.CatVisitInterval.Value);

                int last;
                try { last = m.lastThiefCatDay; } catch { last = 0; }

                // No visit on record yet, so there is nothing to count from: start today.
                if (last <= 0) return true;
                if (day == int.MinValue) return false;

                return day - last >= every;
            }
        }

        return false;
    }

    /// <summary>
    /// Writes today's answer onto the manager: the verdict itself, and the numbers that could
    /// otherwise talk the game out of it later.
    /// </summary>
    private static void Decide(ThiefCatManager m, string why)
    {
        if (m == null) return;

        try
        {
            Capture(m);

            int day = Today();
            bool want = _manualToday ?? ScheduleSays(m, day);

            // The spawn clears the verdict; writing it back would be a second cat.
            if (want && _visitedToday) want = false;

            m.isThiefCatGoingToSpawn = want;

            // Belt as well as braces. If anything re-reads the roll or the gates between now
            // and the arrival hour, it now reads them our way instead of vetoing the day.
            m.thiefCatProbability = want ? 1f : 0f;
            if (want)
            {
                m.minimumDayToSpawnThiefCat = 0;
                m.dayCooldownAfterThiefCat = 0;
            }

            ApplyHour(m);

            if (day != int.MinValue) _appliedDay = day;

            LogOnce($"Cat visits: {why} - day {day}, mode {Sanitise(Plugin.CatVisitMode.Value)}, " +
                    $"cat coming today = {want}, arrival hour {HourOf(m)}, as {Role()}.");

            CatVisitsNet.BroadcastNow();
        }
        catch (Exception e)
        {
            _failure = $"{e.GetType().Name}: {e.Message}";
            LogOnce($"Cat visits: could not write the schedule: {_failure}");
        }
    }

    /// <summary>Pushes the chosen arrival hour, or leaves the game's own alone.</summary>
    private static void ApplyHour(ThiefCatManager m)
    {
        float hour = Plugin.CatVisitHour.Value;
        if (hour < 0f)
        {
            // Back to the game's own, in case a summon moved it earlier today.
            if (_captured) { try { m.thiefCatSpawnHour = _origHour; } catch { } }
            return;
        }

        try { m.thiefCatSpawnHour = Mathf.Clamp(hour, 0f, 23.99f); } catch { }
    }

    // -------------------------------------------------------------- player asks

    /// <summary>Sets today's answer by hand. On a client this becomes a request to the host.</summary>
    public static void SetComingToday(bool coming)
    {
        if (FollowsHost) { CatVisitsNet.RequestToday(coming); return; }

        var m = Manager();
        if (m == null) { LogOnce("Cat visits: no ThiefCatManager yet; nothing to set."); return; }

        _manualToday = coming;

        // A day that has already had its cat cannot have another, and saying so is better
        // than a toggle that silently springs back.
        if (coming && _visitedToday)
        {
            Plugin.Logger.LogInfo("Cat visits: the cat has already been today; " +
                                  "use the summon button to bring it again.");
            return;
        }

        Decide(m, coming ? "người chơi chọn mèo ghé hôm nay" : "người chơi chọn mèo không ghé");
    }

    /// <summary>
    /// Brings the cat now.
    ///
    /// The game's own cheat is what does it, but it is called with the day armed and the
    /// arrival hour pulled back to the current one first. That covers both readings of a
    /// method whose body we cannot see: if the cheat spawns outright the extra writes cost
    /// nothing, and if it only arms the day, the hour it is waiting for has already come.
    /// </summary>
    public static void SummonNow()
    {
        if (FollowsHost) { CatVisitsNet.RequestSummon(); return; }

        var m = Manager();
        if (m == null) { LogOnce("Cat visits: no ThiefCatManager yet; cannot summon."); return; }

        try
        {
            _manualToday = true;
            _visitedToday = false;

            m.isThiefCatGoingToSpawn = true;
            m.thiefCatProbability = 1f;
            m.minimumDayToSpawnThiefCat = 0;
            m.dayCooldownAfterThiefCat = 0;

            var clock = Clock();
            if (clock != null)
            {
                try { m.thiefCatSpawnHour = Mathf.Max(0f, clock.dayHour); } catch { }
            }

            Plugin.Logger.LogInfo($"Cat visits: summoning the cat as {Role()}, day {Today()}, " +
                                  $"game clock {ClockText ?? "?"}.");

            m.CallThiefCatCheat();
            CatVisitsNet.BroadcastNow();
        }
        catch (Exception e)
        {
            _failure = $"{e.GetType().Name}: {e.Message}";
            LogOnce($"Cat visits: summon failed: {_failure}");
        }
    }

    /// <summary>
    /// Adopts a schedule and re-decides today with it. Called from the panel, and on the host
    /// from a client's request.
    /// </summary>
    public static void ApplySchedule(Mode mode, int interval, float hour, string who)
    {
        Plugin.CatVisitMode.Value = (int)mode;
        Plugin.CatVisitInterval.Value = Mathf.Clamp(interval, 1, 30);
        Plugin.CatVisitHour.Value = hour < 0f ? GameHour : Mathf.Clamp(hour, 0f, 23.99f);

        if (FollowsHost) { CatVisitsNet.RequestSchedule(); return; }

        var m = Manager();
        if (m == null) return;

        // A hand-set answer was for the old schedule; choosing a new one is choosing again.
        _manualToday = null;
        Decide(m, who);
    }

    /// <summary>Hands the game back its own numbers.</summary>
    public static void Restore()
    {
        if (!_captured) return;

        var m = Manager();
        if (m != null)
        {
            try
            {
                m.thiefCatProbability = _origProbability;
                m.thiefCatSpawnHour = _origHour;
                m.minimumDayToSpawnThiefCat = _origMinDay;
                m.dayCooldownAfterThiefCat = _origCooldown;
            }
            catch (Exception e)
            {
                LogOnce($"Cat visits: could not restore the game's own numbers: {e.Message}");
            }
        }

        _captured = false;
        _appliedDay = int.MinValue;
        _manualToday = null;
        _failure = null;

        Plugin.Logger.LogInfo("Cat visits: the game's own schedule is back " +
                              $"(probability {_origProbability}, hour {_origHour}, " +
                              $"first day {_origMinDay}, cooldown {_origCooldown}).");
    }

    // ------------------------------------------------------------ what the net needs

    /// <summary>The host's whole answer, as the wire carries it.</summary>
    internal static void ReadState(out Mode mode, out int interval, out float hour,
                                   out bool coming, out bool visited, out int day)
    {
        mode = Sanitise(Plugin.CatVisitMode.Value);
        interval = Mathf.Max(1, Plugin.CatVisitInterval.Value);
        hour = Plugin.CatVisitHour.Value;
        visited = _visitedToday;
        day = Today();

        coming = false;
        try { var m = Manager(); coming = m != null && m.isThiefCatGoingToSpawn; } catch { }
    }

    /// <summary>A client asked the host to change the schedule. Host side only.</summary>
    internal static void AdoptRemoteSchedule(Mode mode, int interval, float hour, ulong sender)
    {
        ApplySchedule(mode, interval, hour, $"người chơi {sender} đổi lịch");
    }

    internal static bool Enabled => Plugin.CatVisitsEnabled.Value && _installed;

    // ----------------------------------------------------------------- plumbing

    private static Mode Sanitise(int value) =>
        value switch
        {
            1 => Mode.Never,
            2 => Mode.EveryNDays,
            3 => Mode.Manual,
            _ => Mode.EveryDay,
        };

    /// <summary>Remembers the game's own numbers the first time the manager is seen.</summary>
    private static void Capture(ThiefCatManager m)
    {
        if (_captured || m == null) return;

        try
        {
            _origProbability = m.thiefCatProbability;
            _origHour = m.thiefCatSpawnHour;
            _origMinDay = m.minimumDayToSpawnThiefCat;
            _origCooldown = m.dayCooldownAfterThiefCat;
            _captured = true;

            Plugin.Logger.LogInfo(
                $"Cat visits: the game's own schedule is probability {_origProbability}, " +
                $"arrival hour {_origHour}, not before day {_origMinDay}, " +
                $"{_origCooldown} day(s) between visits.");
        }
        catch (Exception e)
        {
            LogOnce($"Cat visits: could not read the game's own numbers: {e.Message}");
        }
    }

    private static ThiefCatManager Manager()
    {
        if (_manager != null) return _manager;

        Rescan();
        return _manager;
    }

    private static DayNightSystem Clock()
    {
        if (_clock != null) return _clock;

        Rescan();
        return _clock;
    }

    /// <summary>
    /// One scene search for all three components, on a timer.
    ///
    /// Managers are found this way rather than through <c>GameServer</c> so that a null
    /// anywhere on that chain - which is the normal state in the menu - costs nothing and
    /// throws nothing.
    /// </summary>
    private static void Rescan()
    {
        if (Time.unscaledTime < _nextScan) return;
        _nextScan = Time.unscaledTime + 2f;

        try
        {
            if (_manager == null)
            {
                var found = UnityEngine.Object.FindObjectsByType<ThiefCatManager>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (found != null && found.Length > 0) _manager = found[0];
            }

            if (_restaurant == null)
            {
                var found = UnityEngine.Object.FindObjectsByType<RestaurantManager>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (found != null && found.Length > 0) _restaurant = found[0];
            }

            if (_clock == null)
            {
                var found = UnityEngine.Object.FindObjectsByType<DayNightSystem>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (found != null && found.Length > 0) _clock = found[0];
            }
        }
        catch (Exception e)
        {
            LogOnce($"Cat visits: looking for the managers failed: {e.Message}");
        }
    }

    /// <summary>Today's day number, or int.MinValue when there is no restaurant to ask.</summary>
    public static int Today()
    {
        try
        {
            if (_restaurant == null) Rescan();
            return _restaurant == null ? int.MinValue : _restaurant.Day;
        }
        catch { return int.MinValue; }
    }

    private static int CountCats()
    {
        try
        {
            var found = UnityEngine.Object.FindObjectsByType<ThiefCat>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            return found == null ? 0 : found.Length;
        }
        catch { return 0; }
    }

    private static string SafeComing(ThiefCatManager m)
    {
        try { return m.isThiefCatGoingToSpawn.ToString(); } catch { return "unreadable"; }
    }

    private static string HourOf(ThiefCatManager m)
    {
        try { return m.thiefCatSpawnHour.ToString("0.##"); } catch { return "unreadable"; }
    }

    private static bool SummonKeyPressed()
    {
        try
        {
            string name = Plugin.CatSummonKey.Value;
            if (string.IsNullOrWhiteSpace(name)) return false;

            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) return false;

            if (!Enum.TryParse<UnityEngine.InputSystem.Key>(name, true, out var key) ||
                key == UnityEngine.InputSystem.Key.None)
                return false;

            return kb[key].wasPressedThisFrame;
        }
        catch { return false; }
    }

    /// <summary>True when this machine is the one that may spawn.</summary>
    internal static bool IsHostSide()
    {
        try
        {
            var nm = NetworkManager.Singleton;
            return nm == null || !nm.IsListening || nm.IsServer;
        }
        catch { return true; }
    }

    private static string Role()
    {
        try
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening) return "solo";
            return nm.IsServer ? "host" : "client";
        }
        catch { return "unknown"; }
    }

    private static void LogOnce(string message)
    {
        if (Logged.Add(message)) Plugin.Logger.LogInfo(message);
    }
}
