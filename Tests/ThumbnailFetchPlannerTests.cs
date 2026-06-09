using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class ThumbnailFetchPlannerTests
{
    #region ClassifySources

    [Fact]
    public void Classify_YouTubePlatform_IsPotentialYouTube()
    {
        var plan = ThumbnailFetchPlanner.ClassifySources(new ThumbnailSourceInputs
        {
            PlatformIsYouTube = true,
            HasTrack = true,
        });

        Assert.True(plan.IsPotentialYouTube);
        Assert.False(plan.IsPotentialSoundCloud);
    }

    [Fact]
    public void Classify_BrowserLikelyYouTube_IsPotentialYouTube()
    {
        var plan = ThumbnailFetchPlanner.ClassifySources(new ThumbnailSourceInputs
        {
            PlatformIsBrowser = true,
            HasSourceApp = true,
            IsLikelyYouTube = true,
            HasTrack = true,
        });

        Assert.True(plan.IsPotentialYouTube);
    }

    [Fact]
    public void Classify_BrowserApp_DefaultsToYouTube_WhenNoSoundCloudOverride()
    {
        // A browser-app session that isn't obviously YouTube is still treated as potential YouTube.
        var plan = ThumbnailFetchPlanner.ClassifySources(new ThumbnailSourceInputs
        {
            PlatformIsBrowser = true,
            HasSourceApp = true,
            IsLikelyYouTube = false,
            HasSoundCloudSessionOverride = false,
            HasTrack = true,
            ThumbnailIsNullOrPlaceholder = true,
        });

        Assert.True(plan.IsPotentialYouTube);
        // Still offers a SoundCloud probe (both flags set; caller prefers YouTube).
        Assert.True(plan.IsPotentialSoundCloud);
    }

    [Fact]
    public void Classify_BrowserApp_WithSoundCloudOverride_NotYouTube()
    {
        var plan = ThumbnailFetchPlanner.ClassifySources(new ThumbnailSourceInputs
        {
            PlatformIsBrowser = true,
            HasSourceApp = true,
            IsLikelyYouTube = false,
            HasSoundCloudSessionOverride = true,
            HasTrack = true,
            ThumbnailIsNullOrPlaceholder = true,
        });

        Assert.False(plan.IsPotentialYouTube);
        Assert.True(plan.IsPotentialSoundCloud); // override → probe SoundCloud
    }

    [Fact]
    public void Classify_BrowserNoApp_NotForcedYouTube()
    {
        // Without a source app, the browser-app default-to-YouTube bump does not apply.
        var plan = ThumbnailFetchPlanner.ClassifySources(new ThumbnailSourceInputs
        {
            PlatformIsBrowser = true,
            HasSourceApp = false,
            IsLikelyYouTube = false,
            HasTrack = true,
            ThumbnailIsNullOrPlaceholder = true,
        });

        Assert.False(plan.IsPotentialYouTube);
        Assert.True(plan.IsPotentialSoundCloud);
    }

    [Fact]
    public void Classify_SoundCloudPlatform_IsPotentialSoundCloud()
    {
        var plan = ThumbnailFetchPlanner.ClassifySources(new ThumbnailSourceInputs
        {
            PlatformIsSoundCloud = true,
            HasTrack = true,
        });

        Assert.True(plan.IsPotentialSoundCloud);
        Assert.False(plan.IsPotentialYouTube);
    }

    [Fact]
    public void Classify_BrowserYouTubeTabOpen_BlocksSoundCloudProbe()
    {
        var plan = ThumbnailFetchPlanner.ClassifySources(new ThumbnailSourceInputs
        {
            PlatformIsBrowser = true,
            HasSourceApp = true,
            IsLikelyYouTube = false,
            BrowserHasYouTubeTabOpen = true,
            HasTrack = true,
            ThumbnailIsNullOrPlaceholder = true,
        });

        Assert.False(plan.IsPotentialSoundCloud);
    }

    [Fact]
    public void Classify_BrowserSoundCloudProbe_RequiresTrack()
    {
        var plan = ThumbnailFetchPlanner.ClassifySources(new ThumbnailSourceInputs
        {
            PlatformIsBrowser = true,
            HasSourceApp = true,
            IsLikelyYouTube = false,
            HasTrack = false, // no track → no probe
            ThumbnailIsNullOrPlaceholder = true,
        });

        Assert.False(plan.IsPotentialSoundCloud);
    }

    [Fact]
    public void Classify_BrowserWithGoodThumbnail_NoSoundCloudProbe()
    {
        // Browser, likely-YouTube false but with a good (non-placeholder) thumbnail and no override:
        // the SoundCloud probe is not offered.
        var plan = ThumbnailFetchPlanner.ClassifySources(new ThumbnailSourceInputs
        {
            PlatformIsBrowser = true,
            HasSourceApp = true,
            IsLikelyYouTube = false,
            HasTrack = true,
            ThumbnailIsNullOrPlaceholder = false,
            HasSoundCloudSessionOverride = false,
        });

        Assert.False(plan.IsPotentialSoundCloud);
    }

    #endregion

    #region ShouldStartSoundCloudFetch

    [Fact]
    public void SoundCloudFetch_NewTrack_Starts()
    {
        Assert.True(ThumbnailFetchPlanner.ShouldStartSoundCloudFetch(new SoundCloudFetchInputs
        {
            IsNewSoundCloudTrack = true,
        }));
    }

    [Fact]
    public void SoundCloudFetch_NewTrack_StartsEvenIfSameFetchRunning()
    {
        // A genuinely new track overrides the "already running" guard.
        Assert.True(ThumbnailFetchPlanner.ShouldStartSoundCloudFetch(new SoundCloudFetchInputs
        {
            IsNewSoundCloudTrack = true,
            SameTrackFetchRunning = true,
        }));
    }

    [Fact]
    public void SoundCloudFetch_SameTrackRunning_DoesNotStart()
    {
        Assert.False(ThumbnailFetchPlanner.ShouldStartSoundCloudFetch(new SoundCloudFetchInputs
        {
            IsNewSoundCloudTrack = false,
            SameTrackFetchRunning = true,
            ThumbnailIsNullOrPlaceholder = true,
            RetryIntervalElapsed = true,
        }));
    }

    [Fact]
    public void SoundCloudFetch_PlaceholderRetry_StartsWhenIntervalElapsed()
    {
        Assert.True(ThumbnailFetchPlanner.ShouldStartSoundCloudFetch(new SoundCloudFetchInputs
        {
            ThumbnailIsNullOrPlaceholder = true,
            RetryIntervalElapsed = true,
        }));
    }

    [Fact]
    public void SoundCloudFetch_MismatchedSourceRetry_StartsWhenIntervalElapsed()
    {
        Assert.True(ThumbnailFetchPlanner.ShouldStartSoundCloudFetch(new SoundCloudFetchInputs
        {
            HasMismatchedThumbSource = true,
            RetryIntervalElapsed = true,
        }));
    }

    [Fact]
    public void SoundCloudFetch_RetryBlockedUntilIntervalElapses()
    {
        Assert.False(ThumbnailFetchPlanner.ShouldStartSoundCloudFetch(new SoundCloudFetchInputs
        {
            ThumbnailIsNullOrPlaceholder = true,
            RetryIntervalElapsed = false,
        }));
    }

    [Fact]
    public void SoundCloudFetch_GoodThumbnail_DoesNotStart()
    {
        // Not new, good art, nothing mismatched → no fetch.
        Assert.False(ThumbnailFetchPlanner.ShouldStartSoundCloudFetch(new SoundCloudFetchInputs
        {
            ThumbnailIsNullOrPlaceholder = false,
            HasMismatchedThumbSource = false,
            RetryIntervalElapsed = true,
        }));
    }

    #endregion
}
