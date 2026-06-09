using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class PublishedTrackMatcherTests
{
    private static PublishedTrackSnapshot Snapshot(
        string trackIdentity = "",
        string trackOnlyIdentity = "",
        string sourceAppId = "",
        string sessionInstanceKey = "")
        => new(trackIdentity, trackOnlyIdentity, sourceAppId, sessionInstanceKey);

    #region IsSameTrack

    [Fact]
    public void IsSameTrack_FullIdentityMatch_True()
    {
        var last = Snapshot(trackIdentity: "song|artist");
        Assert.True(PublishedTrackMatcher.IsSameTrack(last, "Song", "Artist", "", ""));
    }

    [Fact]
    public void IsSameTrack_SessionKeyMismatch_False()
    {
        var last = Snapshot(trackIdentity: "song|artist", sessionInstanceKey: "session-A");
        Assert.False(PublishedTrackMatcher.IsSameTrack(last, "Song", "Artist", "", "session-B"));
    }

    [Fact]
    public void IsSameTrack_SessionKeyGuard_SkippedWhenExpectedEmpty()
    {
        var last = Snapshot(trackIdentity: "song|artist", sessionInstanceKey: "session-A");
        Assert.True(PublishedTrackMatcher.IsSameTrack(last, "Song", "Artist", "", ""));
    }

    [Fact]
    public void IsSameTrack_SourceAppMismatch_False()
    {
        var last = Snapshot(trackIdentity: "song|artist", sourceAppId: "app.one");
        Assert.False(PublishedTrackMatcher.IsSameTrack(last, "Song", "Artist", "app.two", ""));
    }

    [Fact]
    public void IsSameTrack_SourceApp_CaseInsensitive()
    {
        var last = Snapshot(trackIdentity: "song|artist", sourceAppId: "App.One");
        Assert.True(PublishedTrackMatcher.IsSameTrack(last, "Song", "Artist", "app.one", ""));
    }

    [Fact]
    public void IsSameTrack_TrackOnlyFallback_WhenArtistUnknown()
    {
        var last = Snapshot(trackIdentity: "song|someartist", trackOnlyIdentity: "song|");
        Assert.True(PublishedTrackMatcher.IsSameTrack(last, "Song", "", "", ""));
    }

    [Fact]
    public void IsSameTrack_NoTrackOnlyFallback_WhenArtistProvided()
    {
        var last = Snapshot(trackIdentity: "song|other", trackOnlyIdentity: "song|");
        Assert.False(PublishedTrackMatcher.IsSameTrack(last, "Song", "Artist", "", ""));
    }

    #endregion

    #region IsNewTrackForThumbnail

    [Fact]
    public void IsNewTrackForThumbnail_DifferentTrack_True()
    {
        Assert.True(PublishedTrackMatcher.IsNewTrackForThumbnail("song|", "Different Song"));
    }

    [Fact]
    public void IsNewTrackForThumbnail_SameTrack_False()
    {
        Assert.False(PublishedTrackMatcher.IsNewTrackForThumbnail("song|", "Song"));
    }

    [Fact]
    public void IsNewTrackForThumbnail_EmptyCurrent_False()
    {
        Assert.False(PublishedTrackMatcher.IsNewTrackForThumbnail("song|", ""));
    }

    #endregion
}
