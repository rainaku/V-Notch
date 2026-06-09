using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class TimelinePositionSolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // A simple, fresh, paused-position baseline: 3-minute track, position 30s, timeline just updated.
    private static TimelineSolveInputs Baseline => new()
    {
        StartTime = TimeSpan.Zero,
        EndTime = TimeSpan.FromMinutes(3),
        MaxSeekTime = TimeSpan.FromMinutes(3),
        Position = TimeSpan.FromSeconds(30),
        LastUpdatedUtc = Now,
        IsInitialOrBigChange = false,
        IsNewTrack = false,
        IsBrowserTimelineTrack = false,
        IsPlaying = false,
        PlaybackRate = 1.0,
        NowUtc = Now,
    };

    [Fact]
    public void Duration_FromEndMinusStart()
    {
        var r = TimelinePositionSolver.Solve(Baseline with { StartTime = TimeSpan.FromSeconds(10), EndTime = TimeSpan.FromSeconds(190) });
        Assert.Equal(TimeSpan.FromSeconds(180), r.Duration);
    }

    [Fact]
    public void Duration_FallsBackToMaxSeekWhenEndNotAfterStart()
    {
        var r = TimelinePositionSolver.Solve(Baseline with
        {
            StartTime = TimeSpan.Zero,
            EndTime = TimeSpan.Zero,
            MaxSeekTime = TimeSpan.FromSeconds(240),
            Position = TimeSpan.FromSeconds(10),
        });
        Assert.Equal(TimeSpan.FromSeconds(240), r.Duration);
    }

    [Fact]
    public void NotPlaying_PassesPositionThroughUnchanged()
    {
        var r = TimelinePositionSolver.Solve(Baseline);
        Assert.Equal(TimeSpan.FromSeconds(30), r.Position);
        Assert.Equal(Now, r.LastUpdatedUtc); // no force-start, no compensation
    }

    [Fact]
    public void NewTrackNearEnd_SnapsToStart()
    {
        // Position within 800ms of the end on a new track → reset to zero and force-start.
        var r = TimelinePositionSolver.Solve(Baseline with
        {
            IsNewTrack = true,
            Position = TimeSpan.FromMinutes(3) - TimeSpan.FromMilliseconds(500),
        });

        Assert.Equal(TimeSpan.Zero, r.Position);
        Assert.False(r.IsIndeterminate); // explicitly cleared
        Assert.Equal(Now, r.LastUpdatedUtc); // force-start anchors to now
    }

    [Fact]
    public void BrowserNewTrack_SuspiciousCarryOverWithStaleTimeline_ResetsToStart()
    {
        // A browser track that "starts" at 90s with a stale (>900ms) timeline is a carried-over position.
        var r = TimelinePositionSolver.Solve(Baseline with
        {
            IsBrowserTimelineTrack = true,
            IsNewTrack = true,
            IsPlaying = true,
            Position = TimeSpan.FromSeconds(90),
            LastUpdatedUtc = Now - TimeSpan.FromSeconds(2), // stale
        });

        Assert.Equal(TimeSpan.Zero, r.Position);
        Assert.Equal(Now, r.LastUpdatedUtc);
    }

    [Fact]
    public void BrowserNewTrack_FreshTimeline_KeepsPosition()
    {
        // Same carried-over position but a fresh timeline (<900ms) is trusted → no reset.
        var r = TimelinePositionSolver.Solve(Baseline with
        {
            IsBrowserTimelineTrack = true,
            IsNewTrack = true,
            IsPlaying = true,
            Position = TimeSpan.FromSeconds(90),
            LastUpdatedUtc = Now - TimeSpan.FromMilliseconds(300), // fresh
        });

        // Position is compensated forward by the 300ms latency (>100ms gate) rather than reset.
        Assert.True(r.Position >= TimeSpan.FromSeconds(90));
        Assert.True(r.Position < TimeSpan.FromSeconds(91));
    }

    [Fact]
    public void Playing_CompensatesForTimelineLatency()
    {
        // Playing, timeline updated 2s ago at rate 1.0 → position advances ~2s.
        var r = TimelinePositionSolver.Solve(Baseline with
        {
            IsPlaying = true,
            Position = TimeSpan.FromSeconds(30),
            LastUpdatedUtc = Now - TimeSpan.FromSeconds(2),
        });

        Assert.Equal(32.0, r.Position.TotalSeconds, 2);
        Assert.Equal(Now, r.LastUpdatedUtc); // compensation re-anchors last-updated to now
    }

    [Fact]
    public void Playing_CompensationRespectsPlaybackRate()
    {
        var r = TimelinePositionSolver.Solve(Baseline with
        {
            IsPlaying = true,
            PlaybackRate = 2.0,
            Position = TimeSpan.FromSeconds(30),
            LastUpdatedUtc = Now - TimeSpan.FromSeconds(2),
        });

        // 2s latency * 2.0 rate = ~4s advance.
        Assert.Equal(34.0, r.Position.TotalSeconds, 2);
    }

    [Fact]
    public void Playing_CompensationCappedAt95PercentOfDuration()
    {
        // Huge latency would overshoot the end; compensation is capped at 95% of duration.
        var r = TimelinePositionSolver.Solve(Baseline with
        {
            IsPlaying = true,
            IsInitialOrBigChange = true, // widens the compensation window to duration+5s
            Position = TimeSpan.FromSeconds(150),
            LastUpdatedUtc = Now - TimeSpan.FromSeconds(120),
        });

        // 95% of 180s = 171s.
        Assert.Equal(171.0, r.Position.TotalSeconds, 2);
    }

    [Fact]
    public void Playing_SmallLatencyBelowGate_NoCompensation()
    {
        // Latency <= 100ms is ignored.
        var r = TimelinePositionSolver.Solve(Baseline with
        {
            IsPlaying = true,
            Position = TimeSpan.FromSeconds(30),
            LastUpdatedUtc = Now - TimeSpan.FromMilliseconds(50),
        });

        Assert.Equal(TimeSpan.FromSeconds(30), r.Position);
    }

    [Fact]
    public void Playing_StaleBeyondWindow_NoCompensation()
    {
        // Non-initial, non-browser: window is 15s. A 20s-old timeline is out of window → ignored.
        var r = TimelinePositionSolver.Solve(Baseline with
        {
            IsPlaying = true,
            Position = TimeSpan.FromSeconds(30),
            LastUpdatedUtc = Now - TimeSpan.FromSeconds(20),
        });

        Assert.Equal(TimeSpan.FromSeconds(30), r.Position);
        Assert.Equal(Now - TimeSpan.FromSeconds(20), r.LastUpdatedUtc); // unchanged
    }

    [Fact]
    public void PositionClampedToDuration()
    {
        var r = TimelinePositionSolver.Solve(Baseline with
        {
            Position = TimeSpan.FromMinutes(5), // beyond the 3-minute duration
        });

        Assert.Equal(TimeSpan.FromMinutes(3), r.Position);
    }

    [Fact]
    public void UnknownDuration_MarksIndeterminate()
    {
        var r = TimelinePositionSolver.Solve(Baseline with
        {
            EndTime = TimeSpan.Zero,
            MaxSeekTime = TimeSpan.Zero,
            Position = TimeSpan.Zero,
        });

        Assert.Equal((bool?)true, r.IsIndeterminate);
    }

    [Fact]
    public void AbsurdDuration_MarksIndeterminate()
    {
        var r = TimelinePositionSolver.Solve(Baseline with
        {
            EndTime = TimeSpan.FromDays(40),
            MaxSeekTime = TimeSpan.FromDays(40),
        });

        Assert.Equal((bool?)true, r.IsIndeterminate);
    }

    [Fact]
    public void NormalDuration_LeavesIndeterminateUntouched()
    {
        var r = TimelinePositionSolver.Solve(Baseline);
        Assert.Null(r.IsIndeterminate);
    }
}
