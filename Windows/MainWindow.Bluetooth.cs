using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    #region Bluetooth Notification

    private DispatcherTimer? _bluetoothDismissTimer;
    private bool _isBluetoothNotificationVisible = false;
    private bool _currentBluetoothConnected = true;

    private void BluetoothModule_DeviceConnected(object? sender, BluetoothDeviceInfo info)
    {
        Dispatcher.BeginInvoke(() =>
        {
            ShowBluetoothNotification(info, connected: true);
        });
    }

    private void BluetoothModule_DeviceDisconnected(object? sender, BluetoothDeviceInfo info)
    {
        Dispatcher.BeginInvoke(() =>
        {
            ShowBluetoothNotification(info, connected: false);
        });
    }

    private void ShowBluetoothNotification(BluetoothDeviceInfo device, bool connected)
    {
        // Don't show if notch is hidden, animating, or music is expanded
        if (!_isNotchVisible || _isAnimating || _isExpanded || _notchState.IsMusicExpanded)
            return;

        _currentBluetoothConnected = connected;

        if (connected)
        {
            BluetoothDeviceName.Text = device.Name;
            BluetoothIcon.Data = GetBluetoothIconGeometry(device.DeviceType);
            BluetoothStatusText.Text = Loc.Get("bluetooth.connected");
        }
        else
        {
            BluetoothDisconnectDeviceName.Text = device.Name;
            BluetoothDisconnectIcon.Data = GetBluetoothIconGeometry(device.DeviceType);
            BluetoothDisconnectStatusText.Text = Loc.Get("bluetooth.disconnected");
        }

        // Show the notification with animation
        AnimateBluetoothNotificationIn(connected);

        // Auto-dismiss after 3 seconds
        _bluetoothDismissTimer?.Stop();
        _bluetoothDismissTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _bluetoothDismissTimer.Tick -= BluetoothDismissTimer_Tick;
        _bluetoothDismissTimer.Tick += BluetoothDismissTimer_Tick;
        _bluetoothDismissTimer.Start();
    }

    private void BluetoothDismissTimer_Tick(object? sender, EventArgs e)
    {
        _bluetoothDismissTimer?.Stop();
        AnimateBluetoothNotificationOut();
    }

    private Grid ActiveBluetoothGrid => _currentBluetoothConnected ? BluetoothNotification : BluetoothDisconnectNotification;
    private TranslateTransform ActiveBluetoothTranslate => _currentBluetoothConnected ? BluetoothNotificationTranslate : BluetoothDisconnectNotificationTranslate;
    private ScaleTransform ActiveBluetoothIconScale => _currentBluetoothConnected ? BluetoothIconScale : BluetoothDisconnectIconScale;

    private void AnimateBluetoothNotificationIn(bool connected)
    {
        _isBluetoothNotificationVisible = true;

        // Hide privacy dot while bluetooth notification is visible
        SuppressPrivacyDot();

        // Hide the other notification if visible
        var otherGrid = connected ? BluetoothDisconnectNotification : BluetoothNotification;
        otherGrid.Visibility = Visibility.Collapsed;
        otherGrid.Opacity = 0;

        var activeGrid = ActiveBluetoothGrid;
        var activeTranslate = ActiveBluetoothTranslate;
        var activeIconScale = ActiveBluetoothIconScale;

        activeGrid.Visibility = Visibility.Visible;
        activeGrid.Opacity = 0;

        // Bounce the notch slightly to draw attention
        PlayBluetoothBounce();

        // Fade out collapsed content
        CollapsedContent.BeginAnimation(OpacityProperty, null);
        CollapsedContent.Opacity = 0;
        CollapsedContent.Visibility = Visibility.Collapsed;

        // Also hide music compact immediately to prevent flash
        if (MusicCompactContent.Visibility == Visibility.Visible)
        {
            MusicCompactContent.BeginAnimation(OpacityProperty, null);
            MusicCompactContent.Opacity = 0;
            MusicCompactContent.Visibility = Visibility.Collapsed;
        }

        // Also hide volume indicator if active
        if (_isVolumeIndicatorActive)
        {
            _volumeIndicatorHideTimer?.Stop();
            _isVolumeIndicatorActive = false;
            VolumeIndicatorContainer.BeginAnimation(OpacityProperty, null);
            VolumeIndicatorContainer.Opacity = 0;
            VolumeIndicatorContainer.Visibility = Visibility.Collapsed;
        }

        // Animate bluetooth notification in
        var fadeIn = MakeAnim(0d, 1d, _dur350, _easeExpOut7, TimeSpan.FromMilliseconds(100));
        activeGrid.BeginAnimation(OpacityProperty, fadeIn);

        // Slide up effect
        var slideUp = MakeAnim(6d, 0d, _dur350, _easeExpOut7, TimeSpan.FromMilliseconds(100));
        activeTranslate.BeginAnimation(
            TranslateTransform.YProperty, slideUp, HandoffBehavior.SnapshotAndReplace);

        // Bluetooth icon pulse animation
        var iconScale = MakeAnim(0.8d, 1d, _dur400, _easeSpring, TimeSpan.FromMilliseconds(150));
        activeIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, iconScale);
        activeIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, iconScale);
    }

    private void AnimateBluetoothNotificationOut()
    {
        var activeGrid = ActiveBluetoothGrid;
        var activeTranslate = ActiveBluetoothTranslate;

        var fadeOut = MakeAnim(1d, 0d, _dur250, _easePowerIn2, null);
        fadeOut.Completed += (s, e) =>
        {
            activeGrid.Visibility = Visibility.Collapsed;
            _isBluetoothNotificationVisible = false;

            // Restore privacy dot
            RestorePrivacyDotVisibility();

            // Restore previous content
            if (_isMusicCompactMode && _currentMediaInfo != null)
            {
                MusicCompactContent.Visibility = Visibility.Visible;
                var fadeInMusic = MakeAnim(0d, 1d, _dur250, _easePowerOut3, null);
                MusicCompactContent.BeginAnimation(OpacityProperty, fadeInMusic);
            }
            else
            {
                CollapsedContent.Visibility = Visibility.Visible;
                var fadeInCollapsed = MakeAnim(0d, 1d, _dur250, _easePowerOut3, null);
                CollapsedContent.BeginAnimation(OpacityProperty, fadeInCollapsed);
            }
        };

        activeGrid.BeginAnimation(OpacityProperty, fadeOut);

        var slideDown = MakeAnim(0d, -4d, _dur250, _easePowerIn2, null);
        activeTranslate.BeginAnimation(
            TranslateTransform.YProperty, slideDown, HandoffBehavior.SnapshotAndReplace);
    }
    private static Geometry GetBluetoothIconGeometry(BluetoothDeviceType deviceType)
    {
        return deviceType switch
        {
            BluetoothDeviceType.Headphones => Geometry.Parse(
                "M12,1C7,1,3,5,3,10v6c0,1.66,1.34,3,3,3h1c1.1,0,2-0.9,2-2v-4c0-1.1-0.9-2-2-2H5.17C5.6,6.17,8.48,3.5,12,3.5 s6.4,2.67,6.83,6.5H17c-1.1,0-2,0.9-2,2v4c0,1.1,0.9,2,2,2h1c1.66,0,3-1.34,3-3v-6C21,5,17,1,12,1z"),
            BluetoothDeviceType.Speaker => Geometry.Parse(
                "M3,9v6h4l5,5V4L7,9H3z M16.5,12c0-1.77-1.02-3.29-2.5-4.03v8.05C15.48,15.29,16.5,13.77,16.5,12z M14,3.23v2.06 c2.89,0.86,5,3.54,5,6.71s-2.11,5.85-5,6.71v2.06c4.01-0.91,7-4.49,7-8.77S18.01,4.14,14,3.23z"),
            BluetoothDeviceType.Keyboard => Geometry.Parse(
                "M20,5H4C2.9,5,2,5.9,2,7v10c0,1.1,0.9,2,2,2h16c1.1,0,2-0.9,2-2V7C22,5.9,21.1,5,20,5z M11,8h2v2h-2V8z M11,11h2v2h-2 V11z M8,8h2v2H8V8z M8,11h2v2H8V11z M7,13H5v-2h2V13z M7,10H5V8h2V10z M16,17H8v-2h8V17z M16,13h-2v-2h2V13z M16,10h-2V8h2V10z M19,13h-2v-2h2V13z M19,10h-2V8h2V10z"),
            BluetoothDeviceType.Mouse => Geometry.Parse(
                "M13,1.07V9h7c0-4.08-3.05-7.44-7-7.93z M4,15c0,4.42,3.58,8,8,8s8-3.58,8-8v-4H4V15z M11,1.07C7.05,1.56,4,4.92,4,9h7V1.07z"),
            BluetoothDeviceType.GameController => Geometry.Parse(
                "M21.58,16.09l-1.09-7.66C20.21,6.46,18.52,5,16.53,5H7.47C5.48,5,3.79,6.46,3.51,8.43l-1.09,7.66 C2.2,17.63,3.39,19,4.94,19c0.68,0,1.32-0.27,1.8-0.75L9,16h6l2.25,2.25c0.48,0.48,1.13,0.75,1.8,0.75 C20.61,19,21.8,17.63,21.58,16.09z M11,11H9v2H8v-2H6v-1h2V8h1v2h2V11z M15,10c-0.55,0-1-0.45-1-1s0.45-1,1-1s1,0.45,1,1 S15.55,10,15,10z M17,13c-0.55,0-1-0.45-1-1s0.45-1,1-1s1,0.45,1,1S17.55,13,17,13z"),
            BluetoothDeviceType.Phone => Geometry.Parse(
                "M17,1.01L7,1C5.9,1,5,1.9,5,3v18c0,1.1,0.9,2,2,2h10c1.1,0,2-0.9,2-2V3C19,1.9,18.1,1.01,17,1.01z M17,19H7V5h10V19z"),
            // Default: Bluetooth logo
            _ => Geometry.Parse(
                "M17.71,7.71L12,2h-1v7.59L6.41,5L5,6.41L10.59,12L5,17.59L6.41,19L11,14.41V22h1l5.71-5.71L13.41,12L17.71,7.71z M13,5.83l1.88,1.88L13,9.59V5.83z M14.88,16.29L13,18.17v-3.76L14.88,16.29z")
        };
    }

    private void PlayBluetoothBounce()
    {
        if (_isExpanded || _isAnimating) return;

        var durPeak = TimeSpan.FromMilliseconds(140);
        var durEnd = TimeSpan.FromMilliseconds(700);

        var bounceX = new DoubleAnimationUsingKeyFrames();
        bounceX.KeyFrames.Add(new EasingDoubleKeyFrame(1.08, KeyTime.FromTimeSpan(durPeak), _easeQuadOut));
        bounceX.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(durEnd), _easeSoftSpring));
        Timeline.SetDesiredFrameRate(bounceX, 144);

        var bounceY = new DoubleAnimationUsingKeyFrames();
        bounceY.KeyFrames.Add(new EasingDoubleKeyFrame(0.94, KeyTime.FromTimeSpan(durPeak), _easeQuadOut));
        bounceY.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(durEnd), _easeSoftSpring));
        Timeline.SetDesiredFrameRate(bounceY, 144);

        NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounceX);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounceY);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounceX);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounceY);
    }

    #endregion
}
