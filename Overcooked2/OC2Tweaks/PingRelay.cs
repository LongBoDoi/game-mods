using System;
using System.Collections.Generic;
using BitStream;
using HarmonyLib;
using Team17.Online;
using Team17.Online.Multiplayer.Connection;
using Team17.Online.Multiplayer.Messaging;
using UnityEngine;

namespace OC2Tweaks
{
    /// <summary>
    /// Passes the host's ping figures on to its clients. The game is client-server: a client only
    /// ever talks to the host, so it cannot measure anyone else, and without this every other row
    /// on a client's panel stays at "đang đo...".
    ///
    /// The figures ride in the game's own latency probe (<see cref="LatencyMessage"/>) rather than
    /// a new message type, so a player without the mod is never sent something it cannot parse:
    /// - Each probe's send time is a negative code (<see cref="ConnectionMonitor.EncodeRelay"/>).
    ///   Real probes carry Time.realtimeSinceStartup, which is never negative.
    /// - A modded client recognises the code, keeps the figure and does not answer.
    /// - An unmodded client answers it like any probe, and the host drops that echo before it can
    ///   touch the latency average (see <see cref="PingRelayReceive"/>).
    /// Only the host's mod sends; a modded client of an unmodded host gets nothing and says so.
    /// </summary>
    internal static class PingRelay
    {
        internal const float Interval = 1f;

        private static float nextSend;
        private static readonly FastList<byte> buffer = new FastList<byte>(16);
        private static readonly BitStreamWriter writer = new BitStreamWriter(buffer);
        private static readonly Message message = new Message();
        private static readonly LatencyMessage payload = new LatencyMessage();

        internal static void Broadcast(float now)
        {
            if (!Plugin.PingRelayReady || now < nextSend) return;
            nextSend = now + Interval;
            var connections = ConnectionMonitor.HostConnections();
            if (connections == null || connections.Count < 2) return; // nobody else to tell about
            List<RelayEntry> entries = ConnectionMonitor.RelayEntries(now);
            if (entries.Count == 0) return;
            foreach (var pair in connections)
            {
                var connection = pair.Value as RemoteConnection;
                if (pair.Key == null || connection == null) continue;
                foreach (RelayEntry entry in entries)
                {
                    // The client measures its own round trip, and more accurately than a relay.
                    if (entry.Id == pair.Key.UniqueId) continue;
                    Send(connection, ConnectionMonitor.EncodeRelay(entry.Id, entry.PingMs, entry.Stale));
                }
            }
        }

        // Mirrors LatencyMeasure.SendLatencyMessage, minus the part that records the send time as
        // the measure's last probe: the code is not a time, and would make it probe every frame.
        private static void Send(RemoteConnection connection, float code)
        {
            payload.m_Stage = LatencyMessage.Stage.Ping;
            payload.m_bReliable = false;
            payload.m_fTime = code;
            buffer.Clear();
            writer.Reset(buffer);
            message.Type = MessageType.LatencyMeasure;
            message.Payload = payload;
            message.Serialise(writer);
            connection.SendMessage(buffer._items, buffer.Count, false);
        }
    }

    [HarmonyPatch(typeof(LatencyMeasure), "OnLatencyReceived")]
    internal static class PingRelayReceive
    {
        private static float nextErrorLog;

        // Every LatencyMeasure registers for every latency message, so this runs once per measure.
        // Only the measure on the matching channel keeps the figure; all of them swallow the code.
        private static bool Prefix(IOnlineMultiplayerSessionUserId from, Serialisable message, bool ___m_bReliable)
        {
            var latency = message as LatencyMessage;
            if (latency == null || !ConnectionMonitor.IsRelay(latency.m_fTime)) return true;
            try
            {
                if (latency.m_Stage == LatencyMessage.Stage.Ping && latency.m_bReliable == ___m_bReliable &&
                    from != null && from.IsHost)
                    ConnectionMonitor.ReceiveRelay(latency.m_fTime, Time.realtimeSinceStartup);
            }
            catch (Exception error)
            {
                if (Time.realtimeSinceStartup >= nextErrorLog)
                {
                    nextErrorLog = Time.realtimeSinceStartup + 5f;
                    Plugin.Log.LogError("Không đọc được độ trễ do chủ phòng gửi: " + error);
                }
            }
            // Never the game's handler: a ping would be answered, and a pong's negative send time
            // would be averaged into the latency.
            return false;
        }
    }
}
