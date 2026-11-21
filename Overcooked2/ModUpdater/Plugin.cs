using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

namespace ModUpdater
{
    /// <summary>
    /// Mod Updater: keeps this repo's Overcooked! 2 mods on the newest GitHub release, so nobody has
    /// to download and run the installer again for each one.
    ///
    /// At launch it starts ModUpdater.ps1 hidden in the background. That helper asks GitHub for the
    /// newest Overcooked2 release and downloads it if it is newer than what was installed. The new
    /// versions can then be loaded straight into the running game from the main menu (the Update
    /// button, or by themselves with UpdateImmediately on; see Reloader). Either way, once the
    /// game closes the helper runs that release's own installer with -Update, so the files on disk
    /// match and the next launch loads the new versions normally.
    ///
    /// Its page in MOD Settings (OC2ModSettings, if installed) shows what is happening and holds
    /// the switches and the Check and Update buttons.
    /// </summary>
    [BepInPlugin(Id, "Mod Updater", Version)]
    [BepInDependency(SettingsBridge.Guid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInProcess("Overcooked2.exe")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "com.manhl.overcooked2.modupdater";
        public const string Version = "1.0.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> AutoUpdate;
        internal static ConfigEntry<bool> UpdateImmediately;
        internal static ConfigEntry<string> Status;

        private Updater updater;
        private SettingsBridge settings;
        private float nextPoll;
        private string shownState;
        private string loadedVersion = "";
        private string attemptedVersion = "";
        private bool loadFailed;

        private void Awake()
        {
            Log = Logger;
            AutoUpdate = Config.Bind("General", "AutoUpdate", true, new ConfigDescription(
                "Tự tải bản mới của các mod khi mở game và cài nó khi bạn tắt game; lần mở sau là bản mới.",
                null, new ConfigurationManagerAttributes { DispName = "Tự động cập nhật", Order = 20 }));
            UpdateImmediately = Config.Bind("General", "UpdateImmediately", false, new ConfigDescription(
                "Tải xong là nạp bản mới vào game ngay khi bạn ở menu chính (không ở trong phòng trực tuyến), không cần tắt game.",
                null, new ConfigurationManagerAttributes { DispName = "Cập nhật ngay khi tải xong", Order = 10 }));
            Status = Config.Bind("General", "Status", "", new ConfigDescription(
                "Tình trạng cập nhật. Chỉ để xem.",
                null, new ConfigurationManagerAttributes { DispName = "Trạng thái", Order = 30, ReadOnly = true, HideDefaultButton = true }));
            // Status changes every few seconds while checking; only real settings belong on disk.
            // MOD Settings saves the file itself whenever a setting is changed there.
            Config.Save();
            Config.SaveOnConfigSet = false;

            string gameDir = Paths.GameRootPath.TrimEnd('\\', '/');
            string script = Path.Combine(Path.GetDirectoryName(Info.Location), "ModUpdater.ps1");
            updater = new Updater(gameDir, script, Path.Combine(Paths.CachePath, "ModUpdater"));

            Updater.Report last = updater.Read();
            if (last.State == "applied")
                Log.LogInfo("Đã cập nhật các mod lên bản " + last.Installed + ".");
            else if (last.State == "error" && last.Latest != "")
                Log.LogWarning("Lần cập nhật trước không xong: " + Describe(last));

            if (!File.Exists(script))
                Log.LogError("Thiếu ModUpdater.ps1 cạnh ModUpdater.dll; hãy cài lại bằng Install.cmd.");
            else if (AutoUpdate.Value || UpdateImmediately.Value)
                updater.Start(false);
            // Switched on mid-game: check now rather than at the next launch.
            EventHandler switchedOn = delegate
            {
                if ((AutoUpdate.Value || UpdateImmediately.Value) && !updater.Busy && File.Exists(script)) updater.Start(false);
            };
            AutoUpdate.SettingChanged += switchedOn;
            UpdateImmediately.SettingChanged += switchedOn;

            settings = new SettingsBridge(this, updater);
            settings.Connect();
            Refresh(true);
        }

        private void Update()
        {
            if (Time.unscaledTime < nextPoll) return;
            nextPoll = Time.unscaledTime + 1f;
            try
            {
                Refresh(false);
                Updater.Report report = updater.Read();
                // Once per downloaded version, so a mod that will not reload is not retried every second.
                if (UpdateImmediately.Value && CanLoadNow(report) && attemptedVersion != report.Latest && Reloader.SafeNow())
                    LoadNow();
            }
            catch (Exception error) { Log.LogWarning("Không đọc được trạng thái cập nhật: " + error.Message); }
        }

        /// <summary>A newer release is downloaded and not yet running.</summary>
        internal bool CanLoadNow(Updater.Report report)
        {
            return report.State == "ready" && report.Latest != loadedVersion && Directory.Exists(updater.StagedDir);
        }

        /// <summary>Loads the downloaded versions into the running game (main menu only; the caller
        /// checks). Their files replace the old ones when the game closes.</summary>
        internal void LoadNow()
        {
            Updater.Report report = updater.Read();
            if (!CanLoadNow(report)) return;
            attemptedVersion = report.Latest;
            Log.LogInfo("Nạp bản " + report.Latest + " vào game đang chạy...");
            Reloader.Result result = Reloader.Run(updater.StagedDir);
            loadedVersion = report.Latest;
            loadFailed = result.Failed.Count > 0;
            updater.RequestInstallOnExit();
            settings.Refresh();
            Refresh(true);
        }

        private string Describe(Updater.Report report)
        {
            if (report.State == "ready" && report.Latest == loadedVersion)
                return loadFailed
                    ? "Đã nạp bản " + loadedVersion + ", trừ vài mod; chúng được cài khi tắt game"
                    : "Đang chạy bản " + loadedVersion + "; tệp được thay khi tắt game";
            return Texts.Describe(report, AutoUpdate.Value);
        }

        private void Refresh(bool force)
        {
            Updater.Report report = updater.Read();
            string text = Describe(report);
            if (!force && text == Status.Value && report.State == shownState) return;
            if (report.State != shownState && report.State != "")
                Log.LogInfo("Cập nhật: " + text);
            shownState = report.State;
            Status.Value = text;
        }

        private void OnDestroy()
        {
            if (settings != null) settings.Dispose();
        }
    }

    /// <summary>Read by MOD Settings (and BepInEx ConfigurationManager) by its type name.</summary>
    internal sealed class ConfigurationManagerAttributes
    {
        public string DispName;
        public int? Order;
        public bool? ReadOnly;
        public bool? HideDefaultButton;
    }
}
