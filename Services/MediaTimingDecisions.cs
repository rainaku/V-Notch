namespace VNotch.Services;

/// <summary>
/// Time-dependent decision logic (functional core) extracted from <see cref="MediaDetectionService"/>.
/// Each method is a pure transition: it takes the current tracking state, the relevant inputs and an
/// explicit "now", and returns the decision together with the next state. The service keeps the
/// mutable fields and simply feeds <c>DateTime.Now</c>/<c>DateTime.UtcNow</c> in; tests feed a fixed
/// clock. This makes the timing rules deterministic and unit-testable without touching SMTC/Win32.
/// </summary>
internal static class MediaTimingDecisions
{
    /// <summary>Sources that get a longer empty-metadata grace window (video/browser re-establish SMTC slowly).</summary>
    private static bool IsLongHoldSource(string lastSource)
        => lastSource == MediaPlatform.YouTube.ToDisplayString()
        || lastSource == MediaPlatform.Browser.ToDisplayString();

    /// <summary>
    /// Maintains the empty-metadata grace window. Returns <c>hold == true</c> when the current (empty)
    /// pass should be deferred to avoid flicker during a brief metadata gap, along with the updated
    /// start-time and stable-signature tracking values.
    /// </summary>
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

    /// <summary>
    /// Debounces a not-yet-playing new track for <paramref name="debounceMs"/> so a paused scrub does
    /// not publish prematurely. Returns <c>debounce == true</c> when publishing should be deferred,
    /// along with the updated pending-track key and timestamp.
    /// </summary>
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

    /// <summary>
    /// Holds a recently-confirmed artist for browser/YouTube sources that briefly report a generic
    /// artist label. Returns the (possibly substituted) artist to display and the next stable-artist
    /// value to remember.
    /// </summary>
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
            // Substitute the remembered artist; keep the stable value as-is.
            return (stableArtist, stableArtist);
        }

        if (!string.IsNullOrEmpty(currentArtist) && !isGeneric)
        {
            // A real artist — remember it as the new stable value.
            return (currentArtist, currentArtist);
        }

        return (currentArtist, stableArtist);
    }

    /// <summary>
    /// Decides whether a freshly-observed Browser pass should keep the previously-confirmed SoundCloud
    /// source instead of reverting to a generic Browser source during a rapid track switch. All gates
    /// must pass: source is Browser, last source was SoundCloud, the track/app/session look like a real
    /// browser session that matches the last-published session, the metadata change is recent, there is
    /// no YouTube hint, and any existing session override is not pointing somewhere other than SoundCloud.
    /// </summary>
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
