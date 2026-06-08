using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Models;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;
using static VNotch.Services.Win32Interop;

namespace VNotch.Presenters;

public interface IClockWidgetHost
{
    // Persisted settings (the single source of truth for which widget is active).
    NotchSettings Settings { get; }
    void SaveSettings();

    // Shared notch view-state flags (owned elsewhere; read-only here).
    bool IsAnimating { get; }
    bool IsSecondaryView { get; }
    bool IsExpanded { get; }
    bool IsTimerView { get; }
    bool IsLyricsActive { get; }

    // Frozen brushes shared by the shell (created once with CreateFrozenBrush).
    Brush TransparentBrush { get; }
    Brush WhiteBrush { get; }

    // Notch view transitions owned by the shell / view router.
    void SwitchToTimerView();
    void CollapseNotch();

    // Win32 host-window geometry needed to grow/shrink the notch for the clock view.
    IntPtr Hwnd { get; }
    int FixedX { get; }
    int FixedY { get; }
    int WindowWidth { get; }
    int WindowHeight { get; set; }
    double ExpandedHeight { get; }

    // The hosting window itself, used for hit-testing (GetPosition), DPI, FindResource
    // and DIP height assignment — exactly the operations that legitimately need the view.
    Window Window { get; }
}

/// <summary>
/// Typed view-contract: the XAML named elements the clock widget + clock view own. Built
/// once by the code-behind partial and handed to the presenter at construction, so element
/// ownership is explicit (Requirement 3.2 / design "Presenter pattern").
/// </summary>
public sealed class ClockWidgetViewRefs
{
    // Expanded widget card (calendar / clock / word clock / weather / sysmon).
    public required UIElement ClockWidget { get; init; }
    public UIElement? WordClockWidget { get; init; }
    public UIElement? WeatherWidgetContent { get; init; }
    public UIElement? SystemMonitorWidgetContent { get; init; }
    public required UIElement CalendarStripContainer { get; init; }
    public UIElement? MonthText { get; init; }
    public UIElement? GreetingSection { get; init; }
    public required UIElement CalendarWidget { get; init; }
    public UIElement? CalendarInnerContent { get; init; }

    // Clock view (analog clock + month calendar) surface.
    public Panel? ClockViewWeekHeader { get; init; }
    public Panel? ClockViewDayGrid { get; init; }
    public TextBlock? ClockViewMonthText { get; init; }
    public FrameworkElement? TimerContent { get; init; }
    public required FrameworkElement NotchBorder { get; init; }

    // The hosting window, used for hit-testing (GetPosition), DPI, FindResource and DIP
    // height assignment — the operations that legitimately require the live view.
    public required Window Window { get; init; }
}

/// <summary>
/// Owns the expanded-notch clock widget (analog / word clock), the month-grid clock view
/// build, the clock-view host-window sizing, and the drag-to-switch gesture state.
/// Relocated verbatim from <c>MainWindow.Clock.cs</c> / <c>MainWindow.ClockView.cs</c> as a
/// behavior-preserving extraction — no observable behavior, timing or UI output changes.
/// </summary>
public sealed class ClockWidgetPresenter : IDisposable
{
    private readonly IClockWidgetHost _host;
    private readonly ClockWidgetViewRefs _refs;
    private bool _disposed;

    public ClockWidgetPresenter(IClockWidgetHost host, ClockWidgetViewRefs refs)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _refs = refs ?? throw new ArgumentNullException(nameof(refs));
    }

    #region Expanded Widget (Calendar / Clock / Word Clock)

    // The order the widget card cycles through when the user drags left/right. Drag
    // left advances to the next entry, drag right goes back; the list wraps around.
    private static readonly string[] _expandedWidgetOrder = { "calendar", "clock", "wordclock", "weather", "sysmon" };

    private bool IsClockWidgetMode =>
        string.Equals(_host.Settings.ExpandedWidget, "clock", StringComparison.OrdinalIgnoreCase);

    private bool IsWordClockWidgetMode =>
        string.Equals(_host.Settings.ExpandedWidget, "wordclock", StringComparison.OrdinalIgnoreCase);

    private bool IsWeatherWidgetMode =>
        string.Equals(_host.Settings.ExpandedWidget, "weather", StringComparison.OrdinalIgnoreCase);

    private bool IsSystemMonitorWidgetMode =>
        string.Equals(_host.Settings.ExpandedWidget, "sysmon", StringComparison.OrdinalIgnoreCase);

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
    /// Applies the user's chosen expanded-notch widget. The month label stays
    /// visible in the calendar / clock modes; only the calendar day strip is swapped
    /// for the analog clock, the spelled-out word clock, the weather widget, or the
    /// system monitor, all of which follow the local system automatically. The greeting
    /// line is hidden in any non-calendar mode to keep the widget card uncluttered.
    /// </summary>
    public void ApplyExpandedWidgetMode()
    {
        if (_refs.ClockWidget == null || _refs.CalendarStripContainer == null) return;

        bool useAnalogClock = IsClockWidgetMode;
        bool useWordClock = IsWordClockWidgetMode;
        bool useWeather = IsWeatherWidgetMode;
        bool useSystemMonitor = IsSystemMonitorWidgetMode;
        bool useCalendar = !useAnalogClock && !useWordClock && !useWeather && !useSystemMonitor;

        _refs.ClockWidget.Visibility = useAnalogClock ? Visibility.Visible : Visibility.Collapsed;
        if (_refs.WordClockWidget != null)
            _refs.WordClockWidget.Visibility = useWordClock ? Visibility.Visible : Visibility.Collapsed;
        if (_refs.WeatherWidgetContent != null)
            _refs.WeatherWidgetContent.Visibility = useWeather ? Visibility.Visible : Visibility.Collapsed;
        if (_refs.SystemMonitorWidgetContent != null)
            _refs.SystemMonitorWidgetContent.Visibility = useSystemMonitor ? Visibility.Visible : Visibility.Collapsed;
        _refs.CalendarStripContainer.Visibility = useCalendar ? Visibility.Visible : Visibility.Collapsed;

        // The weather and system-monitor widgets carry their own labels and span the full
        // card, so the month label is hidden in those modes only.
        if (_refs.MonthText != null)
            _refs.MonthText.Visibility = (useWeather || useSystemMonitor) ? Visibility.Collapsed : Visibility.Visible;

        UpdateGreetingVisibilityForWidget();
    }

    /// <summary>
    /// Greeting is hidden whenever a non-calendar widget is active; in calendar mode
    /// it follows the lyrics state (lyrics replace the greeting).
    /// </summary>
    private void UpdateGreetingVisibilityForWidget()
    {
        if (_refs.GreetingSection == null) return;

        bool show = !IsNonCalendarWidgetMode && !_host.IsLyricsActive;
        _refs.GreetingSection.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
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

    public void OnCalendarWidgetMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        // Always swallow the click so it never bubbles up to the notch (which would
        // collapse it on mouse-down before a drag can start). A plain tap is handled
        // explicitly on mouse-up below.
        e.Handled = true;

        // Only block while a transition is actively running; we intentionally do NOT
        // require _isExpanded here (the widget is only hit-testable while expanded, and
        // gating on it caused presses to be swallowed without starting a drag).
        if (_host.IsAnimating || _host.IsSecondaryView) return;

        _isWidgetDragging = true;
        _widgetDragSwitched = false;
        _widgetDragStart = e.GetPosition(_refs.Window);
        _refs.CalendarWidget.CaptureMouse();
        VNotch.Services.RuntimeLog.Log("WIDGET-DRAG", "down");
    }

    public void OnCalendarWidgetMouseMoveDrag(MouseEventArgs e)
    {
        if (!_isWidgetDragging || _widgetDragSwitched) return;

        double dx = e.GetPosition(_refs.Window).X - _widgetDragStart.X;
        if (Math.Abs(dx) < WidgetDragSwitchThreshold) return;

        // Drag left (dx < 0) advances to the next widget; drag right goes back.
        CycleExpandedWidget(dx < 0 ? 1 : -1);
        _widgetDragSwitched = true;
    }

    public void OnCalendarWidgetMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_isWidgetDragging) return;
        _isWidgetDragging = false;
        if (_refs.CalendarWidget.IsMouseCaptured) _refs.CalendarWidget.ReleaseMouseCapture();
        e.Handled = true;

        if (_widgetDragSwitched) return;

        double dx = e.GetPosition(_refs.Window).X - _widgetDragStart.X;
        double dy = e.GetPosition(_refs.Window).Y - _widgetDragStart.Y;

        // A short-but-deliberate horizontal flick that didn't reach the mid-drag
        // threshold still switches on release.
        if (Math.Abs(dx) >= WidgetDragIntentThreshold && Math.Abs(dx) > Math.Abs(dy))
        {
            CycleExpandedWidget(dx < 0 ? 1 : -1);
            return;
        }

        // Otherwise, if the pointer barely moved treat it as a tap.
        if (Math.Abs(dx) < WidgetTapThreshold && Math.Abs(dy) < WidgetTapThreshold)
        {
            // The analog clock widget opens the full clock view on tap; the other
            // widgets keep the normal click-to-collapse behavior.
            if (IsClockWidgetMode && _host.IsExpanded && !_host.IsSecondaryView && !_host.IsTimerView)
            {
                _host.SwitchToTimerView();
            }
            else
            {
                _host.CollapseNotch();
            }
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
            w => string.Equals(w, _host.Settings.ExpandedWidget, StringComparison.OrdinalIgnoreCase));
        if (current < 0) current = 0;

        int next = (((current + direction) % count) + count) % count;
        if (next == current) return;

        _host.Settings.ExpandedWidget = _expandedWidgetOrder[next];

        AnimateExpandedWidgetSwitch(direction);

        // Persist immediately so the Settings window (and next launch) reflect the
        // widget picked by dragging.
        _host.SaveSettings();
    }

    /// <summary>
    /// Cross-fades + slides the widget card content while swapping which widget is
    /// visible, so a drag-switch feels continuous rather than a hard cut.
    /// </summary>
    private void AnimateExpandedWidgetSwitch(int direction)
    {
        var content = _refs.CalendarInnerContent;
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
                content.BeginAnimation(UIElement.OpacityProperty, null);
                content.Opacity = 1;
                translate.BeginAnimation(TranslateTransform.XProperty, null);
                translate.X = 0;
            };

            content.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            translate.BeginAnimation(TranslateTransform.XProperty, slideIn);
        };

        content.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        translate.BeginAnimation(TranslateTransform.XProperty, slideOut);
    }

    #endregion

    #region Clock View (analog clock + month calendar)

    // The widened notch surface used while the clock view is visible. The clock,
    // month calendar and the Pomodoro timer are stacked inside this surface, so it
    // needs more room than the standard expanded notch.
    private const double _clockViewWidth = 600;
    private const double _clockViewHeight = 310;

    // TimerContent uses Margin="20,34,20,14"; pin the inner content to the final
    // clock-view size so it does NOT re-layout on every frame while the notch border
    // animates its width/height. The notch clip simply reveals it (Apple-style wipe),
    // which keeps the enter/exit transitions smooth instead of janky.
    private double ClockViewContentWidth => _clockViewWidth - 40.0;
    private double ClockViewContentHeight => _clockViewHeight - 48.0;

    private bool _clockViewCalendarBuilt;
    private DateTime _clockViewRenderedDate = DateTime.MinValue;
    private readonly TextBlock[] _clockViewDayTexts = new TextBlock[42];
    private readonly Border[] _clockViewDayCircles = new Border[42];
    private readonly TextBlock[] _clockViewWeekHeaders = new TextBlock[7];

    // English uses the US Sunday-first convention; Vietnamese uses the Monday-first
    // convention with Sunday (CN) at the end (T2..T7, CN). The day-grid offset below
    // follows the same week-start so headers and day numbers stay aligned.
    private static readonly string[] _weekHeadersEn = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
    private static readonly string[] _weekHeadersVi = { "T2", "T3", "T4", "T5", "T6", "T7", "CN" };

    private static bool WeekStartsOnMonday => Loc.CurrentLanguage == "vi";

    private static readonly SolidColorBrush _clockViewAccent = CreateFrozenBrush(255, 69, 58);   // iOS system red
    private static readonly SolidColorBrush _clockViewWeekday = CreateFrozenBrush(235, 235, 240);

    /// <summary>
    /// Creates a frozen (thread-safe, GC-friendly) solid-colour brush. Mirrors the shell's
    /// private <c>CreateFrozenBrush</c> so the presenter owns its own static brushes without
    /// reaching into <c>MainWindow</c>.
    /// </summary>
    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b, byte a = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Lazily builds the static scaffolding for the month calendar: the weekday
    /// header (S M T W T F S) and a fixed 6×7 grid of day cells. The actual day
    /// numbers / today highlight are filled in by <see cref="UpdateClockViewCalendar"/>.
    /// </summary>
    public void BuildClockViewCalendar()
    {
        if (_clockViewCalendarBuilt) return;
        if (_refs.ClockViewWeekHeader == null || _refs.ClockViewDayGrid == null) return;

        var font = (FontFamily)_refs.Window.FindResource("MainSystemFont");

        _refs.ClockViewWeekHeader.Children.Clear();
        for (int i = 0; i < 7; i++)
        {
            var head = new TextBlock
            {
                FontFamily = font,
                FontSize = 12.5,
                FontWeight = FontWeights.Bold,
                Foreground = _clockViewWeekday,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            ApplySmoothNumberText(head);
            _clockViewWeekHeaders[i] = head;
            _refs.ClockViewWeekHeader.Children.Add(head);
        }
        ApplyClockViewWeekHeaderText();

        _refs.ClockViewDayGrid.Children.Clear();
        for (int i = 0; i < 42; i++)
        {
            var dayText = new TextBlock
            {
                FontFamily = font,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = _clockViewWeekday,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            ApplySmoothNumberText(dayText);

            var circle = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Background = _host.TransparentBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = dayText
            };

            _clockViewDayTexts[i] = dayText;
            _clockViewDayCircles[i] = circle;

            var cell = new Grid();
            cell.Children.Add(circle);
            _refs.ClockViewDayGrid.Children.Add(cell);
        }

        _clockViewCalendarBuilt = true;
    }

    /// <summary>
    /// Applies SF Pro (the app's display font) friendly smooth-text settings: ideal
    /// glyph layout with grayscale anti-aliasing, which renders crisply and without
    /// ClearType colour fringing on the pure-black notch, including while the
    /// clock-view scales in/out.
    /// </summary>
    private static void ApplySmoothNumberText(TextBlock tb)
    {
        TextOptions.SetTextFormattingMode(tb, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(tb, TextRenderingMode.Grayscale);
        TextOptions.SetTextHintingMode(tb, TextHintingMode.Animated);
        tb.UseLayoutRounding = true;
        tb.SnapsToDevicePixels = true;
    }

    /// <summary>True once the month-grid scaffolding has been built (read by the shell's calendar tick).</summary>
    public bool IsCalendarBuilt => _clockViewCalendarBuilt;

    /// <summary>
    /// Fills the month grid for <paramref name="now"/>: month label, day numbers and
    /// the red "today" highlight. Always renders 6 rows so the layout stays stable.
    /// </summary>
    public void UpdateClockViewCalendar(DateTime now)
    {
        BuildClockViewCalendar();
        if (!_clockViewCalendarBuilt) return;

        // The month grid only changes at a day rollover, but this is reached from the
        // 30-second calendar tick too — skip the 42-cell rebuild when nothing changed.
        if (_clockViewRenderedDate.Date == now.Date) return;
        _clockViewRenderedDate = now;

        if (_refs.ClockViewMonthText != null)
        {
            _refs.ClockViewMonthText.Text = now.ToString("MMMM", CultureInfo.InvariantCulture).ToUpperInvariant();
        }

        var firstOfMonth = new DateTime(now.Year, now.Month, 1);
        int dow = (int)firstOfMonth.DayOfWeek;              // Sunday == 0 .. Saturday == 6
        int offset = WeekStartsOnMonday ? (dow + 6) % 7     // Monday == 0 .. Sunday == 6
                                        : dow;
        int daysInMonth = DateTime.DaysInMonth(now.Year, now.Month);

        for (int i = 0; i < 42; i++)
        {
            var txt = _clockViewDayTexts[i];
            var circle = _clockViewDayCircles[i];
            int dayNum = i - offset + 1;

            if (dayNum >= 1 && dayNum <= daysInMonth)
            {
                txt.Text = dayNum.ToString(CultureInfo.InvariantCulture);

                if (dayNum == now.Day)
                {
                    circle.Background = _clockViewAccent;
                    txt.Foreground = _host.WhiteBrush;
                    txt.FontWeight = FontWeights.Bold;
                }
                else
                {
                    circle.Background = _host.TransparentBrush;
                    txt.Foreground = _clockViewWeekday;
                    txt.FontWeight = FontWeights.Bold;
                }
            }
            else
            {
                txt.Text = string.Empty;
                circle.Background = _host.TransparentBrush;
            }
        }
    }

    /// <summary>
    /// Refreshes the clock-view month grid. The analog clock face self-updates via
    /// its own render timer, so only the calendar needs an explicit poke.
    /// </summary>
    public void RefreshClockView() => UpdateClockViewCalendar(DateTime.Now);

    /// <summary>
    /// Re-applies everything language-dependent in the clock view: the weekday header
    /// labels AND the day-grid layout (the week-start — and thus the column offset —
    /// differs between English (Sunday-first) and Vietnamese (Monday-first)).
    /// </summary>
    public void RefreshClockViewLocale()
    {
        if (!_clockViewCalendarBuilt) return; // built lazily with the right locale on first open
        ApplyClockViewWeekHeaderText();
        _clockViewRenderedDate = DateTime.MinValue; // force re-layout with the new first day-of-week
        UpdateClockViewCalendar(DateTime.Now);
    }

    /// <summary>
    /// Applies the weekday header labels for the current language: 3-letter names for
    /// English (Sun, Mon, …) and the T2..T7 / CN convention for Vietnamese. Safe to call
    /// whenever the language changes.
    /// </summary>
    private void ApplyClockViewWeekHeaderText()
    {
        if (_clockViewWeekHeaders[0] == null) return;

        var labels = Loc.CurrentLanguage == "vi" ? _weekHeadersVi : _weekHeadersEn;
        for (int i = 0; i < 7; i++)
        {
            if (_clockViewWeekHeaders[i] != null)
                _clockViewWeekHeaders[i].Text = labels[i];
        }

        AlignMonthLabelToFirstColumn();
    }

    /// <summary>
    /// Lines the month label's left edge up with the first weekday header (e.g. "Sun"
    /// / "T2"). The header is centered in its column, so we measure its rendered width
    /// to find its left edge and indent the month to match — works for any language /
    /// header length without magic numbers.
    /// </summary>
    private void AlignMonthLabelToFirstColumn()
    {
        if (_refs.ClockViewMonthText == null || _clockViewWeekHeaders[0] == null) return;

        var header = _clockViewWeekHeaders[0];
        // Calendar area is a fixed 384px wide (600 − 40 margins − 150 clock − 26 gap),
        // split into 7 equal columns by the header/day UniformGrids.
        const double calendarWidth = 384.0;
        double columnCenter = (calendarWidth / 7.0) / 2.0;

        double headerWidth = MeasureTextWidth(header.Text, header.FontFamily, header.FontSize, header.FontWeight);
        double leftEdge = Math.Max(0, columnCenter - (headerWidth / 2.0));

        _refs.ClockViewMonthText.Margin = new Thickness(leftEdge, 0, 0, 3);
    }

    private double MeasureTextWidth(string text, FontFamily family, double fontSize, FontWeight weight)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var typeface = new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal);
        double ppd = VisualTreeHelper.GetDpi(_refs.Window).PixelsPerDip;
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, fontSize, Brushes.White, ppd);
        return ft.Width;
    }

    /// <summary>
    /// Pins the clock-view content to its final size so it is laid out exactly once,
    /// letting the notch-border resize animation clip-reveal it without per-frame
    /// re-layout (which is what caused the enter/exit stutter).
    /// </summary>
    public void PrepareClockViewContentSize()
    {
        if (_refs.TimerContent == null) return;
        _refs.TimerContent.Width = ClockViewContentWidth;
        _refs.TimerContent.Height = ClockViewContentHeight;
    }

    #endregion

    #region Clock View Sizing

    /// <summary>Grows the host window's height so the taller clock-view notch fits.</summary>
    public void ApplyClockViewWindowSize() => ResizeHostWindowHeight(_clockViewHeight);

    /// <summary>Restores the host window's height to the standard expanded-notch footprint.</summary>
    public void RestoreExpandedWindowSize() => ResizeHostWindowHeight(_host.ExpandedHeight);

    /// <summary>
    /// Adjusts ONLY the host window's height. The width and X position are fixed once in
    /// <c>PositionAtTop</c> (sized for the widest surface), so the notch never has to move
    /// horizontally — that horizontal move is exactly what made the notch snap sideways.
    /// The window is top-anchored (y == 0), so growing/shrinking the height extends the
    /// bottom edge only and the top-aligned notch stays perfectly still.
    /// </summary>
    public void ResizeHostWindowHeight(double notchHeightDip)
    {
        double dpiScale = VisualTreeHelper.GetDpi(_refs.Window).DpiScaleX;
        if (dpiScale <= 0) dpiScale = 1.0;

        double windowHeightDip = notchHeightDip + 80;
        _refs.Window.Height = windowHeightDip;
        _host.WindowHeight = (int)Math.Round(windowHeightDip * dpiScale);

        if (_host.Hwnd != IntPtr.Zero)
            SetWindowPos(_host.Hwnd, HWND_TOPMOST, _host.FixedX, _host.FixedY, _host.WindowWidth, _host.WindowHeight, SWP_NOACTIVATE);
    }

    /// <summary>
    /// Animates the notch border between two sizes (width + height together) for the
    /// clock-view enter/exit transitions. Used so the clock + calendar can stretch the
    /// notch wider while keeping every component aligned.
    /// </summary>
    public void AnimateClockViewNotchResize(double fromWidth, double fromHeight,
        double toWidth, double toHeight, Duration duration, TimeSpan delay, Action? onCompleted = null)
    {
        int fps = VNotch.Services.AnimationConfig.TargetFps;

        var notchBorder = _refs.NotchBorder;

        notchBorder.BeginAnimation(FrameworkElement.WidthProperty, null);
        notchBorder.BeginAnimation(FrameworkElement.HeightProperty, null);
        notchBorder.Width = fromWidth;
        notchBorder.Height = fromHeight;

        var widthAnim = MakeAnim(fromWidth, toWidth, duration, _easeAppleOut, delay);
        var heightAnim = MakeAnim(fromHeight, toHeight, duration, _easeAppleOut, delay);
        Timeline.SetDesiredFrameRate(widthAnim, fps);
        Timeline.SetDesiredFrameRate(heightAnim, fps);

        widthAnim.Completed += (_, _) =>
        {
            notchBorder.BeginAnimation(FrameworkElement.WidthProperty, null);
            notchBorder.Width = toWidth;
        };
        heightAnim.Completed += (_, _) =>
        {
            notchBorder.BeginAnimation(FrameworkElement.HeightProperty, null);
            notchBorder.Height = toHeight;
            onCompleted?.Invoke();
        };

        notchBorder.BeginAnimation(FrameworkElement.WidthProperty, widthAnim, HandoffBehavior.SnapshotAndReplace);
        notchBorder.BeginAnimation(FrameworkElement.HeightProperty, heightAnim, HandoffBehavior.SnapshotAndReplace);
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // No owned disposables: the analog clock face self-updates via its own render
        // timer, and all animations are fire-and-forget on shell-owned elements. Release
        // any in-flight drag capture defensively so teardown never strands the mouse.
        if (_refs.CalendarWidget.IsMouseCaptured) _refs.CalendarWidget.ReleaseMouseCapture();
    }
}
