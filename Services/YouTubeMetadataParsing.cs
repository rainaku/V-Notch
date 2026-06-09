using System.Text.Json;
using System.Text.RegularExpressions;

namespace VNotch.Services;

internal static class YouTubeMetadataParsing
{
    private static readonly string[] ThumbnailPreference = { "maxres", "standard", "high", "medium", "default" };

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

    public static bool LooksLikeQuotaExceeded(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;
        return body.Contains("quotaExceeded", StringComparison.Ordinal) ||
               body.Contains("dailyLimitExceeded", StringComparison.Ordinal) ||
               body.Contains("rateLimitExceeded", StringComparison.Ordinal);
    }

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
