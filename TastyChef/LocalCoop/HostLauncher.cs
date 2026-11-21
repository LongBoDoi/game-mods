using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LocalCoop
{
    // Player 1's side: opens and closes the other players' windows (F9 / Shift+F9), tells every
    // window where to sit (layout.txt), and forgets a guest's controller when it leaves.
    internal static class HostLauncher
    {
        private const string SteamAppId = "5019920";

        private static readonly SortedDictionary<int, Process> s_guests = new SortedDictionary<int, Process>();
        private static float s_nextCheck;
        private static string s_lastLayout;

        public static bool HasGuests => s_guests.Count > 0;
        public static int GuestCount => s_guests.Count;

        private static int s_playersAtStart;
        private static bool s_openedAtStart;

        public static void Init()
        {
            AllowSecondCopy();
            CleanStaleSessions();
            s_playersAtStart = Plugin.PlayersAtStart.Value;
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "-tccoop-players" && int.TryParse(args[i + 1], out int n)) s_playersAtStart = Mathf.Clamp(n, 1, Coop.MaxPlayers);
        }

        /// <summary>
        /// The game is built to allow one running copy (boot.config "single-instance="): a second
        /// start just focuses the first window and exits. boot.config is read when a copy starts,
        /// so removing the line here is enough for the guests started from this game.
        /// </summary>
        private static void AllowSecondCopy()
        {
            string path = Path.Combine(Application.dataPath, "boot.config");
            try
            {
                if (!File.Exists(path)) return;
                string text = File.ReadAllText(path);
                var lines = text.Replace("\r\n", "\n").Split('\n');
                if (!lines.Any(l => l.StartsWith("single-instance=", StringComparison.Ordinal))) return;
                string backup = path + ".localcoop-backup";
                if (!File.Exists(backup)) File.Copy(path, backup);
                File.WriteAllText(path, string.Join("\n", lines.Where(l => !l.StartsWith("single-instance=", StringComparison.Ordinal))));
                Plugin.Log.LogInfo("boot.config: allowed more than one copy of the game to run.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not update boot.config (" + e.Message + "); other players' windows may close right after opening.");
            }
        }

        private static void CleanStaleSessions()
        {
            try
            {
                if (!Directory.Exists(Coop.Root)) return;
                foreach (string dir in Directory.GetDirectories(Coop.Root))
                {
                    if (int.TryParse(Path.GetFileName(dir), out int pid) && pid != Coop.HostPid && !Coop.IsAlive(pid))
                        Directory.Delete(dir, true);
                }
                if (Directory.Exists(Coop.Dir)) Directory.Delete(Coop.Dir, true);
            }
            catch { }
        }

        public static void Update()
        {
            if (Coop.IsGuest) return;

            if (Pressed(Plugin.RemovePlayerKey.Value)) RemoveLastGuest();
            else if (Pressed(Plugin.AddPlayerKey.Value)) AddGuest();

            if (!s_openedAtStart && s_playersAtStart > 1 && RoomLink.AtTitle(out _))
            {
                s_openedAtStart = true;
                while (s_guests.Count + 1 < s_playersAtStart && AddGuest()) { }
            }

            if (Time.realtimeSinceStartup < s_nextCheck) return;
            s_nextCheck = Time.realtimeSinceStartup + 1f;

            var gone = s_guests.Where(kv => HasExited(kv.Value)).Select(kv => kv.Key).ToList();
            foreach (int p in gone)
            {
                Plugin.Log.LogInfo($"Player {p}'s window closed.");
                s_guests[p].Dispose();
                s_guests.Remove(p);
                ForgetClaims(p);
                Coop.Delete("quit-" + p);
            }
            if (gone.Count > 0) WriteLayout();
        }

        private static bool AddGuest()
        {
            int player = Enumerable.Range(2, Coop.MaxPlayers - 1).FirstOrDefault(p => !s_guests.ContainsKey(p));
            if (player == 0)
            {
                Overlay.Toast($"Already {Coop.MaxPlayers} players - the game's rooms hold four.");
                return false;
            }

            var players = new List<int> { 1 };
            players.AddRange(s_guests.Keys);
            players.Add(player);
            players.Sort();
            WindowLayout.Rect rect = WindowLayout.Plan(players).First(kv => kv.Key == player).Value;

            string exe = Process.GetCurrentProcess().MainModule.FileName;
            // Its own Unity log: two copies writing one Player.log overwrite each other's lines.
            string guestData = Path.Combine(Path.Combine(Application.persistentDataPath, "LocalCoop"), "Player" + player);
            Directory.CreateDirectory(guestData);
            var psi = new ProcessStartInfo(exe,
                $"{Coop.PlayerArg} {player} {Coop.HostArg} {Coop.HostPid} -screen-fullscreen 0 -popupwindow " +
                $"-screen-width {rect.W} -screen-height {rect.H} -logFile \"{Path.Combine(guestData, "Player.log")}\"")
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe),
            };
            // What Steam sets for games it starts; lets the guest's Steam API find the game.
            psi.EnvironmentVariables["SteamAppId"] = SteamAppId;
            psi.EnvironmentVariables["SteamGameId"] = SteamAppId;
            // BepInEx's loader (Doorstop) marks this process with DOORSTOP_* variables; inherited,
            // they make the guest's loader think mods are already loaded, and it skips them.
            foreach (string key in psi.EnvironmentVariables.Keys.Cast<string>().ToList())
                if (key.StartsWith("DOORSTOP_", StringComparison.OrdinalIgnoreCase)) psi.EnvironmentVariables.Remove(key);

            try
            {
                Directory.CreateDirectory(Coop.Dir);
                ScreenPrefsGuard.Arm();
                Coop.Delete("quit-" + player);
                s_guests[player] = Process.Start(psi);
                Plugin.Log.LogInfo($"Opened a window for Player {player} (process {s_guests[player].Id}).");
                Overlay.Toast($"Opening Player {player}'s window...");
                WriteLayout();
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not start another copy of the game: " + e);
                Overlay.Toast("Could not open another window - see BepInEx\\LogOutput.log.");
                return false;
            }
        }

        private static void RemoveLastGuest()
        {
            if (s_guests.Count == 0) return;
            int player = s_guests.Keys.Last();
            Coop.Write("quit-" + player, "");
            Overlay.Toast($"Closing Player {player}'s window...");
        }

        private static void WriteLayout()
        {
            var players = new List<int>();
            if (s_guests.Count > 0)
            {
                players.Add(1);
                players.AddRange(s_guests.Keys);
            }
            var sb = new StringBuilder();
            foreach (var kv in WindowLayout.Plan(players))
                sb.Append(kv.Key).Append(' ').Append(kv.Value.X).Append(' ').Append(kv.Value.Y).Append(' ')
                  .Append(kv.Value.W).Append(' ').Append(kv.Value.H).Append('\n');
            string text = sb.ToString();
            if (text == s_lastLayout) return;
            s_lastLayout = text;
            Coop.Write("layout.txt", text);
        }

        private static void ForgetClaims(int player)
        {
            string text = Coop.Read("claims.txt");
            if (text == null) return;
            var kept = Coop.Lines(text).Where(f => f[0] != player.ToString(CultureInfo.InvariantCulture)).Select(f => string.Join(" ", f));
            Coop.Write("claims.txt", string.Concat(kept.Select(l => l + "\n")));
        }

        public static void Shutdown()
        {
            if (Coop.IsGuest) return;
            foreach (int p in s_guests.Keys) Coop.Write("quit-" + p, "");
        }

        private static bool HasExited(Process p)
        {
            try { return p.HasExited; } catch { return true; }
        }

        // BepInEx's KeyboardShortcut reads the old Input Manager, which this game turned off.
        private static bool Pressed(KeyboardShortcut shortcut)
        {
            Keyboard kb = Keyboard.current;
            if (kb == null || shortcut.MainKey == KeyCode.None) return false;
            KeyControl(kb, shortcut.MainKey, out var main);
            if (main == null || !main.wasPressedThisFrame) return false;
            var wanted = new HashSet<KeyCode>(shortcut.Modifiers);
            foreach (KeyCode mod in new[] { KeyCode.LeftShift, KeyCode.RightShift, KeyCode.LeftControl, KeyCode.RightControl, KeyCode.LeftAlt, KeyCode.RightAlt })
            {
                KeyControl(kb, mod, out var c);
                bool down = c != null && c.isPressed;
                if (down != wanted.Contains(mod)) return false;
            }
            return true;
        }

        private static void KeyControl(Keyboard kb, KeyCode code, out UnityEngine.InputSystem.Controls.KeyControl control)
        {
            control = null;
            string name = code.ToString();
            if (name.StartsWith("Alpha", StringComparison.Ordinal)) name = "Digit" + name.Substring(5);
            else if (name.StartsWith("Keypad", StringComparison.Ordinal)) name = "Numpad" + name.Substring(6);
            else if (name == "Return") name = "Enter";
            if (Enum.TryParse(name, out Key key) && key != Key.None) control = kb[key];
        }
    }
}
