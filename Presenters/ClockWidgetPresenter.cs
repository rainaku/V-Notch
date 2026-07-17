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
    NotchSettings Settings { get; }
    void SaveSettings();

    bool IsAnimating { get; }
    bool IsSecondaryView { get; }
    bool IsExpanded { get; }
    bool IsTimerView { get; }
    bool IsLyricsActive { get; }

    Brush TransparentBrush { get; }
    Brush WhiteBrush { get; }

    void SwitchToTimerView();
    void CollapseNotch();

    IntPtr Hwnd { get; }
    int FixedX { get; }
    int FixedY { get; }
    int WindowWidth { get; }
    int WindowHeight { get; set; }
    double ExpandedHeight { get; }

    Window Window { get; }
}

public sealed class ClockWidgetViewRefs
{
    public required UIElement ClockWidget { get; init; }
    public UIElement? WordClockWidget { get; init; }
    public UIElement? WeatherWidgetContent { get; init; }
    public UIElement? SystemMonitorWidgetContent { get; init; }
    public required UIElement CalendarStripContainer { get; init; }
    public UIElement? MonthText { get; init; }
    public UIElement? GreetingSection { get; init; }
    public required UIElement CalendarWidget { get; init; }
    public UIElement? CalendarInnerContent { get; init; }

    public Panel? ClockViewWeekHeader { get; init; }
    public Panel? ClockViewDayGrid { get; init; }
    public TextBlock? ClockViewMonthText { get; init; }
    public FrameworkElement? TimerContent { get; init; }
    public required FrameworkElement NotchBorder { get; init; }

    public required Window Window { get; init; }
}

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

    private static readonly string[] _expandedWidgetOrder = { "calendar", "clock", "wordclock", "weather", "sysmon" };

    private bool IsClockWidgetMode =>
        string.Equals(_host.Settings.ExpandedWidget, "clock", StringComparison.OrdinalIgnoreCase);

    private bool IsWordClockWidgetMode =>
        string.Equals(_host.Settings.ExpandedWidget, "wordclock", StringComparison.OrdinalIgnoreCase);

    private bool IsWeatherWidgetMode =>
        string.Equals(_host.Settings.ExpandedWidget, "weather", StringComparison.OrdinalIgnoreCase);

    private bool IsSystemMonitorWidgetMode =>
        string.Equals(_host.Settings.ExpandedWidget, "sysmon", StringComparison.OrdinalIgnoreCase);

    private bool IsAnyClockWidgetMode => IsClockWidgetMode || IsWordClockWidgetMode;

    private bool IsNonCalendarWidgetMode => IsAnyClockWidgetMode || IsWeatherWidgetMode || IsSystemMonitorWidgetMode;

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

        if (_refs.MonthText != null)
            _refs.MonthText.Visibility = useCalendar ? Visibility.Visible : Visibility.Collapsed;

        UpdateGreetingVisibilityForWidget();
    }

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

    private const double WidgetDragSwitchThreshold = 22.0;
    private const double WidgetDragIntentThreshold = 12.0;
    private const double WidgetTapThreshold = 6.0;

    public void OnCalendarWidgetMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;

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

        if (Math.Abs(dx) >= WidgetDragIntentThreshold && Math.Abs(dx) > Math.Abs(dy))
        {
            CycleExpandedWidget(dx < 0 ? 1 : -1);
            return;
        }

        if (Math.Abs(dx) < WidgetTapThreshold && Math.Abs(dy) < WidgetTapThreshold)
        {
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

        _host.SaveSettings();
    }

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

    private const double _clockViewWidth = 600;
    private const double _clockViewHeight = 310;

    private double ClockViewContentWidth => _clockViewWidth - 40.0;
    private double ClockViewContentHeight => _clockViewHeight - 48.0;

    private bool _clockViewCalendarBuilt;
    private DateTime _clockViewRenderedDate = DateTime.MinValue;
    private readonly TextBlock[] _clockViewDayTexts = new TextBlock[42];
    private readonly Border[] _clockViewDayCircles = new Border[42];
    private readonly TextBlock[] _clockViewWeekHeaders = new TextBlock[7];

    private static readonly string[] _weekHeadersEn = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
    private static readonly string[] _weekHeadersVi = { "T2", "T3", "T4", "T5", "T6", "T7", "CN" };

    private static bool WeekStartsOnMonday => Loc.CurrentLanguage == "vi";

    private static readonly SolidColorBrush _clockViewAccent = CreateFrozenBrush(255, 69, 58);
    private static readonly SolidColorBrush _clockViewWeekday = CreateFrozenBrush(235, 235, 240);

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b, byte a = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

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

    private static void ApplySmoothNumberText(TextBlock tb)
    {
        TextOptions.SetTextFormattingMode(tb, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(tb, TextRenderingMode.Grayscale);
        TextOptions.SetTextHintingMode(tb, TextHintingMode.Animated);
        tb.UseLayoutRounding = true;
        tb.SnapsToDevicePixels = true;
    }

    public bool IsCalendarBuilt => _clockViewCalendarBuilt;

    public void UpdateClockViewCalendar(DateTime now)
    {
        BuildClockViewCalendar();
        if (!_clockViewCalendarBuilt) return;

        if (_clockViewRenderedDate.Date == now.Date) return;
        _clockViewRenderedDate = now;

        if (_refs.ClockViewMonthText != null)
        {
            _refs.ClockViewMonthText.Text = now.ToString("MMMM", CultureInfo.InvariantCulture).ToUpperInvariant();
        }

        var firstOfMonth = new DateTime(now.Year, now.Month, 1);
        int dow = (int)firstOfMonth.DayOfWeek;
        int offset = WeekStartsOnMonday ? (dow + 6) % 7
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

    public void RefreshClockView() => UpdateClockViewCalendar(DateTime.Now);

    public void RefreshClockViewLocale()
    {
        if (!_clockViewCalendarBuilt) return;
        ApplyClockViewWeekHeaderText();
        _clockViewRenderedDate = DateTime.MinValue;
        UpdateClockViewCalendar(DateTime.Now);
    }

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

    private void AlignMonthLabelToFirstColumn()
    {
        if (_refs.ClockViewMonthText == null || _clockViewWeekHeaders[0] == null) return;

        var header = _clockViewWeekHeaders[0];
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

    public void PrepareClockViewContentSize()
    {
        if (_refs.TimerContent == null) return;
        _refs.TimerContent.Width = ClockViewContentWidth;
        _refs.TimerContent.Height = ClockViewContentHeight;
    }

    #endregion

    #region Clock View Sizing

    public void ApplyClockViewWindowSize() => ResizeHostWindowHeight(_clockViewHeight);

    public void RestoreExpandedWindowSize() => ResizeHostWindowHeight(_host.ExpandedHeight);

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
        if (_refs.CalendarWidget.IsMouseCaptured) _refs.CalendarWidget.ReleaseMouseCapture();
    }
}
