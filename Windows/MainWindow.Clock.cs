using System;
using System.Windows.Input;

namespace VNotch;

public partial class MainWindow
{
    // NOTE (mainwindow-mvvm-refactor / Task 5): the analog/word clock widget, the
    // month-grid clock view build, the clock-view host sizing, and the drag-to-switch
    // gesture state were relocated to VNotch.Presenters.ClockWidgetPresenter (see
    // Presenters/ClockWidgetPresenter.cs and the presenter wiring in
    // MainWindow.ClockView.cs). The members below remain on the shell only because they
    // are referenced by other partials / XAML that this task must not edit:
    //   - the widget-mode predicates are read by MainWindow.Lyrics.cs (IsClockWidgetMode),
    //     MainWindow.MusicWidget.cs and MainWindow.Calendar.cs (IsNonCalendarWidgetMode);
    //   - ApplyExpandedWidgetMode() is invoked from MainWindow.xaml.cs;
    //   - the three CalendarWidget_Mouse* handlers are bound in MainWindow.xaml.
    // They are thin shims delegating to the presenter (behavior-preserving).

    #region Expanded Widget mode predicates (read by other partials)

    // The widget-mode predicates are pure interpretations of the single source of truth
    // (_settings.ExpandedWidget); they own no state, so they stay on the shell for the
    // other partials that read them. The presenter computes the same values internally.

    private bool IsClockWidgetMode =>
        string.Equals(_settings.ExpandedWidget, "clock", StringComparison.OrdinalIgnoreCase);

    private bool IsWordClockWidgetMode =>
        string.Equals(_settings.ExpandedWidget, "wordclock", StringComparison.OrdinalIgnoreCase);

    private bool IsWeatherWidgetMode =>
        string.Equals(_settings.ExpandedWidget, "weather", StringComparison.OrdinalIgnoreCase);

    private bool IsSystemMonitorWidgetMode =>
        string.Equals(_settings.ExpandedWidget, "sysmon", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for any widget mode that replaces the calendar strip with a clock face
    /// (analog or word clock). Used to hide the calendar-only chrome (month label,
    /// greeting, calendar scroll handling).
    /// </summary>
    private bool IsAnyClockWidgetMode => IsClockWidgetMode || IsWordClockWidgetMode;

    /// <summary>
    /// True for any widget mode other than the calendar (clock, word clock, weather,
    /// system monitor). The calendar-only chrome — day strip, greeting and scroll
    /// handling — is hidden for all of these.
    /// </summary>
    private bool IsNonCalendarWidgetMode => IsAnyClockWidgetMode || IsWeatherWidgetMode || IsSystemMonitorWidgetMode;

    /// <summary>
    /// Applies the user's chosen expanded-notch widget. Delegates to
    /// <see cref="VNotch.Presenters.ClockWidgetPresenter.ApplyExpandedWidgetMode"/>.
    /// Kept here because it is invoked from MainWindow.xaml.cs.
    /// </summary>
    private void ApplyExpandedWidgetMode() => _clockWidgetPresenter?.ApplyExpandedWidgetMode();

    #endregion

    #region Drag-to-switch widget (XAML-bound handlers → presenter)

    private void CalendarWidget_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _clockWidgetPresenter?.OnCalendarWidgetMouseLeftButtonDown(e);

    private void CalendarWidget_MouseMoveDrag(object sender, MouseEventArgs e)
        => _clockWidgetPresenter?.OnCalendarWidgetMouseMoveDrag(e);

    private void CalendarWidget_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => _clockWidgetPresenter?.OnCalendarWidgetMouseLeftButtonUp(e);

    #endregion
}
