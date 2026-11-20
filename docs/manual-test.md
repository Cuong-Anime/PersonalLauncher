# Danh sách kiểm thử thủ công (Manual Acceptance Checklist)

Thực hiện trên Windows 11 (DPI 100%, 125%, 150%, 200%; độ phân giải 1280×720, 1366×768, 1920×1080):

1. **T01 – Trang game chính:** Xác nhận wallpaper phủ toàn bộ cửa sổ kể cả sau sidebar; không có banner sự kiện, bảng tin, nút đăng ký trước hay cột icon social bên phải; sử dụng đúng `buttons_play.png`, `Somthing_Button.png`, `buttons_setting.png`, `buttons_menu.png`, `logo.png`, `logo.ico`.
2. **T02 – Sidebar xuyên nền:** Thử wallpaper sáng và tối; xác nhận nền sidebar bán trong suốt nhìn thấy wallpaper phía sau trong khi logo cá nhân, icon game và nút Toàn bộ game luôn rõ nét.
3. **T03 / T04 – Chỉnh vị trí & ảnh:** Đổi đường dẫn EXE (kiểm tra file cũ không bị xóa/di chuyển), đổi Icon / Title Logo / Wallpaper, thử Hủy (giữ ảnh cũ) và Lưu rồi khởi động lại (giữ tùy chỉnh).
4. **T05 / T06 / T07 – Thêm thủ công, chọn nhanh thẻ, tìm kiếm:** Thêm game có tên/đường dẫn Unicode và dấu cách; dùng phím mũi tên trái/phải đổi thẻ nhanh trong Toàn bộ game; tìm kiếm bằng tiếng Việt có/không dấu (`đ`/`d`), tiếng Nhật và tiếng Trung.
5. **T08 / T09 / T10 – Quét thư mục tự động:** Quét thư mục lồng nhiều cấp, thư mục có nhiều EXE, có tệp `unins000.exe`/`vcredist_x64.exe`, có junction; hủy giữa chừng; quét lại cùng thư mục không tạo trùng và không làm lộ game Secret.
6. **T11 – Đủ bốn ngôn ngữ:** Chuyển lần lượt `Tiếng Việt`, `English`, `日本語`, `简体中文`, kiểm tra nút Play, tooltip, hộp thoại, cài đặt, menu khay hệ thống và khởi động lại.
7. **T12 / T13 – Khay hệ thống, đóng cửa sổ, khởi động cùng Windows & Single Instance:** Thử `X`, `Alt+F4`, nút thu nhỏ, `Tray → Mở launcher`, `Tray → Thoát`, bật/tắt khởi động cùng Windows, mở ứng dụng lần hai (phải đưa cửa sổ cũ lên trước), kiểm tra thoát launcher không tắt game đang chạy.
8. **T15 – T25 – Secret, passcode & Email cảnh báo thật:** Đặt PIN `0042`, kiểm tra tệp `%LOCALAPPDATA%\PersonalLauncher\Security\passcode` không có `0042` nguyên văn; kiểm tra tự khóa khi rời tab/thu nhỏ/khóa Windows (`Win+L`)/chờ 5 phút; kiểm tra sai 4 lần → thoát & mở lại → sai lần 5 → tạm khóa 5 phút và nhận email thật tại địa chỉ email nhận đã cấu hình sau khi thiết lập SMTP.
