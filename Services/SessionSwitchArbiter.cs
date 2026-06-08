namespace VNotch.Services;

/// <summary>
/// Resolved inputs for the session-switch hold decision. All WinRT reads (playback status, timeline
/// freshness), clock access, and instance-state lookups are performed by the caller; this struct
/// captures only the booleans the decision depends on, keeping the rule pure and unit-testable.
/// </summary>
internal readonly struct SessionSwitchInputs
{
    /// <summary>The currently-displayed session belongs to a dedicated music app (Spotify / Music).</summary>
    public bool CurrentIsPremium { get; init; }
    /// <summary>The best-scored candidate is the OS "current" session.</summary>
    public bool BestIsOsCurrent { get; init; }
    /// <summary>The best-scored candidate started playing very recently (the most-recent global playback start).</summary>
    public bool BestIsRecentLatestPlayback { get; init; }
    /// <summary>The currently-displayed session is still in the Playing state.</summary>
    public bool CurrentStillPlaying { get; init; }
    /// <summary>The best-scored candidate has a fresh (recently-advanced) timeline.</summary>
    public bool BestHasFreshTimeline { get; init; }
    /// <summary>Seconds elapsed since the candidate first became the pending switch target.</summary>
    public double PendingElapsedSeconds { get; init; }
}

/// <summary>
/// Pure session-switch arbitration extracted from <see cref="MediaDetectionService"/>'s session
/// resolver. After scoring picks a best candidate, this decides whether to <em>hold</em> the
/// currently-displayed session a little longer (debounce) instead of switching immediately. Holding
/// avoids flicker when a transient session briefly out-scores the one the user is actually watching.
/// </summary>
internal static class SessionSwitchArbiter
{
    /// <summary>
    /// How long to hold the current session before allowing a switch. Dedicated music apps get a
    /// longer window (4s) than browser/video sources (1.5s) because their playback is more stable.
    /// </summary>
    public static double HoldSeconds(bool currentIsPremium) => currentIsPremium ? 4.0 : 1.5;

    /// <summary>
    /// Returns true when the switch to the best candidate should be deferred and the current session
    /// kept. The hold only applies while the current session is still playing and within the hold
    /// window; it is bypassed immediately if the candidate is the OS-current session, started playing
    /// very recently, or has a fresher timeline than the current session.
    /// </summary>
    public static bool ShouldHoldCurrentSession(in SessionSwitchInputs x)
    {
        return !x.BestIsRecentLatestPlayback
            && !x.BestIsOsCurrent
            && !x.BestHasFreshTimeline
            && x.CurrentStillPlaying
            && x.PendingElapsedSeconds < HoldSeconds(x.CurrentIsPremium);
    }
}
