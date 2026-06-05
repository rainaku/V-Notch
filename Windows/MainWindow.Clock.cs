using System;
using System.Windows;

namespace VNotch;

public partial class MainWindow
{
    #region Expanded Widget (Calendar / Clock)

    private bool IsClockWidgetMode =>
        string.Equals(_settings.ExpandedWidget, "clock", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Applies the user's chosen expanded-notch widget. The month label stays
    /// visible in both modes (just like the calendar); only the calendar day
    /// strip is swapped for the analog clock, which follows the local system
    /// time zone automatically. The greeting line is hidden in clock mode.
    /// </summary>
    private void ApplyExpandedWidgetMode()
    {
        if (ClockWidget == null || CalendarStripContainer == null) return;

        bool useClock = IsClockWidgetMode;

        ClockWidget.Visibility = useClock ? Visibility.Visible : Visibility.Collapsed;
        CalendarStripContainer.Visibility = useClock ? Visibility.Collapsed : Visibility.Visible;

        UpdateGreetingVisibilityForWidget();
    }

    /// <summary>
    /// Greeting is hidden whenever the clock widget is active; in calendar mode
    /// it follows the lyrics state (lyrics replace the greeting).
    /// </summary>
    private void UpdateGreetingVisibilityForWidget()
    {
        if (GreetingSection == null) return;

        bool show = !IsClockWidgetMode && !_isLyricsActive;
        GreetingSection.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    #endregion
}
