<p align="center">
  <img src="Assets/logo.png" width="128" height="128" alt="V-Notch Logo">
</p>

<h1 align="center">V-Notch</h1>

<p align="center">
  <b>Dynamic Island for Windows — Smart Notch Experience</b>
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
  V-Notch brings the Dynamic Island experience from Apple devices to your Windows PC.<br>
  A smart, interactive notch that displays media controls, system info, and notifications with smooth animations.
</p>

<p align="center">
  This project is entirely <b>non-profit</b> and <b>free</b>.<br>
  If you'd like to support my work, you can donate at: <a href="https://www.paypal.me/PhuocLe678"><b>PayPal</b></a>
</p>

---

## Features

### Media Controls
- Control Spotify, Apple Music, YouTube, SoundCloud, TikTok and more directly from the notch
- Intelligent media detection combining Windows SMTC and process monitoring
- Real-time progress bar with high-precision seeking and elapsed/remaining time display
- Automatic thumbnail fetching for YouTube, smart-cropped album art (removes Spotify branding bars)
- Color-adaptive UI based on album art using HSL color analysis

### File Shelf
- Dynamic clipboard for files — drag and drop files to store temporarily
- Lasso selection for multi-file picking
- Drag files out to any application (Explorer, Discord, Email, etc.)
- Compact grid layout with thread-safe file management

### System Widgets
- Battery status with charging animation
- Integrated calendar and clock
- Camera indicator

### Animation & UI
- Apple-style expand/collapse transitions at 60 FPS
- Glassmorphism blur effects with hardware acceleration
- Glow engine — real-time HSL color extraction for vibrant UI accents
- Spring animations for language switching and UI element transitions

### System
- **Fullscreen Aware** — Automatically hides when gaming or watching movies (supports both exclusive and windowed fullscreen)
- **Slide animation** when hiding/showing for fullscreen transitions
- **Multi-Monitor** — Choose which display shows the notch
- **Cursor Bypass** — Smart click-through that doesn't interfere with your workflow
- **Hot Corners** — Quick access via configurable screen corners
- **Auto-Update** — Checks and updates automatically from GitHub Releases
- **Start with Windows** — Launch on system startup
- **Multilingual** — English and Vietnamese with real-time switching

---

## Download & Installation

### Requirements
- Windows 10/11 (64-bit)
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)

### Install
1. Download `V-Notch-Setup.exe` from [Releases](https://github.com/rainaku/V-Notch/releases)
2. Run the installer and follow the Setup Wizard
3. Launch **V-Notch** from the Start Menu
4. (Optional) Enable "Start with Windows" in Settings

---

## Usage

### Basic Controls
| Action | Result |
|--------|--------|
| **Hover** | Expand notch to show media controls |
| **Scroll Down** | Switch to File Shelf |
| **Scroll Up** | Switch back to Media Controls |
| **Click** | Toggle compact/expanded view |
| **Media buttons** | Play/Pause, Next/Previous, Seek |

### File Shelf
| Action | Result |
|--------|--------|
| **Drag & drop onto notch** | Add files to the shelf |
| **Lasso (drag on empty space)** | Multi-select files |
| **Ctrl + Click** | Toggle individual file selection |
| **Drag out** | Move files to any folder or app |
| **Delete** | Remove files from the shelf |

### Supported Platforms
| Platform | Capability |
|----------|------------|
| **Spotify** | Full control, smart-cropped album art |
| **Apple Music** | Native Windows app support |
| **YouTube** | Thumbnail fetching, title parsing, 15s seek |
| **TikTok/Reels** | Video title detection, basic playback |
| **SoundCloud** | Browser session detection |
| **Generic** | Any app using Windows Media Session |

---

## System Requirements

| Component | Requirement |
|-----------|-------------|
| OS | Windows 10/11 (64-bit) |
| Runtime | .NET 8 Desktop Runtime |
| RAM | ~20-30 MB |
| CPU | Minimal usage |
| Display | Any resolution (adaptive positioning) |

---

## License

Apache License 2.0 — See [LICENSE](LICENSE) for details.

---

<p align="center">
  <b>Made with love by <a href="https://rainaku.id.vn">rainaku</a></b>
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
