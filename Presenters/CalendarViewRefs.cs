using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using VNotch.Models;

namespace VNotch.Presenters;

/// <summary>
/// Typed view-contract handed to <see cref="CalendarPresenter"/> once at construction.
/// Carries the XAML named elements (and a few shell-owned resources) the calendar feature
/// mutates, so the presenter never reaches back into <c>MainWindow</c> fields.
/// Constructed in <c>MainWindow.Calendar.cs</c> from the live visual tree.
/// </summary>
public sealed class CalendarViewRefs
{
    // Day strip
    public required StackPanel WeekDaysPanel { get; init; }
    public required StackPanel WeekNumbers { get; init; }
    public required TranslateTransform CalendarStripTranslate { get; init; }
    public required TranslateTransform CalendarHighlightTranslate { get; init; }
    public required ScaleTransform CalendarHighlightScale { get; init; }

    // Month label
    public required TextBlock MonthText { get; init; }
    public required ScaleTransform MonthTextScale { get; init; }
    public required TranslateTransform MonthTextTranslate { get; init; }

    // Greeting / context
    public required TextBlock EventText { get; init; }
    public required UIElement BatterySection { get; init; }
    public required UIElement SettingsButton { get; init; }
    public required UIElement GreetingSection { get; init; }
    public required BlurEffect CalendarGreetingContextBlur { get; init; }

    // Widget hover transforms
    public required ScaleTransform CalendarWidgetScale { get; init; }
    public required TranslateTransform CalendarWidgetTranslate { get; init; }

    // Resolved resources (FindResource was only available on the FrameworkElement)
    public required Style SmallTextStyle { get; init; }
    public required Style TitleTextStyle { get; init; }

    // Shared frozen brushes from the shell
    public required Brush BrushBlack { get; init; }
    public required Brush BrushWhite { get; init; }
    public required Brush BrushTransparent { get; init; }

    // Live shell state read at animation time (kept as providers to preserve live reads)
    public required Func<NotchSettings> SettingsProvider { get; init; }
    public required Func<bool> IsNonCalendarWidgetMode { get; init; }

    /// <summary>
    /// Invoked at the end of each calendar tick so the shell can keep the clock-view
    /// month grid current (preserves the original <c>_clockViewCalendarBuilt</c> check).
    /// </summary>
    public required Action<DateTime> OnCalendarTick { get; init; }
}
