<p align="center">
  <img src="Assets/logo.png" width="128" height="128" alt="V-Notch Logo">
</p>

<h1 align="center">V-Notch</h1>

<p align="center">
  <b>Dynamic Island cho Windows — Trải nghiệm Notch thông minh</b>
</p>

<p align="center">
  <a href="https://github.com/rainaku/V-Notch/releases">
    <img src="https://img.shields.io/github/v/release/rainaku/V-Notch?style=for-the-badge&color=8B5CF6&logo=github" alt="Latest Release">
  </a>
  <img src="https://img.shields.io/badge/platform-Windows_10%2F11-lightgrey?style=for-the-badge&logo=windows" alt="Platform">
  <img src="https://img.shields.io/badge/.NET-8.0-purple?style=for-the-badge&logo=dotnet" alt="Framework">
  <a href="LICENSE">
    <img src="https://img.shields.io/github/license/rainaku/V-Notch?style=for-the-badge" alt="License">
  </a>
</p>

<p align="center">
  V-Notch mang trải nghiệm Dynamic Island từ thiết bị Apple lên Windows PC của bạn.<br>
  Một notch thông minh, tương tác — hiển thị media controls, thông tin hệ thống và thông báo với animation mượt mà.
</p>

<p align="center">
  Dự án hoàn toàn <b>phi lợi nhuận</b> và <b>miễn phí</b>.<br>
  Nếu bạn muốn ủng hộ, có thể donate tại: <a href="https://www.paypal.me/PhuocLe678"><b>PayPal</b></a>
</p>

---

## ✨ Tính năng chính

### 🎵 Media Controls
- Điều khiển Spotify, Apple Music, YouTube, SoundCloud, TikTok và nhiều hơn nữa trực tiếp từ notch
- Phát hiện media thông minh kết hợp Windows SMTC và process monitoring
- Thanh progress real-time với seeking chính xác cao và hiển thị thời gian elapsed/remaining
- Thumbnail tự động cho YouTube, smart-crop album art (loại bỏ branding bars của Spotify)
- UI tự thích ứng màu sắc dựa trên album art bằng phân tích HSL

### 📁 File Shelf
- Clipboard động cho files — kéo thả file vào để lưu trữ tạm
- Lasso selection để chọn nhiều file cùng lúc
- Kéo file ra bất kỳ ứng dụng nào (Explorer, Discord, Email, v.v.)
- Giao diện compact với grid layout tối ưu

### 🖥️ System Widgets
- Battery status với charging animation
- Calendar và đồng hồ tích hợp
- Camera indicator

### 🎬 Animation & UI
- Apple-style expand/collapse transitions mượt mà 60 FPS
- Glassmorphism blur effects với hardware acceleration
- Glow engine — trích xuất màu HSL real-time cho UI accents
- Spring animation cho chuyển đổi ngôn ngữ và UI elements

### ⚙️ Hệ thống
- **Fullscreen Aware** — Tự động ẩn khi chơi game hoặc xem phim (hỗ trợ cả exclusive và windowed fullscreen)
- **Slide animation** khi ẩn/hiện cho fullscreen
- **Multi-Monitor** — Chọn màn hình hiển thị notch
- **Cursor Bypass** — Click-through thông minh, không cản trở workflow
- **Hot Corners** — Truy cập nhanh qua góc màn hình
- **Auto-Update** — Kiểm tra và cập nhật tự động từ GitHub Releases
- **Start with Windows** — Khởi động cùng hệ thống
- **Đa ngôn ngữ** — Hỗ trợ Tiếng Anh và Tiếng Việt, chuyển đổi real-time

---

## 📥 Cài đặt

### Yêu cầu
- Windows 10/11 (64-bit)
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)

### Hướng dẫn
1. Tải `V-Notch-Setup.exe` từ [Releases](https://github.com/rainaku/V-Notch/releases)
2. Chạy installer và làm theo hướng dẫn Setup Wizard
3. Khởi động **V-Notch** từ Start Menu
4. (Tùy chọn) Bật "Start with Windows" trong Settings

---

## 🎮 Sử dụng

### Thao tác cơ bản
| Thao tác | Kết quả |
|----------|---------|
| **Hover** | Mở rộng notch hiển thị media controls |
| **Scroll Down** | Chuyển sang File Shelf |
| **Scroll Up** | Quay lại Media Controls |
| **Click** | Toggle compact/expanded |
| **Media buttons** | Play/Pause, Next/Previous, Seek |

### File Shelf
| Thao tác | Kết quả |
|----------|---------|
| **Kéo thả vào notch** | Thêm file vào shelf |
| **Lasso (kéo vùng trống)** | Chọn nhiều file |
| **Ctrl + Click** | Toggle chọn từng file |
| **Kéo ra ngoài** | Di chuyển file đến folder/app khác |
| **Delete** | Xóa file khỏi shelf |

### Nền tảng media hỗ trợ
| Nền tảng | Khả năng |
|----------|----------|
| **Spotify** | Full control, smart-cropped album art |
| **Apple Music** | Hỗ trợ native Windows app |
| **YouTube** | Thumbnail fetching, title parsing, 15s seek |
| **TikTok/Reels** | Video title detection, basic playback |
| **SoundCloud** | Browser session detection |
| **Generic** | Mọi app sử dụng Windows Media Session |

---

## ⚙️ Cài đặt (Settings)

### Giao diện
| Tùy chỉnh | Mô tả | Phạm vi |
|------------|--------|---------|
| Width | Chiều rộng notch | 100px → 400px |
| Height | Chiều cao notch | 20px → 60px |
| Corner Radius | Độ bo góc | 0px → 30px |
| Opacity | Độ trong suốt | 50% → 100% |

### Hành vi
| Tùy chỉnh | Mô tả |
|------------|--------|
| Enable Hover Expand | Tự mở rộng khi hover |
| Hide on Exclusive Fullscreen | Ẩn khi game fullscreen |
| Hide on Windowed Fullscreen | Ẩn khi borderless fullscreen |
| Show Camera Indicator | Hiển thị chấm camera |
| Start with Windows | Khởi động cùng hệ thống |
| Monitor Selection | Chọn màn hình hiển thị |
| Language | Tiếng Anh / Tiếng Việt |

---

## 🏗️ Kiến trúc

### Controllers
| Controller | Chức năng |
|------------|-----------|
| `NotchAnimationController` | Quản lý animation expand/collapse/slide |
| `MusicWidgetController` | Điều khiển media widget và progress |
| `FileShelfController` | Quản lý file shelf với thread-safe locking |
| `CameraPreviewController` | Camera indicator và preview |
| `TimerManager` | Quản lý timer lifecycle |

### Core Services
| Service | Chức năng |
|---------|-----------|
| `NotchManager` | State transitions và window lifecycle |
| `NotchStateManager` | State tracking, stuck recovery |
| `MediaDetectionService` | SMTC + Win32 process monitoring |
| `MediaSessionVolumeService` | Volume control tách biệt |
| `MediaTransportControlService` | Transport commands tách biệt |
| `FullscreenDetector` | Phát hiện exclusive & windowed fullscreen |
| `SettingsService` | Persistent settings với migration |
| `LocalizationService` | Hệ thống đa ngôn ngữ |
| `UpdateService` | Auto-update từ GitHub Releases |
| `RuntimeLog` | Structured logging với severity levels |

### Technical Stack
- **WPF (.NET 8)** — Hardware-accelerated rendering
- **CommunityToolkit.Mvvm** — MVVM pattern
- **NAudio** — Audio processing
- **SF Pro Font** — Apple typography
- **NSIS Installer** — Setup wizard

---

## 📋 Yêu cầu hệ thống

| Thành phần | Yêu cầu |
|------------|----------|
| OS | Windows 10/11 (64-bit) |
| Runtime | .NET 8 Desktop Runtime |
| RAM | ~20-30 MB |
| CPU | Minimal |
| Display | Mọi độ phân giải (adaptive positioning) |

---

## 🛠️ Development

### Setup
1. Clone repository
2. Mở `V-Notch.sln` trong Visual Studio 2022+
3. Build và run với F5

### Build Release
```powershell
./build-release.ps1
```

### Build Installer
```powershell
./build-installer.ps1
```

---

## 📄 License

Apache License 2.0 — Xem [LICENSE](LICENSE) để biết chi tiết.

---

<p align="center">
  <b>Made with ❤️ by <a href="https://rainaku.id.vn">rainaku</a></b>
</p>

<p align="center">
  <a href="https://www.facebook.com/rain.107/">
    <img src="https://img.shields.io/badge/Facebook-1877F2?logo=facebook&logoColor=white" alt="Facebook">
  </a>
  <a href="https://github.com/rainaku/V-Notch">
    <img src="https://img.shields.io/badge/GitHub-181717?logo=github&logoColor=white" alt="GitHub">
  </a>
  <a href="https://rainaku.id.vn">
    <img src="https://img.shields.io/badge/Website-FF7139?logo=firefox&logoColor=white" alt="Website">
  </a>
</p>
