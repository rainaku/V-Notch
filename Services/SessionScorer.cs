using System;

namespace VNotch.Services;

/// <summary>
/// Pure, deterministic scoring inputs for a single SMTC session candidate. All WinRT reads,
/// clock access, and <c>MediaSessionState</c> side-effects are resolved by the caller; this
/// struct captures only the resolved values the scoring formula depends on.
/// </summary>
internal readonly struct SessionScoreInputs
{
    /// <summary>Session media properties reported a non-empty title.</summary>
    public bool HasTitle { get; init; }
    /// <summary>Session media properties reported a non-empty artist.</summary>
    public bool HasArtist { get; init; }
    /// <summary>Session media properties carried a thumbnail.</summary>
    public bool HasThumbnail { get; init; }
    /// <summary>Artist is present and is not a generic source placeholder ("YouTube"/"Browser").</summary>
    public bool ArtistIsNonGeneric { get; init; }

    public bool IsSpotify { get; init; }
    public bool IsMusic { get; init; }
    public bool IsYouTube { get; init; }
    public bool IsBrowser { get; init; }

    /// <summary>This session's app id matches the OS "current" session.</summary>
    public bool IsOsCurrent { get; init; }
    /// <summary>A dedicated music app (Spotify/Music) is actively playing alongside this browser/YouTube session.</summary>
    public bool DedicatedMusicAppPlaying { get; init; }
    /// <summary>This session is currently in the Playing state.</summary>
    public bool IsActive { get; init; }

    /// <summary>Seconds since this session started playing, when known (active sessions only).</summary>
    public double? PlayStartAgeSeconds { get; init; }
    /// <summary>Seconds since the most-recent globally-tracked playback start, when this is that session.</summary>
    public double? LatestPlayingAgeSeconds { get; init; }
    /// <summary>Seconds since this app last played, when a last-playing time is known.</summary>
    public double? LastPlayingIdleSeconds { get; init; }
    /// <summary>Seconds since the session timeline last updated, when a timeline is present.</summary>
    public double? TimelineAgeSeconds { get; init; }

    /// <summary>Timeline is advancing (or recently advanced) for an active session.</summary>
    public bool TimelineBoost { get; init; }
    /// <summary>Timeline is stalled for an active session.</summary>
    public bool TimelinePenalty { get; init; }
}

/// <summary>
/// Pure session-selection scoring extracted from <see cref="MediaDetectionService"/>. Produces the
/// exact same integer score the inline algorithm did, but with no I/O so it is fully unit-testable.
/// Higher scores win. Score contributions are additive, so evaluation order is irrelevant.
/// </summary>
internal static class SessionScorer
{
    public static int Score(in SessionScoreInputs x)
    {
        int score = 0;

        // Metadata presence
        if (x.HasTitle)
        {
            score += 50;
            if (x.HasArtist) score += 20;
            if (x.HasThumbnail) score += 10;
        }

        // Source-kind preference
        if (x.IsSpotify) score += 400;
        else if (x.IsMusic) score += 400;
        else if (x.IsYouTube) score += 350;
        else if (x.IsBrowser) score += 100;

        // OS-current session bonus (reduced for browser/YouTube when a dedicated music app plays)
        if (x.IsOsCurrent)
        {
            score += x.DedicatedMusicAppPlaying ? 200 : 1000;
        }

        // Active playback bonus
        if (x.IsActive)
        {
            score += 500;
            if (x.IsOsCurrent && !(x.IsBrowser || x.IsYouTube)) score += 1000;
        }

        // Recently-started playback decays over 45s
        if (x.PlayStartAgeSeconds is double playStartAge && playStartAge >= 0 && playStartAge < 45)
        {
            score += (int)Math.Max(0, 2600 - (playStartAge * 45));
        }

        // Most-recent globally-tracked playback decays over 30s
        if (x.LatestPlayingAgeSeconds is double latestAge && latestAge >= 0 && latestAge < 30)
        {
            score += (int)Math.Max(0, 2200 - (latestAge * 60));
        }

        // Idle recency
        if (x.LastPlayingIdleSeconds is double idleSeconds && idleSeconds < 30)
        {
            score += (int)((30 - idleSeconds) * 10);
        }

        // Fresh timeline
        if (x.TimelineAgeSeconds is double timelineAge && timelineAge >= 0 && timelineAge < 20)
        {
            score += (int)Math.Max(0, 200 - (timelineAge * 8));
        }

        // Timeline advance / stall
        if (x.TimelineBoost) score += 3000;
        else if (x.TimelinePenalty) score -= 3000;

        // Strong metadata bonus
        if (x.HasTitle)
        {
            score += 1500;
            if (x.ArtistIsNonGeneric) score += 200;
        }

        return score;
    }
}
