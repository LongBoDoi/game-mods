using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using Unity.Services.Authentication;
using Unity.Services.Core;
using UnityEngine;

namespace LocalCoop
{
    // A guest copy must not touch Player 1's data, and must not look like Player 1 to the
    // game's room service:
    //   - saves and every other file under persistentDataPath go to
    //     <persistentDataPath>\LocalCoop\Player<n>\ (seeded once from Player 1's saves, so the
    //     guest's title screen looks like Player 1's and skips first-run screens);
    //   - PlayerPrefs reads fall through to Player 1's (language, keybinds, volume...), but writes
    //     land in an overlay file of the guest's own - including its window size;
    //   - Unity Services sign in with their own profile, so the room service sees a different
    //     player (it rejects a player joining their own room);
    //   - the display name gets " (P<n>)" so the players can be told apart;
    //   - Steam Input's native API is skipped: it would merge every controller into one.
    internal static class GuestIsolation
    {
        private static string s_realDataPath;
        private static string s_guestDataPath;
        private static bool s_quitting;
        private static float s_nextCheck;

        public static void Apply(Harmony harmony)
        {
            s_realDataPath = Application.persistentDataPath;
            s_guestDataPath = Path.Combine(Path.Combine(s_realDataPath, "LocalCoop"), "Player" + Coop.Player);
            Directory.CreateDirectory(s_guestDataPath);
            Seed();

            harmony.Patch(AccessTools.PropertyGetter(typeof(Application), nameof(Application.persistentDataPath)),
                postfix: new HarmonyMethod(typeof(GuestIsolation), nameof(DataPathPostfix)));

            PrefsOverlay.Load(Path.Combine(s_guestDataPath, "LocalCoop.prefs"));
            PrefsOverlay.Install(harmony);
            // Start windowed at the size Player 1 picked; GameSettings applies these at boot.
            PrefsOverlay.SetDisplay(WindowLayout.LaunchWidth, WindowLayout.LaunchHeight);

            harmony.Patch(AccessTools.Method(typeof(UnityServices), nameof(UnityServices.InitializeAsync), Type.EmptyTypes),
                prefix: new HarmonyMethod(typeof(GuestIsolation), nameof(InitializeServicesPrefix)));

            harmony.Patch(AccessTools.Method(typeof(MultiplayerLobbySessionPresenter), nameof(MultiplayerLobbySessionPresenter.GetOrCreateLocalDisplayName)),
                postfix: new HarmonyMethod(typeof(GuestIsolation), nameof(DisplayNamePostfix)));

            // Rooms look a member's name up from Steam by the Steam id it publishes. Every window is
            // the same Steam account, so a guest publishes none and is shown by its own name.
            harmony.Patch(AccessTools.Method(typeof(UnityRestaurantMultiplayerSessionsGateway), "ApplySteamPlayerIdentity"),
                prefix: new HarmonyMethod(typeof(GuestIsolation), nameof(Skip)));

            harmony.Patch(AccessTools.Method(typeof(Steamworks.SteamInput), nameof(Steamworks.SteamInput.Init)),
                prefix: new HarmonyMethod(typeof(GuestIsolation), nameof(SteamInputInitPrefix)));

            Plugin.Log.LogInfo("Guest data folder: " + s_guestDataPath);
        }

        /// <summary>Copies Player 1's saves the first time this guest number runs.</summary>
        private static void Seed()
        {
            foreach (string folder in new[] { "Saves", "SkinPaintSkins", "BearPaintSkins" })
            {
                string from = Path.Combine(s_realDataPath, folder);
                string to = Path.Combine(s_guestDataPath, folder);
                if (!Directory.Exists(from) || Directory.Exists(to)) continue;
                try
                {
                    CopyDirectory(from, to);
                    Plugin.Log.LogInfo($"Copied Player 1's {folder} for this player.");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Could not copy {folder}: {e.Message}");
                }
            }
        }

        private static void CopyDirectory(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string file in Directory.GetFiles(from))
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
            foreach (string dir in Directory.GetDirectories(from))
                CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
        }

        private static void DataPathPostfix(ref string __result)
        {
            if (s_guestDataPath != null) __result = s_guestDataPath;
        }

        private static bool InitializeServicesPrefix(ref Task __result)
        {
            // Profiles: letters, digits, '-' and '_', at most 30 characters.
            var options = new InitializationOptions().SetProfile("LocalCoopPlayer" + Coop.Player);
            __result = UnityServices.InitializeAsync(options);
            return false;
        }

        private static void DisplayNamePostfix(ref string __result)
        {
            if (string.IsNullOrEmpty(__result) || __result.StartsWith("__", StringComparison.Ordinal)) return;
            string suffix = " (P" + Coop.Player + ")";
            // The game caps names at 48 UTF-8 bytes.
            int budget = 48 - Encoding.UTF8.GetByteCount(suffix);
            var sb = new StringBuilder();
            var e = StringInfo.GetTextElementEnumerator(__result);
            int used = 0;
            while (e.MoveNext())
            {
                string element = e.GetTextElement();
                int bytes = Encoding.UTF8.GetByteCount(element);
                if (used + bytes > budget) break;
                sb.Append(element);
                used += bytes;
            }
            __result = sb.ToString().TrimEnd() + suffix;
        }

        private static bool Skip() => false;

        private static bool SteamInputInitPrefix(ref bool __result)
        {
            __result = false;
            return false;
        }

        /// <summary>Closes this guest when Player 1 asks, or when Player 1's game is gone.</summary>
        public static void Update()
        {
            PrefsOverlay.SaveIfDirty(false);
            if (s_quitting || Time.realtimeSinceStartup < s_nextCheck) return;
            s_nextCheck = Time.realtimeSinceStartup + 1f;

            string reason = null;
            if (!Coop.IsAlive(Coop.HostPid)) reason = "Player 1's game has closed";
            else if (File.Exists(Coop.FilePath("quit-" + Coop.Player))) reason = "Player 1 closed this window";
            if (reason == null) return;

            s_quitting = true;
            Coop.Delete("quit-" + Coop.Player);
            Plugin.Log.LogInfo(reason + "; quitting.");
            Flush();
            Application.Quit();
        }

        public static void Flush()
        {
            if (Coop.IsGuest) PrefsOverlay.SaveIfDirty(true);
        }
    }

    // PlayerPrefs for a guest: its own values first, Player 1's underneath, never written to.
    internal static class PrefsOverlay
    {
        private enum Kind { Int, Float, String, Deleted }

        private struct Entry
        {
            public Kind Kind;
            public int I;
            public float F;
            public string S;
        }

        private static readonly Dictionary<string, Entry> s_values = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static bool s_deletedAll;
        private static bool s_dirty;
        private static float s_lastSave;
        private static string s_path;

        public static void Load(string path)
        {
            s_path = path;
            if (!File.Exists(path)) return;
            try
            {
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string[] f = line.Split('\t');
                    if (f.Length == 1 && f[0] == "*") { s_deletedAll = true; continue; }
                    if (f.Length != 3) continue;
                    string key = Decode(f[1]);
                    switch (f[0])
                    {
                        case "i": s_values[key] = new Entry { Kind = Kind.Int, I = int.Parse(f[2], CultureInfo.InvariantCulture) }; break;
                        case "f": s_values[key] = new Entry { Kind = Kind.Float, F = float.Parse(f[2], CultureInfo.InvariantCulture) }; break;
                        case "s": s_values[key] = new Entry { Kind = Kind.String, S = Decode(f[2]) }; break;
                        case "d": s_values[key] = new Entry { Kind = Kind.Deleted }; break;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not read " + path + ": " + e.Message);
            }
        }

        public static void SaveIfDirty(bool force)
        {
            if (!s_dirty || s_path == null) return;
            if (!force && Time.realtimeSinceStartup - s_lastSave < 2f) return;
            s_dirty = false;
            s_lastSave = Time.realtimeSinceStartup;
            var sb = new StringBuilder();
            if (s_deletedAll) sb.Append("*\n");
            foreach (var kv in s_values)
            {
                string key = Encode(kv.Key);
                switch (kv.Value.Kind)
                {
                    case Kind.Int: sb.Append("i\t").Append(key).Append('\t').Append(kv.Value.I.ToString(CultureInfo.InvariantCulture)).Append('\n'); break;
                    case Kind.Float: sb.Append("f\t").Append(key).Append('\t').Append(kv.Value.F.ToString("R", CultureInfo.InvariantCulture)).Append('\n'); break;
                    case Kind.String: sb.Append("s\t").Append(key).Append('\t').Append(Encode(kv.Value.S)).Append('\n'); break;
                    case Kind.Deleted: sb.Append("d\t").Append(key).Append("\t-\n"); break;
                }
            }
            try
            {
                string temp = s_path + ".tmp";
                File.WriteAllText(temp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(s_path)) File.Replace(temp, s_path, null);
                else File.Move(temp, s_path);
            }
            catch (Exception e)
            {
                s_dirty = true;
                Plugin.Log.LogWarning("Could not save " + s_path + ": " + e.Message);
            }
        }

        private static string Encode(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? string.Empty));
        private static string Decode(string s) => Encoding.UTF8.GetString(Convert.FromBase64String(s));

        public static void SetDisplay(int width, int height)
        {
            // FullScreenMode.Windowed; keys are the ones GameSettings.Bootstrap reads.
            Set("tc_settings_fullscreen", new Entry { Kind = Kind.Int, I = (int)FullScreenMode.Windowed });
            if (width > 0 && height > 0)
            {
                Set("tc_settings_resolution_width", new Entry { Kind = Kind.Int, I = width });
                Set("tc_settings_resolution_height", new Entry { Kind = Kind.Int, I = height });
            }
        }

        private static void Set(string key, Entry entry)
        {
            if (key == null) return;
            s_values[key] = entry;
            s_dirty = true;
        }

        /// <summary>0: not in the overlay (ask Player 1's prefs); 1: found; -1: deleted here.</summary>
        private static int Find(string key, out Entry entry)
        {
            entry = default;
            if (key != null && s_values.TryGetValue(key, out entry)) return entry.Kind == Kind.Deleted ? -1 : 1;
            return s_deletedAll ? -1 : 0;
        }

        public static void Install(Harmony harmony)
        {
            var t = typeof(PlayerPrefs);
            void P(string name, Type[] args, string patch) =>
                harmony.Patch(AccessTools.Method(t, name, args), prefix: new HarmonyMethod(typeof(PrefsOverlay), patch));

            P(nameof(PlayerPrefs.SetInt), new[] { typeof(string), typeof(int) }, nameof(SetInt));
            P(nameof(PlayerPrefs.SetFloat), new[] { typeof(string), typeof(float) }, nameof(SetFloat));
            P(nameof(PlayerPrefs.SetString), new[] { typeof(string), typeof(string) }, nameof(SetString));
            P(nameof(PlayerPrefs.GetInt), new[] { typeof(string), typeof(int) }, nameof(GetInt));
            P(nameof(PlayerPrefs.GetInt), new[] { typeof(string) }, nameof(GetInt1));
            P(nameof(PlayerPrefs.GetFloat), new[] { typeof(string), typeof(float) }, nameof(GetFloat));
            P(nameof(PlayerPrefs.GetFloat), new[] { typeof(string) }, nameof(GetFloat1));
            P(nameof(PlayerPrefs.GetString), new[] { typeof(string), typeof(string) }, nameof(GetString));
            P(nameof(PlayerPrefs.GetString), new[] { typeof(string) }, nameof(GetString1));
            P(nameof(PlayerPrefs.HasKey), new[] { typeof(string) }, nameof(HasKey));
            P(nameof(PlayerPrefs.DeleteKey), new[] { typeof(string) }, nameof(DeleteKey));
            P(nameof(PlayerPrefs.DeleteAll), Type.EmptyTypes, nameof(DeleteAll));
            P(nameof(PlayerPrefs.Save), Type.EmptyTypes, nameof(Save));
        }

        private static bool SetInt(string key, int value) { Set(key, new Entry { Kind = Kind.Int, I = value }); return false; }
        private static bool SetFloat(string key, float value) { Set(key, new Entry { Kind = Kind.Float, F = value }); return false; }
        private static bool SetString(string key, string value) { Set(key, new Entry { Kind = Kind.String, S = value ?? string.Empty }); return false; }

        private static bool GetInt(string key, int defaultValue, ref int __result)
        {
            int found = Find(key, out Entry e);
            if (found == 0) return true;
            __result = found < 0 ? defaultValue : e.Kind == Kind.Int ? e.I : e.Kind == Kind.Float ? (int)e.F : defaultValue;
            return false;
        }

        private static bool GetInt1(string key, ref int __result) => GetInt(key, 0, ref __result);

        private static bool GetFloat(string key, float defaultValue, ref float __result)
        {
            int found = Find(key, out Entry e);
            if (found == 0) return true;
            __result = found < 0 ? defaultValue : e.Kind == Kind.Float ? e.F : e.Kind == Kind.Int ? e.I : defaultValue;
            return false;
        }

        private static bool GetFloat1(string key, ref float __result) => GetFloat(key, 0f, ref __result);

        private static bool GetString(string key, string defaultValue, ref string __result)
        {
            int found = Find(key, out Entry e);
            if (found == 0) return true;
            __result = found < 0 ? defaultValue : e.Kind == Kind.String ? e.S : defaultValue;
            return false;
        }

        private static bool GetString1(string key, ref string __result) => GetString(key, string.Empty, ref __result);

        private static bool HasKey(string key, ref bool __result)
        {
            int found = Find(key, out _);
            if (found == 0) return true;
            __result = found > 0;
            return false;
        }

        private static bool DeleteKey(string key) { Set(key, new Entry { Kind = Kind.Deleted }); return false; }

        private static bool DeleteAll()
        {
            s_values.Clear();
            s_deletedAll = true;
            s_dirty = true;
            return false;
        }

        private static bool Save() { SaveIfDirty(true); return false; }
    }
}
