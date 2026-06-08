using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VNotch.Models;
using VNotch.Presenters;

namespace VNotch;

// Task 5 (mainwindow-mvvm-refactor): the clock widget + clock view logic now lives in
// VNotch.Presenters.ClockWidgetPresenter. This partial keeps the shell-side wiring: the
// presenter instance, the lifecycle hooks, the IClockWidgetHost adapter, and thin shims
// for the members that other partials (MainWindow.Timer.cs) and the deferred
// MainWindow_Loaded warm-up still call. No observable behavior changes.
public partial class MainWindow : IClockWidgetHost
{
    #region Clock View geometry constants (read by MainWindow.Timer.cs)

    // These two consts are referenced directly by MainWindow.Timer.cs (owned by another
    // task), so they remain on the shell. They mirror the presenter's own copies.
    private const double _clockViewWidth = 600;
    private const double _clockViewHeight = 310;

    #endregion

    #region ClockWidgetPresenter wiring

    private ClockWidgetPresenter? _clockWidgetPresenter;

    /// <summary>
    /// Constructs the <see cref="ClockWidgetPresenter"/> with the XAML element refs it owns
    /// and this window as its host adapter.
    /// JOIN: call in ctor (after InitializeComponent, alongside the other presenter wiring).
    /// </summary>
    internal void InitializeClockWidgetPresenter()
    {
        if (_clockWidgetPresenter != null) return;

        var refs = new ClockWidgetViewRefs
        {
            ClockWidget = ClockWidget,
            WordClockWidget = WordClockWidget,
            WeatherWidgetContent = WeatherWidgetContent,
            SystemMonitorWidgetContent = SystemMonitorWidgetContent,
            CalendarStripContainer = CalendarStripContainer,
            MonthText = MonthText,
            GreetingSection = GreetingSection,
            CalendarWidget = CalendarWidget,
            CalendarInnerContent = CalendarInnerContent,
            ClockViewWeekHeader = ClockViewWeekHeader,
            ClockViewDayGrid = ClockViewDayGrid,
            ClockViewMonthText = ClockViewMonthText,
            TimerContent = TimerContent,
            NotchBorder = NotchBorder,
            Window = this,
        };

        _clockWidgetPresenter = new ClockWidgetPresenter(this, refs);
    }

    /// <summary>
    /// Disposes the clock widget presenter.
    /// JOIN: call in PerformCleanup (idempotent — safe to call multiple times).
    /// </summary>
    internal void DisposeClockWidgetPresenter()
    {
        _clockWidgetPresenter?.Dispose();
        _clockWidgetPresenter = null;
    }

    #endregion

    #region Shell shims → ClockWidgetPresenter

    // MainWindow_Loaded defers a BuildClockViewCalendar() warm-up on ApplicationIdle. That
    // call site cannot be edited, so this internal shim keeps the exact same warm-up timing
    // by forwarding to the presenter. Initialize the presenter lazily here too, so the
    // warm-up still primes the calendar even if it fires before ctor wiring lands (JOIN).
    internal void BuildClockViewCalendar()
    {
        if (_clockWidgetPresenter == null) InitializeClockWidgetPresenter();
        _clockWidgetPresenter?.BuildClockViewCalendar();
    }

    private void RefreshClockView() => _clockWidgetPresenter?.RefreshClockView();

    private void RefreshClockViewLocale() => _clockWidgetPresenter?.RefreshClockViewLocale();

    // Read by MainWindow.Calendar.cs's calendar tick to gate the month-grid refresh.
    private bool _clockViewCalendarBuilt => _clockWidgetPresenter?.IsCalendarBuilt ?? false;

    // Called from MainWindow.Calendar.cs's OnCalendarTick to refresh the month grid.
    private void UpdateClockViewCalendar(DateTime now) => _clockWidgetPresenter?.UpdateClockViewCalendar(now);

    private void PrepareClockViewContentSize() => _clockWidgetPresenter?.PrepareClockViewContentSize();

    private void ApplyClockViewWindowSize() => _clockWidgetPresenter?.ApplyClockViewWindowSize();

    private void RestoreExpandedWindowSize() => _clockWidgetPresenter?.RestoreExpandedWindowSize();

    // Generic host-window height resize; also called from MainWindow.AudioView.cs, so it
    // stays reachable on the shell as a thin shim onto the presenter that owns the logic.
    private void ResizeHostWindowHeight(double notchHeightDip)
        => _clockWidgetPresenter?.ResizeHostWindowHeight(notchHeightDip);

    private void AnimateClockViewNotchResize(double fromWidth, double fromHeight,
        double toWidth, double toHeight, Duration duration, TimeSpan delay, Action? onCompleted = null)
        => _clockWidgetPresenter?.AnimateClockViewNotchResize(
            fromWidth, fromHeight, toWidth, toHeight, duration, delay, onCompleted);

    #endregion

    #region IClockWidgetHost (adapter onto the shared shell state)

    NotchSettings IClockWidgetHost.Settings => _settings;
    void IClockWidgetHost.SaveSettings() => _settingsService.Save(_settings);

    bool IClockWidgetHost.IsAnimating => _isAnimating;
    bool IClockWidgetHost.IsSecondaryView => _isSecondaryView;
    bool IClockWidgetHost.IsExpanded => _isExpanded;
    bool IClockWidgetHost.IsTimerView => _isTimerView;
    bool IClockWidgetHost.IsLyricsActive => _isLyricsActive;

    Brush IClockWidgetHost.TransparentBrush => _brushTransparent;
    Brush IClockWidgetHost.WhiteBrush => _brushWhite;

    void IClockWidgetHost.SwitchToTimerView() => SwitchToTimerView();
    void IClockWidgetHost.CollapseNotch() => CollapseNotch();

    IntPtr IClockWidgetHost.Hwnd => _hwnd;
    int IClockWidgetHost.FixedX => _fixedX;
    int IClockWidgetHost.FixedY => _fixedY;
    int IClockWidgetHost.WindowWidth => _windowWidth;
    int IClockWidgetHost.WindowHeight { get => _windowHeight; set => _windowHeight = value; }
    double IClockWidgetHost.ExpandedHeight => _expandedHeight;

    Window IClockWidgetHost.Window => this;

    #endregion
}
