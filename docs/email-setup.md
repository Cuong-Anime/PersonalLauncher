# Cấu hình Email Cảnh báo (Sai PIN 5 lần)

## Mục đích

Khi có 5 lần nhập sai mã PIN Secret liên tiếp (tính gộp ở mọi màn hình, không reset khi đóng/mở lại ứng dụng), PersonalLauncher tạo một sự kiện cảnh báo trong bảng `MailOutbox` (SQLite) và gửi email tự động ở tiến trình nền tới địa chỉ email nhận do bạn cấu hình trong **Cài đặt → Secret**.

Nội dung email chỉ gồm tên launcher, thời điểm (UTC & giờ máy), lý do (`Sai PIN 5 lần`) và mã sự kiện (`Event ID`). Không bao giờ chứa mã PIN, tên game ẩn, đường dẫn hay ảnh Secret.

## Hướng dẫn thiết lập (Ví dụ với Gmail SMTP)

1. Mở **Cài đặt → Secret** và nhập mã PIN quản lý.
2. Trong mục **Cảnh báo email khi sai PIN 5 lần**, nhập:
   - **Email nhận thông báo:** Địa chỉ Gmail/email bạn muốn nhận cảnh báo (ví dụ: `tenban@gmail.com`)
   - **SMTP Host:** `smtp.gmail.com`
   - **SMTP Port:** `587` (hoặc `465` với `SslOnConnect`)
   - **Chế độ TLS:** `StartTls` (bắt buộc TLS; chứng chỉ máy chủ luôn được kiểm tra nghiêm ngặt)
   - **Địa chỉ email gửi:** Tài khoản Gmail bạn dùng để gửi cảnh báo (đã bật Xác minh 2 bước và tạo **App Password** 16 ký tự trong Google Account Security)
   - **Kiểu xác thực:** `PasswordOrAppPassword` (hoặc `OAuth2AccessToken` nếu dùng OAuth2)
   - **Thông tin xác thực:** Dán App Password (được mã hóa bằng Windows DPAPI vào `Security\mail-credentials.dat`, không lưu vào `settings.json` hay log).
3. Nhấn **Lưu cấu hình email**.
4. Nhấn **Gửi thư thử** và kiểm tra hộp thư nhận bạn vừa nhập để xác nhận thư thực tế đã đến inbox.

## Cơ chế hàng đợi & thử lại khi mất mạng

- Nếu chưa cấu hình SMTP hoặc đang mất mạng khi xảy ra lần sai thứ 5, sự kiện vẫn nằm trong `MailOutbox` và được giữ qua các lần khởi động lại.
- Khi có mạng và đã cấu hình hợp lệ, background worker tự động thử lại theo giãn cách: **1 phút → 5 phút → 15 phút → 1 giờ** (có jitter).
- Ứng dụng phân biệt rõ 4 trạng thái: `Pending` (đang chờ), `Accepted` (máy chủ đã chấp nhận), `Failed` (lỗi), và `Unknown` (timeout sau khi đã bắt đầu truyền thư — không tự động gửi lặp để tránh spam trùng).
