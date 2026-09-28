# RXCapture — chụp màn hình & chỉnh sửa ảnh

Ứng dụng Windows tự viết hoàn toàn bằng C# / WinForms (.NET Framework 4.8, có sẵn trong Windows 10/11).
Không cần license, không cần cài thêm gì.

## Chạy
- Chạy `bin\RXCapture.exe` (phải giữ `RXCapture.exe.config` cùng thư mục).
- Nhấn **Print Screen** để chụp (kiểu All-in-One). Ứng dụng nằm ở khay hệ thống.

## Build lại
```
powershell -ExecutionPolicy Bypass -File build.ps1
```
Dùng `csc.exe` có sẵn của Windows (C# 5) — không cần Visual Studio hay .NET SDK.

## Phím tắt toàn cục (đổi được trong Settings > Hotkeys)
| Phím | Chức năng |
|---|---|
| Print Screen | All-in-One (chọn cửa sổ / đối tượng / kéo vùng) |
| Ctrl+Shift+R | Vùng chọn |
| Ctrl+Shift+W | Cửa sổ |
| Ctrl+Shift+F | Toàn màn hình |
| Ctrl+Shift+S | Chụp cuộn trang (Scrolling) |
| Ctrl+Shift+D | Vẽ tay (Freehand) |
| Ctrl+Shift+L | Lặp lại vùng vừa chụp |
| Ctrl+Shift+V | Quay video (bấm lại để dừng) |

Nếu Windows đã giữ Print Screen cho Snipping Tool, hãy tắt tùy chọn đó trong *Settings > Accessibility > Keyboard* hoặc đổi phím trong RXCapture.

## Cửa sổ Capture
- 4 tab: **All-in-One**, **Image** (Selection: Region / Window / Full Screen / Scrolling / Freehand / Fixed / Repeat), **Video** (Region / Window / Full Screen, định dạng, FPS), **Presets**.
- **Share** (Editor / Clipboard / File / Editor + Clipboard), **Effects**, **Timer**, **Include cursor**, nút **Capture** đỏ.
- **Presets**: lưu cấu hình hiện tại (loại chụp + đầu ra + hiệu ứng + hẹn giờ), đổi tên, xóa, gán **phím tắt riêng** cho từng preset; bấm đúp hoặc nút Capture để chạy. Có sẵn 5 preset mẫu.
- **Chụp cửa sổ**: chụp đủ cửa sổ dù bị cửa sổ khác che (PrintWindow), tùy chọn bóng đổ quanh cửa sổ và bo góc trong suốt (Windows 11); quay video theo cửa sổ hoặc toàn màn hình.

## Thanh Capture (widget treo mép trên màn hình)
- Một thanh mỏng (dẹt, dài bằng lúc mở rộng) luôn nằm ở mép trên màn hình; **trỏ chuột vào hoặc bấm** để mở rộng (có hiệu ứng trượt), rời chuột đi thì tự thu gọn lại.
- Nút đỏ lớn = Capture theo cấu hình đang chọn (khi đang quay video thì thành nút dừng); nút trái mở Editor; nút bánh răng mở menu (cửa sổ Capture, Library, Settings, Tự thu gọn, Ẩn thanh).
- Thanh xanh: bật/tắt danh sách cấu hình. Mũi tên ◄ ► hoặc lăn chuột để đổi trang: Image / Scrolling / Video / My Presets. Bấm một dòng để chọn, bấm đúp để chụp ngay. **Manage Profiles** mở tab Presets.
- Kéo tab dọc mép trên để đổi vị trí (kể cả sang màn hình khác). Thanh không bao giờ xuất hiện trong ảnh chụp/video. Bật/tắt ở tray hoặc Settings > General.

## Thông số mặc định của công cụ
Mỗi công cụ vẽ (Arrow, Line, Shape, Callout, Text, Step, Pen, Highlighter, Magnify) có bộ thông số riêng mà đối tượng mới bắt đầu với: màu viền/nền, độ dày, kiểu nét, đầu mũi tên, độ trong suốt, đổ bóng, phông chữ, cỡ chữ, màu chữ, đậm/nghiêng/gạch chân, căn lề. Cách đặt: **Settings > Editor > Default tool properties…** (hoặc nút **Defaults…** ở Tools > Properties) để sửa từng công cụ có xem trước và đặt lại; hoặc chỉnh một đối tượng rồi bấm **Set default** để lưu kiểu của nó làm mặc định. Giá trị được lưu và giữ sau khi khởi động lại; đối tượng đã vẽ không bị đổi.

## Menu File của Editor
Bấm tab **File** trong Editor: **New Image** (canvas trống, chọn cỡ + nền/trong suốt, Ctrl+N), **New Capture**, **New from Clipboard**, **Open**, **Save** (Ctrl+S), **Save As ▸** (ảnh / PDF / dự án .scp), **Convert Images** (đổi hàng loạt PNG/JPG/BMP/GIF/TIFF/PDF, kéo thả tệp), **Print ▸**, **Help ▸**, danh sách **Recent Files**, **Editor Options** và **Exit Editor**. Không có các mục chia sẻ đám mây (My Places, Google Drive, thiết bị di động, đăng nhập).

## Tính năng
**Chụp**: All-in-One (đưa chuột vào cửa sổ/đối tượng để chọn, lăn chuột để đổi cha/con, kính lúp + mã màu, phím `C` chép mã màu), Region, Window, Full Screen (màn hình hiện tại hoặc tất cả), Freehand, Fixed region, Scrolling window, Repeat last region, hẹn giờ, có/không con trỏ chuột, hỗ trợ nhiều màn hình và DPI khác nhau, hiệu ứng tự áp dụng (viền, đổ bóng, mép rách).

**Editor** (ribbon tối: File / Tools / Image / Share / Library): Select, Arrow, Line, Shape, Callout, Text (sửa trực tiếp), Step (tự đánh số), Stamp (10 mẫu + ảnh riêng), Pen, Highlighter, Fill, Blur/Pixelate, Magnify, Spotlight, Eraser, Crop, Cut Out. Kiểu (Styles) có sẵn, Outline / Fill / Effects, phông chữ, căn lề, đổ bóng, độ trong suốt, sắp xếp lớp, sao chép/dán đối tượng, Undo/Redo (40 bước), zoom, kéo cạnh ảnh để đổi kích thước canvas.
Menu **Image**: đổi cỡ ảnh, canvas, xoay/lật, trim, viền, đổ bóng, mép rách, bo góc, phản chiếu, chỉnh màu, đen trắng, đảo màu, sepia, blur, sharpen, emboss, hình mờ (watermark).

**Xuất**: PNG / JPG / BMP / GIF / TIFF / PDF, sao chép clipboard, in, email (Simple MAPI), mở bằng Paint, mở thư mục. Mọi ảnh chụp tự lưu vào **Library** (khay thumbnail dưới editor) ở dạng có thể sửa tiếp (`.scp`).

**Video**: quay vùng màn hình, mặc định ra **MP4 (H.264)** (cài đặt cũ đang để AVI được chuyển sang MP4 một lần khi mở bản mới); có thể chọn AVI (Motion-JPEG) hoặc GIF động. MP4 dùng bộ mã hóa có sẵn của Windows (Media Foundation), không cần cài thêm; `ffmpeg.exe` cạnh `RXCapture.exe` chỉ là dự phòng. Thanh điều khiển khi quay có nút **Record / Pause / Resume**, **Stop & save**, **Discard** kèm tên.

**Khay Library**: rê chuột vào một ảnh/video (hoặc chọn nó) để hiện nút **X** ở góc trên phải — bấm để xóa khỏi thư viện (có hỏi xác nhận). **Chọn nhiều để xóa cùng lúc** (như Explorer): **kéo chuột bôi đen** một khung để chọn các mục nó chạm tới (kéo sát mép trên/dưới thì khay tự cuộn), **Ctrl+bấm** để thêm/bớt từng mục, **Shift+bấm** để chọn cả khoảng từ mục trước, bấm vào chỗ trống để bỏ chọn. Có thể chuột phải một mục > **Select multiple** để hiện ô tích. Khi đang chọn: bấm một mục = chỉ chọn mục đó (bấm đúp để mở), chuột phải > **Select all / Deselect all / Delete selected (N)** / Exit selection mode; phím **Delete** xóa, **Esc** thoát, **Ctrl+A** chọn tất cả.

**Xem, cắt và lưu video trong Editor**: bấm một video trong khay Library để phát ngay trong Editor (Play/Pause, kéo thanh tua, phím Space). Để cắt: đoạn được giữ là **khoảng xanh** trên thanh tua — kéo hai tay nắm ở hai đầu khoảng xanh để chỉnh (khung hình tại tay nắm hiện ngay để canh), hoặc tua tới điểm rồi bấm **Set start** / **Set end**; xong bấm **Trim video** — đoạn đã cắt được lưu thành video MP4 mới trong Library, video gốc giữ nguyên. GIF chỉ xem, không cắt. **Save / Save As** (Ctrl+S, Ctrl+Shift+S, tab File) lưu video đang xem ra tệp bạn chọn, giữ nguyên định dạng (MP4/AVI/GIF).

Giao diện tiếng Anh hoặc tiếng Việt (Settings > General > Language).

## Dữ liệu
- Cài đặt: `%APPDATA%\RXCapture\settings.xml`
- Thư viện: `%LOCALAPPDATA%\RXCapture\Library`
- Thư mục lưu mặc định: `Pictures\RXCapture`

## Kiểm thử tự động
```
RXCapture.exe --selftest log.txt      # hiệu ứng, tài liệu, xuất file, ghép ảnh cuộn, AVI/GIF/MP4, cắt video, biểu tượng, thư viện
RXCapture.exe --interact log.txt      # mô phỏng chuột lên canvas cho mọi công cụ
RXCapture.exe --overlaytest log.txt   # lớp phủ chọn vùng trên nhiều màn hình
RXCapture.exe --videotest log.txt     # quay AVI/GIF/MP4 qua giao diện thật, phát trong Editor, cắt video
RXCapture.exe --grab x,y,w,h out.png  # chụp một vùng, không giao diện
```

## Chưa có
Ghi âm (micro/hệ thống) khi quay video, OCR "Grab Text", chia sẻ FTP/đám mây, hiệu ứng phối cảnh, chụp menu nhiều vùng (Multi-area). Chụp menu/tooltip dùng hẹn giờ (Delay) rồi chọn vùng.
