using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Bootstrap;
using UnityEngine;

namespace ModUpdater
{
    /// <summary>
    /// Asks the player, in the game's own dialog box, what to do with a new version once it is
    /// downloaded, the way Arcade MOD (HostTools) asks about its updates:
    ///
    ///   Mod update - version 4.3.0 is out (you have 4.2.0), with the mods it changes or adds
    ///   [Update now]   load it into the game now; its files replace the old ones when the game closes
    ///   [View details] open the release page, then ask again
    ///   [Later]        keep the current version; asked again the next time the game starts
    ///
    /// After Update now a second dialog says what happened, and offers to close the game when a
    /// mod only starts on the next launch. The Check button in MOD Settings reports its result the
    /// same way: a spinner while checking, then the offer, "up to date" or what went wrong.
    ///
    /// Dialogs only open on the main menu outside an online room (where loading the new versions
    /// is safe) and when no other dialog is up; until then they wait.
    /// </summary>
    internal sealed class UpdateDialog
    {
        private const string ReleasePage = "https://github.com/LongBoDoi/game-mods/releases/tag/Overcooked2-v";

        /// <summary>Companions that only install when the third-party mod they extend is there.</summary>
        private static readonly Dictionary<string, string> Needs = new Dictionary<string, string>
        {
            { "DIYChefFix", "dev.gua.overcooked.diychef" },
            { "RandomLevel", "dev.gua.overcooked.diylevel" },
        };

        private readonly Plugin plugin;
        private readonly Updater updater;

        private string answered = "";     // the version the player answered this session
        private bool reopen;              // View details: ask again when the browser is back
        private bool manual;              // the Check button was pressed: report its result
        private bool manualSawWork;
        private float manualUntil;
        private T17DialogBox shown;
        private T17DialogBox spinner;
        private string pendingTitle, pendingMessage;
        private bool pendingOfferQuit;
        private string changesFor;
        private List<Reloader.Change> cachedChanges;

        internal UpdateDialog(Plugin plugin, Updater updater)
        {
            this.plugin = plugin;
            this.updater = updater;
        }

        /// <summary>The Check button: show how the check goes, whatever it finds.</summary>
        internal void CheckRequested()
        {
            manual = true;
            manualSawWork = false;
            manualUntil = Time.realtimeSinceStartup + 120f;
            answered = "";   // a version put off with Later is offered again
        }

        /// <summary>Called about once a second.</summary>
        internal void Poll(Updater.Report report)
        {
            bool working = report.State == "checking" || report.State == "downloading";
            if (manual)
            {
                if (Time.realtimeSinceStartup > manualUntil) manual = false;
                if (working)
                {
                    // The spinner is a dialog too, so nothing else opens while it is up.
                    manualSawWork = true;
                    if (spinner == null && CanShow()) ShowSpinner();
                    return;
                }
                if (spinner != null) { Close(spinner); spinner = null; return; }
            }
            if (shown != null && !shown.IsActive) shown = null;
            if (shown != null || !CanShow()) return;

            if (pendingMessage != null)
            {
                string title = pendingTitle, message = pendingMessage;
                pendingTitle = pendingMessage = null;
                ShowNote(title, message, pendingOfferQuit);
                return;
            }
            if (plugin.CanLoadNow(report) && (report.Latest != answered || reopen))
            {
                manual = false;
                Offer(report);
                return;
            }
            if (!manual || !manualSawWork) return;

            manual = false;
            if (report.State == "uptodate")
                ShowNote(Lang.T("Up to date", "Đã là bản mới nhất"), Texts.Describe(report) + ".", false);
            else if (report.State == "error")
                ShowNote(Lang.T("Update check failed", "Kiểm tra cập nhật thất bại"), Texts.Describe(report) + ".", false);
        }

        private static bool CanShow()
        {
            try
            {
                return Reloader.SafeNow() && T17DialogBoxManager.Instance != null && !T17DialogBoxManager.HasAnyOpenDialogs();
            }
            catch (Exception) { return false; }
        }

        private void Offer(Updater.Report report)
        {
            string latest = report.Latest;
            // Read once per version: the dialog may wait a while for a player to take control.
            if (changesFor != latest)
            {
                cachedChanges = Reloader.Changes(updater.StagedDir, RemovedMods(), Needs);
                changesFor = latest;
            }
            List<Reloader.Change> changes = cachedChanges;
            var text = new System.Text.StringBuilder();
            bool known = report.Installed != "" && report.Installed != "unknown";
            text.Append(known
                ? Lang.T("Version " + latest + " of the mods is out. You have " + report.Installed + ".",
                    "Đã có bản " + latest + " của các mod. Bạn đang dùng bản " + report.Installed + ".")
                : Lang.T("Version " + latest + " of the mods is out.", "Đã có bản " + latest + " của các mod."));
            if (changes.Count > 0)
            {
                text.Append("\n");
                foreach (Reloader.Change change in changes)
                {
                    text.Append("\n- ").Append(change.Name).Append(": ");
                    if (change.OldVersion == null) text.Append(Lang.T("new", "mới")).Append(" (").Append(change.NewVersion).Append(")");
                    else if (change.OldVersion == change.NewVersion) text.Append(change.NewVersion).Append(Lang.T(", fixes", ", bản sửa"));
                    else text.Append(change.OldVersion).Append(" → ").Append(change.NewVersion);
                }
            }
            text.Append("\n\n").Append(Lang.T(
                "Update now loads it into the game straight away. Later keeps the version you have; you'll be asked again next time.",
                "Cập nhật ngay sẽ nạp bản mới vào game luôn. Để sau thì giữ bản đang dùng; lần mở game sau sẽ hỏi lại."));

            T17DialogBox dialog = Open(Lang.T("Mod update", "Cập nhật mod"), text.ToString(),
                Lang.T("Update now", "Cập nhật ngay"), Lang.T("View details", "Xem chi tiết"), Lang.T("Later", "Để sau"));
            if (dialog == null) return;
            answered = latest;
            reopen = false;
            dialog.OnConfirm = delegate { Update(changes); };
            dialog.OnDecline = delegate
            {
                reopen = true;
                try { Application.OpenURL(ReleasePage + latest); }
                catch (Exception error) { Plugin.Log.LogWarning("Không mở được trang phát hành: " + error.Message); }
            };
            dialog.OnCancel = delegate { Plugin.Log.LogInfo("Để sau: giữ bản đang dùng, chưa cập nhật lên " + latest + "."); };
            dialog.Show();
        }

        private void Update(List<Reloader.Change> changes)
        {
            Reloader.Result result = plugin.LoadNow();
            if (result == null) return;
            var added = new List<string>();
            foreach (Reloader.Change change in changes)
                if (change.OldVersion == null) added.Add(change.Name);
            Report(result, added);
        }

        /// <summary>Says what an update did. Also used by the Update button in MOD Settings.</summary>
        internal void Report(Reloader.Result result, List<string> added)
        {
            if (added == null)
            {
                added = new List<string>();
                foreach (Reloader.Change change in Reloader.Changes(updater.StagedDir, RemovedMods(), Needs))
                    if (change.OldVersion == null) added.Add(change.Name);
            }
            string version = plugin.LoadedVersion;
            var text = new System.Text.StringBuilder(Lang.T(
                "The mods now run version " + version + ". Their files are replaced when you close the game.",
                "Các mod đang chạy bản " + version + ". Tệp của chúng được thay khi bạn tắt game."));
            if (added.Count > 0)
                text.Append("\n\n").Append(Lang.T("New in this version, starting the next time you open the game: ",
                    "Mod mới trong bản này, chạy từ lần mở game sau: ")).Append(string.Join(", ", added.ToArray())).Append(".");
            if (result.Failed.Count > 0)
                text.Append("\n\n").Append(Lang.T("Couldn't load these now; they update when you restart the game: ",
                    "Không nạp được ngay các mod này; chúng được cập nhật khi bạn mở lại game: ")).Append(string.Join(", ", result.Failed.ToArray())).Append(".");
            pendingTitle = Lang.T("Mods updated", "Đã cập nhật mod");
            pendingMessage = text.ToString();
            pendingOfferQuit = added.Count > 0 || result.Failed.Count > 0;
        }

        private void ShowNote(string title, string message, bool offerQuit)
        {
            T17DialogBox dialog = offerQuit
                ? Open(title, message, "OK", Lang.T("Close game", "Đóng game"), "")
                : Open(title, message, "OK", "", "");
            if (dialog == null) return;
            if (offerQuit) dialog.OnDecline = delegate { Application.Quit(); };
            dialog.Show();
        }

        private void ShowSpinner()
        {
            T17DialogBox dialog = T17DialogBoxManager.GetDialog(false);
            if (dialog == null) return;
            dialog.Initialize(Lang.T("Checking for updates", "Đang kiểm tra cập nhật"),
                Lang.T("Asking GitHub for a new version...", "Đang hỏi GitHub xem có bản mới không..."),
                "", "", "", T17DialogBox.Symbols.Spinner, false, false);
            dialog.Show();
            spinner = dialog;
        }

        private static void Close(T17DialogBox dialog)
        {
            try { if (dialog.IsActive) dialog.Hide(); }
            catch (Exception) { }
        }

        /// <summary>A dialog with our own words: a quoted tag is shown as it is.</summary>
        private T17DialogBox Open(string title, string message, string confirm, string decline, string cancel)
        {
            T17DialogBox dialog = T17DialogBoxManager.GetDialog(false);
            if (dialog == null) return null;
            dialog.Initialize(title, message, Quote(confirm), Quote(decline), Quote(cancel),
                T17DialogBox.Symbols.Unassigned, false, false);
            shown = dialog;
            return dialog;
        }

        private static string Quote(string text)
        {
            return string.IsNullOrEmpty(text) ? "" : "\"" + text + "\"";
        }

        /// <summary>Mods the player uninstalled; the installer keeps them out of updates.</summary>
        private static ICollection<string> RemovedMods()
        {
            var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = Path.Combine(Paths.ConfigPath, "com.manhl.overcooked2.removed.txt");
                if (File.Exists(path))
                    foreach (string line in File.ReadAllLines(path))
                        if (line.Trim() != "") removed.Add(line.Trim());
            }
            catch (Exception) { }
            return removed;
        }
    }
}
