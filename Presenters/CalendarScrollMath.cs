using System;

namespace VNotch.Presenters;

/// <summary>
/// Pure, Window-free scroll/center index math for the expanded-notch calendar strip.
/// Extracted verbatim from <c>MainWindow.Calendar.cs</c> so it can be unit-tested
/// without a live WPF <see cref="System.Windows.Window"/>. No UI types referenced.
/// </summary>
public sealed class CalendarScrollMath
{
    // Mirrors the consts that used to live in the MainWindow shell field block.
    public const int TotalDays = 11;
    public const int VisibleDays = 3;
    public const double CellWidth = 30.0;

    /// <summary>Highlight pill diameter used to centre it inside a cell.</summary>
    private const double HighlightDiameter = 24.0;

    /// <summary>Clamp a candidate centre index into the valid [0, TotalDays-1] range.</summary>
    public int ClampIndex(int idx) => Math.Max(0, Math.Min(TotalDays - 1, idx));

    /// <summary>
    /// Given the current strip translate X, derive which cell is centred.
    /// Verbatim port of <c>GetCalendarCenterIndexFromStripX</c>.
    /// </summary>
    public int GetCenterIndexFromStripX(double stripX)
    {
        int centerIdx = (int)Math.Round((30.0 - stripX) / CellWidth);
        return ClampIndex(centerIdx);
    }

    /// <summary>
    /// Highlight translate X for a given centre index.
    /// Verbatim port of <c>GetCalendarHighlightXForIndex</c>.
    /// </summary>
    public double GetHighlightXForIndex(int centerIdx)
    {
        return centerIdx * CellWidth + (CellWidth - HighlightDiameter) / 2.0;
    }

    /// <summary>
    /// Strip translate X that centres the given index.
    /// Verbatim port of the <c>(1 * CalendarCellWidth) - (idx * CalendarCellWidth)</c> formula.
    /// </summary>
    public double GetStripXForIndex(int centerIdx)
    {
        return (1 * CellWidth) - (centerIdx * CellWidth);
    }

    /// <summary>
    /// Pure reproduction of the mouse-wheel step accounting from
    /// <c>CalendarWidget_MouseWheel</c>: accumulate the delta, decide how many whole
    /// cells to advance, drain the accumulator, and compute the (clamped) new centre.
    /// Time-based debounce stays in the presenter because it is not pure.
    /// </summary>
    /// <param name="accumulator">The scroll accumulator before this delta.</param>
    /// <param name="delta">The raw wheel delta for this event.</param>
    /// <param name="currentCenterIdx">The current centre index.</param>
    public ScrollStepResult ComputeScrollStep(double accumulator, int delta, int currentCenterIdx)
    {
        double acc = accumulator + delta;
        int direction = acc > 0 ? -1 : 1;
        int stepCount = (int)(Math.Abs(acc) / 120.0);
        if (stepCount == 0 && Math.Abs(acc) >= 72)
        {
            stepCount = 1;
        }

        if (stepCount == 0)
        {
            // Original returns early WITHOUT draining the accumulator.
            return new ScrollStepResult(acc, currentCenterIdx, currentCenterIdx, 0);
        }

        double remaining = acc - Math.Sign(acc) * stepCount * 120.0;

        int oldIdx = currentCenterIdx;
        int newIdx = ClampIndex(currentCenterIdx + (direction * stepCount));

        return new ScrollStepResult(remaining, newIdx, oldIdx, stepCount);
    }
}

/// <summary>
/// Result of <see cref="CalendarScrollMath.ComputeScrollStep"/>.
/// </summary>
public readonly record struct ScrollStepResult(
    double ResultAccumulator,
    int NewCenterIdx,
    int OldCenterIdx,
    int StepCount)
{
    /// <summary>True when at least one whole cell of scroll was registered.</summary>
    public bool HasStep => StepCount > 0;

    /// <summary>True when the (clamped) centre index actually changed.</summary>
    public bool IndexChanged => NewCenterIdx != OldCenterIdx;

    /// <summary>Absolute number of cells the centre moved (for animation duration scaling).</summary>
    public int MovedCells => Math.Abs(NewCenterIdx - OldCenterIdx);
}
