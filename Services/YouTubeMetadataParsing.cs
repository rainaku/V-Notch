using System.Text.Json;
using System.Text.RegularExpressions;

namespace VNotch.Services;

/// <summary>
/// Pure YouTube metadata parsing helpers extracted from <see cref="MediaMetadataLookupService"/>.
/// Deterministic from their arguments (a parsed <see cref="JsonElement"/> or a string), so they are
/// directly unit-testable without any HTTP access.
/// </summary>
internal static class YouTubeMetadataParsing
{
    // Preference order for YouTube Data API thumbnail sizes (best first).
    private static readonly string[] ThumbnailPreference = { "maxres", "standard", "high", "medium", "default" };

    /// <summary>Picks the best available thumbnail URL from a Data API <c>thumbnails</c> object, or null.</summary>
    public static string? PickBestThumbnail(JsonElement thumbnails)
    {
        foreach (var key in ThumbnailPreference)
        {
            if (thumbnails.TryGetProperty(key, out var el) &&
                el.TryGetProperty("url", out var urlEl))
            {
                string? value = urlEl.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }
        return null;
    }

    /// <summary>True when a YouTube Data API error body indicates the daily quota / rate limit was hit.</summary>
    public static bool LooksLikeQuotaExceeded(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;
        return body.Contains("quotaExceeded", StringComparison.Ordinal) ||
               body.Contains("dailyLimitExceeded", StringComparison.Ordinal) ||
               body.Contains("rateLimitExceeded", StringComparison.Ordinal);
    }

    /// <summary>Parses an ISO-8601 duration (e.g. <c>PT3M20S</c>) into a <see cref="TimeSpan"/>; <see cref="TimeSpan.Zero"/> on failure.</summary>
    public static TimeSpan ParseIso8601Duration(string iso)
    {
        try
        {
            var match = Regex.Match(iso, @"PT(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?");
            if (!match.Success) return TimeSpan.Zero;

            int hours = match.Groups[1].Success ? int.Parse(match.Groups[1].Value) : 0;
            int minutes = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;
            int seconds = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;

            return new TimeSpan(hours, minutes, seconds);
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }
}
