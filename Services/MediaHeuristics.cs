namespace VNotch.Services;

/// <summary>
/// Pure, dependency-free string/identity heuristics extracted from <see cref="MediaDetectionService"/>.
/// Everything here is deterministic from its arguments (the only collaborator is the already-tested
/// <see cref="PlatformDetector"/>), which makes the logic unit-testable in isolation.
/// </summary>
internal static class MediaHeuristics
{
    /// <summary>Builds the canonical "track|artist" identity key (trimmed, lower-invariant).</summary>
    public static string BuildTrackIdentity(string track, string artist)
    {
        return $"{track.Trim().ToLowerInvariant()}|{artist.Trim().ToLowerInvariant()}";
    }

    /// <summary>
    /// Resolves the key used to store a per-session source override. A real session-instance key
    /// always wins; otherwise browser apps share an empty key (they multiplex many sites) while
    /// non-browser apps key off their app id.
    /// </summary>
    public static string BuildSourceOverrideKey(string sessionInstanceKey, string sourceAppId)
    {
        if (!string.IsNullOrWhiteSpace(sessionInstanceKey))
        {
            return sessionInstanceKey;
        }

        return PlatformDetector.IsBrowserApp(sourceAppId) ? string.Empty : sourceAppId ?? string.Empty;
    }

    /// <summary>True when the (loosely normalized) track name appears inside the window title.</summary>
    public static bool IsTrackCompatibleWithWindowTitle(string track, string windowTitle)
    {
        if (string.IsNullOrWhiteSpace(track) || string.IsNullOrWhiteSpace(windowTitle))
            return false;

        string normalizedTrack = PlatformDetector.NormalizeForLooseMatch(track);
        string normalizedWindowTitle = PlatformDetector.NormalizeForLooseMatch(windowTitle);
        return !string.IsNullOrEmpty(normalizedTrack) &&
               normalizedWindowTitle.Contains(normalizedTrack, StringComparison.Ordinal);
    }

    /// <summary>Source apps we never want to surface as "now playing" (e.g. Discord call audio).</summary>
    public static bool IsIgnoredSourceApp(string sourceAppId)
    {
        return sourceAppId.Contains("Discord", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Detects SoundCloud default-avatar / placeholder artwork URLs that should be ignored.</summary>
    public static bool IsLikelySoundCloudPlaceholderArtworkUrl(string? url)
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

    /// <summary>
    /// Normalizes bracketed suffixes (" (...)", " [...]") into " - ..." form so that two titles
    /// that differ only in how the remix/feat. segment is delimited still compare as equal.
    /// </summary>
    public static string NormalizeTrackForComparison(string text)
    {
        var result = text
            .Replace(" (", " - ", StringComparison.Ordinal)
            .Replace("(", " - ", StringComparison.Ordinal)
            .Replace(")", "", StringComparison.Ordinal)
            .Replace(" [", " - ", StringComparison.Ordinal)
            .Replace("[", " - ", StringComparison.Ordinal)
            .Replace("]", "", StringComparison.Ordinal);
        return result.Trim();
    }

    /// <summary>Returns the track name with any trailing parenthetical/bracketed segment stripped.</summary>
    public static string ExtractCoreTrackName(string track)
    {
        int parenIdx = track.IndexOf('(');
        int bracketIdx = track.IndexOf('[');
        int cutIdx = -1;

        if (parenIdx > 0 && bracketIdx > 0)
            cutIdx = Math.Min(parenIdx, bracketIdx);
        else if (parenIdx > 0)
            cutIdx = parenIdx;
        else if (bracketIdx > 0)
            cutIdx = bracketIdx;

        if (cutIdx > 0)
            return track.Substring(0, cutIdx).Trim();

        return track;
    }

    /// <summary>
    /// True when the Spotify window title (ground truth) plausibly contains the SMTC track name,
    /// trying a direct match, a bracket-normalized match, and a core-name (pre-parenthesis) match.
    /// </summary>
    public static bool SpotifyTitleContainsTrack(string spotifyWindowTitle, string smtcTrack)
    {
        if (string.IsNullOrEmpty(spotifyWindowTitle) || string.IsNullOrEmpty(smtcTrack))
            return false;

        // Direct match (fast path)
        if (spotifyWindowTitle.Contains(smtcTrack, StringComparison.OrdinalIgnoreCase))
            return true;

        string normalizedTrack = NormalizeTrackForComparison(smtcTrack);
        string normalizedTitle = NormalizeTrackForComparison(spotifyWindowTitle);

        if (normalizedTitle.Contains(normalizedTrack, StringComparison.OrdinalIgnoreCase))
            return true;

        // Also try: extract core track name (before parentheses) and check if window title contains it
        string coreTrack = ExtractCoreTrackName(smtcTrack);
        if (!string.IsNullOrEmpty(coreTrack) && coreTrack.Length >= 3 &&
            spotifyWindowTitle.Contains(coreTrack, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
