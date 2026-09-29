using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace PlayerTweaks;

/// <summary>
/// Shares the plate-cabinet exclusions across a co-op session.
///
/// The exclusion started life as a local BepInEx setting, which is wrong the moment a second
/// player is in the restaurant: the waiter and customer AI is driven by the host, so a mark
/// set on a client changed a list nobody consulted, while a mark set on the host was invisible
/// to the client's own menu. Two machines, two answers, and the one that counted was never the
/// one the player was looking at.
///
/// So the host owns the list and everyone else follows it. A client's key press becomes a
/// request; the host applies it, saves it, and pushes the whole list back out. Nothing is
/// applied on a client except what the host has confirmed, which is what makes the floating
/// menu honest - it now shows the state that actually governs the waiters.
///
/// Cabinets travel as position keys - the same keys the host writes to its config. They used to
/// travel as NetworkObjectIds, on the reasoning that the host assigns those and every machine
/// sees the same number. That is true of a NetworkObject, and a cabinet is not necessarily one:
/// DishStock is a NetworkBehaviour, and several behaviours can ride on a single NetworkObject.
/// Where they do, every cabinet reports the same NetworkObjectId, the host resolves a request to
/// whichever cabinet it finds first, and from the client's seat the cabinet it is aiming at
/// simply never changes - which is exactly the report this was rewritten from. A position is
/// per-cabinet by construction, and it needs no translation on arrival: the id the client sends
/// is already the id the host stores.
///
/// The key is a rounded position, so the two machines must agree on the rounding. They agree to
/// within float drift, which 10cm buckets swallow - except for a cabinet standing on a bucket
/// boundary, where the two could round apart. Hence the host's fallback to the nearest cabinet
/// within a quarter of a metre of the position the key describes: the key carries a place, so a
/// near miss is recoverable rather than fatal.
///
/// The wire is Netcode's own <c>CustomMessagingManager</c> named messages, which is the one
/// channel a mod may use without adding RPCs to the game's NetworkBehaviours. Receiving is a
/// prefix on <c>InvokeNamedMessage</c> rather than a registered handler: the handler table
/// wants an IL2CPP delegate built around a by-value <c>FastBufferReader</c>, and a Harmony
/// prefix gets the same arguments without that conversion.
/// </summary>
internal static class PlateStocksNet
{
    private const string MessageName = "PT.PlateStocks";

    /// <summary>How often the host repeats the list. Cheap, and it heals a missed join.</summary>
    private const float BroadcastSeconds = 2f;

    /// <summary>How long a client waits before deciding the host has no mod to answer with.</summary>
    private const float HostSilentSeconds = 8f;

    /// <summary>How often an unanswered request is repeated, and when to stop waiting.</summary>
    private const float RetrySeconds = 0.6f;
    private const float RequestTimeoutSeconds = 6f;

    /// <summary>How often a client that has heard nothing asks the host to say something,
    /// and how many times before it accepts that nobody is going to answer.</summary>
    private const float HelloSeconds = 1.5f;
    private const int HelloAttempts = 12;

    private static readonly HashSet<string> Logged = new();

    // ------------------------------------------------------------------ state

    private static bool _installed;
    private static bool _hashed;
    private static ulong _hash32, _hash64;

    /// <summary>Cabinets the host says are excluded, by position key. Client-side only.</summary>
    private static readonly HashSet<string> Remote = new(StringComparer.Ordinal);

    private static float _nextBroadcast;
    private static float _lastHeard = -1f;
    private static float _clientSince = -1f;
    private static float _nextHello;
    private static int _hellos;
    private static bool _wasClient;
    private static bool _pinged;
    private static bool _pingHeard;

    // The one request a client has asked for and not yet been answered about. Kept so it can
    // be repeated - a request that is silently dropped once would otherwise look exactly like
    // a key that does nothing - and so the menu can show what was asked for while it is in
    // flight instead of flicking back on every broadcast that arrives meanwhile.
    private static string _pendingKey;
    private static bool _pendingExclude;
    private static float _pendingSince = -1f;
    private static float _pendingSent;
    private static bool _requestFailed;

    /// <summary>A client's request is still waiting on the host.</summary>
    internal static bool RequestPending => _pendingSince >= 0f;

    /// <summary>The host never answered the last request, so the menu should stop implying it did.</summary>
    internal static bool RequestFailed => _requestFailed;

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

    /// <summary>A short note about who is deciding, for the settings panel.</summary>
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
                log.LogWarning("PlateStocksNet: InvokeNamedMessage not found; co-op sync off.");
                return;
            }

            new HarmonyLib.Harmony(Plugin.Guid + ".platestocksnet").Patch(
                target,
                prefix: new HarmonyLib.HarmonyMethod(
                    HarmonyLib.AccessTools.Method(typeof(PlateStocksNet), nameof(OnNamedMessage))));

            _installed = true;
            log.LogInfo("PlateStocksNet: patched InvokeNamedMessage; co-op sync on.");
        }
        catch (Exception e)
        {
            log.LogWarning($"PlateStocksNet: install failed, co-op sync off: {e.GetType().Name}: {e.Message}");
        }
    }

    // ------------------------------------------------------------------- tick

    /// <summary>
    /// Keeps the two sides talking: the host repeats its list, a client introduces itself and
    /// notices when it has been answered.
    /// </summary>
    internal static void Tick()
    {
        if (!_installed) return;

        NetworkManager nm;
        try { nm = NetworkManager.Singleton; } catch { return; }

        if (nm == null || !SafeIsListening(nm))
        {
            // Left the session: drop the host's list so a later solo game is not governed
            // by keys from a restaurant that is no longer loaded.
            if (_wasClient) Reset();
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

        // Host side. Two checks, once per session, so a solo game still reports whether the
        // channel works - there is no way to try this against another player from here.
        if (!_pinged)
        {
            _pinged = true;
            SelfTest();
            Send("~W", true, 0UL);
        }

        if (Time.unscaledTime < _nextBroadcast) return;
        _nextBroadcast = Time.unscaledTime + BroadcastSeconds;

        if (Others()) Broadcast();
    }

    private static void Reset()
    {
        Remote.Clear();
        _lastHeard = -1f;
        _clientSince = -1f;
        _pendingSince = -1f;
        _pendingKey = null;
        _requestFailed = false;
    }

    /// <summary>
    /// The client half of the tick: introduce ourselves until the host answers, and repeat any
    /// request it has not acted on yet.
    ///
    /// The hello matters more than it looks. Without it a client's only way to learn the host
    /// has the mod is the host's own two-second repeat, and that repeat is only sent while the
    /// host believes somebody else is connected - so a client that joins into a mis-read
    /// connection list waits forever and reports the host as unmodded. Asking costs one packet.
    /// </summary>
    private static void TickClient()
    {
        float now = Time.unscaledTime;

        // Capped rather than endless. Once a host has stayed quiet for twenty seconds it is
        // not running this mod, and going on knocking is traffic on somebody else's session
        // for an answer that is not coming. A key press asks again anyway - see RequestToggle,
        // whose own retry is what covers a host that joined the party late.
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
            _requestFailed = true;
            Plugin.Logger.LogWarning(
                $"PlateStocksNet: the host did not answer the request for '{_pendingKey}' within " +
                $"{RequestTimeoutSeconds:0}s. Either it is not running this mod, or it has no " +
                "cabinet at that spot - compare the cabinet list each side logs on startup.");
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

    // ------------------------------------------------------------- host reads

    /// <summary>Whether the host has said this cabinet is excluded. Only asked on a client.</summary>
    internal static bool RemoteHas(DishStock stock)
    {
        string key = PlateStocks.KeyOf(stock);
        if (key == null) return false;

        // A request in flight shows what was asked for. The host keeps broadcasting the old
        // list until it acts on the request, and without this the menu would flick back and
        // forth every couple of seconds while the answer was still on its way.
        if (_pendingSince >= 0f && key == _pendingKey) return _pendingExclude;

        return Remote.Count != 0 && Remote.Contains(key);
    }

    /// <summary>The host's count, adjusted for a request that has not been answered yet.</summary>
    internal static int RemoteCount
    {
        get
        {
            int n = Remote.Count;
            if (_pendingSince < 0f || _pendingKey == null) return n;

            bool listed = Remote.Contains(_pendingKey);
            if (_pendingExclude && !listed) return n + 1;
            if (!_pendingExclude && listed) return n - 1;
            return n;
        }
    }

    internal static bool RemoteKnown => _lastHeard >= 0f;

    /// <summary>
    /// True once a client has waited long enough that silence means the host is not running
    /// this mod, rather than that the first broadcast is still on its way.
    /// </summary>
    internal static bool HostSilent
        => FollowsHost && _lastHeard < 0f &&
           _clientSince >= 0f && Time.unscaledTime - _clientSince > HostSilentSeconds;

    // ---------------------------------------------------------- client writes

    /// <summary>Asks the host to mark or unmark a cabinet, and keeps asking until it answers.</summary>
    internal static void RequestToggle(DishStock stock, bool exclude)
    {
        string key = PlateStocks.KeyOf(stock);
        if (key == null)
        {
            LogOnce("PlateStocksNet: cabinet has no readable position; cannot ask the host.");
            return;
        }

        _pendingKey = key;
        _pendingExclude = exclude;
        _pendingSince = Time.unscaledTime;
        _requestFailed = false;

        // A key press is a fresh reason to believe somebody might answer, so the hello budget
        // starts over: a host that was still loading when the client gave up gets asked again.
        _hellos = 0;
        _nextHello = 0f;

        // Deliberately not LogOnce: one line per key press is what lets an unanswered request
        // tell its own story in the log, and key presses are rare.
        Plugin.Logger.LogInfo(
            $"PlateStocksNet: asking the host to {(exclude ? "exclude" : "include")} " +
            $"'{PlateStocks.Label(stock)}' [{key}].");

        SendPending();
    }

    private static void SendPending()
    {
        if (_pendingKey == null) return;

        _pendingSent = Time.unscaledTime;
        Send("!" + _pendingKey + "=" + (_pendingExclude ? "1" : "0"), false, ServerId());
    }

    /// <summary>Asks the host to clear every exclusion.</summary>
    internal static void RequestClear()
    {
        // Not retried: unlike a toggle there is no per-cabinet answer to wait for, and the
        // host's next broadcast lands within two seconds either way.
        Remote.Clear();
        _pendingSince = -1f;
        _pendingKey = null;
        Send("c", false, ServerId());
    }

    // ------------------------------------------------------------- host write

    /// <summary>Pushes the host's list to everyone. Safe to call when there is no session.</summary>
    internal static void Broadcast()
    {
        if (!_installed || FollowsHost) return;

        var text = new System.Text.StringBuilder("=");
        bool first = true;

        foreach (var stock in PlateStocks.Cabinets())
        {
            if (stock == null || !PlateStocks.IsExcluded(stock)) continue;

            string key = PlateStocks.KeyOf(stock);
            if (key == null) continue;

            if (!first) text.Append(',');
            text.Append(key);
            first = false;
        }

        Send(text.ToString(), true, 0UL);
    }

    /// <summary>Sends the list now rather than waiting for the next repeat.</summary>
    internal static void BroadcastNow()
    {
        if (!_installed || FollowsHost || !Others()) return;

        _nextBroadcast = Time.unscaledTime + BroadcastSeconds;
        Broadcast();
    }

    // --------------------------------------------------------------- the wire

    private static bool _selfTestSeen;

    /// <summary>
    /// Hands a message to Netcode's own dispatcher locally and checks it comes back out of our
    /// patch intact.
    ///
    /// The doubtful part of this whole design is a Harmony prefix taking a by-value IL2CPP
    /// struct - if that marshalling is wrong the feature fails silently and only in co-op,
    /// where it is hardest to see. This asks the question in a solo game instead, and writes
    /// the answer to the log either way.
    /// </summary>
    private static void SelfTest()
    {
        const string payload = "~L";

        try
        {
            var messaging = NetworkManager.Singleton?.CustomMessagingManager;
            if (messaging == null) return;

            int size = payload.Length + 8;
            var writer = new FastBufferWriter(size, Allocator.Temp, size);

            try
            {
                writer.WriteValueSafe(payload, true);

                var reader = new FastBufferReader(writer, Allocator.Temp, -1, 0);
                try
                {
                    _selfTestSeen = false;
                    Ours(0UL);                       // force the hashes to be computed
                    messaging.InvokeNamedMessage(_hash32 != 0UL ? _hash32 : _hash64, 0UL, reader, 0);
                }
                finally { reader.Dispose(); }
            }
            finally { writer.Dispose(); }

            Plugin.Logger.LogInfo(_selfTestSeen
                ? "PlateStocksNet: self-test passed - co-op sync should work."
                : "PlateStocksNet: self-test FAILED - messages are not reaching the patch.");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning(
                $"PlateStocksNet: self-test threw: {e.GetType().Name}: {e.Message}");
        }
    }

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
        // the other end of it - and it is the difference between a client being able to mark
        // a cabinet and the key quietly doing nothing.
        if (TrySend(messaging, payload, true, 0UL, "to the host as a broadcast"))
            LogOnce("PlateStocksNet: direct sends are refused here; using the broadcast form.");
    }

    private static bool TrySend(CustomMessagingManager messaging, string payload, bool toAll,
                                ulong clientId, string what)
    {
        // One byte per character: every payload here is ASCII letters, digits and punctuation.
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
            LogOnce($"PlateStocksNet: send {what} failed: {e.GetType().Name}: {e.Message}");
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
    ///
    /// Netcode hashes the message name with either width depending on its config, so both are
    /// compared; everything that is not ours is handed straight back to the original.
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
                LogOnce($"PlateStocksNet: unreadable message: {e.GetType().Name}: {e.Message}");
                return false;
            }

            Handle(sender, payload);
        }
        catch (Exception e)
        {
            LogOnce($"PlateStocksNet: receive failed: {e.GetType().Name}: {e.Message}");
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
            case '~':
                // Our own two probes. Neither carries state; they only prove a path works.
                if (payload == "~L") _selfTestSeen = true;
                else if (!_pingHeard)
                {
                    _pingHeard = true;
                    Plugin.Logger.LogInfo("PlateStocksNet: loopback over the wire confirmed.");
                }
                return;

            case '=':
                if (host) return;             // our own broadcast coming back to us

                Remote.Clear();
                foreach (var part in payload.Substring(1).Split(','))
                {
                    var key = part.Trim();
                    if (key.Length > 0) Remote.Add(key);
                }

                if (_lastHeard < 0f)
                    Plugin.Logger.LogInfo("PlateStocksNet: the host is running this mod; it " +
                                          $"excludes {Remote.Count} cabinet(s).");

                _lastHeard = Time.unscaledTime;

                // This list is the host's answer. Any broadcast that already agrees with what
                // was asked for settles the request, whether the host acted on it or was
                // simply in that state to begin with.
                if (_pendingSince >= 0f && _pendingKey != null &&
                    Remote.Contains(_pendingKey) == _pendingExclude)
                {
                    _pendingSince = -1f;
                    _requestFailed = false;
                }
                return;

            case '?':
                // A client saying hello. Answering at once is what lets it stop guessing from
                // silence; the periodic repeat then keeps it in step.
                if (!host) return;
                Broadcast();
                return;

            case '!':
                if (!host) return;            // only the host acts on requests
                ApplyRequest(sender, payload);
                return;

            case 'c':
                if (!host) return;
                PlateStocks.IncludeAll();
                return;
        }
    }

    private static void ApplyRequest(ulong sender, string payload)
    {
        int split = payload.IndexOf('=');
        if (split < 2) return;

        string key = payload.Substring(1, split - 1).Trim();
        if (key.Length == 0) return;

        bool exclude = payload.Substring(split + 1).Trim() == "1";

        var stock = PlateStocks.CabinetByKey(key);
        if (stock == null)
        {
            LogOnce($"PlateStocksNet: player {sender} asked about a cabinet at '{key}', and there " +
                    "is none there on this machine. The two sides disagree about where the " +
                    "cabinets stand; the cabinet list each logs on startup will show by how much.");
            return;
        }

        Plugin.Logger.LogInfo(
            $"PlateStocksNet: player {sender} {(exclude ? "excluded" : "included")} " +
            $"'{PlateStocks.Label(stock)}' [{key}].");

        PlateStocks.SetExcluded(stock, exclude);
    }

    private static void LogOnce(string message)
    {
        if (Logged.Add(message)) Plugin.Logger.LogInfo(message);
    }
}
