using VNotch.Presenters;
using Xunit;

namespace VNotch.Tests;

/// <summary>
/// Unit tests for the calendar scroll/center index math extracted into
/// <see cref="CalendarScrollMath"/> (Task 4). All constructed without a live WPF Window.
/// </summary>
public class CalendarScrollMathTests
{
    private readonly CalendarScrollMath _math = new();

    #region Index <-> position round trips

    [Fact]
    public void GetStripXForIndex_DefaultCenter_MatchesLegacyFormula()
    {
        // Legacy: (1 * 30) - (idx * 30); default center index is 5.
        Assert.Equal(30.0 - (5 * 30.0), _math.GetStripXForIndex(5));
    }

    [Fact]
    public void GetCenterIndexFromStripX_InverseOfGetStripXForIndex()
    {
        for (int idx = 0; idx < CalendarScrollMath.TotalDays; idx++)
        {
            double stripX = _math.GetStripXForIndex(idx);
            Assert.Equal(idx, _math.GetCenterIndexFromStripX(stripX));
        }
    }

    [Fact]
    public void GetCenterIndexFromStripX_ClampsBelowZero()
    {
        // A very large positive stripX would yield a negative raw index; must clamp to 0.
        Assert.Equal(0, _math.GetCenterIndexFromStripX(10_000));
    }

    [Fact]
    public void GetCenterIndexFromStripX_ClampsAboveMax()
    {
        // A very large negative stripX would overshoot; must clamp to TotalDays - 1.
        Assert.Equal(CalendarScrollMath.TotalDays - 1, _math.GetCenterIndexFromStripX(-10_000));
    }

    [Fact]
    public void GetHighlightXForIndex_MatchesLegacyFormula()
    {
        // Legacy: centerIdx * 30 + (30 - 24) / 2.0
        Assert.Equal(5 * 30.0 + (30.0 - 24.0) / 2.0, _math.GetHighlightXForIndex(5));
        Assert.Equal(3.0, _math.GetHighlightXForIndex(0));
    }

    #endregion

    #region ClampIndex

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(5, 5)]
    [InlineData(10, 10)]
    [InlineData(11, 10)]
    [InlineData(99, 10)]
    public void ClampIndex_BoundsToValidRange(int input, int expected)
    {
        Assert.Equal(expected, _math.ClampIndex(input));
    }

    #endregion

    #region ComputeScrollStep

    [Fact]
    public void ComputeScrollStep_BelowThreshold_NoStep_AccumulatorRetainsDelta()
    {
        var r = _math.ComputeScrollStep(accumulator: 0, delta: 60, currentCenterIdx: 5);

        Assert.False(r.HasStep);
        Assert.Equal(0, r.StepCount);
        Assert.Equal(5, r.NewCenterIdx);
        Assert.False(r.IndexChanged);
        // Original returns early without draining the accumulator.
        Assert.Equal(60, r.ResultAccumulator);
    }

    [Fact]
    public void ComputeScrollStep_AtSoftThreshold72_AdvancesOneStepUp()
    {
        // 72 <= |acc| < 120 bumps stepCount to 1; positive delta scrolls toward earlier days (-1).
        var r = _math.ComputeScrollStep(accumulator: 0, delta: 72, currentCenterIdx: 5);

        Assert.True(r.HasStep);
        Assert.Equal(1, r.StepCount);
        Assert.Equal(4, r.NewCenterIdx);
        Assert.True(r.IndexChanged);
        Assert.Equal(72 - 120, r.ResultAccumulator);
    }

    [Fact]
    public void ComputeScrollStep_PositiveDelta120_MovesCenterDown_DrainsAccumulator()
    {
        var r = _math.ComputeScrollStep(accumulator: 0, delta: 120, currentCenterIdx: 5);

        Assert.Equal(1, r.StepCount);
        Assert.Equal(4, r.NewCenterIdx);
        Assert.Equal(0, r.ResultAccumulator);
    }

    [Fact]
    public void ComputeScrollStep_NegativeDelta120_MovesCenterUp_DrainsAccumulator()
    {
        var r = _math.ComputeScrollStep(accumulator: 0, delta: -120, currentCenterIdx: 5);

        Assert.Equal(1, r.StepCount);
        Assert.Equal(6, r.NewCenterIdx);
        Assert.Equal(0, r.ResultAccumulator);
    }

    [Fact]
    public void ComputeScrollStep_MultiStepDelta_AdvancesMultipleCells()
    {
        var r = _math.ComputeScrollStep(accumulator: 0, delta: 240, currentCenterIdx: 5);

        Assert.Equal(2, r.StepCount);
        Assert.Equal(3, r.NewCenterIdx);
        Assert.Equal(2, r.MovedCells);
        Assert.Equal(0, r.ResultAccumulator);
    }

    [Fact]
    public void ComputeScrollStep_AccumulatorCarriesLeftoverAcrossEvents()
    {
        // First small nudge below the 72 soft-threshold: no step, accumulator carries 60.
        var first = _math.ComputeScrollStep(accumulator: 0, delta: 60, currentCenterIdx: 5);
        Assert.False(first.HasStep);
        Assert.Equal(60, first.ResultAccumulator);

        // Second nudge: carried 60 + 30 = 90 (>= 72 soft-threshold) -> one step, leftover 90 - 120.
        var second = _math.ComputeScrollStep(first.ResultAccumulator, delta: 30, currentCenterIdx: 5);
        Assert.True(second.HasStep);
        Assert.Equal(1, second.StepCount);
        Assert.Equal(4, second.NewCenterIdx);
        Assert.Equal(90 - 120, second.ResultAccumulator);
    }

    [Fact]
    public void ComputeScrollStep_AtLowerBound_DoesNotMovePastZero()
    {
        var r = _math.ComputeScrollStep(accumulator: 0, delta: 120, currentCenterIdx: 0);

        Assert.True(r.HasStep);          // a step was registered
        Assert.Equal(0, r.NewCenterIdx); // but clamped
        Assert.False(r.IndexChanged);    // so no observable index change
        Assert.Equal(0, r.MovedCells);
    }

    [Fact]
    public void ComputeScrollStep_AtUpperBound_DoesNotMovePastMax()
    {
        int max = CalendarScrollMath.TotalDays - 1;
        var r = _math.ComputeScrollStep(accumulator: 0, delta: -120, currentCenterIdx: max);

        Assert.True(r.HasStep);
        Assert.Equal(max, r.NewCenterIdx);
        Assert.False(r.IndexChanged);
    }

    [Fact]
    public void ComputeScrollStep_LargeMultiStep_ClampsAndReportsMovedCells()
    {
        // 600 -> 5 steps up from index 5 would be -0... actually toward earlier days.
        var r = _math.ComputeScrollStep(accumulator: 0, delta: 600, currentCenterIdx: 5);

        Assert.Equal(5, r.StepCount);
        Assert.Equal(0, r.NewCenterIdx);   // 5 - 5 = 0, within range
        Assert.Equal(5, r.MovedCells);
        Assert.Equal(0, r.ResultAccumulator);
    }

    #endregion
}
