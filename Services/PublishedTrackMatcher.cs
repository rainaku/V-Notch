namespace VNotch.Services;

/// <summary>
/// Immutable snapshot of the identity fields tracked for the last-published media item. Lets the
/// matching rules be expressed as pure functions over explicit inputs rather than instance state.
/// </summary>
internal readonly record struct PublishedTrackSnapshot(
    string TrackIdentity,
    string TrackOnlyIdentity,
    string SourceAppId,
    string SessionInstanceKey);

/// <summary>
/// Pure decision logic (functional core) extracted from <see cref="MediaDetectionService"/> for
/// deciding whether an observed track is still the previously-published one, or is a brand-new
/// track for thumbnail purposes. No instance state, clock, or Win32 access — fully unit-testable.
/// </summary>
internal static class PublishedTrackMatcher
{
    /// <summary>
    /// True when the expected track is the same as the last-published one. Guards (strongest first):
    /// session-instance key, then source-app id, then full track+artist identity, with a track-only
    /// fallback that applies only when the expected artist was unknown.
    /// </summary>
    public static bool IsSameTrack(
        in PublishedTrackSnapshot last,
        string expectedTrack,
        string expectedArtist,
        string expectedSourceAppId,
        string expectedSessionInstanceKey)
    {
        // Session key is the strongest guard — if the session changed, the track is not the same
        // context even if the title matches.
        if (!string.IsNullOrEmpty(expectedSessionInstanceKey) &&
            !string.Equals(last.SessionInstanceKey, expectedSessionInstanceKey, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(expectedSourceAppId) &&
            !string.Equals(last.SourceAppId, expectedSourceAppId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string expectedIdentity = MediaHeuristics.BuildTrackIdentity(expectedTrack, expectedArtist);
        // Primary check: full track+artist identity must match.
        if (string.Equals(last.TrackIdentity, expectedIdentity, StringComparison.Ordinal))
        {
            return true;
        }

        // Fallback: track-only match, but ONLY if the artist was unknown at fetch time.
        if (string.IsNullOrEmpty(expectedArtist))
        {
            string expectedTrackOnly = MediaHeuristics.BuildTrackIdentity(expectedTrack, "");
            return string.Equals(last.TrackOnlyIdentity, expectedTrackOnly, StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>True when the current track (ignoring artist) differs from the last-published track.</summary>
    public static bool IsNewTrackForThumbnail(string lastPublishedTrackOnlyIdentity, string currentTrack)
    {
        return !string.IsNullOrEmpty(currentTrack) &&
               !string.Equals(
                   MediaHeuristics.BuildTrackIdentity(currentTrack, ""),
                   lastPublishedTrackOnlyIdentity,
                   StringComparison.Ordinal);
    }
}
