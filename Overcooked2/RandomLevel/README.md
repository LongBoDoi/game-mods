# RandomLevel

Chơi liên tiếp các màn custom của **OC2DIYLevel** mà không phải quay về menu chọn màn sau mỗi ván.

1. Vào **More Levels / OVC2DIYLevel**, chọn **Chơi ngẫu nhiên — tất cả màn** ở đầu danh sách các bộ màn.
2. Chọn ô lưu như khi chơi màn DIY thông thường.
3. Trong ván, nhấn **F8** để chuyển ngay sang màn ngẫu nhiên khác. Không hiện bảng điều khiển hay nút bỏ qua trong lúc chơi.
4. Khi màn kết thúc, chỉ hiện một dòng gợi ý **F7 — Về menu chính** ở góc trên bên phải bảng kết quả. Chọn **Continue** bằng phím hoặc tay cầm như bình thường để sang một màn ngẫu nhiên khác. Game vẫn chờ mọi người xác nhận trước khi chuyển màn.
5. **Restart** chơi lại màn hiện tại. Muốn dừng, nhấn **F7** ở bảng kết quả. Gợi ý có màu xám trong lúc chạy hoạt ảnh kết quả và sáng lên khi phím đã sẵn sàng.

**Bỏ qua màn:** chỉ dùng khi ván đang chạy trong chế độ ngẫu nhiên và có ít nhất hai màn hợp lệ. Không ghi điểm hoặc sao cho ván bị bỏ qua; điểm cao đã lưu trước đó vẫn được giữ. Chủ phòng bỏ qua sẽ chuyển cả phòng sang màn mới ngay. Màn đã bỏ qua được tính là đã gặp trong lượt xáo trộn. Mod chặn thao tác skip/restart tiếp theo trong lúc tải và ít nhất 3 giây sau yêu cầu trước.

Mod chọn đều giữa các màn hợp lệ trong tất cả bộ màn mà OC2DIYLevel đã nạp. Mỗi màn xuất hiện một lần trong một lượt xáo trộn; hết lượt sẽ xáo lại, tránh chọn ngay màn vừa chơi ở đầu lượt mới. Nếu chỉ có một màn hợp lệ, Continue sẽ chơi lại màn đó. Các mục trùng tên scene hoặc thiếu file màn sẽ bị bỏ qua.

Chọn một màn cụ thể trong menu DIY sẽ trở lại cách chơi thông thường. Chế độ ngẫu nhiên không được lưu qua lần mở game tiếp theo. Muốn chơi Restart bằng phím **F5** trong ván, có thể dùng thêm OC2Tweaks.

## Cài / gỡ

Cần Overcooked! 2 bản Steam Mono, BepInEx 5 x86 và **OC2DIYLevel 0.10.0** đã cài. Tên người chơi thường gọi là OVC2DIYLevel; tên thư mục và plugin thực tế là OC2DIYLevel. Đây là mod bổ sung, không chứa hay thay thế bộ màn của tác giả khác.

Đóng game rồi chạy từ repo:

```bat
Overcooked2\RandomLevel\build.cmd
Overcooked2\installer\Install.cmd -Only RandomLevel
```

Hoặc chọn **RandomLevel** trong menu bộ cài. DLL được cài vào `BepInEx\plugins\RandomLevel\RandomLevel.dll`.

Với gói riêng `RandomLevel-v1.1.3.zip`, giải nén rồi chạy `Install.cmd` để cài riêng RandomLevel; `Uninstall.cmd` gỡ riêng mod này. Game cần đóng khi cài hoặc gỡ.

Gỡ bằng `Overcooked2\installer\Uninstall.cmd -Only RandomLevel`. Cấu hình nằm ở `BepInEx\config\com.manhl.overcooked2.randomlevel.cfg`: `General.ExitResultsKey` mặc định **F7**, `General.SkipKey` mặc định **F8**.

Chủ phòng cần cài RandomLevel; những người tham gia cần cùng bộ màn DIY như trước. Mod dùng chế độ **More Levels**, không điều khiển chế độ Custom Arcade riêng của pack.

## Build và kiểm tra

```bat
dotnet build Overcooked2\RandomLevel\RandomLevel.csproj -c Release
dotnet run --project Overcooked2\RandomLevel.Tests -c Release
powershell -NoProfile -File Overcooked2\tests\CheckRandomLevel.ps1
```

Thêm `-p:GameDir="D:\Games\Overcooked! 2"` khi build nếu game ở nơi khác. Test tự động kiểm tra lượt xáo trộn, tránh lặp, hai lần hỏi màn kế tiếp, Continue, Restart, skip giữa ván và khóa chuyển màn khi bấm liên tiếp. Script kiểm tra DLL đối chiếu tham chiếu, điểm hook và các trường private với game và pack đã cài; các kiểm tra này không thay thế kiểm thử trong game.

Checklist trong game:

- Menu More Levels có đúng một nút ngẫu nhiên, có thể chọn bằng bàn phím và tay cầm; hủy chọn ô lưu rồi chọn màn cụ thể vẫn hoạt động bình thường.
- Chơi xong hai màn liên tiếp: Continue chuyển thẳng sang màn khác, điểm và sao vẫn lưu vào ô đã chọn.
- Gợi ý **F7 — Về menu chính** tự hiện khi bảng kết quả xuất hiện; không có khung menu, nút bấm hay thông tin bổ sung. F7 chỉ có hiệu lực sau khi hoạt ảnh kết quả hoàn tất. Vào màn mới thì gợi ý ẩn.
- Restart tại bảng kết quả và F5 trong ván giữ nguyên màn; không tiêu thụ thêm màn trong lượt ngẫu nhiên.
- Không có bảng điều khiển của RandomLevel trong lúc chơi; F8 vẫn chuyển thẳng sang màn khác, không hiện bảng kết quả hay ghi điểm cho ván dở. Thử cả khi đang tạm dừng.
- Bấm F8 nhiều lần hoặc F5 ngay sau F8 chỉ tải một lần; sau khi màn mới bắt đầu và hết thời gian chờ, có thể bỏ qua tiếp.
- F7 ở bảng kết quả trở về menu; ván chơi thông thường tiếp theo hoạt động như trước.
- Online: host bật ngẫu nhiên, client không cần RandomLevel; chờ mọi người Continue rồi cả phòng cùng vào một scene, giữ nguyên người chơi.

## Luồng chuyển màn

Mod thêm nút vào `DIYLevelEntryUI.AddUI` và mở bộ chọn ô lưu sẵn có qua `OnLevelSelected`. Sau đó một postfix của `ServerCampaignFlowController.GetNextScene` trả về scene ngẫu nhiên cùng trạng thái `LoadKitchen → RunKitchen` mà pack dùng. Game giữ trách nhiệm chờ mọi người, dừng đồng bộ, gửi tiến độ và tải scene qua mạng; hook tải scene của OC2DIYLevel cập nhật cấu hình màn cho từng máy.

OC2DIYLevel hỏi `GetNextScene` trước để kiểm tra có cần về StartScreen hay không; game hỏi thêm một lần để thực sự tải. Kết quả được giữ theo flow controller để hai lần hỏi trả cùng một màn. Khi có yêu cầu Restart, scene hiện tại luôn được ưu tiên.

Skip giữa ván dừng đồng bộ như thao tác Restart của game, rồi gọi `DIYLevelAssetBundleManager.LoadLevelServer` với scene ngẫu nhiên kế tiếp. Luồng kết thúc ván và ghi kết quả không chạy. Chỉ controller đang trong round của scene hiện tại được phép yêu cầu skip; controller cũ bị khóa cho tới khi scene được thay thế.
