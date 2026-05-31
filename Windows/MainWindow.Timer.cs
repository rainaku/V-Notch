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
using static VNotch.Services.Win32Interop;

namespace VNotch;

public partial class MainWindow
{
    // ─── Timer View State ───
    private bool _isTimerView = false;
    private const double _timerViewHeight = 108;

    // ─── Countdown State ───
    private TimeSpan _countdownDuration = TimeSpan.FromMinutes(25);
    private TimeSpan _countdownRemaining = TimeSpan.FromMinutes(25);
    private bool _isCountdownRunning = false;
    private DispatcherTimer? _countdownTimer;

    // ─── Countdown Hold-to-Repeat ───
    private DispatcherTimer? _countdownRepeatTimer;
    private int _countdownRepeatDirection; // +1 or -1
    private int _countdownRepeatCount;
    private const int RepeatInitialDelayMs = 400;
    private const int RepeatFastIntervalMs = 80;
    private const int RepeatAccelerateAfter = 4; // ticks before speeding up



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

        // Shrink notch height for timer view
        double currentHeight = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _expandedHeight;
        NotchBorder.BeginAnimation(HeightProperty, null);
        NotchBorder.Height = currentHeight;
        var heightShrink = MakeAnim(currentHeight, _timerViewHeight, durIn, _easeExpOut6, inDelay);
        heightShrink.Completed += (s, ev) =>
        {
            NotchBorder.BeginAnimation(HeightProperty, null);
            NotchBorder.Height = _timerViewHeight;
        };
        NotchBorder.BeginAnimation(HeightProperty, heightShrink, HandoffBehavior.SnapshotAndReplace);

        // Resize window to fit
        double dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double windowHeightDip = _timerViewHeight + 80;
        this.Height = windowHeightDip;
        _windowHeight = (int)Math.Round(windowHeightDip * dpiScale);
        if (_hwnd != IntPtr.Zero)
            SetWindowPos(_hwnd, HWND_TOPMOST, _fixedX, _fixedY, _windowWidth, _windowHeight, SWP_NOACTIVATE);

        UpdateTimerDisplay();
    }

    private void SwitchFromSecondaryToTimerView()
    {
        if (_isTimerView || _isAnimating) return;
        _isTimerView = true;
        _isSecondaryView = false;
        _isAnimating = true;
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;

        // Stop camera if still active (e.g. called from nav button without scroll)
        if (_isCameraActive)
        {
            StopCameraPreviewSafe();
        }
        ResetCameraSectionLayoutInstant();

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

        // Shrink notch height for timer view
        double currentHeight2 = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _expandedHeight;
        NotchBorder.BeginAnimation(HeightProperty, null);
        NotchBorder.Height = currentHeight2;
        var heightShrink2 = MakeAnim(currentHeight2, _timerViewHeight, durIn, _easeExpOut6, inDelay);
        heightShrink2.Completed += (s, ev) =>
        {
            NotchBorder.BeginAnimation(HeightProperty, null);
            NotchBorder.Height = _timerViewHeight;
        };
        NotchBorder.BeginAnimation(HeightProperty, heightShrink2, HandoffBehavior.SnapshotAndReplace);

        // Resize window to fit
        double dpiScale2 = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double windowHeightDip2 = _timerViewHeight + 80;
        this.Height = windowHeightDip2;
        _windowHeight = (int)Math.Round(windowHeightDip2 * dpiScale2);
        if (_hwnd != IntPtr.Zero)
            SetWindowPos(_hwnd, HWND_TOPMOST, _fixedX, _fixedY, _windowWidth, _windowHeight, SWP_NOACTIVATE);

        UpdateTimerDisplay();
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

        // Restore notch height back to expanded
        double currentH = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _timerViewHeight;
        NotchBorder.BeginAnimation(HeightProperty, null);
        NotchBorder.Height = currentH;
        var heightGrow = MakeAnim(currentH, _expandedHeight, durIn, _easeExpOut6, inDelay);
        heightGrow.Completed += (s, ev) =>
        {
            NotchBorder.BeginAnimation(HeightProperty, null);
            NotchBorder.Height = _expandedHeight;
        };
        NotchBorder.BeginAnimation(HeightProperty, heightGrow, HandoffBehavior.SnapshotAndReplace);

        // Resize window back
        double dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double windowHeightDip = _expandedHeight + 80;
        this.Height = windowHeightDip;
        _windowHeight = (int)Math.Round(windowHeightDip * dpiScale);
        if (_hwnd != IntPtr.Zero)
            SetWindowPos(_hwnd, HWND_TOPMOST, _fixedX, _fixedY, _windowWidth, _windowHeight, SWP_NOACTIVATE);
    }

    private void UpdateTimerNavIconsState()
    {
        HomeIconButton.Opacity = 0.4;
        FileShelfIconButton.Opacity = 0.4;
        TimerIconButton.Opacity = 1.0;
        ShelfCountBadge.Visibility = Visibility.Collapsed;
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

        // Restore notch height back to expanded
        double currentH2 = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _timerViewHeight;
        NotchBorder.BeginAnimation(HeightProperty, null);
        NotchBorder.Height = currentH2;
        var heightGrow2 = MakeAnim(currentH2, _expandedHeight, durIn, _easeExpOut6, inDelay);
        heightGrow2.Completed += (s, ev) =>
        {
            NotchBorder.BeginAnimation(HeightProperty, null);
            NotchBorder.Height = _expandedHeight;
        };
        NotchBorder.BeginAnimation(HeightProperty, heightGrow2, HandoffBehavior.SnapshotAndReplace);

        // Resize window back
        double dpiScale3 = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double windowHeightDip3 = _expandedHeight + 80;
        this.Height = windowHeightDip3;
        _windowHeight = (int)Math.Round(windowHeightDip3 * dpiScale3);
        if (_hwnd != IntPtr.Zero)
            SetWindowPos(_hwnd, HWND_TOPMOST, _fixedX, _fixedY, _windowWidth, _windowHeight, SWP_NOACTIVATE);

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

            // Play system notification sound
            SystemSounds.Exclamation.Play();

            // Collapse to pill and show completion overlay
            ShowCountdownCompletionOnPill();
            return;
        }

        UpdateTimerDisplay();
    }

    private bool _isCountdownCompleteVisible = false;

    private void ShowCountdownCompletionOnPill()
    {
        _isCountdownCompleteVisible = true;

        // If not expanded, expand to timer view size first
        if (!_isExpanded)
        {
            // Expand notch to timer view size
            _isExpanded = true;
            _isTimerView = true;
            _isAnimating = true;

            NotchBorder.IsHitTestVisible = false;

            var durExpand = new Duration(TimeSpan.FromMilliseconds(480));
            var widthAnim = MakeAnim(_expandedWidth, durExpand, _easeExpOut6, 144);
            var heightAnim = MakeAnim(_timerViewHeight, durExpand, _easeExpOut6, 144);

            heightAnim.Completed += (s, ev) =>
            {
                _isAnimating = false;
                NotchBorder.IsHitTestVisible = true;
                ShowCompletionOverlayContent();
            };

            NotchBorder.BeginAnimation(WidthProperty, widthAnim);
            NotchBorder.BeginAnimation(HeightProperty, heightAnim);

            // Resize window
            double dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double windowHeightDip = _timerViewHeight + 80;
            this.Height = windowHeightDip;
            _windowHeight = (int)Math.Round(windowHeightDip * dpiScale);
            if (_hwnd != IntPtr.Zero)
                SetWindowPos(_hwnd, HWND_TOPMOST, _fixedX, _fixedY, _windowWidth, _windowHeight, SWP_NOACTIVATE);

            // Hide collapsed content during expand
            CollapsedContent.Visibility = Visibility.Collapsed;
            MusicCompactContent.Visibility = Visibility.Collapsed;
        }
        else if (_isExpanded && _isTimerView)
        {
            // Already in timer view, just hide timer content and show overlay
            TimerContent.BeginAnimation(OpacityProperty, null);
            TimerContent.Opacity = 0;
            TimerContent.Visibility = Visibility.Collapsed;

            NavIconsBackground.BeginAnimation(OpacityProperty, null);
            NavIconsBackground.Opacity = 0;
            NavIconsBackground.Visibility = Visibility.Collapsed;
            NavIconsPanel.BeginAnimation(OpacityProperty, null);
            NavIconsPanel.Opacity = 0;
            NavIconsPanel.Visibility = Visibility.Collapsed;

            ShowCompletionOverlayContent();
        }
        else
        {
            // Expanded but not in timer view — just show overlay
            ShowCompletionOverlayContent();
        }
    }

    private void ShowCompletionOverlayContent()
    {
        // Hide normal content
        ExpandedContent.Visibility = Visibility.Collapsed;
        TimerContent.Visibility = Visibility.Collapsed;
        SecondaryContent.Visibility = Visibility.Collapsed;

        // Show completion overlay
        CountdownCompleteOverlay.Visibility = Visibility.Visible;
        CountdownCompleteOverlay.Opacity = 1;

        // Flash the 00:00 text
        var flash = new DoubleAnimation(1, 0.2, new Duration(TimeSpan.FromMilliseconds(500)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        Timeline.SetDesiredFrameRate(flash, 30);
        CountdownCompleteText.BeginAnimation(OpacityProperty, flash);
    }

    private void CountdownRestart_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        DismissCountdownCompletion();

        // Reset and start again in timer view
        _countdownRemaining = _countdownDuration;
        _isCountdownRunning = true;
        if (_countdownTimer == null) InitializeCountdownTimer();
        _countdownTimer?.Start();

        // Switch to timer view
        _isTimerView = true;
        TimerContent.Visibility = Visibility.Visible;
        TimerContent.Opacity = 1;
        NavIconsPanel.Visibility = Visibility.Visible;
        NavIconsPanel.Opacity = 1;
        NavIconsBackground.Visibility = Visibility.Visible;
        NavIconsBackground.Opacity = 1;
        UpdateTimerNavIconsState();
        UpdateTimerDisplay();

        CountdownStartIcon.Data = System.Windows.Media.Geometry.Parse("M224,320a16,16,0,0,1-32,0V192a16,16,0,0,1,32,0Zm96,0a16,16,0,0,1-32,0V192a16,16,0,0,1,32,0Z");
        CountdownStartBtn.Background = new SolidColorBrush(Color.FromRgb(0xCC, 0x70, 0x00));
    }

    private void CountdownDismiss_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        DismissCountdownCompletion();

        // Reset timer state and collapse
        _countdownRemaining = _countdownDuration;
        _isTimerView = false;

        // Collapse back to pill
        _isAnimating = true;
        var durCollapse = new Duration(TimeSpan.FromMilliseconds(400));
        var widthAnim = MakeAnim(_collapsedWidth, durCollapse, _easeExpOut6, 144);
        var heightAnim = MakeAnim(_collapsedHeight, durCollapse, _easeExpOut6, 144);

        heightAnim.Completed += (s, ev) =>
        {
            _isAnimating = false;
            _isExpanded = false;
            NotchBorder.IsHitTestVisible = true;

            // Restore normal collapsed content
            if (_isMusicCompactMode)
            {
                MusicCompactContent.Visibility = Visibility.Visible;
                MusicCompactContent.Opacity = 1;
            }
            else
            {
                CollapsedContent.Visibility = Visibility.Visible;
                CollapsedContent.Opacity = 1;
            }
        };

        NotchBorder.BeginAnimation(WidthProperty, widthAnim);
        NotchBorder.BeginAnimation(HeightProperty, heightAnim);

        // Reset play icon
        CountdownStartIcon.Data = System.Windows.Media.Geometry.Parse("M133,440a35.37,35.37,0,0,1-17.5-4.67c-12-6.8-17.46-20-17.46-41.73V118.4c0-21.74,5.48-34.93,17.46-41.73a35.13,35.13,0,0,1,35.77.45L399.68,225.11a38.19,38.19,0,0,1,0,61.78L151.23,435a35.77,35.77,0,0,1-18.27,5Z");
        CountdownStartBtn.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00));
    }

    private void DismissCountdownCompletion()
    {
        _isCountdownCompleteVisible = false;

        // Stop flashing
        CountdownCompleteText.BeginAnimation(OpacityProperty, null);
        CountdownCompleteText.Opacity = 1;

        // Hide overlay
        CountdownCompleteOverlay.Visibility = Visibility.Collapsed;
        CountdownCompleteOverlay.Opacity = 0;
    }

    private void CountdownMinus_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        PlayTimerButtonPress(CountdownMinusBtn);
        if (_isCountdownRunning) return;

        ApplyCountdownStep(-1);
        StartCountdownRepeat(-1);
    }

    private void CountdownPlus_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        PlayTimerButtonPress(CountdownPlusBtn);
        if (_isCountdownRunning) return;

        ApplyCountdownStep(+1);
        StartCountdownRepeat(+1);
    }

    private void ApplyCountdownStep(int direction)
    {
        if (direction > 0 && _countdownDuration.TotalMinutes < 99)
        {
            _countdownDuration = _countdownDuration.Add(TimeSpan.FromMinutes(1));
            _countdownRemaining = _countdownDuration;
            UpdateTimerDisplay();
            AnimateCountdownDisplayPulse(1.02);
        }
        else if (direction < 0 && _countdownDuration.TotalMinutes > 1)
        {
            _countdownDuration = _countdownDuration.Subtract(TimeSpan.FromMinutes(1));
            _countdownRemaining = _countdownDuration;
            UpdateTimerDisplay();
            AnimateCountdownDisplayPulse(1.02);
        }
    }

    private void StartCountdownRepeat(int direction)
    {
        StopCountdownRepeat();
        _countdownRepeatDirection = direction;
        _countdownRepeatCount = 0;
        _countdownRepeatTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(RepeatInitialDelayMs)
        };
        _countdownRepeatTimer.Tick += CountdownRepeat_Tick;
        _countdownRepeatTimer.Start();
    }

    private void CountdownRepeat_Tick(object? sender, EventArgs e)
    {
        if (_isCountdownRunning)
        {
            StopCountdownRepeat();
            return;
        }

        _countdownRepeatCount++;
        ApplyCountdownStep(_countdownRepeatDirection);

        // Accelerate: after a few ticks, switch to fast interval
        if (_countdownRepeatCount == RepeatAccelerateAfter && _countdownRepeatTimer != null)
        {
            _countdownRepeatTimer.Interval = TimeSpan.FromMilliseconds(RepeatFastIntervalMs);
        }
    }

    private void StopCountdownRepeat()
    {
        if (_countdownRepeatTimer != null)
        {
            _countdownRepeatTimer.Stop();
            _countdownRepeatTimer.Tick -= CountdownRepeat_Tick;
            _countdownRepeatTimer = null;
        }
    }

    private void CountdownBtn_MouseLeaveOrUp(object sender, EventArgs e)
    {
        StopCountdownRepeat();
        // Also run the normal hover-leave visual effect
        if (sender is Border button)
        {
            button.Background = new SolidColorBrush(
                button == CountdownPlusBtn ? (Color)ColorConverter.ConvertFromString("#22FFFFFF")
                                           : (Color)ColorConverter.ConvertFromString("#16FFFFFF"));
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
            CountdownStartIcon.Data = System.Windows.Media.Geometry.Parse("M133,440a35.37,35.37,0,0,1-17.5-4.67c-12-6.8-17.46-20-17.46-41.73V118.4c0-21.74,5.48-34.93,17.46-41.73a35.13,35.13,0,0,1,35.77.45L399.68,225.11a38.19,38.19,0,0,1,0,61.78L151.23,435a35.77,35.77,0,0,1-18.27,5Z");
            CountdownStartBtn.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00));
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
            CountdownStartIcon.Data = System.Windows.Media.Geometry.Parse("M224,320a16,16,0,0,1-32,0V192a16,16,0,0,1,32,0Zm96,0a16,16,0,0,1-32,0V192a16,16,0,0,1,32,0Z");
            CountdownStartBtn.Background = new SolidColorBrush(Color.FromRgb(0xCC, 0x70, 0x00));
        }
    }

    private void CountdownReset_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        PlayTimerButtonPress(CountdownResetBtn);
        _isCountdownRunning = false;
        _countdownTimer?.Stop();
        _countdownRemaining = _countdownDuration;
        CountdownStartIcon.Data = System.Windows.Media.Geometry.Parse("M133,440a35.37,35.37,0,0,1-17.5-4.67c-12-6.8-17.46-20-17.46-41.73V118.4c0-21.74,5.48-34.93,17.46-41.73a35.13,35.13,0,0,1,35.77.45L399.68,225.11a38.19,38.19,0,0,1,0,61.78L151.23,435a35.77,35.77,0,0,1-18.27,5Z");
        CountdownStartBtn.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00));
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
        UpdateCountdownProgressFill();
    }

    private void UpdateCountdownProgressFill()
    {
        double totalMs = Math.Max(1.0, _countdownDuration.TotalMilliseconds);
        double remainingMs = Math.Clamp(_countdownRemaining.TotalMilliseconds, 0.0, totalMs);
        double progress = 1.0 - (remainingMs / totalMs);

        double availableWidth = CountdownDisplayPanel.ActualWidth;
        if (availableWidth <= 0)
        {
            availableWidth = CountdownDisplayPanel.MinWidth;
        }

        CountdownProgressFill.Width = Math.Max(0, availableWidth * progress);
        CountdownProgressEdge.Opacity = progress > 0.001 ? 1.0 : 0.0;
    }

    #endregion
}
