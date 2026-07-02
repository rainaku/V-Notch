using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using VNotch.Modules;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch.Presenters;

public sealed class CalendarPresenter : IDisposable
{
    private readonly CalendarModule _module;
    private readonly IDispatcherService _dispatcher;
    private readonly CalendarViewRefs _refs;
    private readonly CalendarScrollMath _math = new();

    private bool _calendarInitialized;
    private readonly TextBlock[] _calendarDayNames = new TextBlock[CalendarScrollMath.TotalDays];
    private readonly Border[] _calendarDayBorders = new Border[CalendarScrollMath.TotalDays];
    private readonly TextBlock[] _calendarDayNumbers = new TextBlock[CalendarScrollMath.TotalDays];
    private double _calendarScrollX = 0.0;
    private int _currentCalendarCenterIdx = 5;
    private double _calendarScrollAccumulator = 0;
    private DateTime _lastCalendarScrollTime = DateTime.MinValue;
    private DateTime _lastCalendarUpdate = DateTime.Now;
    private bool _isMonthAnimating;
    private string _pendingMonthText = string.Empty;

    private bool _disposed;

    public CalendarPresenter(CalendarModule module, IDispatcherService dispatcher, CalendarViewRefs refs)
    {
        _module = module ?? throw new ArgumentNullException(nameof(module));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _refs = refs ?? throw new ArgumentNullException(nameof(refs));

        _module.CalendarUpdated += OnCalendarUpdated;
    }

    #region Calendar Rendering

    private void InitializeCalendar()
    {
        if (_calendarInitialized) return;

        _refs.WeekDaysPanel.Children.Clear();
        _refs.WeekNumbers.Children.Clear();

        for (int i = 0; i < CalendarScrollMath.TotalDays; i++)
        {
            _calendarDayNames[i] = new TextBlock
            {
                Style = _refs.SmallTextStyle,
                FontSize = 9,
                Width = CalendarScrollMath.CellWidth,
                TextAlignment = TextAlignment.Center,
                FontWeight = FontWeights.Bold
            };
            _refs.WeekDaysPanel.Children.Add(_calendarDayNames[i]);

            _calendarDayNumbers[i] = new TextBlock
            {
                Style = _refs.TitleTextStyle,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            _calendarDayBorders[i] = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness((CalendarScrollMath.CellWidth - 24) / 2, 0, (CalendarScrollMath.CellWidth - 24) / 2, 0),
                Child = _calendarDayNumbers[i]
            };
            _refs.WeekNumbers.Children.Add(_calendarDayBorders[i]);
        }

        _currentCalendarCenterIdx = 5;
        _calendarScrollX = _math.GetStripXForIndex(_currentCalendarCenterIdx);
        _refs.CalendarStripTranslate.X = _calendarScrollX;
        _refs.CalendarHighlightTranslate.X = _math.GetHighlightXForIndex(_currentCalendarCenterIdx);

        _calendarInitialized = true;
    }

    private void OnCalendarUpdated(object? sender, CalendarUpdateEventArgs e)
    {
        if (_dispatcher.CheckAccess())
        {
            HandleCalendarUpdated(e.Now);
        }
        else
        {
            _dispatcher.BeginInvoke(() => HandleCalendarUpdated(e.Now));
        }
    }

    private void HandleCalendarUpdated(DateTime now)
    {
        if (!_calendarInitialized) InitializeCalendar();

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
        _refs.EventText.Text = Loc.Get("greeting.enjoyDay");

        _refs.OnCalendarTick(now);
    }

    private void ApplyCalendarCenterVisualState(int centerIdx)
    {
        var highlightedDate = _lastCalendarUpdate.AddDays(centerIdx - 5);
        string newMonth = highlightedDate.ToString("MMM", CultureInfo.InvariantCulture);

        if (_refs.MonthText.Text != newMonth)
        {
            AnimateMonthTextChange(newMonth);
        }

        for (int i = 0; i < CalendarScrollMath.TotalDays; i++)
        {
            _calendarDayNumbers[i].Foreground = (i == centerIdx) ? _refs.BrushBlack : _refs.BrushWhite;
            _calendarDayBorders[i].Background = _refs.BrushTransparent;
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
            _refs.MonthText.Text = newMonth;

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

                if (!string.IsNullOrEmpty(_pendingMonthText) && _pendingMonthText != _refs.MonthText.Text)
                {
                    AnimateMonthTextChange(_pendingMonthText);
                }
            };

            Timeline.SetDesiredFrameRate(fadeIn, VNotch.Services.AnimationConfig.TargetFps);
            Timeline.SetDesiredFrameRate(scaleUp, VNotch.Services.AnimationConfig.TargetFps);
            Timeline.SetDesiredFrameRate(slideDown, VNotch.Services.AnimationConfig.TargetFps);

            _refs.MonthText.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            _refs.MonthTextScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleUp);
            _refs.MonthTextScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleUp);
            _refs.MonthTextTranslate.BeginAnimation(TranslateTransform.YProperty, slideDown);
        };

        Timeline.SetDesiredFrameRate(fadeOut, VNotch.Services.AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(scaleDown, VNotch.Services.AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(slideUp, VNotch.Services.AnimationConfig.TargetFps);

        _refs.MonthText.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        _refs.MonthTextScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDown);
        _refs.MonthTextScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDown);
        _refs.MonthTextTranslate.BeginAnimation(TranslateTransform.YProperty, slideUp);
    }

    private void AnimateCalendarHighlightToIndex(int centerIdx, Duration duration, IEasingFunction easing, bool pulse)
    {
        double targetHighlightX = _math.GetHighlightXForIndex(centerIdx);
        double currentHighlightX = (double)_refs.CalendarHighlightTranslate.GetValue(TranslateTransform.XProperty);

        _refs.CalendarHighlightScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _refs.CalendarHighlightScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _refs.CalendarHighlightTranslate.BeginAnimation(TranslateTransform.YProperty, null);

        var moveAnim = new DoubleAnimation
        {
            From = currentHighlightX,
            To = targetHighlightX,
            Duration = duration,
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(moveAnim, VNotch.Services.AnimationConfig.TargetFps);
        _refs.CalendarHighlightTranslate.BeginAnimation(TranslateTransform.XProperty, moveAnim, HandoffBehavior.SnapshotAndReplace);

        if (!pulse)
        {
            _refs.CalendarHighlightScale.ScaleX = 1.0;
            _refs.CalendarHighlightScale.ScaleY = 1.0;
            _refs.CalendarHighlightTranslate.Y = 0.0;
            return;
        }

        var squashX = new DoubleAnimationUsingKeyFrames { Duration = duration };
        squashX.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(0.0)));
        squashX.KeyFrames.Add(new EasingDoubleKeyFrame(1.085, KeyTime.FromPercent(0.28), _easeSineInOut));
        squashX.KeyFrames.Add(new EasingDoubleKeyFrame(0.975, KeyTime.FromPercent(0.62), _easeSineInOut));
        squashX.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), _easeSineInOut));
        Timeline.SetDesiredFrameRate(squashX, VNotch.Services.AnimationConfig.TargetFps);

        var squashY = new DoubleAnimationUsingKeyFrames { Duration = duration };
        squashY.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(0.0)));
        squashY.KeyFrames.Add(new EasingDoubleKeyFrame(0.93, KeyTime.FromPercent(0.28), _easeSineInOut));
        squashY.KeyFrames.Add(new EasingDoubleKeyFrame(1.02, KeyTime.FromPercent(0.62), _easeSineInOut));
        squashY.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), _easeSineInOut));
        Timeline.SetDesiredFrameRate(squashY, VNotch.Services.AnimationConfig.TargetFps);

        _refs.CalendarHighlightTranslate.Y = 0.0;
        _refs.CalendarHighlightScale.BeginAnimation(ScaleTransform.ScaleXProperty, squashX, HandoffBehavior.SnapshotAndReplace);
        _refs.CalendarHighlightScale.BeginAnimation(ScaleTransform.ScaleYProperty, squashY, HandoffBehavior.SnapshotAndReplace);
    }

    private void UpdateCalendarHighlight(bool animate = true, bool pulse = false)
    {
        if (!_calendarInitialized) return;

        double currentX = (double)_refs.CalendarStripTranslate.GetValue(TranslateTransform.XProperty);
        int centerIdx = _math.GetCenterIndexFromStripX(currentX);
        ApplyCalendarCenterVisualState(centerIdx);

        if (animate)
        {
            AnimateCalendarHighlightToIndex(centerIdx, new Duration(TimeSpan.FromMilliseconds(340)), _easeQuadInOut, pulse);
        }
        else
        {
            _refs.CalendarHighlightTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            _refs.CalendarHighlightTranslate.X = _math.GetHighlightXForIndex(centerIdx);
            _refs.CalendarHighlightScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _refs.CalendarHighlightScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _refs.CalendarHighlightScale.ScaleX = 1.0;
            _refs.CalendarHighlightScale.ScaleY = 1.0;
        }
    }

    public void UpdateCalendarInfo()
    {
    }

    #endregion

    #region Calendar Hover & Scroll

    public void HandleMouseEnter()
    {
        AnimateCalendarWidgetHover(isHovered: true);
        AnimateCalendarContextFocus(isFocused: true);
    }

    public void HandleMouseLeave()
    {
        AnimateCalendarWidgetHover(isHovered: false);
        AnimateCalendarContextFocus(isFocused: false);
    }

    private void AnimateCalendarWidgetHover(bool isHovered)
    {
        double targetScale = isHovered ? 1.035 : 1.0;
        double targetLiftY = isHovered ? -3.0 : 0.0;

        var duration = new Duration(TimeSpan.FromMilliseconds(isHovered ? 350 : 250));
        var easing = (IEasingFunction)(isHovered
            ? new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut }
            : new CubicEase { EasingMode = EasingMode.EaseOut });
        int fps = VNotch.Services.AnimationConfig.TargetFps;

        var scaleXAnim = new DoubleAnimation { To = targetScale, Duration = duration, EasingFunction = easing };
        var scaleYAnim = new DoubleAnimation { To = targetScale, Duration = duration, EasingFunction = easing };
        var liftAnim = new DoubleAnimation { To = targetLiftY, Duration = duration, EasingFunction = easing };

        Timeline.SetDesiredFrameRate(scaleXAnim, fps);
        Timeline.SetDesiredFrameRate(scaleYAnim, fps);
        Timeline.SetDesiredFrameRate(liftAnim, fps);

        _refs.CalendarWidgetScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim, HandoffBehavior.SnapshotAndReplace);
        _refs.CalendarWidgetScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim, HandoffBehavior.SnapshotAndReplace);
        _refs.CalendarWidgetTranslate.BeginAnimation(TranslateTransform.YProperty, liftAnim, HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimateCalendarContextFocus(bool isFocused)
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(isFocused ? 450 : 360));
        var easing = (IEasingFunction)_easeSineInOut;

        AnimateOpacity(_refs.BatterySection, isFocused ? 0.62 : 1.0, duration, easing);
        AnimateOpacity(_refs.SettingsButton, isFocused ? 0.62 : 1.0, duration, easing);
        AnimateOpacity(_refs.GreetingSection, isFocused ? 0.62 : 1.0, duration, easing);
        AnimateBlurRadius(_refs.CalendarGreetingContextBlur, _refs.SettingsProvider().EnableBlurEffects && isFocused ? 4.0 : 0.0, duration, easing);
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
        Timeline.SetDesiredFrameRate(anim, VNotch.Services.AnimationConfig.TargetFps);
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
        Timeline.SetDesiredFrameRate(anim, VNotch.Services.AnimationConfig.TargetFps);
        effect.BeginAnimation(BlurEffect.RadiusProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    public void ResetHoverFocusVisualState()
    {
        _refs.CalendarWidgetScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _refs.CalendarWidgetScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _refs.CalendarWidgetTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        _refs.CalendarWidgetScale.ScaleX = 1.0;
        _refs.CalendarWidgetScale.ScaleY = 1.0;
        _refs.CalendarWidgetTranslate.Y = 0.0;

        ResetCalendarContextElement(_refs.BatterySection, null);
        ResetCalendarContextElement(_refs.SettingsButton, null);
        ResetCalendarContextElement(_refs.GreetingSection, _refs.CalendarGreetingContextBlur);
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

    public void HandleMouseWheel(MouseWheelEventArgs e)
    {
        if (_refs.IsNonCalendarWidgetMode()) return;
        if (!_calendarInitialized) return;

        var step = _math.ComputeScrollStep(_calendarScrollAccumulator, e.Delta, _currentCalendarCenterIdx);
        _calendarScrollAccumulator = step.ResultAccumulator;

        if (!step.HasStep)
        {
            e.Handled = true;
            return;
        }

        if ((DateTime.Now - _lastCalendarScrollTime).TotalMilliseconds < 70)
        {
            e.Handled = true;
            return;
        }
        _lastCalendarScrollTime = DateTime.Now;

        if (!step.IndexChanged)
        {
            e.Handled = true;
            return;
        }

        _currentCalendarCenterIdx = step.NewCenterIdx;
        double newX = _math.GetStripXForIndex(_currentCalendarCenterIdx);
        _calendarScrollX = newX;

        double currentX = (double)_refs.CalendarStripTranslate.GetValue(TranslateTransform.XProperty);
        int movedCells = step.MovedCells;
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
        Timeline.SetDesiredFrameRate(scrollAnim, VNotch.Services.AnimationConfig.TargetFps);
        _refs.CalendarStripTranslate.BeginAnimation(TranslateTransform.XProperty, scrollAnim, HandoffBehavior.SnapshotAndReplace);

        ApplyCalendarCenterVisualState(_currentCalendarCenterIdx);
        AnimateCalendarHighlightToIndex(_currentCalendarCenterIdx, duration, easing, pulse: true);

        e.Handled = true;
    }

    public void ResetCalendarScroll()
    {
        if (!_calendarInitialized) return;

        _currentCalendarCenterIdx = 5;
        double targetX = _math.GetStripXForIndex(_currentCalendarCenterIdx);

        if (Math.Abs(_calendarScrollX - targetX) < 0.1) return;

        _calendarScrollX = targetX;
        _calendarScrollAccumulator = 0;

        double currentX = (double)_refs.CalendarStripTranslate.GetValue(TranslateTransform.XProperty);
        var duration = new Duration(TimeSpan.FromMilliseconds(420));
        var easing = (IEasingFunction)_easeSoftSpring;

        var scrollAnim = new DoubleAnimation
        {
            From = currentX,
            To = targetX,
            Duration = duration,
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(scrollAnim, VNotch.Services.AnimationConfig.TargetFps);
        _refs.CalendarStripTranslate.BeginAnimation(TranslateTransform.XProperty, scrollAnim, HandoffBehavior.SnapshotAndReplace);

        ApplyCalendarCenterVisualState(_currentCalendarCenterIdx);
        AnimateCalendarHighlightToIndex(_currentCalendarCenterIdx, duration, easing, pulse: false);
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _module.CalendarUpdated -= OnCalendarUpdated;
        _disposed = true;
    }
}
