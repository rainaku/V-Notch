using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Models;

namespace VNotch;

/// <summary>
/// Battery icon rendering: fill width, colour (low / charging / normal),
/// charging-bolt overlay, and the subtle pulse while charging.
/// Split out of <see cref="MainWindow"/> xaml.cs for readability.
/// </summary>
public partial class MainWindow
{
    private Storyboard? _chargingPulseStoryboard;

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
        // Reserved: explicit poke point if we ever need to force a refresh
        // outside the BatteryModule's own event cadence.
    }
}
