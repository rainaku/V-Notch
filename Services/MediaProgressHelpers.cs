using System;
using VNotch.Models;

namespace VNotch.Services;
internal static class MediaProgressHelpers
{
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
public static string FormatTime(TimeSpan time)
    {
        if (time.TotalHours >= 1)
        {
            return time.ToString(@"h\:mm\:ss");
        }
        return time.ToString(@"m\:ss");
    }
}
