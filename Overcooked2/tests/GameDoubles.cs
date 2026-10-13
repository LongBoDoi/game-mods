using System;
using System.Collections.Generic;
using System.Reflection;

namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)]
    internal sealed class HarmonyPatch : Attribute
    { public HarmonyPatch(Type type, string method) {} }
    [AttributeUsage(AttributeTargets.Class)]
    internal sealed class HarmonyAfter : Attribute
    { public HarmonyAfter(params string[] owners) {} }
    internal static class AccessTools
    {
        internal static MethodInfo Method(Type type, string name)
        { return type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); }
        internal static FieldInfo Field(Type type, string name)
        { return type.GetField(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic); }
    }
}

public class ClientLobbyFlowController
{
    public int HostCalls;
    public bool FailHosting;
    protected void HostGame()
    {
        HostCalls++;
        if (FailHosting) throw new InvalidOperationException("Host failed.");
    }
}

namespace Steamworks
{
    public struct CSteamID
    {
        public ulong m_SteamID;
        public bool IsValid() { return m_SteamID != 0; }
    }
}
namespace Team17.Online
{
    public interface IOnlineMultiplayerSessionUserId
    {
        string DisplayName { get; }
        bool IsLocal { get; }
        bool IsHost { get; }
    }
    public class SteamOnlineMultiplayerSessionUserId : IOnlineMultiplayerSessionUserId
    {
        public Steamworks.CSteamID m_steamId;
        public string DisplayName { get; set; }
        public bool IsLocal { get; set; }
        public bool IsHost { get; set; }
    }
    public class User
    {
        public enum MachineID { One, Two, Three, Four }
        public bool IsLocal;
        public MachineID Machine;
        public IOnlineMultiplayerSessionUserId SessionId;
        public string DisplayName;
        public GameState GameState;
    }
    public class FastList<T>
    {
        public T[] _items = new T[0];
        public int Count { get { return _items.Length; } }
        public bool Contains(T item) { return Array.IndexOf(_items, item) >= 0; }
        public void Remove(T item)
        {
            var copy = new List<T>(_items);
            copy.Remove(item);
            _items = copy.ToArray();
        }
    }
    public static class ServerUserSystem
    {
        public static User.MachineID s_LocalMachineId = User.MachineID.One;
        public static FastList<User> m_Users = new FastList<User>();
        public static readonly List<User> Removed = new List<User>();
        public static Action<User> AfterRemoval;
        public static void RemoveUser(User user, bool broadcast)
        {
            Removed.Add(user);
            m_Users.Remove(user);
            if (AfterRemoval != null) AfterRemoval(user);
        }
    }
    public interface IOnlineMultiplayerSessionCoordinator
    {
        bool IsHost();
        IOnlineMultiplayerSessionUserId[] Members();
    }
    public interface IOnlinePlatformManager
    { IOnlineMultiplayerSessionCoordinator OnlineMultiplayerSessionCoordinator(); }
    public sealed class TestPlatform : IOnlinePlatformManager, IOnlineMultiplayerSessionCoordinator
    {
        public bool Host = true;
        public bool InSession = true;
        public IOnlineMultiplayerSessionCoordinator OnlineMultiplayerSessionCoordinator() { return this; }
        public bool IsHost() { return Host; }
        public IOnlineMultiplayerSessionUserId[] Members()
        { return InSession ? new IOnlineMultiplayerSessionUserId[0] : null; }
    }
}
public enum GameState { MainMenu, PartyLobby, VSLobby, InLevel, InMap, LoadKitchen }
public enum eConnectionModeSwitchProgress { Complete, InProgress }
public interface IConnectionModeSwitchStatus { eConnectionModeSwitchProgress GetProgress(); }
public sealed class TestConnectionStatus : IConnectionModeSwitchStatus
{
    public eConnectionModeSwitchProgress Progress = eConnectionModeSwitchProgress.Complete;
    public eConnectionModeSwitchProgress GetProgress() { return Progress; }
}
public static class ConnectionModeSwitcher
{
    private static Team17.Online.Multiplayer.Server m_LocalServer;
    public static void SetServer(Team17.Online.Multiplayer.Server server) { m_LocalServer = server; }
    public static TestConnectionStatus Status = new TestConnectionStatus();
    public static IConnectionModeSwitchStatus GetStatus() { return Status; }
}
public static class GameUtils
{
    public static Team17.Online.TestPlatform Platform = new Team17.Online.TestPlatform();
    public static T RequestManagerInterface<T>() where T : class { return Platform as T; }
}
namespace LobbyTools
{
    internal sealed class TestLog { internal void LogInfo(string text) {} }
    internal static class Plugin
    {
        internal static BanStore Bans;
        internal sealed class BoolSetting { internal bool Value; }
        internal static BoolSetting ForceHost = new BoolSetting();
        internal static bool ConnectionMonitorReady = true;
        internal static TestLog Log = new TestLog();
    }
}

namespace Team17.Online.Multiplayer
{
    public class Server
    {
        private Dictionary<IOnlineMultiplayerSessionUserId, Connection.NetworkConnection> m_RemoteClientConnections =
            new Dictionary<IOnlineMultiplayerSessionUserId, Connection.NetworkConnection>();
        public void Add(IOnlineMultiplayerSessionUserId session, Connection.NetworkConnection connection)
        {
            m_RemoteClientConnections.Add(session, connection);
            var remote = connection as Connection.RemoteConnection;
            if (remote != null) remote.Session = session;
        }
    }
}
namespace Team17.Online.Multiplayer.Connection
{
    public interface NetworkConnection { bool GetLatencyTestPaused(); }
    public class BaseConnection
    {
        public virtual IOnlineMultiplayerSessionUserId GetRemoteSessionUserId() { return null; }
        protected float m_fLastReliablePacketReceivedTime;
        protected float m_fLastUnreliablePacketReceivedTime;
    }
    public class LatencyMeasure
    {
        private Queue<float> m_OneWayLatencyHistory = new Queue<float>();
        private float m_fTimeTimeReceived;
        public void Set(float[] oneWaySeconds, float now)
        { m_OneWayLatencyHistory = new Queue<float>(oneWaySeconds); m_fTimeTimeReceived = now; }
        public void SetReplyTime(float now) { m_fTimeTimeReceived = now; }
        public float GetAverageOneWayTripTime()
        {
            float sum = 0;
            foreach (float latency in m_OneWayLatencyHistory) sum += latency;
            return m_OneWayLatencyHistory.Count == 0 ? 0 : sum / m_OneWayLatencyHistory.Count;
        }
    }
    public class RemoteConnection : BaseConnection, NetworkConnection
    {
        public IOnlineMultiplayerSessionUserId Session;
        public override IOnlineMultiplayerSessionUserId GetRemoteSessionUserId() { return Session; }
        private LatencyMeasure m_ReliableLatencyMeasure = new LatencyMeasure();
        private LatencyMeasure m_UnreliableLatencyMeasure = new LatencyMeasure();
        public bool Paused;
        public bool GetLatencyTestPaused() { return Paused; }
        public void SetChannelTimes(float reliable, float unreliable)
        {
            m_ReliableLatencyMeasure.SetReplyTime(reliable);
            m_UnreliableLatencyMeasure.SetReplyTime(unreliable);
        }
        public void Set(float[] reliable, float[] unreliable, float now)
        {
            m_ReliableLatencyMeasure.Set(reliable, now);
            m_UnreliableLatencyMeasure.Set(unreliable, now);
            m_fLastReliablePacketReceivedTime = now;
            m_fLastUnreliablePacketReceivedTime = now;
        }
    }
}
