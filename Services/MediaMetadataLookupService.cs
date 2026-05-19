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

            // Clean " - Topic" suffix from artist (YouTube auto-generated channels)
            string cleanArtist = artist;
            if (!string.IsNullOrEmpty(cleanArtist))
            {
                cleanArtist = Regex.Replace(cleanArtist, @"\s*-\s*Topic$", "", RegexOptions.IgnoreCase).Trim();
            }

            // ─── Step 1: Check if title itself is a video ID (11 chars, base64url) ───
            if (cleanTitle.Length == 11 && Regex.IsMatch(cleanTitle, "^[a-zA-Z0-9_-]{11}$"))
            {
                var validated = await ValidateVideoIdWithOEmbedAsync(cleanTitle, ct);
                if (validated != null)
                    return validated;
            }

            // ─── Step 2: Extract video ID from URL if title contains a YouTube URL ───
            var urlMatch = Regex.Match(cleanTitle, @"(?:youtube\.com/watch\?v=|youtu\.be/)([a-zA-Z0-9_-]{11})");
            if (urlMatch.Success)
            {
                string extractedId = urlMatch.Groups[1].Value;
                var validated = await ValidateVideoIdWithOEmbedAsync(extractedId, ct);
                if (validated != null)
                    return validated;
            }

            // ─── Step 3: Use YouTube Data API v3 search for accurate matching ───
            var apiResult = await TryYouTubeDataApiSearchAsync(cleanTitle, cleanArtist, ct);
            if (apiResult != null)
                return apiResult;

            // ─── Step 4: Fallback to YouTube search page scraping with validation ───
            var scrapeResult = await TryYouTubeSearchScrapeAsync(cleanTitle, cleanArtist, ct);
            if (scrapeResult != null)
                return scrapeResult;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("META-YOUTUBE-SEARCH", ex.ToString());
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
                Duration = TimeSpan.Zero // oEmbed doesn't provide duration
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Uses YouTube Data API v3 to search for a video by title.
    /// Validates the result by comparing the returned title with the expected title.
    /// Falls back gracefully if no API key is configured.
    /// </summary>
    private async Task<YouTubeLookupResult?> TryYouTubeDataApiSearchAsync(string title, string artist, CancellationToken ct)
    {
        try
        {
            string? apiKey = GetYouTubeApiKey();
            if (string.IsNullOrEmpty(apiKey))
                return null;

            // Build search query: combine title and artist for better accuracy
            string query = title;
            if (!string.IsNullOrWhiteSpace(artist) &&
                !artist.Equals("YouTube", StringComparison.OrdinalIgnoreCase) &&
                !artist.Equals("Browser", StringComparison.OrdinalIgnoreCase))
            {
                query = $"{title} {artist}";
            }

            string searchUrl = $"https://www.googleapis.com/youtube/v3/search?part=snippet&type=video&maxResults=3&q={Uri.EscapeDataString(query)}&fields=items(id/videoId,snippet/title,snippet/channelTitle)&key={apiKey}";

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(3000);

            string json = await _httpClient.GetStringAsync(searchUrl, timeoutCts.Token);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
                return null;

            string normalizedTitle = NormalizeForYouTubeMatch(title);

            // Find the best matching result
            YouTubeLookupResult? bestMatch = null;
            int bestScore = -1;

            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idObj) ||
                    !idObj.TryGetProperty("videoId", out var videoIdEl))
                    continue;

                string? videoId = videoIdEl.GetString();
                if (string.IsNullOrEmpty(videoId))
                    continue;

                string? snippetTitle = null;
                string? channelTitle = null;

                if (item.TryGetProperty("snippet", out var snippet))
                {
                    snippetTitle = snippet.TryGetProperty("title", out var sTitleEl) ? sTitleEl.GetString() : null;
                    channelTitle = snippet.TryGetProperty("channelTitle", out var chEl) ? chEl.GetString() : null;
                }

                // Score the match
                int score = ScoreYouTubeMatch(normalizedTitle, artist, snippetTitle, channelTitle);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestMatch = new YouTubeLookupResult
                    {
                        Id = videoId,
                        Title = snippetTitle,
                        Author = channelTitle,
                        Duration = TimeSpan.Zero
                    };
                }
            }

            // Only return if we have a reasonable match (score >= 2)
            if (bestMatch != null && bestScore >= 2)
            {
                // Try to get duration from videos endpoint
                bestMatch = await EnrichWithDurationAsync(bestMatch, apiKey, ct) ?? bestMatch;
                return bestMatch;
            }

            return null;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("META-YOUTUBE-API", $"Data API search failed: {ex.Message}");
            return null;
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
                        Duration = duration
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
    /// Fallback: scrapes YouTube search results page and validates with oEmbed.
    /// Improved over the old approach by validating the found video's title matches.
    /// </summary>
    private async Task<YouTubeLookupResult?> TryYouTubeSearchScrapeAsync(string title, string artist, CancellationToken ct)
    {
        try
        {
            // Build a better search query
            string query = title;
            if (!string.IsNullOrWhiteSpace(artist) &&
                !artist.Equals("YouTube", StringComparison.OrdinalIgnoreCase) &&
                !artist.Equals("Browser", StringComparison.OrdinalIgnoreCase))
            {
                query = $"{title} {artist}";
            }

            string searchUrl = $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(query)}";

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(3000);

            string html = await _httpClient.GetStringAsync(searchUrl, timeoutCts.Token);
            if (string.IsNullOrWhiteSpace(html))
                return null;

            // Extract multiple candidate video IDs
            var candidateIds = new List<string>();
            var videoIdMatches = Regex.Matches(html, @"""videoId"":""([a-zA-Z0-9_-]{11})""");
            foreach (Match m in videoIdMatches)
            {
                string id = m.Groups[1].Value;
                if (!candidateIds.Contains(id))
                {
                    candidateIds.Add(id);
                    if (candidateIds.Count >= 5) break;
                }
            }

            if (candidateIds.Count == 0)
            {
                // Try alternate pattern
                var watchMatches = Regex.Matches(html, @"/watch\?v=([a-zA-Z0-9_-]{11})");
                foreach (Match m in watchMatches)
                {
                    string id = m.Groups[1].Value;
                    if (!candidateIds.Contains(id))
                    {
                        candidateIds.Add(id);
                        if (candidateIds.Count >= 5) break;
                    }
                }
            }

            if (candidateIds.Count == 0)
                return null;

            string normalizedTitle = NormalizeForYouTubeMatch(title);

            // Validate each candidate with oEmbed and pick the best match
            YouTubeLookupResult? bestResult = null;
            int bestScore = -1;

            foreach (var candidateId in candidateIds)
            {
                if (ct.IsCancellationRequested) break;

                var validated = await ValidateVideoIdWithOEmbedAsync(candidateId, ct);
                if (validated == null) continue;

                int score = ScoreYouTubeMatch(normalizedTitle, artist, validated.Title, validated.Author);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestResult = validated;
                }

                // If we have a strong match, no need to check more
                if (score >= 5) break;
            }

            // Accept if score is reasonable (at least partial title overlap)
            if (bestResult != null && bestScore >= 1)
                return bestResult;

            // If no good match found but we have candidates, return the first one
            // (preserves old behavior as last resort)
            if (candidateIds.Count > 0 && bestResult == null)
            {
                return new YouTubeLookupResult { Id = candidateIds[0] };
            }

            return bestResult;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("META-YOUTUBE-SCRAPE", ex.ToString());
            return null;
        }
    }

    /// <summary>
    /// Scores how well a YouTube video matches the expected title/artist.
    /// Higher score = better match.
    /// </summary>
    private static int ScoreYouTubeMatch(string normalizedExpectedTitle, string? expectedArtist, string? candidateTitle, string? candidateAuthor)
    {
        if (string.IsNullOrEmpty(candidateTitle))
            return 0;

        int score = 0;
        string normalizedCandidate = NormalizeForYouTubeMatch(candidateTitle);

        // Full title containment (either direction)
        if (normalizedCandidate.Contains(normalizedExpectedTitle, StringComparison.Ordinal) ||
            normalizedExpectedTitle.Contains(normalizedCandidate, StringComparison.Ordinal))
        {
            score += 5;
        }
        else
        {
            // Token overlap scoring
            var expectedTokens = normalizedExpectedTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var candidateTokens = new HashSet<string>(
                normalizedCandidate.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                StringComparer.Ordinal);

            int matchedTokens = 0;
            foreach (var token in expectedTokens)
            {
                if (token.Length >= 2 && candidateTokens.Contains(token))
                    matchedTokens++;
            }

            if (expectedTokens.Length > 0)
            {
                double ratio = (double)matchedTokens / expectedTokens.Length;
                if (ratio >= 0.7) score += 4;
                else if (ratio >= 0.5) score += 3;
                else if (ratio >= 0.3) score += 2;
                else if (matchedTokens >= 1) score += 1;
            }
        }

        // Artist/channel match bonus
        if (!string.IsNullOrWhiteSpace(expectedArtist) &&
            !expectedArtist.Equals("YouTube", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(candidateAuthor))
        {
            string normalizedArtist = NormalizeForYouTubeMatch(expectedArtist);
            string normalizedAuthor = NormalizeForYouTubeMatch(candidateAuthor);

            if (normalizedAuthor.Contains(normalizedArtist, StringComparison.Ordinal) ||
                normalizedArtist.Contains(normalizedAuthor, StringComparison.Ordinal))
            {
                score += 2;
            }
        }

        return score;
    }

    /// <summary>
    /// Normalizes text for YouTube title matching (lowercase, remove diacritics, keep alphanumeric + spaces).
    /// </summary>
    private static string NormalizeForYouTubeMatch(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        string folded = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(folded.Length);
        bool lastWasSpace = false;

        foreach (var ch in folded)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

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
            sb.Length--;

        return sb.ToString();
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
