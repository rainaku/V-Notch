using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class MediaSourceClassifierTests
{
    #region ApplyFromAppId

    [Fact]
    public void ApplyFromAppId_EmptyAppId_LeavesSourceUnset()
    {
        var info = new MediaInfo();
        MediaSourceClassifier.ApplyFromAppId(info, "");
        Assert.Equal("", info.MediaSource);
    }

    [Fact]
    public void ApplyFromAppId_Spotify_SetsSourceAndFlags()
    {
        var info = new MediaInfo();
        MediaSourceClassifier.ApplyFromAppId(info, "Spotify.exe");

        Assert.Equal("Spotify", info.MediaSource);
        Assert.True(info.IsSpotifyPlaying);
        Assert.True(info.IsSpotifyRunning);
    }

    [Fact]
    public void ApplyFromAppId_YouTube_SetsSourceAndFlag()
    {
        var info = new MediaInfo();
        MediaSourceClassifier.ApplyFromAppId(info, "com.YouTube.desktop");

        Assert.Equal("YouTube", info.MediaSource);
        Assert.True(info.IsYouTubeRunning);
    }

    [Theory]
    [InlineData("Chrome")]
    [InlineData("msedge")]
    [InlineData("Brave.Browser")]
    public void ApplyFromAppId_BrowserApp_ResolvesToBrowser(string appId)
    {
        var info = new MediaInfo();
        MediaSourceClassifier.ApplyFromAppId(info, appId);
        Assert.Equal("Browser", info.MediaSource);
    }

    [Theory]
    [InlineData("AppleMusic")]
    [InlineData("Apple.Music.Preview")]
    [InlineData("Microsoft.Music")]
    public void ApplyFromAppId_AppleMusic_SetsSourceAndFlag(string appId)
    {
        var info = new MediaInfo();
        MediaSourceClassifier.ApplyFromAppId(info, appId);

        Assert.Equal("Apple Music", info.MediaSource);
        Assert.True(info.IsAppleMusicRunning);
    }

    [Fact]
    public void ApplyFromAppId_UnknownNonBrowser_FallsBackToBrowser()
    {
        var info = new MediaInfo();
        MediaSourceClassifier.ApplyFromAppId(info, "com.unknown.player");
        Assert.Equal("Browser", info.MediaSource);
    }

    #endregion

    #region RefineFromMetadata

    [Fact]
    public void RefineFromMetadata_ResolvedNonBrowserSource_NoOp()
    {
        var info = new MediaInfo { MediaSource = "Spotify" };
        MediaSourceClassifier.RefineFromMetadata(info, "anything youtube", "", "");
        Assert.Equal("Spotify", info.MediaSource); // unchanged
    }

    [Theory]
    [InlineData("watch this - youtube", "", "")]
    [InlineData("", "uploaded by youtube", "")]
    [InlineData("", "", "youtube")]
    public void RefineFromMetadata_BrowserWithYouTubeHint_ResolvesYouTube(string title, string artist, string album)
    {
        var info = new MediaInfo { MediaSource = "Browser" };
        MediaSourceClassifier.RefineFromMetadata(info, title, artist, album);

        Assert.Equal("YouTube", info.MediaSource);
        Assert.True(info.IsYouTubeRunning);
    }

    [Fact]
    public void RefineFromMetadata_EmptySourceWithAppleHint_ResolvesAppleMusic()
    {
        var info = new MediaInfo { MediaSource = "" };
        MediaSourceClassifier.RefineFromMetadata(info, "", "", "music.apple.com");

        Assert.Equal("Apple Music", info.MediaSource);
        Assert.True(info.IsAppleMusicRunning);
    }

    [Fact]
    public void RefineFromMetadata_BrowserWithSoundCloudHint_ResolvesSoundCloud()
    {
        var info = new MediaInfo { MediaSource = "Browser" };
        MediaSourceClassifier.RefineFromMetadata(info, "", "soundcloud", "");

        Assert.Equal("SoundCloud", info.MediaSource);
        Assert.True(info.IsSoundCloudRunning);
    }

    [Fact]
    public void RefineFromMetadata_BrowserWithNoHint_StaysBrowser()
    {
        var info = new MediaInfo { MediaSource = "Browser" };
        MediaSourceClassifier.RefineFromMetadata(info, "regular song", "regular artist", "regular album");

        Assert.Equal("Browser", info.MediaSource);
        Assert.False(info.IsYouTubeRunning);
        Assert.False(info.IsSoundCloudRunning);
    }

    #endregion

    #region TryHandleJunkTitle

    [Theory]
    [InlineData("")]
    [InlineData("spotify")]
    [InlineData("Spotify Premium")]
    [InlineData("advertisement")]
    [InlineData("Windows Media Player")]
    [InlineData("chrome")]
    [InlineData("firefox")]
    public void TryHandleJunkTitle_JunkValues_ReturnsTrue(string title)
    {
        var info = new MediaInfo();
        Assert.True(MediaSourceClassifier.TryHandleJunkTitle(info, title, ""));
    }

    [Fact]
    public void TryHandleJunkTitle_YouTubeTitleWithRealArtist_NotJunk()
    {
        // "youtube" title is only junk when the artist is also empty/youtube.
        var info = new MediaInfo();
        Assert.False(MediaSourceClassifier.TryHandleJunkTitle(info, "youtube", "Rick Astley"));
    }

    [Fact]
    public void TryHandleJunkTitle_RealTitle_ReturnsFalse()
    {
        var info = new MediaInfo();
        Assert.False(MediaSourceClassifier.TryHandleJunkTitle(info, "Never Gonna Give You Up", "Rick Astley"));
    }

    [Fact]
    public void TryHandleJunkTitle_JunkOnYouTubeSource_ClearsTrackAndTagsArtist()
    {
        var info = new MediaInfo { MediaSource = "YouTube", CurrentTrack = "stale", CurrentArtist = "stale" };
        bool handled = MediaSourceClassifier.TryHandleJunkTitle(info, "advertisement", "");

        Assert.True(handled);
        Assert.Equal("", info.CurrentTrack);
        Assert.Equal("YouTube", info.CurrentArtist);
    }

    [Fact]
    public void TryHandleJunkTitle_JunkOnNonYouTubeSource_LeavesTrackUntouched()
    {
        var info = new MediaInfo { MediaSource = "Spotify", CurrentTrack = "keepme", CurrentArtist = "keepme" };
        bool handled = MediaSourceClassifier.TryHandleJunkTitle(info, "spotify", "");

        Assert.True(handled);
        Assert.Equal("keepme", info.CurrentTrack); // untouched
    }

    #endregion
}
