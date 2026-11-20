# Hướng dẫn sử dụng PersonalLauncher

## 1. Thêm game vào thư viện

1. Mở launcher, nhấn biểu tượng **Toàn bộ game** ở góc dưới cùng sidebar trái (hoặc nhấn `+ Thêm game` khi thư viện còn trống).
2. Nhấn **+ Thêm game** ở góc trên phải:
   - **Tab Thủ công:** Điền tên game (hỗ trợ tiếng Việt/Nhật/Trung), chọn tệp `.exe`, tùy chọn chọn icon sidebar, wallpaper nền, logo tên game PNG trong suốt, thư mục làm việc và đối số dòng lệnh.
   - **Tab Tự động theo thư mục:** Chọn thư mục gốc cần quét, nhấn **Quét thư mục**. Trình quét chỉ đọc tệp (không chạy thử EXE), tự bỏ chọn tệp phụ trợ (installer/updater/redist), đặt tên mặc định theo **thư mục trực tiếp chứa EXE** và cho phép chỉnh sửa tên hoặc chọn đúng EXE trước khi thêm hàng loạt. Mọi game nhập tự động dùng icon và wallpaper mặc định.

## 2. Chuyển game, khởi chạy và chỉnh sửa

- Chọn icon game ở sidebar trái hoặc nhấp vào thẻ trong **Toàn bộ game** để đổi wallpaper nền.
- Trong **Toàn bộ game**, nhấp đúp hoặc nhấn `Enter` trên thẻ để mở trang game (không tự chạy game).
- Nhấn `Ctrl+F` để tìm nhanh theo tên (hỗ trợ tiếng Việt không dấu kể cả `đ` → `d`, tiếng Nhật và tiếng Trung).
- Nhấn **Bắt đầu game** để khởi chạy trực tiếp EXE của game.
- Ba nút tròn bên cạnh nút Bắt đầu (hoặc menu **Chỉnh sửa game** khi cửa sổ hẹp):
  - **Thư mục:** Đổi đường dẫn EXE/thư mục làm việc/đối số hoặc mở thư mục hiện tại (không di chuyển hay xóa tệp game thật).
  - **Logo:** Đổi icon sidebar và logo tên game PNG trong suốt.
  - **Wallpaper:** Chọn ảnh tĩnh (`PNG/JPG`) hoặc **Live Wallpaper video động** (`MP4/WEBM/MOV/WMV/M4V/AVI/MKV`) chạy lặp mượt toàn màn hình (tự tạm dừng khi thu nhỏ/xuống tray để tiết kiệm GPU/CPU). Riêng thẻ game trong **Toàn bộ game / Secret** luôn giữ dạng **ảnh bìa tĩnh** (tự trích khung hình tĩnh từ video hoặc cho bạn chọn riêng ảnh bìa tĩnh PNG/JPG).
  - **Xóa khỏi thư viện:** Chỉ xóa bản ghi và ảnh do launcher quản lý; tệp game trên ổ đĩa luôn được giữ nguyên.

## 2.1. Cập nhật màu nút (Buttons) tùy chỉnh

- Để đổi nhanh mà không cần build lại: ghi đè trực tiếp các tệp PNG (`buttons_play.png`, `buttons_play_blank.png`, `buttons_menu.png`, `buttons_setting.png`, `Somthing_Button.png`) trong thư mục `publish\win-x64\Assets\buttons\` rồi khởi động lại launcher.
- Để đóng gói cố định vào bản build: sửa các tệp trong `src\PersonalLauncher.App\Assets\buttons\` rồi chạy `.\packaging\publish.ps1`.

## 3. Danh mục Secret và mã PIN

- Lần đầu mở **Toàn bộ game → Secret** hoặc **Cài đặt → Secret**, ứng dụng yêu cầu tạo mã PIN đúng 4 chữ số `0–9` (hỗ trợ số `0` ở đầu, ví dụ `0042`).
- Những lần sau:
  - Mở **Toàn bộ game → Secret**: nhập đủ 4 chữ số (hoặc dán 4 số), danh mục tự mở khi khớp.
  - Mở **Cài đặt → Secret**: luôn yêu cầu xác thực lại mã PIN quản lý để ẩn/bỏ ẩn game, thêm trực tiếp game vào Secret, đổi PIN hoặc cấu hình cảnh báo email.
- Danh mục Secret tự động khóa khi bạn rời khỏi vùng Secret, thu nhỏ cửa sổ, thu vào khay hệ thống, khóa màn hình Windows, thoát ứng dụng hoặc sau 5 phút không thao tác.
- Nhập sai PIN 5 lần liên tiếp (tính chung mọi màn hình và giữ nguyên sau khi khởi động lại) sẽ tạm khóa nhập PIN trong 5 phút và đưa sự kiện cảnh báo vào hàng đợi gửi email tới địa chỉ email nhận bạn đã cấu hình.

## 4. Cài đặt, Ngôn ngữ, Khay hệ thống & Khởi động cùng Windows

- Nhấn biểu tượng bánh răng ở góc trên phải để mở **Thiết lập**:
  - **Thông dụng:** Đổi ngay 4 ngôn ngữ (`Tiếng Việt`, `English`, `日本語`, `简体中文`), bật/tắt khởi động cùng Windows (mở vào khay hệ thống) và chọn hành vi khi đóng cửa sổ (Thu vào khay hệ thống hoặc Thoát hoàn toàn).
  - **Giao diện:** Đổi logo cá nhân ở đầu sidebar hoặc khôi phục mặc định.
  - **Secret:** Quản lý game ẩn, đổi PIN và cấu hình email cảnh báo.
  - **Thông tin:** Xem phiên bản launcher và mở thư mục dữ liệu.
- Khi đang ở chế độ thu vào khay hệ thống, nhấp chuột phải vào biểu tượng launcher trên khay hệ thống → **Thoát** để đóng hoàn toàn ứng dụng. Game đang chạy sẽ không bị tắt khi bạn đóng launcher.

## 5. Sao lưu, khôi phục và giới hạn bảo mật Secret

- Để sao lưu thư viện trên **cùng tài khoản Windows**, sao lưu toàn bộ thư mục `%LOCALAPPDATA%\PersonalLauncher\`.
- Tệp `Security\passcode` và `Security\mail-credentials.dat` được bảo vệ bằng **Windows DPAPI (CurrentUser)** gắn với tài khoản Windows hiện tại; không thể sao chép trực tiếp sang tài khoản hoặc máy Windows khác để giải mã.
- **Giới hạn bảo mật:** Secret bảo vệ việc hiển thị và truy cập trong giao diện/service của launcher; nó không mã hóa thư mục game trên ổ đĩa và không ngăn người có quyền quản trị máy tự mở trực tiếp tệp EXE từ Explorer.
