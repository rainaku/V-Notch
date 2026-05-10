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

/// <summary>
/// Reusable animation primitives on top of <see cref="VNotch.Services.AnimationPrimitives"/>:
/// notch hover, thumbnail hover, button-press squash, icon swaps, skip arrow
/// animations, corner-radius morph, progress-bar hover lift, appearance
/// animation and the status-bar reveal (battery / settings / update badge
/// fly-in when the notch expands).
/// Split out of <see cref="MainWindow"/> .Animation partial for readability.
/// </summary>
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

        double thumbScale = isHovered ? 1.6 : 1.0;
        double notchWidth = isHovered ? _collapsedWidth + 64 : _collapsedWidth;
        double notchHeight = isHovered ? 84 : _collapsedHeight;
        double infoOpacity = isHovered ? 1 : 0;
        
        var duration = isHovered ? _dur500 : _dur350;
        var easing = isHovered ? (IEasingFunction)_easeThumbSpring : _easeExpOut6;
        var animFps = 144;

        
        var widthAnim = MakeAnim(notchWidth, duration, isHovered ? _easeExpOut6 : _easeQuadOut, animFps);
        var heightAnim = MakeAnim(notchHeight, duration, isHovered ? _easeExpOut6 : _easeQuadOut, animFps);
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
    }

    private void UpdateCompactMarquee()
    {
        if (_currentMediaInfo == null) return;
        
        CompactTitleMarquee.Text = _currentMediaInfo.CurrentTrack;
        CompactTitleMarquee.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        
        double textWidth = CompactTitleMarquee.DesiredSize.Width;

        double containerWidth = Math.Max(0, ((NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : NotchBorder.Width) + 64) - 12);

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
        var scaleDown = MakeAnim(1d, 0.9d, _dur80, null, null);
        var scaleUp = new DoubleAnimation(0.9, 1, _dur100) { BeginTime = TimeSpan.FromMilliseconds(80) };

        var transform = button.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        button.RenderTransform = transform;

        transform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDown);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDown);

        scaleDown.Completed += (s, e) =>
        {
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleUp);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleUp);
        };
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

    public double CurrentCornerRadius
    {
        get => (double)GetValue(CurrentCornerRadiusProperty);
        set => SetValue(CurrentCornerRadiusProperty, value);
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

    private void AnimateCornerRadius(double targetRadius, TimeSpan duration)
    {
        double startRadius = NotchBorder.CornerRadius.BottomLeft;
        
        if (Math.Abs(targetRadius - startRadius) < 0.5) return;

        CurrentCornerRadius = startRadius;

        var anim = MakeAnim(startRadius, targetRadius, new Duration(duration), _easeExpOut6, null);
        this.BeginAnimation(CurrentCornerRadiusProperty, anim);
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

    private void AnimateProgressBarHover(bool isHovered)
    {
        double margin = isHovered ? 0 : (_isMusicExpanded ? 6 : 17);
        double scaleY = isHovered ? 1.8 : 1.0;
        double bgOpacity = isHovered ? 0.4 : 1.0;
        
        var duration = isHovered ? _dur400 : _dur350;
        var easing = isHovered ? (IEasingFunction)_easeExpOut6 : _easeQuadOut;

        
        var marginAnim = new ThicknessAnimation(ProgressBarContainer.Margin, new Thickness(margin, 0, margin, 0), duration)
        {
            EasingFunction = easing
        };
        ProgressBarContainer.BeginAnimation(MarginProperty, marginAnim);

        
        var scaleAnim = MakeAnim(scaleY, duration, easing);
        ProgressBarMainScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);

        
        var bgFadeAnim = MakeAnim(bgOpacity, duration, _easeQuadOut);
        ProgressBarBg.BeginAnimation(OpacityProperty, bgFadeAnim);
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
        SettingsButton.BeginAnimation(OpacityProperty, null);
        SettingsTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        SettingsScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        SettingsScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        SettingsRotate.BeginAnimation(RotateTransform.AngleProperty, null);

        if (show)
        {
            BatterySection.Visibility = Visibility.Visible;
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

        SettingsButton.BeginAnimation(OpacityProperty, settingsOpacityAnim, HandoffBehavior.SnapshotAndReplace);
        SettingsTranslate.BeginAnimation(TranslateTransform.YProperty, settingsTranslateAnim, HandoffBehavior.SnapshotAndReplace);
        SettingsScale.BeginAnimation(ScaleTransform.ScaleXProperty, settingsScaleAnim, HandoffBehavior.SnapshotAndReplace);
        SettingsScale.BeginAnimation(ScaleTransform.ScaleYProperty, settingsScaleAnim, HandoffBehavior.SnapshotAndReplace);
        SettingsRotate.BeginAnimation(RotateTransform.AngleProperty, settingsRotateAnim, HandoffBehavior.SnapshotAndReplace);
    }

    #endregion
}

