using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ModUpdater
{
    /// <summary>
    /// Starts ModUpdater.ps1 and reads back what it reports. All network and file work happens in
    /// that helper: Windows PowerShell speaks the TLS GitHub requires (this game's old Mono does
    /// not), and it outlives the game, which is the only time the mod DLLs can be replaced.
    /// </summary>
    internal sealed class Updater
    {
        internal sealed class Report
        {
            internal string State = "";
            internal string Installed = "";
            internal string Latest = "";
            internal string Error = "";
            internal string Detail = "";
        }

        private readonly string gameDir;
        private readonly string script;
        private readonly string stateFile;
        private readonly string installFlag;
        internal readonly string StagedDir;
        private Process helper;
        private DateTime readStamp;
        private Report cached = new Report();

        internal Updater(string gameDir, string script, string workDir)
        {
            this.gameDir = gameDir;
            this.script = script;
            stateFile = Path.Combine(workDir, "state.txt");
            installFlag = Path.Combine(workDir, "install.flag");
            StagedDir = Path.Combine(workDir, "staged");
            Directory.CreateDirectory(workDir);
        }

        /// <summary>True while a helper this game started is still running (checking, downloading
        /// or waiting for the game to close).</summary>
        internal bool Busy
        {
            get
            {
                try { return helper != null && !helper.HasExited; }
                catch { return false; }
            }
        }

        internal bool ScriptFound { get { return File.Exists(script); } }

        /// <summary>Starts the helper. <paramref name="installOnly"/> skips asking GitHub and just
        /// installs the package already downloaded once the game closes.</summary>
        internal void Start(bool installOnly)
        {
            if (Busy) return;
            string windows = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            string powershell = Path.Combine(windows, @"System32\WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(powershell)) powershell = "powershell.exe";
            var info = new ProcessStartInfo(powershell,
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File " + Quote(script) +
                " -GameDir " + Quote(gameDir) +
                " -WaitPid " + Process.GetCurrentProcess().Id +
                (installOnly ? " -NoCheck" : ""));
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.WorkingDirectory = gameDir;
            try
            {
                helper = Process.Start(info);
            }
            catch (Exception error)
            {
                helper = null;
                Plugin.Log.LogError("Không chạy được trình cập nhật (PowerShell): " + error.Message);
            }
        }

        /// <summary>After a reload: the new versions already run, so their files must replace the
        /// old ones when the game closes even with automatic install switched off.</summary>
        internal void RequestInstallOnExit()
        {
            File.WriteAllText(installFlag, DateTime.Now.ToString("o"));
            // A helper already waiting for the game picks the flag up; otherwise start one.
            if (!Busy) Start(true);
        }

        internal Report Read()
        {
            try
            {
                if (!File.Exists(stateFile)) return cached = new Report();
                DateTime stamp = File.GetLastWriteTimeUtc(stateFile);
                if (stamp == readStamp) return cached;
                var values = new Dictionary<string, string>();
                foreach (string line in File.ReadAllLines(stateFile, Encoding.UTF8))
                {
                    int split = line.IndexOf('=');
                    if (split > 0) values[line.Substring(0, split).Trim()] = line.Substring(split + 1).Trim();
                }
                var report = new Report();
                report.State = Value(values, "state");
                report.Installed = Value(values, "installed");
                report.Latest = Value(values, "latest");
                report.Error = Value(values, "error");
                report.Detail = Value(values, "detail");
                readStamp = stamp;
                cached = report;
            }
            catch (IOException)
            {
                // The helper is swapping the file in right now; the next poll reads it.
            }
            return cached;
        }

        private static string Value(Dictionary<string, string> values, string key)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : "";
        }

        private static string Quote(string value)
        {
            // A trailing backslash would escape the closing quote.
            return "\"" + value.TrimEnd('\\') + "\"";
        }
    }
}
