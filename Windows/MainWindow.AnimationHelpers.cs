using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using VNotch.Controls;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;
public partial class MainWindow
{
    #region Hover Animations

    private void AnimateNotchHover(bool isHovered)
    {
        if (_isExpanded || _isAnimating) return;

        double targetScale = isHovered ? 1.08 : 1.0;
        var duration = isHovered ? _dur500 : _dur350;
        var easing = isHovered ? (IEasingFunction)_easeSoftSpring : _easeQuadOut;

        var animX = MakeAnim(targetScale, duration, easing);
        NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
    }

    private void AnimateThumbnailHover(bool isHovered)
    {
        if (_isExpanded || _isAnimating) return;

        double thumbScale = isHovered ? 1.5 : 1.0;
        double notchWidth = isHovered ? _collapsedWidth + 32 : _collapsedWidth;
        double notchHeight = isHovered ? _collapsedHeight + 36 : _collapsedHeight;
        double infoOpacity = isHovered ? 1 : 0;
        
        var duration = isHovered ? _dur500 : _dur350;
        var easing = isHovered ? (IEasingFunction)_easeThumbSpring : _easeExpOut6;
        var animFps = 144;

        var widthAnim = MakeAnim(notchWidth, duration, isHovered ? _easeExpOut6 : _easeQuadOut, animFps);
        var heightAnim = MakeAnim(notchHeight, duration, isHovered ? _easeExpOut6 : _easeQuadOut, animFps);
        VNotch.Services.RuntimeLog.Log("NOTCH-WIDTH",
            $"AnimateNotchHover -> {notchWidth} (hover={isHovered}, _isExpanded={_isExpanded}, _isAnimating={_isAnimating})");
        NotchBorder.BeginAnimation(WidthProperty, widthAnim);
        NotchBorder.BeginAnimation(HeightProperty, heightAnim);

        var thumbScaleAnimX = MakeAnim(thumbScale, duration, easing, animFps);
        var thumbScaleAnimY = MakeAnim(thumbScale, duration, easing, animFps);
        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, thumbScaleAnimX);
        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, thumbScaleAnimY);

        if (isHovered)
        {
            CompactHoverInfo.Visibility = Visibility.Visible;
            UpdateCompactMarquee();
            widthAnim.Completed += (s, e) =>
            {
                if (!_isExpanded && !_isAnimating && _isMusicCompactMode)
                {
                    UpdateCompactMarquee();
                }
            };
        }

        var fadeAnim = MakeAnim(infoOpacity, isHovered ? _dur200 : _dur100, _easeQuadOut);
        if (!isHovered)
        {
            fadeAnim.Completed += (s, e) => { if (CompactHoverInfo.Opacity < 0.1) CompactHoverInfo.Visibility = Visibility.Collapsed; };
        }
        CompactHoverInfo.BeginAnimation(OpacityProperty, fadeAnim);

        double radius = isHovered ? 24 : _cornerRadiusCollapsed;
        AnimateCornerRadius(radius, duration.TimeSpan);

        // Animate compact thumbnail corner radius - reduce when scaled up to avoid looking too round
        double thumbRadius = isHovered ? 2.5 : 6;
        double startThumbRadius = CompactThumbnailBorder.CornerRadius.TopLeft;
        if (Math.Abs(thumbRadius - startThumbRadius) > 0.1)
        {
            CurrentCompactThumbnailRadius = startThumbRadius;
            var thumbRadiusAnim = MakeAnim(startThumbRadius, thumbRadius, duration, easing);
            this.BeginAnimation(CurrentCompactThumbnailRadiusProperty, thumbRadiusAnim);
        }
    }

    private void UpdateCompactMarquee()
    {
        if (_currentMediaInfo == null) return;
        
        CompactTitleMarquee.Text = _currentMediaInfo.CurrentTrack;
        CompactTitleMarquee.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        
        double textWidth = CompactTitleMarquee.DesiredSize.Width;

        double containerWidth = Math.Max(0, ((NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : NotchBorder.Width) + 32) - 12);

        const double marqueeTriggerOverflow = 10.0;

        if (textWidth > containerWidth + marqueeTriggerOverflow && containerWidth > 0)
        {
            
            CompactHoverInfo.OpacityMask = CompactMarqueeFadeBrush;
            MarqueeController.StartMarqueeAnimation(CompactTitleMarqueeTranslate, textWidth - containerWidth + 12);
        }
        else
        {
            
            CompactHoverInfo.OpacityMask = null;
            
            CompactTitleMarqueeTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            CompactTitleMarqueeTranslate.X = Math.Max(0, (containerWidth - textWidth) / 2);
        }
    }

    #endregion

    #region Animation Helpers

    private void FadeSwitch(FrameworkElement from, FrameworkElement to)
    {

        from.BeginAnimation(OpacityProperty, null);
        to.BeginAnimation(OpacityProperty, null);

        var fadeOut = MakeAnim(0, _dur100);
        fadeOut.Completed += (s, e) =>
        {
            if (from.Opacity < 0.05) from.Visibility = Visibility.Collapsed;
        };
        from.BeginAnimation(OpacityProperty, fadeOut);

        to.Visibility = Visibility.Visible;
        var fadeIn = MakeAnim(1, _dur200);
        to.BeginAnimation(OpacityProperty, fadeIn);
    }

    private void AnimateButtonScale(ScaleTransform scaleTransform, double targetScale)
    {
        var animX = new DoubleAnimation(scaleTransform.ScaleX, targetScale, _dur150) { EasingFunction = _easeQuadOut };
        var animY = new DoubleAnimation(scaleTransform.ScaleY, targetScale, _dur150) { EasingFunction = _easeQuadOut };

        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
    }

    private void AnimateIconSwitch(Canvas fromIcon, Canvas toIcon, TimeSpan duration, EasingFunctionBase easing)
    {

        var fromTransform = fromIcon.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        fromIcon.RenderTransform = fromTransform;
        fromTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        fromTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        fromIcon.BeginAnimation(OpacityProperty, null);

        var toTransform = toIcon.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        toIcon.RenderTransform = toTransform;
        toTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        toTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        toIcon.BeginAnimation(OpacityProperty, null);

        fromIcon.Visibility = Visibility.Visible;
        fromTransform.ScaleX = 1;
        fromTransform.ScaleY = 1;
        fromIcon.Opacity = 1;

        toIcon.Visibility = Visibility.Visible;
        toTransform.ScaleX = 0.3;
        toTransform.ScaleY = 0.3;
        toIcon.Opacity = 0;

        var dur = new Duration(duration);
        var scaleDown = new DoubleAnimation(1, 0.3, dur) { EasingFunction = easing };
        var fadeOut = new DoubleAnimation(1, 0, dur) { EasingFunction = easing };
        var scaleUp = new DoubleAnimation(0.3, 1, dur) { EasingFunction = easing };
        var fadeIn = new DoubleAnimation(0, 1, dur) { EasingFunction = easing };

        var capturedFromIcon = fromIcon;
        var capturedFromTransform = fromTransform;

        fadeOut.Completed += (s, e) =>
        {

            capturedFromTransform.ScaleX = 1;
            capturedFromTransform.ScaleY = 1;
            capturedFromIcon.Opacity = 1;

            capturedFromIcon.Visibility = Visibility.Collapsed;
            capturedFromTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            capturedFromTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            capturedFromIcon.BeginAnimation(OpacityProperty, null);
        };

        fromTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDown);
        fromTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDown);
        fromIcon.BeginAnimation(OpacityProperty, fadeOut);

        toTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleUp);
        toTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleUp);
        toIcon.BeginAnimation(OpacityProperty, fadeIn);
    }

    private void PlayButtonPressAnimation(Border button)
    {
        var transform = button.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        button.RenderTransform = transform;
        button.RenderTransformOrigin = new Point(0.5, 0.5);

        // Cancel any in-progress animations
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        // Phase 1: Quick squish down (haptic press feel)
        var squish = MakeAnim(1d, 0.82d, _dur80, _easeQuadIn, null);

        // Phase 2: Spring bounce back with overshoot (haptic release)
        var bounce = new DoubleAnimation(0.82, 1.0, _dur250)
        {
            EasingFunction = _easeHapticBounce,
            BeginTime = TimeSpan.Zero
        };
        Timeline.SetDesiredFrameRate(bounce, 120);

        squish.Completed += (s, e) =>
        {
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
        };

        transform.BeginAnimation(ScaleTransform.ScaleXProperty, squish);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, squish);
    }

    private void PlayNextSkipAnimation()
    {
        PlayNextSkipAnimation(NextArrow0, NextArrow1, NextArrow2);
    }

    private void PlayNextSkipAnimation(System.Windows.Shapes.Path arrow0, System.Windows.Shapes.Path arrow1, System.Windows.Shapes.Path arrow2)
    {
        var arrow2Transform = arrow2.RenderTransform as TranslateTransform ?? new TranslateTransform();
        arrow2.RenderTransform = arrow2Transform;

        var slideOut2 = new DoubleAnimation(0, 8, _dur250) { EasingFunction = _easeQuadOut };
        var fadeOut2 = new DoubleAnimation(1, 0, _dur250) { EasingFunction = _easeQuadOut };

        var arrow1Transform = arrow1.RenderTransform as TranslateTransform ?? new TranslateTransform();
        arrow1.RenderTransform = arrow1Transform;

        var slideRight1 = new DoubleAnimation(0, 8, _dur250) { EasingFunction = _easeQuadOut };

        var arrow0Transform = arrow0.RenderTransform as TranslateTransform ?? new TranslateTransform();
        arrow0.RenderTransform = arrow0Transform;

        var slideIn0 = new DoubleAnimation(0, 8, _dur250) { EasingFunction = _easeQuadOut };
        var fadeIn0 = new DoubleAnimation(0, 1, _dur250) { EasingFunction = _easeQuadOut };

        arrow2Transform.BeginAnimation(TranslateTransform.XProperty, slideOut2);
        arrow2.BeginAnimation(OpacityProperty, fadeOut2);
        arrow1Transform.BeginAnimation(TranslateTransform.XProperty, slideRight1);
        arrow0Transform.BeginAnimation(TranslateTransform.XProperty, slideIn0);
        arrow0.BeginAnimation(OpacityProperty, fadeIn0);

        fadeOut2.Completed += (s, e) =>
        {
            arrow2Transform.X = 0;
            arrow2.Opacity = 1;
            arrow1Transform.X = 0;
            arrow0Transform.X = 0;
            arrow0.Opacity = 0;

            arrow2Transform.BeginAnimation(TranslateTransform.XProperty, null);
            arrow2.BeginAnimation(OpacityProperty, null);
            arrow1Transform.BeginAnimation(TranslateTransform.XProperty, null);
            arrow0Transform.BeginAnimation(TranslateTransform.XProperty, null);
            arrow0.BeginAnimation(OpacityProperty, null);
        };
    }

    private void PlayPrevSkipAnimation()
    {
        PlayPrevSkipAnimation(PrevArrow0, PrevArrow1, PrevArrow2);
    }

    private void PlayPrevSkipAnimation(System.Windows.Shapes.Path arrow0, System.Windows.Shapes.Path arrow1, System.Windows.Shapes.Path arrow2)
    {
        var arrow1Transform = arrow1.RenderTransform as TranslateTransform ?? new TranslateTransform();
        arrow1.RenderTransform = arrow1Transform;

        var slideOut1 = new DoubleAnimation(0, -8, _dur250) { EasingFunction = _easeQuadOut };
        var fadeOut1 = new DoubleAnimation(1, 0, _dur250) { EasingFunction = _easeQuadOut };

        var arrow2Transform = arrow2.RenderTransform as TranslateTransform ?? new TranslateTransform();
        arrow2.RenderTransform = arrow2Transform;

        var slideLeft2 = new DoubleAnimation(0, -8, _dur250) { EasingFunction = _easeQuadOut };

        var arrow0Transform = arrow0.RenderTransform as TranslateTransform ?? new TranslateTransform();
        arrow0.RenderTransform = arrow0Transform;

        var slideIn0 = new DoubleAnimation(0, -8, _dur250) { EasingFunction = _easeQuadOut };
        var fadeIn0 = new DoubleAnimation(0, 1, _dur250) { EasingFunction = _easeQuadOut };

        arrow1Transform.BeginAnimation(TranslateTransform.XProperty, slideOut1);
        arrow1.BeginAnimation(OpacityProperty, fadeOut1);
        arrow2Transform.BeginAnimation(TranslateTransform.XProperty, slideLeft2);
        arrow0Transform.BeginAnimation(TranslateTransform.XProperty, slideIn0);
        arrow0.BeginAnimation(OpacityProperty, fadeIn0);

        fadeOut1.Completed += (s, e) =>
        {
            arrow1Transform.X = 0;
            arrow1.Opacity = 1;
            arrow2Transform.X = 0;
            arrow0Transform.X = 0;
            arrow0.Opacity = 0;

            arrow1Transform.BeginAnimation(TranslateTransform.XProperty, null);
            arrow1.BeginAnimation(OpacityProperty, null);
            arrow2Transform.BeginAnimation(TranslateTransform.XProperty, null);
            arrow0Transform.BeginAnimation(TranslateTransform.XProperty, null);
            arrow0.BeginAnimation(OpacityProperty, null);
        };
    }

    private void PlayAppearAnimation()
    {
        NotchBorder.Opacity = 0;

        var opacityAnim = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(400)),
            EasingFunction = _easeQuadOut
        };

        NotchBorder.BeginAnimation(OpacityProperty, opacityAnim);
    }

    public static readonly DependencyProperty CurrentCornerRadiusProperty =
        DependencyProperty.Register("CurrentCornerRadius", typeof(double), typeof(MainWindow),
            new PropertyMetadata(0.0, OnCurrentCornerRadiusChanged));

    public static readonly DependencyProperty CurrentThumbnailAnimationRadiusProperty =
        DependencyProperty.Register("CurrentThumbnailAnimationRadius", typeof(double), typeof(MainWindow),
            new PropertyMetadata(6.0, OnCurrentThumbnailAnimationRadiusChanged));

    public static readonly DependencyProperty CurrentCompactThumbnailRadiusProperty =
        DependencyProperty.Register("CurrentCompactThumbnailRadius", typeof(double), typeof(MainWindow),
            new PropertyMetadata(6.0, OnCurrentCompactThumbnailRadiusChanged));

    public double CurrentCornerRadius
    {
        get => (double)GetValue(CurrentCornerRadiusProperty);
        set => SetValue(CurrentCornerRadiusProperty, value);
    }

    public double CurrentThumbnailAnimationRadius
    {
        get => (double)GetValue(CurrentThumbnailAnimationRadiusProperty);
        set => SetValue(CurrentThumbnailAnimationRadiusProperty, value);
    }

    public double CurrentCompactThumbnailRadius
    {
        get => (double)GetValue(CurrentCompactThumbnailRadiusProperty);
        set => SetValue(CurrentCompactThumbnailRadiusProperty, value);
    }

    private static void OnCurrentCornerRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MainWindow window)
        {
            double radius = (double)e.NewValue;
            var cr = new CornerRadius(0, 0, radius, radius);
            window.NotchBorder.CornerRadius = cr;
            window.InnerClipBorder.CornerRadius = cr;
            window.MediaBackground.CornerRadius = cr;
            window.MediaBackground2.CornerRadius = cr;
            window.NotchBorderShadow.CornerRadius = cr;
            window.UpdateNotchClip();
        }
    }

    private static void OnCurrentThumbnailAnimationRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MainWindow window)
        {
            return;
        }

        double radius = (double)e.NewValue;
        var cornerRadius = new CornerRadius(radius);

        if (window.AnimationThumbnailBorder != null)
        {
            window.AnimationThumbnailBorder.CornerRadius = cornerRadius;
        }

        if (window.AnimationThumbnailClip != null)
        {
            window.AnimationThumbnailClip.RadiusX = radius;
            window.AnimationThumbnailClip.RadiusY = radius;
        }
    }

    private static void OnCurrentCompactThumbnailRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MainWindow window) return;

        double radius = (double)e.NewValue;
        window.CompactThumbnailBorder.CornerRadius = new CornerRadius(radius);
    }

    private void AnimateCornerRadius(double targetRadius, TimeSpan duration)
    {
        // Cancel any in-progress corner radius animation to prevent jitter from conflicting animations (e
        this.BeginAnimation(CurrentCornerRadiusProperty, null);

        double startRadius = NotchBorder.CornerRadius.BottomLeft;
        
        if (Math.Abs(targetRadius - startRadius) < 0.5) return;

        CurrentCornerRadius = startRadius;

        var anim = MakeAnim(startRadius, targetRadius, new Duration(duration), _easeExpOut6, null);
        Timeline.SetDesiredFrameRate(anim, 144);
        this.BeginAnimation(CurrentCornerRadiusProperty, anim);
    }

    private void AnimateThumbnailAnimationRadius(double fromRadius, double toRadius, Duration duration, IEasingFunction easing, TimeSpan? beginTime = null)
    {
        CurrentThumbnailAnimationRadius = fromRadius;
        this.BeginAnimation(CurrentThumbnailAnimationRadiusProperty, null);

        var anim = MakeAnim(fromRadius, toRadius, duration, easing, beginTime);
        this.BeginAnimation(CurrentThumbnailAnimationRadiusProperty, anim);
    }

    public void PlayTrackChangeBounce()
    {
        if (_isExpanded || _isAnimating) return;

        var durPeak = TimeSpan.FromMilliseconds(150);
        var durEnd = TimeSpan.FromMilliseconds(800);

        var bounceX = new DoubleAnimationUsingKeyFrames();
        bounceX.KeyFrames.Add(new EasingDoubleKeyFrame(1.12, KeyTime.FromTimeSpan(durPeak), _easeQuadOut));
        bounceX.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(durEnd), _easeSoftSpring));
        Timeline.SetDesiredFrameRate(bounceX, 144);

        var bounceY = new DoubleAnimationUsingKeyFrames();
        bounceY.KeyFrames.Add(new EasingDoubleKeyFrame(0.92, KeyTime.FromTimeSpan(durPeak), _easeQuadOut));
        bounceY.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(durEnd), _easeSoftSpring));
        Timeline.SetDesiredFrameRate(bounceY, 144);

        NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounceX);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounceY);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounceX);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounceY);
    }

    private BlurEffect? _mediaControlsHoverBlur;
    private TransformGroup? _currentTimeHoverTransform;
    private TransformGroup? _remainingTimeHoverTransform;
    private ScaleTransform? _currentTimeHoverScale;
    private ScaleTransform? _remainingTimeHoverScale;
    private TranslateTransform? _currentTimeHoverTranslate;
    private TranslateTransform? _remainingTimeHoverTranslate;

    private void AnimateProgressBarHover(bool isHovered)
    {
        double scaleX = isHovered ? 1.04 : 1.0;
        double blurRadius = isHovered ? 4.0 : 0.0;
        double surroundOpacity = isHovered ? 0.45 : 1.0;
        double timeScale = isHovered ? 1.22 : 1.0;
        double timeTranslateY = isHovered ? 3.0 : 0.0;
        
        var duration = TimeSpan.FromMilliseconds(isHovered ? 350 : 250);
        var easing = (IEasingFunction)(isHovered
            ? new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut }
            : new CubicEase { EasingMode = EasingMode.EaseOut });
        int fps = 144;

        // Progress bar — animate height directly (preserves corner radius proportions)
        ProgressBarContainer.BeginAnimation(MarginProperty, null);
        double barHeight = isHovered ? 10 : 4;
        var heightAnim = new DoubleAnimation { To = barHeight, Duration = duration, EasingFunction = easing };
        var scaleXAnim = new DoubleAnimation { To = scaleX, Duration = duration, EasingFunction = easing };
        Timeline.SetDesiredFrameRate(heightAnim, fps);
        Timeline.SetDesiredFrameRate(scaleXAnim, fps);
        ProgressBarBg.BeginAnimation(HeightProperty, heightAnim);
        ProgressBar.BeginAnimation(HeightProperty, heightAnim);
        ProgressBarMainScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);

        // Animate clip radius to keep rounded ends (half of height)
        double barRadius = barHeight / 2.0;
        var clipRadiusAnim = new DoubleAnimation { To = barRadius, Duration = duration, EasingFunction = easing };
        Timeline.SetDesiredFrameRate(clipRadiusAnim, fps);
        ProgressBarClip.BeginAnimation(RectangleGeometry.RadiusXProperty, clipRadiusAnim);
        ProgressBarClip.BeginAnimation(RectangleGeometry.RadiusYProperty, clipRadiusAnim);

        // Time text — scale + translate down
        if (_currentTimeHoverTransform == null)
        {
            _currentTimeHoverScale = new ScaleTransform(1, 1);
            _currentTimeHoverTranslate = new TranslateTransform(0, 0);
            _currentTimeHoverTransform = new TransformGroup();
            _currentTimeHoverTransform.Children.Add(_currentTimeHoverScale);
            _currentTimeHoverTransform.Children.Add(_currentTimeHoverTranslate);
            CurrentTimeText.RenderTransformOrigin = new Point(0, 0.5);
            CurrentTimeText.RenderTransform = _currentTimeHoverTransform;
        }
        if (_remainingTimeHoverTransform == null)
        {
            _remainingTimeHoverScale = new ScaleTransform(1, 1);
            _remainingTimeHoverTranslate = new TranslateTransform(0, 0);
            _remainingTimeHoverTransform = new TransformGroup();
            _remainingTimeHoverTransform.Children.Add(_remainingTimeHoverScale);
            _remainingTimeHoverTransform.Children.Add(_remainingTimeHoverTranslate);
            RemainingTimeText.RenderTransformOrigin = new Point(1, 0.5);
            RemainingTimeText.RenderTransform = _remainingTimeHoverTransform;
        }

        var timeScaleAnim = new DoubleAnimation { To = timeScale, Duration = duration, EasingFunction = easing };
        var timeTranslateAnim = new DoubleAnimation { To = timeTranslateY, Duration = duration, EasingFunction = easing };
        Timeline.SetDesiredFrameRate(timeScaleAnim, fps);
        Timeline.SetDesiredFrameRate(timeTranslateAnim, fps);

        _currentTimeHoverScale!.BeginAnimation(ScaleTransform.ScaleXProperty, timeScaleAnim);
        _currentTimeHoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, timeScaleAnim);
        _currentTimeHoverTranslate!.BeginAnimation(TranslateTransform.YProperty, timeTranslateAnim);
        _remainingTimeHoverScale!.BeginAnimation(ScaleTransform.ScaleXProperty, timeScaleAnim);
        _remainingTimeHoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, timeScaleAnim);
        _remainingTimeHoverTranslate!.BeginAnimation(TranslateTransform.YProperty, timeTranslateAnim);

        // Blur & dim controls only (not title/artist)
        var blurAnim = new DoubleAnimation { To = blurRadius, Duration = duration, EasingFunction = easing };
        var dimAnim = new DoubleAnimation { To = surroundOpacity, Duration = duration, EasingFunction = easing };
        Timeline.SetDesiredFrameRate(blurAnim, fps);
        Timeline.SetDesiredFrameRate(dimAnim, fps);

        _mediaControlsHoverBlur ??= new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
        MediaControls.Effect = _mediaControlsHoverBlur;
        _mediaControlsHoverBlur.BeginAnimation(BlurEffect.RadiusProperty, blurAnim);
        MediaControls.BeginAnimation(OpacityProperty, dimAnim);
    }

    #endregion

    #region Status Bar Animation

    private void AnimateStatusBarReveal(bool show)
    {
        var dur = TimeSpan.FromMilliseconds(show ? 340 : 220);
        var easing = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 };
        var settingsEase = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.22 };
        var animFps = 144;

        BatterySection.BeginAnimation(OpacityProperty, null);
        BatteryTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        NavIconsPanel.BeginAnimation(OpacityProperty, null);
        NavIconsBackground.BeginAnimation(OpacityProperty, null);
        NavIconsTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        SettingsButton.BeginAnimation(OpacityProperty, null);
        SettingsTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        SettingsScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        SettingsScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        SettingsRotate.BeginAnimation(RotateTransform.AngleProperty, null);

        if (show)
        {
            BatterySection.Visibility = Visibility.Visible;
            NavIconsPanel.Visibility = Visibility.Visible;
            UpdateNavIconsActiveState();
            if (_isSecondaryView)
                NavIconsBackground.Visibility = Visibility.Visible;
            SettingsButton.Visibility = Visibility.Visible;
            SettingsScale.ScaleX = 0.86;
            SettingsScale.ScaleY = 0.86;
            SettingsRotate.Angle = 20;
        }

        var batteryOpacityAnim = new DoubleAnimation
        {
            To = show ? 1.0 : 0.0,
            Duration = dur,
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(batteryOpacityAnim, animFps);

        var batteryTranslateAnim = new DoubleAnimation
        {
            To = show ? 0 : -6,
            Duration = dur,
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(batteryTranslateAnim, animFps);

        var settingsDelay = show ? TimeSpan.FromMilliseconds(56) : TimeSpan.Zero;
        var settingsOpacityAnim = new DoubleAnimation
        {
            To = show ? 1.0 : 0.0,
            Duration = dur,
            EasingFunction = easing,
            BeginTime = settingsDelay
        };
        Timeline.SetDesiredFrameRate(settingsOpacityAnim, animFps);

        var settingsTranslateAnim = new DoubleAnimation
        {
            To = show ? 0 : -6,
            Duration = dur,
            EasingFunction = settingsEase,
            BeginTime = settingsDelay
        };
        Timeline.SetDesiredFrameRate(settingsTranslateAnim, animFps);

        var settingsScaleAnim = new DoubleAnimation
        {
            To = show ? 1.0 : 0.86,
            Duration = new Duration(TimeSpan.FromMilliseconds(show ? 460 : 200)),
            EasingFunction = settingsEase,
            BeginTime = settingsDelay
        };
        Timeline.SetDesiredFrameRate(settingsScaleAnim, animFps);

        var settingsRotateAnim = new DoubleAnimation
        {
            To = show ? 45 : 20,
            Duration = new Duration(TimeSpan.FromMilliseconds(show ? 520 : 200)),
            EasingFunction = easing,
            BeginTime = settingsDelay
        };
        Timeline.SetDesiredFrameRate(settingsRotateAnim, animFps);

        // Update notification animation (with 30ms stagger when showing, between battery and settings)
        if (_isUpdateAvailable && UpdateNotificationButton != null)
        {
            var updateOpacityAnim = new DoubleAnimation
            {
                To = show ? 1.0 : 0.0,
                Duration = dur,
                EasingFunction = easing,
                BeginTime = show ? TimeSpan.FromMilliseconds(30) : TimeSpan.Zero
            };
            Timeline.SetDesiredFrameRate(updateOpacityAnim, animFps);

            var updateTranslateAnim = new DoubleAnimation
            {
                To = show ? 0 : -4,
                Duration = dur,
                EasingFunction = easing,
                BeginTime = show ? TimeSpan.FromMilliseconds(30) : TimeSpan.Zero
            };
            Timeline.SetDesiredFrameRate(updateTranslateAnim, animFps);

            if (show)
            {
                // Reset and start pulse before reveal; don't depend on Completed timing.
                if (UpdateIconBrush != null)
                {
                    UpdateIconBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                    UpdateIconBrush.Color = Color.FromRgb(48, 209, 88);
                }
                UpdateNotificationButton.IsHitTestVisible = true;
                UpdateNotificationButton.Cursor = System.Windows.Input.Cursors.Hand;
                StartUpdatePulseAnimation();
            }
            else
            {
                UpdateNotificationButton.IsHitTestVisible = false;
                StopUpdatePulseAnimation();
            }

            UpdateNotificationButton.BeginAnimation(OpacityProperty, updateOpacityAnim);
            UpdateNotificationTranslate.BeginAnimation(TranslateTransform.YProperty, updateTranslateAnim);
        }

        // Apply animations
        BatterySection.BeginAnimation(OpacityProperty, batteryOpacityAnim);
        BatteryTranslate.BeginAnimation(TranslateTransform.YProperty, batteryTranslateAnim);

        // NavIconsPanel (icons themselves) always animate with expand/collapse
        NavIconsPanel.BeginAnimation(OpacityProperty, batteryOpacityAnim);
        NavIconsTranslate.BeginAnimation(TranslateTransform.YProperty, batteryTranslateAnim);

        // NavIconsBackground (black border) only in secondary view
        if (_isSecondaryView)
        {
            var navBgOpacityAnim = new DoubleAnimation
            {
                To = show ? 1.0 : 0.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(show ? 340 : 120)),
                EasingFunction = easing
            };
            Timeline.SetDesiredFrameRate(navBgOpacityAnim, animFps);
            NavIconsBackground.BeginAnimation(OpacityProperty, navBgOpacityAnim);
        }

        SettingsButton.BeginAnimation(OpacityProperty, settingsOpacityAnim, HandoffBehavior.SnapshotAndReplace);
        SettingsTranslate.BeginAnimation(TranslateTransform.YProperty, settingsTranslateAnim, HandoffBehavior.SnapshotAndReplace);
        SettingsScale.BeginAnimation(ScaleTransform.ScaleXProperty, settingsScaleAnim, HandoffBehavior.SnapshotAndReplace);
        SettingsScale.BeginAnimation(ScaleTransform.ScaleYProperty, settingsScaleAnim, HandoffBehavior.SnapshotAndReplace);
        SettingsRotate.BeginAnimation(RotateTransform.AngleProperty, settingsRotateAnim, HandoffBehavior.SnapshotAndReplace);
    }

    #endregion

    #region Settings Absorb Animation
public void PlaySettingsEjectAnimation()
    {
        if (_isExpanded || _isAnimating) return;

        const int fps = 144;

        NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        NotchBorder.BeginAnimation(WidthProperty, null);
        NotchBorder.BeginAnimation(HeightProperty, null);

        var ejectDur = TimeSpan.FromMilliseconds(150);
        var springDur = TimeSpan.FromMilliseconds(600);

        // Notch width: expand then spring back
        double currentWidth = _collapsedWidth;
        double peakWidth = currentWidth + 40;

        var widthAnim = new DoubleAnimationUsingKeyFrames();
        widthAnim.KeyFrames.Add(new EasingDoubleKeyFrame(peakWidth,
            KeyTime.FromTimeSpan(ejectDur),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        widthAnim.KeyFrames.Add(new EasingDoubleKeyFrame(currentWidth,
            KeyTime.FromTimeSpan(ejectDur + springDur),
            _easeSoftSpring));
        Timeline.SetDesiredFrameRate(widthAnim, fps);

        // Notch height: expand then spring back
        double currentHeight = _collapsedHeight;
        double peakHeight = currentHeight + 8;

        var heightAnim = new DoubleAnimationUsingKeyFrames();
        heightAnim.KeyFrames.Add(new EasingDoubleKeyFrame(peakHeight,
            KeyTime.FromTimeSpan(ejectDur),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        heightAnim.KeyFrames.Add(new EasingDoubleKeyFrame(currentHeight,
            KeyTime.FromTimeSpan(ejectDur + springDur),
            _easeSoftSpring));
        Timeline.SetDesiredFrameRate(heightAnim, fps);

        // ScaleY: brief stretch downward (ejecting)
        var scaleY = new DoubleAnimationUsingKeyFrames();
        scaleY.KeyFrames.Add(new EasingDoubleKeyFrame(1.15,
            KeyTime.FromTimeSpan(ejectDur),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        scaleY.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,
            KeyTime.FromTimeSpan(ejectDur + springDur),
            _easeSoftSpring));
        Timeline.SetDesiredFrameRate(scaleY, fps);

        NotchBorder.BeginAnimation(WidthProperty, widthAnim);
        NotchBorder.BeginAnimation(HeightProperty, heightAnim);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
    }
public void PlaySettingsAbsorbAnimation()
    {
        if (_isExpanded || _isAnimating) return;

        const int fps = 144;

        // Cancel any in-progress animations on the notch
        NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        NotchBorder.BeginAnimation(WidthProperty, null);
        NotchBorder.BeginAnimation(HeightProperty, null);
        var absorbDelay = TimeSpan.FromMilliseconds(480);
        var openDur = TimeSpan.FromMilliseconds(200);
        var springDur = TimeSpan.FromMilliseconds(700);

        // --- Notch width: expand wider then spring back ---
        double currentWidth = _collapsedWidth;
        double peakWidth = currentWidth + 60; 

        var widthAnim = new DoubleAnimationUsingKeyFrames { BeginTime = absorbDelay };
        widthAnim.KeyFrames.Add(new EasingDoubleKeyFrame(peakWidth,
            KeyTime.FromTimeSpan(openDur),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        widthAnim.KeyFrames.Add(new EasingDoubleKeyFrame(currentWidth,
            KeyTime.FromTimeSpan(openDur + springDur),
            _easeSoftSpring));
        Timeline.SetDesiredFrameRate(widthAnim, fps);

        // --- Notch height: expand slightly then spring back ---
        double currentHeight = _collapsedHeight;
        double peakHeight = currentHeight + 10;

        var heightAnim = new DoubleAnimationUsingKeyFrames { BeginTime = absorbDelay };
        heightAnim.KeyFrames.Add(new EasingDoubleKeyFrame(peakHeight,
            KeyTime.FromTimeSpan(openDur),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        heightAnim.KeyFrames.Add(new EasingDoubleKeyFrame(currentHeight,
            KeyTime.FromTimeSpan(openDur + springDur),
            _easeSoftSpring));
        Timeline.SetDesiredFrameRate(heightAnim, fps);

        // --- Corner radius: open up (larger radius = more rounded/open) then back ---
        double currentRadius = _cornerRadiusCollapsed;
        double peakRadius = currentRadius + 6;

        var radiusAnim = new DoubleAnimationUsingKeyFrames { BeginTime = absorbDelay };
        radiusAnim.KeyFrames.Add(new EasingDoubleKeyFrame(peakRadius,
            KeyTime.FromTimeSpan(openDur),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        radiusAnim.KeyFrames.Add(new EasingDoubleKeyFrame(currentRadius,
            KeyTime.FromTimeSpan(openDur + springDur),
            _easeSoftSpring));
        Timeline.SetDesiredFrameRate(radiusAnim, fps);

        // --- Subtle scale Y stretch (notch "breathes in") ---
        var scaleY = new DoubleAnimationUsingKeyFrames { BeginTime = absorbDelay };
        scaleY.KeyFrames.Add(new EasingDoubleKeyFrame(1.12,
            KeyTime.FromTimeSpan(openDur),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        scaleY.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,
            KeyTime.FromTimeSpan(openDur + springDur),
            _easeSoftSpring));
        Timeline.SetDesiredFrameRate(scaleY, fps);

        NotchBorder.BeginAnimation(WidthProperty, widthAnim);
        NotchBorder.BeginAnimation(HeightProperty, heightAnim);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
        this.BeginAnimation(CurrentCornerRadiusProperty, radiusAnim);
    }

    #endregion
}

