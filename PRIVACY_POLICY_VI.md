# Chính Sách Bảo Mật — V-Notch

**Ngày hiệu lực:** 15 tháng 5, 2026  
**Phiên bản ứng dụng:** 1.6.3  
**Nhà phát triển:** rainaku

---

## 1. Giới thiệu

V-Notch là ứng dụng desktop mã nguồn mở dành cho Windows, cung cấp giao diện notch kiểu macOS. Chính sách bảo mật này mô tả dữ liệu mà ứng dụng truy cập, cách sử dụng, và cách lưu trữ.

V-Notch được thiết kế theo nguyên tắc ưu tiên quyền riêng tư. Ứng dụng không thu thập, truyền tải, hoặc lưu trữ dữ liệu cá nhân trên bất kỳ máy chủ bên ngoài nào.

---

## 2. Dữ liệu mà ứng dụng truy cập

### 2.1 Thông tin phiên phương tiện

Ứng dụng truy cập Windows Media Session API để lấy metadata về phương tiện đang phát (ví dụ: Spotify, YouTube, SoundCloud, hoặc trình phát trên trình duyệt). Bao gồm tên bài hát, nghệ sĩ, ảnh bìa album, vị trí phát, và trạng thái phát.

Dữ liệu này chỉ được sử dụng để hiển thị trên giao diện notch theo thời gian thực và không được lưu trữ hoặc truyền đi.

### 2.2 Camera

Ứng dụng có thể truy cập camera hệ thống khi người dùng chủ động kích hoạt tính năng xem trước camera. Khung hình video chỉ được xử lý cục bộ để hiển thị. Không có hoạt động ghi hình, chụp ảnh, hoặc truyền dữ liệu camera.

### 2.3 Hệ thống tệp

Khi người dùng kéo tệp vào tính năng File Shelf, ứng dụng truy cập đường dẫn tệp và metadata cơ bản để hiển thị và quản lý. Nội dung tệp không được đọc, sửa đổi, hoặc truyền đi.

### 2.4 Âm thanh hệ thống

Ứng dụng truy cập Windows Core Audio API để đọc và điều chỉnh mức âm lượng hệ thống.

### 2.5 Tiêu đề cửa sổ

Ứng dụng quét tiêu đề cửa sổ đang hoạt động để nhận diện nguồn phát media (ví dụ: phát hiện YouTube hoặc SoundCloud). Thông tin này được sử dụng cục bộ và không được lưu trữ hoặc truyền đi.

---

## 3. Kết nối mạng

V-Notch không bao gồm bất kỳ hệ thống analytics, telemetry, hoặc theo dõi người dùng nào. Ứng dụng thực hiện các yêu cầu mạng sau:

### 3.1 Kiểm tra cập nhật

Ứng dụng truy vấn GitHub Releases API để xác định xem có phiên bản mới hơn hay không.

- **Điểm đến:** `https://api.github.com/repos/rainaku/V-Notch/releases/latest`
- **Dữ liệu gửi đi:** Header HTTP tiêu chuẩn (User-Agent: "V-Notch-Updater")
- **Dữ liệu nhận về:** Số phiên bản mới nhất, URL tải xuống, ghi chú phát hành
- **Tần suất:** Tối thiểu 45 giây giữa các lần kiểm tra; phản hồi được cache
- **Yêu cầu hành động người dùng:** Tải xuống và cài đặt chỉ được thực hiện khi người dùng chủ động thao tác

### 3.2 Tải ảnh bìa album

Để hiển thị ảnh bìa album cho phương tiện đang phát, ứng dụng có thể truy vấn:

- **YouTube:** Yêu cầu tìm kiếm để lấy thumbnail video từ `i.ytimg.com`
- **SoundCloud:** Yêu cầu API để lấy URL ảnh bìa

Dữ liệu truyền đi bao gồm tên bài hát và nghệ sĩ được sử dụng làm tham số tìm kiếm. Không có thông tin nhận dạng người dùng nào được bao gồm trong các yêu cầu này.

---

## 4. Lưu trữ dữ liệu cục bộ

Tất cả dữ liệu lâu dài được lưu trữ hoàn toàn trên thiết bị của người dùng tại `%APPDATA%\V-Notch\`.

### 4.1 Tệp cài đặt (settings.json)

Chứa tùy chọn người dùng bao gồm kích thước, vị trí, kiểu hiển thị notch, tùy chọn animation và hiệu ứng, cài đặt thông báo, ngôn ngữ, và hành vi khởi động.

### 4.2 Tệp log (vnotch-debug.log)

Chứa sự kiện ứng dụng và thông tin lỗi phục vụ mục đích chẩn đoán. Tệp này không chứa thông tin cá nhân và không bao giờ được truyền ra bên ngoài.

---

## 5. Dữ liệu không được thu thập

Ứng dụng không thu thập hoặc xử lý thông tin cá nhân, không theo dõi hành vi hoặc thói quen sử dụng của người dùng, không truyền dữ liệu analytics hoặc telemetry, không gửi báo cáo lỗi tự động, không ghi âm hoặc ghi hình, không truy cập dịch vụ định vị, không chia sẻ dữ liệu với bên thứ ba, và không tạo hồ sơ hoặc tài khoản người dùng.

---

## 6. Quyền

| Quyền | Mục đích | Bắt buộc |
|---|---|---|
| Media Session | Hiển thị phương tiện đang phát | Có |
| Camera | Xem trước camera trong notch | Không (tùy chọn) |
| Internet | Kiểm tra cập nhật và tải ảnh bìa | Không (tùy chọn) |
| Hệ thống tệp | File Shelf kéo-thả | Không (tùy chọn) |
| Audio Endpoint | Điều khiển âm lượng | Có |

---

## 7. Bảo mật

Ứng dụng chạy với quyền người dùng tiêu chuẩn và không yêu cầu quyền quản trị viên cho hoạt động bình thường. Mã nguồn được công khai để kiểm tra tại [github.com/rainaku/V-Notch](https://github.com/rainaku/V-Notch).

---

## 8. Quyền riêng tư trẻ em

V-Notch không thu thập dữ liệu từ bất kỳ người dùng nào, bao gồm trẻ em. Ứng dụng phù hợp cho mọi lứa tuổi.

---

## 9. Thay đổi chính sách

Chính sách bảo mật này có thể được cập nhật khi có tính năng mới. Các thay đổi sẽ được ghi nhận trong changelog của ứng dụng và phản ánh trong phiên bản cập nhật của tài liệu này.

---

## 10. Liên hệ

Nếu có câu hỏi về Chính sách bảo mật này, vui lòng tạo issue tại:  
[https://github.com/rainaku/V-Notch/issues](https://github.com/rainaku/V-Notch/issues)
