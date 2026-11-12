using System;
using System.Collections.Generic;
using RandomLevel;

namespace HarmonyLib
{
    public sealed class HarmonyPatch : Attribute { public HarmonyPatch(Type type, string method) { } }
    public sealed class HarmonyPostfix : Attribute { }
    public sealed class HarmonyAfter : Attribute { public HarmonyAfter(string id) { } }
    public sealed class HarmonyPriority : Attribute { public HarmonyPriority(int priority) { } }
    public static class Priority { public const int Last = 0; }
}
public enum GameState { NotSet, MainMenu, LoadKitchen, RunKitchen }
public sealed class ServerCampaignFlowController
{
    public bool InRound;
    public UnityEngine.GameObject gameObject = new UnityEngine.GameObject();
}
public sealed class MultiplayerController
{
    public int Stops;
    public void StopSynchronisation() { Stops++; GameUtils.Events.Add("stop"); }
}
public sealed class SceneEntry { public string SceneName; }
public sealed class LevelSettings { public SceneEntry SceneDirectoryVarientEntry; }
public sealed class GameSession { public LevelSettings LevelSettings; }
public static class GameUtils
{
    public static GameSession Session;
    public static MultiplayerController Multiplayer;
    public static readonly List<string> Events = new List<string>();
    public static GameSession GetGameSession() { return Session; }
    public static T RequireManager<T>() { return (T)(object)Multiplayer; }
}
namespace UnityEngine
{
    public sealed class GameObject
    {
        public bool activeInHierarchy = true;
        public SceneManagement.Scene scene = new SceneManagement.Scene { name = "current", Valid = true };
    }
    public static class Resources
    {
        public static ServerCampaignFlowController[] Flows = new ServerCampaignFlowController[0];
        public static T[] FindObjectsOfTypeAll<T>() { return (T[])(object)Flows; }
    }
}
namespace UnityEngine.SceneManagement
{
    public struct Scene
    {
        public string name;
        public bool Valid;
        public bool IsValid() { return Valid; }
    }
    public static class SceneManager
    {
        public static Scene Active = new Scene { name = "current", Valid = true };
        public static Scene GetActiveScene() { return Active; }
    }
}
namespace OC2DIYLevel
{
    public static class DIYLevelEntryUI { public static string selectedLevel; }
    public static class DIYLevelAssetBundleManager
    {
        public static bool FailLoading;
        public static int Loads;
        public static string LastScene;
        public static void LoadLevelServer(string scene)
        {
            Loads++;
            LastScene = scene;
            GameUtils.Events.Add("load:" + scene);
            if (FailLoading) throw new Exception("load failed");
        }
    }
}
namespace RandomLevel
{
    internal static class Plugin
    {
        internal const string DiyId = "dev.gua.overcooked.diylevel";
        internal static bool Enabled;
        internal static bool Control;
        internal static RandomRotation Rotation;
        internal static object Results;
        internal static TestLog Log = new TestLog();
        internal static bool CanControl() { return Control; }
    }
    internal sealed class TestLog
    {
        internal int Errors;
        internal void LogInfo(string message) { }
        internal void LogError(string message) { Errors++; }
    }
}
