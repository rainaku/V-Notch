# Privacy Policy — V-Notch

**Effective Date:** May 15, 2026  
**Application Version:** 1.6.3  
**Developer:** rainaku

---

## 1. Introduction

V-Notch is an open-source desktop application for Windows that provides a macOS-style notch interface. This Privacy Policy describes what data the application accesses, how it is used, and how it is stored.

V-Notch is designed with a privacy-first approach. The application does not collect, transmit, or store personal data on any external server.

---

## 2. Data Accessed by the Application

### 2.1 Media Session Information

The application accesses the Windows Media Session API to retrieve metadata about currently playing media (e.g., Spotify, YouTube, SoundCloud, or browser-based players). This includes track title, artist name, album artwork, playback position, and playback state.

This data is used solely for real-time display on the notch interface and is not persisted or transmitted.

### 2.2 Camera

The application may access the system camera when the user explicitly activates the camera preview feature. Video frames are processed locally for display purposes only. No recording, capture, or transmission of camera data occurs.

### 2.3 File System

When the user drags files into the File Shelf feature, the application accesses file paths and basic metadata for display and management purposes. File content is not read, modified, or transmitted.

### 2.4 System Audio

The application accesses the Windows Core Audio API to read and adjust system volume levels.

### 2.5 Window Titles

The application scans active window titles to identify media sources (e.g., detecting YouTube or SoundCloud playback). This information is used locally and is not stored or transmitted.

---

## 3. Network Connections

V-Notch does not include any analytics, telemetry, or user tracking systems. The application makes the following network requests:

### 3.1 Update Checks

The application queries the GitHub Releases API to determine whether a newer version is available.

- **Endpoint:** `https://api.github.com/repos/rainaku/V-Notch/releases/latest`
- **Data transmitted:** Standard HTTP headers (User-Agent: "V-Notch-Updater")
- **Data received:** Latest version number, download URL, release notes
- **Frequency:** Minimum 45-second interval between checks; responses are cached
- **User action required:** Downloads and installations are initiated only by explicit user action

### 3.2 Album Artwork Retrieval

To display album artwork for currently playing media, the application may query:

- **YouTube:** Search requests to retrieve video thumbnails from `i.ytimg.com`
- **SoundCloud:** API requests to retrieve track artwork URLs

Data transmitted consists of track title and artist name used as search parameters. No user-identifiable information is included in these requests.

---

## 4. Local Data Storage

All persistent data is stored exclusively on the user's device at `%APPDATA%\V-Notch\`.

### 4.1 Settings File (settings.json)

Contains user preferences including notch dimensions, position, visual style, animation preferences, notification settings, language preference, and startup behavior.

### 4.2 Debug Log (vnotch-debug.log)

Contains application events and error information for diagnostic purposes. This file does not contain personal information and is never transmitted externally.

---

## 5. Data Not Collected

The application does not collect or process personal information, track user behavior, transmit analytics or telemetry data, send automated crash reports, record audio or video, access location services, share data with third parties, or create user profiles or accounts.

---

## 6. Permissions

| Permission | Purpose | Required |
|---|---|---|
| Media Session | Display currently playing media | Yes |
| Camera | Camera preview in notch | No (opt-in) |
| Internet | Update checks and artwork retrieval | No (optional) |
| File System | File Shelf drag-and-drop | No (opt-in) |
| Audio Endpoint | Volume control | Yes |

---

## 7. Security

The application runs under standard user privileges and does not require administrator access for normal operation. The source code is publicly available for review at [github.com/rainaku/V-Notch](https://github.com/rainaku/V-Notch).

---

## 8. Children's Privacy

V-Notch does not collect data from any user, including children. The application is suitable for all ages.

---

## 9. Changes to This Policy

This Privacy Policy may be updated when new features are introduced. Changes will be documented in the application changelog and reflected in updated versions of this document.

---

## 10. Contact

For questions regarding this Privacy Policy, please open an issue at:  
[https://github.com/rainaku/V-Notch/issues](https://github.com/rainaku/V-Notch/issues)
