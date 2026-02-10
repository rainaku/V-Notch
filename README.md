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

- **Smart Media Controls** - Control Spotify, YouTube, SoundCloud, TikTok, and more directly from the notch
- **Dynamic Island Animations** - Smooth, Apple-style expand/collapse animations with 60 FPS
- **System Info at a Glance** - Battery status, calendar, and time in a beautiful compact view
- **Volume Control** - Quick volume adjustment with visual feedback
- **Live Media Thumbnails** - Album art and video thumbnails with color-adaptive glow effects
- **Progress Bar** - Real-time media progress tracking with seek functionality
- **Multi-Platform Support** - Works with Spotify, YouTube, SoundCloud, TikTok, Facebook, Instagram, Twitter/X
- **Always on Top** - Stays above all windows including fullscreen apps
- **Highly Customizable** - Adjust size, opacity, corner radius, and behavior
- **Multi-Monitor Support** - Choose which monitor to display the notch
- **Smart Collapse** - Auto-collapse when not in use, expand on hover
- **Low Resource Usage** - Optimized for minimal CPU and RAM usage

## Download & Installation

### Requirements
- Windows 10/11 (64-bit)
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)

### Installation
1. Download `V-Notch.zip` from [Releases](https://github.com/rainaku/V-Notch/releases)
2. Extract to any folder
3. Run `V-Notch.exe`
4. (Optional) Enable "Start with Windows" in settings

## Usage

### Basic Controls
| Action | Result |
|--------|--------|
| **Hover** | Expand notch to show full controls |
| **Click Notch** | Toggle between compact and expanded view |
| **Click Media Widget** | Expand inline music player |
| **Media Controls** | Play/Pause, Next/Previous track |
| **Volume Bar** | Click and drag to adjust volume |
| **Progress Bar** | Click to seek in media |

### Media Platform Support

| Platform | Features |
|----------|----------|
| **Spotify** | Full control, album art, track info |
| **YouTube** | Play/pause, seek 15s, thumbnail, title |
| **SoundCloud** | Basic controls, track info |
| **TikTok** | Video controls, seek support |
| **Facebook** | Video controls |
| **Instagram** | Reel controls |
| **Twitter/X** | Video controls |
| **Browser** | Generic media session support |

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

### Core Components

```
V-Notch/
├── MainWindow.xaml.cs          # Main UI and animations
├── MainWindow.Progress.cs      # Media progress tracking
├── Services/
│   ├── NotchManager.cs         # Notch state & positioning
│   ├── MediaDetectionService.cs # Media session detection
│   ├── BatteryService.cs       # Battery info
│   ├── VolumeService.cs        # Volume control
│   ├── AnimationService.cs     # Animation helpers
│   └── ...                     # Other services
├── Models/
│   ├── NotchSettings.cs        # Settings model
│   └── NotchContent.cs         # Content model
└── BrowserExtension/           # Browser companion extension
```

### Technical Highlights

- **WPF (.NET 8)** - Modern UI framework with hardware acceleration
- **Windows Media Session API** - Native media control integration
- **Low-level Win32 Hooks** - Always-on-top window management
- **60 FPS Animations** - Smooth UI transitions
- **Color Extraction** - Dynamic background from album art
- **Memory Optimized** - ~20-30 MB RAM usage

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

### v1.0.0
- Initial release
- Dynamic Island-style notch with expand/collapse animations
- Media control for Spotify, YouTube, and major platforms
- Battery and calendar widgets
- Volume control with visual feedback
- Real-time progress bar with seeking
- Color-adaptive background from album art
- Multi-monitor support
- Settings window with customization options
- System tray integration
- Start with Windows option
- Browser extension for enhanced detection

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
