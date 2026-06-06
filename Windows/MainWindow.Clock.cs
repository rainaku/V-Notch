using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    #region Expanded Widget (Calendar / Clock / Word Clock)

    // The order the widget card cycles through when the user drags left/right. Drag
    // left advances to the next entry, drag right goes back; the list wraps around.
    private static readonly string[] _expandedWidgetOrder = { "calendar", "clock", "wordclock" };

    private bool IsClockWidgetMode =>
        string.Equals(_settings.ExpandedWidget, "clock", StringComparison.OrdinalIgnoreCase);

    private bool IsWordClockWidgetMode =>
        string.Equals(_settings.ExpandedWidget, "wordclock", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for any widget mode that replaces the calendar strip with a clock face
    /// (analog or word clock). Used to hide the calendar-only chrome (month label,
    /// greeting, calendar scroll handling).
    /// </summary>
    private bool IsAnyClockWidgetMode => IsClockWidgetMode || IsWordClockWidgetMode;

    /// <summary>
    /// Applies the user's chosen expanded-notch widget. The month label stays
    /// visible in every mode (just like the calendar); only the calendar day strip
    /// is swapped for the analog clock or the spelled-out word clock, both of which
    /// follow the local system time zone automatically. The greeting line is hidden
    /// in either clock mode to keep the widget card uncluttered.
    /// </summary>
    private void ApplyExpandedWidgetMode()
    {
        if (ClockWidget == null || CalendarStripContainer == null) return;

        bool useAnalogClock = IsClockWidgetMode;
        bool useWordClock = IsWordClockWidgetMode;
        bool useCalendar = !useAnalogClock && !useWordClock;

        ClockWidget.Visibility = useAnalogClock ? Visibility.Visible : Visibility.Collapsed;
        if (WordClockWidget != null)
            WordClockWidget.Visibility = useWordClock ? Visibility.Visible : Visibility.Collapsed;
        CalendarStripContainer.Visibility = useCalendar ? Visibility.Visible : Visibility.Collapsed;

        UpdateGreetingVisibilityForWidget();
    }

    /// <summary>
    /// Greeting is hidden whenever a clock widget is active; in calendar mode
    /// it follows the lyrics state (lyrics replace the greeting).
    /// </summary>
    private void UpdateGreetingVisibilityForWidget()
    {
        if (GreetingSection == null) return;

        bool show = !IsAnyClockWidgetMode && !_isLyricsActive;
        GreetingSection.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    #endregion

    #region Drag-to-switch widget

    private bool _isWidgetDragging;
    private bool _widgetDragSwitched;
    private Point _widgetDragStart;

    // How far (px) the pointer must travel horizontally before a switch fires mid-drag,
    // the smaller intent threshold used to still switch on release, and how little it may
    // move for the gesture to instead count as a plain tap (collapse).
    private const double WidgetDragSwitchThreshold = 22.0;
    private const double WidgetDragIntentThreshold = 12.0;
    private const double WidgetTapThreshold = 6.0;

    private void CalendarWidget_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Always swallow the click so it never bubbles up to the notch (which would
        // collapse it on mouse-down before a drag can start). A plain tap is handled
        // explicitly on mouse-up below.
        e.Handled = true;

        // Only block while a transition is actively running; we intentionally do NOT
        // require _isExpanded here (the widget is only hit-testable while expanded, and
        // gating on it caused presses to be swallowed without starting a drag).
        if (_isAnimating || _isSecondaryView) return;

        _isWidgetDragging = true;
        _widgetDragSwitched = false;
        _widgetDragStart = e.GetPosition(this);
        CalendarWidget.CaptureMouse();
        VNotch.Services.RuntimeLog.Log("WIDGET-DRAG", "down");
    }

    private void CalendarWidget_MouseMoveDrag(object sender, MouseEventArgs e)
    {
        if (!_isWidgetDragging || _widgetDragSwitched) return;

        double dx = e.GetPosition(this).X - _widgetDragStart.X;
        if (Math.Abs(dx) < WidgetDragSwitchThreshold) return;

        // Drag left (dx < 0) advances to the next widget; drag right goes back.
        CycleExpandedWidget(dx < 0 ? 1 : -1);
        _widgetDragSwitched = true;
    }

    private void CalendarWidget_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isWidgetDragging) return;
        _isWidgetDragging = false;
        if (CalendarWidget.IsMouseCaptured) CalendarWidget.ReleaseMouseCapture();
        e.Handled = true;

        if (_widgetDragSwitched) return;

        double dx = e.GetPosition(this).X - _widgetDragStart.X;
        double dy = e.GetPosition(this).Y - _widgetDragStart.Y;

        // A short-but-deliberate horizontal flick that didn't reach the mid-drag
        // threshold still switches on release.
        if (Math.Abs(dx) >= WidgetDragIntentThreshold && Math.Abs(dx) > Math.Abs(dy))
        {
            CycleExpandedWidget(dx < 0 ? 1 : -1);
            return;
        }

        // Otherwise, if the pointer barely moved treat it as a tap and collapse the
        // notch, matching a normal click anywhere else on it.
        if (Math.Abs(dx) < WidgetTapThreshold && Math.Abs(dy) < WidgetTapThreshold)
        {
            CollapseNotch();
        }
    }

    /// <summary>
    /// Advances the expanded widget by <paramref name="direction"/> steps through
    /// <see cref="_expandedWidgetOrder"/> (wrapping), applies it with a directional
    /// slide/fade, and persists the choice so it stays in sync with Settings.
    /// </summary>
    private void CycleExpandedWidget(int direction)
    {
        int count = _expandedWidgetOrder.Length;
        int current = Array.FindIndex(_expandedWidgetOrder,
            w => string.Equals(w, _settings.ExpandedWidget, StringComparison.OrdinalIgnoreCase));
        if (current < 0) current = 0;

        int next = (((current + direction) % count) + count) % count;
        if (next == current) return;

        _settings.ExpandedWidget = _expandedWidgetOrder[next];

        AnimateExpandedWidgetSwitch(direction);

        // Persist immediately so the Settings window (and next launch) reflect the
        // widget picked by dragging.
        _settingsService.Save(_settings);
    }

    /// <summary>
    /// Cross-fades + slides the widget card content while swapping which widget is
    /// visible, so a drag-switch feels continuous rather than a hard cut.
    /// </summary>
    private void AnimateExpandedWidgetSwitch(int direction)
    {
        var content = CalendarInnerContent;
        if (content == null)
        {
            ApplyExpandedWidgetMode();
            return;
        }

        if (content.RenderTransform is not TranslateTransform translate)
        {
            translate = new TranslateTransform();
            content.RenderTransform = translate;
        }

        double exitX = direction > 0 ? -16 : 16;
        double enterX = direction > 0 ? 16 : -16;

        var durOut = new Duration(TimeSpan.FromMilliseconds(130));
        var durIn = new Duration(TimeSpan.FromMilliseconds(300));
        int fps = VNotch.Services.AnimationConfig.TargetFps;

        var fadeOut = new DoubleAnimation(1, 0, durOut) { EasingFunction = _easeQuadIn };
        var slideOut = new DoubleAnimation(0, exitX, durOut) { EasingFunction = _easeQuadIn };
        Timeline.SetDesiredFrameRate(fadeOut, fps);
        Timeline.SetDesiredFrameRate(slideOut, fps);

        fadeOut.Completed += (_, _) =>
        {
            ApplyExpandedWidgetMode();

            translate.BeginAnimation(TranslateTransform.XProperty, null);
            translate.X = enterX;

            var fadeIn = new DoubleAnimation(0, 1, durIn) { EasingFunction = _easeExpOut6 };
            var slideIn = new DoubleAnimation(enterX, 0, durIn) { EasingFunction = _easeExpOut7 };
            Timeline.SetDesiredFrameRate(fadeIn, fps);
            Timeline.SetDesiredFrameRate(slideIn, fps);

            fadeIn.Completed += (_, _) =>
            {
                content.BeginAnimation(OpacityProperty, null);
                content.Opacity = 1;
                translate.BeginAnimation(TranslateTransform.XProperty, null);
                translate.X = 0;
            };

            content.BeginAnimation(OpacityProperty, fadeIn);
            translate.BeginAnimation(TranslateTransform.XProperty, slideIn);
        };

        content.BeginAnimation(OpacityProperty, fadeOut);
        translate.BeginAnimation(TranslateTransform.XProperty, slideOut);
    }

    #endregion
}
