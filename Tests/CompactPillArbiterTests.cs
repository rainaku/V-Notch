using VNotch.Controllers;
using Xunit;

namespace VNotch.Tests;

/// <summary>
/// Characterization tests pinning the CURRENT slot-contention behavior of
/// <see cref="CompactPillArbiter"/> before it moves under a coordinator (Phase 2/Task 7).
/// Numeric slot order: None=0, Clipboard=1, Volume=2, Bluetooth=3, Charging=4, Greeting=5.
/// A request is rejected only when a strictly higher-numbered slot is already active.
/// Validates: Requirements 6.1, 6.4, 1.4
/// </summary>
public class CompactPillArbiterTests
{
    private readonly CompactPillArbiter _arbiter = new();

    #region Initial / IsActive

    [Fact]
    public void InitialState_NoSlotActive()
    {
        Assert.Equal(CompactPillSlot.None, _arbiter.ActiveSlot);
        Assert.Equal(0, _arbiter.ActiveToken);
        Assert.False(_arbiter.IsActive(CompactPillSlot.Volume));
    }

    [Fact]
    public void IsActive_TrueForAcquiredSlot()
    {
        _arbiter.TryAcquire(CompactPillSlot.Volume);
        Assert.True(_arbiter.IsActive(CompactPillSlot.Volume));
        Assert.False(_arbiter.IsActive(CompactPillSlot.Clipboard));
    }

    #endregion

    #region CanAcquire

    [Fact]
    public void CanAcquire_None_IsFalse()
    {
        Assert.False(_arbiter.CanAcquire(CompactPillSlot.None));
    }

    [Fact]
    public void CanAcquire_AnySlot_WhenIdle_IsTrue()
    {
        Assert.True(_arbiter.CanAcquire(CompactPillSlot.Clipboard));
        Assert.True(_arbiter.CanAcquire(CompactPillSlot.Greeting));
    }

    [Fact]
    public void CanAcquire_LowerNumberedSlot_WhenHigherActive_IsFalse()
    {
        _arbiter.TryAcquire(CompactPillSlot.Volume); // 2
        Assert.False(_arbiter.CanAcquire(CompactPillSlot.Clipboard)); // 1 < 2 → blocked
    }

    [Fact]
    public void CanAcquire_HigherOrEqualNumberedSlot_WhenActive_IsTrue()
    {
        _arbiter.TryAcquire(CompactPillSlot.Volume); // 2
        Assert.True(_arbiter.CanAcquire(CompactPillSlot.Volume));    // equal
        Assert.True(_arbiter.CanAcquire(CompactPillSlot.Bluetooth)); // 3 ≥ 2
    }

    #endregion

    #region TryAcquire

    [Fact]
    public void TryAcquire_None_Fails()
    {
        var result = _arbiter.TryAcquire(CompactPillSlot.None);
        Assert.False(result.Won);
        Assert.Equal(0, result.Token);
        Assert.Equal(CompactPillSlot.None, result.Preempted);
    }

    [Fact]
    public void TryAcquire_WhenIdle_WinsWithFirstToken()
    {
        var result = _arbiter.TryAcquire(CompactPillSlot.Volume);
        Assert.True(result.Won);
        Assert.Equal(1, result.Token);
        Assert.Equal(CompactPillSlot.None, result.Preempted);
        Assert.Equal(CompactPillSlot.Volume, _arbiter.ActiveSlot);
        Assert.Equal(1, _arbiter.ActiveToken);
    }

    [Fact]
    public void TryAcquire_LowerNumberedSlot_IsRejected_ActiveUnchanged()
    {
        _arbiter.TryAcquire(CompactPillSlot.Volume); // 2
        var result = _arbiter.TryAcquire(CompactPillSlot.Clipboard); // 1 < 2

        Assert.False(result.Won);
        Assert.Equal(0, result.Token);
        Assert.Equal(CompactPillSlot.Volume, result.Preempted); // reports the blocker
        Assert.Equal(CompactPillSlot.Volume, _arbiter.ActiveSlot);
    }

    [Fact]
    public void TryAcquire_HigherNumberedSlot_PreemptsAndIncrementsToken()
    {
        _arbiter.TryAcquire(CompactPillSlot.Volume); // token 1
        var result = _arbiter.TryAcquire(CompactPillSlot.Bluetooth); // 3 ≥ 2

        Assert.True(result.Won);
        Assert.Equal(2, result.Token);
        Assert.Equal(CompactPillSlot.Volume, result.Preempted);
        Assert.Equal(CompactPillSlot.Bluetooth, _arbiter.ActiveSlot);
    }

    [Fact]
    public void TryAcquire_SameSlotAgain_NotReportedAsPreempted()
    {
        _arbiter.TryAcquire(CompactPillSlot.Volume);
        var result = _arbiter.TryAcquire(CompactPillSlot.Volume);

        Assert.True(result.Won);
        Assert.Equal(CompactPillSlot.None, result.Preempted); // same slot is not "preempted"
        Assert.Equal(2, result.Token);
    }

    #endregion

    #region Release / tokens / ForceClear

    [Fact]
    public void Release_WithCurrentToken_ClearsSlot()
    {
        var result = _arbiter.TryAcquire(CompactPillSlot.Volume);
        _arbiter.Release(result.Token);

        Assert.Equal(CompactPillSlot.None, _arbiter.ActiveSlot);
        Assert.Equal(0, _arbiter.ActiveToken);
    }

    [Fact]
    public void Release_WithStaleToken_IsNoOp()
    {
        var first = _arbiter.TryAcquire(CompactPillSlot.Volume);     // token 1
        _arbiter.TryAcquire(CompactPillSlot.Bluetooth);              // token 2 (preempts)

        _arbiter.Release(first.Token); // stale → must not clear the live Bluetooth slot

        Assert.Equal(CompactPillSlot.Bluetooth, _arbiter.ActiveSlot);
        Assert.Equal(2, _arbiter.ActiveToken);
    }

    [Fact]
    public void Release_ZeroToken_IsNoOp()
    {
        _arbiter.TryAcquire(CompactPillSlot.Volume);
        _arbiter.Release(0);
        Assert.Equal(CompactPillSlot.Volume, _arbiter.ActiveSlot);
    }

    [Fact]
    public void IsTokenCurrent_TracksLatestAcquire()
    {
        var first = _arbiter.TryAcquire(CompactPillSlot.Volume);
        Assert.True(_arbiter.IsTokenCurrent(first.Token));

        var second = _arbiter.TryAcquire(CompactPillSlot.Bluetooth);
        Assert.False(_arbiter.IsTokenCurrent(first.Token));
        Assert.True(_arbiter.IsTokenCurrent(second.Token));
        Assert.False(_arbiter.IsTokenCurrent(0));
    }

    [Fact]
    public void ForceClear_ResetsSlotAndToken()
    {
        _arbiter.TryAcquire(CompactPillSlot.Greeting);
        _arbiter.ForceClear();

        Assert.Equal(CompactPillSlot.None, _arbiter.ActiveSlot);
        Assert.Equal(0, _arbiter.ActiveToken);
    }

    [Fact]
    public void Token_IsMonotonic_AcrossAcquireReleaseCycles()
    {
        var a = _arbiter.TryAcquire(CompactPillSlot.Volume);
        _arbiter.Release(a.Token);
        var b = _arbiter.TryAcquire(CompactPillSlot.Volume);

        Assert.Equal(1, a.Token);
        Assert.Equal(2, b.Token); // never reused
    }

    #endregion
}
