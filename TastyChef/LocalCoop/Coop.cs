using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace LocalCoop
{
    // Who this copy of the game is, and the folder all copies of one session share.
    //
    // Player 1 is whatever copy was started normally. Guests are started by Player 1 with
    //   -tccoop-player <n> -tccoop-host <Player 1's process id>
    // and the session folder is %TEMP%\TastyChefLocalCoop\<Player 1's process id>\, holding:
    //   layout.txt   "<player> <x> <y> <w> <h>" per live player (written by Player 1)
    //   claims.txt   "<player> <utc ticks>" per controller a guest took (written by guests,
    //                pruned by Player 1 when a guest leaves)
    //   room.txt     Player 1's LAN room code, empty when there is none
    //   quit-<n>     asks guest n to close
    internal static class Coop
    {
        public const string PlayerArg = "-tccoop-player";
        public const string HostArg = "-tccoop-host";
        public const int MaxPlayers = 4;   // the game's rooms hold four

        public static int Player { get; private set; } = 1;
        public static int HostPid { get; private set; }
        public static bool IsGuest => Player > 1;
        public static string Dir { get; private set; }

        /// <summary>Guests: always. Player 1: while at least one guest window is open.</summary>
        public static bool Active => IsGuest || HostLauncher.HasGuests;

        public static void Init(string[] args)
        {
            HostPid = Process.GetCurrentProcess().Id;
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == PlayerArg && int.TryParse(args[i + 1], out int p) && p >= 2 && p <= MaxPlayers) Player = p;
                else if (args[i] == HostArg && int.TryParse(args[i + 1], out int h) && h > 0) HostPid = h;
            }
            if (!IsGuest) HostPid = Process.GetCurrentProcess().Id;
            Dir = Path.Combine(Root, HostPid.ToString(CultureInfo.InvariantCulture));
        }

        public static string Root => Path.Combine(Path.GetTempPath(), "TastyChefLocalCoop");

        public static string FilePath(string name) => Path.Combine(Dir, name);

        public static string Read(string name)
        {
            string path = FilePath(name);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try { return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null; }
                catch (IOException) { Thread.Sleep(5); }
                catch (UnauthorizedAccessException) { Thread.Sleep(5); }
            }
            return null;
        }

        /// <summary>Writes through a temp file so readers never see half a file.</summary>
        public static void Write(string name, string text)
        {
            Directory.CreateDirectory(Dir);
            string path = FilePath(name);
            string temp = path + "." + Process.GetCurrentProcess().Id + ".tmp";
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    File.WriteAllText(temp, text, new UTF8Encoding(false));
                    if (File.Exists(path)) File.Replace(temp, path, null);
                    else File.Move(temp, path);
                    return;
                }
                catch (IOException) { Thread.Sleep(10); }
                catch (UnauthorizedAccessException) { Thread.Sleep(10); }
            }
            Plugin.Log.LogWarning("Could not write " + path);
        }

        /// <summary>Appends one line; claims from several guests can arrive at once.</summary>
        public static void AppendLine(string name, string line)
        {
            Directory.CreateDirectory(Dir);
            string path = FilePath(name);
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None))
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
                        fs.Write(bytes, 0, bytes.Length);
                    }
                    return;
                }
                catch (IOException) { Thread.Sleep(10); }
            }
            Plugin.Log.LogWarning("Could not append to " + path);
        }

        public static void Delete(string name)
        {
            try { File.Delete(FilePath(name)); } catch { }
        }

        public static IEnumerable<string[]> Lines(string text)
        {
            if (string.IsNullOrEmpty(text)) yield break;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length > 0) yield return line.Split(' ');
            }
        }

        public static bool IsAlive(int pid)
        {
            try
            {
                using (var p = Process.GetProcessById(pid)) return !p.HasExited;
            }
            catch { return false; }
        }
    }
}
