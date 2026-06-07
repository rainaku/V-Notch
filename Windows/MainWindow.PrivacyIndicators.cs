using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    #region Privacy Indicators (Mic/Camera In-Use — Single Dot)

    // iOS behavior: single dot, orange = mic, green = cam, alternates when both active.
    private bool _privacyIndicatorsVisible = false;
    private PrivacyIndicatorState _lastPrivacyState = PrivacyIndicatorState.Empty;

    // Right margin of the privacy dot panel within NotchContent. In compact music mode the
    // visualizer occupies the top-right corner, so we shift the dot further left to clear it.
    private const double PrivacyDotDefaultRightMargin = 27.0;
    private const double PrivacyDotCompactMusicRightMargin = 34.0;
    private const double PrivacyDotTopMargin = 11.2;
    private const double PrivacyDotSize = 7.0;

    private static readonly Color _micColor = Color.FromRgb(0xFF, 0x95, 0x00);  // Orange — mic only
    private static readonly Color _camColor = Color.FromRgb(0x30, 0xD1, 0x58);  // Green — cam only
    private static readonly Color _bothColor = Color.FromRgb(0x00, 0x4E, 0x92); // Navy blue — mic+cam
    private static readonly Color _screenRecColor = Color.FromRgb(0xFF, 0x3B, 0x30); // Red — screen recording

    private void PrivacyModule_StateChanged(object? sender, PrivacyIndicatorState state)
    {
        Dispatcher.BeginInvoke(() => UpdatePrivacyIndicators(state));
    }

    private void UpdatePrivacyIndicators(PrivacyIndicatorState state)
    {
        if (!_settings.ShowSystemNotifications)
        {
            HidePrivacyIndicators();
            _lastPrivacyState = state;
            return;
        }

        if (_isCameraActive && state.CameraInUse)
        {
            state = state with
            {
                CameraInUse = false,
                CameraConsumers = Array.Empty<string>()
            };
        }

        bool shouldShow = state.AnyInUse;
        bool wasShowing = _privacyIndicatorsVisible;

        _lastPrivacyState = state;

        if (shouldShow && !wasShowing)
        {
            ShowPrivacyDot(state);
        }
        else if (!shouldShow && wasShowing)
        {
            HidePrivacyIndicators();
        }
        else if (shouldShow)
        {
            bool suppressed = _isVolumeIndicatorActive || _isBluetoothNotificationVisible || _isClipboardPeekActive;

            // Self-heal: we think the dot is visible but it isn't actually drawn — e.g. a
            // recording started while the volume/Bluetooth/clipboard UI was up. Draw it now.
            if (!suppressed && PrivacyIndicatorPanel.Visibility != Visibility.Visible)
            {
                ShowPrivacyDot(state);
            }
            else
            {
                // Already visible — update color only.
                ApplyDotColor(state, animate: true);
            }
        }
    }

    private void ShowPrivacyDot(PrivacyIndicatorState state)
    {
        _privacyIndicatorsVisible = true;

        // Don't show visually if volume bar or bluetooth notification or clipboard peek is active
        if (_isVolumeIndicatorActive || _isBluetoothNotificationVisible || _isClipboardPeekActive)
            return;

        PrivacyDot.Visibility = Visibility.Visible;
        PrivacyIndicatorPanel.Visibility = Visibility.Visible;

        UpdatePrivacyDotPosition();

        // Set initial color
        ApplyDotColor(state, animate: false);

        // Fade in panel
        var fadeIn = MakeAnim(0d, 1d, _dur350, _easeExpOut7, null);
        PrivacyIndicatorPanel.BeginAnimation(OpacityProperty, fadeIn);

        // Pop-in scale
        AnimateDotIn(PrivacyDotScale);

        // Start breathing
        StartBreathingAnimation(PrivacyDot);

        RuntimeLog.Log("PRIVACY", $"Dot shown — Mic: {state.MicrophoneInUse}, Cam: {state.CameraInUse}");
    }

    private void HidePrivacyIndicators()
    {
        if (!_privacyIndicatorsVisible) return;
        _privacyIndicatorsVisible = false;

        StopBreathingAnimation(PrivacyDot);

        var fadeOut = MakeAnim(1d, 0d, _dur250, _easePowerIn2, null);
        fadeOut.Completed += (_, _) =>
        {
            PrivacyIndicatorPanel.Visibility = Visibility.Collapsed;
            PrivacyDot.Visibility = Visibility.Collapsed;
        };
        PrivacyIndicatorPanel.BeginAnimation(OpacityProperty, fadeOut);

        RuntimeLog.Log("PRIVACY", "Dot hidden");
    }

    private void SuppressPrivacyDot()
    {
        if (!_privacyIndicatorsVisible) return;

        StopBreathingAnimation(PrivacyDot);
        PrivacyIndicatorPanel.BeginAnimation(OpacityProperty, null);
        PrivacyIndicatorPanel.Opacity = 0;
        PrivacyIndicatorPanel.Visibility = Visibility.Collapsed;
    }

    private void RestorePrivacyDotVisibility()
    {
        if (!_privacyIndicatorsVisible) return;
        // Don't restore if another suppressor is still active
        if (_isVolumeIndicatorActive || _isBluetoothNotificationVisible || _isClipboardPeekActive) return;

        PrivacyDot.Visibility = Visibility.Visible;
        PrivacyIndicatorPanel.Visibility = Visibility.Visible;

        UpdatePrivacyDotPosition();

        ApplyDotColor(_lastPrivacyState, animate: false);

        var fadeIn = MakeAnim(0d, 1d, _dur250, _easePowerOut3, null);
        PrivacyIndicatorPanel.BeginAnimation(OpacityProperty, fadeIn);

        StartBreathingAnimation(PrivacyDot);
    }

    // Keeps the privacy dot clear of the compact-mode music visualizer (top-right corner)
    // and vertically centered in the dynamic island pill so it lines up with the content.
    private void UpdatePrivacyDotPosition()
    {
        bool compact = _isMusicCompactMode && !_isExpanded;
        double right = compact ? PrivacyDotCompactMusicRightMargin : PrivacyDotDefaultRightMargin;

        double top = PrivacyDotTopMargin;
        if (compact && _settings.EnableDynamicIslandMode)
        {
            // In island mode the visualizer/thumbnail are centered at H/2 — match that.
            double h = GetCollapsedHeight();
            if (h > 0) top = Math.Max(0, (h - PrivacyDotSize) / 2.0);
        }

        PrivacyIndicatorPanel.Margin = new Thickness(0, top, right, 0);
    }

    private void ApplyDotColor(PrivacyIndicatorState state, bool animate)
    {
        Color targetColor;
        if (state.ScreenRecordingActive)
            targetColor = _screenRecColor; // Red — screen recording (highest priority)
        else if (state.MicrophoneInUse && state.CameraInUse)
            targetColor = _bothColor;      // Navy — mic+cam
        else if (state.CameraInUse)
            targetColor = _camColor;       // Green — cam only
        else
            targetColor = _micColor;       // Orange — mic only

        if (animate)
        {
            var colorAnim = new ColorAnimation
            {
                To = targetColor,
                Duration = new Duration(TimeSpan.FromMilliseconds(300)),
                EasingFunction = _easeSineInOut
            };
            Timeline.SetDesiredFrameRate(colorAnim, 60);
            PrivacyDotBrush.BeginAnimation(SolidColorBrush.ColorProperty, colorAnim);
        }
        else
        {
            PrivacyDotBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            PrivacyDotBrush.Color = targetColor;
        }
    }

    private static void AnimateDotIn(ScaleTransform scale)
    {
        var popIn = new DoubleAnimationUsingKeyFrames();
        popIn.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        popIn.KeyFrames.Add(new EasingDoubleKeyFrame(1.2, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)), _easeExpOut7));
        popIn.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(400)), _easeSoftSpring));
        Timeline.SetDesiredFrameRate(popIn, VNotch.Services.AnimationConfig.TargetFps);

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, popIn);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, popIn);
    }

    private static void StartBreathingAnimation(FrameworkElement dot)
    {
        var breathing = new DoubleAnimation
        {
            From = 1.0,
            To = 0.4,
            Duration = new Duration(TimeSpan.FromMilliseconds(1400)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = _easeSineInOut
        };
        // Slow 1.4s breathing — 24fps is visually identical and lighter on the compositor.
        Timeline.SetDesiredFrameRate(breathing, 24);
        dot.BeginAnimation(OpacityProperty, breathing);
    }

    private static void StopBreathingAnimation(FrameworkElement dot)
    {
        dot.BeginAnimation(OpacityProperty, null);
        dot.Opacity = 1.0;
    }

    #endregion
}
