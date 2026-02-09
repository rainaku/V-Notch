# V-Notch Browser Extension

Browser extension để kết nối V-Notch với video YouTube trên trình duyệt.

## Tính năng

- ✅ **Real-time progress tracking** - Theo dõi tiến trình video chính xác 100%
- ✅ **Play/Pause control** - Điều khiển phát/dừng từ Notch
- ✅ **Seek** - Click vào thanh tiến trình để seek video
- ✅ **Skip Next/Previous** - Chuyển video tiếp theo hoặc quay lại

## Cài đặt Extension

### Chrome / Edge / Brave

1. Mở trình duyệt và vào `chrome://extensions/` (hoặc `edge://extensions/` cho Edge)
2. Bật chế độ **Developer mode** (góc trên bên phải)
3. Click **Load unpacked**
4. Chọn thư mục `BrowserExtension` này

### Firefox

1. Mở Firefox và vào `about:debugging#/runtime/this-firefox`
2. Click **Load Temporary Add-on...**
3. Chọn file `manifest.json` trong thư mục này

## Cách hoạt động

1. **V-Notch** chạy WebSocket server trên cổng `52741`
2. **Extension** inject content script vào trang YouTube
3. Content script kết nối WebSocket và gửi thông tin video realtime
4. V-Notch hiển thị progress bar chính xác và nhận lệnh điều khiển từ người dùng

## Lưu ý

- Extension cần V-Notch đang chạy để kết nối
- Chỉ hoạt động trên các trang YouTube (`youtube.com`)
- Trạng thái kết nối hiển thị trong popup của extension

## Gỡ lỗi

Nếu extension không kết nối được:
1. Đảm bảo V-Notch đang chạy
2. Kiểm tra port 52741 không bị chặn bởi firewall
3. Reload extension và refresh trang YouTube

## Icons

Extension icons được tạo từ V-Notch logo. Nếu cần thay đổi icon:
- `icons/icon16.png` - 16x16 pixels
- `icons/icon48.png` - 48x48 pixels  
- `icons/icon128.png` - 128x128 pixels
