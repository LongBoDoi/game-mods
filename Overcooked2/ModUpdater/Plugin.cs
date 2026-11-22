using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

[assembly: System.Reflection.AssemblyVersion("2.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("2.0.0.0")]

namespace ModUpdater
{
    /// <summary>
    /// Mod Updater: keeps this repo's Overcooked! 2 mods on the newest GitHub release, so nobody has
    /// to download and run the installer again for each one.
    ///
    /// At launch it starts ModUpdater.ps1 hidden in the background. That helper asks GitHub for the
    /// newest Overcooked2 release and downloads it if it is newer than what was installed. Then a
    /// dialog on the main menu asks what to do with it (see <see cref="UpdateDialog"/>): Update now
    /// loads the new versions straight into the running game (see <see cref="Reloader"/>) and has
    /// the helper install the files when the game closes; Later keeps the current version and asks
    /// again next launch.
    ///
    /// Its page in MOD Settings (OC2ModSettings, if installed) shows what is happening and holds
    /// the startup-check switch and the Check and Update buttons.
    /// </summary>
    [BepInPlugin(Id, "Mod Updater", Version)]
    // Loads after TiengViet when it is installed, so the language it sets applies from the start.
    [BepInDependency("com.manhl.overcooked2.tiengviet", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(SettingsBridge.Guid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInProcess("Overcooked2.exe")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "com.manhl.overcooked2.modupdater";
        public const string Version = "2.0.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> CheckOnStart;
        internal static ConfigEntry<string> Status;

        private Updater updater;
        private SettingsBridge settings;
        private UpdateDialog dialog;
        private float nextPoll;
        private string shownState;
        private string loadedVersion = "";
        private bool loadFailed;

        // MOD Settings reads these names each time it rebuilds its list, so they are kept and
        // rewritten when the language changes (see NameSettings).
        private readonly ConfigurationManagerAttributes checkNames = new ConfigurationManagerAttributes { Order = 20 };
        private readonly ConfigurationManagerAttributes statusNames =
            new ConfigurationManagerAttributes { Order = 30, ReadOnly = true, HideDefaultButton = true };
        private string namedIn;

        internal string LoadedVersion { get { return loadedVersion; } }

        private void Awake()
        {
            Log = Logger;
            NameSettings();
            CheckOnStart = Config.Bind("General", "CheckOnStart", true, new ConfigDescription(
                checkNames.Description, null, checkNames));
            Status = Config.Bind("General", "Status", "", new ConfigDescription(
                statusNames.Description, null, statusNames));
            // Status changes every few seconds while checking; only real settings belong on disk.
            // MOD Settings saves the file itself whenever a setting is changed there.
            Config.Save();
            Config.SaveOnConfigSet = false;

            string gameDir = Paths.GameRootPath.TrimEnd('\\', '/');
            string script = Path.Combine(Path.GetDirectoryName(Info.Location), "ModUpdater.ps1");
            updater = new Updater(gameDir, script, Path.Combine(Paths.CachePath, "ModUpdater"));
            dialog = new UpdateDialog(this, updater);

            Updater.Report last = updater.Read();
            if (last.State == "applied")
                Log.LogInfo("Đã cập nhật các mod lên bản " + last.Installed + ".");
            else if (last.State == "error" && last.Latest != "")
                Log.LogWarning("Lần cập nhật trước không xong: " + Describe(last));

            if (!File.Exists(script))
                Log.LogError("Thiếu ModUpdater.ps1 cạnh ModUpdater.dll; hãy cài lại bằng Install.cmd.");
            else if (CheckOnStart.Value)
                updater.Start(false);

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
                if (NameSettings() && settings != null)
                {
                    settings.Relabel();
                    settings.Refresh();
                }
                Refresh(false);
                dialog.Poll(updater.Read());
            }
            catch (Exception error) { Log.LogWarning("Không đọc được trạng thái cập nhật: " + error.Message); }
        }

        /// <summary>A newer release is downloaded and not yet running.</summary>
        internal bool CanLoadNow(Updater.Report report)
        {
            return report.State == "ready" && report.Latest != loadedVersion && Directory.Exists(updater.StagedDir);
        }

        /// <summary>The Check button: ask GitHub now and show the result in a dialog.</summary>
        internal void CheckNow()
        {
            dialog.CheckRequested();
            updater.Start(false);
        }

        /// <summary>The Update button: the same as Update now in the dialog.</summary>
        internal void UpdateFromSettings()
        {
            Reloader.Result result = LoadNow();
            if (result != null) dialog.Report(result, null);
        }

        /// <summary>Loads the downloaded versions into the running game (main menu only; the caller
        /// checks). Their files replace the old ones when the game closes.</summary>
        internal Reloader.Result LoadNow()
        {
            Updater.Report report = updater.Read();
            if (!CanLoadNow(report)) return null;
            Log.LogInfo("Nạp bản " + report.Latest + " vào game đang chạy...");
            Reloader.Result result = Reloader.Run(updater.StagedDir);
            loadedVersion = report.Latest;
            loadFailed = result.Failed.Count > 0;
            updater.RequestInstallOnExit();
            settings.Refresh();
            Refresh(true);
            return result;
        }

        private string Describe(Updater.Report report)
        {
            if (report.State == "ready" && report.Latest == loadedVersion)
            {
                if (Lang.Vi)
                    return loadFailed
                        ? "Đã nạp bản " + loadedVersion + ", trừ vài mod; chúng được cài khi tắt game"
                        : "Đang chạy bản " + loadedVersion + "; tệp được thay khi tắt game";
                return loadFailed
                    ? "Loaded version " + loadedVersion + ", except a few mods; they install when you close the game"
                    : "Running version " + loadedVersion + "; files are replaced when you close the game";
            }
            return Texts.Describe(report);
        }

        /// <summary>Puts the settings' names and descriptions in the language in use. True when
        /// they changed since last time.</summary>
        private bool NameSettings()
        {
            string language = Lang.Vi ? "vi" : "en";
            if (language == namedIn) return false;
            namedIn = language;
            checkNames.DispName = Lang.T("Check when the game starts", "Kiểm tra khi mở game");
            checkNames.Description = Lang.T(
                "Ask GitHub for a new version each time the game starts. When there is one, a dialog on the main menu asks whether to update now.",
                "Hỏi GitHub xem có bản mới mỗi lần mở game. Nếu có, một hộp thoại ở menu chính sẽ hỏi bạn có muốn cập nhật ngay không.");
            statusNames.DispName = Lang.T("Status", "Trạng thái");
            statusNames.Description = Lang.T("Update status. View only.", "Tình trạng cập nhật. Chỉ để xem.");
            return true;
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
        public string Description;
        public int? Order;
        public bool? ReadOnly;
        public bool? HideDefaultButton;
    }
}
