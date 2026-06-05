using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace VNotch.Controls;

/// <summary>
/// A compact analog watch face rendered with vector primitives.
/// Shows hour/minute/second hands, a tick ruler, hour markers and the
/// current day-of-month. Always uses the local system time zone
/// (the PC default), via <see cref="DateTime.Now"/>.
/// </summary>
public class AnalogClock : FrameworkElement
{
    // ─── Palette ───────────────────────────────────────────────
    private static readonly Brush HandBrush;          // hour + minute hands (white)
    private static readonly Brush MajorTickBrush;      // hour markers (light gray)
    private static readonly Brush MinorTickBrush;      // minute ticks (dim gray)
    private static readonly Brush AccentBrush;         // second hand + date + hub (red)
    private static readonly Brush HubHoleBrush;        // tiny hole in the center hub
    private static readonly Pen MinorTickPen;
    private static readonly Pen MajorRulerPen;
    private static readonly Typeface DateTypeface;

    private readonly DispatcherTimer _timer;
    private bool _isRunning;

    static AnalogClock()
    {
        HandBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7));
        MajorTickBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xCC));
        MinorTickBrush = new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x73));
        AccentBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x23, 0x1F));
        HubHoleBrush = new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0A));

        MinorTickPen = new Pen(MinorTickBrush, 1.0) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        MajorRulerPen = new Pen(MajorTickBrush, 1.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        DateTypeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        HandBrush.Freeze();
        MajorTickBrush.Freeze();
        MinorTickBrush.Freeze();
        AccentBrush.Freeze();
        HubHoleBrush.Freeze();
        MinorTickPen.Freeze();
        MajorRulerPen.Freeze();
    }

    public AnalogClock()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            // ~30 fps for a smooth sweeping second hand
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _timer.Tick += (_, _) => InvalidateVisual();

        Loaded += (_, _) => UpdateRunningState();
        Unloaded += (_, _) => StopTimer();
        IsVisibleChanged += (_, _) => UpdateRunningState();
    }

    private void UpdateRunningState()
    {
        if (IsVisible)
            StartTimer();
        else
            StopTimer();
    }

    private void StartTimer()
    {
        if (_isRunning) return;
        _isRunning = true;
        _timer.Start();
        InvalidateVisual();
    }

    private void StopTimer()
    {
        if (!_isRunning) return;
        _isRunning = false;
        _timer.Stop();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double diameter = Math.Min(w, h);
        double r = diameter / 2.0;
        var center = new Point(w / 2.0, h / 2.0);

        DateTime now = DateTime.Now; // local system time zone (PC default)

        DrawTicks(dc, center, r);
        DrawHourMarkers(dc, center, r);
        DrawDate(dc, center, r, now.Day);
        DrawHands(dc, center, r, now);
        DrawCenterHub(dc, center, r);
    }

    private static void DrawTicks(DrawingContext dc, Point center, double r)
    {
        // 60 minute ticks around the rim; every 5th is a touch longer/brighter.
        double outer = r * 0.97;
        for (int i = 0; i < 60; i++)
        {
            // The hour positions get bold markers instead, so keep the ruler subtle there.
            bool isFive = i % 5 == 0;
            double angle = i * 6.0 * Math.PI / 180.0;
            double sin = Math.Sin(angle);
            double cos = Math.Cos(angle);

            double innerLen = isFive ? r * 0.86 : r * 0.91;
            var p1 = new Point(center.X + sin * innerLen, center.Y - cos * innerLen);
            var p2 = new Point(center.X + sin * outer, center.Y - cos * outer);
            dc.DrawLine(isFive ? MajorRulerPen : MinorTickPen, p1, p2);
        }
    }

    private static void DrawHourMarkers(DrawingContext dc, Point center, double r)
    {
        double markerLen = r * 0.18;
        double thickness = Math.Max(2.0, r * 0.075);
        double markerCenter = r * 0.70;

        for (int h = 0; h < 12; h++)
        {
            // The 3 o'clock marker is replaced by the date complication.
            if (h == 3) continue;

            double angle = h * 30.0;
            dc.PushTransform(new RotateTransform(angle, center.X, center.Y));
            var rect = new Rect(
                center.X - thickness / 2.0,
                center.Y - (markerCenter + markerLen / 2.0),
                thickness,
                markerLen);
            dc.DrawRoundedRectangle(MajorTickBrush, null, rect, thickness / 2.0, thickness / 2.0);
            dc.Pop();
        }
    }

    private void DrawDate(DrawingContext dc, Point center, double r, int day)
    {
        double fontSize = r * 0.30;
        var text = new FormattedText(
            day.ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            DateTypeface,
            fontSize,
            AccentBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        // Centered on the 3 o'clock position.
        double cx = center.X + r * 0.56;
        double cy = center.Y;
        var origin = new Point(cx - text.Width / 2.0, cy - text.Height / 2.0);
        dc.DrawText(text, origin);
    }

    private static void DrawHands(DrawingContext dc, Point center, double r, DateTime now)
    {
        double seconds = now.Second + now.Millisecond / 1000.0;
        double minutes = now.Minute + seconds / 60.0;
        double hours = (now.Hour % 12) + minutes / 60.0;

        double hourAngle = hours * 30.0;
        double minuteAngle = minutes * 6.0;
        double secondAngle = seconds * 6.0;

        // Hour hand
        DrawHand(dc, center, hourAngle, HandBrush, null,
            length: r * 0.52, tail: r * 0.11, thickness: Math.Max(2.5, r * 0.085));

        // Minute hand
        DrawHand(dc, center, minuteAngle, HandBrush, null,
            length: r * 0.74, tail: r * 0.13, thickness: Math.Max(2.0, r * 0.065));

        // Second hand (red, thin)
        DrawHand(dc, center, secondAngle, AccentBrush, null,
            length: r * 0.82, tail: r * 0.22, thickness: Math.Max(1.2, r * 0.022));
    }

    private static void DrawHand(DrawingContext dc, Point center, double angleDeg, Brush brush, Pen? pen,
        double length, double tail, double thickness)
    {
        dc.PushTransform(new RotateTransform(angleDeg, center.X, center.Y));
        var rect = new Rect(
            center.X - thickness / 2.0,
            center.Y - length,
            thickness,
            length + tail);
        dc.DrawRoundedRectangle(brush, pen, rect, thickness / 2.0, thickness / 2.0);
        dc.Pop();
    }

    private static void DrawCenterHub(DrawingContext dc, Point center, double r)
    {
        double hub = Math.Max(2.5, r * 0.06);
        dc.DrawEllipse(AccentBrush, null, center, hub, hub);
        dc.DrawEllipse(HubHoleBrush, null, center, hub * 0.42, hub * 0.42);
    }
}
