using System.Text.RegularExpressions;

namespace VNotch.Services;

internal static class SoundCloudMatching
{
    private static readonly HashSet<string> ReservedUsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "search", "charts", "discover", "stream", "you", "terms", "privacy", "mobile", "upload", "signin",
        "login", "settings", "jobs", "blog", "developers", "pages", "playlists", "stations", "likes", "messages",
        "pro", "for-artists", "popular", "tracks", "explore", "featured"
    };

    private static readonly HashSet<string> ReservedTrackSlugs = new(StringComparer.OrdinalIgnoreCase)
    {
        "sets", "tracks", "likes", "reposts", "spotlight", "albums", "following", "followers"
    };

    public static string? ExtractTrackUrl(string rawText)
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

    public static string SanitizeSearchText(string value)
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

    public static List<(string Url, int Score)> ExtractTrackUrlsFromSearchHtml(string html, string title, string artist)
    {
        var result = new List<(string Url, int Score)>();
        if (string.IsNullOrWhiteSpace(html))
        {
            return result;
        }

        string normalizedTitle = PlatformDetector.NormalizeForLooseMatch(title.ToLowerInvariant());
        string normalizedArtist = PlatformDetector.NormalizeForLooseMatch((artist ?? "").ToLowerInvariant());
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

            AddCandidate(scoreMap, segments[0], segments[1], normalizedTitle, normalizedArtist);
        }

        var absoluteMatches = Regex.Matches(
            html,
            @"https?://(?:www\.)?soundcloud\.com/(?<user>[^/\s""'?#]+)/(?<slug>[^/\s""'?#]+)",
            RegexOptions.IgnoreCase);
        foreach (Match match in absoluteMatches)
        {
            AddCandidate(scoreMap, match.Groups["user"].Value, match.Groups["slug"].Value, normalizedTitle, normalizedArtist);
        }

        var sorted = new List<KeyValuePair<string, int>>(scoreMap);
        sorted.Sort((a, b) => b.Value.CompareTo(a.Value));

        foreach (var item in sorted)
        {
            result.Add((item.Key, item.Value));
        }

        return result;
    }

    private static void AddCandidate(
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

        if (ReservedUsers.Contains(user) || ReservedTrackSlugs.Contains(slug))
        {
            return;
        }

        string url = $"https://soundcloud.com/{user}/{slug}";
        int score = ScoreCandidate(user, slug, normalizedTitle, normalizedArtist);
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

    public static int ScoreCandidate(string user, string slug, string normalizedTitle, string normalizedArtist)
    {
        int score = 0;
        string normalizedSlug = PlatformDetector.NormalizeForLooseMatch(slug.ToLowerInvariant());
        string normalizedUser = PlatformDetector.NormalizeForLooseMatch(user.ToLowerInvariant());

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

    public static bool IsOEmbedMatch(string expectedTitle, string expectedArtist, string? candidateTitle, string? candidateAuthor, int candidateScore, bool strictMode = false)
    {
        string normalizedExpectedTitle = PlatformDetector.NormalizeForLooseMatch(expectedTitle.ToLowerInvariant());
        string normalizedExpectedArtist = PlatformDetector.NormalizeForLooseMatch((expectedArtist ?? "").ToLowerInvariant());
        string normalizedCandidateTitle = PlatformDetector.NormalizeForLooseMatch((candidateTitle ?? "").ToLowerInvariant());
        string normalizedCandidateAuthor = PlatformDetector.NormalizeForLooseMatch((candidateAuthor ?? "").ToLowerInvariant());
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

    public static int CountTokenOverlap(string left, string right)
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

    public static string NormalizeArtworkUrl(string url)
    {
        string normalized = url.Replace("\\u0026", "&").Replace("\\/", "/");
        if (MediaHeuristics.IsLikelySoundCloudPlaceholderArtworkUrl(normalized))
        {
            return normalized;
        }

        return Regex.Replace(normalized, @"-(?:large|t\d+x\d+)\.", "-t500x500.", RegexOptions.IgnoreCase);
    }
}
