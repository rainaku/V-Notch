namespace VNotch.Services;

/// <summary>
/// Resolved inputs for <see cref="ThumbnailFetchPlanner.ClassifySources"/>. The caller resolves the
/// platform, the (IO-bound) "likely YouTube" / session-override / open-browser-tab hints, and the
/// current thumbnail's placeholder state into plain booleans; the classification itself is pure.
/// </summary>
internal readonly struct ThumbnailSourceInputs
{
    public bool PlatformIsYouTube { get; init; }
    public bool PlatformIsBrowser { get; init; }
    public bool PlatformIsSoundCloud { get; init; }
    public bool HasSourceApp { get; init; }
    /// <summary>Window/URL/cache heuristics suggest the browser session is YouTube.</summary>
    public bool IsLikelyYouTube { get; init; }
    /// <summary>A session override has previously confirmed this browser session as SoundCloud.</summary>
    public bool HasSoundCloudSessionOverride { get; init; }
    /// <summary>A visible browser tab currently shows a YouTube URL (hard gate against SoundCloud probing).</summary>
    public bool BrowserHasYouTubeTabOpen { get; init; }
    public bool HasTrack { get; init; }
    /// <summary>The current thumbnail is missing or looks like a SoundCloud placeholder/avatar.</summary>
    public bool ThumbnailIsNullOrPlaceholder { get; init; }
}

/// <summary>Which remote artwork lookups are worth attempting for the current track.</summary>
internal readonly struct ThumbnailSourcePlan
{
    public bool IsPotentialYouTube { get; init; }
    public bool IsPotentialSoundCloud { get; init; }
}

/// <summary>
/// Resolved inputs for <see cref="ThumbnailFetchPlanner.ShouldStartSoundCloudFetch"/>. Captures the
/// SoundCloud-specific tracking state (new-track, in-flight fetch, retry interval) the caller reads
/// from instance fields and the clock.
/// </summary>
internal readonly struct SoundCloudFetchInputs
{
    /// <summary>The SoundCloud track identity differs from the last one we attempted artwork for.</summary>
    public bool IsNewSoundCloudTrack { get; init; }
    /// <summary>A fetch for this exact track identity is already running.</summary>
    public bool SameTrackFetchRunning { get; init; }
    /// <summary>The current thumbnail is missing or a placeholder.</summary>
    public bool ThumbnailIsNullOrPlaceholder { get; init; }
    /// <summary>The cached thumbnail source is not SoundCloud (so the displayed art is from elsewhere).</summary>
    public bool HasMismatchedThumbSource { get; init; }
    /// <summary>Enough time has passed since the last artwork attempt to retry.</summary>
    public bool RetryIntervalElapsed { get; init; }
}

/// <summary>
/// Pure thumbnail-fetch planning extracted from <see cref="MediaDetectionService.StartThumbnailFetchIfNeeded"/>.
/// It does not perform any IO, cancellation, caching, or task scheduling — it only decides which remote
/// artwork sources are worth probing and whether a SoundCloud fetch should be (re)started. The service
/// resolves the IO/instance hints into the input structs, then executes the side effects implied by the plan.
/// </summary>
internal static class ThumbnailFetchPlanner
{
    /// <summary>
    /// Classifies the candidate artwork sources for the current track. A browser-app session is treated
    /// as potential YouTube unless a SoundCloud override is present; a SoundCloud probe is offered for a
    /// browser session that is not YouTube, has a track, and currently shows missing/placeholder art (or
    /// a confirmed SoundCloud override). The two flags are independent — when both are set, the caller
    /// prefers the YouTube fetch.
    /// </summary>
    public static ThumbnailSourcePlan ClassifySources(in ThumbnailSourceInputs x)
    {
        bool isPotentialYouTube = x.PlatformIsYouTube || (x.PlatformIsBrowser && x.IsLikelyYouTube);

        bool isBrowserApp = x.PlatformIsBrowser && x.HasSourceApp;
        if (isBrowserApp && !isPotentialYouTube && !x.HasSoundCloudSessionOverride)
        {
            isPotentialYouTube = true;
        }

        bool shouldProbeSoundCloudFromBrowser = x.PlatformIsBrowser &&
                                                !x.IsLikelyYouTube &&
                                                !x.BrowserHasYouTubeTabOpen &&
                                                x.HasTrack &&
                                                (x.ThumbnailIsNullOrPlaceholder || x.HasSoundCloudSessionOverride);

        bool isPotentialSoundCloud = x.PlatformIsSoundCloud || shouldProbeSoundCloudFromBrowser;

        return new ThumbnailSourcePlan
        {
            IsPotentialYouTube = isPotentialYouTube,
            IsPotentialSoundCloud = isPotentialSoundCloud,
        };
    }

    /// <summary>
    /// Decides whether to start a SoundCloud artwork fetch. A fetch already running for the same track
    /// is left alone (no duplicate). Otherwise a fetch starts when the track is new, or when the current
    /// art is missing/placeholder/mismatched and the retry interval has elapsed.
    /// </summary>
    public static bool ShouldStartSoundCloudFetch(in SoundCloudFetchInputs x)
    {
        if (!x.IsNewSoundCloudTrack && x.SameTrackFetchRunning)
        {
            return false;
        }

        bool shouldRetryPlaceholder = (x.ThumbnailIsNullOrPlaceholder || x.HasMismatchedThumbSource) &&
                                      x.RetryIntervalElapsed;

        return x.IsNewSoundCloudTrack || shouldRetryPlaceholder;
    }
}
