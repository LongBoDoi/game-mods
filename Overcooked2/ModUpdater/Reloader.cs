using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Bootstrap;
using Mono.Cecil;
using Team17.Online;

namespace ModUpdater
{
    /// <summary>
    /// Swaps this repo's mods for the versions in a downloaded release while the game keeps running.
    ///
    /// Mono cannot unload an assembly, and the DLL files stay locked while the game runs, so this
    /// does what BepInEx's ScriptEngine does: the new DLL is read into memory, given a unique
    /// assembly name (so Mono never hands back the copy already loaded) and loaded from there. The
    /// old plugin is destroyed - its OnDestroy takes its Harmony patches off - and the new one is
    /// added to the BepInEx manager object in its place, under the same PluginInfo. The files on
    /// disk are still replaced when the game closes, so the next launch loads them normally.
    ///
    /// Only on the main menu and outside an online room: mid-level, the old mod's chefs, timers
    /// and screens would be left behind with nothing driving them.
    /// </summary>
    internal static class Reloader
    {
        private const string OwnPrefix = "com.manhl.overcooked2.";

        private static readonly MethodInfo SetInstance =
            typeof(PluginInfo).GetProperty("Instance").GetSetMethod(true);
        private static readonly MethodInfo SetMetadata =
            typeof(PluginInfo).GetProperty("Metadata").GetSetMethod(true);

        /// <summary>What is running now, per plugin GUID, so the same build is never loaded twice.</summary>
        private static readonly Dictionary<string, string> running = new Dictionary<string, string>();

        /// <summary>Main menu, not in an online room. Game types are touched only in here, so a
        /// game update that moved them makes this false instead of breaking the mod.</summary>
        internal static bool SafeNow()
        {
            try { return OnMainMenu(); }
            catch { return false; }
        }

        private static bool OnMainMenu()
        {
            if (T17FrontendFlow.Instance == null) return false;
            IOnlinePlatformManager platform = GameUtils.RequestManagerInterface<IOnlinePlatformManager>();
            IOnlineMultiplayerSessionCoordinator session = platform == null ? null : platform.OnlineMultiplayerSessionCoordinator();
            return session == null || session.Members() == null;
        }

        internal sealed class Result
        {
            internal readonly List<string> Reloaded = new List<string>();
            internal readonly List<string> Failed = new List<string>();
        }

        /// <summary>Reloads every installed mod of ours whose DLL in <paramref name="stagedDir"/>
        /// differs from the one running. Mod Updater itself is replaced when the game closes.</summary>
        internal static Result Run(string stagedDir)
        {
            var result = new Result();
            foreach (KeyValuePair<string, PluginInfo> pair in new List<KeyValuePair<string, PluginInfo>>(Chainloader.PluginInfos))
            {
                string guid = pair.Key;
                PluginInfo info = pair.Value;
                if (!guid.StartsWith(OwnPrefix, StringComparison.Ordinal) || guid == Plugin.Id) continue;
                if (info.Instance == null || string.IsNullOrEmpty(info.Location)) continue;
                string staged = Path.Combine(stagedDir, Path.GetFileName(info.Location));
                if (!File.Exists(staged)) continue;

                string name = info.Metadata.Name;
                try
                {
                    byte[] bytes = File.ReadAllBytes(staged);
                    string hash = Hash(bytes);
                    string current;
                    if (!running.TryGetValue(guid, out current)) current = Hash(File.ReadAllBytes(info.Location));
                    if (hash == current) continue;

                    // Everything that can fail happens before the old plugin is touched.
                    Type type = FindPlugin(LoadRenamed(bytes, Path.GetDirectoryName(info.Location)), guid);
                    BepInPlugin metadata = MetadataHelper.GetMetadata(type);
                    string oldVersion = info.Metadata.Version.ToString();

                    UnityEngine.Object.DestroyImmediate(info.Instance);
                    SetInstance.Invoke(info, new object[] { null });
                    SetMetadata.Invoke(info, new object[] { metadata });
                    // The constructor finds this PluginInfo by GUID and reuses it; Awake runs now.
                    var plugin = (BaseUnityPlugin)Chainloader.ManagerObject.AddComponent(type);
                    SetInstance.Invoke(info, new object[] { plugin });

                    running[guid] = hash;
                    result.Reloaded.Add(name + " " + oldVersion + " → " + metadata.Version);
                    Plugin.Log.LogInfo("Đã nạp lại " + name + ": " + oldVersion + " → " + metadata.Version + ".");
                }
                catch (Exception error)
                {
                    result.Failed.Add(name);
                    Plugin.Log.LogError("Không nạp lại được " + name + " (sẽ cài khi tắt game): " + error);
                }
            }
            return result;
        }

        private static Assembly LoadRenamed(byte[] bytes, string pluginDir)
        {
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Paths.ManagedPath);
            resolver.AddSearchDirectory(Paths.BepInExAssemblyDirectory);
            resolver.AddSearchDirectory(pluginDir);
            using (var input = new MemoryStream(bytes))
            using (AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { AssemblyResolver = resolver }))
            using (var output = new MemoryStream())
            {
                assembly.Name.Name = assembly.Name.Name + "-" + DateTime.Now.Ticks;
                assembly.Write(output);
                return Assembly.Load(output.ToArray());
            }
        }

        private static Type FindPlugin(Assembly assembly, string guid)
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException partial) { types = partial.Types; }
            foreach (Type type in types)
            {
                if (type == null || type.IsAbstract || !typeof(BaseUnityPlugin).IsAssignableFrom(type)) continue;
                BepInPlugin metadata = MetadataHelper.GetMetadata(type);
                if (metadata != null && metadata.GUID == guid) return type;
            }
            throw new InvalidOperationException("The new DLL has no plugin with GUID " + guid + ".");
        }

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA1.Create()) return Convert.ToBase64String(sha.ComputeHash(bytes));
        }
    }
}
