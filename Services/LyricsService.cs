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
    private static readonly HttpClient _http = new()
    {
        BaseAddress = new Uri("https://lrclib.net"),
        Timeout = TimeSpan.FromSeconds(8)
    };

    // This is an unofficial desktop endpoint. Fail fast and always retain
    // LRCLIB as the fallback when Musixmatch rejects or changes it.
    private static readonly HttpClient _musixmatchHttp = new()
    {
        BaseAddress = new Uri("https://apic-desktop.musixmatch.com"),
        Timeout = TimeSpan.FromSeconds(5)
    };
    private static readonly SemaphoreSlim _musixmatchTokenLock = new(1, 1);
    private static string? _musixmatchToken;
    private static DateTime _musixmatchTokenExpiresUtc;

    static LyricsService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("V-Notch/1.7.0 (https://github.com/rainaku/V-Notch)");
        _musixmatchHttp.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
        _musixmatchHttp.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", "AWSELBCORS=0; AWSELB=0");
    }

    private CancellationTokenSource? _cts;
    private string _lastFetchKey = "";

    public async Task<List<LyricLine>?> FetchSyncedLyricsAsync(string trackName, string artistName, int durationSeconds)
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
            var musixmatch = await TryGetMusixmatchAsync(trackName, artistName, durationSeconds, token);
            if (musixmatch is { Count: > 0 }) return musixmatch;

            var exact = await TryGetExactAsync(trackName, artistName, durationSeconds, token);
            if (exact is { Count: > 0 }) return exact;

            // 2. Fuzzy search fallback — far more forgiving. Try the raw title
            //    first, then a cleaned version with the noisy suffixes removed.
            var searched = await TrySearchAsync(trackName, artistName, durationSeconds, token);
            if (searched is { Count: > 0 }) return searched;

            string cleanTrack = CleanTitle(trackName);
            string cleanArtist = CleanArtist(artistName);
            if (cleanTrack != trackName || cleanArtist != artistName)
            {
                var cleaned = await TrySearchAsync(cleanTrack, cleanArtist, durationSeconds, token);
                if (cleaned is { Count: > 0 }) return cleaned;
            }

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

    private static async Task<List<LyricLine>?> TryGetMusixmatchAsync(
        string trackName, string artistName, int durationSeconds, CancellationToken token)
    {
        try
        {
            string? userToken = await GetMusixmatchTokenAsync(token);
            if (string.IsNullOrEmpty(userToken)) return null;

            string url = "/ws/1.1/macro.subtitles.get?format=json" +
                         "&namespace=lyrics_richsynched&subtitle_format=mxm" +
                         "&app_id=web-desktop-app-v1.0" +
                         $"&usertoken={Uri.EscapeDataString(userToken)}" +
                         $"&q_artist={Uri.EscapeDataString(artistName)}" +
                         $"&q_artists={Uri.EscapeDataString(artistName)}" +
                         $"&q_track={Uri.EscapeDataString(trackName)}" +
                         $"&q_duration={durationSeconds}&f_subtitle_length={durationSeconds}";

            RuntimeLog.Log("LYRICS", $"Fetching (Musixmatch): {trackName} - {artistName}");
            var response = await _musixmatchHttp.GetAsync(url, token);
            if (!response.IsSuccessStatusCode)
            {
                RuntimeLog.Log("LYRICS", $"Musixmatch HTTP {(int)response.StatusCode}; falling back to LRCLIB");
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(token);
            using var doc = JsonDocument.Parse(json);
            if (!TryGetMusixmatchSubtitle(doc.RootElement, out string? subtitle)) return null;

            var lines = ParseMusixmatchLyrics(subtitle!);
            if (lines.Count > 0)
                RuntimeLog.Log("LYRICS", $"Got {lines.Count} synced lines (Musixmatch) for '{trackName}'");
            return lines.Count > 0 ? lines : null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            RuntimeLog.Log("LYRICS", $"Musixmatch unavailable ({ex.Message}); falling back to LRCLIB");
            return null;
        }
    }

    private static async Task<string?> GetMusixmatchTokenAsync(CancellationToken token)
    {
        if (!string.IsNullOrEmpty(_musixmatchToken) && DateTime.UtcNow < _musixmatchTokenExpiresUtc)
            return _musixmatchToken;

        await _musixmatchTokenLock.WaitAsync(token);
        try
        {
            if (!string.IsNullOrEmpty(_musixmatchToken) && DateTime.UtcNow < _musixmatchTokenExpiresUtc)
                return _musixmatchToken;

            var response = await _musixmatchHttp.GetAsync(
                "/ws/1.1/token.get?app_id=web-desktop-app-v1.0", token);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (!doc.RootElement.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("body", out var body) ||
                !body.TryGetProperty("user_token", out var tokenElement))
                return null;

            _musixmatchToken = tokenElement.GetString();
            _musixmatchTokenExpiresUtc = DateTime.UtcNow.AddMinutes(9);
            return _musixmatchToken;
        }
        finally
        {
            _musixmatchTokenLock.Release();
        }
    }

    private static bool TryGetMusixmatchSubtitle(JsonElement root, out string? subtitle)
    {
        subtitle = null;
        if (!root.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("body", out var body) ||
            !body.TryGetProperty("macro_calls", out var calls) ||
            !calls.TryGetProperty("track.subtitles.get", out var trackSubtitles) ||
            !trackSubtitles.TryGetProperty("message", out var subtitleMessage) ||
            !subtitleMessage.TryGetProperty("body", out var subtitleBody) ||
            !subtitleBody.TryGetProperty("subtitle_list", out var subtitleList) ||
            subtitleList.ValueKind != JsonValueKind.Array || subtitleList.GetArrayLength() == 0)
            return false;

        var first = subtitleList[0];
        if (!first.TryGetProperty("subtitle", out var subtitleElement) ||
            !subtitleElement.TryGetProperty("subtitle_body", out var subtitleBodyElement))
            return false;

        subtitle = subtitleBodyElement.GetString();
        return !string.IsNullOrWhiteSpace(subtitle);
    }

    private static List<LyricLine> ParseMusixmatchLyrics(string lyrics)
    {
        var lines = new List<LyricLine>();
        using var doc = JsonDocument.Parse(lyrics);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return lines;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("time", out var time) ||
                !time.TryGetProperty("minutes", out var minutes) ||
                !time.TryGetProperty("seconds", out var seconds) ||
                !minutes.TryGetInt32(out int minuteValue) ||
                !seconds.TryGetInt32(out int secondValue))
                continue;

            int hundredths = time.TryGetProperty("hundredths", out var hundredthsElement) &&
                             hundredthsElement.TryGetInt32(out int value)
                ? value : 0;
            string text = item.TryGetProperty("text", out var textElement) ? textElement.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(text)) continue;

            lines.Add(new LyricLine(
                new TimeSpan(0, 0, minuteValue, secondValue, hundredths * 10), text));
        }

        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }

    private async Task<List<LyricLine>?> TryGetExactAsync(string trackName, string artistName, int durationSeconds, CancellationToken token)
    {
        string url = $"/api/get?track_name={Uri.EscapeDataString(trackName)}" +
                     $"&artist_name={Uri.EscapeDataString(artistName)}&duration={durationSeconds}";

        RuntimeLog.Log("LYRICS", $"Fetching (exact): {trackName} - {artistName} ({durationSeconds}s)");

        var response = await _http.GetAsync(url, token);
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

        var response = await _http.GetAsync(url, token);
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
