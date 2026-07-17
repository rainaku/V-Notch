using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VNotch.Services;

internal sealed class LyricsService : IDisposable
{
    private static readonly HttpClient _lrclibHttp = new()
    {
        BaseAddress = new Uri("https://lrclib.net"),
        Timeout = TimeSpan.FromSeconds(8)
    };

    private static readonly HttpClient _lrcMuxHttp = new()
    {
        BaseAddress = new Uri("https://api.lrcmux.dev"),
        Timeout = TimeSpan.FromSeconds(12)
    };

    static LyricsService()
    {
        const string userAgent = "V-Notch/1.8.0 (https://github.com/rainaku/V-Notch)";
        _lrclibHttp.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        _lrcMuxHttp.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    }

    private CancellationTokenSource? _cts;
    private string _lastFetchKey = "";

    public async Task<LyricsResult?> FetchSyncedLyricsAsync(string trackName, string artistName, int durationSeconds)
    {
        string fetchKey = $"{trackName}|{artistName}|{durationSeconds}";
        if (fetchKey == _lastFetchKey) return null;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _lastFetchKey = fetchKey;

        try
        {
            // 1. Exact lookup — fastest, but lrclib requires track/artist/duration
            //    to match closely. Spotify titles often carry suffixes
            //    ("- Remastered", "(feat. X)") or a slightly different duration,
            //    so this misses plenty of songs that DO have lyrics.
            var exact = await TryGetExactAsync(trackName, artistName, durationSeconds, token);
            if (exact is { Count: > 0 }) return new LyricsResult(exact, "LRCLIB");

            // 2. Fuzzy search fallback — far more forgiving. Try the raw title
            //    first, then a cleaned version with the noisy suffixes removed.
            var searched = await TrySearchAsync(trackName, artistName, durationSeconds, token);
            if (searched is { Count: > 0 }) return new LyricsResult(searched, "LRCLIB");

            string cleanTrack = CleanTitle(trackName);
            string cleanArtist = CleanArtist(artistName);
            if (cleanTrack != trackName || cleanArtist != artistName)
            {
                var cleaned = await TrySearchAsync(cleanTrack, cleanArtist, durationSeconds, token);
                if (cleaned is { Count: > 0 }) return new LyricsResult(cleaned, "LRCLIB");
            }

            // LRCLIB has already been tried, so ask lrc mux only for its other
            // providers. It may return either word- or line-synchronised lyrics.
            var aggregated = await TryLrcMuxAsync(trackName, artistName, durationSeconds, token);
            if (aggregated != null) return aggregated;

            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("LYRICS", $"Error: {ex.Message}");
            return null;
        }
    }

    private async Task<List<LyricLine>?> TryGetExactAsync(string trackName, string artistName, int durationSeconds, CancellationToken token)
    {
        string url = $"/api/get?track_name={Uri.EscapeDataString(trackName)}" +
                     $"&artist_name={Uri.EscapeDataString(artistName)}&duration={durationSeconds}";

        RuntimeLog.Log("LYRICS", $"Fetching (exact): {trackName} - {artistName} ({durationSeconds}s)");

        var response = await _lrclibHttp.GetAsync(url, token);
        if (!response.IsSuccessStatusCode)
        {
            RuntimeLog.Log("LYRICS", $"Exact HTTP {(int)response.StatusCode} for '{trackName}'");
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(token);
        using var doc = JsonDocument.Parse(json);
        var lines = ExtractSyncedLines(doc.RootElement);
        if (lines is { Count: > 0 })
            RuntimeLog.Log("LYRICS", $"Got {lines.Count} synced lines (exact) for '{trackName}'");
        return lines;
    }

    private async Task<List<LyricLine>?> TrySearchAsync(string trackName, string artistName, int durationSeconds, CancellationToken token)
    {
        string url = $"/api/search?track_name={Uri.EscapeDataString(trackName)}" +
                     $"&artist_name={Uri.EscapeDataString(artistName)}";

        RuntimeLog.Log("LYRICS", $"Fetching (search): {trackName} - {artistName}");

        var response = await _lrclibHttp.GetAsync(url, token);
        if (!response.IsSuccessStatusCode)
        {
            RuntimeLog.Log("LYRICS", $"Search HTTP {(int)response.StatusCode} for '{trackName}'");
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(token);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        // Pick the candidate that actually has synced lyrics and whose duration is
        // closest to what's playing — guards against matching the wrong edit.
        JsonElement best = default;
        bool found = false;
        int bestDelta = int.MaxValue;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("syncedLyrics", out var sp) || sp.ValueKind == JsonValueKind.Null)
                continue;
            if (string.IsNullOrWhiteSpace(sp.GetString()))
                continue;

            int dur = item.TryGetProperty("duration", out var dp) && dp.ValueKind == JsonValueKind.Number
                ? (int)Math.Round(dp.GetDouble())
                : 0;
            int delta = dur > 0 ? Math.Abs(dur - durationSeconds) : 0;

            if (!found || delta < bestDelta)
            {
                best = item;
                bestDelta = delta;
                found = true;
            }
        }

        if (!found) return null;

        var lines = ExtractSyncedLines(best);
        if (lines is { Count: > 0 })
            RuntimeLog.Log("LYRICS", $"Got {lines.Count} synced lines (search, Δ{bestDelta}s) for '{trackName}'");
        return lines;
    }

    private async Task<LyricsResult?> TryLrcMuxAsync(
        string trackName,
        string artistName,
        int durationSeconds,
        CancellationToken token)
    {
        string url = $"/get?title={Uri.EscapeDataString(trackName)}" +
                     $"&artist={Uri.EscapeDataString(artistName)}" +
                     $"&duration={durationSeconds}" +
                     "&level=word&format=json&sources=%21lrclib";

        RuntimeLog.Log("LYRICS", $"Fetching (lrc mux): {trackName} - {artistName}");

        using var response = await _lrcMuxHttp.GetAsync(url, token);
        if (!response.IsSuccessStatusCode)
        {
            RuntimeLog.Log("LYRICS", $"lrc mux HTTP {(int)response.StatusCode} for '{trackName}'");
            return null;
        }

        string json = await response.Content.ReadAsStringAsync(token);
        var result = ParseLrcMuxResult(json);
        if (result is { Lines.Count: > 0 })
            RuntimeLog.Log("LYRICS", $"Got {result.Lines.Count} synced lines from {result.Provider} for '{trackName}'");
        return result;
    }

    internal static LyricsResult? ParseLrcMuxResult(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        if (!root.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object)
            return null;

        string syncLevel = meta.TryGetProperty("level", out var levelProp)
            ? levelProp.GetString() ?? ""
            : "";
        if (!syncLevel.Equals("word", StringComparison.OrdinalIgnoreCase) &&
            !syncLevel.Equals("line", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string provider = "lrc mux";
        if (meta.TryGetProperty("source", out var source) &&
            source.ValueKind == JsonValueKind.Object &&
            source.TryGetProperty("name", out var nameProp) &&
            !string.IsNullOrWhiteSpace(nameProp.GetString()))
        {
            provider = $"{nameProp.GetString()!.Trim()} via lrc mux";
        }

        if (!root.TryGetProperty("lines", out var linesProp) || linesProp.ValueKind != JsonValueKind.Array)
            return null;

        var lines = new List<LyricLine>();
        foreach (var item in linesProp.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("text", out var textProp) ||
                !item.TryGetProperty("start", out var startProp) ||
                startProp.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            string text = textProp.GetString()?.Trim() ?? "";
            if (text.Length == 0 ||
                !startProp.TryGetInt64(out long startMilliseconds) ||
                startMilliseconds < 0)
            {
                continue;
            }

            lines.Add(new LyricLine(TimeSpan.FromMilliseconds(startMilliseconds), text));
        }

        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines.Count > 0 ? new LyricsResult(lines, provider) : null;
    }

    private static List<LyricLine>? ExtractSyncedLines(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty("syncedLyrics", out var syncedProp) || syncedProp.ValueKind == JsonValueKind.Null)
            return null;

        string syncedLyrics = syncedProp.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(syncedLyrics)) return null;

        var lines = ParseLrc(syncedLyrics);
        return lines.Count > 0 ? lines : null;
    }

    private static string CleanTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return title;

        // Drop bracketed/parenthesised extras and "- Remastered/Live/Version" tails
        // that Spotify appends but lrclib's catalogue usually omits.
        string s = System.Text.RegularExpressions.Regex.Replace(title, @"\s*[\(\[][^\)\]]*[\)\]]", "");
        int dash = s.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) s = s[..dash];
        return s.Trim().Length == 0 ? title.Trim() : s.Trim();
    }

    private static string CleanArtist(string artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return artist;

        // Use only the primary artist (before "feat.", "&", "," , "x").
        string s = artist;
        foreach (var sep in new[] { " feat.", " ft.", " featuring", " & ", ", ", " x " })
        {
            int idx = s.IndexOf(sep, StringComparison.OrdinalIgnoreCase);
            if (idx > 0) s = s[..idx];
        }
        return s.Trim().Length == 0 ? artist.Trim() : s.Trim();
    }

    public void Reset()
    {
        _lastFetchKey = "";
        _cts?.Cancel();
    }

    private static List<LyricLine> ParseLrc(string lrc)
    {
        var lines = new List<LyricLine>();
        foreach (var rawLine in lrc.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length < 10 || line[0] != '[') continue;

            int closeBracket = line.IndexOf(']');
            if (closeBracket < 5) continue;

            string timestamp = line[1..closeBracket];
            string text = line[(closeBracket + 1)..].Trim();

            if (string.IsNullOrWhiteSpace(text)) continue;

            if (TryParseTimestamp(timestamp, out var time))
            {
                lines.Add(new LyricLine(time, text));
            }
        }

        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }

    private static bool TryParseTimestamp(string ts, out TimeSpan result)
    {
        result = TimeSpan.Zero;

        int colonIdx = ts.IndexOf(':');
        int dotIdx = ts.IndexOf('.');
        if (colonIdx < 1 || dotIdx < colonIdx) return false;

        if (!int.TryParse(ts[..colonIdx], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes))
            return false;
        if (!int.TryParse(ts[(colonIdx + 1)..dotIdx], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds))
            return false;

        string fracStr = ts[(dotIdx + 1)..];
        if (!int.TryParse(fracStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int frac))
            return false;

        int ms = fracStr.Length switch
        {
            1 => frac * 100,
            2 => frac * 10,
            3 => frac,
            _ => frac
        };

        result = new TimeSpan(0, 0, minutes, seconds, ms);
        return true;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}

internal readonly record struct LyricLine(TimeSpan Time, string Text);

internal sealed record LyricsResult(List<LyricLine> Lines, string Provider);
