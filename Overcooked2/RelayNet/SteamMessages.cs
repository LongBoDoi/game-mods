using System;
using System.Runtime.InteropServices;
using Steamworks;

namespace RelayNet
{
    /// <summary>
    /// The Steam client's ISteamNetworkingMessages interface ("SteamNetworkingMessages002"), the
    /// message API that runs over Steam Datagram Relay (SDR), Valve's relay backbone.
    ///
    /// Overcooked! 2 ships a 2018 Steamworks.NET that predates this interface, so there is no
    /// managed binding for it. The interface lives in the Steam client, though, not in the game's
    /// steam_api library: the game's own SteamClient.GetISteamGenericInterface hands out any
    /// interface version the running Steam client knows, and its methods are then called through
    /// the C++ vtable. Nothing native is shipped with the mod.
    ///
    /// Calling convention: on 64-bit (the Mac build) a C++ method is an ordinary C function whose
    /// first argument is the object. On 32-bit Windows (the Windows build) it is MSVC "thiscall",
    /// with the object in ECX, which Mono's delegate marshalling cannot do on this old runtime, so
    /// each method gets a 10-byte stub that moves the first stack argument into ECX and jumps to
    /// the method: the managed side then calls it as a plain stdcall function.
    ///
    /// Received messages are SteamNetworkingMessage_t structs read at fixed offsets. Every message
    /// is checked against what must be true of it (the channel it came from, a SteamID sender, a
    /// release function) before anything in it is trusted, and a mismatch switches the whole mod
    /// off for the session instead of guessing.
    /// </summary>
    internal static class SteamMessages
    {
        internal const string InterfaceVersion = "SteamNetworkingMessages002";

        // k_nSteamNetworkingSend_*
        internal const int SendUnreliable = 0;
        internal const int SendNoNagle = 1;
        internal const int SendNoDelay = 4;
        internal const int SendReliable = 8;
        internal const int SendAutoRestartBrokenSession = 32;

        internal const int ResultOK = 1;   // k_EResultOK

        // ESteamNetworkingConnectionState
        internal const int StateNone = 0;
        internal const int StateConnecting = 1;
        internal const int StateFindingRoute = 2;
        internal const int StateConnected = 3;
        internal const int StateClosedByPeer = 4;
        internal const int StateProblemDetectedLocally = 5;

        private const int IdentityTypeSteamId = 16;   // k_ESteamNetworkingIdentityType_SteamID
        private const int IdentitySize = 136;          // sizeof(SteamNetworkingIdentity)
        private const int MaxBatch = 64;

        // vtable slots of ISteamNetworkingMessages002, in declaration order
        private const int SlotSend = 0;
        private const int SlotReceive = 1;
        private const int SlotAccept = 2;
        private const int SlotClose = 3;
        private const int SlotSessionInfo = 5;

        // SteamNetConnectionInfo_t (same on every platform: its 64-bit field is already aligned)
        private const int InfoPopRemote = 168;
        private const int InfoPopRelay = 172;
        private const int InfoFlags = 440;
        private const int InfoFlagRelayed = 16;        // k_nSteamNetworkConnectionInfoFlags_Relayed
        // SteamNetConnectionRealTimeStatus_t
        private const int StatusPing = 4;

        private static IntPtr iface;
        private static bool thisCallStubs;
        private static IntPtr identity, sendBuffer, messages, infoBuffer, statusBuffer;
        private static int sendCapacity;

        // SteamNetworkingMessage_t offsets for this process
        private static int offSize, offIdentity, offRelease, offChannel;
        private static bool identityOffsetKnown;

        private static IntPtr releasePointer;
        private static ReleaseFn release;

        /// <summary>Why the interface is unusable, or null.</summary>
        internal static string Error { get; private set; }

        internal static bool Ready { get { return iface != IntPtr.Zero && Error == null; } }

        // ---- delegates -----------------------------------------------------------------------

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SendC(IntPtr self, IntPtr identity, IntPtr data, uint size, int flags, int channel);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SendS(IntPtr self, IntPtr identity, IntPtr data, uint size, int flags, int channel);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ReceiveC(IntPtr self, int channel, IntPtr messages, int max);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ReceiveS(IntPtr self, int channel, IntPtr messages, int max);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool IdentityC(IntPtr self, IntPtr identity);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool IdentityS(IntPtr self, IntPtr identity);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int InfoC(IntPtr self, IntPtr identity, IntPtr info, IntPtr status);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int InfoS(IntPtr self, IntPtr identity, IntPtr info, IntPtr status);

        // m_pfnRelease is a plain C function pointer, cdecl everywhere.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ReleaseFn(IntPtr message);

        private static SendC sendC; private static SendS sendS;
        private static ReceiveC receiveC; private static ReceiveS receiveS;
        private static IdentityC acceptC, closeC; private static IdentityS acceptS, closeS;
        private static InfoC infoC; private static InfoS infoS;

        /// <summary>Called with each received message; the data pointer is valid only during the call.</summary>
        internal delegate void MessageHandler(ulong from, IntPtr data, int size);

        // ---- setup ---------------------------------------------------------------------------

        /// <summary>Looks the interface up in the running Steam client. Steam must be initialised.</summary>
        internal static bool Init()
        {
            if (iface != IntPtr.Zero || Error != null) return Ready;
            try
            {
                IntPtr found = SteamClient.GetISteamGenericInterface(
                    SteamAPI.GetHSteamUser(), SteamAPI.GetHSteamPipe(), InterfaceVersion);
                if (found == IntPtr.Zero)
                {
                    Error = "Steam không có " + InterfaceVersion;
                    return false;
                }

                bool windows = Environment.OSVersion.Platform == PlatformID.Win32NT;
                thisCallStubs = windows && IntPtr.Size == 4;
                offSize = IntPtr.Size;
                if (IntPtr.Size == 8)
                {
                    offIdentity = 16; identityOffsetKnown = true;
                    offRelease = 184; offChannel = 192;
                }
                else if (windows)
                {
                    // MSVC packs these structs to 8. The identity starts at 12 when it is packed
                    // to 1 (as the SDK declares it) and at 16 otherwise; the first message
                    // received tells which, and every field after it lands in the same place
                    // either way.
                    offIdentity = 12; identityOffsetKnown = false;
                    offRelease = 180; offChannel = 184;
                }
                else
                {
                    offIdentity = 12; identityOffsetKnown = true;
                    offRelease = 176; offChannel = 180;
                }

                identity = Alloc(IdentitySize);
                messages = Alloc(MaxBatch * IntPtr.Size);
                infoBuffer = Alloc(1024);
                statusBuffer = Alloc(1024);
                EnsureSendBuffer(2048);

                if (thisCallStubs)
                {
                    sendS = (SendS)Method(found, SlotSend, typeof(SendS));
                    receiveS = (ReceiveS)Method(found, SlotReceive, typeof(ReceiveS));
                    acceptS = (IdentityS)Method(found, SlotAccept, typeof(IdentityS));
                    closeS = (IdentityS)Method(found, SlotClose, typeof(IdentityS));
                    infoS = (InfoS)Method(found, SlotSessionInfo, typeof(InfoS));
                }
                else
                {
                    sendC = (SendC)Method(found, SlotSend, typeof(SendC));
                    receiveC = (ReceiveC)Method(found, SlotReceive, typeof(ReceiveC));
                    acceptC = (IdentityC)Method(found, SlotAccept, typeof(IdentityC));
                    closeC = (IdentityC)Method(found, SlotClose, typeof(IdentityC));
                    infoC = (InfoC)Method(found, SlotSessionInfo, typeof(InfoC));
                }
                iface = found;
                return true;
            }
            catch (Exception error)
            {
                Error = "không mở được " + InterfaceVersion + ": " + error.Message;
                return false;
            }
        }

        /// <summary>Stops all use of the interface for the rest of this run.</summary>
        internal static void Disable(string why)
        {
            if (Error == null) Error = why;
        }

        private static IntPtr Alloc(int size)
        {
            IntPtr p = Marshal.AllocHGlobal(size);
            for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
            return p;
        }

        private static void EnsureSendBuffer(int size)
        {
            if (size <= sendCapacity) return;
            int capacity = Math.Max(size, sendCapacity * 2);
            if (sendBuffer != IntPtr.Zero) Marshal.FreeHGlobal(sendBuffer);
            sendBuffer = Marshal.AllocHGlobal(capacity);
            sendCapacity = capacity;
        }

        private static Delegate Method(IntPtr instance, int slot, Type type)
        {
            IntPtr vtable = Marshal.ReadIntPtr(instance);
            IntPtr target = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
            if (target == IntPtr.Zero) throw new InvalidOperationException("vtable slot " + slot + " is empty");
            if (thisCallStubs) target = ThisCallStub(target);
            return Marshal.GetDelegateForFunctionPointer(target, type);
        }

        // ---- 32-bit Windows thiscall stubs ---------------------------------------------------

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocationType, uint protect);

        private static IntPtr stubPage;
        private static int stubUsed;

        /// <summary>
        /// A stub callable as stdcall f(self, args...) that calls the thiscall method target:
        ///   pop eax  (return address)   pop ecx  (self)   push eax   mov eax, target   jmp eax
        /// The method pops the remaining arguments itself on return, as stdcall expects.
        /// </summary>
        private static IntPtr ThisCallStub(IntPtr target)
        {
            if (stubPage == IntPtr.Zero)
            {
                stubPage = VirtualAlloc(IntPtr.Zero, new UIntPtr(4096), 0x3000 /* commit|reserve */, 0x40 /* execute+read+write */);
                if (stubPage == IntPtr.Zero) throw new InvalidOperationException("VirtualAlloc failed: " + Marshal.GetLastWin32Error());
            }
            byte[] code = { 0x58, 0x59, 0x50, 0xB8, 0, 0, 0, 0, 0xFF, 0xE0 };
            BitConverter.GetBytes(target.ToInt32()).CopyTo(code, 4);
            IntPtr at = new IntPtr(stubPage.ToInt32() + stubUsed);
            Marshal.Copy(code, 0, at, code.Length);
            stubUsed += 16;
            return at;
        }

        // ---- calls ---------------------------------------------------------------------------

        private static void SetIdentity(ulong steamId)
        {
            Marshal.WriteInt32(identity, 0, IdentityTypeSteamId);
            Marshal.WriteInt32(identity, 4, 8);
            Marshal.WriteInt64(identity, 8, (long)steamId);
        }

        /// <summary>SendMessageToUser. Returns the EResult (1 = OK).</summary>
        internal static int Send(ulong to, byte[] data, int size, int flags, int channel)
        {
            if (!Ready) return 0;
            EnsureSendBuffer(size);
            if (size > 0) Marshal.Copy(data, 0, sendBuffer, size);
            SetIdentity(to);
            return thisCallStubs
                ? sendS(iface, identity, sendBuffer, (uint)size, flags, channel)
                : sendC(iface, identity, sendBuffer, (uint)size, flags, channel);
        }

        /// <summary>AcceptSessionWithUser: true when a session with them exists or was pending.</summary>
        internal static bool Accept(ulong user)
        {
            if (!Ready) return false;
            SetIdentity(user);
            return thisCallStubs ? acceptS(iface, identity) : acceptC(iface, identity);
        }

        /// <summary>
        /// CloseSessionWithUser. Still allowed after Disable (the vtable is fine even when a
        /// message layout was not), so the other side sees the session closed and falls back.
        /// </summary>
        internal static void Close(ulong user)
        {
            if (iface == IntPtr.Zero) return;
            SetIdentity(user);
            if (thisCallStubs) closeS(iface, identity); else closeC(iface, identity);
        }

        /// <summary>The state of the session with a user, with its ping and the relay it goes through.</summary>
        internal static int SessionInfo(ulong user, out int ping, out bool relayed, out string relayPop, out string remotePop)
        {
            ping = -1; relayed = false; relayPop = null; remotePop = null;
            if (!Ready) return StateNone;
            SetIdentity(user);
            Marshal.WriteInt32(statusBuffer, StatusPing, -1);
            int state = thisCallStubs
                ? infoS(iface, identity, infoBuffer, statusBuffer)
                : infoC(iface, identity, infoBuffer, statusBuffer);
            if (state == StateNone) return state;
            ping = Marshal.ReadInt32(statusBuffer, StatusPing);
            relayed = (Marshal.ReadInt32(infoBuffer, InfoFlags) & InfoFlagRelayed) != 0;
            relayPop = PopName((uint)Marshal.ReadInt32(infoBuffer, InfoPopRelay));
            remotePop = PopName((uint)Marshal.ReadInt32(infoBuffer, InfoPopRemote));
            return state;
        }

        /// <summary>
        /// Receives up to one batch of messages on a channel and hands each to the handler, then
        /// frees it. Returns how many were handled, or -1 when a message did not look like a
        /// SteamNetworkingMessage_t (the interface is then disabled).
        /// </summary>
        internal static int Receive(int channel, MessageHandler handler)
        {
            if (!Ready) return 0;
            int count = thisCallStubs
                ? receiveS(iface, channel, messages, MaxBatch)
                : receiveC(iface, channel, messages, MaxBatch);
            if (count <= 0) return 0;
            if (count > MaxBatch) { Disable("ReceiveMessagesOnChannel trả về " + count); return -1; }

            for (int i = 0; i < count; i++)
            {
                IntPtr message = Marshal.ReadIntPtr(messages, i * IntPtr.Size);
                ulong from;
                if (!Check(message, channel, out from))
                {
                    // Leak the rest of the batch rather than call a release pointer we can't trust.
                    return -1;
                }
                IntPtr data = Marshal.ReadIntPtr(message, 0);
                int size = Marshal.ReadInt32(message, offSize);
                try
                {
                    handler(from, data, size);
                }
                finally
                {
                    Release(message);
                }
            }
            return count;
        }

        private static bool Check(IntPtr message, int channel, out ulong from)
        {
            from = 0;
            if (message == IntPtr.Zero) { Disable("tin nhắn rỗng"); return false; }

            int size = Marshal.ReadInt32(message, offSize);
            IntPtr data = Marshal.ReadIntPtr(message, 0);
            int gotChannel = Marshal.ReadInt32(message, offChannel);
            IntPtr releaseFn = Marshal.ReadIntPtr(message, offRelease);
            if (size < 0 || (size > 0 && data == IntPtr.Zero) || gotChannel != channel || releaseFn == IntPtr.Zero)
            {
                Disable("SteamNetworkingMessage_t không đúng bố cục (kênh " + gotChannel + "/" + channel + ", " + size + " byte)");
                return false;
            }

            if (!identityOffsetKnown)
            {
                if (IsSteamIdentity(message, 12)) offIdentity = 12;
                else if (IsSteamIdentity(message, 16)) offIdentity = 16;
                else { Disable("không đọc được người gửi trong SteamNetworkingMessage_t"); return false; }
                identityOffsetKnown = true;
            }
            if (!IsSteamIdentity(message, offIdentity))
            {
                // Only SteamID senders are possible here: we only ever talk to SteamIDs.
                Disable("người gửi không phải SteamID");
                return false;
            }
            from = (ulong)Marshal.ReadInt64(message, offIdentity + 8);
            return true;
        }

        private static bool IsSteamIdentity(IntPtr message, int offset)
        {
            return Marshal.ReadInt32(message, offset) == IdentityTypeSteamId
                && Marshal.ReadInt32(message, offset + 4) == 8;
        }

        private static void Release(IntPtr message)
        {
            IntPtr fn = Marshal.ReadIntPtr(message, offRelease);
            if (fn != releasePointer || release == null)
            {
                release = (ReleaseFn)Marshal.GetDelegateForFunctionPointer(fn, typeof(ReleaseFn));
                releasePointer = fn;
            }
            release(message);
        }

        /// <summary>A SteamNetworkingPOPID as its three or four letter data-centre code ("sgp", "hkg").</summary>
        private static string PopName(uint id)
        {
            if (id == 0) return null;
            char[] c = { (char)((id >> 16) & 0xFF), (char)((id >> 8) & 0xFF), (char)(id & 0xFF), (char)((id >> 24) & 0xFF) };
            int n = c[3] == 0 ? 3 : 4;
            for (int i = 0; i < n; i++) if (c[i] < 0x20 || c[i] > 0x7E) return null;
            return new string(c, 0, n);
        }
    }
}
