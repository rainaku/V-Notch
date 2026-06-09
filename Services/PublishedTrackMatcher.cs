namespace VNotch.Services;

internal readonly record struct PublishedTrackSnapshot(
    string TrackIdentity,
    string TrackOnlyIdentity,
    string SourceAppId,
    string SessionInstanceKey);

internal static class PublishedTrackMatcher
{
    public static bool IsSameTrack(
        in PublishedTrackSnapshot last,
        string expectedTrack,
        string expectedArtist,
        string expectedSourceAppId,
        string expectedSessionInstanceKey)
    {
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
        if (string.Equals(last.TrackIdentity, expectedIdentity, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.IsNullOrEmpty(expectedArtist))
        {
            string expectedTrackOnly = MediaHeuristics.BuildTrackIdentity(expectedTrack, "");
            return string.Equals(last.TrackOnlyIdentity, expectedTrackOnly, StringComparison.Ordinal);
        }

        return false;
    }

    public static bool IsNewTrackForThumbnail(string lastPublishedTrackOnlyIdentity, string currentTrack)
    {
        return !string.IsNullOrEmpty(currentTrack) &&
               !string.Equals(
                   MediaHeuristics.BuildTrackIdentity(currentTrack, ""),
                   lastPublishedTrackOnlyIdentity,
                   StringComparison.Ordinal);
    }
}
