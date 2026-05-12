using System;
using VNotch.Services;

namespace VNotch.Controllers;

/// <summary>
/// Manages music widget state: expand/collapse logic, layout mode, progress timing.
/// Decoupled from UI — the MainWindow handles actual animations.
/// </summary>
public sealed class MusicWidgetController
{
    private readonly NotchStateManager _stateManager;

    private bool _isMusicAnimating = false;
    private double _musicWidgetSmallWidth = 0;

    // ─── Public State ───

    public bool IsMusicExpanded => _stateManager.IsMusicExpanded;
    public bool IsMusicAnimating => _isMusicAnimating;
    public double SmallWidth => _musicWidgetSmallWidth;

    // ─── Events ───

    /// <summary>Raised when music widget expand animation should begin.</summary>
    public event Action? ExpandRequested;

    /// <summary>Raised when music widget collapse animation should begin.</summary>
    public event Action? CollapseRequested;

    /// <summary>Raised when progress section layout needs recalculation.</summary>
    public event Action? LayoutUpdateRequested;

    public MusicWidgetController(NotchStateManager stateManager)
    {
        _stateManager = stateManager;
    }

    // ─── Expand/Collapse Flow ───

    public bool TryBeginExpand(double currentWidgetWidth)
    {
        if (_isMusicAnimating) return false;
        _isMusicAnimating = true;
        _musicWidgetSmallWidth = currentWidgetWidth;

        if (!_stateManager.TryTransitionTo(NotchState.MusicExpanding))
        {
            _isMusicAnimating = false;
            return false;
        }

        ExpandRequested?.Invoke();
        return true;
    }

    public void CompleteExpand()
    {
        _isMusicAnimating = false;
        _stateManager.TryTransitionTo(NotchState.MusicExpanded);
        LayoutUpdateRequested?.Invoke();
    }

    public bool TryBeginCollapse()
    {
        if (_isMusicAnimating) return false;
        _isMusicAnimating = true;

        if (!_stateManager.TryTransitionTo(NotchState.MusicCollapsing))
        {
            _isMusicAnimating = false;
            return false;
        }

        CollapseRequested?.Invoke();
        return true;
    }

    public void CompleteCollapse()
    {
        _isMusicAnimating = false;
        _stateManager.TryTransitionTo(NotchState.Expanded);
        LayoutUpdateRequested?.Invoke();
    }

    /// <summary>
    /// Gets the target width for collapse animation.
    /// </summary>
    public double GetCollapseTargetWidth(double expandedContentWidth)
    {
        return _musicWidgetSmallWidth > 0
            ? _musicWidgetSmallWidth
            : (expandedContentWidth / 3.0) - 8;
    }

    // ─── Progress Layout Calculations ───

    public record ProgressLayout(
        double ContainerHeight,
        double BarHeight,
        double BarRadius,
        double TimeTopMargin,
        double TimeSideMargin,
        bool UseCompactLayout);

    /// <summary>
    /// Computes the progress section layout dimensions based on current state.
    /// </summary>
    public ProgressLayout ComputeProgressLayout()
    {
        bool useCompact = !IsMusicExpanded;
        return new ProgressLayout(
            ContainerHeight: useCompact ? 8 : 12,
            BarHeight: useCompact ? 3 : 4,
            BarRadius: useCompact ? 1.5 : 2.0,
            TimeTopMargin: useCompact ? 4 : 2,
            TimeSideMargin: useCompact ? 4 : 6,
            UseCompactLayout: useCompact);
    }

    /// <summary>
    /// Computes the visible text width for media info based on available space.
    /// </summary>
    public static double ComputeVisibleTextWidth(
        double widgetWidth, double thumbnailWidth, double thumbnailGap,
        double infoSectionWidth, double fallbackWidth)
    {
        if (widgetWidth > 0)
        {
            double available = widgetWidth - thumbnailWidth - thumbnailGap - 4;
            return Math.Max(0, Math.Min(340, available));
        }

        if (infoSectionWidth > 0)
            return Math.Max(0, Math.Min(340, infoSectionWidth));

        return fallbackWidth;
    }
}
