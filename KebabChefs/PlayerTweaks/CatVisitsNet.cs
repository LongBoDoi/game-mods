using System;
using System.Collections.Generic;
using System.Globalization;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace PlayerTweaks;

/// <summary>
/// Carries the cat's schedule across a co-op session.
///
/// The cat is a NetworkObject, so only the host can put one in the restaurant. That makes
/// every lever on <c>ThiefCatManager</c> host-side by construction: a client writing the
/// day's verdict writes to a field its own machine will never act on, and the player sitting
/// at that machine would have no way of telling. So the host owns the schedule and repeats
/// it, and a client's panel is a request form - it shows the host's answer, never its own
/// guess.
///
/// Same wire as the plate cabinets: Netcode's <c>CustomMessagingManager</c> named messages,
/// received through a Harmony prefix on <c>InvokeNamedMessage</c> rather than a registered
/// handler, because the handler table wants an IL2CPP delegate built around a by-value
/// <c>FastBufferReader</c>. Two prefixes on that method coexist: each returns false only for
/// its own message name, and hands everything else back.
///
/// One request is deliberately not retried. A schedule or a "not today" can be repeated
/// safely - the host ends in the same state however many arrive - but a summon is not
/// idempotent, and a retry that arrives after the first one landed is a second cat. It is
/// sent once; if the packet is lost the button is still there to press.
/// </summary>
internal static class CatVisitsNet
{
    private const string MessageName = "PT.CatVisits";

    /// <summary>How often the host repeats the schedule. Cheap, and it heals a missed join.</summary>
    private const float BroadcastSeconds = 2f;

    /// <summary>How long a client waits before deciding the host has no mod to answer with.</summary>
    private const float HostSilentSeconds = 8f;

    private const float RetrySeconds = 0.6f;
    private const float RequestTimeoutSeconds = 6f;

    private const float HelloSeconds = 1.5f;
    private const int HelloAttempts = 12;

    private static readonly HashSet<string> Logged = new();

    // ------------------------------------------------------------------ state

    private static bool _installed;
    private static bool _hashed;
    private static ulong _hash32, _hash64;

    private static float _nextBroadcast;
    private static float _lastHeard = -1f;
    private static float _clientSince = -1f;
    private static float _nextHello;
    private static int _hellos;
    private static bool _wasClient;

    /// <summary>The host's answer, as last heard. Client-side only.</summary>
    private static CatVisits.Mode _mode = CatVisits.Mode.EveryDay;
    private static int _interval = 2;
    private static float _hour = CatVisits.GameHour;
    private static bool _coming;
    private static bool _visited;
    private static int _day = int.MinValue;

    /// <summary>
    /// Whether the host has the feature switched on. Carried rather than inferred from
    /// silence: a host running the mod with the option off looks exactly like a host with no
    /// mod at all, and those are two different things to tell the player.
    /// </summary>
    private static bool _hostOn;

    // The one request in flight, kept so it can be repeated and so the panel can show what
    // was asked for instead of flicking back on every broadcast that arrives meanwhile.
    private static string _pending;
    private static float _pendingSince = -1f;
    private static float _pendingSent;
    private static bool _requestFailed;

    /// <summary>What a pending request asked for, so the panel can show it while it is in flight.</summary>
    private static bool _wantComing;
    private static bool _wantComingPending;

    // ------------------------------------------------------------------ read-out

    /// <summary>True when this machine must defer to a host rather than decide for itself.</summary>
    internal static bool FollowsHost
    {
        get
        {
            try
            {
                var nm = NetworkManager.Singleton;
                return nm != null && nm.IsListening && !nm.IsServer;
            }
            catch { return false; }
        }
    }

    internal static bool HostKnown => _lastHeard >= 0f;

    /// <summary>
    /// True once a client has waited long enough that silence means the host is not running
    /// this mod, rather than that the first broadcast is still on its way.
    /// </summary>
    internal static bool HostSilent
        => FollowsHost && _lastHeard < 0f &&
           _clientSince >= 0f && Time.unscaledTime - _clientSince > HostSilentSeconds;

    internal static CatVisits.Mode RemoteMode => _mode;
    internal static int RemoteInterval => _interval;
    internal static float RemoteHour => _hour;
    internal static bool RemoteVisited => _visited;
    internal static int RemoteDay => _day;
    internal static bool RemoteEnabled => _hostOn;

    /// <summary>Whether the host says the cat is coming, or what we have just asked it for.</summary>
    internal static bool RemoteComing => _wantComingPending && _pendingSince >= 0f ? _wantComing : _coming;

    internal static bool RequestPending => _pendingSince >= 0f;
    internal static bool RequestFailed => _requestFailed;

    /// <summary>
    /// A schedule change is on its way to the host. While it is, the panel shows what was
    /// asked for rather than the host's old answer - otherwise a radio button would sit on
    /// the previous choice for a round trip and read as a dead control.
    /// </summary>
    internal static bool SchedulePending
        => _pendingSince >= 0f && _pending != null && _pending[0] == '!';

    /// <summary>A short note about who is deciding, for the panel.</summary>
    internal static string StatusSuffix
    {
        get
        {
            if (!_installed) return "";

            if (FollowsHost)
            {
                if (_lastHeard >= 0f) return "theo chủ phòng";
                if (_clientSince >= 0f && Time.unscaledTime - _clientSince > HostSilentSeconds)
                    return "chủ phòng chưa cài mod";
                return "đang chờ chủ phòng";
            }

            return Others() ? "bạn là chủ phòng" : "";
        }
    }

    // ---------------------------------------------------------------- install

    internal static void Install(BepInEx.Logging.ManualLogSource log)
    {
        try
        {
            var target = HarmonyLib.AccessTools.Method(
                typeof(CustomMessagingManager), nameof(CustomMessagingManager.InvokeNamedMessage));

            if (target == null)
            {
                log.LogWarning("CatVisitsNet: InvokeNamedMessage not found; co-op sync off.");
                return;
            }

            new HarmonyLib.Harmony(Plugin.Guid + ".catvisitsnet").Patch(
                target,
                prefix: new HarmonyLib.HarmonyMethod(
                    HarmonyLib.AccessTools.Method(typeof(CatVisitsNet), nameof(OnNamedMessage))));

            _installed = true;
            log.LogInfo("CatVisitsNet: patched InvokeNamedMessage; co-op sync on.");
        }
        catch (Exception e)
        {
            log.LogWarning($"CatVisitsNet: install failed, co-op sync off: {e.GetType().Name}: {e.Message}");
        }
    }

    // ------------------------------------------------------------------- tick

    internal static void Tick()
    {
        if (!_installed) return;

        NetworkManager nm;
        try { nm = NetworkManager.Singleton; } catch { return; }

        if (nm == null || !SafeIsListening(nm))
        {
            // Left the session: forget the host's schedule so a later solo game is not shown
            // numbers from a restaurant that is no longer loaded.
            if (_wasClient) { Reset(); _wasClient = false; }
            return;
        }

        bool client = FollowsHost;

        if (client != _wasClient)
        {
            Reset();
            _wasClient = client;
            _clientSince = client ? Time.unscaledTime : -1f;
            _nextHello = 0f;
            _hellos = 0;
        }

        if (client) { TickClient(); return; }

        if (Time.unscaledTime < _nextBroadcast) return;
        _nextBroadcast = Time.unscaledTime + BroadcastSeconds;

        if (Others()) Broadcast();
    }

    private static void Reset()
    {
        _lastHeard = -1f;
        _clientSince = -1f;
        _pending = null;
        _pendingSince = -1f;
        _wantComingPending = false;
        _requestFailed = false;
        _day = int.MinValue;
        _hostOn = false;
    }

    private static void TickClient()
    {
        float now = Time.unscaledTime;

        // Capped rather than endless: once a host has stayed quiet this long it is not running
        // the mod, and going on knocking is traffic on somebody else's session for an answer
        // that is not coming. Any request starts the budget over.
        if (_lastHeard < 0f && _hellos < HelloAttempts && now >= _nextHello)
        {
            _hellos++;
            _nextHello = now + HelloSeconds;
            Send("?", false, ServerId());
        }

        if (_pendingSince < 0f) return;

        if (now - _pendingSince > RequestTimeoutSeconds)
        {
            _pendingSince = -1f;
            _wantComingPending = false;
            _requestFailed = true;
            Plugin.Logger.LogWarning(
                $"CatVisitsNet: the host did not answer '{_pending}' within {RequestTimeoutSeconds:0}s. " +
                "Either it is not running this mod, or it has the cat schedule switched off.");
            return;
        }

        if (now - _pendingSent >= RetrySeconds) SendPending();
    }

    private static bool SafeIsListening(NetworkManager nm)
    {
        try { return nm.IsListening; }
        catch { return false; }
    }

    /// <summary>Whether anyone else is connected, so the host can stay quiet in a solo game.</summary>
    private static bool Others()
    {
        try
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening) return false;

            // The interop stub for IReadOnlyList exposes only the indexer, so the count has to
            // come from the List it is really backed by.
            var ids = nm.ConnectedClientsIds;
            var list = ids?.TryCast<Il2CppSystem.Collections.Generic.List<ulong>>();

            return list == null || list.Count > 1;
        }
        catch { return true; }   // if the count cannot be read, talking costs less than silence
    }

    // ---------------------------------------------------------- client writes

    /// <summary>Asks the host to use this schedule. Safe to repeat.</summary>
    internal static void RequestSchedule()
    {
        var mode = (int)CatVisits.Mode.EveryDay;
        try { mode = Plugin.CatVisitMode.Value; } catch { }

        string payload = "!" + mode.ToString(CultureInfo.InvariantCulture) + "," +
                         Plugin.CatVisitInterval.Value.ToString(CultureInfo.InvariantCulture) + "," +
                         Plugin.CatVisitHour.Value.ToString("0.##", CultureInfo.InvariantCulture);

        _wantComingPending = false;
        Begin(payload, $"đổi lịch mèo: {payload.Substring(1)}");
    }

    /// <summary>Asks the host to bring - or not bring - the cat today. Safe to repeat.</summary>
    internal static void RequestToday(bool coming)
    {
        _wantComing = coming;
        _wantComingPending = true;
        Begin("t" + (coming ? "1" : "0"), coming ? "xin mèo ghé hôm nay" : "xin mèo không ghé hôm nay");
    }

    /// <summary>
    /// Asks the host to bring the cat now. Sent once and never retried - a summon is not
    /// idempotent, and a retry arriving after the first landed is a second cat.
    /// </summary>
    internal static void RequestSummon()
    {
        _pending = null;
        _pendingSince = -1f;
        _wantComingPending = false;
        _requestFailed = false;

        _hellos = 0;
        _nextHello = 0f;

        Plugin.Logger.LogInfo("CatVisitsNet: asking the host to summon the cat.");
        Send("s", false, ServerId());
    }

    private static void Begin(string payload, string what)
    {
        _pending = payload;
        _pendingSince = Time.unscaledTime;
        _requestFailed = false;

        // A press is a fresh reason to believe somebody might answer, so the hello budget
        // starts over: a host still loading when the client gave up gets asked again.
        _hellos = 0;
        _nextHello = 0f;

        // One line per press on purpose, not LogOnce: it is what lets an unanswered request
        // tell its own story in the log, and presses are rare.
        Plugin.Logger.LogInfo($"CatVisitsNet: {what}.");

        SendPending();
    }

    private static void SendPending()
    {
        if (_pending == null) return;

        _pendingSent = Time.unscaledTime;
        Send(_pending, false, ServerId());
    }

    // ------------------------------------------------------------- host write

    /// <summary>Pushes the host's schedule to everyone. Safe to call when there is no session.</summary>
    internal static void Broadcast()
    {
        if (!_installed || FollowsHost) return;

        CatVisits.ReadState(out var mode, out int interval, out float hour,
                            out bool coming, out bool visited, out int day);

        string payload = "=" +
            ((int)mode).ToString(CultureInfo.InvariantCulture) + "," +
            interval.ToString(CultureInfo.InvariantCulture) + "," +
            hour.ToString("0.##", CultureInfo.InvariantCulture) + "," +
            (coming ? "1" : "0") + "," +
            (visited ? "1" : "0") + "," +
            day.ToString(CultureInfo.InvariantCulture) + "," +
            (CatVisits.Enabled ? "1" : "0");

        Send(payload, true, 0UL);
    }

    /// <summary>Sends the schedule now rather than waiting for the next repeat.</summary>
    internal static void BroadcastNow()
    {
        if (!_installed || FollowsHost || !Others()) return;

        _nextBroadcast = Time.unscaledTime + BroadcastSeconds;
        Broadcast();
    }

    // --------------------------------------------------------------- the wire

    private static ulong ServerId()
    {
        try { return NetworkManager.ServerClientId; }
        catch { return 0UL; }
    }

    private static void Send(string payload, bool toAll, ulong clientId)
    {
        if (!_installed || string.IsNullOrEmpty(payload)) return;

        var messaging = Messaging();
        if (messaging == null) return;

        if (toAll) { TrySend(messaging, payload, true, 0UL, "to everyone"); return; }

        if (TrySend(messaging, payload, false, clientId, "to the host")) return;

        // Some Netcode versions treat the single-target send as the server's to make. On a
        // client the broadcast form costs the same one packet - the host is the only thing on
        // the other end of it.
        if (TrySend(messaging, payload, true, 0UL, "to the host as a broadcast"))
            LogOnce("CatVisitsNet: direct sends are refused here; using the broadcast form.");
    }

    private static bool TrySend(CustomMessagingManager messaging, string payload, bool toAll,
                                ulong clientId, string what)
    {
        // One byte per character: every payload here is ASCII digits and punctuation.
        int size = payload.Length + 8;
        var writer = new FastBufferWriter(size, Allocator.Temp, size);

        try
        {
            writer.WriteValueSafe(payload, true);

            if (toAll)
                messaging.SendNamedMessageToAll(MessageName, writer, NetworkDelivery.Reliable);
            else
                messaging.SendNamedMessage(MessageName, clientId, writer, NetworkDelivery.Reliable);

            return true;
        }
        catch (Exception e)
        {
            LogOnce($"CatVisitsNet: send {what} failed: {e.GetType().Name}: {e.Message}");
            return false;
        }
        finally { writer.Dispose(); }
    }

    private static CustomMessagingManager Messaging()
    {
        try
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening) return null;

            return nm.CustomMessagingManager;
        }
        catch { return null; }
    }

    /// <summary>
    /// Catches our own named message before Netcode looks for a handler it will not find.
    /// Anything that is not ours is handed straight back to the original, which is what lets
    /// this share the method with the plate-cabinet channel.
    /// </summary>
    private static bool OnNamedMessage(ulong hash, ulong sender, FastBufferReader reader)
    {
        try
        {
            if (!Ours(hash)) return true;

            string payload = null;
            try { reader.ReadValueSafe(out payload, true); }
            catch (Exception e)
            {
                LogOnce($"CatVisitsNet: unreadable message: {e.GetType().Name}: {e.Message}");
                return false;
            }

            Handle(sender, payload);
        }
        catch (Exception e)
        {
            LogOnce($"CatVisitsNet: receive failed: {e.GetType().Name}: {e.Message}");
        }

        return false;
    }

    private static bool Ours(ulong hash)
    {
        if (!_hashed)
        {
            _hashed = true;
            try { _hash32 = XXHash.Hash32(MessageName); } catch { }
            try { _hash64 = XXHash.Hash64(MessageName); } catch { }
        }

        return (_hash32 != 0UL && hash == _hash32) || (_hash64 != 0UL && hash == _hash64);
    }

    private static void Handle(ulong sender, string payload)
    {
        if (string.IsNullOrEmpty(payload)) return;

        bool host = !FollowsHost;

        switch (payload[0])
        {
            case '=':
                if (host) return;                 // our own broadcast coming back to us
                ReadBroadcast(payload);
                return;

            case '?':
                // A client saying hello. Answering at once is what lets it stop guessing from
                // silence; the periodic repeat then keeps it in step.
                if (!host) return;
                Broadcast();
                return;

            case '!':
                if (!host) return;
                ApplySchedule(sender, payload);
                return;

            case 't':
                if (!host) return;
                if (!CatVisits.Enabled) { LogOnce($"CatVisitsNet: player {sender} asked about today, " +
                                                  "and the cat schedule is switched off here."); return; }

                Plugin.Logger.LogInfo($"CatVisitsNet: player {sender} asked for the cat " +
                                      $"{(payload.Length > 1 && payload[1] == '1' ? "to come" : "not to come")} today.");
                CatVisits.SetComingToday(payload.Length > 1 && payload[1] == '1');
                Broadcast();
                return;

            case 's':
                if (!host) return;
                if (!CatVisits.Enabled) { LogOnce($"CatVisitsNet: player {sender} asked to summon the cat, " +
                                                  "and the cat schedule is switched off here."); return; }

                Plugin.Logger.LogInfo($"CatVisitsNet: player {sender} asked to summon the cat.");
                CatVisits.SummonNow();
                Broadcast();
                return;
        }
    }

    private static void ReadBroadcast(string payload)
    {
        var parts = payload.Substring(1).Split(',');
        if (parts.Length < 7) return;

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mode)) return;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int interval)) return;
        if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float hour)) return;
        if (!int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int day)) day = int.MinValue;

        _mode = mode switch
        {
            1 => CatVisits.Mode.Never,
            2 => CatVisits.Mode.EveryNDays,
            3 => CatVisits.Mode.Manual,
            _ => CatVisits.Mode.EveryDay,
        };
        _interval = Mathf.Max(1, interval);
        _hour = hour;
        _coming = parts[3] == "1";
        _visited = parts[4] == "1";
        _day = day;
        _hostOn = parts[6] == "1";

        if (_lastHeard < 0f)
            Plugin.Logger.LogInfo(_hostOn
                ? $"CatVisitsNet: the host is running this mod - mode {_mode}, every {_interval} day(s), " +
                  $"arrival hour {_hour}, cat coming today = {_coming}."
                : "CatVisitsNet: the host is running this mod but has the cat schedule switched " +
                  "off, so the game's own roll decides and requests from here are refused.");

        _lastHeard = Time.unscaledTime;

        // The host's own state is the answer to any request. A broadcast that already agrees
        // settles it, whether the host acted on it or was in that state to begin with.
        if (_pendingSince >= 0f && Settled())
        {
            _pendingSince = -1f;
            _wantComingPending = false;
            _requestFailed = false;
        }
    }

    /// <summary>Whether the host's latest broadcast matches what was asked for.</summary>
    private static bool Settled()
    {
        if (_pending == null) return true;

        if (_pending[0] == 't') return _coming == _wantComing;

        if (_pending[0] == '!')
        {
            try
            {
                return (int)_mode == Plugin.CatVisitMode.Value &&
                       _interval == Mathf.Max(1, Plugin.CatVisitInterval.Value) &&
                       Mathf.Abs(_hour - Plugin.CatVisitHour.Value) < 0.01f;
            }
            catch { return true; }
        }

        return true;
    }

    private static void ApplySchedule(ulong sender, string payload)
    {
        if (!CatVisits.Enabled)
        {
            LogOnce($"CatVisitsNet: player {sender} sent a cat schedule, and the feature is " +
                    "switched off here - the host decides, so nothing was changed.");
            return;
        }

        var parts = payload.Substring(1).Split(',');
        if (parts.Length < 3) return;

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mode)) return;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int interval)) return;
        if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float hour)) return;

        var chosen = mode switch
        {
            1 => CatVisits.Mode.Never,
            2 => CatVisits.Mode.EveryNDays,
            3 => CatVisits.Mode.Manual,
            _ => CatVisits.Mode.EveryDay,
        };

        Plugin.Logger.LogInfo($"CatVisitsNet: player {sender} set the cat schedule to {chosen}, " +
                              $"every {interval} day(s), arrival hour {hour}.");

        CatVisits.AdoptRemoteSchedule(chosen, interval, hour, sender);
        Broadcast();
    }

    private static void LogOnce(string message)
    {
        if (Logged.Add(message)) Plugin.Logger.LogInfo(message);
    }
}
