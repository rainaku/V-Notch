using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

/// <summary>
/// Update-check polling + "new version available" badge rendering.
/// Split out of <see cref="MainWindow"/> xaml.cs for readability. Still a
/// partial because it drives several named XAML elements directly
/// (UpdateNotificationButton, UpdateIconBrush, UpdateInlineTooltip, etc.).
/// </summary>
public partial class MainWindow
{
    #region Update Notification Handlers

    private async void UpdateCheckTimer_Tick(object? sender, EventArgs e)
    {
        if (_updateService == null) return;
        await CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var updateInfo = await _updateService.CheckForUpdatesAsync();

            if (updateInfo != null && updateInfo.IsNewerVersion)
            {
                _isUpdateAvailable = true;
                _availableUpdate = updateInfo;
                ShowUpdateNotification();
            }
            else
            {
                _isUpdateAvailable = false;
                _availableUpdate = null;
                HideUpdateNotification();
            }
        }
        catch
        {
            // Silently fail - don't interrupt user experience
        }
    }

    private void ShowUpdateNotification()
    {
        if (_availableUpdate == null || !_availableUpdate.IsNewerVersion)
        {
            _isUpdateAvailable = false;
            HideUpdateNotification();
            return;
        }

        bool wasVisible = UpdateNotificationButton.Visibility == Visibility.Visible;
        UpdateNotificationButton.Visibility = Visibility.Visible;
        UpdateNotificationButton.IsHitTestVisible = true;
        UpdateNotificationButton.Tag = $"Version v{_availableUpdate?.Version?.ToString() ?? "-"}";
        UpdateNotificationButton.Cursor = Cursors.Hand;
        UpdateNotificationButton.Opacity = 1.0;
        SetUpdateInlineTooltipContent(
            $"Version v{_availableUpdate?.Version?.ToString() ?? "-"}",
            "Click to download & install");

        if (wasVisible)
        {
            if (!_isUpdateInstalling)
            {
                StartUpdatePulseAnimation();
            }
            return;
        }

        UpdateIconBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        UpdateIconBrush.Color = Color.FromRgb(48, 209, 88);

        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(400),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var slideIn = new DoubleAnimation
        {
            From = -4,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(400),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        fadeIn.Completed += (s, e) =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                StartUpdatePulseAnimation();
            }), DispatcherPriority.Render);
        };

        UpdateNotificationButton.BeginAnimation(OpacityProperty, fadeIn);
        UpdateNotificationTranslate.BeginAnimation(TranslateTransform.YProperty, slideIn);
    }

    private void HideUpdateNotification()
    {
        HideUpdateInlineTooltip();

        if (UpdateNotificationButton.Visibility == Visibility.Collapsed)
            return;

        StopUpdatePulseAnimation();
        UpdateNotificationButton.IsHitTestVisible = false;
        UpdateNotificationButton.Cursor = Cursors.Hand;

        var fadeOut = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(300),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        fadeOut.Completed += (s, e) =>
        {
            UpdateNotificationButton.Visibility = Visibility.Collapsed;
        };

        UpdateNotificationButton.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void StartUpdatePulseAnimation()
    {
        if (UpdateIconBrush == null) return;

        UpdateIconBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        UpdateIconBrush.Color = Color.FromRgb(48, 209, 88);

        if (_updatePulseTimer == null)
        {
            _updatePulseTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(33) // ~30fps is sufficient for color pulse
            };
            _updatePulseTimer.Tick += UpdatePulseTimer_Tick;
        }

        _updatePulseStartedAtUtc = DateTime.UtcNow;
        _updatePulseTimer.Start();
    }

    private void StopUpdatePulseAnimation()
    {
        if (_updatePulseTimer != null)
        {
            _updatePulseTimer.Stop();
        }

        if (UpdateIconBrush != null)
        {
            UpdateIconBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            UpdateIconBrush.Color = Color.FromRgb(48, 209, 88);
        }
    }

    private void UpdatePulseTimer_Tick(object? sender, EventArgs e)
    {
        if (UpdateIconBrush == null) return;

        if (!_isUpdateAvailable || UpdateNotificationButton.Visibility != Visibility.Visible)
        {
            StopUpdatePulseAnimation();
            return;
        }

        const double periodMs = 3000.0;
        var elapsedMs = (DateTime.UtcNow - _updatePulseStartedAtUtc).TotalMilliseconds;
        var phase = (elapsedMs % periodMs) / periodMs;
        var mix = 0.5 - (0.5 * Math.Cos(phase * Math.PI * 2.0));

        byte r = (byte)Math.Round(48 + ((255 - 48) * mix));
        byte g = (byte)Math.Round(209 + ((255 - 209) * mix));
        byte b = (byte)Math.Round(88 + ((255 - 88) * mix));
        UpdateIconBrush.Color = Color.FromRgb(r, g, b);
    }

    private async void UpdateNotification_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        if (_availableUpdate == null || _isUpdateInstalling) return;

        _isUpdateInstalling = true;
        UpdateNotificationButton.Tag = "Preparing download...";
        UpdateNotificationButton.Cursor = Cursors.Wait;
        UpdateNotificationButton.Opacity = 0.95;
        StopUpdatePulseAnimation();
        SetUpdateInlineTooltipContent("Preparing download...", "Opening updater...");
        ShowUpdateInlineTooltip();

        var updateProgressWindow = new UpdateDownloadWindow();
        updateProgressWindow.SetIndeterminate("Preparing download...");
        updateProgressWindow.Show();

        var downloadProgress = new Progress<double>(p =>
        {
            if (p < 0)
            {
                updateProgressWindow.SetIndeterminate("Downloading update...");
                UpdateNotificationButton.Tag = "Downloading update...";
                SetUpdateInlineTooltipContent("Downloading update...", "Please wait...");
                return;
            }

            updateProgressWindow.SetStatus($"Downloading update... {p:0}%");
            updateProgressWindow.SetProgress(p);
            UpdateNotificationButton.Tag = $"Downloading... {p:0}%";
            SetUpdateInlineTooltipContent($"Downloading... {p:0}%", "Please wait...");
        });

        try
        {
            var installed = await _updateService.DownloadAndInstallUpdateAsync(_availableUpdate, downloadProgress);

            if (!installed)
            {
                updateProgressWindow.Close();
                _isUpdateInstalling = false;
                UpdateNotificationButton.Tag = $"Version v{_availableUpdate.Version}";
                UpdateNotificationButton.Cursor = Cursors.Hand;
                UpdateNotificationButton.Opacity = 1.0;
                SetUpdateInlineTooltipContent(
                    $"Version v{_availableUpdate.Version}",
                    "Click to download & install");
                StartUpdatePulseAnimation();
                MessageBox.Show(
                    "Unable to download/install update right now. Please try again.",
                    "Update Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch
        {
            updateProgressWindow.Close();
            _isUpdateInstalling = false;
            UpdateNotificationButton.Tag = $"Version v{_availableUpdate?.Version?.ToString() ?? "-"}";
            UpdateNotificationButton.Cursor = Cursors.Hand;
            UpdateNotificationButton.Opacity = 1.0;
            SetUpdateInlineTooltipContent(
                $"Version v{_availableUpdate?.Version?.ToString() ?? "-"}",
                "Click to download & install");
            StartUpdatePulseAnimation();
            MessageBox.Show(
                "Update process failed unexpectedly. Please try again.",
                "Update Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void UpdateNotification_MouseEnter(object sender, MouseEventArgs e)
    {
        _suspendTopmostUntilUtc = DateTime.UtcNow.AddMilliseconds(1200);
        AnimateUpdateNotificationHover(true);
        ShowUpdateInlineTooltip();

        if (_availableUpdate != null && !_isUpdateInstalling)
        {
            UpdateNotificationButton.Tag = $"Version v{_availableUpdate.Version}";
            SetUpdateInlineTooltipContent(
                $"Version v{_availableUpdate.Version}",
                "Click to download & install");
        }
    }

    private void UpdateNotification_MouseLeave(object sender, MouseEventArgs e)
    {
        HideUpdateInlineTooltip();
        AnimateUpdateNotificationHover(false);
    }

    private void UpdateNotification_ToolTipOpening(object sender, ToolTipEventArgs e)
    {
        _isUpdateTooltipOpen = true;
        _suspendTopmostUntilUtc = DateTime.UtcNow.AddMilliseconds(1500);
    }

    private void UpdateNotification_ToolTipClosing(object sender, ToolTipEventArgs e)
    {
        _isUpdateTooltipOpen = false;
        _suspendTopmostUntilUtc = DateTime.UtcNow.AddMilliseconds(220);
    }

    private void AnimateUpdateNotificationHover(bool isEnter)
    {
        const int fps = 144;
        var scaleAnim = new DoubleAnimation
        {
            To = isEnter ? 1.10 : 1.0,
            Duration = TimeSpan.FromMilliseconds(isEnter ? 180 : 220),
            EasingFunction = isEnter
                ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.20 }
                : _easeQuadOut
        };

        Timeline.SetDesiredFrameRate(scaleAnim, fps);
        UpdateNotificationScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim, HandoffBehavior.SnapshotAndReplace);
        UpdateNotificationScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim, HandoffBehavior.SnapshotAndReplace);
    }

    private void SetUpdateInlineTooltipContent(string status, string hint)
    {
        UpdateInlineStatusText.Text = status;
        UpdateInlineHintText.Text = hint;
    }

    private void ShowUpdateInlineTooltip()
    {
        if (!_isUpdateAvailable || UpdateNotificationButton.Visibility != Visibility.Visible)
            return;

        _isUpdateTooltipOpen = true;
        _suspendTopmostUntilUtc = DateTime.UtcNow.AddMilliseconds(1200);
        UpdateInlineTooltip.BeginAnimation(OpacityProperty, null);
        UpdateInlineTooltip.Visibility = Visibility.Visible;

        var fadeIn = new DoubleAnimation
        {
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(140),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        UpdateInlineTooltip.BeginAnimation(OpacityProperty, fadeIn, HandoffBehavior.SnapshotAndReplace);
    }

    private void HideUpdateInlineTooltip()
    {
        _isUpdateTooltipOpen = false;
        _suspendTopmostUntilUtc = DateTime.UtcNow.AddMilliseconds(220);

        if (UpdateInlineTooltip.Visibility != Visibility.Visible) return;

        UpdateInlineTooltip.BeginAnimation(OpacityProperty, null);
        var fadeOut = new DoubleAnimation
        {
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(120),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        fadeOut.Completed += (_, _) =>
        {
            UpdateInlineTooltip.Visibility = Visibility.Collapsed;
            UpdateInlineTooltip.Opacity = 0;
        };

        UpdateInlineTooltip.BeginAnimation(OpacityProperty, fadeOut, HandoffBehavior.SnapshotAndReplace);
    }

    #endregion
}
