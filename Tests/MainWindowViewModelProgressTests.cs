using System;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Models;
using VNotch.Tests.Fakes;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

/// <summary>
/// Characterization tests pinning the CURRENT progress-prediction, seek-debounce, drag,
/// and volume-ratio behavior of <see cref="MainWindowViewModel"/> before the progress
/// engine moves into ProgressController (Phase 4 / Task 15).
///
/// The VM reads <see cref="DateTime.Now"/> directly (time is not abstracted), so the
/// cap-seconds branches are pinned by making the elapsed time far exceed the cap — once
/// the predicted advance is clamped to the cap it becomes exact and deterministic.
/// The displayed position is driven via <c>MediaInfo.LastUpdated</c>, which the VM copies
/// into its internal <c>_lastMediaUpdate</c> on a timeline update.
///
/// Validates: Requirements 6.1, 6.2, 6.3, 6.4, 1.4
/// </summary>
public class MainWindowViewModelProgressTests
{
    private readonly FakeMediaDetectionService _media = new();
    private readonly FakeVolumeService _volume = new(isAvailable: true);
    private readonly MainWindowViewModel _vm;

    public MainWindowViewModelProgressTests()
    {
        _vm = new MainWindowViewModel(
            _media,
            new FakeSettingsService(),
            _volume,
            new FakeBatteryService(),
            new FakeDispatcherService());

        // RenderProgressBar only runs when the notch is "expanded".
        _vm.IsExpandedCheck = () => true;
    }

    private static MediaInfo Timeline(
        string source,
        double durationSeconds,
        double positionSeconds = 0,
        bool playing = true,
        bool throttled = false,
        DateTimeOffset? lastUpdated = null,
        string track = "T",
        string artist = "Artist",
        string appId = "app1")
        => new MediaInfo
        {
            IsAnyMediaPlaying = true,
            IsPlaying = playing,
            IsThrottled = throttled,
            IsIndeterminate = false,
            IsSeekEnabled = true,
            CurrentTrack = track,
            CurrentArtist = artist,
            MediaSource = source,
            SourceAppId = appId,
            Duration = TimeSpan.FromSeconds(durationSeconds),
            Position = TimeSpan.FromSeconds(positionSeconds),
            PlaybackRate = 1.0,
            LastUpdated = lastUpdated ?? DateTimeOffset.Now
        };

    // ─── Cap-seconds branches in RenderProgressBar ───
    // elapsed = 1 hour, so the predicted advance is clamped to the cap, exactly.

    [Fact]
    public void RenderProgressBar_DefaultSource_CapsPredictionAt30Seconds()
    {
        var info = Timeline("Spotify", durationSeconds: 100_000, positionSeconds: 0,
            lastUpdated: DateTimeOffset.Now.AddHours(-1));

        _media.RaiseMediaChanged(info);

        Assert.Equal("0:30", _vm.CurrentTimeText);
    }

    [Fact]
    public void RenderProgressBar_YouTube_CapsPredictionAt600Seconds()
    {
        var info = Timeline("YouTube", durationSeconds: 100_000, positionSeconds: 0,
            lastUpdated: DateTimeOffset.Now.AddHours(-1));

        _media.RaiseMediaChanged(info);

        Assert.Equal("10:00", _vm.CurrentTimeText);
    }

    [Fact]
    public void RenderProgressBar_Browser_CapsPredictionAt600Seconds()
    {
        var info = Timeline("Browser", durationSeconds: 100_000, positionSeconds: 0,
            lastUpdated: DateTimeOffset.Now.AddHours(-1));

        _media.RaiseMediaChanged(info);

        Assert.Equal("10:00", _vm.CurrentTimeText);
    }

    [Fact]
    public void RenderProgressBar_Throttled_CapsPredictionAt3600Seconds()
    {
        var info = Timeline("Spotify", durationSeconds: 100_000, positionSeconds: 0,
            throttled: true, lastUpdated: DateTimeOffset.Now.AddHours(-1));

        _media.RaiseMediaChanged(info);

        // 3600s formats with the hours branch of FormatTime.
        Assert.Equal("1:00:00", _vm.CurrentTimeText);
    }

    // ─── Ratio clamp [0,1] ───

    [Fact]
    public void RenderProgressBar_PredictionPastDuration_ClampsRatioToOne()
    {
        var info = Timeline("Spotify", durationSeconds: 10, positionSeconds: 0,
            throttled: true, lastUpdated: DateTimeOffset.Now.AddHours(-1));

        _media.RaiseMediaChanged(info);

        Assert.Equal(1.0, _vm.ProgressRatio, 3);
        Assert.Equal("0:10", _vm.CurrentTimeText); // capped to duration
    }

    [Fact]
    public void RenderProgressBar_NegativePausedPosition_ClampsToZero()
    {
        var info = Timeline("Spotify", durationSeconds: 100, positionSeconds: -5, playing: false,
            lastUpdated: DateTimeOffset.Now);

        _media.RaiseMediaChanged(info);

        Assert.Equal(0.0, _vm.ProgressRatio, 3);
        Assert.Equal("0:00", _vm.CurrentTimeText);
    }

    // ─── FormatTime: m:ss vs h:mm:ss (via RemainingTimeText = FormatTime(duration)) ───

    [Fact]
    public void FormatTime_UnderOneHour_UsesMinuteSecondFormat()
    {
        var info = Timeline("Spotify", durationSeconds: 90, positionSeconds: 0, playing: false,
            lastUpdated: DateTimeOffset.Now);

        _media.RaiseMediaChanged(info);

        Assert.Equal("1:30", _vm.RemainingTimeText);
    }

    [Fact]
    public void FormatTime_OverOneHour_UsesHourMinuteSecondFormat()
    {
        var info = Timeline("Spotify", durationSeconds: 3661, positionSeconds: 0, playing: false,
            lastUpdated: DateTimeOffset.Now);

        _media.RaiseMediaChanged(info);

        Assert.Equal("1:01:01", _vm.RemainingTimeText);
    }

    [Fact]
    public void RenderProgressBar_ZeroDuration_ShowsDashes()
    {
        // Indeterminate stream → no known duration → RenderProgressBar emits placeholders.
        var info = new MediaInfo
        {
            IsAnyMediaPlaying = true,
            IsPlaying = true,
            IsIndeterminate = true,
            CurrentTrack = "T",
            CurrentArtist = "Artist",
            MediaSource = "Spotify",
            SourceAppId = "app1",
            Duration = TimeSpan.Zero,
            Position = TimeSpan.Zero,
            LastUpdated = DateTimeOffset.Now
        };

        _media.RaiseMediaChanged(info);

        Assert.Equal("--:--", _vm.CurrentTimeText);
        Assert.Equal("--:--", _vm.RemainingTimeText);
        Assert.Equal(0.0, _vm.ProgressRatio, 3);
    }

    // ─── Seek debounce window (2.5s) ───

    [Fact]
    public async Task SeekDebounce_IgnoresIncomingPositionWithinWindow()
    {
        // Initial paused position at 10s.
        _media.RaiseMediaChanged(Timeline("Spotify", 100, positionSeconds: 10, playing: false,
            lastUpdated: DateTimeOffset.Now));
        Assert.Equal("0:10", _vm.CurrentTimeText);

        // Seek to 50s — opens the 2.5s debounce window.
        await _vm.SeekToPosition(TimeSpan.FromSeconds(50));
        Assert.Equal(TimeSpan.FromSeconds(50), _media.LastSeek);
        Assert.Equal("0:50", _vm.CurrentTimeText);

        // A stale SMTC update (still reporting 10s) for the same track arrives within the window.
        _media.RaiseMediaChanged(Timeline("Spotify", 100, positionSeconds: 10, playing: false,
            lastUpdated: DateTimeOffset.Now));

        // The seeked position is retained; the stale 10s is ignored.
        Assert.Equal("0:50", _vm.CurrentTimeText);
    }

    [Fact]
    public async Task SeekDebounce_AcceptsIncomingPositionAfterWindowExpires()
    {
        _media.RaiseMediaChanged(Timeline("Spotify", 100, positionSeconds: 10, playing: false,
            lastUpdated: DateTimeOffset.Now));

        await _vm.SeekToPosition(TimeSpan.FromSeconds(50));
        Assert.Equal("0:50", _vm.CurrentTimeText);

        // Wait past the 2.5s debounce window.
        Thread.Sleep(2600);

        _media.RaiseMediaChanged(Timeline("Spotify", 100, positionSeconds: 10, playing: false,
            lastUpdated: DateTimeOffset.Now));

        // Window expired → the incoming position is accepted again.
        Assert.Equal("0:10", _vm.CurrentTimeText);
    }

    [Fact]
    public async Task SeekRelative_OpensDebounceAndSeeksAbsolute()
    {
        _media.RaiseMediaChanged(Timeline("Spotify", 100, positionSeconds: 10, playing: false,
            lastUpdated: DateTimeOffset.Now));

        await _vm.SeekRelative(20); // 10 + 20 = 30 (paused → no elapsed prediction)

        Assert.Equal(TimeSpan.FromSeconds(30), _media.LastSeekAbsolute);
        Assert.Equal("0:30", _vm.CurrentTimeText);

        // Stale update within the window is ignored.
        _media.RaiseMediaChanged(Timeline("Spotify", 100, positionSeconds: 10, playing: false,
            lastUpdated: DateTimeOffset.Now));
        Assert.Equal("0:30", _vm.CurrentTimeText);
    }

    [Fact]
    public async Task SeekRelative_ClampsToDurationBounds()
    {
        _media.RaiseMediaChanged(Timeline("Spotify", 100, positionSeconds: 90, playing: false,
            lastUpdated: DateTimeOffset.Now));

        await _vm.SeekRelative(1000); // would overshoot → clamp to duration
        Assert.Equal(TimeSpan.FromSeconds(100), _media.LastSeekAbsolute);

        await _vm.SeekRelative(-5000); // would undershoot → clamp to zero
        Assert.Equal(TimeSpan.Zero, _media.LastSeekAbsolute);
    }

    // ─── Drag handling ───

    [Fact]
    public void Drag_IgnoresMediaUpdatesWhileDragging()
    {
        _media.RaiseMediaChanged(Timeline("Spotify", 100, positionSeconds: 20, playing: false,
            lastUpdated: DateTimeOffset.Now));

        _vm.StartDraggingProgress();
        _vm.UpdateDragPosition(0.75);
        Assert.Equal(0.75, _vm.ProgressRatio, 3);
        Assert.Equal("1:15", _vm.CurrentTimeText);

        // Incoming media update while dragging must be ignored.
        _media.RaiseMediaChanged(Timeline("Spotify", 100, positionSeconds: 90, playing: false,
            lastUpdated: DateTimeOffset.Now));
        Assert.Equal(0.75, _vm.ProgressRatio, 3);
        Assert.Equal("1:15", _vm.CurrentTimeText);

        // RenderProgressBar is also frozen during drag.
        _vm.RenderProgressBar();
        Assert.Equal(0.75, _vm.ProgressRatio, 3);

        _vm.StopDraggingProgress();
    }

    [Fact]
    public void UpdateDragPosition_ClampsRatio()
    {
        _media.RaiseMediaChanged(Timeline("Spotify", 100, positionSeconds: 0, playing: false,
            lastUpdated: DateTimeOffset.Now));

        _vm.StartDraggingProgress();

        _vm.UpdateDragPosition(1.5); // clamp high
        Assert.Equal(1.0, _vm.ProgressRatio, 3);

        _vm.UpdateDragPosition(-0.5); // clamp low
        Assert.Equal(0.0, _vm.ProgressRatio, 3);

        _vm.StopDraggingProgress();
    }

    [Fact]
    public void GetDragSeekPosition_MapsAndClampsRatioToDuration()
    {
        _media.RaiseMediaChanged(Timeline("Spotify", 100, positionSeconds: 0, playing: false,
            lastUpdated: DateTimeOffset.Now));

        Assert.Equal(TimeSpan.FromSeconds(50), _vm.GetDragSeekPosition(0.5));
        Assert.Equal(TimeSpan.FromSeconds(100), _vm.GetDragSeekPosition(1.5)); // clamp high
        Assert.Equal(TimeSpan.Zero, _vm.GetDragSeekPosition(-0.5));            // clamp low
    }

    // ─── Volume ratio → icon thresholds ───

    [Fact]
    public void SetVolumeFromRatio_MidRange_SetsVolumeAndIcon()
    {
        _vm.SetVolumeFromRatio(0.5f);

        Assert.Equal(0.5f, _vm.CurrentVolume);
        Assert.Equal(0.5f, _volume.LastSetVolume);
        Assert.Equal("\uE994", _vm.VolumeIconText); // 0.33 ≤ v < 0.66
    }

    [Fact]
    public void SetVolumeFromRatio_ClampsAboveOne()
    {
        _vm.SetVolumeFromRatio(1.5f);

        Assert.Equal(1.0f, _vm.CurrentVolume);
        Assert.Equal("\uE995", _vm.VolumeIconText); // v ≥ 0.66
    }

    [Fact]
    public void SetVolumeFromRatio_ClampsBelowZero_ShowsMutedIcon()
    {
        _vm.SetVolumeFromRatio(-0.5f);

        Assert.Equal(0f, _vm.CurrentVolume);
        Assert.Equal("\uE74F", _vm.VolumeIconText); // v ≤ 0.01
    }

    [Fact]
    public void SetVolumeFromRatio_LowRange_ShowsLowIcon()
    {
        _vm.SetVolumeFromRatio(0.1f);

        Assert.Equal("\uE993", _vm.VolumeIconText); // 0.01 < v < 0.33
    }
}
