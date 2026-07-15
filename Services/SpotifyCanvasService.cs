using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace VNotch.Services;

/// <summary>
/// Resolves a Spotify track from its display metadata and retrieves the Canvas
/// URL exposed by PaxSenix's Spotify Canvas API. All failures intentionally
/// return null so Canvas remains an optional enhancement to the lyrics view.
/// </summary>
internal sealed class SpotifyCanvasService : IDisposable
{
    private static readonly Uri DefaultApiBaseUri = new("https://api.paxsenix.org/");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(12);
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private const int MaxCacheEntries = 128;

    private readonly HttpClient _http;
    private readonly Uri _apiBaseUri;
    private readonly bool _ownsHttpClient;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public SpotifyCanvasService()
        : this(CreateHttpClient(), DefaultApiBaseUri, ownsHttpClient: true)
    {
    }

    internal SpotifyCanvasService(HttpClient http, Uri? apiBaseUri = null)
        : this(http, apiBaseUri ?? DefaultApiBaseUri, ownsHttpClient: false)
    {
    }

    private SpotifyCanvasService(HttpClient http, Uri apiBaseUri, bool ownsHttpClient)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _apiBaseUri = EnsureTrailingSlash(apiBaseUri);
        _ownsHttpClient = ownsHttpClient;
    }

    public async Task<Uri?> FetchCanvasAsync(
        string trackName,
        string artistName,
        TimeSpan duration,
        string? configuredApiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(trackName) || cancellationToken.IsCancellationRequested)
            return null;

        string apiKey = ResolveApiKey(configuredApiKey);
        if (string.IsNullOrWhiteSpace(apiKey))
            return null;

        string cacheKey = BuildCacheKey(trackName, artistName, duration);
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            if (cached.ExpiresAtUtc > DateTimeOffset.UtcNow)
                return cached.CanvasUri;

            _cache.TryRemove(cacheKey, out _);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RequestTimeout);

        try
        {
            string? trackId = await ResolveTrackIdAsync(
                trackName, artistName, duration, apiKey, timeoutCts.Token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(trackId))
                return null;

            Uri canvasEndpoint = BuildCanvasEndpoint(trackId);
            string? canvasJson = await GetJsonAsync(canvasEndpoint, apiKey, timeoutCts.Token).ConfigureAwait(false);
            if (canvasJson == null)
                return null;

            Uri? canvasUri = ParseCanvasUri(canvasJson);
            if (canvasUri != null)
            {
                TrimCacheIfNeeded();
                _cache[cacheKey] = new CacheEntry(canvasUri, DateTimeOffset.UtcNow + CacheLifetime);
            }

            return canvasUri;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("SPOTIFY-CANVAS", $"Canvas lookup failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public void ClearCache() => _cache.Clear();

    private async Task<string?> ResolveTrackIdAsync(
        string trackName,
        string artistName,
        TimeSpan duration,
        string apiKey,
        CancellationToken token)
    {
        string query = string.IsNullOrWhiteSpace(artistName)
            ? trackName.Trim()
            : $"{trackName.Trim()} {artistName.Trim()}";
        var endpoint = new Uri(_apiBaseUri, $"spotify/search?q={Uri.EscapeDataString(query)}");
        string? json = await GetJsonAsync(endpoint, apiKey, token).ConfigureAwait(false);
        return json == null ? null : ParseTrackId(json, trackName, artistName, duration);
    }

    private async Task<string?> GetJsonAsync(Uri endpoint, string apiKey, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            RuntimeLog.Debug("SPOTIFY-CANVAS", () =>
                $"API returned HTTP {(int)response.StatusCode} for {endpoint.AbsolutePath}");
            return null;
        }

        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            return null;

        byte[] bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        if (bytes.Length == 0 || bytes.Length > MaxResponseBytes)
            return null;

        return Encoding.UTF8.GetString(bytes);
    }

    private Uri BuildCanvasEndpoint(string trackId)
    {
        string? overrideValue = Environment.GetEnvironmentVariable("VNOTCH_SPOTIFY_CANVAS_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(overrideValue))
        {
            string expanded = overrideValue.Contains("{trackId}", StringComparison.Ordinal)
                ? overrideValue.Replace("{trackId}", Uri.EscapeDataString(trackId), StringComparison.Ordinal)
                : AppendQuery(overrideValue, "trackId", trackId);

            if (Uri.TryCreate(expanded, UriKind.Absolute, out var customEndpoint) && IsSafeApiEndpoint(customEndpoint))
                return customEndpoint;
        }

        return new Uri(_apiBaseUri, $"spotify/canvas?id={Uri.EscapeDataString(trackId)}");
    }

    private static bool IsSafeApiEndpoint(Uri endpoint) =>
        endpoint.Scheme == Uri.UriSchemeHttps ||
        (endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback);

    private static string AppendQuery(string endpoint, string name, string value)
    {
        string separator = endpoint.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{endpoint}{separator}{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}";
    }

    internal static string? ParseTrackId(
        string json,
        string expectedTrack,
        string expectedArtist,
        TimeSpan expectedDuration)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            var candidates = new List<TrackCandidate>();
            CollectTrackCandidates(document.RootElement, candidates, depth: 0);

            TrackCandidate? best = null;
            int bestScore = int.MinValue;
            foreach (var candidate in candidates)
            {
                int titleScore = MatchScore(candidate.Title, expectedTrack, exact: 100, contains: 72);
                if (titleScore < 72)
                    continue;

                int score = titleScore;
                if (!string.IsNullOrWhiteSpace(expectedArtist))
                    score += MatchScore(candidate.Artist, expectedArtist, exact: 35, contains: 22);

                if (candidate.Duration > TimeSpan.Zero && expectedDuration > TimeSpan.Zero)
                {
                    double delta = Math.Abs((candidate.Duration - expectedDuration).TotalSeconds);
                    score += delta <= 4 ? 12 : delta <= 12 ? 5 : delta > 45 ? -15 : 0;
                }

                if (score > bestScore)
                {
                    best = candidate;
                    bestScore = score;
                }
            }

            return best?.Id;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void CollectTrackCandidates(JsonElement element, List<TrackCandidate> candidates, int depth)
    {
        if (depth > 24)
            return;

        if (element.ValueKind == JsonValueKind.Object)
        {
            string? id = GetTrackId(element);
            string? title = GetDirectString(element, "trackName", "track_name", "title", "name");
            if (id != null && !string.IsNullOrWhiteSpace(title))
            {
                string artist = GetArtistName(element) ?? "";
                TimeSpan duration = GetDuration(element);
                candidates.Add(new TrackCandidate(id, title, artist, duration));
            }

            foreach (var property in element.EnumerateObject())
                CollectTrackCandidates(property.Value, candidates, depth + 1);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectTrackCandidates(item, candidates, depth + 1);
        }
    }

    private static string? GetTrackId(JsonElement element)
    {
        foreach (string propertyName in new[] { "trackUri", "track_uri", "uri", "url", "externalUrl" })
        {
            string? value = GetDirectString(element, propertyName);
            string? parsed = ExtractTrackId(value);
            if (parsed != null)
                return parsed;
        }

        foreach (string propertyName in new[] { "trackId", "track_id", "spotifyId", "spotify_id", "id" })
        {
            string? value = GetDirectString(element, propertyName);
            if (IsSpotifyId(value))
                return value;
        }

        return null;
    }

    private static string? ExtractTrackId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        const string uriPrefix = "spotify:track:";
        int uriIndex = value.IndexOf(uriPrefix, StringComparison.OrdinalIgnoreCase);
        if (uriIndex >= 0)
        {
            string possibleId = value.Substring(uriIndex + uriPrefix.Length).Split('?', '/', '&')[0];
            if (IsSpotifyId(possibleId))
                return possibleId;
        }

        const string pathPrefix = "/track/";
        int pathIndex = value.IndexOf(pathPrefix, StringComparison.OrdinalIgnoreCase);
        if (pathIndex >= 0)
        {
            string possibleId = value.Substring(pathIndex + pathPrefix.Length).Split('?', '/', '&')[0];
            if (IsSpotifyId(possibleId))
                return possibleId;
        }

        return IsSpotifyId(value) ? value : null;
    }

    private static bool IsSpotifyId(string? value) =>
        value is { Length: 22 } && value.All(char.IsLetterOrDigit);

    private static string? GetArtistName(JsonElement element)
    {
        string? direct = GetDirectString(element, "artistName", "artist_name", "author", "subtitle");
        if (!string.IsNullOrWhiteSpace(direct))
            return direct;

        if (TryGetProperty(element, "artist", out var artist))
        {
            if (artist.ValueKind == JsonValueKind.String)
                return artist.GetString();
            if (artist.ValueKind == JsonValueKind.Object)
                return GetDirectString(artist, "name", "artistName", "artist_name");
        }

        if (TryGetProperty(element, "artists", out var artists))
        {
            if (artists.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in artists.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                        return item.GetString();
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        string? name = GetDirectString(item, "name", "artistName", "artist_name");
                        if (!string.IsNullOrWhiteSpace(name))
                            return name;
                    }
                }
            }
            else if (artists.ValueKind == JsonValueKind.Object)
            {
                string? name = FindFirstNamedValue(artists, depth: 0);
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }
        }

        return null;
    }

    private static string? FindFirstNamedValue(JsonElement element, int depth)
    {
        if (depth > 5)
            return null;

        if (element.ValueKind == JsonValueKind.Object)
        {
            string? direct = GetDirectString(element, "name", "artistName", "artist_name");
            if (!string.IsNullOrWhiteSpace(direct))
                return direct;
            foreach (var property in element.EnumerateObject())
            {
                string? nested = FindFirstNamedValue(property.Value, depth + 1);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                string? nested = FindFirstNamedValue(item, depth + 1);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
        }

        return null;
    }

    private static TimeSpan GetDuration(JsonElement element)
    {
        foreach (string name in new[] { "durationMs", "duration_ms", "durationMillis", "duration_millis" })
        {
            if (TryGetNumber(element, name, out double milliseconds) && milliseconds > 0)
                return TimeSpan.FromMilliseconds(milliseconds);
        }

        foreach (string name in new[] { "duration", "durationSeconds", "duration_seconds" })
        {
            if (TryGetNumber(element, name, out double seconds) && seconds > 0)
                return seconds > 10_000 ? TimeSpan.FromMilliseconds(seconds) : TimeSpan.FromSeconds(seconds);
        }

        return TimeSpan.Zero;
    }

    private static bool TryGetNumber(JsonElement element, string name, out double value)
    {
        value = 0;
        if (!TryGetProperty(element, name, out var property))
            return false;
        if (property.ValueKind == JsonValueKind.Number)
            return property.TryGetDouble(out value);
        return property.ValueKind == JsonValueKind.String &&
               double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    internal static Uri? ParseCanvasUri(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            return FindCanvasUri(document.RootElement, depth: 0);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Uri? FindCanvasUri(JsonElement element, int depth)
    {
        if (depth > 24)
            return null;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String &&
                    IsCanvasPropertyName(property.Name) &&
                    TryCreateCanvasUri(property.Value.GetString(), out var preferred))
                {
                    return preferred;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                Uri? nested = FindCanvasUri(property.Value, depth + 1);
                if (nested != null)
                    return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                Uri? nested = FindCanvasUri(item, depth + 1);
                if (nested != null)
                    return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.String &&
                 TryCreateCanvasUri(element.GetString(), out var direct))
        {
            return direct;
        }

        return null;
    }

    private static bool IsCanvasPropertyName(string name) =>
        name.Contains("canvas", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("videoUrl", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("video_url", StringComparison.OrdinalIgnoreCase);

    private static bool TryCreateCanvasUri(string? value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate) || candidate.Scheme != Uri.UriSchemeHttps)
            return false;

        bool trustedHost = candidate.Host.Equals("scdn.co", StringComparison.OrdinalIgnoreCase) ||
                           candidate.Host.EndsWith(".scdn.co", StringComparison.OrdinalIgnoreCase);
        bool isMp4 = candidate.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
        if (!trustedHost || !isMp4)
            return false;

        uri = candidate;
        return true;
    }

    private static string? GetDirectString(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (TryGetProperty(element, name, out var property) && property.ValueKind == JsonValueKind.String)
                return property.GetString();
        }
        return null;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static int MatchScore(string? candidate, string expected, int exact, int contains)
    {
        string normalizedCandidate = NormalizeForMatch(candidate);
        string normalizedExpected = NormalizeForMatch(expected);
        if (normalizedCandidate.Length == 0 || normalizedExpected.Length == 0)
            return 0;
        if (normalizedCandidate == normalizedExpected)
            return exact;
        if (normalizedCandidate.Contains(normalizedExpected, StringComparison.Ordinal) ||
            normalizedExpected.Contains(normalizedCandidate, StringComparison.Ordinal))
            return contains;
        return 0;
    }

    private static string NormalizeForMatch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        string decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        bool pendingSpace = false;
        foreach (char ch in decomposed)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingSpace && builder.Length > 0)
                    builder.Append(' ');
                builder.Append(char.ToLowerInvariant(ch));
                pendingSpace = false;
            }
            else
            {
                pendingSpace = true;
            }
        }
        return builder.ToString();
    }

    private static string BuildCacheKey(string track, string artist, TimeSpan duration) =>
        $"{NormalizeForMatch(track)}|{NormalizeForMatch(artist)}|{Math.Round(duration.TotalSeconds)}";

    private static string ResolveApiKey(string? configuredApiKey) =>
        !string.IsNullOrWhiteSpace(configuredApiKey)
            ? configuredApiKey.Trim()
            : (Environment.GetEnvironmentVariable("PAXSENIX_API_KEY") ??
               Environment.GetEnvironmentVariable("VNOTCH_SPOTIFY_CANVAS_API_KEY") ?? "").Trim();

    private void TrimCacheIfNeeded()
    {
        if (_cache.Count < MaxCacheEntries)
            return;

        foreach (var entry in _cache.OrderBy(pair => pair.Value.ExpiresAtUtc).Take(_cache.Count - MaxCacheEntries + 1))
            _cache.TryRemove(entry.Key, out _);
    }

    private static Uri EnsureTrailingSlash(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
            throw new ArgumentException("The API base URI must be absolute.", nameof(uri));
        return uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? uri : new Uri(uri.AbsoluteUri + "/");
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("V-Notch/1.8 SpotifyCanvas");
        return client;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }

    private sealed record CacheEntry(Uri CanvasUri, DateTimeOffset ExpiresAtUtc);
    private sealed record TrackCandidate(string Id, string Title, string Artist, TimeSpan Duration);
}
