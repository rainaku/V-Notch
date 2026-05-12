using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using VNotch.Modules;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;
public partial class MainWindow
{
    #region Calendar Rendering

    private void InitializeCalendar()
    {
        if (_calendarInitialized) return;

        WeekDaysPanel.Children.Clear();
        WeekNumbers.Children.Clear();

        for (int i = 0; i < CalendarTotalDays; i++)
        {
            _calendarDayNames[i] = new TextBlock
            {
                Style = (Style)FindResource("SmallText"),
                FontSize = 9,
                Width = CalendarCellWidth,
                TextAlignment = TextAlignment.Center,
                FontWeight = FontWeights.Bold
            };
            WeekDaysPanel.Children.Add(_calendarDayNames[i]);

            _calendarDayNumbers[i] = new TextBlock
            {
                Style = (Style)FindResource("TitleText"),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            _calendarDayBorders[i] = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness((CalendarCellWidth - 24) / 2, 0, (CalendarCellWidth - 24) / 2, 0),
                Child = _calendarDayNumbers[i]
            };
            WeekNumbers.Children.Add(_calendarDayBorders[i]);
        }

        _currentCalendarCenterIdx = 5;
        _calendarScrollX = (1 * CalendarCellWidth) - (_currentCalendarCenterIdx * CalendarCellWidth);
        CalendarStripTranslate.X = _calendarScrollX;
        CalendarHighlightTranslate.X = _currentCalendarCenterIdx * CalendarCellWidth + (CalendarCellWidth - 24) / 2.0;

        _calendarInitialized = true;
    }

    private void CalendarModule_CalendarUpdated(object? sender, CalendarUpdateEventArgs e)
    {
        if (!_calendarInitialized) InitializeCalendar();

        var now = e.Now;
        _lastCalendarUpdate = now;

        var dayNames = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };

        for (int i = -5; i <= 5; i++)
        {
            int idx = i + 5;
            var date = now.AddDays(i);

            _calendarDayNames[idx].Text = dayNames[(int)date.DayOfWeek];
            _calendarDayNumbers[idx].Text = date.Day.ToString();
        }

        UpdateCalendarHighlight(animate: false, pulse: false);
        EventText.Text = "Enjoy your day!";
    }

    private int GetCalendarCenterIndexFromStripX(double stripX)
    {
        int centerIdx = (int)Math.Round((30.0 - stripX) / CalendarCellWidth);
        return Math.Max(0, Math.Min(CalendarTotalDays - 1, centerIdx));
    }

    private static double GetCalendarHighlightXForIndex(int centerIdx)
    {
        return centerIdx * CalendarCellWidth + (CalendarCellWidth - 24) / 2.0;
    }

    private void ApplyCalendarCenterVisualState(int centerIdx)
    {
        var highlightedDate = _lastCalendarUpdate.AddDays(centerIdx - 5);
        string newMonth = highlightedDate.ToString("MMM", CultureInfo.InvariantCulture);

        if (MonthText.Text != newMonth)
        {
            AnimateMonthTextChange(newMonth);
        }

        for (int i = 0; i < CalendarTotalDays; i++)
        {
            _calendarDayNumbers[i].Foreground = (i == centerIdx) ? _brushBlack : _brushWhite;
            _calendarDayBorders[i].Background = _brushTransparent;
        }
    }

    private void AnimateMonthTextChange(string newMonth)
    {
        if (_isMonthAnimating)
        {
            _pendingMonthText = newMonth;
            return;
        }

        _isMonthAnimating = true;
        _pendingMonthText = string.Empty;

        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };

        var fadeOut = new DoubleAnimation
        {
            From = 1.0,
            To = 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(140)),
            EasingFunction = easeIn
        };

        var scaleDown = new DoubleAnimation
        {
            From = 1.0,
            To = 0.85,
            Duration = new Duration(TimeSpan.FromMilliseconds(140)),
            EasingFunction = easeIn
        };

        var slideUp = new DoubleAnimation
        {
            From = 0,
            To = -8,
            Duration = new Duration(TimeSpan.FromMilliseconds(140)),
            EasingFunction = easeIn
        };

        fadeOut.Completed += (s, e) =>
        {
            MonthText.Text = newMonth;

            var fadeIn = new DoubleAnimation
            {
                From = 0.0,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(140)),
                EasingFunction = easeOut
            };

            var scaleUp = new DoubleAnimation
            {
                From = 0.85,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(140)),
                EasingFunction = easeOut
            };

            var slideDown = new DoubleAnimation
            {
                From = 8,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(140)),
                EasingFunction = easeOut
            };

            fadeIn.Completed += (s2, e2) =>
            {
                _isMonthAnimating = false;

                if (!string.IsNullOrEmpty(_pendingMonthText) && _pendingMonthText != MonthText.Text)
                {
                    AnimateMonthTextChange(_pendingMonthText);
                }
            };

            Timeline.SetDesiredFrameRate(fadeIn, 60);
            Timeline.SetDesiredFrameRate(scaleUp, 60);
            Timeline.SetDesiredFrameRate(slideDown, 60);

            MonthText.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            MonthTextScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleUp);
            MonthTextScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleUp);
            MonthTextTranslate.BeginAnimation(TranslateTransform.YProperty, slideDown);
        };

        Timeline.SetDesiredFrameRate(fadeOut, 60);
        Timeline.SetDesiredFrameRate(scaleDown, 60);
        Timeline.SetDesiredFrameRate(slideUp, 60);

        MonthText.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        MonthTextScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDown);
        MonthTextScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDown);
        MonthTextTranslate.BeginAnimation(TranslateTransform.YProperty, slideUp);
    }

    private void AnimateCalendarHighlightToIndex(int centerIdx, Duration duration, IEasingFunction easing, bool pulse)
    {
        double targetHighlightX = GetCalendarHighlightXForIndex(centerIdx);
        double currentHighlightX = (double)CalendarHighlightTranslate.GetValue(TranslateTransform.XProperty);

        CalendarHighlightScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CalendarHighlightScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CalendarHighlightTranslate.BeginAnimation(TranslateTransform.YProperty, null);

        var moveAnim = new DoubleAnimation
        {
            From = currentHighlightX,
            To = targetHighlightX,
            Duration = duration,
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(moveAnim, 120);
        CalendarHighlightTranslate.BeginAnimation(TranslateTransform.XProperty, moveAnim, HandoffBehavior.SnapshotAndReplace);

        if (!pulse)
        {
            CalendarHighlightScale.ScaleX = 1.0;
            CalendarHighlightScale.ScaleY = 1.0;
            CalendarHighlightTranslate.Y = 0.0;
            return;
        }

        var squashX = new DoubleAnimationUsingKeyFrames { Duration = duration };
        squashX.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(0.0)));
        squashX.KeyFrames.Add(new EasingDoubleKeyFrame(1.085, KeyTime.FromPercent(0.28), _easeSineInOut));
        squashX.KeyFrames.Add(new EasingDoubleKeyFrame(0.975, KeyTime.FromPercent(0.62), _easeSineInOut));
        squashX.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), _easeSineInOut));
        Timeline.SetDesiredFrameRate(squashX, 120);

        var squashY = new DoubleAnimationUsingKeyFrames { Duration = duration };
        squashY.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(0.0)));
        squashY.KeyFrames.Add(new EasingDoubleKeyFrame(0.93, KeyTime.FromPercent(0.28), _easeSineInOut));
        squashY.KeyFrames.Add(new EasingDoubleKeyFrame(1.02, KeyTime.FromPercent(0.62), _easeSineInOut));
        squashY.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), _easeSineInOut));
        Timeline.SetDesiredFrameRate(squashY, 120);

        CalendarHighlightTranslate.Y = 0.0;
        CalendarHighlightScale.BeginAnimation(ScaleTransform.ScaleXProperty, squashX, HandoffBehavior.SnapshotAndReplace);
        CalendarHighlightScale.BeginAnimation(ScaleTransform.ScaleYProperty, squashY, HandoffBehavior.SnapshotAndReplace);
    }

    private void UpdateCalendarHighlight(bool animate = true, bool pulse = false)
    {
        if (!_calendarInitialized) return;

        double currentX = (double)CalendarStripTranslate.GetValue(TranslateTransform.XProperty);
        int centerIdx = GetCalendarCenterIndexFromStripX(currentX);
        ApplyCalendarCenterVisualState(centerIdx);

        if (animate)
        {
            AnimateCalendarHighlightToIndex(centerIdx, new Duration(TimeSpan.FromMilliseconds(340)), _easeQuadInOut, pulse);
        }
        else
        {
            CalendarHighlightTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            CalendarHighlightTranslate.X = GetCalendarHighlightXForIndex(centerIdx);
            CalendarHighlightScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CalendarHighlightScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            CalendarHighlightScale.ScaleX = 1.0;
            CalendarHighlightScale.ScaleY = 1.0;
        }
    }

    private void UpdateCalendarInfo()
    {
        // Reserved: explicit poke point when the notch is expanded or the
        // calendar module otherwise wants a non-event driven refresh.
    }

    #endregion

    #region Calendar Hover & Scroll

    private void CalendarWidget_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        AnimateCalendarWidgetHover(isHovered: true);
        AnimateCalendarContextFocus(isFocused: true);
    }

    private void CalendarWidget_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        AnimateCalendarWidgetHover(isHovered: false);
        AnimateCalendarContextFocus(isFocused: false);
    }

    private void AnimateCalendarWidgetHover(bool isHovered)
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(isHovered ? 350 : 420));
        double currentScaleX = (double)CalendarWidgetScale.GetValue(ScaleTransform.ScaleXProperty);
        double currentScaleY = (double)CalendarWidgetScale.GetValue(ScaleTransform.ScaleYProperty);
        double currentLiftY = (double)CalendarWidgetTranslate.GetValue(TranslateTransform.YProperty);

        var scaleXAnim = new DoubleAnimationUsingKeyFrames { Duration = duration };
        var scaleYAnim = new DoubleAnimationUsingKeyFrames { Duration = duration };
        var liftAnim = new DoubleAnimationUsingKeyFrames { Duration = duration };

        scaleXAnim.KeyFrames.Add(new EasingDoubleKeyFrame(currentScaleX, KeyTime.FromPercent(0.0)));
        scaleYAnim.KeyFrames.Add(new EasingDoubleKeyFrame(currentScaleY, KeyTime.FromPercent(0.0)));
        liftAnim.KeyFrames.Add(new EasingDoubleKeyFrame(currentLiftY, KeyTime.FromPercent(0.0)));

        if (isHovered)
        {
            scaleXAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.135, KeyTime.FromPercent(0.40), _easeSineInOut));
            scaleYAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0.955, KeyTime.FromPercent(0.40), _easeSineInOut));
            scaleXAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.095, KeyTime.FromPercent(0.72), _easeSineInOut));
            scaleYAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.085, KeyTime.FromPercent(0.72), _easeSineInOut));
            scaleXAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.105, KeyTime.FromPercent(1.0), _easeSineInOut));
            scaleYAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.105, KeyTime.FromPercent(1.0), _easeSineInOut));

            liftAnim.KeyFrames.Add(new EasingDoubleKeyFrame(-3.7, KeyTime.FromPercent(0.48), _easeSineInOut));
            liftAnim.KeyFrames.Add(new EasingDoubleKeyFrame(-3.1, KeyTime.FromPercent(1.0), _easeSineInOut));
        }
        else
        {
            scaleXAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0.985, KeyTime.FromPercent(0.42), _easeSineInOut));
            scaleYAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.02, KeyTime.FromPercent(0.42), _easeSineInOut));
            scaleXAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), _easeSineInOut));
            scaleYAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), _easeSineInOut));

            liftAnim.KeyFrames.Add(new EasingDoubleKeyFrame(-0.6, KeyTime.FromPercent(0.42), _easeSineInOut));
            liftAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromPercent(1.0), _easeSineInOut));
        }

        Timeline.SetDesiredFrameRate(scaleXAnim, 120);
        Timeline.SetDesiredFrameRate(scaleYAnim, 120);
        Timeline.SetDesiredFrameRate(liftAnim, 120);

        CalendarWidgetScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim, HandoffBehavior.SnapshotAndReplace);
        CalendarWidgetScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim, HandoffBehavior.SnapshotAndReplace);
        CalendarWidgetTranslate.BeginAnimation(TranslateTransform.YProperty, liftAnim, HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimateCalendarContextFocus(bool isFocused)
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(isFocused ? 450 : 360));
        var easing = (IEasingFunction)_easeSineInOut;

        AnimateOpacity(BatterySection, isFocused ? 0.62 : 1.0, duration, easing);
        AnimateOpacity(NavIconsPanel, isFocused ? 0.62 : 1.0, duration, easing);
        AnimateOpacity(GreetingSection, isFocused ? 0.62 : 1.0, duration, easing);
        AnimateBlurRadius(CalendarGreetingContextBlur, isFocused ? 4.0 : 0.0, duration, easing);
    }

    private static void AnimateOpacity(UIElement element, double to, Duration duration, IEasingFunction easing)
    {
        var anim = new DoubleAnimation
        {
            From = (double)element.GetValue(UIElement.OpacityProperty),
            To = to,
            Duration = duration,
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(anim, 120);
        element.BeginAnimation(UIElement.OpacityProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    private static void AnimateBlurRadius(BlurEffect effect, double to, Duration duration, IEasingFunction easing)
    {
        var anim = new DoubleAnimation
        {
            From = (double)effect.GetValue(BlurEffect.RadiusProperty),
            To = to,
            Duration = duration,
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(anim, 120);
        effect.BeginAnimation(BlurEffect.RadiusProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    private void ResetCalendarHoverFocusVisualState()
    {
        CalendarWidgetScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CalendarWidgetScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CalendarWidgetTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        CalendarWidgetScale.ScaleX = 1.0;
        CalendarWidgetScale.ScaleY = 1.0;
        CalendarWidgetTranslate.Y = 0.0;

        ResetCalendarContextElement(BatterySection, null);
        ResetCalendarContextElement(NavIconsPanel, null);
        ResetCalendarContextElement(GreetingSection, CalendarGreetingContextBlur);
    }

    private static void ResetCalendarContextElement(UIElement element, BlurEffect? effect)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1.0;
        if (effect != null)
        {
            effect.BeginAnimation(BlurEffect.RadiusProperty, null);
            effect.Radius = 0.0;
        }
    }

    private void CalendarWidget_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (!_calendarInitialized) return;

        _calendarScrollAccumulator += e.Delta;
        int direction = _calendarScrollAccumulator > 0 ? -1 : 1;
        int stepCount = (int)(Math.Abs(_calendarScrollAccumulator) / 120.0);
        if (stepCount == 0 && Math.Abs(_calendarScrollAccumulator) >= 72)
        {
            stepCount = 1;
        }
        if (stepCount == 0)
        {
            e.Handled = true;
            return;
        }

        _calendarScrollAccumulator -= Math.Sign(_calendarScrollAccumulator) * stepCount * 120.0;

        if ((DateTime.Now - _lastCalendarScrollTime).TotalMilliseconds < 70)
        {
            e.Handled = true;
            return;
        }
        _lastCalendarScrollTime = DateTime.Now;

        int oldIdx = _currentCalendarCenterIdx;
        int newIdx = _currentCalendarCenterIdx + (direction * stepCount);
        newIdx = Math.Max(0, Math.Min(CalendarTotalDays - 1, newIdx));

        if (newIdx == oldIdx)
        {
            e.Handled = true;
            return;
        }

        _currentCalendarCenterIdx = newIdx;
        double newX = (1 * CalendarCellWidth) - (_currentCalendarCenterIdx * CalendarCellWidth);
        _calendarScrollX = newX;

        double currentX = (double)CalendarStripTranslate.GetValue(TranslateTransform.XProperty);
        int movedCells = Math.Abs(newIdx - oldIdx);
        double durationMs = Math.Clamp(240 + (movedCells * 90), 240, 520);
        var duration = new Duration(TimeSpan.FromMilliseconds(durationMs));
        var easing = (IEasingFunction)_easeSoftSpring;

        var scrollAnim = new DoubleAnimation
        {
            From = currentX,
            To = newX,
            Duration = duration,
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(scrollAnim, 120);
        CalendarStripTranslate.BeginAnimation(TranslateTransform.XProperty, scrollAnim, HandoffBehavior.SnapshotAndReplace);

        ApplyCalendarCenterVisualState(_currentCalendarCenterIdx);
        AnimateCalendarHighlightToIndex(_currentCalendarCenterIdx, duration, easing, pulse: true);

        e.Handled = true;
    }

    public void ResetCalendarScroll()
    {
        if (!_calendarInitialized) return;

        _currentCalendarCenterIdx = 5;
        double targetX = (1 * CalendarCellWidth) - (_currentCalendarCenterIdx * CalendarCellWidth);

        if (Math.Abs(_calendarScrollX - targetX) < 0.1) return;

        _calendarScrollX = targetX;
        _calendarScrollAccumulator = 0;

        double currentX = (double)CalendarStripTranslate.GetValue(TranslateTransform.XProperty);
        var duration = new Duration(TimeSpan.FromMilliseconds(420));
        var easing = (IEasingFunction)_easeSoftSpring;

        var scrollAnim = new DoubleAnimation
        {
            From = currentX,
            To = targetX,
            Duration = duration,
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(scrollAnim, 120);
        CalendarStripTranslate.BeginAnimation(TranslateTransform.XProperty, scrollAnim, HandoffBehavior.SnapshotAndReplace);

        ApplyCalendarCenterVisualState(_currentCalendarCenterIdx);
        AnimateCalendarHighlightToIndex(_currentCalendarCenterIdx, duration, easing, pulse: false);
    }

    #endregion
}
