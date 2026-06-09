using System;

namespace VNotch.Services;

internal readonly struct SessionScoreInputs
{
    public bool HasTitle { get; init; }
    public bool HasArtist { get; init; }
    public bool HasThumbnail { get; init; }
    public bool ArtistIsNonGeneric { get; init; }

    public bool IsSpotify { get; init; }
    public bool IsMusic { get; init; }
    public bool IsYouTube { get; init; }
    public bool IsBrowser { get; init; }

    public bool IsOsCurrent { get; init; }
    public bool DedicatedMusicAppPlaying { get; init; }
    public bool IsActive { get; init; }

    public double? PlayStartAgeSeconds { get; init; }
    public double? LatestPlayingAgeSeconds { get; init; }
    public double? LastPlayingIdleSeconds { get; init; }
    public double? TimelineAgeSeconds { get; init; }

    public bool TimelineBoost { get; init; }
    public bool TimelinePenalty { get; init; }
}

internal static class SessionScorer
{
    public static int Score(in SessionScoreInputs x)
    {
        int score = 0;

        if (x.HasTitle)
        {
            score += 50;
            if (x.HasArtist) score += 20;
            if (x.HasThumbnail) score += 10;
        }

        if (x.IsSpotify) score += 400;
        else if (x.IsMusic) score += 400;
        else if (x.IsYouTube) score += 350;
        else if (x.IsBrowser) score += 100;

        if (x.IsOsCurrent)
        {
            score += x.DedicatedMusicAppPlaying ? 200 : 1000;
        }

        if (x.IsActive)
        {
            score += 500;
            if (x.IsOsCurrent && !(x.IsBrowser || x.IsYouTube)) score += 1000;
        }

        if (x.PlayStartAgeSeconds is double playStartAge && playStartAge >= 0 && playStartAge < 45)
        {
            score += (int)Math.Max(0, 2600 - (playStartAge * 45));
        }

        if (x.LatestPlayingAgeSeconds is double latestAge && latestAge >= 0 && latestAge < 30)
        {
            score += (int)Math.Max(0, 2200 - (latestAge * 60));
        }

        if (x.LastPlayingIdleSeconds is double idleSeconds && idleSeconds < 30)
        {
            score += (int)((30 - idleSeconds) * 10);
        }

        if (x.TimelineAgeSeconds is double timelineAge && timelineAge >= 0 && timelineAge < 20)
        {
            score += (int)Math.Max(0, 200 - (timelineAge * 8));
        }

        if (x.TimelineBoost) score += 3000;
        else if (x.TimelinePenalty) score -= 3000;

        if (x.HasTitle)
        {
            score += 1500;
            if (x.ArtistIsNonGeneric) score += 200;
        }

        return score;
    }
}
