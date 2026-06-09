using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VNotch.Models;
using VNotch.Presenters;

namespace VNotch;

public partial class MainWindow : IClockWidgetHost
{
    #region Clock View geometry constants (read by MainWindow.Timer.cs)

    private const double _clockViewWidth = 600;
    private const double _clockViewHeight = 310;

    #endregion

    #region ClockWidgetPresenter wiring

    private ClockWidgetPresenter? _clockWidgetPresenter;

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

    internal void DisposeClockWidgetPresenter()
    {
        _clockWidgetPresenter?.Dispose();
        _clockWidgetPresenter = null;
    }

    #endregion

    #region Shell shims → ClockWidgetPresenter

    internal void BuildClockViewCalendar()
    {
        if (_clockWidgetPresenter == null) InitializeClockWidgetPresenter();
        _clockWidgetPresenter?.BuildClockViewCalendar();
    }

    private void RefreshClockView() => _clockWidgetPresenter?.RefreshClockView();

    private void RefreshClockViewLocale() => _clockWidgetPresenter?.RefreshClockViewLocale();

    private bool _clockViewCalendarBuilt => _clockWidgetPresenter?.IsCalendarBuilt ?? false;

    private void UpdateClockViewCalendar(DateTime now) => _clockWidgetPresenter?.UpdateClockViewCalendar(now);

    private void PrepareClockViewContentSize() => _clockWidgetPresenter?.PrepareClockViewContentSize();

    private void ApplyClockViewWindowSize() => _clockWidgetPresenter?.ApplyClockViewWindowSize();

    private void RestoreExpandedWindowSize() => _clockWidgetPresenter?.RestoreExpandedWindowSize();

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
