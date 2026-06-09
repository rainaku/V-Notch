namespace VNotch.Services;

internal static class MediaTimingDecisions
{
    private static bool IsLongHoldSource(string lastSource)
        => lastSource == MediaPlatform.YouTube.ToDisplayString()
        || lastSource == MediaPlatform.Browser.ToDisplayString();

    public static (bool hold, DateTime emptyStart, string stableSignature) EvaluateEmptyMetadataHold(
        string currentTrack,
        bool isAnyMediaPlaying,
        string currentSignature,
        string lastSource,
        DateTime emptyStart,
        string stableSignature,
        DateTime now)
    {
        if (string.IsNullOrEmpty(currentTrack))
        {
            if (isAnyMediaPlaying)
            {
                if (emptyStart == DateTime.MinValue)
                {
                    emptyStart = now;
                }

                double holdSeconds = IsLongHoldSource(lastSource) ? 4.0 : 2.5;
                if ((now - emptyStart).TotalSeconds < holdSeconds && !string.IsNullOrEmpty(stableSignature))
                {
                    return (true, emptyStart, stableSignature);
                }
            }
            else
            {
                emptyStart = DateTime.MinValue;
                stableSignature = "";
            }
        }
        else
        {
            emptyStart = DateTime.MinValue;
            stableSignature = currentSignature;
        }

        return (false, emptyStart, stableSignature);
    }

    public static (bool debounce, string pendingKey, DateTime pendingSince) EvaluateNewTrackDebounce(
        string currentTrack,
        string currentArtist,
        bool isPlaying,
        bool forceRefresh,
        string lastPublishedTrackIdentity,
        string pendingKey,
        DateTime pendingSince,
        DateTime nowUtc,
        double debounceMs = 600)
    {
        bool isNewTrack = !string.IsNullOrEmpty(currentTrack) &&
                          !string.Equals(
                              MediaHeuristics.BuildTrackIdentity(currentTrack, currentArtist),
                              lastPublishedTrackIdentity,
                              StringComparison.Ordinal);

        if (isNewTrack && !isPlaying && !forceRefresh)
        {
            string candidateKey = MediaHeuristics.BuildTrackIdentity(currentTrack, currentArtist);
            if (candidateKey != pendingKey)
            {
                pendingKey = candidateKey;
                pendingSince = nowUtc;
            }

            bool debounce = (nowUtc - pendingSince).TotalMilliseconds < debounceMs;
            return (debounce, pendingKey, pendingSince);
        }

        return (false, "", pendingSince);
    }

    public static (string artist, string stableArtist) EvaluateArtistStabilization(
        string currentArtist,
        string stableArtist,
        DateTime lastSourceConfirmedTime,
        DateTime now,
        double holdSeconds = 15.0)
    {
        bool isGeneric = currentArtist == MediaPlatform.YouTube.ToDisplayString()
                      || currentArtist == MediaPlatform.Browser.ToDisplayString();

        if (isGeneric &&
            !string.IsNullOrEmpty(stableArtist) &&
            (now - lastSourceConfirmedTime).TotalSeconds < holdSeconds)
        {
            return (stableArtist, stableArtist);
        }

        if (!string.IsNullOrEmpty(currentArtist) && !isGeneric)
        {
            return (currentArtist, currentArtist);
        }

        return (currentArtist, stableArtist);
    }

    public static bool ShouldPreserveSoundCloud(
        string mediaSource,
        string currentTrack,
        string currentArtist,
        string sourceAppId,
        string sessionInstanceKey,
        string lastSource,
        string lastPublishedSessionInstanceKey,
        DateTime lastMetadataChangeTime,
        DateTime now,
        bool hasSessionOverride,
        string sessionOverride,
        double freshnessSeconds = 3.0)
    {
        if (!string.Equals(mediaSource, MediaPlatform.Browser.ToDisplayString(), StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.Equals(lastSource, MediaPlatform.SoundCloud.ToDisplayString(), StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(currentTrack) ||
            string.IsNullOrWhiteSpace(sourceAppId) ||
            !PlatformDetector.IsBrowserApp(sourceAppId) ||
            string.IsNullOrWhiteSpace(sessionInstanceKey))
            return false;

        if (!string.Equals(lastPublishedSessionInstanceKey, sessionInstanceKey, StringComparison.Ordinal))
            return false;

        if ((now - lastMetadataChangeTime).TotalSeconds > freshnessSeconds)
            return false;

        bool hasYouTubeHint = currentTrack.Contains("youtube", StringComparison.OrdinalIgnoreCase) ||
                              currentArtist.Contains("youtube", StringComparison.OrdinalIgnoreCase);
        if (hasYouTubeHint)
            return false;

        if (hasSessionOverride &&
            !string.IsNullOrEmpty(sessionOverride) &&
            !string.Equals(sessionOverride, MediaPlatform.SoundCloud.ToDisplayString(), StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }
}
