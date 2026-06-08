using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class MediaTimingDecisionsTests
{
    private static readonly DateTime Base = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    #region EvaluateEmptyMetadataHold

    [Fact]
    public void EmptyMetadataHold_NonEmptyTrack_ResetsAndStoresSignature()
    {
        var (hold, emptyStart, stable) = MediaTimingDecisions.EvaluateEmptyMetadataHold(
            currentTrack: "Song", isAnyMediaPlaying: true, currentSignature: "sig",
            lastSource: "Spotify", emptyStart: Base.AddSeconds(-10), stableSignature: "old", now: Base);

        Assert.False(hold);
        Assert.Equal(DateTime.MinValue, emptyStart);
        Assert.Equal("sig", stable);
    }

    [Fact]
    public void EmptyMetadataHold_EmptyTrack_NotPlaying_ClearsState()
    {
        var (hold, emptyStart, stable) = MediaTimingDecisions.EvaluateEmptyMetadataHold(
            currentTrack: "", isAnyMediaPlaying: false, currentSignature: "sig",
            lastSource: "Spotify", emptyStart: Base, stableSignature: "old", now: Base);

        Assert.False(hold);
        Assert.Equal(DateTime.MinValue, emptyStart);
        Assert.Equal("", stable);
    }

    [Fact]
    public void EmptyMetadataHold_EmptyPlaying_WithinWindow_Holds()
    {
        var (hold, emptyStart, _) = MediaTimingDecisions.EvaluateEmptyMetadataHold(
            currentTrack: "", isAnyMediaPlaying: true, currentSignature: "sig",
            lastSource: "Spotify", emptyStart: DateTime.MinValue, stableSignature: "sig", now: Base);

        Assert.True(hold);
        Assert.Equal(Base, emptyStart); // start time anchored to now on first empty pass
    }

    [Fact]
    public void EmptyMetadataHold_EmptyPlaying_NoStableSignature_DoesNotHold()
    {
        var (hold, _, _) = MediaTimingDecisions.EvaluateEmptyMetadataHold(
            currentTrack: "", isAnyMediaPlaying: true, currentSignature: "sig",
            lastSource: "Spotify", emptyStart: DateTime.MinValue, stableSignature: "", now: Base);

        Assert.False(hold);
    }

    [Fact]
    public void EmptyMetadataHold_NonVideo_ExpiresAfter2Point5s()
    {
        // 3s elapsed > 2.5s window for a non-video source → no longer holds.
        var (hold, _, _) = MediaTimingDecisions.EvaluateEmptyMetadataHold(
            currentTrack: "", isAnyMediaPlaying: true, currentSignature: "sig",
            lastSource: "Spotify", emptyStart: Base.AddSeconds(-3), stableSignature: "sig", now: Base);

        Assert.False(hold);
    }

    [Fact]
    public void EmptyMetadataHold_VideoSource_GetsLongerWindow()
    {
        // Same 3s elapsed still holds for YouTube/Browser because their window is 4s.
        var (holdYouTube, _, _) = MediaTimingDecisions.EvaluateEmptyMetadataHold(
            currentTrack: "", isAnyMediaPlaying: true, currentSignature: "sig",
            lastSource: "YouTube", emptyStart: Base.AddSeconds(-3), stableSignature: "sig", now: Base);
        Assert.True(holdYouTube);

        var (holdBrowser, _, _) = MediaTimingDecisions.EvaluateEmptyMetadataHold(
            currentTrack: "", isAnyMediaPlaying: true, currentSignature: "sig",
            lastSource: "Browser", emptyStart: Base.AddSeconds(-3), stableSignature: "sig", now: Base);
        Assert.True(holdBrowser);
    }

    #endregion

    #region EvaluateNewTrackDebounce

    [Fact]
    public void NewTrackDebounce_NotANewTrack_NoDebounce()
    {
        var (debounce, pendingKey, _) = MediaTimingDecisions.EvaluateNewTrackDebounce(
            currentTrack: "Song", currentArtist: "Artist", isPlaying: false, forceRefresh: false,
            lastPublishedTrackIdentity: "song|artist", pendingKey: "", pendingSince: Base, nowUtc: Base);

        Assert.False(debounce);
        Assert.Equal("", pendingKey);
    }

    [Fact]
    public void NewTrackDebounce_NewTrackButPlaying_NoDebounce()
    {
        var (debounce, _, _) = MediaTimingDecisions.EvaluateNewTrackDebounce(
            currentTrack: "New", currentArtist: "Artist", isPlaying: true, forceRefresh: false,
            lastPublishedTrackIdentity: "old|artist", pendingKey: "", pendingSince: Base, nowUtc: Base);

        Assert.False(debounce);
    }

    [Fact]
    public void NewTrackDebounce_FirstObservation_DebouncesAndAnchorsTime()
    {
        var (debounce, pendingKey, pendingSince) = MediaTimingDecisions.EvaluateNewTrackDebounce(
            currentTrack: "New Song", currentArtist: "Artist", isPlaying: false, forceRefresh: false,
            lastPublishedTrackIdentity: "old|artist", pendingKey: "", pendingSince: DateTime.MinValue, nowUtc: Base);

        Assert.True(debounce);
        Assert.Equal("new song|artist", pendingKey);
        Assert.Equal(Base, pendingSince);
    }

    [Fact]
    public void NewTrackDebounce_WindowElapsed_StopsDebouncing()
    {
        var (debounce, _, _) = MediaTimingDecisions.EvaluateNewTrackDebounce(
            currentTrack: "New Song", currentArtist: "Artist", isPlaying: false, forceRefresh: false,
            lastPublishedTrackIdentity: "old|artist",
            pendingKey: "new song|artist", pendingSince: Base.AddMilliseconds(-700), nowUtc: Base);

        Assert.False(debounce); // 700ms > 600ms debounce window
    }

    [Fact]
    public void NewTrackDebounce_ForceRefresh_Bypasses()
    {
        var (debounce, _, _) = MediaTimingDecisions.EvaluateNewTrackDebounce(
            currentTrack: "New", currentArtist: "Artist", isPlaying: false, forceRefresh: true,
            lastPublishedTrackIdentity: "old|artist", pendingKey: "", pendingSince: Base, nowUtc: Base);

        Assert.False(debounce);
    }

    #endregion

    #region EvaluateArtistStabilization

    [Fact]
    public void ArtistStabilization_GenericArtist_WithinWindow_SubstitutesStable()
    {
        var (artist, stable) = MediaTimingDecisions.EvaluateArtistStabilization(
            currentArtist: "YouTube", stableArtist: "Real Artist",
            lastSourceConfirmedTime: Base.AddSeconds(-5), now: Base);

        Assert.Equal("Real Artist", artist);
        Assert.Equal("Real Artist", stable);
    }

    [Fact]
    public void ArtistStabilization_GenericArtist_WindowExpired_KeepsGeneric()
    {
        var (artist, stable) = MediaTimingDecisions.EvaluateArtistStabilization(
            currentArtist: "YouTube", stableArtist: "Real Artist",
            lastSourceConfirmedTime: Base.AddSeconds(-20), now: Base);

        Assert.Equal("YouTube", artist);
        Assert.Equal("Real Artist", stable); // stable unchanged
    }

    [Fact]
    public void ArtistStabilization_RealArtist_RemembersAsStable()
    {
        var (artist, stable) = MediaTimingDecisions.EvaluateArtistStabilization(
            currentArtist: "Adele", stableArtist: "old",
            lastSourceConfirmedTime: Base.AddSeconds(-5), now: Base);

        Assert.Equal("Adele", artist);
        Assert.Equal("Adele", stable);
    }

    [Fact]
    public void ArtistStabilization_GenericArtist_NoStable_LeavesUnchanged()
    {
        var (artist, stable) = MediaTimingDecisions.EvaluateArtistStabilization(
            currentArtist: "YouTube", stableArtist: "",
            lastSourceConfirmedTime: Base.AddSeconds(-5), now: Base);

        Assert.Equal("YouTube", artist);
        Assert.Equal("", stable);
    }

    #endregion

    #region ShouldPreserveSoundCloud

    // Builds the all-gates-pass argument set; individual tests override one parameter to flip a gate.
    private static bool PreserveSoundCloud(
        string mediaSource = "Browser",
        string currentTrack = "Some Song",
        string currentArtist = "Some Artist",
        string sourceAppId = "Chrome",
        string sessionInstanceKey = "sess-1",
        string lastSource = "SoundCloud",
        string lastPublishedSessionInstanceKey = "sess-1",
        double lastMetadataChangeSecondsAgo = 1.0,
        bool hasSessionOverride = false,
        string sessionOverride = "")
        => MediaTimingDecisions.ShouldPreserveSoundCloud(
            mediaSource, currentTrack, currentArtist, sourceAppId, sessionInstanceKey,
            lastSource, lastPublishedSessionInstanceKey,
            Base.AddSeconds(-lastMetadataChangeSecondsAgo), Base,
            hasSessionOverride, sessionOverride);

    [Fact]
    public void ShouldPreserveSoundCloud_AllGatesPass_True()
    {
        Assert.True(PreserveSoundCloud());
    }

    [Fact]
    public void ShouldPreserveSoundCloud_SourceNotBrowser_False()
    {
        Assert.False(PreserveSoundCloud(mediaSource: "Spotify"));
    }

    [Fact]
    public void ShouldPreserveSoundCloud_LastSourceNotSoundCloud_False()
    {
        Assert.False(PreserveSoundCloud(lastSource: "YouTube"));
    }

    [Fact]
    public void ShouldPreserveSoundCloud_EmptyTrack_False()
    {
        Assert.False(PreserveSoundCloud(currentTrack: "  "));
    }

    [Fact]
    public void ShouldPreserveSoundCloud_NonBrowserApp_False()
    {
        Assert.False(PreserveSoundCloud(sourceAppId: "Spotify.exe"));
    }

    [Fact]
    public void ShouldPreserveSoundCloud_SessionKeyMismatch_False()
    {
        Assert.False(PreserveSoundCloud(sessionInstanceKey: "sess-2")); // last published was sess-1
    }

    [Fact]
    public void ShouldPreserveSoundCloud_StaleMetadata_False()
    {
        Assert.False(PreserveSoundCloud(lastMetadataChangeSecondsAgo: 5.0)); // > 3s freshness window
    }

    [Fact]
    public void ShouldPreserveSoundCloud_YouTubeHintInTrack_False()
    {
        Assert.False(PreserveSoundCloud(currentTrack: "watch on youtube"));
    }

    [Fact]
    public void ShouldPreserveSoundCloud_SessionOverridePointsElsewhere_False()
    {
        Assert.False(PreserveSoundCloud(hasSessionOverride: true, sessionOverride: "YouTube"));
    }

    [Fact]
    public void ShouldPreserveSoundCloud_SessionOverrideIsSoundCloud_True()
    {
        // An override that already says SoundCloud is not a blocker.
        Assert.True(PreserveSoundCloud(hasSessionOverride: true, sessionOverride: "SoundCloud"));
    }

    #endregion
}
