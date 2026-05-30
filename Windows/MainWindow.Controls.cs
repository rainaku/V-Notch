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

            // Immediately notify progress engine so bar stops/resumes without waiting for SMTC
            _progressEngine.NotifyUserPlayPause(_isPlaying);

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
                OptimisticPrepareForNextTrack();
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
                OptimisticPrepareForPreviousTrack();
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

            // Immediately notify progress engine so bar stops/resumes without waiting for SMTC
            _progressEngine.NotifyUserPlayPause(_isPlaying);

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
                OptimisticPrepareForNextTrack();
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
                OptimisticPrepareForPreviousTrack();
                await _mediaService.PreviousTrackAsync();
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-CTRL", ex, "InlinePrev failed");
        }
    }

    private void OptimisticPrepareForPreviousTrack()
    {
        try
        {
            _allowProgressBackwardRenderUntil = DateTime.Now.AddSeconds(3);
            _suppressExternalSeekDetectionUntil = DateTime.Now.AddSeconds(3);
            _progressEngine.NotifyUserSeek(TimeSpan.Zero);

            // Animate the rewind so the user sees the bar slide back to 0 instead of snapping
            AnimateProgressRewindTo(0);
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("PROGRESS-PREV-PREP", ex.Message);
        }
    }

    private void OptimisticPrepareForNextTrack()
    {
        try
        {
            // Freeze the progress bar at its current position to prevent the "jump to end"
            // glitch that occurs when the media player briefly reports position=duration
            // before the track change is detected.
            var frame = _progressEngine.GetUiFrame();
            _progressEngine.NotifyUserSeek(frame.Position);
            _suppressExternalSeekDetectionUntil = DateTime.Now.AddSeconds(3);
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("PROGRESS-NEXT-PREP", ex.Message);
        }
    }

    private void ThumbnailBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenCurrentMediaSourceFromThumbnail();
    }

    private void CompactThumbnailBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // If gesture controls are enabled and media is playing in collapsed mode,
        // let the gesture system handle this (swipe to skip track).
        // The gesture system will call ExpandNotch via ToggleNotchFromClick if it was just a tap.
        if (_settings.EnableGestureControls && !_isExpanded && !_isMusicExpanded &&
            _currentMediaInfo != null && _currentMediaInfo.IsAnyMediaPlaying && !_isAnimating)
        {
            // Don't handle here — let event bubble up to NotchWrapper for gesture tracking
            return;
        }

        e.Handled = true;
        // Click on compact thumbnail (hover state) → expand the notch
        if (!_isExpanded && !_isAnimating)
        {
            ExpandNotch();
        }
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
            // Enable bitmap caching during scale animation to prevent sub-pixel jitter
            button.CacheMode ??= new System.Windows.Media.BitmapCache(1.5);

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

            // Clear bitmap cache after animation completes to save memory
            animX.Completed += (_, _) =>
            {
                if (!button.IsMouseOver)
                    button.CacheMode = null;
            };

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
    private int _volumeIndicatorToken = 0;
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
    private void ShowVolumeIndicator(float volume)
    {
        if (VolumeIndicatorContainer == null || VolumeIndicatorFill == null) return;
        if (!_isMusicCompactMode) return;
        // CRITICAL: never run the compact-mode volume UI when the notch is expanded
        if (_isExpanded || _isAnimating) return;

        // ─── Arbiter: try to acquire the volume slot ───
        // - If charging / bluetooth / greeting is showing → reject (those have higher priority).
        // - If clipboard is showing → preempt and continue.
        // - If volume is already showing → returns Won (refresh case).
        if (!_isVolumeIndicatorActive)
        {
            if (!TryAcquireCompactSlot(VNotch.Controllers.CompactPillSlot.Volume, out int token))
            {
                return;
            }
            _volumeIndicatorToken = token;
        }

        // ─── First time showing: hide compact content ───
        if (!_isVolumeIndicatorActive)
        {
            _isVolumeIndicatorActive = true;

            // Hide privacy dot while volume bar is active
            SuppressPrivacyDot();

            // Reset thumbnail hover state if active (animate back smoothly)
            if (_isCompactThumbnailHovered)
            {
                _isCompactThumbnailHovered = false;
                _compactThumbnailHoverLeaveTimer.Stop();

                // Animate notch size from hover → collapsed+20 (volume expanded size)
                NotchBorder.BeginAnimation(HeightProperty, null);
                AnimateCompactWidth(_collapsedWidth + 20, _dur400, _easeExpOut6, _volumeIndicatorToken);
                var heightAnim = MakeAnim(_collapsedHeight, _dur400, _easeExpOut6, 144);
                NotchBorder.BeginAnimation(HeightProperty, heightAnim);

                // Animate thumbnail scale back to 1
                var thumbScaleAnim = MakeAnim(1.0, _dur350, _easeExpOut6, 144);
                CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, thumbScaleAnim);
                CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, thumbScaleAnim);

                // Fade out hover info
                CompactHoverInfo.BeginAnimation(OpacityProperty, null);
                var hoverFadeOut = MakeAnim(0.0, _dur200, _easeQuadOut);
                hoverFadeOut.Completed += (s, e) => CompactHoverInfo.Visibility = Visibility.Collapsed;
                CompactHoverInfo.BeginAnimation(OpacityProperty, hoverFadeOut);

                // Animate corner radius back
                AnimateCornerRadius(_cornerRadiusCollapsed, TimeSpan.FromMilliseconds(400));
            }
            else
            {
                // Normal case: expand notch slightly for volume bar
                AnimateCompactWidth(_collapsedWidth + 20, _dur350, _easeExpOut6, _volumeIndicatorToken);
            }

            // Set initial fill width immediately (no animation from 0)
            double initContainerWidth = _collapsedWidth - 32;
            VolumeIndicatorFill.Width = Math.Max(0, initContainerWidth * volume);

            // Fade out MusicViz + thumbnail smoothly
            MusicViz.BeginAnimation(OpacityProperty, null);
            var vizOut = MakeAnim(1.0, 0.0, _dur200, _easeQuadOut);
            vizOut.Completed += (s, e) =>
            {
                if (_isVolumeIndicatorActive)
                    MusicViz.Visibility = Visibility.Collapsed;
            };
            MusicViz.BeginAnimation(OpacityProperty, vizOut);

            CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
            var thumbOut = MakeAnim(1.0, 0.0, _dur200, _easeQuadOut);
            thumbOut.Completed += (s, e) =>
            {
                if (_isVolumeIndicatorActive)
                    CompactThumbnailBorder.Visibility = Visibility.Collapsed;
            };
            CompactThumbnailBorder.BeginAnimation(OpacityProperty, thumbOut);

            // Show indicator container with fade in (slightly delayed)
            VolumeIndicatorContainer.Visibility = Visibility.Visible;
            VolumeIndicatorContainer.Opacity = 0;
            VolumeIndicatorContainer.BeginAnimation(OpacityProperty, null);
            var indicatorIn = MakeAnim(0.0, 1.0, _dur250, _easeQuadOut);
            VolumeIndicatorContainer.BeginAnimation(OpacityProperty, indicatorIn);
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
    private void HideVolumeIndicator()
    {
        if (VolumeIndicatorContainer == null) return;
        int token = _volumeIndicatorToken;

        _isVolumeIndicatorActive = false;
        _volumeSynced = false;
        _compactPillArbiter.Release(token);
        _volumeIndicatorToken = 0;

        // Restore privacy dot
        RestorePrivacyDotVisibility();

        // If the notch is expanded (user opened it while volume indicator was visible), don't drive the compact-mode shrink animation — that would collapse the expanded view's width mid-flight
        if (_isExpanded || _isAnimating)
        {
            VolumeIndicatorContainer.BeginAnimation(OpacityProperty, null);
            VolumeIndicatorContainer.Opacity = 0;
            VolumeIndicatorContainer.Visibility = Visibility.Collapsed;
            return;
        }

        // Notch shrink back to collapsed width via the arbitered width helper.
        AnimateCompactWidth(_collapsedWidth, _dur350, _easeExpOut6, 0);

        // Fade out indicator
        VolumeIndicatorContainer.BeginAnimation(OpacityProperty, null);
        var fadeOut = MakeAnim(1.0, 0.0, _dur250, _easeQuadOut);
        fadeOut.Completed += (s, e) =>
        {
            if (!_isVolumeIndicatorActive)
                VolumeIndicatorContainer.Visibility = Visibility.Collapsed;
        };
        VolumeIndicatorContainer.BeginAnimation(OpacityProperty, fadeOut);

        // Restore thumbnail (only if clipboard notification is not active)
        if (!_isClipboardPeekActive)
        {
            CompactThumbnailBorder.Visibility = Visibility.Visible;
            CompactThumbnailBorder.Opacity = 0;
            CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
            var thumbIn = MakeAnim(0.0, 1.0, _dur250, _easeQuadOut);
            CompactThumbnailBorder.BeginAnimation(OpacityProperty, thumbIn);
        }

        // Restore MusicViz (only if clipboard notification is not active)
        if (!_isClipboardPeekActive)
        {
            MusicViz.Visibility = Visibility.Visible;
            MusicViz.Opacity = 0;
            MusicViz.BeginAnimation(OpacityProperty, null);
            var vizIn = MakeAnim(0.0, 1.0, _dur100, _easeQuadOut);
            MusicViz.BeginAnimation(OpacityProperty, vizIn);
        }
    }

    // Instantly clears the volume indicator state without the compact shrink animation.
    // Used when the user clicks to expand the notch while the volume bar is showing —
    // the expand animation will take over the notch sizing.
    private void DismissVolumeIndicatorImmediate()
    {
        _volumeIndicatorHideTimer?.Stop();
        int token = _volumeIndicatorToken;

        _isVolumeIndicatorActive = false;
        _volumeSynced = false;
        _isDraggingVolumeIndicator = false;
        _compactPillArbiter.Release(token);
        _volumeIndicatorToken = 0;

        if (VolumeIndicatorContainer != null)
        {
            VolumeIndicatorContainer.ReleaseMouseCapture();
            VolumeIndicatorContainer.BeginAnimation(OpacityProperty, null);
            VolumeIndicatorContainer.Opacity = 0;
            VolumeIndicatorContainer.Visibility = Visibility.Collapsed;
        }

        // Clear the held fade-out animations from ShowVolumeIndicator. These hold
        // opacity at 0 (FillBehavior.HoldEnd); if left active, the expand/collapse
        // completion handlers' local Opacity = 1 assignment loses to the animation
        // and the thumbnail stays invisible after returning to the compact pill.
        if (CompactThumbnailBorder != null)
        {
            CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
        }
        if (MusicViz != null)
        {
            MusicViz.BeginAnimation(OpacityProperty, null);
        }

        RestorePrivacyDotVisibility();
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

        // Apply to system on thread pool (non-blocking) for real-time responsiveness
        float volumeToSet = newVolume;
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            _mediaService.TrySetCurrentSessionVolume(volumeToSet);
        });
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
