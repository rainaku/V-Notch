using System;
using System.Windows;

namespace VNotch;

public partial class MainWindow
{
    #region Expanded Widget (Calendar / Clock)

    /// <summary>
    /// Applies the user's chosen expanded-notch widget by toggling between the
    /// calendar strip and the analog clock. The clock follows the local system
    /// time zone automatically.
    /// </summary>
    private void ApplyExpandedWidgetMode()
    {
        if (ClockWidget == null || CalendarInnerContent == null) return;

        bool useClock = string.Equals(_settings.ExpandedWidget, "clock", StringComparison.OrdinalIgnoreCase);

        ClockWidget.Visibility = useClock ? Visibility.Visible : Visibility.Collapsed;
        CalendarInnerContent.Visibility = useClock ? Visibility.Collapsed : Visibility.Visible;
    }

    #endregion
}
