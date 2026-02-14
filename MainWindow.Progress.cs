using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Runtime.InteropServices;
using VNotch.Models;
using VNotch.Services;

namespace VNotch;

/// <summary>
/// Partial class for Progress Bar logic
/// </summary>
public partial class MainWindow
{
    private bool _isDraggingProgress = false; // Flag for drag-to-seek
    private TimeSpan _dragSeekPosition = TimeSpan.Zero; // Store seek position during drag

    private void ProgressTimer_Tick(object? sender, EventArgs e)
    {
        // 1. Update progress tracking for the UI
        if (_currentMediaInfo != null && _currentMediaInfo.IsAnyMediaPlaying)
        {
            if (_isExpanded)
            {
                RenderProgressBar();
            }
        }

        // 1.5 Update Volume UI (if not dragging) - sync from system volume
        if (_isExpanded && _isMusicExpanded && _volumeService != null && _volumeService.IsAvailable && !_isDraggingVolume)
        {
            _currentVolume = _volumeService.GetVolume();
            VolumeBarScale.ScaleX = _currentVolume;
        }
    }

    private DateTime _lastOutsideClickTime = DateTime.MinValue;
    private bool _wasLButtonDown = false;

    private void AutoCollapseTimer_Tick(object? sender, EventArgs e)
    {
        // 2. Auto-collapse logic (Essential for WS_EX_NOACTIVATE windows)
        if ((_isExpanded || _isMusicExpanded) && !_isAnimating)
        {
            // Detect mouse down using state tracking
            bool isDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;

            if (isDown && !_wasLButtonDown) // Transition: up -> down
            {
                if (GetCursorPos(out POINT pt))
                {
                    IntPtr hWndAtPoint = WindowFromPoint(pt);

                    // Check if click is NOT on this window or any of its child controls
                    if (hWndAtPoint != _hwnd && !IsChildWindow(_hwnd, hWndAtPoint))
                    {
                        if (_isSecondaryView)
                        {
                            // In menu 2, enforce double click to avoid closing while dragging files
                            var now = DateTime.Now;
                            double doubleClickTime = GetDoubleClickTime();

                            if ((now - _lastOutsideClickTime).TotalMilliseconds < doubleClickTime)
                            {
                                CollapseAll();
                                _lastOutsideClickTime = DateTime.MinValue;
                            }
                            else
                            {
                                _lastOutsideClickTime = now;
                            }
                        }
                        else
                        {
                            // In menu 1, single click outside is enough
                            CollapseAll();
                        }
                    }
                }
            }
            _wasLButtonDown = isDown;
        }
        else
        {
            _wasLButtonDown = false;
        }
    }

    private bool IsChildWindow(IntPtr parent, IntPtr child)
    {
        IntPtr current = child;
        while (current != IntPtr.Zero)
        {
            if (current == parent) return true;
            current = GetParent(current);
        }
        return false;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    private DateTime _lastTimelineAvailableTime = DateTime.MinValue;
    private string _lastSessionId = "";
    private string _lastProgressSignature = "";

    /// <summary>
    /// Called when MediaDetectionService fires MediaChanged event
    /// </summary>
    private void UpdateProgressTracking(MediaInfo info)
    {
        // Detect session switch for smooth UI transitions
        bool isSessionSwitch = !string.IsNullOrEmpty(info.SourceAppId) && info.SourceAppId != _lastSessionId;
        if (isSessionSwitch)
        {
            _lastSessionId = info.SourceAppId;
            HandleSessionTransition();
        }

        // IMPORTANT: Track changes must bypass seek-debounce.
        // When user seeks to the end and the player auto-advances, the next track often arrives within the debounce window.
        // If we return early, progress stays clamped at 100% and appears frozen.
        string signature = info.GetSignature();
        bool signatureChanged = !string.IsNullOrEmpty(signature) && signature != _lastProgressSignature;
        if (signatureChanged)
        {
            _lastProgressSignature = signature;

            // Allow immediate state refresh for the new track/session.
            _seekDebounceUntil = DateTime.MinValue;

            // Reset local timeline to whatever we have now (avoid carrying old duration/position).
            _lastKnownDuration = info.Duration.TotalSeconds > 0 ? info.Duration : TimeSpan.Zero;
            _lastKnownPosition = info.Position < TimeSpan.Zero ? TimeSpan.Zero : info.Position;
            _lastMediaUpdate = info.LastUpdated.LocalDateTime;
        }

        _currentMediaInfo = info;

        // Progress bar should ALWAYS be visible as per user request
        ProgressSection.Visibility = Visibility.Visible;
        ProgressSection.Opacity = 1;

        // Show/hide specific progress elements based on timeline availability
        bool showProgressDetails = info.IsAnyMediaPlaying && (info.HasTimeline || info.IsIndeterminate);

        if (showProgressDetails)
        {
            // IMPORTANT: If user is dragging, don't update internal position from SMTC.
            // If we are within seek-debounce, still allow updates when the track/session signature changed.
            if (_isDraggingProgress) return;
            if (DateTime.Now < _seekDebounceUntil)
            {
                string sigNow = info.GetSignature();
                bool changed = !string.IsNullOrEmpty(sigNow) && sigNow != _lastProgressSignature;
                if (!changed) return;

                _lastProgressSignature = sigNow;
                _seekDebounceUntil = DateTime.MinValue;
            }

            if (info.HasTimeline)
            {
                // ROUTE TO PLATFORM SPECIFIC LOGIC
                if (_isYouTubeVideoMode)
                {
                    UpdateYouTubeVideoProgress(info);
                }
                else if (info.MediaSource == "YouTube")
                {
                    UpdateYouTubeProgress(info);
                }
                else if (info.MediaSource == "Spotify")
                {
                    UpdateSpotifyProgress(info);
                }
                else
                {
                    UpdateGeneralProgress(info);
                }

                IndeterminateProgress.Visibility = Visibility.Collapsed;
                ProgressBar.Visibility = Visibility.Visible;
            }
            else if (info.IsIndeterminate)
            {
                IndeterminateProgress.Visibility = Visibility.Visible;
                ProgressBar.Visibility = Visibility.Collapsed;
                StartIndeterminateAnimation();
            }

            // Sync seek capability
            if (_isYouTubeVideoMode)
            {
                ProgressBarContainer.Cursor = Cursors.Hand;
            }
            else
            {
                ProgressBarContainer.Cursor = info.IsSeekEnabled ? Cursors.Hand : Cursors.Arrow;
            }

            UpdateProgressTimerState();
            _isMediaPlaying = info.IsPlaying;

            if (_isExpanded) RenderProgressBar();
        }
        else
        {
            // Case when no media is playing: Keep section visible but reset UI to zero
            UpdateProgressTimerState();
            _isMediaPlaying = false;
            _lastSessionId = "";

            // Ensure static progress bar is visible instead of indeterminate
            IndeterminateProgress.Visibility = Visibility.Collapsed;
            ProgressBar.Visibility = Visibility.Visible;
            ProgressBarContainer.Cursor = Cursors.Arrow;

            _lastKnownDuration = TimeSpan.Zero;
            _lastKnownPosition = TimeSpan.Zero;

            ResetProgressUI();
            if (_isExpanded) RenderProgressBar();
        }
    }

    private void HandleSessionTransition()
    {
        // Smooth transition effect
        var anim = new DoubleAnimation(0.2, 1.0, TimeSpan.FromMilliseconds(400))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        MediaWidget.BeginAnimation(OpacityProperty, anim);
    }

    private void StartIndeterminateAnimation()
    {
        if (IndeterminateProgress.Visibility != Visibility.Visible) return;

        var anim = new DoubleAnimation(0.3, 0.8, TimeSpan.FromSeconds(1))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        IndeterminateProgress.BeginAnimation(OpacityProperty, anim);
    }

    private void UpdateYouTubeVideoProgress(MediaInfo info)
    {
        // For YouTube Video Mode, we trust the local player events more than SMTC.
        // The events update _lastKnownPosition and _lastKnownDuration in MainWindow.xaml.cs
        // We don't overwrite them with potentially stale SMTC data here.
    }

    private void UpdateYouTubeProgress(MediaInfo info)
    {
        // If it's a new track or throttled, and we don't have a new duration yet, 
        // reset duration to prevent old track's duration from being used for the new track.
        bool isNewTrack = info.CurrentTrack != _lastAnimatedTrackSignature.Split('|')[0];
        if (isNewTrack || info.IsThrottled)
        {
            if (info.Duration.TotalSeconds > 0)
            {
                _lastKnownDuration = info.Duration;
            }
            else
            {
                // FORCE RESET: If we are synched/new track and no duration yet, 
                // we must reset everything to 0 to prevent "Stuck on old track" visual.
                _lastKnownDuration = TimeSpan.Zero;
                _lastKnownPosition = TimeSpan.Zero;
            }
        }
        else if (info.Duration.TotalSeconds > 0)
        {
            _lastKnownDuration = info.Duration;
        }

        // If throttled, always use the compensated position from info
        _lastKnownPosition = info.Position;
        _lastMediaUpdate = info.LastUpdated.LocalDateTime;
    }

    private void UpdateSpotifyProgress(MediaInfo info)
    {
        if (info.Duration.TotalSeconds > 0) _lastKnownDuration = info.Duration;

        _lastKnownPosition = info.Position;
        _lastMediaUpdate = info.LastUpdated.LocalDateTime;
    }

    private void UpdateGeneralProgress(MediaInfo info)
    {
        if (info.Duration.TotalSeconds > 0) _lastKnownDuration = info.Duration;

        _lastKnownPosition = info.Position;
        _lastMediaUpdate = info.LastUpdated.LocalDateTime;
    }

    private void ResetProgressUI()
    {
        ProgressBarScale.ScaleX = 0;
        CurrentTimeText.Text = "0:00";
        RemainingTimeText.Text = "0:00";
        IndeterminateProgress.BeginAnimation(OpacityProperty, null);
        IndeterminateProgress.Visibility = Visibility.Collapsed;
    }

    private void RenderProgressBar()
    {
        if (_isDraggingProgress || _currentMediaInfo == null) return;

        var duration = _lastKnownDuration;
        if (duration.TotalSeconds <= 0)
        {
            CurrentTimeText.Text = "--:--";
            RemainingTimeText.Text = "--:--";
            ProgressBarScale.ScaleX = 0;
            return;
        }
        TimeSpan displayPosition;

        if (_isMediaPlaying)
        {
            var timeSinceUpdate = DateTime.Now - _lastMediaUpdate;

            // For browser/YouTube sources, extrapolation needs a very high cap 
            // because browser SMTC updates are extremely infrequent (can be > 60s)
            // If throttled, we ignore the cap to keep progress moving locally.
            double capSeconds = (_currentMediaInfo.IsThrottled) ? 3600 :
                               (_currentMediaInfo.MediaSource == "YouTube" || _currentMediaInfo.MediaSource == "Browser") ? 600 : 30;

            if (timeSinceUpdate > TimeSpan.FromSeconds(capSeconds))
                timeSinceUpdate = TimeSpan.FromSeconds(capSeconds);

            displayPosition = _lastKnownPosition + TimeSpan.FromTicks((long)(timeSinceUpdate.Ticks * _currentMediaInfo.PlaybackRate));

            if (displayPosition > duration) displayPosition = duration;
        }
        else
        {
            displayPosition = _lastKnownPosition;
        }

        if (displayPosition < TimeSpan.Zero) displayPosition = TimeSpan.Zero;

        double ratio = displayPosition.TotalSeconds / duration.TotalSeconds;
        ratio = Math.Clamp(ratio, 0, 1);

        ProgressBarScale.ScaleX = ratio;
        CurrentTimeText.Text = FormatTime(displayPosition);
        RemainingTimeText.Text = FormatTime(duration);
    }

    private string FormatTime(TimeSpan time)
    {
        if (time.TotalHours >= 1)
            return time.ToString(@"h\:mm\:ss");
        return time.ToString(@"m\:ss");
    }

    #region Progress Bar Click and Drag to Seek

    private void ProgressBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_currentMediaInfo == null || !_currentMediaInfo.IsSeekEnabled) return;

        // Prevent event from bubbling up to parent (which would collapse notch)
        e.Handled = true;

        // Start dragging
        _isDraggingProgress = true;
        ProgressBarContainer.CaptureMouse();

        // Update UI immediately to show where user clicked
        UpdateProgressFromMouse(e);
    }

    private void ProgressBar_MouseMove(object sender, MouseEventArgs e)
    {
        // Prevent bubbling
        e.Handled = true;

        // Update progress while dragging
        if (_isDraggingProgress && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateProgressFromMouse(e);
        }
    }

    private async void ProgressBar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // Prevent bubbling
        e.Handled = true;

        if (_isDraggingProgress)
        {
            _isDraggingProgress = false;
            ProgressBarContainer.ReleaseMouseCapture();

            // Seek to final position
            await SeekToPosition(_dragSeekPosition);
        }
    }

    /// <summary>
    /// Update progress bar UI from mouse position (during drag)
    /// </summary>
    private void UpdateProgressFromMouse(MouseEventArgs e)
    {
        if (_lastKnownDuration.TotalSeconds <= 0) return;

        var position = e.GetPosition(ProgressBarContainer);
        double ratio = position.X / ProgressBarContainer.ActualWidth;
        ratio = Math.Clamp(ratio, 0, 1);

        // Update UI immediately
        ProgressBarScale.ScaleX = ratio;

        // Calculate and store seek position
        _dragSeekPosition = TimeSpan.FromSeconds(_lastKnownDuration.TotalSeconds * ratio);

        // Update displayed time
        CurrentTimeText.Text = FormatTime(_dragSeekPosition);
    }

    /// <summary>
    /// Seek to specified position
    /// </summary>
    private async Task SeekToPosition(TimeSpan newPos)
    {
        if (_lastKnownDuration.TotalSeconds <= 0) return;

        try
        {
            // Update local state immediately for instant UI feedback
            _lastKnownPosition = newPos;
            _lastMediaUpdate = DateTime.Now;
            _seekDebounceUntil = DateTime.Now.AddSeconds(2.5); // Increased to 2.5s for stability
            if (_isExpanded) RenderProgressBar();

            if (_isYouTubeVideoMode)
            {
                YouTubePlayer.SeekTo(newPos.TotalSeconds);
            }
            else
            {
                // Send seek command via Windows Media Session API
                await _mediaService.SeekAsync(newPos);
            }
        }
        catch { }
    }

    private async Task SeekRelative(double seconds)
    {
        if (_lastKnownDuration.TotalSeconds <= 0) return;

        // Calculate current extrapolated position
        var elapsed = DateTime.Now - _lastMediaUpdate;
        var currentPos = _lastKnownPosition + (_isMediaPlaying ? elapsed : TimeSpan.Zero);

        var newPos = currentPos + TimeSpan.FromSeconds(seconds);

        // Clamp
        if (newPos < TimeSpan.Zero) newPos = TimeSpan.Zero;
        if (newPos > _lastKnownDuration) newPos = _lastKnownDuration;

        // Update local state immediately for instant UI feedback
        _lastKnownPosition = newPos;
        _lastMediaUpdate = DateTime.Now;
        _seekDebounceUntil = DateTime.Now.AddSeconds(2.5); // Increased to 2.5s
        if (_isExpanded) RenderProgressBar();

        try
        {
            if (_isYouTubeVideoMode)
            {
                YouTubePlayer.SeekTo(newPos.TotalSeconds);
            }
            else
            {
                // We use the service's relative seek for better accuracy with the actual session
                await _mediaService.SeekRelativeAsync(seconds);
            }
        }
        catch { }
    }

    #endregion

    #region Progress Bar Animation on Expand

    // Expand animation removed

    private void UpdateProgressTimerState()
    {
        if (_progressTimer == null || _autoCollapseTimer == null) return;

        bool isExpanded = _isExpanded || _isMusicExpanded;
        bool showProgress = _currentMediaInfo != null && _currentMediaInfo.IsAnyMediaPlaying &&
                            (_currentMediaInfo.HasTimeline || _currentMediaInfo.IsIndeterminate);
        bool shouldRunProgress = isExpanded && showProgress;
        bool shouldRunAutoCollapse = isExpanded;

        if (shouldRunProgress)
        {
            if (!_progressTimer.IsEnabled) _progressTimer.Start();
        }
        else
        {
            if (_progressTimer.IsEnabled) _progressTimer.Stop();
        }

        if (shouldRunAutoCollapse)
        {
            if (!_autoCollapseTimer.IsEnabled) _autoCollapseTimer.Start();
        }
        else
        {
            if (_autoCollapseTimer.IsEnabled) _autoCollapseTimer.Stop();
        }
    }

    #endregion
}

