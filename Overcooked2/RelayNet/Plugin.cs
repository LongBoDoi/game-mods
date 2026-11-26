using System;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Steamworks;
using Team17.Online;
using Team17.Online.Multiplayer.Connection;
using UnityEngine;

[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

namespace RelayNet
{
    /// <summary>
    /// RelayNet: sends Overcooked! 2's online traffic over Steam Datagram Relay, Valve's modern
    /// relay network, instead of the legacy Steam P2P API the game was built on. That legacy API
    /// connects players directly when it can, and when it can't (common between countries and
    /// behind home routers) it falls back to Valve's old relay servers, which often take a long
    /// way round. Steam Datagram Relay routes over Valve's own backbone, picked by measured ping.
    ///
    /// Only players who both run RelayNet switch, each pair on its own; with anyone else the game
    /// keeps its old path, so mixed lobbies still work. See Relay for how the switch keeps the
    /// game's messages in order, and SteamMessages for how the interface is reached.
    ///
    /// An overlay (F9) shows each connection: which path it is on, the relay's ping and data
    /// centre, and the round trip the game itself measures, which is what you feel in play.
    /// </summary>
    [BepInPlugin(Id, "Relay Net", Version)]
    [BepInDependency("com.manhl.overcooked2.tiengviet", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInProcess("Overcooked2.exe")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "com.manhl.overcooked2.relaynet";
        public const string Version = "1.0.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> ShowStats;
        internal static ConfigEntry<KeyboardShortcut> StatsKey;

        private static readonly FieldInfo RemoteUserField = AccessTools.Field(typeof(LatencyMeasure), "m_RemoteUserId");
        private static readonly FieldInfo ReliableField = AccessTools.Field(typeof(LatencyMeasure), "m_bReliable");
        private static readonly FieldInfo AverageField = AccessTools.Field(typeof(LatencyMeasure), "m_fAverageOneWayLatency");

        private Harmony _harmony;
        private GUIStyle _style;
        private readonly StringBuilder _text = new StringBuilder();

        private void Awake()
        {
            Log = Logger;
            Enabled = Config.Bind("General", "Enabled", true,
                Lang.T("Send online traffic over Steam's relay network (Steam Datagram Relay) to players who also run RelayNet. Takes effect from the next online session.",
                    "Gửi dữ liệu online qua mạng relay của Steam (Steam Datagram Relay) với người chơi cũng có RelayNet. Có hiệu lực từ phiên online sau."));
            ShowStats = Config.Bind("General", "ShowStats", true,
                Lang.T("Show the connection overlay (path, relay ping, the game's measured round trip) during online play.",
                    "Hiện bảng kết nối (đường đi, ping relay, độ trễ khứ hồi game đo) khi chơi online."));
            StatsKey = Config.Bind("General", "StatsKey", new KeyboardShortcut(KeyCode.F9),
                Lang.T("Shows or hides the connection overlay.", "Bật/tắt bảng kết nối."));

            _harmony = new Harmony(Id);
            try
            {
                Type t = typeof(SteamOnlineMultiplayerSessionTransportCoordinator);
                _harmony.Patch(AccessTools.Method(t, "Open"), postfix: new HarmonyMethod(typeof(Plugin), "AfterOpen"));
                _harmony.Patch(AccessTools.Method(t, "Close"), prefix: new HarmonyMethod(typeof(Plugin), "BeforeClose"));
                _harmony.Patch(AccessTools.Method(t, "CloseConnection"), prefix: new HarmonyMethod(typeof(Plugin), "BeforeCloseConnection"));
                _harmony.Patch(AccessTools.Method(t, "SendData"), prefix: new HarmonyMethod(typeof(Plugin), "BeforeSendData"));
                _harmony.Patch(AccessTools.Method(t, "SendVoipData"), prefix: new HarmonyMethod(typeof(Plugin), "BeforeSendVoipData"));
                _harmony.Patch(AccessTools.Method(t, "Update"), postfix: new HarmonyMethod(typeof(Plugin), "AfterUpdate"));
                _harmony.Patch(AccessTools.Method(t, "OnDataEvent"), prefix: new HarmonyMethod(typeof(Plugin), "BeforeOnDataEvent"));
                _harmony.Patch(AccessTools.Method(typeof(LatencyMeasure), "OnLatencyReceived"), postfix: new HarmonyMethod(typeof(Plugin), "AfterLatencyReceived"));
                Log.LogInfo("Đã tải RelayNet " + Version + ": chơi online qua mạng relay của Steam.");
            }
            catch (Exception error)
            {
                _harmony.UnpatchSelf();
                _harmony = null;
                Log.LogError("Không thể bật RelayNet: " + error);
            }
        }

        private void Update()
        {
            if (_harmony == null) return;
            try
            {
                SelfTest.Tick();
                if (StatsKey.Value.IsDown()) ShowStats.Value = !ShowStats.Value;
            }
            catch (Exception error)
            {
                Log.LogError("RelayNet: " + error);
            }
        }

        private void OnDestroy()
        {
            try { Relay.End(); } catch (Exception) { }
            if (_harmony != null) _harmony.UnpatchSelf();
        }

        // ---- overlay -----------------------------------------------------------------------------

        private void OnGUI()
        {
            if (_harmony == null || !ShowStats.Value || !Relay.SessionOpen) return;
            _text.Length = 0;
            _text.Append(Lang.T("RelayNet", "RelayNet"));
            if (!Relay.RelayOn)
                _text.Append(Enabled.Value ? Lang.T(" — relay unavailable", " — không dùng được relay") : Lang.T(" — off", " — đang tắt"));
            int rows = 0;
            foreach (Peer p in Relay.Peers)
            {
                if (!p.GameTalks && !p.HeardFrom) continue;
                _text.Append('\n').Append(Relay.Name(p.Id)).Append(": ").Append(Relay.Describe(p, true));
                rows++;
            }
            if (rows == 0) _text.Append('\n').Append(Lang.T("waiting for players…", "đang chờ người chơi…"));

            float scale = Mathf.Max(1f, Screen.height / 1080f);
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.box);
                _style.alignment = TextAnchor.UpperLeft;
                _style.wordWrap = false;
                _style.normal.textColor = Color.white;
            }
            _style.fontSize = Mathf.RoundToInt(14 * scale);
            _style.padding = new RectOffset(Mathf.RoundToInt(8 * scale), Mathf.RoundToInt(8 * scale), Mathf.RoundToInt(6 * scale), Mathf.RoundToInt(6 * scale));
            GUIContent content = new GUIContent(_text.ToString());
            Vector2 size = _style.CalcSize(content);
            Rect rect = new Rect(Screen.width - size.x - 10 * scale, 10 * scale, size.x, size.y);
            GUI.Box(rect, content, _style);
        }

        // ---- patches -----------------------------------------------------------------------------

        private static void AfterOpen(SteamOnlineMultiplayerSessionTransportCoordinator __instance, bool __result)
        {
            if (!__result) return;
            try { Relay.Begin(__instance); }
            catch (Exception error) { Log.LogError("RelayNet (Open): " + error); }
        }

        private static void BeforeClose()
        {
            try { Relay.End(); }
            catch (Exception error) { Log.LogError("RelayNet (Close): " + error); }
        }

        private static void BeforeCloseConnection(CSteamID steamId)
        {
            try { Relay.Drop(steamId.m_SteamID); }
            catch (Exception error) { Log.LogError("RelayNet (CloseConnection): " + error); }
        }

        private static bool BeforeSendData(CSteamID toSteamId, byte[] data, int dataSize, bool sendReliably, ref bool __result)
        {
            try
            {
                bool result;
                if (!toSteamId.IsValid() || toSteamId.IsLobby() || data == null) return true;
                if (!Relay.TrySend(toSteamId, data, dataSize, sendReliably, false, out result)) return true;
                __result = result;
                return false;
            }
            catch (Exception error)
            {
                Log.LogError("RelayNet (SendData): " + error);
                return true;
            }
        }

        private static bool BeforeSendVoipData(CSteamID toSteamId, byte[] data, int dataSize, ref bool __result)
        {
            try
            {
                bool result;
                if (!toSteamId.IsValid() || toSteamId.IsLobby() || data == null) return true;
                if (!Relay.TrySend(toSteamId, data, dataSize, false, true, out result)) return true;
                __result = result;
                return false;
            }
            catch (Exception error)
            {
                Log.LogError("RelayNet (SendVoipData): " + error);
                return true;
            }
        }

        private static void AfterUpdate(SteamOnlineMultiplayerSessionTransportCoordinator __instance, byte[] receiveBuffer)
        {
            try { Relay.Pump(__instance, receiveBuffer); }
            catch (Exception error) { Log.LogError("RelayNet (Update): " + error); }
        }

        private static bool BeforeOnDataEvent(CSteamID fromSteamId, byte[] data, int dataSizeInBytes)
        {
            try { return !Relay.TakeMarker(fromSteamId, data, dataSizeInBytes); }
            catch (Exception) { return true; }
        }

        private static void AfterLatencyReceived(LatencyMeasure __instance)
        {
            try
            {
                SteamOnlineMultiplayerSessionUserId user = RemoteUserField.GetValue(__instance) as SteamOnlineMultiplayerSessionUserId;
                if (user == null) return;
                Relay.NoteLatency(user.m_steamId.m_SteamID, (bool)ReliableField.GetValue(__instance), (float)AverageField.GetValue(__instance));
            }
            catch (Exception) { }
        }
    }
}
