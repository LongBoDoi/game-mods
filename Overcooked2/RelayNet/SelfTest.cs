using System;
using System.Runtime.InteropServices;
using Steamworks;
using UnityEngine;

namespace RelayNet
{
    /// <summary>
    /// Once Steam is up, RelayNet sends itself one message over ISteamNetworkingMessages and reads
    /// it back. That exercises every native piece the mod relies on (the interface lookup, the
    /// vtable calls, the SteamNetworkingMessage_t layout and its release function) on this
    /// machine, before any game traffic depends on them. A message that comes back wrong turns
    /// the relay off for this run. Steam not looping a message back to its sender within the
    /// time limit only means the test can't tell: the relay stays on, and every message from
    /// another player is still checked the same way.
    /// </summary>
    internal static class SelfTest
    {
        private const float Timeout = 15f;

        private static bool started, done;
        private static float deadline;
        private static ulong me;
        private static readonly byte[] payload = new byte[16];

        internal static string Status = "chưa chạy";

        /// <summary>False only when the test proved the native layer wrong.</summary>
        internal static bool Usable { get; private set; }

        static SelfTest() { Usable = true; }

        internal static void Tick()
        {
            if (done) return;
            if (!started)
            {
                if (!SteamPlayerManager.Initialized) return;
                started = true;
                if (!SteamMessages.Init())
                {
                    Finish(false, "không mở được giao diện: " + SteamMessages.Error);
                    return;
                }
                me = SteamUser.GetSteamID().m_SteamID;
                payload[0] = (byte)'R'; payload[1] = (byte)'N'; payload[2] = (byte)'E'; payload[3] = (byte)'T';
                payload[4] = Relay.SelfTestType;
                byte[] nonce = BitConverter.GetBytes(DateTime.UtcNow.Ticks);
                Buffer.BlockCopy(nonce, 0, payload, 5, 8);
                if (!Relay.SendSelfTest(me, payload))
                {
                    // Steam refusing a message to oneself says nothing about the layout.
                    Inconclusive("Steam không nhận gửi cho chính mình");
                    return;
                }
                deadline = Time.realtimeSinceStartup + Timeout;
                return;
            }

            if (Relay.ReceiveSelfTest() < 0)
            {
                Finish(false, SteamMessages.Error);
                return;
            }
            if (!done && Time.realtimeSinceStartup > deadline)
                Inconclusive("không nhận lại được tin gửi cho chính mình sau " + Timeout + " giây");
        }

        internal static void Received(ulong from, IntPtr data, int size)
        {
            if (done) return;
            byte[] got = new byte[Math.Max(size, 0)];
            if (size > 0) Marshal.Copy(data, got, 0, size);
            bool same = size == payload.Length && from == me;
            for (int i = 0; same && i < size; i++) same = got[i] == payload[i];
            if (same) Finish(true, "đạt: gửi và nhận lại qua " + SteamMessages.InterfaceVersion + " đúng từng byte");
            else
            {
                SteamMessages.Disable("tự kiểm tra nhận sai dữ liệu (" + size + " byte từ " + from + ")");
                Finish(false, SteamMessages.Error);
            }
        }

        private static void Inconclusive(string why)
        {
            done = true;
            Status = "không kết luận được: " + why;
            Plugin.Log.LogInfo("Tự kiểm tra mạng relay: " + Status + ". Vẫn dùng relay; mỗi gói nhận được vẫn được kiểm tra.");
        }

        private static void Finish(bool ok, string text)
        {
            done = true;
            Status = text;
            if (ok)
            {
                Plugin.Log.LogInfo("Tự kiểm tra mạng relay: " + text + ".");
            }
            else
            {
                Usable = false;
                Plugin.Log.LogError("Tự kiểm tra mạng relay thất bại: " + text + ". RelayNet sẽ không dùng relay trong lần chạy này.");
            }
        }
    }
}
