using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VNotch.Services;

/// <summary>
/// Fetches synced lyrics from LRClib (https://lrclib.net).
/// Free, no API key, no telemetry — only sends track name, artist, and duration.
/// </summary>
internal sealed class LyricsService : IDisposable
{
    private static readonly HttpClient _http = new()
    {
        BaseAddress = new Uri("https://lrclib.net"),
        Timeout = TimeSpan.FromSeconds(8)
    };

    static LyricsService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("V-Notch/1.7.0 (https://github.com/rainaku/V-Notch)");
    }

    private CancellationTokenSource? _cts;
    private string _lastFetchKey = "";

    /// <summary>
    /// Fetches synced lyrics for a Spotify track. Returns null if not found or not synced.
    /// </summary>
    public async Task<List<LyricLine>?> FetchSyncedLyricsAsync(string trackName, string artistName, int durationSeconds)
    {
        string fetchKey = $"{trackName}|{artistName}|{durationSeconds}";
        if (fetchKey == _lastFetchKey) return null; // Already fetched for this track

        // Cancel any in-flight request
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _lastFetchKey = fetchKey;

        try
        {
            string encodedTrack = Uri.EscapeDataString(trackName);
            string encodedArtist = Uri.EscapeDataString(artistName);
            string url = $"/api/get?track_name={encodedTrack}&artist_name={encodedArtist}&duration={durationSeconds}";

            RuntimeLog.Log("LYRICS", $"Fetching: {trackName} - {artistName} ({durationSeconds}s)");

            var response = await _http.GetAsync(url, token);

            if (!response.IsSuccessStatusCode)
            {
                RuntimeLog.Log("LYRICS", $"HTTP {(int)response.StatusCode} for '{trackName}'");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("syncedLyrics", out var syncedProp) || syncedProp.ValueKind == JsonValueKind.Null)
            {
                RuntimeLog.Log("LYRICS", $"No synced lyrics for '{trackName}'");
                return null;
            }

            string syncedLyrics = syncedProp.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(syncedLyrics)) return null;

            var lines = ParseLrc(syncedLyrics);
            RuntimeLog.Log("LYRICS", $"Got {lines.Count} synced lines for '{trackName}'");
            return lines.Count > 0 ? lines : null;
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

    /// <summary>
    /// Resets the last fetch key so the same track can be re-fetched.
    /// </summary>
    public void Reset()
    {
        _lastFetchKey = "";
        _cts?.Cancel();
    }

    /// <summary>
    /// Parses LRC format: [mm:ss.xx] text
    /// </summary>
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

            // Skip empty/instrumental lines
            if (string.IsNullOrWhiteSpace(text)) continue;

            if (TryParseTimestamp(timestamp, out var time))
            {
                lines.Add(new LyricLine(time, text));
            }
        }

        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }

    /// <summary>
    /// Parses timestamp in format mm:ss.xx or mm:ss.xxx
    /// </summary>
    private static bool TryParseTimestamp(string ts, out TimeSpan result)
    {
        result = TimeSpan.Zero;

        // Format: mm:ss.xx or mm:ss.xxx
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

        // Normalize to milliseconds
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

/// <summary>
/// A single line of synced lyrics with its timestamp.
/// </summary>
internal readonly record struct LyricLine(TimeSpan Time, string Text);
