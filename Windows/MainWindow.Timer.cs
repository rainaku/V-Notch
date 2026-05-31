using System;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    // ─── Timer View State ───
    private bool _isTimerView = false;

    // ─── Countdown State ───
    private TimeSpan _countdownDuration = TimeSpan.FromMinutes(25);
    private TimeSpan _countdownRemaining = TimeSpan.FromMinutes(25);
    private bool _isCountdownRunning = false;
    private DispatcherTimer? _countdownTimer;

    // ─── Alarm State ───
    private int _alarmHour = 7;
    private int _alarmMinute = 0;
    private bool _isAlarmSet = false;
    private DispatcherTimer? _alarmCheckTimer;

    #region Timer View Navigation

    private void TimerIconButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_isTimerView && !_isAnimating)
        {
            if (_isSecondaryView)
            {
                // Switch from file shelf to timer
                SwitchFromSecondaryToTimerView();
            }
            else
            {
                // Switch from primary to timer
                SwitchToTimerView();
            }
        }
    }

    private void SwitchToTimerView()
    {
        if (_isTimerView || _isAnimating) return;
        _isTimerView = true;
        _isAnimating = true;
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;

        UpdateTimerNavIconsState();
        NavIconsPanel.Visibility = Visibility.Visible;
        NavIconsPanel.Opacity = 1;

        NavIconsBackground.BeginAnimation(OpacityProperty, null);
        NavIconsBackground.Opacity = 0;
        NavIconsBackground.Visibility = Visibility.Visible;
        var navBgFadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(300)))
        {
            EasingFunction = _easePowerOut3,
            BeginTime = TimeSpan.FromMilliseconds(200)
        };
        Timeline.SetDesiredFrameRate(navBgFadeIn, 120);
        NavIconsBackground.BeginAnimation(OpacityProperty, navBgFadeIn);

        NotchBorder.IsHitTestVisible = false;

        var durOut = new Duration(TimeSpan.FromMilliseconds(180));
        var durIn = new Duration(TimeSpan.FromMilliseconds(480));
        var inDelay = TimeSpan.FromMilliseconds(50);
        const int fps = 144;

        // Fade out primary content
        var primaryGroup = new TransformGroup();
        var primaryScale = new ScaleTransform(1, 1);
        var primaryTranslate = new TranslateTransform(0, 0);
        primaryGroup.Children.Add(primaryScale);
        primaryGroup.Children.Add(primaryTranslate);
        ExpandedContent.RenderTransform = primaryGroup;
        ExpandedContent.RenderTransformOrigin = new Point(0.5, 0.5);

        var fadeOut = MakeAnim(1, 0, durOut, _easeQuadIn);
        var slideUp = MakeAnim(0, -16, durOut, _easeQuadIn);
        var scaleDownX = MakeAnim(1, 0.93, durOut, _easeQuadIn);
        var scaleDownY = MakeAnim(1, 0.93, durOut, _easeQuadIn);
        Timeline.SetDesiredFrameRate(slideUp, fps);
        Timeline.SetDesiredFrameRate(scaleDownX, fps);
        Timeline.SetDesiredFrameRate(scaleDownY, fps);

        var expandedBlur = ExpandedContent.Effect as BlurEffect ?? new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
        ExpandedContent.Effect = expandedBlur;
        var blurOutAnim = MakeAnim(0, 10, durOut, _easeQuadIn);

        fadeOut.Completed += (s, ev) =>
        {
            ExpandedContent.Visibility = Visibility.Collapsed;
            ExpandedContent.RenderTransform = null;
            ExpandedContent.Effect = null;
            expandedBlur.Radius = 0;
        };

        ExpandedContent.BeginAnimation(OpacityProperty, fadeOut);
        primaryTranslate.BeginAnimation(TranslateTransform.YProperty, slideUp);
        primaryScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDownX);
        primaryScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDownY);
        expandedBlur.BeginAnimation(BlurEffect.RadiusProperty, blurOutAnim);

        // Fade in timer content
        TimerContent.Visibility = Visibility.Visible;
        TimerContent.Opacity = 0;

        var timerGroup = new TransformGroup();
        var timerScale = new ScaleTransform(0.93, 0.93);
        var timerTranslate = new TranslateTransform(0, 26);
        timerGroup.Children.Add(timerScale);
        timerGroup.Children.Add(timerTranslate);
        TimerContent.RenderTransform = timerGroup;
        TimerContent.RenderTransformOrigin = new Point(0.5, 0.5);

        var fadeIn = MakeAnim(0, 1, durIn, _easeExpOut6, inDelay);
        var springSlide = MakeAnim(26, 0, durIn, _easeExpOut7, inDelay);
        var springScaleX = MakeAnim(0.93, 1, durIn, _easeSoftSpring, inDelay);
        var springScaleY = MakeAnim(0.93, 1, durIn, _easeSoftSpring, inDelay);
        Timeline.SetDesiredFrameRate(fadeIn, fps);
        Timeline.SetDesiredFrameRate(springSlide, fps);
        Timeline.SetDesiredFrameRate(springScaleX, fps);
        Timeline.SetDesiredFrameRate(springScaleY, fps);

        fadeIn.Completed += (s, ev) =>
        {
            _isAnimating = false;
            NotchBorder.IsHitTestVisible = true;
            TimerContent.Opacity = 1;
            TimerContent.BeginAnimation(OpacityProperty, null);
            TimerContent.RenderTransform = null;
        };

        TimerContent.BeginAnimation(OpacityProperty, fadeIn);
        timerTranslate.BeginAnimation(TranslateTransform.YProperty, springSlide);
        timerScale.BeginAnimation(ScaleTransform.ScaleXProperty, springScaleX);
        timerScale.BeginAnimation(ScaleTransform.ScaleYProperty, springScaleY);

        UpdateTimerDisplay();
        UpdateAlarmDisplay();
    }

    private void SwitchFromSecondaryToTimerView()
    {
        if (_isTimerView || _isAnimating) return;
        _isTimerView = true;
        _isSecondaryView = false;
        _isAnimating = true;
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;

        UpdateTimerNavIconsState();
        NotchBorder.IsHitTestVisible = false;

        var durOut = new Duration(TimeSpan.FromMilliseconds(180));
        var durIn = new Duration(TimeSpan.FromMilliseconds(480));
        var inDelay = TimeSpan.FromMilliseconds(50);
        const int fps = 144;

        // Fade out secondary content
        var secondaryGroup = new TransformGroup();
        var secondaryScale = new ScaleTransform(1, 1);
        var secondaryTranslate = new TranslateTransform(0, 0);
        secondaryGroup.Children.Add(secondaryScale);
        secondaryGroup.Children.Add(secondaryTranslate);
        SecondaryContent.RenderTransform = secondaryGroup;
        SecondaryContent.RenderTransformOrigin = new Point(0.5, 0.5);

        var fadeOut = MakeAnim(1, 0, durOut, _easeQuadIn);
        var slideUp = MakeAnim(0, -16, durOut, _easeQuadIn);
        var scaleDownX = MakeAnim(1, 0.93, durOut, _easeQuadIn);
        var scaleDownY = MakeAnim(1, 0.93, durOut, _easeQuadIn);
        Timeline.SetDesiredFrameRate(slideUp, fps);
        Timeline.SetDesiredFrameRate(scaleDownX, fps);
        Timeline.SetDesiredFrameRate(scaleDownY, fps);

        var secondaryBlur = SecondaryContent.Effect as BlurEffect ?? new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
        SecondaryContent.Effect = secondaryBlur;
        var blurOutAnim = MakeAnim(0, 10, durOut, _easeQuadIn);

        fadeOut.Completed += (s, ev) =>
        {
            SecondaryContent.Visibility = Visibility.Collapsed;
            SecondaryContent.RenderTransform = null;
            SecondaryContent.Effect = null;
            secondaryBlur.Radius = 0;
        };

        SecondaryContent.BeginAnimation(OpacityProperty, fadeOut);
        secondaryTranslate.BeginAnimation(TranslateTransform.YProperty, slideUp);
        secondaryScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDownX);
        secondaryScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDownY);
        secondaryBlur.BeginAnimation(BlurEffect.RadiusProperty, blurOutAnim);

        // Fade in timer content
        TimerContent.Visibility = Visibility.Visible;
        TimerContent.Opacity = 0;

        var timerGroup = new TransformGroup();
        var timerScale = new ScaleTransform(0.93, 0.93);
        var timerTranslate = new TranslateTransform(0, 26);
        timerGroup.Children.Add(timerScale);
        timerGroup.Children.Add(timerTranslate);
        TimerContent.RenderTransform = timerGroup;
        TimerContent.RenderTransformOrigin = new Point(0.5, 0.5);

        var fadeIn = MakeAnim(0, 1, durIn, _easeExpOut6, inDelay);
        var springSlide = MakeAnim(26, 0, durIn, _easeExpOut7, inDelay);
        var springScaleX = MakeAnim(0.93, 1, durIn, _easeSoftSpring, inDelay);
        var springScaleY = MakeAnim(0.93, 1, durIn, _easeSoftSpring, inDelay);
        Timeline.SetDesiredFrameRate(fadeIn, fps);
        Timeline.SetDesiredFrameRate(springSlide, fps);
        Timeline.SetDesiredFrameRate(springScaleX, fps);
        Timeline.SetDesiredFrameRate(springScaleY, fps);

        fadeIn.Completed += (s, ev) =>
        {
            _isAnimating = false;
            NotchBorder.IsHitTestVisible = true;
            TimerContent.Opacity = 1;
            TimerContent.BeginAnimation(OpacityProperty, null);
            TimerContent.RenderTransform = null;
        };

        TimerContent.BeginAnimation(OpacityProperty, fadeIn);
        timerTranslate.BeginAnimation(TranslateTransform.YProperty, springSlide);
        timerScale.BeginAnimation(ScaleTransform.ScaleXProperty, springScaleX);
        timerScale.BeginAnimation(ScaleTransform.ScaleYProperty, springScaleY);

        UpdateTimerDisplay();
        UpdateAlarmDisplay();
    }

    private void SwitchFromTimerToPrimaryView()
    {
        if (!_isTimerView || _isAnimating) return;
        _isTimerView = false;
        _isAnimating = true;
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;

        UpdateNavIconsActiveState();
        NavIconsBackground.BeginAnimation(OpacityProperty, null);
        NavIconsBackground.Opacity = 0;
        NavIconsBackground.Visibility = Visibility.Collapsed;

        NotchBorder.IsHitTestVisible = false;

        var durOut = new Duration(TimeSpan.FromMilliseconds(180));
        var durIn = new Duration(TimeSpan.FromMilliseconds(480));
        var inDelay = TimeSpan.FromMilliseconds(50);
        const int fps = 144;

        // Fade out timer content
        var timerGroup = new TransformGroup();
        var timerScale = new ScaleTransform(1, 1);
        var timerTranslate = new TranslateTransform(0, 0);
        timerGroup.Children.Add(timerScale);
        timerGroup.Children.Add(timerTranslate);
        TimerContent.RenderTransform = timerGroup;
        TimerContent.RenderTransformOrigin = new Point(0.5, 0.5);

        var fadeOut = MakeAnim(1, 0, durOut, _easeQuadIn);
        var slideDown = MakeAnim(0, 16, durOut, _easeQuadIn);
        var scaleDownX = MakeAnim(1, 0.93, durOut, _easeQuadIn);
        var scaleDownY = MakeAnim(1, 0.93, durOut, _easeQuadIn);
        Timeline.SetDesiredFrameRate(slideDown, fps);
        Timeline.SetDesiredFrameRate(scaleDownX, fps);
        Timeline.SetDesiredFrameRate(scaleDownY, fps);

        var timerBlur = TimerContent.Effect as BlurEffect ?? new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
        TimerContent.Effect = timerBlur;
        var blurOutAnim = MakeAnim(0, 10, durOut, _easeQuadIn);

        fadeOut.Completed += (s, ev) =>
        {
            TimerContent.Visibility = Visibility.Collapsed;
            TimerContent.RenderTransform = null;
            TimerContent.Effect = null;
            timerBlur.Radius = 0;
        };

        TimerContent.BeginAnimation(OpacityProperty, fadeOut);
        timerTranslate.BeginAnimation(TranslateTransform.YProperty, slideDown);
        timerScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDownX);
        timerScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDownY);
        timerBlur.BeginAnimation(BlurEffect.RadiusProperty, blurOutAnim);

        // Fade in primary content
        ExpandedContent.Visibility = Visibility.Visible;
        ExpandedContent.Opacity = 0;
        ExpandedContent.Effect = null;

        var primaryGroup = new TransformGroup();
        var primaryScale = new ScaleTransform(0.93, 0.93);
        var primaryTranslate = new TranslateTransform(0, -26);
        primaryGroup.Children.Add(primaryScale);
        primaryGroup.Children.Add(primaryTranslate);
        ExpandedContent.RenderTransform = primaryGroup;
        ExpandedContent.RenderTransformOrigin = new Point(0.5, 0.5);

        var fadeIn = MakeAnim(0, 1, durIn, _easeExpOut6, inDelay);
        var springSlide = MakeAnim(-26, 0, durIn, _easeExpOut7, inDelay);
        var springScaleX = MakeAnim(0.93, 1, durIn, _easeSoftSpring, inDelay);
        var springScaleY = MakeAnim(0.93, 1, durIn, _easeSoftSpring, inDelay);
        Timeline.SetDesiredFrameRate(fadeIn, fps);
        Timeline.SetDesiredFrameRate(springSlide, fps);
        Timeline.SetDesiredFrameRate(springScaleX, fps);
        Timeline.SetDesiredFrameRate(springScaleY, fps);

        fadeIn.Completed += (s, ev) =>
        {
            _isAnimating = false;
            NotchBorder.IsHitTestVisible = true;
            ExpandedContent.Opacity = 1;
            ExpandedContent.BeginAnimation(OpacityProperty, null);
            ExpandedContent.RenderTransform = null;
        };

        ExpandedContent.BeginAnimation(OpacityProperty, fadeIn);
        primaryTranslate.BeginAnimation(TranslateTransform.YProperty, springSlide);
        primaryScale.BeginAnimation(ScaleTransform.ScaleXProperty, springScaleX);
        primaryScale.BeginAnimation(ScaleTransform.ScaleYProperty, springScaleY);
    }

    private void UpdateTimerNavIconsState()
    {
        HomeIconButton.Opacity = 0.4;
        FileShelfIconButton.Opacity = 0.4;
        TimerIconButton.Opacity = 1.0;
    }

    private void SwitchFromTimerToSecondaryView()
    {
        if (!_isTimerView || _isAnimating) return;
        _isTimerView = false;
        _isSecondaryView = true;
        _isAnimating = true;
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;

        UpdateNavIconsActiveState();
        NotchBorder.IsHitTestVisible = false;

        var durOut = new Duration(TimeSpan.FromMilliseconds(180));
        var durIn = new Duration(TimeSpan.FromMilliseconds(480));
        var inDelay = TimeSpan.FromMilliseconds(50);
        const int fps = 144;

        // Fade out timer content
        var timerGroup = new TransformGroup();
        var timerScale = new ScaleTransform(1, 1);
        var timerTranslate = new TranslateTransform(0, 0);
        timerGroup.Children.Add(timerScale);
        timerGroup.Children.Add(timerTranslate);
        TimerContent.RenderTransform = timerGroup;
        TimerContent.RenderTransformOrigin = new Point(0.5, 0.5);

        var fadeOut = MakeAnim(1, 0, durOut, _easeQuadIn);
        var slideUp = MakeAnim(0, -16, durOut, _easeQuadIn);
        var scaleDownX = MakeAnim(1, 0.93, durOut, _easeQuadIn);
        var scaleDownY = MakeAnim(1, 0.93, durOut, _easeQuadIn);
        Timeline.SetDesiredFrameRate(slideUp, fps);
        Timeline.SetDesiredFrameRate(scaleDownX, fps);
        Timeline.SetDesiredFrameRate(scaleDownY, fps);

        var timerBlur = TimerContent.Effect as BlurEffect ?? new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
        TimerContent.Effect = timerBlur;
        var blurOutAnim = MakeAnim(0, 10, durOut, _easeQuadIn);

        fadeOut.Completed += (s, ev) =>
        {
            TimerContent.Visibility = Visibility.Collapsed;
            TimerContent.RenderTransform = null;
            TimerContent.Effect = null;
            timerBlur.Radius = 0;
        };

        TimerContent.BeginAnimation(OpacityProperty, fadeOut);
        timerTranslate.BeginAnimation(TranslateTransform.YProperty, slideUp);
        timerScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDownX);
        timerScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDownY);
        timerBlur.BeginAnimation(BlurEffect.RadiusProperty, blurOutAnim);

        // Fade in secondary content
        SecondaryContent.Visibility = Visibility.Visible;
        SecondaryContent.Opacity = 0;
        EnableKeyboardInput();

        var secondaryGroup = new TransformGroup();
        var secondaryScale = new ScaleTransform(0.93, 0.93);
        var secondaryTranslate = new TranslateTransform(0, 26);
        secondaryGroup.Children.Add(secondaryScale);
        secondaryGroup.Children.Add(secondaryTranslate);
        SecondaryContent.RenderTransform = secondaryGroup;
        SecondaryContent.RenderTransformOrigin = new Point(0.5, 0.5);

        var fadeIn = MakeAnim(0, 1, durIn, _easeExpOut6, inDelay);
        var springSlide = MakeAnim(26, 0, durIn, _easeExpOut7, inDelay);
        var springScaleX = MakeAnim(0.93, 1, durIn, _easeSoftSpring, inDelay);
        var springScaleY = MakeAnim(0.93, 1, durIn, _easeSoftSpring, inDelay);
        Timeline.SetDesiredFrameRate(fadeIn, fps);
        Timeline.SetDesiredFrameRate(springSlide, fps);
        Timeline.SetDesiredFrameRate(springScaleX, fps);
        Timeline.SetDesiredFrameRate(springScaleY, fps);

        fadeIn.Completed += (s, ev) =>
        {
            _isAnimating = false;
            NotchBorder.IsHitTestVisible = true;
            SecondaryContent.Opacity = 1;
            SecondaryContent.BeginAnimation(OpacityProperty, null);
            SecondaryContent.RenderTransform = null;

            if (_isCameraActive)
            {
                AnimateCameraSectionToShelf(true);
            }
            else
            {
                ResetCameraSectionLayoutInstant();
            }
        };

        SecondaryContent.BeginAnimation(OpacityProperty, fadeIn);
        secondaryTranslate.BeginAnimation(TranslateTransform.YProperty, springSlide);
        secondaryScale.BeginAnimation(ScaleTransform.ScaleXProperty, springScaleX);
        secondaryScale.BeginAnimation(ScaleTransform.ScaleYProperty, springScaleY);

        UpdateShelfCapacityIndicator();
    }

    #endregion

    #region Timer View Microinteractions

    private void TimerControlButton_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border button)
        {
            AnimateTimerButtonScale(button, 1.045);
        }
    }

    private void TimerControlButton_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border button)
        {
            AnimateTimerButtonScale(button, 1.0);
        }
    }

    private void AnimateTimerButtonScale(Border button, double targetScale)
    {
        var scale = button.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        button.RenderTransform = scale;
        button.RenderTransformOrigin = new Point(0.5, 0.5);

        var animX = MakeAnim(scale.ScaleX, targetScale, _dur150, _easeQuadOut);
        var animY = MakeAnim(scale.ScaleY, targetScale, _dur150, _easeQuadOut);
        Timeline.SetDesiredFrameRate(animX, 144);
        Timeline.SetDesiredFrameRate(animY, 144);

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
    }

    private void PlayTimerButtonPress(Border button)
    {
        PlayButtonPressAnimation(button);
    }

    private void AnimateCountdownDisplayPulse(double targetScale = 1.025)
    {
        var scale = CountdownDisplayPanel.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        CountdownDisplayPanel.RenderTransform = scale;
        CountdownDisplayPanel.RenderTransformOrigin = new Point(0.5, 0.5);

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        var upX = MakeAnim(1.0, targetScale, _dur80, _easeQuadOut, null);
        var upY = MakeAnim(1.0, targetScale, _dur80, _easeQuadOut, null);
        var settleX = new DoubleAnimation(targetScale, 1.0, _dur250) { EasingFunction = _easeSoftSpring };
        var settleY = new DoubleAnimation(targetScale, 1.0, _dur250) { EasingFunction = _easeSoftSpring };
        Timeline.SetDesiredFrameRate(settleX, 144);
        Timeline.SetDesiredFrameRate(settleY, 144);

        upX.Completed += (_, _) => scale.BeginAnimation(ScaleTransform.ScaleXProperty, settleX);
        upY.Completed += (_, _) => scale.BeginAnimation(ScaleTransform.ScaleYProperty, settleY);

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, upX);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, upY);

        var textFlash = MakeAnim(0.72, 1.0, _dur200, _easeQuadOut, null);
        CountdownDisplay.BeginAnimation(OpacityProperty, textFlash);
    }

    private void AnimateAlarmDisplayPulse()
    {
        var scale = AlarmDisplayMode.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        AlarmDisplayMode.RenderTransform = scale;
        AlarmDisplayMode.RenderTransformOrigin = new Point(0.5, 0.5);

        var upX = MakeAnim(1.0, 1.04, _dur80, _easeQuadOut, null);
        var upY = MakeAnim(1.0, 1.04, _dur80, _easeQuadOut, null);
        var settleX = new DoubleAnimation(1.04, 1.0, _dur250) { EasingFunction = _easeSoftSpring };
        var settleY = new DoubleAnimation(1.04, 1.0, _dur250) { EasingFunction = _easeSoftSpring };

        upX.Completed += (_, _) => scale.BeginAnimation(ScaleTransform.ScaleXProperty, settleX);
        upY.Completed += (_, _) => scale.BeginAnimation(ScaleTransform.ScaleYProperty, settleY);

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, upX);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, upY);
    }

    private (ScaleTransform Scale, TranslateTransform Translate) PrepareAlarmPickerTransform(double scaleValue, double y)
    {
        var scale = new ScaleTransform(scaleValue, scaleValue);
        var translate = new TranslateTransform(0, y);
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(translate);
        AlarmPickerMode.RenderTransform = group;
        AlarmPickerMode.RenderTransformOrigin = new Point(0.5, 1.0);
        return (scale, translate);
    }

    #endregion

    #region Countdown Logic

    private void InitializeCountdownTimer()
    {
        _countdownTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _countdownTimer.Tick += CountdownTimer_Tick;
    }

    private void CountdownTimer_Tick(object? sender, EventArgs e)
    {
        _countdownRemaining = _countdownRemaining.Subtract(TimeSpan.FromMilliseconds(100));

        if (_countdownRemaining <= TimeSpan.Zero)
        {
            _countdownRemaining = TimeSpan.Zero;
            _isCountdownRunning = false;
            _countdownTimer?.Stop();
            CountdownStartText.Text = "▶";
            CountdownStartBtn.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x00));

            // Flash the display to indicate completion
            FlashCountdownComplete();

            // Play system notification sound
            SystemSounds.Exclamation.Play();
        }

        UpdateTimerDisplay();
    }

    private void FlashCountdownComplete()
    {
        var flash = new DoubleAnimation(1, 0.3, new Duration(TimeSpan.FromMilliseconds(300)))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(3)
        };
        CountdownDisplay.BeginAnimation(OpacityProperty, flash);
    }

    private void CountdownMinus_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        PlayTimerButtonPress(CountdownMinusBtn);
        if (_isCountdownRunning) return;

        if (_countdownDuration.TotalMinutes > 1)
        {
            _countdownDuration = _countdownDuration.Subtract(TimeSpan.FromMinutes(1));
            _countdownRemaining = _countdownDuration;
            UpdateTimerDisplay();
            AnimateCountdownDisplayPulse(1.02);
        }
    }

    private void CountdownPlus_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        PlayTimerButtonPress(CountdownPlusBtn);
        if (_isCountdownRunning) return;

        if (_countdownDuration.TotalMinutes < 99)
        {
            _countdownDuration = _countdownDuration.Add(TimeSpan.FromMinutes(1));
            _countdownRemaining = _countdownDuration;
            UpdateTimerDisplay();
            AnimateCountdownDisplayPulse(1.02);
        }
    }

    private void CountdownStart_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        PlayTimerButtonPress(CountdownStartBtn);

        if (_countdownTimer == null)
            InitializeCountdownTimer();

        if (_isCountdownRunning)
        {
            // Pause
            _isCountdownRunning = false;
            _countdownTimer?.Stop();
            CountdownStartText.Text = "▶";
            CountdownStartBtn.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x00));
            AnimateCountdownDisplayPulse(1.018);
        }
        else
        {
            if (_countdownRemaining <= TimeSpan.Zero)
            {
                _countdownRemaining = _countdownDuration;
            }

            // Start
            _isCountdownRunning = true;
            _countdownTimer?.Start();
            CountdownStartText.Text = "⏸";
            CountdownStartBtn.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x00));
            AnimateCountdownDisplayPulse(1.035);
        }
    }

    private void CountdownReset_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        PlayTimerButtonPress(CountdownResetBtn);
        _isCountdownRunning = false;
        _countdownTimer?.Stop();
        _countdownRemaining = _countdownDuration;
        CountdownStartText.Text = "▶";
        CountdownStartBtn.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x00));
        CountdownDisplay.BeginAnimation(OpacityProperty, null);
        CountdownDisplay.Opacity = 1;
        UpdateTimerDisplay();
        AnimateCountdownDisplayPulse(1.025);
    }

    private void UpdateTimerDisplay()
    {
        var minutes = (int)_countdownRemaining.TotalMinutes;
        var seconds = _countdownRemaining.Seconds;
        CountdownDisplay.Text = $"{minutes:D2}:{seconds:D2}";
    }

    #endregion

    #region Alarm Logic

    private bool _isAlarmPickerOpen = false;
    private bool _alarmPickerInitialized = false;
    private const int WheelItemHeight = 28;
    private const int WheelVisibleItems = 3; // show 3 items at a time (top, center, bottom)

    private void InitializeAlarmCheckTimer()
    {
        _alarmCheckTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _alarmCheckTimer.Tick += AlarmCheck_Tick;
    }

    private void AlarmCheck_Tick(object? sender, EventArgs e)
    {
        if (!_isAlarmSet) return;

        var now = DateTime.Now;
        if (now.Hour == _alarmHour && now.Minute == _alarmMinute && now.Second == 0)
        {
            // Alarm triggered!
            _isAlarmSet = false;
            _alarmCheckTimer?.Stop();
            AlarmSetText.Text = "OK";
            AlarmSetBtn.Background = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));

            // Flash alarm display
            var flash = new DoubleAnimation(1, 0.3, new Duration(TimeSpan.FromMilliseconds(300)))
            {
                AutoReverse = true,
                RepeatBehavior = new RepeatBehavior(5)
            };
            AlarmDisplay.BeginAnimation(OpacityProperty, flash);

            // Play system notification sound
            SystemSounds.Asterisk.Play();

            UpdateAlarmDisplay();
        }
    }

    private void InitializeAlarmWheelPicker()
    {
        if (_alarmPickerInitialized) return;
        _alarmPickerInitialized = true;

        // Populate hour items (00-23)
        AlarmHourItems.Children.Clear();
        // Add padding items at top
        AlarmHourItems.Children.Add(CreateWheelPadding());
        for (int i = 0; i < 24; i++)
        {
            AlarmHourItems.Children.Add(CreateWheelItem(i.ToString("D2"), i));
        }
        // Add padding items at bottom
        AlarmHourItems.Children.Add(CreateWheelPadding());

        // Populate minute items (00-59)
        AlarmMinuteItems.Children.Clear();
        // Add padding items at top
        AlarmMinuteItems.Children.Add(CreateWheelPadding());
        for (int i = 0; i < 60; i++)
        {
            AlarmMinuteItems.Children.Add(CreateWheelItem(i.ToString("D2"), i));
        }
        // Add padding items at bottom
        AlarmMinuteItems.Children.Add(CreateWheelPadding());
    }

    private FrameworkElement CreateWheelItem(string text, int value)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Height = WheelItemHeight,
            Padding = new Thickness(0, 4, 0, 4),
            Tag = value
        };
        tb.SetValue(FontFamilyProperty, FindResource("MainSystemFont") as FontFamily);
        return tb;
    }

    private FrameworkElement CreateWheelPadding()
    {
        return new Border { Height = WheelItemHeight, Background = Brushes.Transparent };
    }

    private void ScrollWheelToValue(ScrollViewer scroller, int value)
    {
        // value is 0-based index, offset by 1 for the top padding
        double offset = value * WheelItemHeight;
        scroller.ScrollToVerticalOffset(offset);
    }

    private int GetWheelSelectedValue(ScrollViewer scroller)
    {
        double offset = scroller.VerticalOffset;
        int index = (int)Math.Round(offset / WheelItemHeight);
        return Math.Max(0, index);
    }

    private void SnapWheelToNearest(ScrollViewer scroller, int maxValue)
    {
        double offset = scroller.VerticalOffset;
        int index = (int)Math.Round(offset / WheelItemHeight);
        index = Math.Max(0, Math.Min(index, maxValue - 1));
        double targetOffset = index * WheelItemHeight;

        // Smooth snap animation
        var anim = new DoubleAnimation(offset, targetOffset, new Duration(TimeSpan.FromMilliseconds(150)))
        {
            EasingFunction = _easeExpOut6
        };
        Timeline.SetDesiredFrameRate(anim, 120);

        // Use a timer to animate scroll position
        var startTime = DateTime.UtcNow;
        var duration = TimeSpan.FromMilliseconds(150);
        var startOffset = offset;
        var snapTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(8) };
        snapTimer.Tick += (s, ev) =>
        {
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            var progress = Math.Min(1.0, elapsed / duration.TotalMilliseconds);
            // Ease out cubic
            var eased = 1.0 - Math.Pow(1.0 - progress, 3);
            var current = startOffset + (targetOffset - startOffset) * eased;
            scroller.ScrollToVerticalOffset(current);

            if (progress >= 1.0)
            {
                snapTimer.Stop();
                scroller.ScrollToVerticalOffset(targetOffset);
                UpdateAlarmFromWheels();
            }
        };
        snapTimer.Start();
    }

    private void UpdateAlarmFromWheels()
    {
        _alarmHour = Math.Min(23, GetWheelSelectedValue(AlarmHourScroller));
        _alarmMinute = Math.Min(59, GetWheelSelectedValue(AlarmMinuteScroller));
        UpdateAlarmDisplay();
    }

    private void AlarmHourScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        var scroller = AlarmHourScroller;
        double newOffset = scroller.VerticalOffset - (e.Delta > 0 ? WheelItemHeight : -WheelItemHeight);
        newOffset = Math.Max(0, Math.Min(newOffset, (24 - 1) * WheelItemHeight));
        scroller.ScrollToVerticalOffset(newOffset);

        // Debounce snap
        SnapWheelToNearest(scroller, 24);
    }

    private void AlarmMinuteScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        var scroller = AlarmMinuteScroller;
        double newOffset = scroller.VerticalOffset - (e.Delta > 0 ? WheelItemHeight : -WheelItemHeight);
        newOffset = Math.Max(0, Math.Min(newOffset, (60 - 1) * WheelItemHeight));
        scroller.ScrollToVerticalOffset(newOffset);

        // Debounce snap
        SnapWheelToNearest(scroller, 60);
    }

    private void AlarmPickerBtn_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        PlayTimerButtonPress(AlarmPickerBtn);
        if (_isAlarmSet) return; // Can't change time while alarm is active

        if (!_isAlarmPickerOpen)
        {
            ShowAlarmPicker();
        }
        else
        {
            HideAlarmPicker();
        }
    }

    private void ShowAlarmPicker()
    {
        _isAlarmPickerOpen = true;
        InitializeAlarmWheelPicker();

        AlarmPickerBtnText.Text = "Done";
        AlarmPickerBtn.Background = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50));
        var (pickerScale, pickerTranslate) = PrepareAlarmPickerTransform(0.96, 8);

        // Animate display mode out, picker mode in
        var fadeOut = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(150)))
        {
            EasingFunction = _easeQuadIn
        };
        fadeOut.Completed += (s, ev) =>
        {
            AlarmDisplayMode.Visibility = Visibility.Collapsed;
            AlarmPickerMode.Visibility = Visibility.Visible;

            // Set scroll positions to current alarm values
            Dispatcher.BeginInvoke(() =>
            {
                ScrollWheelToValue(AlarmHourScroller, _alarmHour);
                ScrollWheelToValue(AlarmMinuteScroller, _alarmMinute);
            }, System.Windows.Threading.DispatcherPriority.Loaded);

            var fadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(200)))
            {
                EasingFunction = _easeExpOut6
            };
            var scaleInX = MakeAnim(0.96, 1.0, _dur250, _easeSoftSpring, null);
            var scaleInY = MakeAnim(0.96, 1.0, _dur250, _easeSoftSpring, null);
            var slideIn = MakeAnim(8.0, 0.0, _dur250, _easeExpOut6, null);
            AlarmPickerMode.BeginAnimation(OpacityProperty, fadeIn);
            pickerScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleInX);
            pickerScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleInY);
            pickerTranslate.BeginAnimation(TranslateTransform.YProperty, slideIn);
        };
        AlarmDisplayMode.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void HideAlarmPicker()
    {
        _isAlarmPickerOpen = false;

        // Read final values from wheels
        UpdateAlarmFromWheels();

        AlarmPickerBtnText.Text = "Set";
        AlarmPickerBtn.Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x0B));
        var (pickerScale, pickerTranslate) = PrepareAlarmPickerTransform(1.0, 0);

        // Animate picker mode out, display mode in
        var fadeOut = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(150)))
        {
            EasingFunction = _easeQuadIn
        };
        fadeOut.Completed += (s, ev) =>
        {
            AlarmPickerMode.Visibility = Visibility.Collapsed;
            AlarmDisplayMode.Visibility = Visibility.Visible;

            var fadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(200)))
            {
                EasingFunction = _easeExpOut6
            };
            AlarmDisplayMode.BeginAnimation(OpacityProperty, fadeIn);
        };
        AlarmPickerMode.BeginAnimation(OpacityProperty, fadeOut);
        pickerScale.BeginAnimation(ScaleTransform.ScaleXProperty, MakeAnim(1.0, 0.97, _dur150, _easeQuadIn, null));
        pickerScale.BeginAnimation(ScaleTransform.ScaleYProperty, MakeAnim(1.0, 0.97, _dur150, _easeQuadIn, null));
        pickerTranslate.BeginAnimation(TranslateTransform.YProperty, MakeAnim(0.0, 8.0, _dur150, _easeQuadIn, null));
    }

    private void AlarmSet_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        PlayTimerButtonPress(AlarmSetBtn);

        if (_alarmCheckTimer == null)
            InitializeAlarmCheckTimer();

        // If picker is open, close it first and read values
        if (_isAlarmPickerOpen)
        {
            UpdateAlarmFromWheels();
            HideAlarmPicker();
        }

        if (_isAlarmSet)
        {
            // Cancel alarm
            _isAlarmSet = false;
            _alarmCheckTimer?.Stop();
            AlarmSetText.Text = "OK";
            AlarmSetBtn.Background = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
            AlarmDisplay.BeginAnimation(OpacityProperty, null);
            AlarmDisplay.Opacity = 1;
        }
        else
        {
            // Set alarm
            _isAlarmSet = true;
            _alarmCheckTimer?.Start();
            AlarmSetText.Text = "Off";
            AlarmSetBtn.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
        }

        UpdateAlarmDisplay();
        AnimateAlarmDisplayPulse();
    }

    private void UpdateAlarmDisplay()
    {
        AlarmDisplay.Text = $"{_alarmHour:D2}:{_alarmMinute:D2}";
    }

    #endregion
}
