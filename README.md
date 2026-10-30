# Crush ghé chơi

App Windows nhỏ nằm ở khay hệ thống. Thỉnh thoảng crush ghé lên màn hình, chào, hun gió thả tim rồi tự đi. Crush không giành bàn phím, và chuột bấm xuyên qua được.

Người dùng tự thêm hình crush của họ: hình đã tách nền (PNG trong suốt) dùng luôn; ai tự tải thêm bộ tách nền thì app tách nền ảnh thường ngay trên máy. App không gửi ảnh đi đâu và không tự tải gì về. Bản phát hành không chứa ảnh người thật nào; chưa thêm hình thì crush là một trái tim hoạt hình.

**Tải app:** vào [Releases](https://github.com/vienglacial/crush-ghe-choi/releases), tải `CrushGheChoi-<phiên bản>-win-x64.zip`, giải nén rồi mở `CrushGheChoi.exe`. Hướng dẫn chi tiết: [packaging/HUONG-DAN.txt](packaging/HUONG-DAN.txt).

## Chạy app

Bản portable đã kèm runtime nên không cần cài .NET:

```
dist\CrushGheChoi\CrushGheChoi.exe
```

Giữ thư mục `Assets` cạnh file exe. Lần đầu chạy sẽ hiện màn giới thiệu. Sau đó app nằm ở khay hệ thống (biểu tượng trái tim):

- **Nhấp đúp** vào trái tim: crush ghé ngay.
- **Chuột phải**: ghé ngay, chọn màn, tạm nghỉ, *Yên lặng 2 giờ*, tần suất ghé, vị trí xuất hiện, thêm hình crush, cài đặt, thoát.

Tham số dòng lệnh:

| Tham số | Tác dụng |
| --- | --- |
| `--tray` | Chạy thẳng xuống khay, không hiện thông báo (dùng khi chạy cùng Windows). |
| `--visit` | Gọi crush ghé ngay. Nếu app đang chạy thì nhờ phiên đó gọi. |
| `--exit` | Thoát phiên đang chạy, ví dụ trước khi chép đè bản mới. |
| `--add-photos [ảnh…]` | Mở cửa sổ Thêm hình, kèm các hình cần thêm. Truyền thẳng đường dẫn ảnh (không cờ) cũng được. |

Mở exe khi app đang chạy sẽ mở cửa sổ Cài đặt của phiên đó. App không bao giờ chạy hai phiên cùng lúc, trừ khi đặt biến môi trường `CRUSHGHECHOI_PROFILE` (xem bên dưới).

## Tính năng

- **Thêm hình crush:** chọn hoặc kéo thả hình. Hình đã tách nền (PNG trong suốt) dùng luôn; app tìm gương mặt bằng YuNet (đi kèm, 230 KB) rồi đề xuất đường cắt nửa người và chỗ tim bay ra. Người dùng kéo đường cắt, đặt lại chỗ tim, chọn hướng tim, tẩy chỗ dính nền, hoàn tác (Ctrl+Z) trước khi lưu. Sửa hoặc xóa hình trong *Cài đặt → Crush*.
- **Tự tách nền (tùy chọn):** ảnh thường (JPG…) cần bộ tách nền BiRefNet-portrait. App không tự tải: bảng "Ảnh này còn nền" có nút mở trang tải bằng trình duyệt và nút chọn tệp đã tải; app kiểm tra SHA-256 rồi chép vào thư mục models.
- **Ghé ngẫu nhiên theo khoảng bạn chọn:** ví dụ 5–15 phút, 45–90 phút, hoặc tự nhập (tối thiểu 1 phút, tối đa 10 giờ). Có nút chọn nhanh trong Cài đặt và trong menu khay → *Tần suất ghé*.
- **Vị trí:** ngẫu nhiên khắp màn hình (mặc định), hoặc cố định ở góc trái/phải dưới. Có thể kéo tới chỗ mình thích. Chọn được màn hình, kể cả *ngẫu nhiên giữa các màn hình*.
- **Nhiều hình:** mỗi lần ghé bốc một hình, dùng hết lượt mới lặp lại, không hiện lại ngay hình vừa hiện.
- **Ba màn biểu diễn:** chào, hun gió, chào rồi hun (≈ 9 giây). Có bong bóng chữ. Tim bay ra từ cạnh môi hoặc bàn tay, lệch ra xa gương mặt.
- **Câu chào:** sửa được, mỗi dòng một câu.
- **Tạm nghỉ và giờ yên lặng:** tạm nghỉ 30 phút / 1 giờ / 2 giờ / đến mai. Giờ yên lặng mặc định 22:30–08:30, tính được khoảng vắt qua nửa đêm.
- **Không làm phiền:** hoãn khi có app toàn màn hình (trình chiếu, game, video). Dừng khi khóa máy hoặc máy ngủ. Mở lại thì tính lịch mới, không phát bù.
- **Ngoại lệ toàn màn hình:** Premiere Pro, After Effects, Photoshop, Illustrator, DaVinci Resolve và CapCut hay phủ kín màn hình nên Windows coi là toàn màn hình. Các app này vẫn được ghé. Sửa danh sách trong *Cài đặt → Màn hình*.
- **Tùy chọn khác:** chuyển động nhẹ, màu tim (hồng đỏ / tím / pastel), số tim, tiếng chuông nhẹ (mặc định tắt), chạy cùng Windows, sao lưu/khôi phục cấu hình.

## Dữ liệu trên máy

| Gì | Ở đâu |
| --- | --- |
| Cấu hình | `%APPDATA%\CrushGheChoi\settings.json` |
| Hình crush của người dùng | `%APPDATA%\CrushGheChoi\crush\` (`stills\*.png`, `manifest.json`, bản nháp để chỉnh lại trong `drafts\`) |
| Bộ tách nền (nếu người dùng cài) | `%LOCALAPPDATA%\CrushGheChoi\models\` |
| Nhật ký | `%LOCALAPPDATA%\CrushGheChoi\logs\app.log` |
| Chạy cùng Windows | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, giá trị `CrushGheChoi` |

- Nếu tệp cấu hình hỏng, app giữ lại một bản `settings.broken-*.json` rồi chạy tiếp với cài đặt mặc định.
- Đặt `CRUSHGHECHOI_PROFILE=ten` (chữ, số, gạch) thì app chạy một phiên riêng với dữ liệu ở `CrushGheChoi-ten`, song song được với phiên chính. Dùng để thử nghiệm mà không đụng dữ liệu thật. Model tách nền dùng chung.
- Mạng: app không dùng mạng. Nút "Mở trang tải" chỉ mở trình duyệt tới tệp model trên bản phát hành của dự án rembg (GitHub).

## Bộ hình

Hình người dùng thêm được lưu thành một bộ hình thường. Một bộ hình là một thư mục có `manifest.json`:

```json
{
  "schema": 1,
  "name": "Crush của tôi",
  "version": "2",
  "still": "stills/a.png",
  "fps": 24,
  "stills": [
    { "file": "stills/a.png", "kissPoint": { "x": 0.205, "y": 0.29 }, "drift": -1 },
    { "file": "stills/b.png", "kissPoint": { "x": 0.36,  "y": 0.44 }, "drift": 1 }
  ],
  "clips": {
    "idle":        { "frames": "idle", "loop": true },
    "wave":        { "frames": "wave" },
    "kissPrepare": { "frames": "kiss_prepare" },
    "kiss":        { "frames": "kiss", "events": { "kiss_release": 12 } },
    "goodbye":     { "frames": "goodbye" }
  }
}
```

- Mọi hình là **PNG nền trong suốt**.
- `kissPoint` là chỗ tim bay ra, tính theo tỷ lệ 0..1 từ góc trên trái của hình.
- `drift` là hướng tim bay lệch: -1 sang trái, 1 sang phải.
- `clips` không bắt buộc. Mỗi clip là một thư mục chuỗi PNG (đánh số `001.png`, `002.png`…), cùng kích thước với `still`. Bộ có clip thì luôn dùng ảnh chính `still` để khớp với clip.
- `kiss_release` là số thứ tự khung lúc bàn tay rời môi; tim bay ra đúng lúc đó.
- Động tác nào thiếu hoặc lỗi thì app bỏ qua và tự tạo chuyển động từ ảnh đứng. Bộ hình hỏng hẳn thì app quay về hình mẫu.

Thứ tự ưu tiên: thư mục chọn trong *Cài đặt → Crush → Nâng cao*, rồi tới bộ "Crush của tôi", cuối cùng là hình mẫu đi kèm app (`Assets\Crush\default`, tạo bằng [tools/make_app_assets.py](tools/make_app_assets.py)).

### Tách nền hàng loạt bằng script

[tools/prepare_asset.py](tools/prepare_asset.py) cùng thuật toán với app, chạy theo một tệp cấu hình liệt kê từng ảnh (đường cắt `bottom`, điểm tim `kissAt` theo pixel ảnh gốc, vùng xóa `erase`…, xem đầu tệp script). Cấu hình và ảnh cá nhân để trong `personal\` (không đưa lên git):

```
pip install onnxruntime pillow numpy
python -P tools/prepare_asset.py personal/crush-set.json ^
    --model tools/models/BiRefNet-portrait-epoch_150.onnx ^
    --out personal/cutouts --preview preview.jpg
```

- Model BiRefNet-portrait (khoảng 1 GB): <https://github.com/danielgatis/rembg/releases/download/v0.0.0/BiRefNet-portrait-epoch_150.onnx>, để vào `tools/models/`. Bản nhẹ (BiRefNet-general lite, 224 MB) và ISNet đã thử: hay dính người hoặc đồ vật phía sau.
- Mở `preview.jpg` để kiểm tra: mỗi hình có lưới 10% và một chấm đỏ ở điểm tim. Thêm `--only <số>` để dựng lại một ảnh.

## Build từ mã nguồn

Cần .NET 10 SDK. Trên máy này SDK cài riêng ở `%LOCALAPPDATA%\Microsoft\dotnet`, nên đặt `DOTNET_ROOT` trỏ vào đó.

```
dotnet build CrushGheChoi.slnx
dotnet test tests/CrushGheChoi.Tests/CrushGheChoi.Tests.csproj
```

### Đóng gói bản phát hành

```
powershell -ExecutionPolicy Bypass -File packaging\build-public.ps1
```

Script dọn sạch `bin`, `obj`, `dist` (build không tự xóa tệp thừa, nên ảnh cũ có thể còn sót), chạy kiểm thử, đóng gói bản portable một tệp exe kèm runtime, kiểm tra trong gói **chỉ có đúng các tệp cho phép** (không ảnh cá nhân, không model tách nền), rồi nén thành `dist\CrushGheChoi-<phiên bản>-win-x64.zip`.

Không bật `EnableCompressionInSingleFile`. Bản nén nhỏ hơn trên đĩa nhưng tốn RAM gấp nhiều lần: đo được khoảng 250 MB, so với khoảng 50 MB khi không nén.

Cấu trúc:

| Thư mục | Nội dung |
| --- | --- |
| `src/CrushGheChoi.Core` | Logic thuần, có kiểm thử: lịch ghé, giờ yên lặng, cài đặt, đọc bộ hình, kịch bản màn biểu diễn, mô phỏng tim, bố cục/vị trí, tách nền (xử lý mask, dò mặt, đề xuất cắt, dựng hình, lưu bộ hình người dùng). |
| `src/CrushGheChoi` | App WPF: cửa sổ crush trong suốt, khay hệ thống, Cài đặt, màn giới thiệu, cửa sổ Thêm hình, chạy model bằng ONNX Runtime, tải model, theo dõi khóa máy/ngủ/toàn màn hình, nhiều màn hình. |
| `tests/CrushGheChoi.Tests` | Kiểm thử tự động (xUnit). |
| `tools` | Script tách nền hàng loạt, tạo icon, tiếng chuông và hình mẫu. |
| `packaging` | Script đóng gói bản phát hành và hướng dẫn cho người dùng. |
| `personal` | Ảnh và cấu hình cá nhân. Không đưa lên git, không vào bản phát hành. |

## Giới hạn đã biết

- **Tự tách nền chạy bằng CPU:** khoảng 45–90 giây mỗi ảnh và cần khoảng 6 GB RAM trống. App cảnh báo khi RAM trống ít. GPU 6 GB (RTX 2060) không đủ bộ nhớ cho model này nên không dùng GPU.
- **Trang tải bộ tách nền** là bản phát hành của rembg trên GitHub. Nếu đường dẫn đó mất, cần tìm đúng tệp `BiRefNet-portrait-epoch_150.onnx` ở nơi khác (app kiểm tra mã SHA-256 nên tệp sai sẽ bị từ chối).
- **Ảnh HEIC** cần cài *HEIF Image Extensions* từ Microsoft Store.
- **Chưa có động tác thật.** Bộ hình chỉ có ảnh đứng. Phần nhô lên, nghiêng, "đẩy" lúc hun, tim và bong bóng do app tạo. Muốn có vẫy tay và hun gió thật thì cần chuỗi PNG hoặc clip quay thật.
- **Ảnh selfie bị khung cắt** ở tay, vai hoặc máy ảnh: được làm mờ dần ở cạnh đó thay vì để một đường thẳng.
- **App chưa được ký số:** Windows SmartScreen sẽ cảnh báo khi mở lần đầu.
- **Đã thử trên máy thật:** 3 màn hình đều ở tỷ lệ 100%. Đổi giữa các màn có tỷ lệ khác nhau (ví dụ 100% và 150%) đã có xử lý nhưng chưa thử thực tế.
- **Chưa thử trên máy:** kéo chuột thật ở chế độ chỉnh vị trí, chạy cùng Windows. Phần tính toán liên quan có kiểm thử tự động.
- **Chế độ tập trung:** app chưa nhận biết Focus Assist / Do Not Disturb của Windows. Khi họp hoặc thu màn hình, dùng *Yên lặng 2 giờ*.
- **Emoji** trong bong bóng chữ hiện đơn sắc, do WPF chưa hỗ trợ emoji màu.
