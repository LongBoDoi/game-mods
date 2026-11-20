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

        internal static bool KeepMeasuring(BaseConnection connection)
        {
            if (!Plugin.ConnectionMonitorReady || !(connection is RemoteConnection) || !SessionStatus.IsInSession()) return false;
            IConnectionModeSwitchStatus status = ConnectionModeSwitcher.GetStatus();
            if (status == null || status.GetProgress() != eConnectionModeSwitchProgress.Complete) return false;

            if (SessionStatus.IsHost())
            {
                var server = LocalServer.GetValue(null) as Server;
                var connections = server == null ? null : Connections.GetValue(server) as
                    Dictionary<IOnlineMultiplayerSessionUserId, NetworkConnection>;
                var session = connection.GetRemoteSessionUserId();
                NetworkConnection current;
                return session != null && connections != null && connections.TryGetValue(session, out current) &&
                    ReferenceEquals(current, connection);
            }

            var client = LocalClient.GetValue(null) as Team17.Online.Multiplayer.Client;
            var clientConn = client == null ? null : ClientConnectionField.GetValue(client) as NetworkConnection;
            return clientConn != null && ReferenceEquals(clientConn, connection);
        }

        internal static void Tick(float now)
        {
            if (!Plugin.ConnectionMonitorReady || !SessionStatus.IsInSession())
            { players.Clear(); return; }
            IConnectionModeSwitchStatus status = ConnectionModeSwitcher.GetStatus();
            if (status == null || status.GetProgress() != eConnectionModeSwitchProgress.Complete)
            { players.Clear(); return; }

            var server = LocalServer.GetValue(null) as Server;
            var connections = server == null ? null : Connections.GetValue(server) as
                Dictionary<IOnlineMultiplayerSessionUserId, NetworkConnection>;
            var client = LocalClient.GetValue(null) as Team17.Online.Multiplayer.Client;
            var clientConn = client == null ? null : ClientConnectionField.GetValue(client) as RemoteConnection;
            bool host = SessionStatus.IsHost();

            var present = new List<IOnlineMultiplayerSessionUserId>();
            foreach (DisplayParty party in Moderation.DisplayParties())
            {
                if (party.Session == null) continue;
                present.Add(party.Session);
                PlayerConnection player;
                if (!players.TryGetValue(party.Session, out player))
                { player = new PlayerConnection(); players.Add(party.Session, player); }

                if (party.IsLocal)
                {
                    if (host)
                    {
                        // The host talks to itself over loopback: no network round trip at all.
                        player.Sample = new ConnectionSample { Available = true, Measured = true, PingMs = 0f, LastReplyTime = now };
                    }
                    else
                    {
                        player.Sample = Read(clientConn, now);
                    }
                }
                else
                {
                    // Only the host holds every remote client's connection; a client can see the
                    // roster but has no way to measure anyone else's latency.
                    NetworkConnection connection;
                    player.Sample = host && connections != null && connections.TryGetValue(party.Session, out connection) ?
                        Read(connection as RemoteConnection, now) : new ConnectionSample();
                }
            }
            foreach (var session in new List<IOnlineMultiplayerSessionUserId>(players.Keys))
                if (!present.Contains(session)) players.Remove(session);
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
                bool fresh = now - reply <= 3f;
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

        internal static string Display(IOnlineMultiplayerSessionUserId session, float now)
        {
            var player = Get(session);
            if (player == null || !player.Sample.Available) return "Độ trễ: đang đo...";
            bool validPing = player.Sample.Measured && !float.IsNaN(player.Sample.PingMs) &&
                !float.IsInfinity(player.Sample.PingMs) && player.Sample.PingMs >= 0;
            return "Độ trễ: " + (validPing ? player.Sample.PingMs.ToString("0") + " ms" +
                (now - player.Sample.LastReplyTime > 3f ? " (đang cập nhật)" : "") : "đang đo...");
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
