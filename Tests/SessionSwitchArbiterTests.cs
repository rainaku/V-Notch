using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class SessionSwitchArbiterTests
{
    // All-gates-pass baseline: a non-premium current session, still playing, within the hold window,
    // and a candidate that is neither OS-current, recently-started, nor fresher. This holds.
    private static readonly SessionSwitchInputs HoldBaseline = new()
    {
        CurrentIsPremium = false,
        BestIsOsCurrent = false,
        BestIsRecentLatestPlayback = false,
        CurrentStillPlaying = true,
        BestHasFreshTimeline = false,
        PendingElapsedSeconds = 0.5,
    };

    [Fact]
    public void HoldSeconds_PremiumGetsLongerWindow()
    {
        Assert.Equal(4.0, SessionSwitchArbiter.HoldSeconds(currentIsPremium: true));
        Assert.Equal(1.5, SessionSwitchArbiter.HoldSeconds(currentIsPremium: false));
    }

    [Fact]
    public void Baseline_Holds()
    {
        Assert.True(SessionSwitchArbiter.ShouldHoldCurrentSession(HoldBaseline));
    }

    [Fact]
    public void CandidateIsOsCurrent_SwitchesImmediately()
    {
        Assert.False(SessionSwitchArbiter.ShouldHoldCurrentSession(HoldBaseline with { BestIsOsCurrent = true }));
    }

    [Fact]
    public void CandidateRecentlyStarted_SwitchesImmediately()
    {
        Assert.False(SessionSwitchArbiter.ShouldHoldCurrentSession(HoldBaseline with { BestIsRecentLatestPlayback = true }));
    }

    [Fact]
    public void CandidateHasFresherTimeline_SwitchesImmediately()
    {
        Assert.False(SessionSwitchArbiter.ShouldHoldCurrentSession(HoldBaseline with { BestHasFreshTimeline = true }));
    }

    [Fact]
    public void CurrentNotPlaying_SwitchesImmediately()
    {
        Assert.False(SessionSwitchArbiter.ShouldHoldCurrentSession(HoldBaseline with { CurrentStillPlaying = false }));
    }

    [Fact]
    public void NonPremium_HoldExpiresAfter1Point5s()
    {
        Assert.True(SessionSwitchArbiter.ShouldHoldCurrentSession(HoldBaseline with { PendingElapsedSeconds = 1.4 }));
        Assert.False(SessionSwitchArbiter.ShouldHoldCurrentSession(HoldBaseline with { PendingElapsedSeconds = 1.5 }));
        Assert.False(SessionSwitchArbiter.ShouldHoldCurrentSession(HoldBaseline with { PendingElapsedSeconds = 2.0 }));
    }

    [Fact]
    public void Premium_HoldsLongerUpTo4s()
    {
        var premium = HoldBaseline with { CurrentIsPremium = true };

        // Past the non-premium window but still within the premium window.
        Assert.True(SessionSwitchArbiter.ShouldHoldCurrentSession(premium with { PendingElapsedSeconds = 2.0 }));
        Assert.True(SessionSwitchArbiter.ShouldHoldCurrentSession(premium with { PendingElapsedSeconds = 3.9 }));
        Assert.False(SessionSwitchArbiter.ShouldHoldCurrentSession(premium with { PendingElapsedSeconds = 4.0 }));
    }
}
