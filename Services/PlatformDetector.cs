using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VNotch.Services;

/// <summary>
/// Identifies the media platform (YouTube, Spotify, SoundCloud, etc.) from
/// window titles, SMTC app IDs, and track metadata. Replaces scattered string
/// matching throughout MediaDetectionService with a centralized, testable class.
/// </summary>
public static class PlatformDetector
{
    // ─── App ID → Platform mapping ───

    private static readonly (string Pattern, MediaPlatform Platform)[] AppIdRules =
    {
        ("Spotify", MediaPlatform.Spotify),
    };

    private static readonly (string Pattern, MediaPlatform Platform)[] BrowserAppIdPatterns =
    {
        ("Chrome", MediaPlatform.Browser),
        ("Edge", MediaPlatform.Browser),
        ("Firefox", MediaPlatform.Browser),
        ("MS-Edge", MediaPlatform.Browser),
        ("msedge", MediaPlatform.Browser),
        ("Opera", MediaPlatform.Browser),
        ("Brave", MediaPlatform.Browser),
        ("Vivaldi", MediaPlatform.Browser),
        ("Coccoc", MediaPlatform.Browser),
        ("Arc", MediaPlatform.Browser),
        ("Sidekick", MediaPlatform.Browser),
        ("Browser", MediaPlatform.Browser),
    };

    // ─── Window title separators to strip ───

    private static readonly string[] TitleSeparators =
    {
        " - YouTube", " – YouTube", " - SoundCloud", " | Facebook",
        " - TikTok", " / X", " | TikTok", " • Instagram",
        " - Apple Music", " – Apple Music",
        " - Google Chrome", " - Microsoft\u200B Edge", " - Microsoft Edge",
        " - Mozilla Firefox", " - Opera", " - Brave", " - Cốc Cốc",
        " - Browser", " – Current browser"
    };

    // ─── Public API ───

    /// <summary>
    /// Detect platform from a SMTC source app ID.
    /// </summary>
    public static MediaPlatform DetectFromAppId(string? sourceAppId)
    {
        if (string.IsNullOrEmpty(sourceAppId))
            return MediaPlatform.Unknown;

        foreach (var (pattern, platform) in AppIdRules)
        {
            if (sourceAppId.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return platform;
        }

        return MediaPlatform.Unknown;
    }

    /// <summary>
    /// Check if a source app ID belongs to a web browser.
    /// </summary>
    public static bool IsBrowserApp(string? sourceAppId)
    {
        if (string.IsNullOrEmpty(sourceAppId))
            return false;

        foreach (var (pattern, _) in BrowserAppIdPatterns)
        {
            if (sourceAppId.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Detect the most likely platform from a collection of window titles.
    /// YouTube takes priority; other platforms are fallback.
    /// </summary>
    public static MediaPlatform DetectFromWindowTitles(IEnumerable<string> windowTitles)
    {
        MediaPlatform fallback = MediaPlatform.Unknown;

        foreach (var title in windowTitles)
        {
            var lower = title.ToLower();

            if (lower.Contains("youtube") && !lower.StartsWith("youtube -") && lower != "youtube")
                return MediaPlatform.YouTube;

            if (lower.Contains("soundcloud") && fallback == MediaPlatform.Unknown)
                fallback = MediaPlatform.SoundCloud;
            else if ((lower.Contains("apple music") || lower.Contains("music.apple.com")) && fallback == MediaPlatform.Unknown)
                fallback = MediaPlatform.AppleMusic;
            else if (lower.Contains("facebook") && (lower.Contains("watch") || lower.Contains("video")) && fallback == MediaPlatform.Unknown)
                fallback = MediaPlatform.Facebook;
            else if (lower.Contains("tiktok") && lower.Contains(" | ") && fallback == MediaPlatform.Unknown)
                fallback = MediaPlatform.TikTok;
            else if (lower.Contains("instagram") && (lower.Contains("reel") || lower.Contains("video")) && fallback == MediaPlatform.Unknown)
                fallback = MediaPlatform.Instagram;
            else if ((lower.Contains("twitter") || lower.Contains(" / x")) && (lower.Contains("video") || lower.Contains("watch")) && fallback == MediaPlatform.Unknown)
                fallback = MediaPlatform.Twitter;
        }

        return fallback;
    }

    /// <summary>
    /// Get the platform hint string (for backward compatibility with existing code).
    /// Returns empty string for Unknown.
    /// </summary>
    public static string DetectPlatformHint(IEnumerable<string> windowTitles)
    {
        var platform = DetectFromWindowTitles(windowTitles);
        return platform.ToDisplayString();
    }

    /// <summary>
    /// Check if a track is likely playing on a specific platform by matching
    /// the track name against window titles containing the platform name.
    /// </summary>
    public static bool HasReliableWindowMatch(IEnumerable<string> windowTitles, string track, MediaPlatform platform)
    {
        if (string.IsNullOrWhiteSpace(track))
            return false;

        string platformName = platform.ToDisplayString().ToLower();
        if (string.IsNullOrEmpty(platformName))
            return false;

        string normalizedTrack = NormalizeForLooseMatch(track);
        if (string.IsNullOrEmpty(normalizedTrack))
            return false;

        foreach (var title in windowTitles)
        {
            if (!title.Contains(platformName, StringComparison.OrdinalIgnoreCase))
                continue;

            string normalizedTitle = NormalizeForLooseMatch(title);
            if (normalizedTitle.Contains(normalizedTrack, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Overload accepting platform as string for backward compatibility.
    /// </summary>
    public static bool HasReliableWindowMatch(IEnumerable<string> windowTitles, string track, string platformName)
    {
        if (string.IsNullOrWhiteSpace(track) || string.IsNullOrWhiteSpace(platformName))
            return false;

        string normalizedTrack = NormalizeForLooseMatch(track);
        if (string.IsNullOrEmpty(normalizedTrack))
            return false;

        foreach (var title in windowTitles)
        {
            if (!title.Contains(platformName, StringComparison.OrdinalIgnoreCase))
                continue;

            string normalizedTitle = NormalizeForLooseMatch(title);
            if (normalizedTitle.Contains(normalizedTrack, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Extract the video/track title from a browser window title by stripping
    /// platform suffixes, notification counts, and play state indicators.
    /// </summary>
    public static string ExtractTitleFromWindow(string windowTitle, MediaPlatform platform)
    {
        return ExtractTitleFromWindow(windowTitle, platform.ToDisplayString());
    }

    /// <summary>
    /// Extract the video/track title from a browser window title (string platform overload).
    /// </summary>
    public static string ExtractTitleFromWindow(string windowTitle, string platformFallback)
    {
        var title = windowTitle;

        // Strip notification count prefix: (3) or (10+)
        title = Regex.Replace(title, @"^\(\d+\+?\)\s*", "");
        // Strip play/pause emoji prefix
        title = Regex.Replace(title, @"^[▶⏸▶️⏸️\s]*", "");
        // Strip timestamp prefix
        title = Regex.Replace(title, @"^[▶⏸\s\d:]+\|", "").Trim();

        // Strip platform suffixes
        foreach (var sep in TitleSeparators)
        {
            int idx = title.IndexOf(sep, StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                title = title.Substring(0, idx);
            }
        }

        title = title.Trim();

        // Strip trailing separators
        title = Regex.Replace(title, @"\s+[\-\|–•]\s*$", "");

        return string.IsNullOrEmpty(title) ? platformFallback : title;
    }

    /// <summary>
    /// Parse a Spotify window title into artist and track.
    /// Format: "Artist - Track" or just "Track".
    /// </summary>
    public static (string Artist, string Track) ParseSpotifyTitle(string title)
    {
        var parts = title.Split(" - ", 2);
        if (parts.Length == 2)
        {
            return (parts[0].Trim(), parts[1].Trim());
        }

        return ("Spotify", title.Trim());
    }

    /// <summary>
    /// Normalize a string for loose matching: strip diacritics, lowercase, keep only letters/digits/spaces.
    /// </summary>
    public static string NormalizeForLooseMatch(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        string folded = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(folded.Length);
        bool lastWasSpace = false;

        foreach (var ch in folded)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }

        if (sb.Length > 0 && sb[sb.Length - 1] == ' ')
            sb.Length--;

        return sb.ToString();
    }
}

// ─── Platform Enum ───

/// <summary>
/// Known media platforms that V-Notch can detect and display.
/// </summary>
public enum MediaPlatform
{
    Unknown,
    Spotify,
    YouTube,
    SoundCloud,
    AppleMusic,
    Browser,
    Facebook,
    TikTok,
    Instagram,
    Twitter
}

/// <summary>
/// Extension methods for MediaPlatform enum.
/// </summary>
public static class MediaPlatformExtensions
{
    /// <summary>
    /// Get the user-facing display string for a platform.
    /// </summary>
    public static string ToDisplayString(this MediaPlatform platform) => platform switch
    {
        MediaPlatform.Spotify => "Spotify",
        MediaPlatform.YouTube => "YouTube",
        MediaPlatform.SoundCloud => "SoundCloud",
        MediaPlatform.AppleMusic => "Apple Music",
        MediaPlatform.Browser => "Browser",
        MediaPlatform.Facebook => "Facebook",
        MediaPlatform.TikTok => "TikTok",
        MediaPlatform.Instagram => "Instagram",
        MediaPlatform.Twitter => "Twitter",
        _ => ""
    };

    /// <summary>
    /// Parse a display string back to a MediaPlatform enum.
    /// </summary>
    public static MediaPlatform ParsePlatform(string? value)
    {
        if (string.IsNullOrEmpty(value)) return MediaPlatform.Unknown;

        return value.ToLowerInvariant() switch
        {
            "spotify" => MediaPlatform.Spotify,
            "youtube" => MediaPlatform.YouTube,
            "soundcloud" => MediaPlatform.SoundCloud,
            "apple music" => MediaPlatform.AppleMusic,
            "browser" => MediaPlatform.Browser,
            "facebook" => MediaPlatform.Facebook,
            "tiktok" => MediaPlatform.TikTok,
            "instagram" => MediaPlatform.Instagram,
            "twitter" => MediaPlatform.Twitter,
            _ => MediaPlatform.Unknown
        };
    }
}
