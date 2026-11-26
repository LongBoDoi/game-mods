using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Steamworks;
using Team17.Online;
using UnityEngine;

namespace RelayNet
{
    /// <summary>What RelayNet knows about one other player in the online session.</summary>
    internal sealed class Peer
    {
        internal ulong Id;
        internal bool GameTalks;        // the game sends data to them (host <-> client)
        internal float FirstHello = -1f, NextHello;
        internal bool HeardFrom;        // a hello of theirs reached us over the relay
        internal bool TheyHeardUs;      // their hello says ours reached them
        internal bool Upgraded;         // we send to them over the relay
        internal bool Failed;           // the relay failed with them this session: old path only
        internal bool GaveUp;           // no answer to our hellos: they don't have RelayNet
        internal bool SwitchSeen;       // their switch marker came in on the old path
        internal bool FlushNow;
        internal readonly List<byte[]> Held = new List<byte[]>();
        internal readonly List<int> HeldChannel = new List<int>();
        internal float HeldSince;
        internal float NextKeepalive;

        // shown on the overlay / in the log
        internal int State = SteamMessages.StateNone;
        internal int RelayPing = -1;
        internal string RelayPop;
        internal bool RelayRelayed;
        internal bool OldActive, OldRelay;
        internal float GameRttReliable = -1f, GameRttUnreliable = -1f;
        internal int SentRelay, SentOld, ReceivedRelay;
    }

    /// <summary>
    /// Moves Overcooked! 2's online traffic from the legacy Steam P2P API (ISteamNetworking) to
    /// Steam Datagram Relay (ISteamNetworkingMessages), one player at a time, without the game
    /// noticing anything but the lower latency.
    ///
    /// The game sends everything through SteamOnlineMultiplayerSessionTransportCoordinator:
    /// SendData (channel 1, reliable or not), SendVoipData (channel 2) and Update, which reads
    /// packets and hands them to the game through OnDataEvent / OnVoipDataEvent. RelayNet wraps
    /// those four methods; everything above them (messages, keepalives, disconnect timeouts,
    /// latency measurement) is untouched and simply runs over the new path.
    ///
    /// Per player:
    ///  1. While the game talks to them, send a hello on the relay's control channel every second
    ///     (each hello says whether theirs reached us). Players without RelayNet never answer, and
    ///     after 30 s we stop asking: they keep the old path, as does everyone in a mixed lobby.
    ///  2. Once both directions are proven (we heard them, they heard us), send one switch marker
    ///     on the OLD path, reliably and on the game's own channel, and send everything after it
    ///     over the relay. The receiver holds relay data from that player until the marker
    ///     arrives; since the old reliable channel is ordered, every old reliable message sent
    ///     before the switch has been handed to the game by then, so the game sees one ordered
    ///     stream. (If the marker never comes, held data is released after 3 s.)
    ///  3. If the relay session breaks or a send fails, go back to the old path for that player.
    ///  4. While on the relay, a tiny keepalive marker goes over the old path every 2 s so the
    ///     legacy P2P session stays up as a fallback and never times out idle.
    /// Markers are 16 fixed bytes, only ever sent to players proven to run RelayNet, and taken out
    /// before the game sees them.
    /// </summary>
    internal static class Relay
    {
        private const int DataChannel = 1;          // the game's own channel numbers
        private const int VoipChannel = 2;
        private const int ControlChannel = 7;
        private const int SelfTestChannel = 6;

        private const byte ProtocolVersion = 1;
        private const byte TypeHello = 1;
        private const byte TypeSelfTest = 0x7E;

        private const float HelloInterval = 1f;
        private const float GiveUpAfter = 30f;
        private const float HoldTimeout = 3f;
        private const float KeepaliveInterval = 2f;
        private const float StatusInterval = 1f;
        private const float LogInterval = 30f;
        private const int MaxHeld = 1024;

        private static readonly byte[] Magic = { (byte)'R', (byte)'N', (byte)'E', (byte)'T' };
        private static readonly byte[] SwitchMarker = Marker("SWITCH");
        private static readonly byte[] KeepaliveMarker = Marker("ALIVE");

        private static readonly Dictionary<ulong, Peer> peers = new Dictionary<ulong, Peer>();
        private static readonly byte[] control = new byte[16];

        private static bool open;           // the game's Steam session is open
        private static bool relayOn;        // ... and RelayNet moves traffic in it
        private static CSteamID lobby;
        private static ulong self;
        private static SteamOnlineMultiplayerSessionTransportCoordinator owner;
        private static Action<CSteamID, byte[], int> deliverData, deliverVoip;
        private static byte[] buffer;
        private static float nextStatus, nextLog, nextAccept;
        private static readonly HashSet<string> warned = new HashSet<string>();

        private static readonly MethodInfo OnDataEvent = AccessTools.Method(typeof(SteamOnlineMultiplayerSessionTransportCoordinator), "OnDataEvent");
        private static readonly MethodInfo OnVoipDataEvent = AccessTools.Method(typeof(SteamOnlineMultiplayerSessionTransportCoordinator), "OnVoipDataEvent");
        private static readonly FieldInfo LobbyField = AccessTools.Field(typeof(SteamOnlineMultiplayerSessionTransportCoordinator), "m_lobbyId");

        // handlers kept as fields so receiving allocates no delegates
        private static readonly SteamMessages.MessageHandler onControl = HandleControl;
        private static readonly SteamMessages.MessageHandler onData = HandleData;
        private static readonly SteamMessages.MessageHandler onVoip = HandleVoip;
        private static readonly SteamMessages.MessageHandler onSelfTest = HandleSelfTest;

        internal static bool SessionOpen { get { return open; } }
        internal static bool RelayOn { get { return relayOn; } }
        internal static IEnumerable<Peer> Peers { get { return peers.Values; } }

        private static byte[] Marker(string tag)
        {
            byte[] m = new byte[16];
            m[0] = 0x00; m[1] = 0xFF;
            string text = "RNET-" + tag;
            for (int i = 0; i < text.Length; i++) m[2 + i] = (byte)text[i];
            m[15] = 0x7F;
            return m;
        }

        private static Peer Get(ulong id)
        {
            Peer p;
            if (!peers.TryGetValue(id, out p))
            {
                p = new Peer { Id = id };
                peers[id] = p;
            }
            return p;
        }

        internal static string Name(ulong id)
        {
            try
            {
                string name = SteamFriends.GetFriendPersonaName(new CSteamID(id));
                if (!string.IsNullOrEmpty(name) && name != "[unknown]") return name;
            }
            catch (Exception) { }
            return id.ToString();
        }

        private static void WarnOnce(string key, string text)
        {
            if (warned.Add(key)) Plugin.Log.LogWarning(text);
        }

        // ---- session lifecycle (from the patches) --------------------------------------------

        internal static void Begin(SteamOnlineMultiplayerSessionTransportCoordinator coordinator)
        {
            End();
            open = true;
            Bind(coordinator);
            lobby = (CSteamID)LobbyField.GetValue(coordinator);
            self = SteamUser.GetSteamID().m_SteamID;
            relayOn = Plugin.Enabled.Value && SteamMessages.Init() && SelfTest.Usable;
            if (relayOn)
                Plugin.Log.LogInfo("Phiên online mới: RelayNet sẽ chuyển sang mạng relay của Steam với người chơi nào cũng có RelayNet.");
            else if (!Plugin.Enabled.Value)
                Plugin.Log.LogInfo("Phiên online mới: RelayNet đang tắt (Enabled = false), chỉ đo độ trễ.");
            else
                Plugin.Log.LogWarning("Phiên online mới: không dùng được mạng relay của Steam (" + (SteamMessages.Error ?? SelfTest.Status) + "), chỉ đo độ trễ.");
            nextLog = Time.realtimeSinceStartup + LogInterval;
        }

        internal static void End()
        {
            if (!open) return;
            if (relayOn)
            {
                foreach (Peer p in peers.Values)
                {
                    try { SteamMessages.Close(p.Id); } catch (Exception) { }
                }
            }
            if (peers.Count > 0) LogSummary("Kết thúc phiên");
            peers.Clear();
            open = false;
            relayOn = false;
            owner = null;
            deliverData = deliverVoip = null;
            buffer = null;
        }

        internal static void Drop(ulong id)
        {
            Peer p;
            if (!peers.TryGetValue(id, out p)) return;
            if (relayOn) { try { SteamMessages.Close(id); } catch (Exception) { } }
            peers.Remove(id);
            Plugin.Log.LogInfo(Name(id) + " đã rời phiên.");
        }

        private static void Bind(SteamOnlineMultiplayerSessionTransportCoordinator coordinator)
        {
            if (coordinator == owner && deliverData != null) return;
            owner = coordinator;
            deliverData = (Action<CSteamID, byte[], int>)Delegate.CreateDelegate(typeof(Action<CSteamID, byte[], int>), coordinator, OnDataEvent);
            deliverVoip = (Action<CSteamID, byte[], int>)Delegate.CreateDelegate(typeof(Action<CSteamID, byte[], int>), coordinator, OnVoipDataEvent);
        }

        // ---- sending (SendData / SendVoipData prefixes) --------------------------------------

        /// <summary>True when the relay took the packet; result is then what SendData returns.</summary>
        internal static bool TrySend(CSteamID to, byte[] data, int size, bool reliable, bool voip, out bool result)
        {
            result = false;
            if (!open) return false;
            Peer p = Get(to.m_SteamID);
            p.GameTalks = true;
            if (!relayOn || !p.Upgraded)
            {
                p.SentOld++;
                return false;
            }

            int flags = voip ? (SteamMessages.SendUnreliable | SteamMessages.SendNoNagle | SteamMessages.SendNoDelay)
                : reliable ? (SteamMessages.SendReliable | SteamMessages.SendNoNagle)
                : (SteamMessages.SendUnreliable | SteamMessages.SendNoNagle);
            // No AutoRestartBrokenSession: a broken session should fail the send and fall back to
            // the old path now, not reconnect quietly while reliable messages pile up.
            int sent = SteamMessages.Send(p.Id, data, size, flags, voip ? VoipChannel : DataChannel);
            if (sent == SteamMessages.ResultOK)
            {
                p.SentRelay++;
                result = true;
                return true;
            }
            if (!reliable)
            {
                // An unreliable packet the relay could not take is simply lost, as it could be on
                // the old path. Only a reliable one is worth falling back for.
                p.SentRelay++;
                return true;
            }
            Downgrade(p, "gửi qua relay thất bại (EResult " + sent + ")");
            p.SentOld++;
            return false;
        }

        private static void Downgrade(Peer p, string why)
        {
            if (!p.Upgraded) return;
            p.Upgraded = false;
            p.Failed = true;
            Plugin.Log.LogWarning("Quay lại đường P2P cũ với " + Name(p.Id) + ": " + why + ".");
        }

        // ---- the old path (OnDataEvent prefix) -----------------------------------------------

        /// <summary>True when the packet was one of RelayNet's markers and must not reach the game.</summary>
        internal static bool TakeMarker(CSteamID from, byte[] data, int size)
        {
            if (size != 16 || data == null || data.Length < 16 || data[0] != 0x00 || data[1] != 0xFF || data[15] != 0x7F) return false;
            bool isSwitch = Same(data, SwitchMarker), isKeepalive = !isSwitch && Same(data, KeepaliveMarker);
            if (!isSwitch && !isKeepalive) return false;
            if (isSwitch && open)
            {
                Peer p = Get(from.m_SteamID);
                if (!p.SwitchSeen)
                {
                    p.SwitchSeen = true;
                    p.FlushNow = true;
                    Plugin.Log.LogInfo(Name(p.Id) + " đã chuyển sang mạng relay của Steam (gói chuyển đã tới, " + p.Held.Count + " gói đang chờ).");
                }
            }
            return true;
        }

        private static bool Same(byte[] a, byte[] b)
        {
            for (int i = 0; i < 16; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // ---- every frame (Update postfix) ----------------------------------------------------

        internal static void Pump(SteamOnlineMultiplayerSessionTransportCoordinator coordinator, byte[] receiveBuffer)
        {
            if (!open || receiveBuffer == null) return;
            Bind(coordinator);
            buffer = receiveBuffer;
            float now = Time.realtimeSinceStartup;

            if (relayOn)
            {
                if (SteamMessages.Receive(ControlChannel, onControl) < 0 ||
                    !FlushReady() ||
                    SteamMessages.Receive(DataChannel, onData) < 0 ||
                    SteamMessages.Receive(VoipChannel, onVoip) < 0)
                {
                    Fail();
                    return;
                }
                foreach (Peer p in peers.Values) Step(p, now);
                if (now >= nextAccept)
                {
                    nextAccept = now + 1f;
                    AcceptLobbyMembers();
                }
            }

            if (now >= nextStatus)
            {
                nextStatus = now + StatusInterval;
                foreach (Peer p in peers.Values) Poll(p);
            }
            if (now >= nextLog)
            {
                nextLog = now + LogInterval;
                if (peers.Count > 0) LogSummary("Tình trạng");
            }
        }

        private static void Fail()
        {
            Plugin.Log.LogError("Tắt RelayNet cho lần chạy này: " + SteamMessages.Error + ". Mọi kết nối quay lại đường P2P cũ.");
            relayOn = false;
            foreach (Peer p in peers.Values)
            {
                p.Upgraded = false;
                FlushHeld(p);
                // Closing tells their RelayNet within a second ("closed by peer"), and it goes
                // back to the old path too.
                try { SteamMessages.Close(p.Id); } catch (Exception) { }
            }
        }

        private static void Step(Peer p, float now)
        {
            // their switch marker arrived (or never will): release what we held for them
            if (!p.SwitchSeen && p.Held.Count > 0 && now - p.HeldSince > HoldTimeout)
            {
                p.SwitchSeen = true;
                p.FlushNow = true;
                WarnOnce("hold" + p.Id, "Gói chuyển của " + Name(p.Id) + " không tới sau " + HoldTimeout + " giây; giao " + p.Held.Count + " gói đang chờ.");
            }

            if (p.Failed || !p.GameTalks) return;

            if (p.Upgraded)
            {
                if (now >= p.NextKeepalive)
                {
                    p.NextKeepalive = now + KeepaliveInterval;
                    SendOld(p.Id, KeepaliveMarker, EP2PSend.k_EP2PSendUnreliable);
                }
                return;
            }

            if (p.HeardFrom && p.TheyHeardUs)
            {
                // Both directions work. The marker goes out before the first relay packet.
                if (!SendOld(p.Id, SwitchMarker, EP2PSend.k_EP2PSendReliable))
                {
                    WarnOnce("switch" + p.Id, "Không gửi được gói chuyển cho " + Name(p.Id) + " qua đường cũ; thử lại sau.");
                    return;
                }
                p.Upgraded = true;
                p.NextKeepalive = now + KeepaliveInterval;
                Plugin.Log.LogInfo("Đang gửi cho " + Name(p.Id) + " qua mạng relay của Steam.");
                return;
            }

            if (p.GaveUp || now < p.NextHello) return;
            if (p.FirstHello < 0f) p.FirstHello = now;
            if (!p.HeardFrom && now - p.FirstHello > GiveUpAfter)
            {
                p.GaveUp = true;
                Plugin.Log.LogInfo(Name(p.Id) + " không trả lời qua mạng relay sau " + GiveUpAfter + " giây (chưa cài RelayNet, đang tắt, hoặc relay bị chặn); vẫn dùng đường P2P cũ.");
                return;
            }
            p.NextHello = now + HelloInterval;
            SendHello(p);
        }

        private static bool SendOld(ulong to, byte[] marker, EP2PSend mode)
        {
            try { return SteamNetworking.SendP2PPacket(new CSteamID(to), marker, (uint)marker.Length, mode, DataChannel); }
            catch (Exception) { return false; }
        }

        private static void SendHello(Peer p)
        {
            control[0] = Magic[0]; control[1] = Magic[1]; control[2] = Magic[2]; control[3] = Magic[3];
            control[4] = ProtocolVersion;
            control[5] = TypeHello;
            // bit 0: yours reached me. bit 1: I know mine reached you (no answer needed).
            control[6] = (byte)((p.HeardFrom ? 1 : 0) | (p.TheyHeardUs ? 2 : 0));
            SteamMessages.Send(p.Id, control, 7,
                SteamMessages.SendReliable | SteamMessages.SendNoNagle | SteamMessages.SendAutoRestartBrokenSession,
                ControlChannel);   // hellos may restart a session: that is how a handshake retries
        }

        /// <summary>A pending relay session from a lobby member is accepted (as the game accepts their P2P session).</summary>
        private static void AcceptLobbyMembers()
        {
            try
            {
                int n = SteamMatchmaking.GetNumLobbyMembers(lobby);
                for (int i = 0; i < n; i++)
                {
                    ulong member = SteamMatchmaking.GetLobbyMemberByIndex(lobby, i).m_SteamID;
                    if (member != self) SteamMessages.Accept(member);
                }
            }
            catch (Exception) { }
        }

        private static bool IsLobbyMember(ulong id)
        {
            Peer p;
            if (peers.TryGetValue(id, out p) && (p.GameTalks || p.HeardFrom)) return true;
            try
            {
                int n = SteamMatchmaking.GetNumLobbyMembers(lobby);
                for (int i = 0; i < n; i++)
                    if (SteamMatchmaking.GetLobbyMemberByIndex(lobby, i).m_SteamID == id) return true;
            }
            catch (Exception) { }
            return false;
        }

        // ---- receiving -----------------------------------------------------------------------

        private static void HandleControl(ulong from, IntPtr data, int size)
        {
            if (size < 7 || !IsLobbyMember(from)) return;
            byte[] m = control;
            System.Runtime.InteropServices.Marshal.Copy(data, m, 0, 7);
            if (m[0] != Magic[0] || m[1] != Magic[1] || m[2] != Magic[2] || m[3] != Magic[3]) return;
            if (m[4] != ProtocolVersion)
            {
                WarnOnce("ver" + from, Name(from) + " dùng RelayNet khác phiên bản giao thức (" + m[4] + "); vẫn dùng đường P2P cũ với họ.");
                return;
            }
            if (m[5] != TypeHello) return;

            Peer p = Get(from);
            bool first = !p.HeardFrom;
            p.HeardFrom = true;
            p.GaveUp = false;
            if ((m[6] & 1) != 0) p.TheyHeardUs = true;
            if (first) Plugin.Log.LogInfo("Nhận được lời chào RelayNet từ " + Name(from) + " qua mạng relay.");
            // They don't know yet that theirs arrived: answer straight away. (They keep saying
            // hello every second until they do, so a lost answer is repeated.)
            if ((m[6] & 2) == 0) SendHello(p);
        }

        private static void HandleData(ulong from, IntPtr data, int size) { Take(from, data, size, DataChannel); }

        private static void HandleVoip(ulong from, IntPtr data, int size) { Take(from, data, size, VoipChannel); }

        private static void Take(ulong from, IntPtr data, int size, int channel)
        {
            if (size <= 0) return;
            if (!IsLobbyMember(from)) return;
            Peer p = Get(from);
            p.ReceivedRelay++;
            if (!p.SwitchSeen)
            {
                if (p.Held.Count >= MaxHeld) { WarnOnce("full" + from, "Quá nhiều gói chờ từ " + Name(from) + "; bỏ bớt."); return; }
                byte[] copy = new byte[size];
                System.Runtime.InteropServices.Marshal.Copy(data, copy, 0, size);
                if (p.Held.Count == 0) p.HeldSince = Time.realtimeSinceStartup;
                p.Held.Add(copy);
                p.HeldChannel.Add(channel);
                return;
            }
            if (size > buffer.Length)
            {
                WarnOnce("big", "Bỏ một gói " + size + " byte, lớn hơn bộ đệm nhận của game (" + buffer.Length + ").");
                return;
            }
            System.Runtime.InteropServices.Marshal.Copy(data, buffer, 0, size);
            Deliver(from, size, channel);
        }

        /// <summary>Hands every player's held relay data to the game once their switch marker is in.</summary>
        private static bool FlushReady()
        {
            foreach (Peer p in peers.Values)
                if (p.FlushNow) FlushHeld(p);
            return true;
        }

        private static void FlushHeld(Peer p)
        {
            p.FlushNow = false;
            if (buffer == null) return;
            for (int i = 0; i < p.Held.Count; i++)
            {
                byte[] data = p.Held[i];
                if (data.Length > buffer.Length) continue;
                Buffer.BlockCopy(data, 0, buffer, 0, data.Length);
                Deliver(p.Id, data.Length, p.HeldChannel[i]);
            }
            p.Held.Clear();
            p.HeldChannel.Clear();
        }

        private static void Deliver(ulong from, int size, int channel)
        {
            Action<CSteamID, byte[], int> deliver = channel == VoipChannel ? deliverVoip : deliverData;
            if (deliver != null) deliver(new CSteamID(from), buffer, size);
        }

        // ---- status --------------------------------------------------------------------------

        private static void Poll(Peer p)
        {
            try
            {
                P2PSessionState_t old;
                if (SteamNetworking.GetP2PSessionState(new CSteamID(p.Id), out old))
                {
                    p.OldActive = old.m_bConnectionActive != 0;
                    p.OldRelay = old.m_bUsingRelay != 0;
                }
            }
            catch (Exception) { }

            if (!relayOn || (!p.Upgraded && !p.HeardFrom)) return;
            int ping; bool relayed; string relayPop, remotePop;
            int state = SteamMessages.SessionInfo(p.Id, out ping, out relayed, out relayPop, out remotePop);
            if (state != p.State)
                Plugin.Log.LogInfo("Phiên relay với " + Name(p.Id) + ": " + StateName(state) + (ping >= 0 ? ", ping " + ping + " ms" : "") + ".");
            p.State = state;
            p.RelayPing = ping;
            p.RelayRelayed = relayed;
            p.RelayPop = relayPop;
            if (p.Upgraded && (state == SteamMessages.StateClosedByPeer || state == SteamMessages.StateProblemDetectedLocally))
                Downgrade(p, "phiên relay " + StateName(state));
        }

        internal static string StateName(int state)
        {
            switch (state)
            {
                case SteamMessages.StateNone: return "none";
                case SteamMessages.StateConnecting: return "connecting";
                case SteamMessages.StateFindingRoute: return "finding route";
                case SteamMessages.StateConnected: return "connected";
                case SteamMessages.StateClosedByPeer: return "closed by peer";
                case SteamMessages.StateProblemDetectedLocally: return "problem detected locally";
                default: return "state " + state;
            }
        }

        /// <summary>From LatencyMeasure.OnLatencyReceived: the game's own round trip to a player.</summary>
        internal static void NoteLatency(ulong id, bool reliable, float oneWaySeconds)
        {
            if (!open || oneWaySeconds <= 0f) return;
            Peer p = Get(id);
            float rtt = oneWaySeconds * 2000f;
            if (reliable) p.GameRttReliable = rtt; else p.GameRttUnreliable = rtt;
        }

        private static void LogSummary(string title)
        {
            foreach (Peer p in peers.Values)
            {
                if (!p.GameTalks && !p.HeardFrom) continue;
                Plugin.Log.LogInfo(title + " — " + Name(p.Id) + ": " + Describe(p, false)
                    + " | gói gửi relay/cũ " + p.SentRelay + "/" + p.SentOld + ", nhận relay " + p.ReceivedRelay);
            }
        }

        /// <summary>One line about a player's connection, in the language in use (log: Vietnamese).</summary>
        internal static string Describe(Peer p, bool forScreen)
        {
            bool vi = !forScreen || Lang.Vi;
            string path;
            if (p.Upgraded)
            {
                path = (vi ? "relay Steam" : "Steam relay")
                    + (p.RelayPing >= 0 ? " " + p.RelayPing + " ms" : "")
                    + (p.RelayPop != null ? (vi ? " qua " : " via ") + p.RelayPop : "");
            }
            else
            {
                path = (vi ? "P2P cũ" : "old P2P")
                    + (p.OldActive ? (p.OldRelay ? (vi ? " (qua máy chủ chuyển tiếp)" : " (relayed)") : (vi ? " (trực tiếp)" : " (direct)")) : "");
                if (relayOn && !p.Failed && !p.GaveUp && !p.HeardFrom) path += vi ? ", đang chào relay…" : ", trying relay…";
                else if (p.GaveUp) path += vi ? ", họ chưa có RelayNet" : ", they don't have RelayNet";
                else if (p.Failed) path += vi ? ", relay lỗi" : ", relay failed";
            }
            float rtt = p.GameRttReliable >= 0f ? p.GameRttReliable : p.GameRttUnreliable;
            string game = rtt >= 0f ? (vi ? " · game đo " : " · game RTT ") + Mathf.RoundToInt(rtt) + " ms" : "";
            return path + game;
        }

        // ---- self-test hook --------------------------------------------------------------------

        internal static int ReceiveSelfTest() { return SteamMessages.Receive(SelfTestChannel, onSelfTest); }

        internal static bool SendSelfTest(ulong me, byte[] payload)
        {
            return SteamMessages.Send(me, payload, payload.Length,
                SteamMessages.SendReliable | SteamMessages.SendNoNagle, SelfTestChannel) == SteamMessages.ResultOK;
        }

        private static void HandleSelfTest(ulong from, IntPtr data, int size) { SelfTest.Received(from, data, size); }

        internal static byte SelfTestType { get { return TypeSelfTest; } }
    }
}
