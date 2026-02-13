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

        _currentMediaInfo = info;
        
        // Show/hide progress section based on timeline availability
        bool showProgress = info.IsAnyMediaPlaying && (info.HasTimeline || info.IsIndeterminate);
        
        if (showProgress)
        {
            ProgressSection.Visibility = Visibility.Visible;
            ProgressSection.Opacity = 1;

            if (info.HasTimeline)
            {
                _lastKnownDuration = info.Duration;
                
                // Only accept position updates from the system if we aren't in a seek debounce period.
                // This prevents the "rubber-band" effect where the UI jumps back to the old position
                // because the system (SMTC) hasn't processed the seek command yet.
                if (DateTime.Now >= _seekDebounceUntil)
                {
                    _lastKnownPosition = info.Position;
                    _lastMediaUpdate = DateTime.Now;
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
            ProgressBarContainer.Cursor = info.IsSeekEnabled ? Cursors.Hand : Cursors.Arrow;
            
            UpdateProgressTimerState();
            _isMediaPlaying = info.IsPlaying;

            if (_isExpanded) RenderProgressBar();
        }
        else
        {
            ProgressSection.Visibility = Visibility.Collapsed;
            UpdateProgressTimerState();
            _isMediaPlaying = false;
            _lastSessionId = "";
            ResetProgressUI();
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
        
        if (_currentMediaInfo.IsIndeterminate)
        {
            CurrentTimeText.Text = "Live";
            RemainingTimeText.Text = "Live";
            return;
        }

        var duration = _lastKnownDuration;
        if (duration.TotalSeconds <= 0) return;
        
        // HIGH PRECISION INTERPOLATION
        // We use our local stabilized anchors (_lastKnownPosition and _lastMediaUpdate)
        // instead of raw _currentMediaInfo.Position to prevent flickering when the system 
        // reports stale data during track transitions or seeks.
        TimeSpan displayPosition;
        
        if (_isMediaPlaying)
        {
            var timeSinceUpdate = DateTime.Now - _lastMediaUpdate;
            // Cap extrapolation to 5 seconds to prevent runaway progress during heavy system lag
            if (timeSinceUpdate > TimeSpan.FromSeconds(5)) timeSinceUpdate = TimeSpan.FromSeconds(5);
            
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
            // Send seek command via Windows Media Session API
            await _mediaService.SeekAsync(newPos);
            
            // Update local state
            _lastKnownPosition = newPos;
            _lastMediaUpdate = DateTime.Now;
            
            // Set debounce - ignore API position updates for 2 seconds
            // This prevents the "jump back" effect when API returns stale data
            _seekDebounceUntil = DateTime.Now.AddSeconds(2.0);
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

        try
        {
            // We use the service's relative seek for better accuracy with the actual session
            // but we also update our local state to prevent UI flicker.
            await _mediaService.SeekRelativeAsync(seconds);

            // Update local state for immediate UI feedback
            _lastKnownPosition = newPos;
            _lastMediaUpdate = DateTime.Now;
            _seekDebounceUntil = DateTime.Now.AddSeconds(2.0);
            
            if (_isExpanded) RenderProgressBar();
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

