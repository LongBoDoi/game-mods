using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Team17.Online;
using Team17.Online.Multiplayer;
using Team17.Online.Multiplayer.Connection;

namespace OC2Tweaks
{
    internal struct ConnectionSample
    {
        internal bool Available, Measured;
        internal float PingMs, LastReplyTime;
    }

    internal sealed class PlayerConnection
    {
        internal ConnectionSample Sample;
        /// <summary>This machine's own row.</summary>
        internal bool Local;
        /// <summary>
        /// Another client's row, seen from a client: nobody here can measure it, so the figure can
        /// only arrive from the host (see <see cref="ConnectionMonitor.ReceiveRelay"/>).
        /// </summary>
        internal bool NeedsRelay;
    }

    /// <summary>One measured player the host passes on to its clients.</summary>
    internal struct RelayEntry
    {
        internal byte Id;
        internal float PingMs;
        internal bool Stale;
    }

    internal static class ConnectionMonitor
    {
        private static readonly FieldInfo LocalServer = AccessTools.Field(typeof(ConnectionModeSwitcher), "m_LocalServer");
        private static readonly FieldInfo LocalClient = AccessTools.Field(typeof(ConnectionModeSwitcher), "m_LocalClient");
        private static readonly FieldInfo Connections = AccessTools.Field(typeof(Server), "m_RemoteClientConnections");
        private static readonly FieldInfo ClientConnectionField = AccessTools.Field(typeof(Team17.Online.Multiplayer.Client), "m_ServerConnection");
        private static readonly FieldInfo Reliable = AccessTools.Field(typeof(RemoteConnection), "m_ReliableLatencyMeasure");
        private static readonly FieldInfo Unreliable = AccessTools.Field(typeof(RemoteConnection), "m_UnreliableLatencyMeasure");
        private static readonly FieldInfo History = AccessTools.Field(typeof(LatencyMeasure), "m_OneWayLatencyHistory");
        private static readonly FieldInfo ReplyTime = AccessTools.Field(typeof(LatencyMeasure), "m_fTimeTimeReceived");
        private static readonly Dictionary<IOnlineMultiplayerSessionUserId, PlayerConnection> players =
            new Dictionary<IOnlineMultiplayerSessionUserId, PlayerConnection>();

        // ---- Host -> client relay ------------------------------------------------------------
        // A client only holds a connection to the host, so it cannot measure anyone else. The host
        // passes its own figures on inside the game's latency probe, with a negative send time as
        // the marker: real probes carry Time.realtimeSinceStartup, which is never negative.
        // PingRelay holds the wire side; this file keeps the encoding and the figures.

        /// <summary>Largest ping that fits the 14-bit field; anything above is shown as this.</summary>
        internal const int RelayMaxMs = 16383;
        /// <summary>How long a figure counts as current. The host relays once a second.</summary>
        internal const float FreshSeconds = 3f;
        /// <summary>After this long in a room with no relay at all, the host evidently lacks the mod.</summary>
        internal const float HostSilentSeconds = 6f;

        private struct RelayedPing
        {
            internal float PingMs, ReceivedAt;
            internal bool HostStale;
        }
        private static readonly Dictionary<byte, RelayedPing> relayed = new Dictionary<byte, RelayedPing>();
        private static float clientSince = -1f, lastRelayAt = -1f;

        internal static void Validate()
        {
            foreach (FieldInfo field in new[] { LocalServer, LocalClient, Connections, ClientConnectionField, Reliable, Unreliable, History, ReplyTime })
                if (field == null) throw new MissingFieldException("Không thể đọc dữ liệu độ trễ từ trò chơi.");
        }

        internal static PlayerConnection Get(IOnlineMultiplayerSessionUserId session)
        {
            PlayerConnection player;
            return session != null && players.TryGetValue(session, out player) ? player : null;
        }

        private static bool Settled()
        {
            if (!Plugin.ConnectionMonitorReady || !SessionStatus.IsInSession()) return false;
            IConnectionModeSwitchStatus status = ConnectionModeSwitcher.GetStatus();
            return status != null && status.GetProgress() == eConnectionModeSwitchProgress.Complete;
        }

        /// <summary>The host's connection table, or null when this machine is not a settled host.</summary>
        internal static Dictionary<IOnlineMultiplayerSessionUserId, NetworkConnection> HostConnections()
        {
            if (!Settled() || !SessionStatus.IsHost()) return null;
            var server = LocalServer.GetValue(null) as Server;
            return server == null ? null : Connections.GetValue(server) as
                Dictionary<IOnlineMultiplayerSessionUserId, NetworkConnection>;
        }

        internal static bool KeepMeasuring(BaseConnection connection)
        {
            if (!(connection is RemoteConnection) || !Settled()) return false;

            if (SessionStatus.IsHost())
            {
                var connections = HostConnections();
                var session = connection.GetRemoteSessionUserId();
                NetworkConnection current;
                return session != null && connections != null && connections.TryGetValue(session, out current) &&
                    ReferenceEquals(current, connection);
            }

            var client = LocalClient.GetValue(null) as Team17.Online.Multiplayer.Client;
            var clientConn = client == null ? null : ClientConnectionField.GetValue(client) as NetworkConnection;
            return clientConn != null && ReferenceEquals(clientConn, connection);
        }

        private static void ForgetRelays()
        {
            relayed.Clear();
            clientSince = -1f;
            lastRelayAt = -1f;
        }

        internal static void Tick(float now)
        {
            if (!Settled()) { players.Clear(); ForgetRelays(); return; }

            var server = LocalServer.GetValue(null) as Server;
            var connections = server == null ? null : Connections.GetValue(server) as
                Dictionary<IOnlineMultiplayerSessionUserId, NetworkConnection>;
            var client = LocalClient.GetValue(null) as Team17.Online.Multiplayer.Client;
            var clientConn = client == null ? null : ClientConnectionField.GetValue(client) as RemoteConnection;
            bool host = SessionStatus.IsHost();
            if (host) ForgetRelays();
            else if (clientSince < 0) clientSince = now;

            // A player who left keeps no figure; one who joins under a reused id must not inherit it.
            foreach (byte id in new List<byte>(relayed.Keys))
                if (now - relayed[id].ReceivedAt > 10f) relayed.Remove(id);

            var present = new List<IOnlineMultiplayerSessionUserId>();
            foreach (DisplayParty party in Moderation.DisplayParties())
            {
                if (party.Session == null) continue;
                present.Add(party.Session);
                PlayerConnection player;
                if (!players.TryGetValue(party.Session, out player))
                { player = new PlayerConnection(); players.Add(party.Session, player); }
                player.Local = party.IsLocal;
                player.NeedsRelay = false;

                if (party.IsLocal)
                {
                    // The host talks to itself over loopback: no network round trip at all.
                    player.Sample = host ? Loopback(now) : Read(clientConn, now);
                }
                else if (host)
                {
                    NetworkConnection connection;
                    player.Sample = connections != null && connections.TryGetValue(party.Session, out connection) ?
                        Read(connection as RemoteConnection, now) : new ConnectionSample();
                }
                else if (party.Session.IsHost)
                {
                    // Every figure in the room is a round trip to the host, so the host's own is
                    // zero here exactly as it is on the host's panel.
                    player.Sample = Loopback(now);
                }
                else
                {
                    player.NeedsRelay = true;
                    RelayedPing relay;
                    player.Sample = relayed.TryGetValue(party.Session.UniqueId, out relay) ?
                        new ConnectionSample
                        {
                            Available = true, Measured = true, PingMs = relay.PingMs,
                            // The host's own "no fresh reply" mark carries over as an old reply time.
                            LastReplyTime = relay.HostStale ? float.MinValue : relay.ReceivedAt
                        } : new ConnectionSample();
                }
            }
            foreach (var session in new List<IOnlineMultiplayerSessionUserId>(players.Keys))
                if (!present.Contains(session)) players.Remove(session);
        }

        private static ConnectionSample Loopback(float now)
        {
            return new ConnectionSample { Available = true, Measured = true, PingMs = 0f, LastReplyTime = now };
        }

        private static ConnectionSample Read(RemoteConnection connection, float now)
        {
            if (connection == null) return new ConnectionSample();
            var sample = new ConnectionSample { Available = true };
            bool haveFreshChannel = false;
            foreach (var field in new[] { Reliable, Unreliable })
            {
                var measure = (LatencyMeasure)field.GetValue(connection);
                var history = (Queue<float>)History.GetValue(measure);
                if (history.Count == 0) continue;
                float latency = measure.GetAverageOneWayTripTime();
                if (float.IsNaN(latency) || float.IsInfinity(latency) || latency < 0) continue;
                float reply = (float)ReplyTime.GetValue(measure);
                bool fresh = now - reply <= FreshSeconds;
                // An old sample from one channel must not override a fresh reply on the other.
                if (haveFreshChannel && !fresh) continue;
                if (fresh && !haveFreshChannel)
                { sample.PingMs = 0; sample.LastReplyTime = reply; haveFreshChannel = true; }
                sample.Measured = true;
                // The game stores ONE-WAY seconds. Display conventional round-trip milliseconds.
                sample.PingMs = Math.Max(sample.PingMs, latency * 2000f);
                sample.LastReplyTime = Math.Max(sample.LastReplyTime, reply);
            }
            return sample;
        }

        private static bool ValidPing(ConnectionSample sample)
        {
            return sample.Available && sample.Measured && !float.IsNaN(sample.PingMs) &&
                !float.IsInfinity(sample.PingMs) && sample.PingMs >= 0;
        }

        /// <summary>The current ping in ms, or false when there is no usable figure.</summary>
        internal static bool TryPing(IOnlineMultiplayerSessionUserId session, out float pingMs)
        {
            var player = Get(session);
            bool valid = player != null && ValidPing(player.Sample);
            pingMs = valid ? player.Sample.PingMs : 0f;
            return valid;
        }

        /// <summary>The figure has had a reply within <see cref="FreshSeconds"/>.</summary>
        internal static bool IsCurrent(IOnlineMultiplayerSessionUserId session, float now)
        {
            var player = Get(session);
            return player != null && ValidPing(player.Sample) && now - player.Sample.LastReplyTime <= FreshSeconds;
        }

        /// <summary>
        /// A client's row for another client, with nothing from the host after
        /// <see cref="HostSilentSeconds"/>: the host is not running this mod, so it never will.
        /// </summary>
        internal static bool HostLacksMod(IOnlineMultiplayerSessionUserId session, float now)
        {
            var player = Get(session);
            return player != null && player.NeedsRelay && !ValidPing(player.Sample) &&
                clientSince >= 0 && lastRelayAt < 0 && now - clientSince > HostSilentSeconds;
        }

        internal static string Display(IOnlineMultiplayerSessionUserId session, float now)
        {
            var player = Get(session);
            if (player == null || !ValidPing(player.Sample))
                return HostLacksMod(session, now) ? "Độ trễ: chủ phòng chưa cài OC2Tweaks" : "Độ trễ: đang đo...";
            return "Độ trễ: " + player.Sample.PingMs.ToString("0") + " ms" +
                (IsCurrent(session, now) ? "" : " (đang cập nhật)");
        }

        // ---- relay, host side ----

        /// <summary>The host's measured remote players, to pass on to its clients.</summary>
        internal static List<RelayEntry> RelayEntries(float now)
        {
            var result = new List<RelayEntry>();
            if (!SessionStatus.IsHost()) return result;
            foreach (var pair in players)
            {
                if (pair.Value.Local || pair.Key == null || !ValidPing(pair.Value.Sample)) continue;
                result.Add(new RelayEntry
                {
                    Id = pair.Key.UniqueId, PingMs = pair.Value.Sample.PingMs,
                    Stale = now - pair.Value.Sample.LastReplyTime > FreshSeconds
                });
            }
            return result;
        }

        /// <summary>
        /// Packs one player into a probe's send time: -(1 + (id &lt;&lt; 15 | stale &lt;&lt; 14 | ms)).
        /// The largest code is 2^23, so every one is an exact float.
        /// </summary>
        internal static float EncodeRelay(byte id, float pingMs, bool stale)
        {
            int ms = pingMs >= RelayMaxMs ? RelayMaxMs : pingMs > 0 ? (int)(pingMs + 0.5f) : 0;
            int bits = (id << 15) | (stale ? 1 << 14 : 0) | ms;
            return -(bits + 1f);
        }

        internal static bool IsRelay(float sendTime) { return sendTime < 0; }

        internal static bool DecodeRelay(float sendTime, out byte id, out float pingMs, out bool stale)
        {
            id = 0; pingMs = 0; stale = false;
            if (!IsRelay(sendTime) || float.IsInfinity(sendTime)) return false;
            float raw = -sendTime - 1f;
            if (raw < 0 || raw > (1 << 23) - 1 || raw != (float)Math.Floor(raw)) return false;
            int bits = (int)raw;
            id = (byte)(bits >> 15);
            stale = (bits & (1 << 14)) != 0;
            pingMs = bits & RelayMaxMs;
            return true;
        }

        // ---- relay, client side ----

        /// <summary>A relayed figure from the host arrived. Ignored while hosting.</summary>
        internal static bool ReceiveRelay(float sendTime, float now)
        {
            byte id; float pingMs; bool stale;
            if (SessionStatus.IsHost() || !DecodeRelay(sendTime, out id, out pingMs, out stale)) return false;
            relayed[id] = new RelayedPing { PingMs = pingMs, HostStale = stale, ReceivedAt = now };
            lastRelayAt = now;
            return true;
        }
    }

    [HarmonyPatch(typeof(BaseConnection), "GetLatencyTestPaused")]
    internal static class PersistentPing
    {
        internal static bool Prefix(BaseConnection __instance, ref bool __result)
        {
            if (!ConnectionMonitor.KeepMeasuring(__instance)) return true;
            // The game's normal dispatch schedules probes and its mailbox processes the replies.
            // Leave the original pause flag intact so unpatching restores the game's behaviour.
            __result = false;
            return false;
        }
    }
}
