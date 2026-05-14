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
using static VNotch.Services.Win32Interop;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    #region Media Controls

    private bool _isPlaying = true;

    private async void PlayPauseButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        try
        {
            if ((DateTime.Now - _lastMediaActionTime).TotalMilliseconds < 500) return;
            _lastMediaActionTime = DateTime.Now;

            _isPlaying = !_isPlaying;
            UpdatePlayPauseIcon();
            PlayButtonPressAnimation(PlayPauseButton);

            await _mediaService.PlayPauseAsync();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-CTRL", ex, "PlayPause failed");
        }
    }

    private async void NextButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        try
        {
            if ((DateTime.Now - _lastMediaActionTime).TotalMilliseconds < 500) return;
            _lastMediaActionTime = DateTime.Now;

            PlayButtonPressAnimation(NextButton);
            PlayNextSkipAnimation();

            if (_currentMediaInfo?.IsVideoSource == true)
            {
                await SeekRelative(15);
            }
            else
            {
                await _mediaService.NextTrackAsync();
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-CTRL", ex, "NextTrack failed");
        }
    }

    private async void PrevButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        try
        {
            if ((DateTime.Now - _lastMediaActionTime).TotalMilliseconds < 500) return;
            _lastMediaActionTime = DateTime.Now;

            PlayButtonPressAnimation(PrevButton);
            PlayPrevSkipAnimation();

            if (_currentMediaInfo?.IsVideoSource == true)
            {
                await SeekRelative(-15);
            }
            else
            {
                await _mediaService.PreviousTrackAsync();
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-CTRL", ex, "PrevTrack failed");
        }
    }

    private void UpdatePlayPauseIcon()
    {
        var duration = TimeSpan.FromMilliseconds(180);

        if (_isPlaying)
        {
            AnimateIconSwitch(PlayIcon, PauseIcon, duration, _easeQuadInOut);
            AnimateIconSwitch(InlinePlayIcon, InlinePauseIcon, duration, _easeQuadInOut);
        }
        else
        {
            AnimateIconSwitch(PauseIcon, PlayIcon, duration, _easeQuadInOut);
            AnimateIconSwitch(InlinePauseIcon, InlinePlayIcon, duration, _easeQuadInOut);
        }
    }

    private async void InlinePlayPauseButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        try
        {
            if ((DateTime.Now - _lastMediaActionTime).TotalMilliseconds < 500) return;
            _lastMediaActionTime = DateTime.Now;

            _isPlaying = !_isPlaying;
            UpdatePlayPauseIcon();
            PlayButtonPressAnimation(InlinePlayPauseButton);

            await _mediaService.PlayPauseAsync();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-CTRL", ex, "InlinePlayPause failed");
        }
    }

    private async void InlineNextButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        try
        {
            if ((DateTime.Now - _lastMediaActionTime).TotalMilliseconds < 500) return;
            _lastMediaActionTime = DateTime.Now;

            PlayButtonPressAnimation(InlineNextButton);
            PlayNextSkipAnimation(InlineNextArrow0, InlineNextArrow1, InlineNextArrow2);

            if (_currentMediaInfo?.IsVideoSource == true)
            {
                await SeekRelative(15);
            }
            else
            {
                await _mediaService.NextTrackAsync();
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-CTRL", ex, "InlineNext failed");
        }
    }

    private async void InlinePrevButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        try
        {
            if ((DateTime.Now - _lastMediaActionTime).TotalMilliseconds < 500) return;
            _lastMediaActionTime = DateTime.Now;

            PlayButtonPressAnimation(InlinePrevButton);
            PlayPrevSkipAnimation(InlinePrevArrow0, InlinePrevArrow1, InlinePrevArrow2);

            if (_currentMediaInfo?.IsVideoSource == true)
            {
                await SeekRelative(-15);
            }
            else
            {
                await _mediaService.PreviousTrackAsync();
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-CTRL", ex, "InlinePrev failed");
        }
    }

    private void ThumbnailBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenCurrentMediaSourceFromThumbnail();
    }

    private void CompactThumbnailBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenCurrentMediaSourceFromThumbnail();
    }

    private void OpenCurrentMediaSourceFromThumbnail()
    {
        var info = _currentMediaInfo;
        if (info == null || !info.IsAnyMediaPlaying) return;
        Task.Run(() => MediaWindowActivator.TryActivateForMedia(info))
            .SafeFireAndForget("MEDIA-ACTIVATE");
    }

    private void SendMediaKey(byte key)
    {
        keybd_event(key, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        keybd_event(key, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private void MediaButton_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border button && button.RenderTransform is ScaleTransform transform)
        {
            var animX = new DoubleAnimation(transform.ScaleX, 1.18, _dur150) { EasingFunction = _easeQuadOut };
            var animY = new DoubleAnimation(transform.ScaleY, 1.18, _dur150) { EasingFunction = _easeQuadOut };
            Timeline.SetDesiredFrameRate(animX, 120);
            Timeline.SetDesiredFrameRate(animY, 120);
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
        }
    }

    private void MediaButton_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border button && button.RenderTransform is ScaleTransform transform)
        {
            var animX = new DoubleAnimation(transform.ScaleX, 1.0, _dur200) { EasingFunction = _easeQuadOut };
            var animY = new DoubleAnimation(transform.ScaleY, 1.0, _dur200) { EasingFunction = _easeQuadOut };
            Timeline.SetDesiredFrameRate(animX, 120);
            Timeline.SetDesiredFrameRate(animY, 120);
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
        }
    }

    #endregion

    #region Volume Control

    private float _currentVolume = 0.5f;
    private const float VolumeScrollStep = 0.05f; // 5% per scroll tick
    private DispatcherTimer? _volumeIndicatorHideTimer;
    private bool _isVolumeIndicatorActive = false;
    private bool _volumeSynced = false;

    /// <summary>
    /// Adjusts system volume by scroll delta. Proportional to scroll amount for smooth control.
    /// </summary>
    private void AdjustVolumeByScroll(int delta)
    {
        // Sync current volume from system only once per scroll session
        if (!_volumeSynced)
        {
            if (_mediaService.TryGetCurrentSessionVolume(out float vol, out _))
            {
                _currentVolume = vol;
            }
            _volumeSynced = true;
        }

        // Proportional: 120 delta (one tick) = VolumeScrollStep
        float step = (delta / 120f) * VolumeScrollStep;
        float newVolume = Math.Clamp(_currentVolume + step, 0f, 1f);
        _currentVolume = newVolume;

        // Update visual indicator instantly
        ShowVolumeIndicator(newVolume);

        // Apply volume to system on thread pool (non-blocking)
        float volumeToSet = newVolume;
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            _mediaService.TrySetCurrentSessionVolume(volumeToSet);
        });

        // Update expanded UI if visible
        if (_isExpanded)
        {
            VolumeBarScale.ScaleX = newVolume;
            UpdateVolumeIcon(newVolume, false);
        }
    }

    /// <summary>
    /// Shows the volume indicator bar on the notch (only in compact music pill mode).
    /// Hides compact content (thumbnail + equalizer) with animation, shows indicator.
    /// Auto-hides after 1.2 seconds of inactivity.
    /// </summary>
    private void ShowVolumeIndicator(float volume)
    {
        if (VolumeIndicatorContainer == null || VolumeIndicatorFill == null) return;
        if (!_isMusicCompactMode) return;

        // ─── First time showing: hide compact content ───
        if (!_isVolumeIndicatorActive)
        {
            _isVolumeIndicatorActive = true;

            // Set initial fill width immediately (no animation from 0)
            double initContainerWidth = _collapsedWidth - 32;
            VolumeIndicatorFill.Width = Math.Max(0, initContainerWidth * volume);

            // Hide MusicViz + thumbnail instantly to avoid animation conflicts during scroll
            MusicViz.BeginAnimation(OpacityProperty, null);
            MusicViz.Opacity = 0;
            MusicViz.Visibility = Visibility.Collapsed;

            CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
            CompactThumbnailBorder.Opacity = 0;
            CompactThumbnailBorder.Visibility = Visibility.Collapsed;

            // Show indicator container with fade in
            VolumeIndicatorContainer.Visibility = Visibility.Visible;
            VolumeIndicatorContainer.Opacity = 1;
            VolumeIndicatorContainer.BeginAnimation(OpacityProperty, null);

            // Notch expand slightly
            NotchBorder.BeginAnimation(WidthProperty, null);
            var expandAnim = MakeAnim(_collapsedWidth, _collapsedWidth + 20, _dur350, _easeExpOut6);
            expandAnim.FillBehavior = FillBehavior.Stop;
            Timeline.SetDesiredFrameRate(expandAnim, 144);
            expandAnim.Completed += (s, e) =>
            {
                if (_isVolumeIndicatorActive)
                {
                    NotchBorder.BeginAnimation(WidthProperty, null);
                    NotchBorder.Width = _collapsedWidth + 20;
                }
            };
            NotchBorder.BeginAnimation(WidthProperty, expandAnim);
        }

        // ─── Update fill width directly — instant, no animation ───
        double containerWidth = VolumeIndicatorContainer.ActualWidth;
        if (containerWidth <= 0)
        {
            containerWidth = _collapsedWidth - 32;
        }
        VolumeIndicatorFill.Width = Math.Max(0, containerWidth * volume);

        // ─── Reset hide timer (reuse instance) ───
        if (_volumeIndicatorHideTimer == null)
        {
            _volumeIndicatorHideTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1200)
            };
            _volumeIndicatorHideTimer.Tick += (s, e) =>
            {
                _volumeIndicatorHideTimer.Stop();
                HideVolumeIndicator();
            };
        }
        _volumeIndicatorHideTimer.Stop();
        _volumeIndicatorHideTimer.Start();
    }

    /// <summary>
    /// Hides volume indicator and restores compact content with reversed animation.
    /// </summary>
    private void HideVolumeIndicator()
    {
        if (VolumeIndicatorContainer == null) return;
        _isVolumeIndicatorActive = false;
        _volumeSynced = false;

        // Notch shrink back to collapsed width
        NotchBorder.BeginAnimation(WidthProperty, null);
        var shrinkAnim = MakeAnim(_collapsedWidth + 20, _collapsedWidth, _dur350, _easeExpOut6);
        shrinkAnim.FillBehavior = FillBehavior.Stop;
        Timeline.SetDesiredFrameRate(shrinkAnim, 144);
        shrinkAnim.Completed += (s, e) =>
        {
            NotchBorder.BeginAnimation(WidthProperty, null);
            NotchBorder.Width = _collapsedWidth;
        };
        NotchBorder.BeginAnimation(WidthProperty, shrinkAnim);

        // Fade out indicator
        VolumeIndicatorContainer.BeginAnimation(OpacityProperty, null);
        var fadeOut = MakeAnim(1.0, 0.0, _dur250, _easeQuadOut);
        fadeOut.Completed += (s, e) =>
        {
            if (!_isVolumeIndicatorActive)
                VolumeIndicatorContainer.Visibility = Visibility.Collapsed;
        };
        VolumeIndicatorContainer.BeginAnimation(OpacityProperty, fadeOut);

        // Restore thumbnail
        CompactThumbnailBorder.Visibility = Visibility.Visible;
        CompactThumbnailBorder.Opacity = 0;
        CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
        var thumbIn = MakeAnim(0.0, 1.0, _dur250, _easeQuadOut);
        CompactThumbnailBorder.BeginAnimation(OpacityProperty, thumbIn);

        // Restore MusicViz
        MusicViz.Visibility = Visibility.Visible;
        MusicViz.Opacity = 0;
        MusicViz.BeginAnimation(OpacityProperty, null);
        var vizIn = MakeAnim(0.0, 1.0, _dur100, _easeQuadOut);
        MusicViz.BeginAnimation(OpacityProperty, vizIn);
    }

    #region Volume Indicator Drag

    private bool _isDraggingVolumeIndicator = false;

    private void VolumeIndicator_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _isDraggingVolumeIndicator = true;
        VolumeIndicatorContainer.CaptureMouse();
        SetVolumeFromIndicatorPosition(e);

        // Stop the hide timer while dragging
        _volumeIndicatorHideTimer?.Stop();
    }

    private void VolumeIndicator_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingVolumeIndicator || e.LeftButton != MouseButtonState.Pressed) return;
        e.Handled = true;
        SetVolumeFromIndicatorPosition(e);
    }

    private void VolumeIndicator_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingVolumeIndicator) return;
        e.Handled = true;
        _isDraggingVolumeIndicator = false;
        VolumeIndicatorContainer.ReleaseMouseCapture();

        // Restart hide timer
        _volumeIndicatorHideTimer?.Stop();
        _volumeIndicatorHideTimer?.Start();
    }

    private void SetVolumeFromIndicatorPosition(MouseEventArgs e)
    {
        var pos = e.GetPosition(VolumeIndicatorContainer);
        double containerWidth = VolumeIndicatorContainer.ActualWidth;
        if (containerWidth <= 0) containerWidth = _collapsedWidth - 32;

        float newVolume = (float)Math.Clamp(pos.X / containerWidth, 0.0, 1.0);
        _currentVolume = newVolume;

        // Update visual
        VolumeIndicatorFill.Width = Math.Max(0, containerWidth * newVolume);

        // Apply to system on thread pool
        float volumeToSet = newVolume;
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            _mediaService.TrySetCurrentSessionVolume(volumeToSet);
        });

        if (_isExpanded)
        {
            VolumeBarScale.ScaleX = newVolume;
            UpdateVolumeIcon(newVolume, false);
        }
    }

    #endregion

    private void VolumeIcon_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_mediaService.TryToggleCurrentSessionMute())
        {
            SyncVolumeFromActiveSession();
        }
    }

    private void VolumeIcon_MouseEnter(object sender, MouseEventArgs e)
    {
        var animX = new DoubleAnimation(1, 1.2, _dur150) { EasingFunction = _easeQuadOut };
        var animY = new DoubleAnimation(1, 1.2, _dur150) { EasingFunction = _easeQuadOut };
        VolumeIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
        VolumeIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
    }

    private void VolumeIcon_MouseLeave(object sender, MouseEventArgs e)
    {
        var animX = new DoubleAnimation(VolumeIconScale.ScaleX, 1, _dur150) { EasingFunction = _easeQuadOut };
        var animY = new DoubleAnimation(VolumeIconScale.ScaleY, 1, _dur150) { EasingFunction = _easeQuadOut };
        VolumeIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
        VolumeIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
    }

    private void VolumeBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _isDraggingVolume = true;
        VolumeBarContainer.CaptureMouse();
        SetVolumeFromMousePosition(e);
    }

    private void VolumeBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDraggingVolume && e.LeftButton == MouseButtonState.Pressed)
        {
            SetVolumeFromMousePosition(e);
        }
    }

    private void VolumeBar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingVolume)
        {
            _isDraggingVolume = false;
            VolumeBarContainer.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void SetVolumeFromMousePosition(MouseEventArgs e)
    {
        const double volumeBarWidth = 100.0;
        var pos = e.GetPosition(VolumeBarContainer);
        float newVolume = (float)Math.Clamp(pos.X / volumeBarWidth, 0.0, 1.0);

        _currentVolume = newVolume;
        VolumeBarScale.ScaleX = newVolume;
        UpdateVolumeIcon(newVolume, false);

        _mediaService.TrySetCurrentSessionVolume(newVolume);
    }

    private void SyncVolumeFromActiveSession()
    {
        if (_isDraggingVolume) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isDraggingVolume) return;

            if (_mediaService.TryGetCurrentSessionVolume(out float volume, out bool isMuted))
            {
                _currentVolume = volume;
                VolumeBarScale.ScaleX = _currentVolume;
                UpdateVolumeIcon(_currentVolume, isMuted);
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void UpdateVolumeIcon(float volume, bool isMuted)
    {
        if (isMuted || volume <= 0.01f)
        {
            VolumeIcon.Text = "\uE74F";
        }
        else if (volume < 0.33f)
        {
            VolumeIcon.Text = "\uE993";
        }
        else if (volume < 0.66f)
        {
            VolumeIcon.Text = "\uE994";
        }
        else
        {
            VolumeIcon.Text = "\uE995";
        }
    }

    #endregion
}
