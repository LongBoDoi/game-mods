# Bếp Tiếp Sức — Chợ Đêm / Night Market Relay

Bản **2.0.0** của màn riêng cho **OC2DIYLevel 0.10.0**, trong **GameMods Originals**.
Màn dài **6 phút**, hỗ trợ **1–4 người**, với **9 công thức thuộc 3 nhóm món**.

## Thiết kế gameplay

Hai quầy của một khu chợ chia sẻ cùng danh sách đơn. Mỗi bờ có ba bàn sơ chế,
thiết bị nấu, cửa giao món, đĩa, bồn rửa, thùng rác và bình chữa cháy.

| Khu | Nguyên liệu | Thiết bị nấu | Công việc chính |
|---|---|---|---|
| Tây, nền gạch sáng | Bột, cà chua, cá, rong biển, rau | 2 lò, 1 chảo | Sơ chế rau/cá/bột, nướng pizza, chiên burger, rửa và giao món |
| Đông, nền đá xanh đậm | Phô mai, xúc xích, thịt, bánh, gạo, dưa leo | 2 nồi, 1 chảo | Sơ chế thịt/phô mai/xúc xích/dưa, nấu cơm, chiên burger, rửa và giao món |

Các món đều cần phối hợp hai phía:

- **Burger**: bò; bò phô mai; bò rau cà chua; bò rau phô mai. Thịt và bánh ở phía đông,
  rau/cà chua ở phía tây. Mỗi bên đều có thể chiên, ráp và giao.
- **Pizza**: margherita và pepperoni. Bột/cà chua ở tây; phô mai/xúc xích ở đông;
  hai lò đặt ở hai đầu khu tây. Có mặt bàn trống quanh đảo sơ chế để ráp pizza.
- **Sushi**: cá, dưa leo, và cá + dưa leo. Cá/rong biển ở tây, cơm/dưa leo ở đông;
  phải chuẩn bị phần cơm và phần rau/cá song song.

Hai băng chuyền bốn ô chạy ngược chiều ở giữa. Làn trên chuyển tây → đông, làn
trong chuyển đông → tây; hai đầu có bàn nhận. Tốc độ 0,65 ô/giây để có thời gian
lấy đồ, đồng thời phải giữ đầu nhận thông thoáng. Có thể ném nguyên liệu qua khoảng
cách giữa hai bờ, hoặc tự mang đi qua hai lối rộng hai ô ở trên và dưới. Đĩa và dụng
cụ cần giữ luồng vận chuyển riêng, tránh chặn nguyên liệu trên băng chuyền.

Hai đảo sơ chế dạng chữ L tạo lối đi vòng và nhiều mặt bàn, thay cho hai khoảng
phòng trống của bản 1.0. Tổng sáu đĩa sạch được chia mỗi bờ ba chiếc. Mỗi cửa giao
món liên kết với điểm trả đĩa của chính bờ đó, nên hai phía đều phải rửa đĩa.

![Sơ đồ bố cục](build/manhl_relay_kitchen/layout.png)

## Hình ảnh và sửa lỗi bản 1.0

Tường trang trí, tường va chạm, mặt nền và thiết bị được dựng trên cùng hệ tọa độ.
Trạm sát biên có khoảng hở 1,2 đơn vị từ tâm đến tường; kiểm tra cả chiều rộng hai
ô của bồn rửa và cửa giao món sau khi xoay. Không giữ tường cũ của scene mẫu.

Nền dùng texture gạch men, đá xanh và gỗ lấy trực tiếp từ Overcooked! 2, với mật độ
lát khớp kích thước ô. Hai bờ có vật liệu khác nhau và dải gỗ làm dấu các lối giao
nhau. Trang trí gồm cửa sổ, đèn tường, biển sushi, chậu tre, cây và ghế ở bên ngoài.
Ánh sáng tây ấm, đông lạnh nhẹ, cùng nhạc The Neon City và âm thanh khu sushi.

## Trạng thái kiểm tra

Kiểm tra tự động xác nhận đường đi liên thông, điểm sinh người, nguyên liệu cần
phối hợp hai phía, vị trí thiết bị trong tường, liên kết trả đĩa, chiều và tốc độ
băng chuyền, dữ liệu texture, tham chiếu script và tài nguyên trong game.
`build-report.json` ghi số liệu và checksum cụ thể.

**Chưa hoàn tất chơi thử trong game.** Trong session dựng bản 2.0, lệnh khởi động
Overcooked2.exe từ công cụ thoát ngay với mã 53; đang chờ mở game thủ công để chụp
scene và đo các đối tượng sau khi nạp. Điểm sao dưới đây là mục tiêu thiết kế,
chưa phải ngưỡng được cân bằng bằng một ván chơi thực tế.

| Số người | 1 sao | 2 sao | 3 sao | 4 sao |
|---|---:|---:|---:|---:|
| 1 | 360 | 650 | 950 | 1400 |
| 2 | 700 | 1200 | 1750 | 2450 |
| 3 | 950 | 1650 | 2350 | 3200 |
| 4 | 1200 | 2050 | 2850 | 3850 |

## Cài và chơi

Đóng game và chạy `Install.cmd` trong thư mục này. Có thể chỉ định vị trí game:

```powershell
.\Install.cmd -GameDir "E:\Steam\steamapps\common\Overcooked! 2"
```

Vào **More Levels → GameMods Originals → Bếp Tiếp Sức — Chợ Đêm / Night Market Relay**.
RandomLevel tự nhận màn khi bắt đầu lượt ngẫu nhiên mới. Mọi máy chơi cùng phải có
cùng bản màn và pack. Bộ cài chỉ chép thư mục `levels/manhl_relay_kitchen`, kiểm tra
checksum và tài nguyên tương thích; không sửa save hoặc màn khác.

Bộ cài OC2DIYLevel chung thay thế pack bằng bản nguồn. Vì vậy thư mục màn này cũng
cần có trong `Overcooked2/installer/plugins/OC2DIYLevel/levels` để lần cài lại giữ
được màn. Muốn gỡ, đóng game rồi xóa riêng thư mục `levels/manhl_relay_kitchen`.

## Dựng lại

`layout.json` chứa tọa độ thiết bị, thực đơn và cấu hình theo số người.
`scene_v2.py` dựng scene từ các prefab stub đúng định dạng của pack.
`build_level.py` tạo metadata, ảnh sơ đồ và thực hiện kiểm tra bản dựng.

Cần Python 3.12, UnityPy trong `requirements.txt`, pack OC2DIYLevel và game cài cục bộ:

```powershell
python -m pip install -r requirements.txt
python build_level.py --source "..\installer\plugins\OC2DIYLevel" --game-dir "E:\Steam\steamapps\common\Overcooked! 2"
python -m unittest -v test_level
```

`build.cmd` dùng Python cục bộ trong repo nếu có. Đầu ra mặc định ở
`build/manhl_relay_kitchen`; `--output <folder>` chọn nơi khác ngoài pack nguồn.
Script hỗ trợ khung 16 × 13 ô; các prefab và kiểm tra vật lý được viết cho khung này.
Không thay đổi đầu vào. Bản dựng nhúng texture nền cần thiết, không sửa bundle gốc
của game và không yêu cầu thêm plugin gameplay.

## Nguồn thành phần

Ý tưởng, bố cục, cấu hình, ánh sáng và công cụ dựng màn được tạo trong repo này.
Khung kỹ thuật lấy từ scene `Dmm/s_dmm_1` của **呆喵喵 (Dmm)**; toàn bộ khu bàn,
thiết bị và trang trí nền cũ được thay thế. Prefab stub lấy từ OC2DIYLevel `common01`;
đồ họa, âm thanh và cơ chế nấu là tài nguyên Overcooked! 2 được game nạp khi chơi.
Các gói đầu vào bên thứ ba không được đưa vào mã nguồn repo.

Tham khảo [tài liệu OC2DIYLevel](https://github.com/gua248/Overcooked2-LevelEditor/blob/main/Docs/en/tutorial.md)
và [UnityPy](https://github.com/K0lb3/UnityPy).