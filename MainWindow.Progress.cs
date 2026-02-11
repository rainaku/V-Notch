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
    private string _lastTrackName = "";
    private bool _isDraggingProgress = false; // Flag for drag-to-seek
    private TimeSpan _dragSeekPosition = TimeSpan.Zero; // Store seek position during drag
    private string _lastArtistName = "";

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
    
    /// <summary>
    /// Called when MediaDetectionService fires MediaChanged event
    /// </summary>
    private void UpdateProgressTracking(MediaInfo info)
    {
        _currentMediaInfo = info;
        
        // Show/hide progress section based on timeline availability
        if (info.IsAnyMediaPlaying && info.HasTimeline)
        {
            // Has timeline - show progress bar
            ProgressSection.Visibility = Visibility.Visible;
            ProgressSection.Opacity = 1;

            // Manage timer for smooth UI updates
            UpdateProgressTimerState();
            
            // Detect play/pause state change BEFORE updating _isMediaPlaying
            bool playStateChanged = _isMediaPlaying != info.IsPlaying;
            _lastKnownDuration = info.Duration;
            
            // Check if we're in debounce period after a user seek
            bool isDebouncing = DateTime.Now < _seekDebounceUntil;
            
            if (!isDebouncing)
            {
                var apiPos = info.Position;
                bool isNewTrack = info.CurrentTrack != _lastTrackName || info.CurrentArtist != _lastArtistName;
                
                if (isNewTrack || playStateChanged)
                {
                     // New track or play/pause state changed -> Always accept API position
                     _lastKnownPosition = apiPos;
                     _lastMediaUpdate = DateTime.Now;
                     if (isNewTrack)
                     {
                         _lastTrackName = info.CurrentTrack;
                         _lastArtistName = info.CurrentArtist;
                     }
                }
                else
                {
                    // Same track, same state - only sync for genuine user seeks
                    var elapsed = DateTime.Now - _lastMediaUpdate;
                    var extrapolatedPos = _lastKnownPosition + (_isMediaPlaying ? elapsed : TimeSpan.Zero);
                    
                    // Calculate difference: positive = API ahead, negative = API behind
                    var apiDelta = (apiPos - extrapolatedPos).TotalSeconds;
                    
                    // Only snap to API for genuine user seeks or large drifts:
                    // - Forward seek: API jumps ahead by > 5s
                    // - Backward seek: API jumps behind by > 10s
                    // For anything in between, trust our smooth extrapolation.
                    // The API (especially Spotify) often lags or sends stale data.
                    if (apiDelta > 5.0 || apiDelta < -10.0)
                    {
                        _lastKnownPosition = apiPos;
                        _lastMediaUpdate = DateTime.Now;
                    }
                    // Natural resync: play/pause changes and new tracks always accept API position
                }
            }
            
            // NOW update the playing state (after sync logic used the old state)
            _isMediaPlaying = info.IsPlaying;
            
            // Render immediately
            if (_isExpanded)
            {
                RenderProgressBar();
            }
        }
        else if (info.IsAnyMediaPlaying)
        {
            // Has media but no timeline - hide progress bar
            ProgressSection.Visibility = Visibility.Collapsed;
            UpdateProgressTimerState(); // Ensure timer stops if no timeline
            _isMediaPlaying = false;
        }
        else
        {
            // No media - reset everything
            ProgressSection.Visibility = Visibility.Collapsed;
            UpdateProgressTimerState();
            _isMediaPlaying = false;
            _seekDebounceUntil = DateTime.MinValue;
            ResetProgressUI();
        }
    }
    
    private void ResetProgressUI()
    {
        ProgressBarScale.ScaleX = 0;
        CurrentTimeText.Text = "0:00";
        RemainingTimeText.Text = "0:00";
    }
    
    private void RenderProgressBar()
    {
        // Skip if user is dragging
        if (_isDraggingProgress) return;
        
        var duration = _lastKnownDuration;
        if (duration.TotalSeconds <= 0) return;
        
        // Calculate current position with extrapolation
        TimeSpan displayPosition;
        
        if (_isMediaPlaying)
        {
            // Playing - extrapolate from last known position
            var elapsed = DateTime.Now - _lastMediaUpdate;
            displayPosition = _lastKnownPosition + elapsed;
            
            // Cap at duration
            if (displayPosition > duration) 
                displayPosition = duration;
        }
        else
        {
            // Paused - show exact position
            displayPosition = _lastKnownPosition;
        }
        
        // Ensure non-negative
        if (displayPosition < TimeSpan.Zero)
            displayPosition = TimeSpan.Zero;
        
        // Calculate ratio and update bar
        double ratio = displayPosition.TotalSeconds / duration.TotalSeconds;
        ratio = Math.Clamp(ratio, 0, 1);
        
        ProgressBarScale.ScaleX = ratio;
        
        // Update time text
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
            _seekDebounceUntil = DateTime.Now.AddSeconds(2);
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
        
        // Progress bar timer: only if expanded AND media playing AND has timeline
        bool shouldRunProgress = isExpanded && 
                          _currentMediaInfo != null && 
                          _currentMediaInfo.IsAnyMediaPlaying && 
                          _currentMediaInfo.HasTimeline;

        // Auto-collapse timer: only if expanded
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

