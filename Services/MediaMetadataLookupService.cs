using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VNotch.Services;

public sealed class MediaMetadataLookupService : IMediaMetadataLookupService
{
    private static readonly HttpClient _httpClient = new();

    private static readonly HashSet<string> _soundCloudReservedUsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "search", "charts", "discover", "stream", "you", "terms", "privacy", "mobile", "upload", "signin",
        "login", "settings", "jobs", "blog", "developers", "pages", "playlists", "stations", "likes", "messages",
        "pro", "for-artists", "popular", "tracks", "explore", "featured"
    };

    private static readonly HashSet<string> _soundCloudReservedTrackSlugs = new(StringComparer.OrdinalIgnoreCase)
    {
        "sets", "tracks", "likes", "reposts", "spotlight", "albums", "following", "followers"
    };

    static MediaMetadataLookupService()
    {
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }
    }

    public async Task<YouTubeLookupResult?> TryGetYouTubeVideoIdWithInfoAsync(string title, string artist = "", CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(title))
                return null;

            string cleanTitle = title.Trim();

            // ─── Step 1: Check if title itself is a video ID (11 chars, base64url) ───
            if (cleanTitle.Length == 11 && Regex.IsMatch(cleanTitle, "^[a-zA-Z0-9_-]{11}$"))
            {
                var validated = await ResolveVideoIdAsync(cleanTitle, ct);
                if (validated != null)
                    return validated;
            }

            // ─── Step 2: Extract video ID from URL if title contains a YouTube URL ───
            var urlMatch = Regex.Match(cleanTitle, @"(?:youtube\.com/watch\?.*v=|youtu\.be/|music\.youtube\.com/watch\?.*v=)([a-zA-Z0-9_-]{11})");
            if (urlMatch.Success)
            {
                string extractedId = urlMatch.Groups[1].Value;
                var validated = await ResolveVideoIdAsync(extractedId, ct);
                if (validated != null)
                    return validated;
            }

            // No more title-based search — URL-based lookup via TryGetYouTubeVideoInfoFromUrlAsync
            // is the primary method now. This fallback only handles edge cases where
            // the title itself contains a video ID or URL.
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("META-YOUTUBE-SEARCH", ex.ToString());
        }

        return null;
    }

    /// <summary>
    /// Resolves a YouTube video from a direct URL. Extracts the video ID and validates via oEmbed.
    /// This is 100% accurate — no guessing, no search-by-name.
    /// When the user has enabled the YouTube Data API and a key is configured, the Data API
    /// (videos.list?part=snippet,contentDetails) is used as the primary source so we get
    /// authoritative title, channel, duration, and thumbnail in a single call. oEmbed remains
    /// the fallback when the API is disabled, quota-exceeded, or the request fails.
    /// </summary>
    public async Task<YouTubeLookupResult?> TryGetYouTubeVideoInfoFromUrlAsync(string url, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;

            // Extract video ID from various YouTube URL formats
            var match = Regex.Match(url, @"(?:youtube\.com/watch\?.*v=|youtu\.be/|music\.youtube\.com/watch\?.*v=|youtube\.com/embed/|youtube\.com/v/)([a-zA-Z0-9_-]{11})");
            if (!match.Success)
                return null;

            string videoId = match.Groups[1].Value;
            return await ResolveVideoIdAsync(videoId, ct);
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("META-YOUTUBE-URL", ex.ToString());
        }

        return null;
    }

    /// <summary>
    /// Central resolver for a known videoId. Prefers Data API when available, falls back
    /// to oEmbed (+ optional duration enrichment).
    /// </summary>
    private async Task<YouTubeLookupResult?> ResolveVideoIdAsync(string videoId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(videoId))
            return null;

        // Quick cache hit — avoid burning quota and avoid re-validating the same video
        // every time the same track loops or comes back into focus.
        if (TryGetCachedVideo(videoId, out var cached) && cached != null)
        {
            RuntimeLog.Log("META-YOUTUBE-API",
                $"cache-hit videoId={videoId} source={cached.Source} duration={cached.Duration} thumb={(string.IsNullOrEmpty(cached.ThumbnailUrl) ? "(none)" : "data-api")}");
            return cached;
        }

        string? apiKey = GetYouTubeApiKey();
        if (!string.IsNullOrEmpty(apiKey))
        {
            var apiResult = await TryGetVideoFromDataApiAsync(videoId, apiKey, ct);
            if (apiResult != null)
            {
                CacheVideo(videoId, apiResult);
                return apiResult;
            }
            // Data API failed (quota, network, invalid key, video flagged…) → fallback to oEmbed.
            RuntimeLog.Log("META-YOUTUBE-API", $"data-api-miss videoId={videoId} -> falling back to oEmbed");
        }
        else
        {
            RuntimeLog.Log("META-YOUTUBE-API", $"data-api-disabled videoId={videoId} -> using oEmbed only");
        }

        var oembed = await ValidateVideoIdWithOEmbedAsync(videoId, ct);
        if (oembed == null)
            return null;

        // Even without the toggle, we historically did a contentDetails enrichment when a key
        // happened to be present. Preserve that behaviour for compatibility.
        if (!string.IsNullOrEmpty(apiKey))
        {
            oembed = await EnrichWithDurationAsync(oembed, apiKey, ct) ?? oembed;
        }

        CacheVideo(videoId, oembed);
        return oembed;
    }

    /// <summary>
    /// Resolves SoundCloud artwork directly from a SoundCloud track URL via oEmbed.
    /// 100% accurate — no search needed.
    /// </summary>
    public async Task<string?> TryGetSoundCloudArtworkFromUrlAsync(string soundCloudUrl, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(soundCloudUrl))
                return null;

            // Validate it's a SoundCloud URL
            if (!soundCloudUrl.Contains("soundcloud.com/", StringComparison.OrdinalIgnoreCase))
                return null;

            var result = await TryGetSoundCloudOEmbedAsync(soundCloudUrl, ct);
            if (!string.IsNullOrWhiteSpace(result.ThumbnailUrl))
            {
                string normalized = NormalizeSoundCloudArtworkUrl(result.ThumbnailUrl!);
                if (!IsLikelySoundCloudPlaceholderArtworkUrl(normalized))
                    return normalized;
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("META-SOUNDCLOUD-URL", ex.ToString());
        }

        return null;
    }

    /// <summary>
    /// Validates a video ID using YouTube's oEmbed endpoint.
    /// Returns a YouTubeLookupResult with title/author/duration if valid, null otherwise.
    /// This is free, requires no API key, and confirms the video exists.
    /// </summary>
    private async Task<YouTubeLookupResult?> ValidateVideoIdWithOEmbedAsync(string videoId, CancellationToken ct)
    {
        try
        {
            string oembedUrl = $"https://www.youtube.com/oembed?url=https://www.youtube.com/watch?v={videoId}&format=json";
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(2000);

            string json = await _httpClient.GetStringAsync(oembedUrl, timeoutCts.Token);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? oembedTitle = root.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
            string? oembedAuthor = root.TryGetProperty("author_name", out var authorEl) ? authorEl.GetString() : null;

            return new YouTubeLookupResult
            {
                Id = videoId,
                Title = oembedTitle,
                Author = oembedAuthor,
                Duration = TimeSpan.Zero, // oEmbed doesn't provide duration
                ThumbnailUrl = null,      // caller falls back to i.ytimg.com/vi/{id}/maxresdefault.jpg cascade
                Source = YouTubeLookupSource.OEmbed,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Calls the YouTube Data API v3 (videos.list?part=snippet,contentDetails) to get
    /// authoritative title, channel, duration, and the best available thumbnail in a
    /// single request (1 quota unit). Returns null on any failure so the caller can
    /// fall back to oEmbed without surfacing the error.
    /// </summary>
    private async Task<YouTubeLookupResult?> TryGetVideoFromDataApiAsync(string videoId, string apiKey, CancellationToken ct)
    {
        // If a previous call hit quotaExceeded, skip Data API for the rest of the day.
        if (IsQuotaCooldownActive())
            return null;

        try
        {
            string url = $"https://www.googleapis.com/youtube/v3/videos?part=snippet,contentDetails&id={Uri.EscapeDataString(videoId)}&fields=items(snippet(title,channelTitle,thumbnails),contentDetails/duration)&key={apiKey}";

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(2500);

            using var response = await _httpClient.GetAsync(url, timeoutCts.Token);
            string json = await response.Content.ReadAsStringAsync(timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                if (LooksLikeQuotaExceeded(json))
                {
                    TripQuotaCooldown();
                    RuntimeLog.Log("META-YOUTUBE-API", "quotaExceeded — Data API disabled until tomorrow, falling back to oEmbed.");
                }
                else
                {
                    RuntimeLog.Log("META-YOUTUBE-API", $"HTTP {(int)response.StatusCode} for videoId={videoId}");
                }
                return null;
            }

            if (string.IsNullOrWhiteSpace(json))
                return null;

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
                return null;

            var item = items[0];
            string? title = null;
            string? channel = null;
            string? thumbnailUrl = null;
            TimeSpan duration = TimeSpan.Zero;

            if (item.TryGetProperty("snippet", out var snippet))
            {
                if (snippet.TryGetProperty("title", out var titleEl)) title = titleEl.GetString();
                if (snippet.TryGetProperty("channelTitle", out var channelEl)) channel = channelEl.GetString();
                if (snippet.TryGetProperty("thumbnails", out var thumbs))
                    thumbnailUrl = PickBestThumbnail(thumbs);
            }

            if (item.TryGetProperty("contentDetails", out var details) &&
                details.TryGetProperty("duration", out var durationEl))
            {
                string? iso = durationEl.GetString();
                if (!string.IsNullOrEmpty(iso))
                    duration = ParseIso8601Duration(iso);
            }

            RuntimeLog.Log("META-YOUTUBE-API",
                $"data-api-ok videoId={videoId} title='{title}' channel='{channel}' duration={duration} thumb={(string.IsNullOrWhiteSpace(thumbnailUrl) ? "(none)" : "ok")}");

            return new YouTubeLookupResult
            {
                Id = videoId,
                Title = title,
                Author = channel,
                Duration = duration,
                ThumbnailUrl = thumbnailUrl,
                Source = YouTubeLookupSource.DataApi,
            };
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("META-YOUTUBE-API", ex.ToString());
            return null;
        }
    }    /// <summary>
    /// Picks the highest-resolution available thumbnail from snippet.thumbnails.
    /// Order: maxres → standard → high → medium → default.
    /// </summary>
    private static string? PickBestThumbnail(JsonElement thumbnails)
    {
        foreach (var key in _thumbnailPreference)
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

    private static readonly string[] _thumbnailPreference = { "maxres", "standard", "high", "medium", "default" };

    private static bool LooksLikeQuotaExceeded(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;
        return body.Contains("quotaExceeded", StringComparison.Ordinal) ||
               body.Contains("dailyLimitExceeded", StringComparison.Ordinal) ||
               body.Contains("rateLimitExceeded", StringComparison.Ordinal);
    }

    // ── Quota cooldown ───────────────────────────────────────────────────────
    // YouTube Data API quota resets at midnight Pacific Time. A simple per-process
    // cooldown is enough for our use case: once we see a quota error, stop calling
    // until the next UTC day rollover. Worst case the user restarts the app.
    private static long _quotaCooldownUntilTicks;

    private static bool IsQuotaCooldownActive()
        => Volatile.Read(ref _quotaCooldownUntilTicks) > DateTime.UtcNow.Ticks;

    private static void TripQuotaCooldown()
    {
        var resumeAt = DateTime.UtcNow.Date.AddDays(1).AddHours(8); // PT midnight ≈ UTC+8h
        Volatile.Write(ref _quotaCooldownUntilTicks, resumeAt.Ticks);
    }

    // ── Per-videoId cache ────────────────────────────────────────────────────
    // Same track usually loops back to the notch repeatedly (track change events,
    // app refocus, retries). We don't need to spend quota every time.
    private static readonly object _videoCacheLock = new();
    private static readonly Dictionary<string, (YouTubeLookupResult Result, DateTime ExpiresUtc)> _videoCache = new(StringComparer.Ordinal);
    private static readonly TimeSpan _videoCacheTtl = TimeSpan.FromHours(6);
    private const int VideoCacheMaxEntries = 256;

    private static bool TryGetCachedVideo(string videoId, out YouTubeLookupResult? result)
    {
        lock (_videoCacheLock)
        {
            if (_videoCache.TryGetValue(videoId, out var entry) && entry.ExpiresUtc > DateTime.UtcNow)
            {
                result = entry.Result;
                return true;
            }
        }
        result = null;
        return false;
    }

    private static void CacheVideo(string videoId, YouTubeLookupResult result)
    {
        if (string.IsNullOrEmpty(videoId)) return;
        lock (_videoCacheLock)
        {
            if (_videoCache.Count >= VideoCacheMaxEntries)
            {
                // Cheap eviction: drop the first half of stale entries.
                var now = DateTime.UtcNow;
                var stale = _videoCache.Where(kv => kv.Value.ExpiresUtc <= now).Select(kv => kv.Key).ToList();
                foreach (var key in stale) _videoCache.Remove(key);
                if (_videoCache.Count >= VideoCacheMaxEntries)
                {
                    foreach (var key in _videoCache.Keys.Take(_videoCache.Count / 2).ToList())
                        _videoCache.Remove(key);
                }
            }
            _videoCache[videoId] = (result, DateTime.UtcNow.Add(_videoCacheTtl));
        }
    }

    /// <summary>
    /// Enriches a YouTubeLookupResult with duration from the videos endpoint.
    /// </summary>
    private async Task<YouTubeLookupResult?> EnrichWithDurationAsync(YouTubeLookupResult result, string apiKey, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrEmpty(result.Id))
                return result;

            string url = $"https://www.googleapis.com/youtube/v3/videos?part=contentDetails&id={result.Id}&fields=items/contentDetails/duration&key={apiKey}";

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(2000);

            string json = await _httpClient.GetStringAsync(url, timeoutCts.Token);
            if (string.IsNullOrWhiteSpace(json))
                return result;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
                return result;

            var firstItem = items[0];
            if (firstItem.TryGetProperty("contentDetails", out var details) &&
                details.TryGetProperty("duration", out var durationEl))
            {
                string? isoDuration = durationEl.GetString();
                if (!string.IsNullOrEmpty(isoDuration))
                {
                    var duration = ParseIso8601Duration(isoDuration);
                    return new YouTubeLookupResult
                    {
                        Id = result.Id,
                        Title = result.Title,
                        Author = result.Author,
                        Duration = duration,
                        ThumbnailUrl = result.ThumbnailUrl,
                        Source = result.Source,
                    };
                }
            }

            return result;
        }
        catch
        {
            return result;
        }
    }

    /// <summary>
    /// Parses ISO 8601 duration format (PT#H#M#S) to TimeSpan.
    /// </summary>
    private static TimeSpan ParseIso8601Duration(string iso)
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

    /// <summary>
    /// Gets the YouTube Data API key from the app's settings.
    /// Returns null if not configured or disabled (graceful fallback to scraping).
    /// </summary>
    private static string? GetYouTubeApiKey()
    {
        try
        {
            // Read from settings.json in AppData
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string settingsPath = Path.Combine(appDataPath, "V-Notch", "settings.json");

            if (!File.Exists(settingsPath))
                return null;

            string json = File.ReadAllText(settingsPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Check if YouTube API is enabled
            if (root.TryGetProperty("EnableYouTubeApi", out var enabledEl) && enabledEl.GetBoolean())
            {
                if (root.TryGetProperty("YouTubeApiKey", out var keyEl))
                {
                    string? key = keyEl.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(key) && key.Length > 10)
                        return key;
                }
            }
        }
        catch { }

        return null;
    }

    public async Task<string?> TryGetSoundCloudArtworkUrlAsync(string title, string artist = "", bool requireStrongMatch = false, CancellationToken ct = default)
    {
        try
        {
            var directTrackUrl = ExtractSoundCloudTrackUrl(title);
            if (!string.IsNullOrEmpty(directTrackUrl))
            {
                var direct = await TryGetSoundCloudOEmbedAsync(directTrackUrl, ct);
                if (!string.IsNullOrWhiteSpace(direct.ThumbnailUrl))
                {
                    string normalizedDirectThumbnail = NormalizeSoundCloudArtworkUrl(direct.ThumbnailUrl!);
                    if (!IsLikelySoundCloudPlaceholderArtworkUrl(normalizedDirectThumbnail))
                    {
                        return normalizedDirectThumbnail;
                    }
                }
            }

            string cleanedTitle = SanitizeSearchText(title);
            string cleanedArtist = SanitizeSearchText(artist);
            string query = cleanedTitle;
            if (!string.IsNullOrWhiteSpace(artist) &&
                !artist.Equals("SoundCloud", StringComparison.OrdinalIgnoreCase) &&
                !artist.Equals("Browser", StringComparison.OrdinalIgnoreCase))
            {
                query = $"{cleanedTitle} {cleanedArtist}".Trim();
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                return null;
            }

            var combinedScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string[] searchUrls =
            {
                $"https://m.soundcloud.com/search/sounds?q={Uri.EscapeDataString(query)}",
                $"https://soundcloud.com/search/sounds?q={Uri.EscapeDataString(query)}"
            };

            var searchTasks = new List<Task<string?>>(searchUrls.Length);
            foreach (var searchUrl in searchUrls)
            {
                searchTasks.Add(GetStringWithTimeoutAsync(searchUrl, 1200, ct));
            }

            var searchHtmls = await Task.WhenAll(searchTasks);
            foreach (var html in searchHtmls)
            {
                if (string.IsNullOrWhiteSpace(html))
                {
                    continue;
                }

                var found = ExtractSoundCloudTrackUrlsFromSearchHtml(html, title, artist);
                foreach (var candidate in found)
                {
                    if (combinedScores.TryGetValue(candidate.Url, out int existing))
                    {
                        if (candidate.Score > existing)
                        {
                            combinedScores[candidate.Url] = candidate.Score;
                        }
                    }
                    else
                    {
                        combinedScores[candidate.Url] = candidate.Score;
                    }
                }
            }

            if (combinedScores.Count == 0)
            {
                return null;
            }

            var candidates = new List<(string Url, int Score)>();
            foreach (var item in combinedScores)
            {
                candidates.Add((item.Key, item.Value));
            }
            candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

            if (candidates.Count > 3)
            {
                candidates = candidates.GetRange(0, 3);
            }

            var probeTasks = new List<Task<SoundCloudCandidateProbe>>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                probeTasks.Add(ProbeSoundCloudCandidateAsync(
                    candidate.Url,
                    candidate.Score,
                    i,
                    title,
                    artist,
                    requireStrongMatch,
                    ct));
            }

            var probes = await Task.WhenAll(probeTasks);
            Array.Sort(probes, static (a, b) => a.Index.CompareTo(b.Index));

            for (int i = 0; i < probes.Length; i++)
            {
                var probe = probes[i];
                if (probe.IsMatch && !string.IsNullOrWhiteSpace(probe.ThumbnailUrl))
                {
                    return probe.ThumbnailUrl;
                }
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private readonly record struct SoundCloudCandidateProbe(int Index, string? ThumbnailUrl, bool IsMatch);

    private async Task<SoundCloudCandidateProbe> ProbeSoundCloudCandidateAsync(
        string url,
        int candidateScore,
        int index,
        string expectedTitle,
        string expectedArtist,
        bool requireStrongMatch,
        CancellationToken ct)
    {
        try
        {
            var result = await TryGetSoundCloudOEmbedAsync(url, ct);
            if (string.IsNullOrWhiteSpace(result.ThumbnailUrl))
            {
                return new SoundCloudCandidateProbe(index, null, false);
            }

            string normalizedThumb = NormalizeSoundCloudArtworkUrl(result.ThumbnailUrl!);
            if (IsLikelySoundCloudPlaceholderArtworkUrl(normalizedThumb))
            {
                return new SoundCloudCandidateProbe(index, null, false);
            }

            bool isMatch = IsSoundCloudOEmbedMatch(
                expectedTitle,
                expectedArtist,
                result.Title,
                result.Author,
                candidateScore,
                requireStrongMatch);

            return new SoundCloudCandidateProbe(index, normalizedThumb, isMatch);
        }
        catch
        {
            return new SoundCloudCandidateProbe(index, null, false);
        }
    }

    private async Task<string?> GetStringWithTimeoutAsync(string url, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            return await _httpClient.GetStringAsync(url, timeoutCts.Token);
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractSoundCloudTrackUrl(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return null;
        }

        var match = Regex.Match(
            rawText,
            @"https?://(?:www\.)?soundcloud\.com/(?<user>[^/\s?#]+)/(?<slug>[^/\s?#]+)",
            RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            return null;
        }

        return $"https://soundcloud.com/{match.Groups["user"].Value}/{match.Groups["slug"].Value}";
    }

    private static string SanitizeSearchText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string normalized = value.Replace('\u2013', '-').Replace('\u2014', '-').Trim();
        normalized = normalized.Trim('[', ']', '(', ')', '"', '\'');
        normalized = Regex.Replace(normalized, @"\s+", " ");
        return normalized.Trim();
    }

    private static List<(string Url, int Score)> ExtractSoundCloudTrackUrlsFromSearchHtml(string html, string title, string artist)
    {
        var result = new List<(string Url, int Score)>();
        if (string.IsNullOrWhiteSpace(html))
        {
            return result;
        }

        string normalizedTitle = NormalizeForLooseMatch(title.ToLowerInvariant());
        string normalizedArtist = NormalizeForLooseMatch((artist ?? "").ToLowerInvariant());
        var scoreMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var pathMatches = Regex.Matches(html, @"href=""/(?<path>[^""?#]+)", RegexOptions.IgnoreCase);
        foreach (Match match in pathMatches)
        {
            string path = match.Groups["path"].Value.Replace("\\/", "/").Trim('/');
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length != 2)
            {
                continue;
            }

            string user = segments[0];
            string slug = segments[1];
            AddSoundCloudCandidate(scoreMap, user, slug, normalizedTitle, normalizedArtist);
        }

        var absoluteMatches = Regex.Matches(
            html,
            @"https?://(?:www\.)?soundcloud\.com/(?<user>[^/\s""'?#]+)/(?<slug>[^/\s""'?#]+)",
            RegexOptions.IgnoreCase);
        foreach (Match match in absoluteMatches)
        {
            string user = match.Groups["user"].Value;
            string slug = match.Groups["slug"].Value;
            AddSoundCloudCandidate(scoreMap, user, slug, normalizedTitle, normalizedArtist);
        }

        var sorted = new List<KeyValuePair<string, int>>(scoreMap);
        sorted.Sort((a, b) => b.Value.CompareTo(a.Value));

        foreach (var item in sorted)
        {
            result.Add((item.Key, item.Value));
        }

        return result;
    }

    private static void AddSoundCloudCandidate(
        Dictionary<string, int> scoreMap,
        string user,
        string slug,
        string normalizedTitle,
        string normalizedArtist)
    {
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(slug))
        {
            return;
        }

        if (user.Contains('.') || slug.Length < 2)
        {
            return;
        }

        if (_soundCloudReservedUsers.Contains(user) || _soundCloudReservedTrackSlugs.Contains(slug))
        {
            return;
        }

        string url = $"https://soundcloud.com/{user}/{slug}";
        int score = ScoreSoundCloudCandidate(user, slug, normalizedTitle, normalizedArtist);
        if (scoreMap.TryGetValue(url, out int existing))
        {
            if (score > existing)
            {
                scoreMap[url] = score;
            }
        }
        else
        {
            scoreMap[url] = score;
        }
    }

    private static int ScoreSoundCloudCandidate(string user, string slug, string normalizedTitle, string normalizedArtist)
    {
        int score = 0;
        string normalizedSlug = NormalizeForLooseMatch(slug.ToLowerInvariant());
        string normalizedUser = NormalizeForLooseMatch(user.ToLowerInvariant());

        if (!string.IsNullOrEmpty(normalizedTitle))
        {
            if (normalizedSlug.Contains(normalizedTitle, StringComparison.Ordinal) || normalizedTitle.Contains(normalizedSlug, StringComparison.Ordinal))
            {
                score += 5;
            }
            else
            {
                var titleTokens = normalizedTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var token in titleTokens)
                {
                    if (token.Length >= 3 && normalizedSlug.Contains(token, StringComparison.Ordinal))
                    {
                        score += 1;
                    }
                }
            }
        }

        if (!string.IsNullOrEmpty(normalizedArtist) &&
            normalizedArtist != "soundcloud" &&
            normalizedArtist != "browser")
        {
            if (normalizedUser.Contains(normalizedArtist, StringComparison.Ordinal) || normalizedArtist.Contains(normalizedUser, StringComparison.Ordinal))
            {
                score += 3;
            }
        }

        return score;
    }

    private static bool IsSoundCloudOEmbedMatch(string expectedTitle, string expectedArtist, string? candidateTitle, string? candidateAuthor, int candidateScore, bool strictMode = false)
    {
        string normalizedExpectedTitle = NormalizeForLooseMatch(expectedTitle.ToLowerInvariant());
        string normalizedExpectedArtist = NormalizeForLooseMatch((expectedArtist ?? "").ToLowerInvariant());
        string normalizedCandidateTitle = NormalizeForLooseMatch((candidateTitle ?? "").ToLowerInvariant());
        string normalizedCandidateAuthor = NormalizeForLooseMatch((candidateAuthor ?? "").ToLowerInvariant());
        int titleOverlap = CountTokenOverlap(normalizedExpectedTitle, normalizedCandidateTitle);

        bool titleMatches = !string.IsNullOrEmpty(normalizedExpectedTitle) &&
                            !string.IsNullOrEmpty(normalizedCandidateTitle) &&
                            (normalizedCandidateTitle.Contains(normalizedExpectedTitle, StringComparison.Ordinal) ||
                             normalizedExpectedTitle.Contains(normalizedCandidateTitle, StringComparison.Ordinal));

        bool artistMatches = string.IsNullOrEmpty(normalizedExpectedArtist) ||
                             normalizedExpectedArtist == "soundcloud" ||
                             normalizedExpectedArtist == "browser" ||
                             (!string.IsNullOrEmpty(normalizedCandidateAuthor) &&
                              (normalizedCandidateAuthor.Contains(normalizedExpectedArtist, StringComparison.Ordinal) ||
                               normalizedExpectedArtist.Contains(normalizedCandidateAuthor, StringComparison.Ordinal)));

        if (titleMatches && artistMatches)
        {
            return true;
        }

        if (titleMatches && titleOverlap >= 1)
        {
            return true;
        }

        if (strictMode)
        {
            return titleOverlap >= 2 && (artistMatches || candidateScore >= 2);
        }

        if (artistMatches && (titleOverlap >= 1 || candidateScore >= 2))
        {
            return true;
        }

        return candidateScore >= 3 && titleOverlap >= 1;
    }

    private static int CountTokenOverlap(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return 0;
        }

        var rightTokens = new HashSet<string>(
            right.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.Ordinal);

        int overlap = 0;
        foreach (var token in left.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length < 2)
            {
                continue;
            }

            if (rightTokens.Contains(token))
            {
                overlap++;
            }
        }

        return overlap;
    }

    private static string NormalizeSoundCloudArtworkUrl(string url)
    {
        string normalized = url.Replace("\\u0026", "&").Replace("\\/", "/");
        if (IsLikelySoundCloudPlaceholderArtworkUrl(normalized))
        {
            return normalized;
        }

        return Regex.Replace(normalized, @"-(?:large|t\d+x\d+)\.", "-t500x500.", RegexOptions.IgnoreCase);
    }

    private async Task<(string? ThumbnailUrl, string? Title, string? Author)> TryGetSoundCloudOEmbedAsync(string trackUrl, CancellationToken ct)
    {
        try
        {
            string requestUrl = $"https://soundcloud.com/oembed?format=json&url={Uri.EscapeDataString(trackUrl)}";
            string? json = await GetStringWithTimeoutAsync(requestUrl, 1100, ct);
            if (string.IsNullOrWhiteSpace(json))
            {
                return (null, null, null);
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? thumbnailUrl = root.TryGetProperty("thumbnail_url", out var thumbnailEl) ? thumbnailEl.GetString() : null;
            string? oembedTitle = root.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
            string? oembedAuthor = root.TryGetProperty("author_name", out var authorEl) ? authorEl.GetString() : null;
            return (thumbnailUrl, oembedTitle, oembedAuthor);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private static string NormalizeForLooseMatch(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string folded = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(folded.Length);
        bool lastWasSpace = false;

        foreach (var ch in folded)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

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

        if (sb.Length > 0 && sb[^1] == ' ')
        {
            sb.Length--;
        }

        return sb.ToString();
    }

    private static bool IsLikelySoundCloudPlaceholderArtworkUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return true;
        }

        string normalized = url.Replace("\\u0026", "&").Replace("\\/", "/").ToLowerInvariant();
        return normalized.Contains("default_avatar", StringComparison.Ordinal) ||
               normalized.Contains("/images/default_", StringComparison.Ordinal) ||
               normalized.Contains("default-soundcloud", StringComparison.Ordinal) ||
               normalized.Contains("/avatars-", StringComparison.Ordinal);
    }
}
