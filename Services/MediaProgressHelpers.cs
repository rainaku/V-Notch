using System;
using VNotch.Models;

namespace VNotch.Services;

/// <summary>
/// Pure helpers used while rendering the progress bar / time text.
///
/// Extracted from <c>MainWindow.Progress.cs</c>. Nothing here touches WPF
/// elements or shared window state — callers pass a <see cref="MediaInfo"/> or
/// a <see cref="TimeSpan"/> in and get a computed value back.
/// </summary>
internal static class MediaProgressHelpers
{
    /// <summary>
    /// Correct a snapshot whose <see cref="MediaInfo.LastUpdated"/> is set in
    /// the future (some bridges stamp timestamps optimistically at startup).
    /// Without this, the progress engine's "snapshot-age" compensation
    /// would overshoot on the first frame.
    /// </summary>
    public static void NormalizeStartupSnapshotTimestamp(MediaInfo info)
    {
        if (!info.IsPlaying || info.Position <= TimeSpan.Zero)
        {
            return;
        }

        var updatedUtc = info.LastUpdated.ToUniversalTime();
        var nowUtc = DateTimeOffset.UtcNow;
        if (updatedUtc > nowUtc.AddMilliseconds(250))
        {
            info.LastUpdated = nowUtc;
        }
    }

    /// <summary>
    /// True for sources whose timeline is being bridged from a browser — i.e.
    /// YouTube, SoundCloud, generic "Browser" entries, and any session whose
    /// source-app identifier names a known browser executable. The progress
    /// engine uses this to relax its backward-jump / duration-change
    /// thresholds because browser bridges are noisier than native MTC apps.
    /// </summary>
    public static bool IsLikelyBrowserProgressSource(MediaInfo info)
    {
        if (string.Equals(info.MediaSource, "YouTube", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(info.YouTubeVideoId))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(info.SourceAppId) &&
            info.SourceAppId.Contains("YouTube", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(info.MediaSource, "SoundCloud", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(info.MediaSource, "Browser", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(info.SourceAppId) &&
            (info.SourceAppId.Contains("chrome", StringComparison.OrdinalIgnoreCase) ||
             info.SourceAppId.Contains("edge", StringComparison.OrdinalIgnoreCase) ||
             info.SourceAppId.Contains("msedge", StringComparison.OrdinalIgnoreCase) ||
             info.SourceAppId.Contains("firefox", StringComparison.OrdinalIgnoreCase) ||
             info.SourceAppId.Contains("brave", StringComparison.OrdinalIgnoreCase) ||
             info.SourceAppId.Contains("opera", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Formats a time span as <c>m:ss</c> below one hour and <c>h:mm:ss</c>
    /// above it.
    /// </summary>
    public static string FormatTime(TimeSpan time)
    {
        if (time.TotalHours >= 1)
        {
            return time.ToString(@"h\:mm\:ss");
        }
        return time.ToString(@"m\:ss");
    }
}
