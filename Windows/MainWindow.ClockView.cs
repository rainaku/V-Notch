using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;
using static VNotch.Services.Win32Interop;

namespace VNotch;

public partial class MainWindow
{
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
    /// Lazily builds the static scaffolding for the month calendar: the weekday
    /// header (S M T W T F S) and a fixed 6×7 grid of day cells. The actual day
    /// numbers / today highlight are filled in by <see cref="UpdateClockViewCalendar"/>.
    /// </summary>
    private void BuildClockViewCalendar()
    {
        if (_clockViewCalendarBuilt) return;
        if (ClockViewWeekHeader == null || ClockViewDayGrid == null) return;

        var font = (FontFamily)FindResource("MainSystemFont");

        ClockViewWeekHeader.Children.Clear();
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
            ClockViewWeekHeader.Children.Add(head);
        }
        ApplyClockViewWeekHeaderText();

        ClockViewDayGrid.Children.Clear();
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
                Background = _brushTransparent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = dayText
            };

            _clockViewDayTexts[i] = dayText;
            _clockViewDayCircles[i] = circle;

            var cell = new Grid();
            cell.Children.Add(circle);
            ClockViewDayGrid.Children.Add(cell);
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

    /// <summary>
    /// Fills the month grid for <paramref name="now"/>: month label, day numbers and
    /// the red "today" highlight. Always renders 6 rows so the layout stays stable.
    /// </summary>
    private void UpdateClockViewCalendar(DateTime now)
    {
        BuildClockViewCalendar();
        if (!_clockViewCalendarBuilt) return;

        // The month grid only changes at a day rollover, but this is reached from the
        // 30-second calendar tick too — skip the 42-cell rebuild when nothing changed.
        if (_clockViewRenderedDate.Date == now.Date) return;
        _clockViewRenderedDate = now;

        if (ClockViewMonthText != null)
        {
            ClockViewMonthText.Text = now.ToString("MMMM", CultureInfo.InvariantCulture).ToUpperInvariant();
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
                    txt.Foreground = _brushWhite;
                    txt.FontWeight = FontWeights.Bold;
                }
                else
                {
                    circle.Background = _brushTransparent;
                    txt.Foreground = _clockViewWeekday;
                    txt.FontWeight = FontWeights.Bold;
                }
            }
            else
            {
                txt.Text = string.Empty;
                circle.Background = _brushTransparent;
            }
        }
    }

    /// <summary>
    /// Refreshes the clock-view month grid. The analog clock face self-updates via
    /// its own render timer, so only the calendar needs an explicit poke.
    /// </summary>
    private void RefreshClockView() => UpdateClockViewCalendar(DateTime.Now);

    /// <summary>
    /// Re-applies everything language-dependent in the clock view: the weekday header
    /// labels AND the day-grid layout (the week-start — and thus the column offset —
    /// differs between English (Sunday-first) and Vietnamese (Monday-first)).
    /// </summary>
    private void RefreshClockViewLocale()
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
        if (ClockViewMonthText == null || _clockViewWeekHeaders[0] == null) return;

        var header = _clockViewWeekHeaders[0];
        // Calendar area is a fixed 384px wide (600 − 40 margins − 150 clock − 26 gap),
        // split into 7 equal columns by the header/day UniformGrids.
        const double calendarWidth = 384.0;
        double columnCenter = (calendarWidth / 7.0) / 2.0;

        double headerWidth = MeasureTextWidth(header.Text, header.FontFamily, header.FontSize, header.FontWeight);
        double leftEdge = Math.Max(0, columnCenter - (headerWidth / 2.0));

        ClockViewMonthText.Margin = new Thickness(leftEdge, 0, 0, 3);
    }

    private double MeasureTextWidth(string text, FontFamily family, double fontSize, FontWeight weight)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var typeface = new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal);
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, fontSize, Brushes.White, ppd);
        return ft.Width;
    }

    /// <summary>
    /// Pins the clock-view content to its final size so it is laid out exactly once,
    /// letting the notch-border resize animation clip-reveal it without per-frame
    /// re-layout (which is what caused the enter/exit stutter).
    /// </summary>
    private void PrepareClockViewContentSize()
    {
        if (TimerContent == null) return;
        TimerContent.Width = ClockViewContentWidth;
        TimerContent.Height = ClockViewContentHeight;
    }

    #endregion

    #region Clock View Sizing

    /// <summary>Grows the host window's height so the taller clock-view notch fits.</summary>
    private void ApplyClockViewWindowSize() => ResizeHostWindowHeight(_clockViewHeight);

    /// <summary>Restores the host window's height to the standard expanded-notch footprint.</summary>
    private void RestoreExpandedWindowSize() => ResizeHostWindowHeight(_expandedHeight);

    /// <summary>
    /// Adjusts ONLY the host window's height. The width and X position are fixed once in
    /// <c>PositionAtTop</c> (sized for the widest surface), so the notch never has to move
    /// horizontally — that horizontal move is exactly what made the notch snap sideways.
    /// The window is top-anchored (y == 0), so growing/shrinking the height extends the
    /// bottom edge only and the top-aligned notch stays perfectly still.
    /// </summary>
    private void ResizeHostWindowHeight(double notchHeightDip)
    {
        double dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (dpiScale <= 0) dpiScale = 1.0;

        double windowHeightDip = notchHeightDip + 80;
        this.Height = windowHeightDip;
        _windowHeight = (int)Math.Round(windowHeightDip * dpiScale);

        if (_hwnd != IntPtr.Zero)
            SetWindowPos(_hwnd, HWND_TOPMOST, _fixedX, _fixedY, _windowWidth, _windowHeight, SWP_NOACTIVATE);
    }

    /// <summary>
    /// Animates the notch border between two sizes (width + height together) for the
    /// clock-view enter/exit transitions. Used so the clock + calendar can stretch the
    /// notch wider while keeping every component aligned.
    /// </summary>
    private void AnimateClockViewNotchResize(double fromWidth, double fromHeight,
        double toWidth, double toHeight, Duration duration, TimeSpan delay, Action? onCompleted = null)
    {
        int fps = VNotch.Services.AnimationConfig.TargetFps;

        NotchBorder.BeginAnimation(WidthProperty, null);
        NotchBorder.BeginAnimation(HeightProperty, null);
        NotchBorder.Width = fromWidth;
        NotchBorder.Height = fromHeight;

        var widthAnim = MakeAnim(fromWidth, toWidth, duration, _easeAppleOut, delay);
        var heightAnim = MakeAnim(fromHeight, toHeight, duration, _easeAppleOut, delay);
        Timeline.SetDesiredFrameRate(widthAnim, fps);
        Timeline.SetDesiredFrameRate(heightAnim, fps);

        widthAnim.Completed += (_, _) =>
        {
            NotchBorder.BeginAnimation(WidthProperty, null);
            NotchBorder.Width = toWidth;
        };
        heightAnim.Completed += (_, _) =>
        {
            NotchBorder.BeginAnimation(HeightProperty, null);
            NotchBorder.Height = toHeight;
            onCompleted?.Invoke();
        };

        NotchBorder.BeginAnimation(WidthProperty, widthAnim, HandoffBehavior.SnapshotAndReplace);
        NotchBorder.BeginAnimation(HeightProperty, heightAnim, HandoffBehavior.SnapshotAndReplace);
    }

    #endregion
}
