using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;
public partial class MainWindow
{
    private Storyboard? _chargingPulseStoryboard;
    private bool _wasCharging = true; // Start as true to suppress notification on first battery update at app launch
    private bool _isChargingNotificationVisible = false;
    private DispatcherTimer? _chargingNotificationDismissTimer;

    private void HandleBatteryUpdate(BatteryInfo battery)
    {
        BatteryPercent.Text = battery.GetPercentageText();

        // Inner fill space = BatteryIcon.Width(27.08) - BorderThickness(1.36*2) - MarginLeft(1.36) = 23.0
        double targetWidth = Math.Max(1.08, battery.Percentage / 100.0 * 23.0);
        var widthAnimation = new DoubleAnimation
        {
            To = targetWidth,
            Duration = TimeSpan.FromMilliseconds(300),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        BatteryFill.BeginAnimation(WidthProperty, widthAnimation);

        SolidColorBrush fillBrush;
        SolidColorBrush percentBrush;
        bool showLightning = false;

        if (battery.Percentage <= 20 && !battery.IsCharging)
        {
            fillBrush = _brushLowBattery;
            percentBrush = _brushLowBattery;
            showLightning = false;
        }
        else if (battery.IsCharging)
        {
            fillBrush = _brushCharging;
            percentBrush = _brushWhite;
            showLightning = true;
        }
        else
        {
            fillBrush = _brushWhite;
            percentBrush = _brushWhite;
            showLightning = false;
        }

        AnimateBrushTransition(BatteryFill, fillBrush);
        AnimateBrushTransition(BatteryPercent, percentBrush);
        AnimateChargingBolt(showLightning);

        if (battery.IsCharging)
        {
            StartChargingPulse();
        }
        else
        {
            StopChargingPulse();
        }

        // Show compact charging notification when charger is plugged in
        if (battery.IsCharging && !_wasCharging)
        {
            ShowChargingNotification(battery.Percentage);
        }
        _wasCharging = battery.IsCharging;
    }

    private void ShowChargingNotification(int percentage)
    {
        // Don't show if expanded, animating, greeting active, or another notification is visible
        if (_isExpanded || _isAnimating || _isGreetingActive || _isBluetoothNotificationVisible || _isChargingNotificationVisible)
            return;

        _isChargingNotificationVisible = true;

        ChargingPercentText.Text = $"{percentage}%";
        ChargingStatusText.Text = Loc.Get("battery.charging");

        // Set battery fill width based on percentage (max 17px)
        double fillWidth = Math.Max(2, percentage / 100.0 * 17.0);
        ChargingBatteryFill.Width = fillWidth;

        // Hide current content
        CollapsedContent.BeginAnimation(OpacityProperty, null);
        CollapsedContent.Opacity = 0;
        CollapsedContent.Visibility = Visibility.Collapsed;

        // Force hide music compact content regardless of current state
        MusicCompactContent.BeginAnimation(OpacityProperty, null);
        MusicCompactContent.Opacity = 0;
        MusicCompactContent.Visibility = Visibility.Collapsed;

        // Also hide volume indicator if active
        if (_isVolumeIndicatorActive)
        {
            _volumeIndicatorHideTimer?.Stop();
            _isVolumeIndicatorActive = false;
            VolumeIndicatorContainer.BeginAnimation(OpacityProperty, null);
            VolumeIndicatorContainer.Opacity = 0;
            VolumeIndicatorContainer.Visibility = Visibility.Collapsed;
        }

        // Show charging notification
        ChargingNotification.Visibility = Visibility.Visible;
        ChargingNotification.Opacity = 0;

        // Bounce the notch
        PlayChargingBounce();

        // Fade in + slide up
        var fadeIn = MakeAnim(0d, 1d, _dur350, _easeExpOut7, TimeSpan.FromMilliseconds(100));
        ChargingNotification.BeginAnimation(OpacityProperty, fadeIn);

        var slideUp = MakeAnim(6d, 0d, _dur350, _easeExpOut7, TimeSpan.FromMilliseconds(100));
        ChargingNotificationTranslate.BeginAnimation(TranslateTransform.YProperty, slideUp);

        // Icon spring scale
        var iconScale = MakeAnim(0.6d, 1d, _dur400, _easeSpring, TimeSpan.FromMilliseconds(150));
        ChargingIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, iconScale);
        ChargingIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, iconScale);

        // Auto-dismiss after 3 seconds
        _chargingNotificationDismissTimer?.Stop();
        _chargingNotificationDismissTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(3000)
        };
        _chargingNotificationDismissTimer.Tick += (s, e) =>
        {
            _chargingNotificationDismissTimer.Stop();
            DismissChargingNotification();
        };
        _chargingNotificationDismissTimer.Start();
    }

    private void DismissChargingNotification()
    {
        if (!_isChargingNotificationVisible) return;

        // Shrink notch back to collapsed width
        var widthShrink = new DoubleAnimation(_collapsedWidth, new Duration(TimeSpan.FromMilliseconds(400)))
        {
            EasingFunction = _easeExpOut6
        };
        Timeline.SetDesiredFrameRate(widthShrink, 144);
        widthShrink.Completed += (s, e) =>
        {
            NotchBorder.BeginAnimation(WidthProperty, null);
            NotchBorder.Width = _collapsedWidth;
        };
        NotchBorder.BeginAnimation(WidthProperty, widthShrink);

        var fadeOut = MakeAnim(1d, 0d, _dur250, _easePowerIn2, null);
        fadeOut.Completed += (s, e) =>
        {
            ChargingNotification.Visibility = Visibility.Collapsed;
            _isChargingNotificationVisible = false;

            // Restore previous content
            if (_isMusicCompactMode && _currentMediaInfo != null)
            {
                MusicCompactContent.Visibility = Visibility.Visible;
                MusicCompactContent.Opacity = 0;
                var fadeInMusic = MakeAnim(0d, 1d, _dur250, _easePowerOut3, null);
                MusicCompactContent.BeginAnimation(OpacityProperty, fadeInMusic);
            }
            else
            {
                CollapsedContent.Visibility = Visibility.Visible;
                CollapsedContent.Opacity = 0;
                var fadeInCollapsed = MakeAnim(0d, 1d, _dur250, _easePowerOut3, null);
                CollapsedContent.BeginAnimation(OpacityProperty, fadeInCollapsed);
            }
        };

        ChargingNotification.BeginAnimation(OpacityProperty, fadeOut);

        var slideDown = MakeAnim(0d, -4d, _dur250, _easePowerIn2, null);
        ChargingNotificationTranslate.BeginAnimation(TranslateTransform.YProperty, slideDown);
    }

    private void PlayChargingBounce()
    {
        if (_isExpanded || _isAnimating) return;

        const int fps = 144;

        // Expand width slightly to accommodate the charging notification content
        double targetWidth = _collapsedWidth + 28;
        var widthExpand = new DoubleAnimation(targetWidth, new Duration(TimeSpan.FromMilliseconds(500)))
        {
            EasingFunction = _easeSoftSpring
        };
        Timeline.SetDesiredFrameRate(widthExpand, fps);
        NotchBorder.BeginAnimation(WidthProperty, widthExpand);

        // Subtle bounce scale
        var durPeak = TimeSpan.FromMilliseconds(140);
        var durEnd = TimeSpan.FromMilliseconds(700);

        var bounceX = new DoubleAnimationUsingKeyFrames();
        bounceX.KeyFrames.Add(new EasingDoubleKeyFrame(1.06, KeyTime.FromTimeSpan(durPeak), _easeQuadOut));
        bounceX.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(durEnd), _easeSoftSpring));
        Timeline.SetDesiredFrameRate(bounceX, fps);

        var bounceY = new DoubleAnimationUsingKeyFrames();
        bounceY.KeyFrames.Add(new EasingDoubleKeyFrame(0.95, KeyTime.FromTimeSpan(durPeak), _easeQuadOut));
        bounceY.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(durEnd), _easeSoftSpring));
        Timeline.SetDesiredFrameRate(bounceY, fps);

        NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounceX);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounceY);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounceX);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounceY);
    }

    private static void AnimateBrushTransition(FrameworkElement element, SolidColorBrush targetBrush)
    {
        var currentBrush = element is TextBlock tb ? tb.Foreground as SolidColorBrush :
                           element is Border border ? border.Background as SolidColorBrush : null;

        if (currentBrush == null || currentBrush.Color == targetBrush.Color)
        {
            if (element is TextBlock textBlock)
                textBlock.Foreground = targetBrush;
            else if (element is Border borderElement)
                borderElement.Background = targetBrush;
            return;
        }

        var colorAnimation = new ColorAnimation
        {
            To = targetBrush.Color,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };

        var animatedBrush = new SolidColorBrush(currentBrush.Color);
        if (element is TextBlock textBlockElement)
            textBlockElement.Foreground = animatedBrush;
        else if (element is Border borderElement2)
            borderElement2.Background = animatedBrush;

        animatedBrush.BeginAnimation(SolidColorBrush.ColorProperty, colorAnimation);
    }

    private void AnimateChargingBolt(bool show)
    {
        var opacityAnimation = new DoubleAnimation
        {
            To = show ? 1.0 : 0.0,
            Duration = TimeSpan.FromMilliseconds(250),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var scaleAnimation = new DoubleAnimation
        {
            To = show ? 1.0 : 0.95,
            Duration = TimeSpan.FromMilliseconds(250),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        ChargingBolt.BeginAnimation(OpacityProperty, opacityAnimation);
        ChargingBoltScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
        ChargingBoltScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation);
    }

    private void StartChargingPulse()
    {
        if (_chargingPulseStoryboard != null) return;

        _chargingPulseStoryboard = new Storyboard
        {
            RepeatBehavior = RepeatBehavior.Forever
        };

        var pulseAnimation = new DoubleAnimation
        {
            From = 1.0,
            To = 0.85,
            Duration = TimeSpan.FromMilliseconds(1000),
            AutoReverse = true,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };

        Storyboard.SetTarget(pulseAnimation, BatteryFill);
        Storyboard.SetTargetProperty(pulseAnimation, new PropertyPath("Opacity"));
        _chargingPulseStoryboard.Children.Add(pulseAnimation);

        _chargingPulseStoryboard.Begin();
    }

    private void StopChargingPulse()
    {
        if (_chargingPulseStoryboard == null) return;

        _chargingPulseStoryboard.Stop();
        _chargingPulseStoryboard = null;

        var resetAnimation = new DoubleAnimation
        {
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        BatteryFill.BeginAnimation(OpacityProperty, resetAnimation);
    }

    private void UpdateBatteryInfo()
    {
        // Reserved: explicit poke point if we ever need to force a refresh outside the BatteryModule's own event cadence.
    }
}
