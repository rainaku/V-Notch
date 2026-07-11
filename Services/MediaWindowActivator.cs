using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using VNotch.Models;
using static VNotch.Services.Win32Interop;

namespace VNotch.Services;

internal static class MediaWindowActivator
{
    public static bool TryActivateForMedia(MediaInfo info)
    {
        var candidates = GetProcessCandidates(info).ToList();
        var processNames = new HashSet<string>(candidates, StringComparer.OrdinalIgnoreCase);
        bool preferBrowserTabMatch = info.IsVideoSource || info.Platform == MediaPlatform.SoundCloud;

        if (preferBrowserTabMatch && TryActivateBestMatchingWindow(info, processNames, out _))
        {
            return true;
        }

        foreach (string processName in candidates)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(processName); }
            catch (Exception ex)
            {
                RuntimeLog.Error("MEDIA-ACTIVATOR", ex.ToString());
                continue;
            }

            foreach (var process in processes)
            {
                try
                {
                    process.Refresh();
                    IntPtr hwnd = process.MainWindowHandle;
                    if (hwnd == IntPtr.Zero) continue;

                    if (TryActivateWindow(hwnd)) return true;
                }
                catch (Exception ex)
                {
                    RuntimeLog.Error("MEDIA-ACTIVATOR", ex.ToString());
                }
            }
        }

        return TryActivateBestMatchingWindow(info, processNames, out _);
    }
    public static IEnumerable<string> GetProcessCandidates(MediaInfo info)
    {
        var candidates = new List<string>();

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (candidates.Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase))) return;
            candidates.Add(value);
        }

        foreach (Match match in Regex.Matches(info.SourceAppId ?? string.Empty, @"([A-Za-z0-9_\-]+)\.exe", RegexOptions.IgnoreCase))
        {
            Add(match.Groups[1].Value);
        }

        string sourceAppId = info.SourceAppId ?? string.Empty;

        if (sourceAppId.Contains("spotify", StringComparison.OrdinalIgnoreCase) ||
            info.Platform == MediaPlatform.Spotify)
        {
            Add("Spotify");
        }

        if (sourceAppId.Contains("applemusic", StringComparison.OrdinalIgnoreCase) ||
            sourceAppId.Contains("apple music", StringComparison.OrdinalIgnoreCase) ||
            info.Platform == MediaPlatform.AppleMusic)
        {
            Add("AppleMusic");
        }

        if (sourceAppId.Contains("msedge", StringComparison.OrdinalIgnoreCase) ||
            sourceAppId.Contains("edge", StringComparison.OrdinalIgnoreCase)) Add("msedge");
        if (sourceAppId.Contains("chrome", StringComparison.OrdinalIgnoreCase)) Add("chrome");
        if (sourceAppId.Contains("firefox", StringComparison.OrdinalIgnoreCase)) Add("firefox");
        if (sourceAppId.Contains("brave", StringComparison.OrdinalIgnoreCase)) Add("brave");
        if (sourceAppId.Contains("opera", StringComparison.OrdinalIgnoreCase)) Add("opera");
        if (sourceAppId.Contains("vivaldi", StringComparison.OrdinalIgnoreCase)) Add("vivaldi");
        if (sourceAppId.Contains("arc", StringComparison.OrdinalIgnoreCase)) Add("arc");
        if (sourceAppId.Contains("sidekick", StringComparison.OrdinalIgnoreCase)) Add("sidekick");

        if (string.IsNullOrWhiteSpace(sourceAppId) &&
            (info.IsVideoSource || info.Platform == MediaPlatform.SoundCloud))
        {
            Add("msedge"); Add("chrome"); Add("firefox"); Add("brave"); Add("opera");
        }

        if (info.IsVideoSource || info.Platform == MediaPlatform.SoundCloud)
        {
            Add("msedge"); Add("chrome"); Add("firefox"); Add("brave"); Add("opera"); Add("vivaldi");
        }

        return candidates;
    }
    public static bool TryActivateBestMatchingWindow(MediaInfo info, ISet<string> processNames, out bool usedBrowser)
    {
        usedBrowser = false;
        IntPtr bestHwnd = IntPtr.Zero;
        string bestProcessName = string.Empty;
        int bestScore = 0;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;

            GetWindowThreadProcessId(hwnd, out uint processId);
            if (processId == 0) return true;

            string processName;
            try { processName = Process.GetProcessById((int)processId).ProcessName; }
            catch { return true; }

            if (processNames.Count > 0 && !processNames.Contains(processName)) return true;

            string title = GetWindowTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title)) return true;

            int score = ScoreWindow(title, processName, info);
            if (score <= 0 && info.IsVideoSource && IsBrowserProcess(processName)) score = 1;
            if (score > bestScore)
            {
                bestScore = score;
                bestHwnd = hwnd;
                bestProcessName = processName;
            }

            return true;
        }, IntPtr.Zero);

        usedBrowser = IsBrowserProcess(bestProcessName);
        return bestHwnd != IntPtr.Zero && TryActivateWindow(bestHwnd);
    }
    public static bool TryActivateWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        else ShowWindow(hwnd, SW_SHOW);

        return SetForegroundWindow(hwnd);
    }

    public static bool IsBrowserProcess(string processName)
    {
        return processName.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("firefox", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("brave", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("opera", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("vivaldi", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("browser", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("arc", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("sidekick", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        int length = GetWindowTextLength(hwnd);
        if (length <= 0) return string.Empty;

        var sb = new StringBuilder(length + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static int ScoreWindow(string title, string processName, MediaInfo info)
    {
        int score = 0;
        string source = info.MediaSource ?? string.Empty;
        string track = NormalizeTitle(info.CurrentTrack);
        string artist = NormalizeTitle(info.CurrentArtist);
        string window = NormalizeTitle(title);

        if (!string.IsNullOrWhiteSpace(source) && title.Contains(source, StringComparison.OrdinalIgnoreCase)) score += 80;
        if (!string.IsNullOrWhiteSpace(track) && window.Contains(track, StringComparison.OrdinalIgnoreCase)) score += 140;
        if (!string.IsNullOrWhiteSpace(artist) && artist is not "youtube" and not "browser" && window.Contains(artist, StringComparison.OrdinalIgnoreCase)) score += 70;

        if (info.Platform == MediaPlatform.YouTube && title.Contains("YouTube", StringComparison.OrdinalIgnoreCase)) score += 90;
        if (info.Platform == MediaPlatform.SoundCloud && title.Contains("SoundCloud", StringComparison.OrdinalIgnoreCase)) score += 90;
        if (info.Platform == MediaPlatform.Facebook && title.Contains("Facebook", StringComparison.OrdinalIgnoreCase)) score += 90;
        if (info.Platform == MediaPlatform.TikTok && title.Contains("TikTok", StringComparison.OrdinalIgnoreCase)) score += 90;
        if (info.Platform == MediaPlatform.Instagram && title.Contains("Instagram", StringComparison.OrdinalIgnoreCase)) score += 90;
        if (info.Platform == MediaPlatform.Twitter &&
            (title.Contains("Twitter", StringComparison.OrdinalIgnoreCase) || title.Contains(" / X", StringComparison.OrdinalIgnoreCase))) score += 90;
        if (info.IsVideoSource && IsBrowserProcess(processName)) score += 25;

        return score;
    }

    private static string NormalizeTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string normalized = Regex.Replace(value, @"\s+", " ").Trim().ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"\s+-\s+(youtube|google chrome|microsoft edge|mozilla firefox|brave|opera|vivaldi).*$", "", RegexOptions.IgnoreCase);
        return normalized;
    }
}
