<p align="center">
  <img src="Services/icons/logo.png" width="128" height="128" alt="V-Notch Logo">
</p>

<h1 align="center">V-Notch</h1>

<p align="center">
  <b>Dynamic Island for Windows - Smart Notch Experience</b>
</p>

<p align="center">
  <a href="https://github.com/rainaku/V-Notch/releases">
    <img src="https://img.shields.io/github/v/release/rainaku/V-Notch?style=for-the-badge&color=8B5CF6&logo=github" alt="Latest Release">
  </a>
  <img src="https://img.shields.io/badge/platform-Windows-lightgrey?style=for-the-badge&logo=windows" alt="Platform">
  <img src="https://img.shields.io/badge/.NET-8.0-purple?style=for-the-badge&logo=dotnet" alt="Framework">
  <a href="LICENSE">
    <img src="https://img.shields.io/github/license/rainaku/V-Notch?style=for-the-badge" alt="License">
  </a>
</p>

<p align="center">
  V-Notch brings the iconic Dynamic Island experience from Apple devices to your Windows PC. 
  A smart, interactive notch that displays media controls, system info, and notifications with beautiful animations.
</p>

<p align="center">
  This project is entirely <b>non-profit</b> and <b>free</b>. <br>
  If you'd like to support my work, you can donate at my PayPal page: <a href="https://www.paypal.me/PhuocLe678"><b>Donate</b></a>
</p>

---

## Features

- **Smart Media Controls** - Control Spotify, Apple Music, YouTube, SoundCloud, TikTok, and more directly from the notch
- **File Shelf** - Dynamic clipboard for files. Drag & drop files to store, multi-select with lasso, and drag them out to any application
- **Dynamic Island Animations** - Ultra-smooth, Apple-style expand/collapse transitions with 60 FPS hardware acceleration
- **Intelligent Media Detection** - Hybrid detection using Windows SMTC and process monitoring for 99% accuracy
- **Color-Adaptive UI** - Dynamic background and glow effects that intelligently adapt to album art using HSL color analysis
- **Live Media Thumbnails** - Automatic thumbnail fetching for YouTube and smart cropping (removes Spotify branding bars)
- **Advanced Progress Tracking** - Real-time media progress with high-precision seeking and elapsed/remaining time display
- **System Info Dashboard** - Integrated battery status (with charging animations), calendar, and time widgets
- **Multi-Monitor Support** - Smart positioning and monitor selection with safe area management
- **Hot Corners** - Quick access to notch features via configurable screen corners
- **Cursor Bypass** - Intelligent click-through technology that doesn't interfere with your workflow
- **Fullscreen Aware** - Automatically adjusts behavior or hides when playing games or watching movies
- **Performance Optimized** - Low resource footprint (~20-30MB RAM) with lazy-loaded components

## Download & Installation

### Requirements
- Windows 10/11 (64-bit)
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)

### Installation
1. Download the latest `V-Notch-Setup.exe` from [Releases](https://github.com/rainaku/V-Notch/releases)
2. Run the installer and follow the instructions
3. Launch **V-Notch** from the Start Menu
4. (Optional) Enable "Start with Windows" in settings for a seamless experience

## Usage

### Basic Controls
| Action | Result |
|--------|--------|
| **Hover** | Expand notch to show media controls |
| **Scroll Down** | Switch to **File Shelf** while expanded |
| **Scroll Up** | Switch back to **Media Controls** |
| **Click Notch** | Toggle between compact and expanded view |
| **Media Controls** | Play/Pause, Next/Previous track, Seek |
| **Volume Bar** | Drag to adjust system volume |

### File Shelf Controls
| Action | Result |
|--------|--------|
| **Drag & Drop** | Add files to the shelf from Explorer |
| **Lasso Select** | Click and drag on empty space to multi-select |
| **Ctrl + Click** | Toggle individual file selection |
| **Drag Out** | Move selected files to any folder or app (Email, Discord, etc.) |
| **Delete Key** | Remove selected files from the shelf |

### Supported Platforms
| Platform | Capability |
|----------|------------|
| **Spotify** | Full control, smart-cropped album art, lyrics focus |
| **Apple Music** | Native Windows app support with high-res art |
| **YouTube** | Thumbnail fetching, title parsing, 15s seek support |
| **Tiktok/Reels** | Video title detection and basic playback control |
| **SoundCloud** | Native browser session detection |
| **Generic** | Works with any app implementing Windows Media Session |

## Settings

### Appearance
| Setting | Description | Range |
|---------|-------------|-------|
| **Width** | Notch width | 100px -> 400px |
| **Height** | Notch height | 20px -> 60px |
| **Corner Radius** | Roundness of corners | 0px -> 30px |
| **Opacity** | Transparency level | 50% -> 100% |

### Behavior
| Setting | Description |
|---------|-------------|
| **Enable Hover Expand** | Auto-expand when mouse hovers |
| **Show Camera Indicator** | Display camera dot indicator |
| **Start with Windows** | Launch on system startup |
| **Monitor Selection** | Choose which screen to show notch |

## Architecture

### Core Services
- **NotchManager** - Central controller for state transitions and window lifecycle
- **MediaDetectionService** - Multi-threaded engine for SMTC and Win32 process monitoring
- **AnimationService** - Custom Easing functions for premium "fluid" motion
- **FileShelfLogic** - High-performance file management with drag-and-drop integration
- **CursorBypassService** - Low-level hooks to manage mouse interaction zones

### Technical Highlights
- **WPF (.NET 8)** - Leveraging hardware acceleration for blur/glow effects
- **SMTC Integration** - Deep integration with Windows System Media Transport Controls
- **Smart Cropping** - Algorithm to remove branding strips and center-crop non-square art
- **Glow Engine** - Real-time HSL-based color extraction for vibrant UI accents
- **Lazy Loading** - Secondary views (Shelf) are initialized only when needed to save RAM

## System Requirements

| Component | Requirement |
|-----------|-------------|
| OS | Windows 10/11 (64-bit) |
| Runtime | .NET 8 Desktop Runtime |
| RAM | ~20-30 MB |
| CPU | Minimal usage |
| Display | Any resolution (adaptive positioning) |

## Browser Extension

V-Notch includes a companion browser extension for enhanced media detection:

### Supported Browsers
- Google Chrome
- Microsoft Edge
- Mozilla Firefox
- Opera, Brave, Vivaldi, Cốc Cốc

### Installation
1. Open `BrowserExtension` folder
2. Load as unpacked extension in your browser
3. Grant media access permissions

## Changelog

### v1.1.0 (Latest)
- **New Feature**: **File Shelf** - Store and manage files directly in the notch
- **Improved**: Enhanced Spotify & Apple Music detection stability
- **New**: Smart thumbnail cropping for better visual consistency
- **Update**: Improved HSL-based dynamic color extraction
- **Fixed**: UI flickering during rapid expand/collapse animations
- **Added**: Fullscreen detection to prevent interference with games

### v1.0.0
- Initial release with core Dynamic Island experience
- Basic media controls and system widgets
- Multi-monitor hardware support
- Dynamic background effects

## Contributing

Feel free to submit issues and pull requests!

### Development Setup
1. Clone the repository
2. Open `V-Notch.csproj` in Visual Studio 2022+
3. Build and run with F5

## License

MIT License - Feel free to use, modify, and distribute.

---

**Made with love by [rainaku](https://rainaku.id.vn)**

[![Facebook](https://img.shields.io/badge/Facebook-1877F2?logo=facebook&logoColor=white)](https://www.facebook.com/rain.107/)
[![GitHub](https://img.shields.io/badge/GitHub-181717?logo=github&logoColor=white)](https://github.com/rainaku/V-Notch)
[![Website](https://img.shields.io/badge/Website-FF7139?logo=firefox&logoColor=white)](https://rainaku.id.vn)
