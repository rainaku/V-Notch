using VNotch.Models;

namespace VNotch.Services;

/// <summary>
/// Pure media-source classification extracted from <see cref="MediaDetectionService"/>. Each method
/// derives a <see cref="MediaPlatform"/> from app-id / metadata / title hints and writes the result
/// onto the supplied <see cref="MediaInfo"/>. No instance state, clock, or Win32 access is involved,
/// so the rules are fully unit-testable.
///
/// Canonical source names are emitted via <see cref="MediaPlatformExtensions.ToDisplayString"/> rather
/// than string literals; the produced values are identical to the previous hard-coded strings.
/// </summary>
internal static class MediaSourceClassifier
{
    /// <summary>Maps a session source-app id to a media source (Spotify / YouTube / Apple Music / Browser).</summary>
    public static void ApplyFromAppId(MediaInfo info, string sessionSourceApp)
    {
        if (string.IsNullOrEmpty(sessionSourceApp)) return;

        if (sessionSourceApp.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
        {
            info.MediaSource = MediaPlatform.Spotify.ToDisplayString();
            info.IsSpotifyPlaying = true;
            info.IsSpotifyRunning = true;
        }
        else if (sessionSourceApp.Contains("YouTube", StringComparison.OrdinalIgnoreCase))
        {
            info.MediaSource = MediaPlatform.YouTube.ToDisplayString();
            info.IsYouTubeRunning = true;
        }
        else if (PlatformDetector.IsBrowserApp(sessionSourceApp))
        {
            info.MediaSource = MediaPlatform.Browser.ToDisplayString();
        }
        else if (sessionSourceApp.Contains("Music", StringComparison.OrdinalIgnoreCase) ||
                 sessionSourceApp.Contains("Apple", StringComparison.OrdinalIgnoreCase) ||
                 sessionSourceApp.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase))
        {
            info.MediaSource = MediaPlatform.AppleMusic.ToDisplayString();
            info.IsAppleMusicRunning = true;
        }
        else
        {
            info.MediaSource = MediaPlatform.Browser.ToDisplayString();
        }
    }

    /// <summary>
    /// Refines an unresolved Browser source into YouTube / Apple Music / SoundCloud using lower-cased
    /// track metadata hints. No-op when the source is already resolved to something other than Browser.
    /// </summary>
    public static void RefineFromMetadata(MediaInfo info, string lowerTitle, string lowerArtist, string lowerAlbum)
    {
        if (info.MediaSource != MediaPlatform.Browser.ToDisplayString() && !string.IsNullOrEmpty(info.MediaSource)) return;

        bool isYouTube = lowerArtist.Contains("youtube") ||
                         lowerTitle.Contains("youtube") ||
                         lowerTitle.EndsWith("- youtube") ||
                         lowerTitle.EndsWith("– youtube") ||
                         lowerAlbum.Contains("youtube");

        if (isYouTube)
        {
            info.MediaSource = MediaPlatform.YouTube.ToDisplayString();
            info.IsYouTubeRunning = true;
        }
        else if (lowerArtist.Contains("apple music") || lowerTitle.Contains("apple music") || lowerAlbum.Contains("apple music") || lowerAlbum.Contains("music.apple.com"))
        {
            info.MediaSource = MediaPlatform.AppleMusic.ToDisplayString();
            info.IsAppleMusicRunning = true;
        }
        else if (lowerArtist.Contains("soundcloud") || lowerTitle.Contains("soundcloud") || lowerAlbum.Contains("soundcloud"))
        {
            info.MediaSource = MediaPlatform.SoundCloud.ToDisplayString();
            info.IsSoundCloudRunning = true;
        }
    }

    /// <summary>
    /// Detects placeholder/junk SMTC titles (app names, ads, empty). Returns true when the caller
    /// should abort the pass; for a YouTube source it first clears the track and tags the artist.
    /// </summary>
    public static bool TryHandleJunkTitle(MediaInfo info, string sessionTitle, string sessionArtist)
    {
        string lowerTitle = sessionTitle.ToLower();
        string lowerArtist = sessionArtist.ToLower();

        bool isJunkTitle = string.IsNullOrEmpty(sessionTitle) ||
                           lowerTitle == "spotify" ||
                           lowerTitle == "advertisement" ||
                           lowerTitle == "windows media player" ||
                           lowerTitle == "spotify free" ||
                           lowerTitle == "spotify premium" ||
                           lowerTitle == "chrome" ||
                           lowerTitle == "edge" ||
                           lowerTitle == "brave" ||
                           lowerTitle == "opera" ||
                           lowerTitle == "firefox" ||
                           (lowerTitle == "youtube" && (string.IsNullOrEmpty(sessionArtist) || lowerArtist == "youtube"));

        if (!isJunkTitle) return false;

        if (info.MediaSource == MediaPlatform.YouTube.ToDisplayString())
        {
            info.CurrentTrack = "";
            info.CurrentArtist = MediaPlatform.YouTube.ToDisplayString();
        }

        return true;
    }
}
