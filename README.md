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

## Đóng gói file cài đặt (Setup)
```
powershell -ExecutionPolicy Bypass -File build-setup.ps1 [-Version 1.0.1]
```
Tạo `dist\RXCapture-Setup-<phiên bản>.exe` (~0,6 MB, tự chứa toàn bộ ứng dụng; chỉ cần `csc.exe` có sẵn của Windows). Bấm đúp file này để cài: chọn thư mục (mặc định `%LOCALAPPDATA%\Programs\RXCapture`), tùy chọn biểu tượng ở màn hình nền, chạy cùng Windows và chạy ngay sau khi cài. Cài cho tài khoản hiện tại, **không cần quyền quản trị**; có mục trong *Settings > Apps* để gỡ, và `Uninstall.exe` trong thư mục cài (hỏi có xóa luôn cài đặt/thư viện hay không). Dòng lệnh: `/S` (im lặng), `/D=<thư mục>`, `/nodesktop`, `/startup` (chạy cùng Windows), `/launch`; gỡ im lặng: `Uninstall.exe /uninstall /S [/removedata]`. Cài đè lên bản cũ sẽ tự tắt RXCapture đang chạy từ thư mục đó và giữ nguyên dữ liệu.

**Lưu từng phiên bản trên git:** mỗi lần `build-setup.ps1` chạy, bản dựng còn được chép vào `releases<phiên bản>` (file cài đặt, bản portable `.zip` chỉ gồm `RXCapture.exe` + `.config`, `.sha256`, chứng chỉ công khai `.cer`, `BUILD.txt` ghi commit và ngày build). Thư mục này **được commit lên git** để giữ lịch sử và dùng lại bản cũ. Một thư mục phiên bản đã có sẽ không bị ghi đè: muốn build bản mới hãy tăng `-Version` (ví dụ `-Version 1.0.2`); `-Force` mới cho ghi đè, `-NoRelease` để build mà không lưu.

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
- **Quay video có tiếng**: nút **MIC** (micro) và **SPEAKER** (âm thanh phát ra từ máy tính - loa) trên thanh quay video, có thể trộn cả hai. Bật/tắt trước khi bấm Record; khi đang quay, bấm để **tắt tiếng / bật lại** (nguồn nào không bật lúc bắt đầu thì không thêm được giữa chừng). Tiếng đi theo đồng hồ quay nên khớp hình, tạm dừng không ghi tiếng. Lưu ở MP4 (AAC), Editor phát được tiếng và cắt video vẫn giữ tiếng.
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

**Chụp cuộn (Scrolling)**: nhấn `Ctrl+Shift+S` (hoặc chọn vùng rồi bấm nút **Scroll**) → kéo chọn vùng nội dung cần chụp → con trỏ đổi thành **mũi tên chỉ xuống** → **bấm chuột vào nội dung trang** (chỗ bấm là nơi app gửi lệnh lăn chuột, nên phải là phần cuộn được, không phải thanh công cụ/cột bên) → RXCapture tự cuộn dần xuống tới hết trang rồi ghép thành một ảnh dài. Nhấn **Esc** bất cứ lúc nào để dừng và giữ phần đã chụp; chuột phải ở bước chọn điểm để quay lại chỉnh vùng. Cột bên đổi khi cuộn (minimap, mục lục, quảng cáo) và thanh cố định không làm hỏng việc ghép ảnh.

**Editor** (ribbon tối: File / Tools / Image / Share / Library): Select, Arrow, Line, Shape, Callout, Text (sửa trực tiếp), Step (tự đánh số), Stamp (10 mẫu + ảnh riêng), Pen, Highlighter, Fill, Blur/Pixelate, Magnify, Spotlight, Eraser, Crop, Cut Out. Kiểu (Styles) có sẵn, Outline / Fill / Effects, phông chữ, căn lề, đổ bóng, độ trong suốt, sắp xếp lớp, sao chép/dán đối tượng, Undo/Redo (40 bước), zoom, kéo cạnh ảnh để đổi kích thước canvas.
Menu **Image**: đổi cỡ ảnh, canvas, xoay/lật, trim, viền, đổ bóng, mép rách, bo góc, phản chiếu, chỉnh màu, đen trắng, đảo màu, sepia, blur, sharpen, emboss, hình mờ (watermark).

**Xuất**: PNG / JPG / BMP / GIF / TIFF / PDF, sao chép clipboard, in, email (Simple MAPI), mở bằng Paint, mở thư mục. Mọi ảnh chụp tự lưu vào **Library** (khay thumbnail dưới editor) ở dạng có thể sửa tiếp (`.scp`).

**File đã lưu là file chính**: sau khi **Save As** (ảnh hoặc video), file đó được gắn với mục trong thư viện và trở thành file chính khi chỉnh sửa: tiêu đề Editor hiện tên file, **Save / Ctrl+S ghi đè đúng file đó** (không hỏi tên, không tạo file mới), Save As lần sau mở sẵn đúng thư mục/tên/định dạng đó. Liên kết được nhớ lâu dài (kể cả sau khi mở lại từ thư viện hoặc khởi động lại). Mở lại một file ảnh mà RXCapture đã lưu và chưa bị sửa ở nơi khác sẽ tiếp tục đúng mục cũ (còn nguyên các đối tượng vẽ); file bị chương trình khác sửa thì mở như ảnh mới.

**Video**: quay vùng màn hình, mặc định ra **MP4 (H.264)** (cài đặt cũ đang để AVI được chuyển sang MP4 một lần khi mở bản mới); có thể chọn AVI (Motion-JPEG) hoặc GIF động. MP4 dùng bộ mã hóa có sẵn của Windows (Media Foundation), không cần cài thêm; `ffmpeg.exe` cạnh `RXCapture.exe` chỉ là dự phòng. Thanh điều khiển khi quay có nút **Record / Pause / Resume**, **Stop & save**, **Discard** kèm tên.

**Khay Library**: rê chuột vào một ảnh/video (hoặc chọn nó) để hiện nút **X** ở góc trên phải — bấm để **Đóng**: mục biến khỏi danh sách nhưng **không bị xóa** (file vẫn nằm trong thư viện, sửa dở đã được lưu; chuột phải > *Restore closed items* để mở lại tất cả, hoặc mở lại file đã lưu). **Xóa thật** nằm riêng trong chuột phải > *Delete*: luôn hỏi xác nhận (mặc định là "No"), rồi chuyển file vào **Thùng rác Windows** nên khôi phục được (khôi phục vào thư mục thư viện là mục hiện lại). Tự dọn khi thư viện quá 300 mục chỉ xóa mục cũ *chưa lưu ra file*, không bao giờ xóa mục đã lưu. **Chọn nhiều để đóng/xóa cùng lúc** (như Explorer): **kéo chuột bôi đen** một khung để chọn các mục nó chạm tới (kéo sát mép trên/dưới thì khay tự cuộn), **Ctrl+bấm** để thêm/bớt từng mục, **Shift+bấm** để chọn cả khoảng từ mục trước, bấm vào chỗ trống để bỏ chọn. Có thể chuột phải một mục > **Select multiple** để hiện ô tích. Khi đang chọn: bấm một mục = chỉ chọn mục đó (bấm đúp để mở), chuột phải > **Select all / Deselect all / Close selected (N) / Delete selected (N)** / Exit selection mode; phím **Delete** xóa (có hỏi), **Esc** thoát, **Ctrl+A** chọn tất cả.

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

## Lịch sử phiên bản
Mỗi lần đóng gói lại file cài đặt, thêm một mục ở đầu danh sách này (số phiên bản lấy từ `build-setup.ps1 -Version`).

### 1.0.2 (chưa đóng gói)
**Mới**
- Quay video có tiếng: nút **MIC** và **SPEAKER** trên thanh quay video (bật trước khi quay; khi đang quay bấm để tắt/bật tiếng). Trộn micro + âm thanh máy tính, lưu MP4 (AAC); Settings > Video có hai ô tick mặc định.
- Editor phát được tiếng của video, tua/dừng khớp hình; cắt video giữ nguyên phần tiếng tương ứng.
- Mỗi lần đóng gói, bản dựng được lưu vào `releases\<phiên bản>\` và commit lên git.
- Thanh quay video thu nhỏ (790 × 56), nút bo góc, chấm trạng thái nhấp nháy khi quay, thanh mức âm trên nút Mic/Speaker.

**Sửa lỗi**
- Quay video xong, Editor mở nhưng không chuyển sang video vừa quay: nay tự mở và phát đúng video mới (danh sách bên dưới cũng chọn video đó).
- Đóng cửa sổ Editor (chỉ ẩn xuống khay) khi đang phát video thì hình và tiếng vẫn chạy phía sau: nay tự tạm dừng khi cửa sổ bị ẩn.

### 1.0.1
**Sửa lỗi**
- Hộp thoại Watermark (và mọi hộp thoại có thanh trượt): thanh trượt tự cao ~45 px nên đè lên hàng bên dưới, làm ô *Position* bị cắt phần trên. Đã cố định chiều cao thanh trượt.
- Các ô chọn dạng danh sách trong hộp thoại tối bị Windows vẽ khung xanh nhạt, nền sáng, nút mũi tên trắng: đã vẽ lại theo giao diện tối.
- Chạy ẩn dưới khay (`--minimized`, do mục chạy cùng Windows) bị lưu vào cài đặt, khiến những lần mở tay sau đó không hiện cửa sổ chính. Giờ chỉ áp dụng cho lần chạy đó, và nếu cả biểu tượng khay lẫn widget đều tắt thì vẫn hiện cửa sổ.
- Mục chạy cùng Windows trỏ tới đường dẫn cũ sau khi di chuyển hoặc cài lại app: giờ tự trỏ lại nếu file exe cũ không còn. Việc chuyển mục cũ từ tên ShotCraft sang RXCapture trước đây không chạy (đường dẫn registry mất dấu `\`), nay đã chạy.
- Bấm X trên danh sách ảnh/video từng xóa hẳn file, dễ mất ảnh quan trọng: giờ chỉ **Close** (ẩn khỏi danh sách); *Delete* hỏi trước (mặc định "No") và đưa vào Thùng rác; ảnh đã lưu ra file không bị tự dọn khi thư viện đầy.
- Mỗi lần Save tạo một file mới: giờ file đã lưu là file chính của mục đó, Save ghi đè lên nó, tiêu đề và danh sách hiện tên file.

**Mới**
- Chạy cùng Windows: ô tick trong Settings, mục *Start with Windows* ở menu khay, ô tick trong trình cài đặt (cờ `/startup`).
- Menu khay gọn hơn; thanh công cụ hẹp hơn, icon nét mảnh; thanh quay video chữ in hoa, cỡ lớn hơn.
- File cài đặt và `RXCapture.exe` được ký số (`tools\sign.ps1`, mặc định chứng chỉ tự cấp; dùng `-Thumbprint` cho chứng chỉ mua). Chứng chỉ tự cấp không hết cảnh báo SmartScreen/diệt virus trên máy chưa tin cậy nó.

### 1.0.0
Bản cài đặt đầu tiên. Các lỗi đã sửa trước khi phát hành:
- Video bị méo hình khi chiều rộng không chia hết cho 16 (sai bước dòng khi đọc khung hình).
- Chụp cuộn trang lỗi khi một phần trang không cuộn cùng nội dung (thanh bên đổi, phần đầu cố định); làm lại cách dùng: chọn vùng → *Scroll* → click vào nội dung, app tự cuộn đến hết trang, *Esc* để dừng.
- Không phát được video trong Editor và mặc định lưu AVI: nay phát trực tiếp, cắt đoạn bằng thanh xanh, Save/Save As, mặc định MP4 không cần ffmpeg.
- Thanh quay video chỉ có icon (khó hiểu), bị che khi quay toàn màn hình, hiện trên thanh tác vụ.
- Danh sách ảnh/video chưa có nút X, chưa chọn nhiều được (kéo chọn, Ctrl, Shift).

## Chưa có
OCR "Grab Text", chia sẻ FTP/đám mây, hiệu ứng phối cảnh, chụp menu nhiều vùng (Multi-area). Chụp menu/tooltip dùng hẹn giờ (Delay) rồi chọn vùng.
