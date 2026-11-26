using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;

namespace ChefSkills
{
    /// <summary>
    /// One message between the machines: a topic saying what it is (see
    /// <see cref="SpecialNet.Topic"/>), the chef it is about (network entity ID), and a few numbers.
    /// </summary>
    internal struct SpecialMessage
    {
        public ushort Topic;
        public uint Chef;
        public float A, B, C, D, E, F, G;
        /// <summary>A short text (at most 200 bytes of UTF-8 are sent): a refusal reason, an effect label.</summary>
        public string Text;
        public int I;
        /// <summary>The machine that first sent it (filled in on receipt; this machine when local).</summary>
        public CSteamID From;

        public Vector3 Vector1
        {
            get { return new Vector3(A, B, C); }
            set { A = value.x; B = value.y; C = value.z; }
        }

        public Vector3 Vector2
        {
            get { return new Vector3(D, E, F); }
            set { D = value.x; E = value.y; F = value.z; }
        }
    }

    /// <summary>
    /// Chef Skills' own fast channel between the machines in an online game, for things that must
    /// arrive within a frame or two: a special being used, a buff starting, a module's own events.
    /// (Lobby member data, which carries the builds, takes too long for that.)
    ///
    /// It is a Steam P2P channel of its own (7; the game uses 1 for its messages and 2 for voice),
    /// over the connections the game already has. Like the game, everything goes through the host:
    /// a client sends to the host, and the host passes a message meant for everyone on to every
    /// other client. Offline, or on the host, a message to the host is handled at once.
    ///
    /// It is only used while everyone in the game has this version of Chef Skills (a machine
    /// without it would never read the channel), so it does nothing otherwise online.
    /// </summary>
    internal static class SpecialNet
    {
        /// <summary>Topics of the framework's own messages. Modules use (int)Special * 10 + n.</summary>
        internal static class Topic
        {
            /// <summary>
            /// Client -> host: a player used their active special. Vector1 = position, Vector2 = facing, I = special;
            /// a chosen target (SpecialAim): G = its distance along the facing, Text = its chef ID (if a chef).
            /// </summary>
            internal const ushort ActivateRequest = 1;
            /// <summary>Host -> all: a special was used. I = special, A = cooldown, B = duration.</summary>
            internal const ushort Activated = 2;
            /// <summary>Host -> all: an activation was refused (nothing to use it on). I = special, Text = why.</summary>
            internal const ushort Denied = 3;
            /// <summary>Any -> all: a buff started. I = buff, A = duration, B = strength, D = source chef.</summary>
            internal const ushort Buff = 4;
            /// <summary>Any -> all: a visual effect (see SpecialFx).</summary>
            internal const ushort Fx = 5;
            /// <summary>Any -> all: a chef is choosing a target (see SpecialAim). I = special (0 = done), Vector1 = target, Text = its chef.</summary>
            internal const ushort Aim = 6;
        }

        private const int Channel = 7;
        private const byte Magic = 0xC5;
        // 4: the balance pass (SkillSync version 8).
        private const byte Protocol = 4;
        private const byte RouteToHost = 1;
        private const byte RouteToAll = 2;
        private const byte RouteRelay = 3;
        // Fixed part: header, topic, chef, A..G, I, origin; then a length byte and the text.
        private const int Size = 3 + 2 + 4 + 7 * 4 + 4 + 8;
        // Room for a text in both languages (Lang.Both; Vietnamese takes 2-3 bytes a letter in UTF-8).
        // Size + 1 + MaxText stays within the 256-byte receive buffer.
        private const int MaxText = 200;

        private static readonly Dictionary<ushort, List<Action<SpecialMessage>>> Handlers =
            new Dictionary<ushort, List<Action<SpecialMessage>>>();
        private static readonly byte[] SendBuffer = new byte[Size + 1 + MaxText];
        private static readonly byte[] ReceiveBuffer = new byte[256];
        private static readonly HashSet<ushort> Warned = new HashSet<ushort>();

        /// <summary>Runs <paramref name="handler"/> on this machine for every message of this topic.</summary>
        internal static void Register(ushort topic, Action<SpecialMessage> handler)
        {
            List<Action<SpecialMessage>> list;
            if (!Handlers.TryGetValue(topic, out list))
            {
                list = new List<Action<SpecialMessage>>();
                Handlers[topic] = list;
            }
            list.Add(handler);
        }

        /// <summary>
        /// This machine runs the game's server side: the host online, or any game offline. Things
        /// only the server decides (items, orders, fire, other chefs' positions) are done here.
        /// </summary>
        internal static bool IsServer
        {
            get
            {
                // Asked many times a frame by the specials' patches; the answer can't change mid-frame.
                if (s_serverFrame != Time.frameCount)
                {
                    CSteamID lobby;
                    bool host;
                    s_isServer = !SkillSync.TryGetLobby(out lobby, out host) || host;
                    s_serverFrame = Time.frameCount;
                }
                return s_isServer;
            }
        }

        private static int s_serverFrame = -1;
        private static bool s_isServer = true;

        /// <summary>Online, with the channel usable (everyone has this version of Chef Skills).</summary>
        internal static bool Online
        {
            get
            {
                CSteamID lobby;
                bool host;
                return SkillSync.TryGetLobby(out lobby, out host) && SkillSync.EveryoneHasMod;
            }
        }

        /// <summary>To the host only (handled here at once when this machine is the server).</summary>
        internal static void SendToHost(SpecialMessage message)
        {
            CSteamID lobby;
            bool host;
            if (!SkillSync.TryGetLobby(out lobby, out host) || host)
            {
                message.From = Self();
                Dispatch(message);
                return;
            }
            if (!SkillSync.EveryoneHasMod) return;
            Send(SteamMatchmaking.GetLobbyOwner(lobby), RouteToHost, message, Self());
        }

        /// <summary>To every machine, this one included (handled here at once).</summary>
        internal static void SendToAll(SpecialMessage message)
        {
            CSteamID self = Self();
            message.From = self;
            // Out first, then handled here: a handler may send messages of its own (the host's
            // Activated handlers do), and the others must get them after this one, in the order
            // this machine handles them. Handled first, they would reach the others first.
            CSteamID lobby;
            bool host;
            if (SkillSync.TryGetLobby(out lobby, out host) && SkillSync.EveryoneHasMod)
            {
                if (host) SendToClients(lobby, message, self, self);
                else Send(SteamMatchmaking.GetLobbyOwner(lobby), RouteRelay, message, self);
            }
            Dispatch(message);
        }

        /// <summary>Reads everything waiting on the channel. Called every frame by the plugin.</summary>
        internal static void Poll()
        {
            if (!SteamPlayerManager.Initialized) return;
            try
            {
                uint size;
                int guard = 0;
                while (guard++ < 256 && SteamNetworking.IsP2PPacketAvailable(out size, Channel))
                {
                    uint read;
                    CSteamID sender;
                    if (!SteamNetworking.ReadP2PPacket(ReceiveBuffer, (uint)ReceiveBuffer.Length, out read, out sender, Channel))
                        break;
                    Receive(sender, (int)read);
                }
            }
            catch (Exception error)
            {
                Plugin.Log.LogDebug("Không đọc được kênh Chef Skills: " + error.Message);
            }
        }

        private static void Receive(CSteamID sender, int length)
        {
            if (length < Size || ReceiveBuffer[0] != Magic || ReceiveBuffer[1] != Protocol) return;
            byte route = ReceiveBuffer[2];
            SpecialMessage message = Decode(ReceiveBuffer, length);
            ulong origin = BitConverter.ToUInt64(ReceiveBuffer, Size - 8);
            message.From = new CSteamID(origin != 0 ? origin : sender.m_SteamID);

            CSteamID lobby;
            bool host;
            if (!SkillSync.TryGetLobby(out lobby, out host)) return;
            switch (route)
            {
                case RouteToHost:
                    if (host) Dispatch(message);
                    break;
                case RouteRelay:
                    // A client's message for everyone: pass it on, then handle it here (the same
                    // order as SendToAll, so what a handler sends follows it).
                    if (!host) break;
                    SendToClients(lobby, message, message.From, Self());
                    Dispatch(message);
                    break;
                case RouteToAll:
                    // Only the host sends these.
                    if (sender == SteamMatchmaking.GetLobbyOwner(lobby)) Dispatch(message);
                    break;
            }
        }

        private static void SendToClients(CSteamID lobby, SpecialMessage message, CSteamID origin, CSteamID self)
        {
            int members = SteamMatchmaking.GetNumLobbyMembers(lobby);
            for (int i = 0; i < members; i++)
            {
                CSteamID member = SteamMatchmaking.GetLobbyMemberByIndex(lobby, i);
                if (member == self || member == origin) continue;
                Send(member, RouteToAll, message, origin);
            }
        }

        private static void Dispatch(SpecialMessage message)
        {
            List<Action<SpecialMessage>> list;
            if (!Handlers.TryGetValue(message.Topic, out list))
            {
                if (Warned.Add(message.Topic))
                    Plugin.Log.LogDebug("Tin nhắn Chef Skills không ai xử lý: chủ đề " + message.Topic);
                return;
            }
            for (int i = 0; i < list.Count; i++)
            {
                try
                {
                    list[i](message);
                }
                catch (Exception error)
                {
                    Plugin.Log.LogWarning("Lỗi khi xử lý tin nhắn Chef Skills (chủ đề " + message.Topic + "): " + error);
                }
            }
        }

        private static void Send(CSteamID to, byte route, SpecialMessage message, CSteamID origin)
        {
            if (!to.IsValid()) return;
            try
            {
                int length = Encode(SendBuffer, route, message, origin);
                SteamNetworking.SendP2PPacket(to, SendBuffer, (uint)length, EP2PSend.k_EP2PSendReliable, Channel);
            }
            catch (Exception error)
            {
                Plugin.Log.LogDebug("Không gửi được tin nhắn Chef Skills: " + error.Message);
            }
        }

        private static CSteamID Self()
        {
            try
            {
                return SteamPlayerManager.Initialized ? SteamUser.GetSteamID() : CSteamID.Nil;
            }
            catch (Exception)
            {
                return CSteamID.Nil;
            }
        }

        private static int Encode(byte[] buffer, byte route, SpecialMessage m, CSteamID origin)
        {
            buffer[0] = Magic;
            buffer[1] = Protocol;
            buffer[2] = route;
            int o = 3;
            Put(buffer, ref o, BitConverter.GetBytes(m.Topic));
            Put(buffer, ref o, BitConverter.GetBytes(m.Chef));
            Put(buffer, ref o, BitConverter.GetBytes(m.A));
            Put(buffer, ref o, BitConverter.GetBytes(m.B));
            Put(buffer, ref o, BitConverter.GetBytes(m.C));
            Put(buffer, ref o, BitConverter.GetBytes(m.D));
            Put(buffer, ref o, BitConverter.GetBytes(m.E));
            Put(buffer, ref o, BitConverter.GetBytes(m.F));
            Put(buffer, ref o, BitConverter.GetBytes(m.G));
            Put(buffer, ref o, BitConverter.GetBytes(m.I));
            Put(buffer, ref o, BitConverter.GetBytes(origin.m_SteamID));
            byte[] text = string.IsNullOrEmpty(m.Text) ? new byte[0] : System.Text.Encoding.UTF8.GetBytes(m.Text);
            // Too long for both languages: send the English alone rather than cut a letter in half.
            if (text.Length > MaxText && m.Text.IndexOf(Lang.Split) >= 0)
                text = System.Text.Encoding.UTF8.GetBytes(m.Text.Substring(0, m.Text.IndexOf(Lang.Split)));
            int count = Math.Min(text.Length, MaxText);
            buffer[o++] = (byte)count;
            Buffer.BlockCopy(text, 0, buffer, o, count);
            return o + count;
        }

        private static SpecialMessage Decode(byte[] buffer, int length)
        {
            int o = 3;
            var m = new SpecialMessage();
            m.Topic = BitConverter.ToUInt16(buffer, o); o += 2;
            m.Chef = BitConverter.ToUInt32(buffer, o); o += 4;
            m.A = BitConverter.ToSingle(buffer, o); o += 4;
            m.B = BitConverter.ToSingle(buffer, o); o += 4;
            m.C = BitConverter.ToSingle(buffer, o); o += 4;
            m.D = BitConverter.ToSingle(buffer, o); o += 4;
            m.E = BitConverter.ToSingle(buffer, o); o += 4;
            m.F = BitConverter.ToSingle(buffer, o); o += 4;
            m.G = BitConverter.ToSingle(buffer, o); o += 4;
            m.I = BitConverter.ToInt32(buffer, o);
            if (length > Size)
            {
                int count = Math.Min(buffer[Size], length - Size - 1);
                if (count > 0) m.Text = System.Text.Encoding.UTF8.GetString(buffer, Size + 1, count);
            }
            return m;
        }

        private static void Put(byte[] buffer, ref int offset, byte[] bytes)
        {
            Buffer.BlockCopy(bytes, 0, buffer, offset, bytes.Length);
            offset += bytes.Length;
        }
    }
}
