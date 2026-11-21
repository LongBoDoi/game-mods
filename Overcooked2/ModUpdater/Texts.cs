namespace ModUpdater
{
    /// <summary>The helper reports codes; these are the words shown for them.</summary>
    internal static class Texts
    {
        internal static string Describe(Updater.Report report, bool automatic)
        {
            string latest = report.Latest;
            bool known = report.Installed != "" && report.Installed != "unknown";
            switch (report.State)
            {
                case "checking": return "Đang kiểm tra bản mới...";
                case "downloading": return "Đang tải bản " + latest + "...";
                case "uptodate": return "Đã là bản mới nhất (" + (known ? report.Installed : latest) + ")";
                case "ready":
                    return automatic && known
                        ? "Đã tải bản " + latest + ": bấm Cập nhật để dùng ngay, hoặc tự cài khi tắt game"
                        : "Đã tải bản " + latest + ": bấm Cập nhật để dùng ngay";
                case "applying": return "Đang cài bản " + latest + "...";
                case "applied": return "Đã cập nhật lên bản " + report.Installed;
                case "error": return Error(report);
                default: return automatic ? "Chưa kiểm tra" : "Tự động cập nhật đang tắt";
            }
        }

        private static string Error(Updater.Report report)
        {
            string latest = report.Latest;
            switch (report.Error)
            {
                case "offline": return "Không kết nối được GitHub, sẽ thử lại lần mở game sau";
                case "norelease": return "Chưa có bản phát hành nào";
                case "download": return "Tải bản " + latest + " không được, sẽ thử lại";
                case "running": return "Chưa cài được bản " + latest + ": game vẫn đang chạy";
                case "denied": return "Chưa cài được bản " + latest + ": không ghi được vào thư mục game";
                case "install": return "Cài bản " + latest + " không được (xem BepInEx\\cache\\ModUpdater\\update.log)";
                default: return "Trình cập nhật gặp lỗi (xem BepInEx\\cache\\ModUpdater\\update.log)";
            }
        }
    }
}
