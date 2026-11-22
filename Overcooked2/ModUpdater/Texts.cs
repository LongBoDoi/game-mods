namespace ModUpdater
{
    /// <summary>The helper reports codes; these are the words shown for them.</summary>
    internal static class Texts
    {
        internal static string Describe(Updater.Report report, bool automatic)
        {
            string latest = report.Latest;
            bool known = report.Installed != "" && report.Installed != "unknown";
            bool vi = Lang.Vi;
            switch (report.State)
            {
                case "checking": return vi ? "Đang kiểm tra bản mới..." : "Checking for a new version...";
                case "downloading": return vi ? "Đang tải bản " + latest + "..." : "Downloading version " + latest + "...";
                case "uptodate":
                    return (vi ? "Đã là bản mới nhất (" : "Already up to date (") + (known ? report.Installed : latest) + ")";
                case "ready":
                    if (automatic && known)
                        return vi ? "Đã tải bản " + latest + ": bấm Cập nhật để dùng ngay, hoặc tự cài khi tắt game"
                            : "Version " + latest + " downloaded: press Update to use it now, or it installs when you close the game";
                    return vi ? "Đã tải bản " + latest + ": bấm Cập nhật để dùng ngay"
                        : "Version " + latest + " downloaded: press Update to use it now";
                case "applying": return vi ? "Đang cài bản " + latest + "..." : "Installing version " + latest + "...";
                case "applied": return vi ? "Đã cập nhật lên bản " + report.Installed : "Updated to version " + report.Installed;
                case "error": return Error(report);
                default:
                    return automatic ? Lang.T("Not checked yet", "Chưa kiểm tra")
                        : Lang.T("Automatic updates are off", "Tự động cập nhật đang tắt");
            }
        }

        private static string Error(Updater.Report report)
        {
            string latest = report.Latest;
            bool vi = Lang.Vi;
            switch (report.Error)
            {
                case "offline":
                    return Lang.T("Couldn't reach GitHub, will try again next time the game starts",
                        "Không kết nối được GitHub, sẽ thử lại lần mở game sau");
                case "norelease": return Lang.T("No release published yet", "Chưa có bản phát hành nào");
                case "download":
                    return vi ? "Tải bản " + latest + " không được, sẽ thử lại"
                        : "Couldn't download version " + latest + ", will try again";
                case "running":
                    return vi ? "Chưa cài được bản " + latest + ": game vẫn đang chạy"
                        : "Couldn't install version " + latest + " yet: the game is still running";
                case "denied":
                    return vi ? "Chưa cài được bản " + latest + ": không ghi được vào thư mục game"
                        : "Couldn't install version " + latest + " yet: can't write to the game folder";
                case "install":
                    return vi ? "Cài bản " + latest + " không được (xem BepInEx\\cache\\ModUpdater\\update.log)"
                        : "Installing version " + latest + " failed (see BepInEx\\cache\\ModUpdater\\update.log)";
                default:
                    return Lang.T("The updater ran into an error (see BepInEx\\cache\\ModUpdater\\update.log)",
                        "Trình cập nhật gặp lỗi (xem BepInEx\\cache\\ModUpdater\\update.log)");
            }
        }
    }
}
