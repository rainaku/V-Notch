using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class SessionScorerTests
{
    private static readonly SessionScoreInputs Empty = new();

    [Fact]
    public void Empty_ScoresZero()
    {
        Assert.Equal(0, SessionScorer.Score(Empty));
    }

    #region Metadata presence

    [Fact]
    public void Title_AddsBaseAndStrongBonus()
    {
        Assert.Equal(1550, SessionScorer.Score(Empty with { HasTitle = true }));
    }

    [Fact]
    public void Artist_OnlyCountsWithTitle()
    {
        Assert.Equal(0, SessionScorer.Score(Empty with { HasArtist = true }));
        Assert.Equal(1570, SessionScorer.Score(Empty with { HasTitle = true, HasArtist = true }));
    }

    [Fact]
    public void Thumbnail_OnlyCountsWithTitle()
    {
        Assert.Equal(0, SessionScorer.Score(Empty with { HasThumbnail = true }));
        Assert.Equal(1560, SessionScorer.Score(Empty with { HasTitle = true, HasThumbnail = true }));
    }

    [Fact]
    public void NonGenericArtist_OnlyCountsWithTitle()
    {
        Assert.Equal(0, SessionScorer.Score(Empty with { ArtistIsNonGeneric = true }));
        Assert.Equal(1750, SessionScorer.Score(Empty with { HasTitle = true, ArtistIsNonGeneric = true }));
    }

    #endregion

    #region Source kind (mutually exclusive, ordered)

    [Fact]
    public void SourceKind_AppliesHighestPriorityOnly()
    {
        Assert.Equal(400, SessionScorer.Score(Empty with { IsSpotify = true }));
        Assert.Equal(400, SessionScorer.Score(Empty with { IsMusic = true }));
        Assert.Equal(350, SessionScorer.Score(Empty with { IsYouTube = true }));
        Assert.Equal(100, SessionScorer.Score(Empty with { IsBrowser = true }));

        Assert.Equal(400, SessionScorer.Score(Empty with { IsSpotify = true, IsMusic = true }));
        Assert.Equal(350, SessionScorer.Score(Empty with { IsYouTube = true, IsBrowser = true }));
    }

    #endregion

    #region OS-current and active

    [Fact]
    public void OsCurrent_FullBonusUnlessDedicatedMusicPlaying()
    {
        Assert.Equal(1000, SessionScorer.Score(Empty with { IsOsCurrent = true }));
        Assert.Equal(200, SessionScorer.Score(Empty with { IsOsCurrent = true, DedicatedMusicAppPlaying = true }));
    }

    [Fact]
    public void Active_AddsBaseBonus()
    {
        Assert.Equal(500, SessionScorer.Score(Empty with { IsActive = true }));
    }

    [Fact]
    public void Active_OsCurrentNonBrowser_AddsExtra()
    {
        Assert.Equal(2500, SessionScorer.Score(Empty with { IsActive = true, IsOsCurrent = true }));
    }

    [Fact]
    public void Active_OsCurrentBrowser_NoExtra()
    {
        Assert.Equal(1600, SessionScorer.Score(Empty with { IsActive = true, IsOsCurrent = true, IsBrowser = true }));
    }

    #endregion

    #region Time-decay contributions

    [Theory]
    [InlineData(0.0, 2600)]
    [InlineData(10.0, 2150)]
    [InlineData(44.0, 620)]
    [InlineData(45.0, 0)]
    [InlineData(-1.0, 0)]
    public void PlayStartAge_DecaysOver45s(double age, int expected)
    {
        Assert.Equal(expected, SessionScorer.Score(Empty with { PlayStartAgeSeconds = age }));
    }

    [Theory]
    [InlineData(0.0, 2200)]
    [InlineData(10.0, 1600)]
    [InlineData(30.0, 0)]
    [InlineData(-1.0, 0)]
    public void LatestPlayingAge_DecaysOver30s(double age, int expected)
    {
        Assert.Equal(expected, SessionScorer.Score(Empty with { LatestPlayingAgeSeconds = age }));
    }

    [Theory]
    [InlineData(0.0, 300)]
    [InlineData(29.0, 10)]
    [InlineData(30.0, 0)]
    [InlineData(-5.0, 350)]
    public void LastPlayingIdle_AddsRecencyBonus(double idle, int expected)
    {
        Assert.Equal(expected, SessionScorer.Score(Empty with { LastPlayingIdleSeconds = idle }));
    }

    [Theory]
    [InlineData(0.0, 200)]
    [InlineData(10.0, 120)]
    [InlineData(20.0, 0)]
    [InlineData(-1.0, 0)]
    public void TimelineAge_AddsFreshnessBonus(double age, int expected)
    {
        Assert.Equal(expected, SessionScorer.Score(Empty with { TimelineAgeSeconds = age }));
    }

    #endregion

    #region Timeline advance / stall

    [Fact]
    public void TimelineBoost_AddsLargeBonus()
    {
        Assert.Equal(3000, SessionScorer.Score(Empty with { TimelineBoost = true }));
    }

    [Fact]
    public void TimelinePenalty_SubtractsLargeAmount()
    {
        Assert.Equal(-3000, SessionScorer.Score(Empty with { TimelinePenalty = true }));
    }

    [Fact]
    public void TimelineBoost_WinsOverPenalty()
    {
        Assert.Equal(3000, SessionScorer.Score(Empty with { TimelineBoost = true, TimelinePenalty = true }));
    }

    #endregion

    [Fact]
    public void CombinedRealisticActiveSpotify_SumsContributions()
    {
        var inputs = Empty with
        {
            HasTitle = true,
            HasArtist = true,
            HasThumbnail = true,
            ArtistIsNonGeneric = true,
            IsSpotify = true,
            IsOsCurrent = true,
            IsActive = true,
            PlayStartAgeSeconds = 0.0,
            TimelineAgeSeconds = 0.0,
            TimelineBoost = true,
        };

        Assert.Equal(10480, SessionScorer.Score(inputs));
    }
}
