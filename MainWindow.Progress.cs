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
    private bool _isProgressAnimating = false; // Flag to prevent RenderProgressBar from overriding animation
    private bool _isDraggingProgress = false; // Flag for drag-to-seek
    private TimeSpan _dragSeekPosition = TimeSpan.Zero; // Store seek position during drag
    private string _lastArtistName = "";

    private void ProgressTimer_Tick(object? sender, EventArgs e)
    {
        // 0. Aggressively stay on top when expanded (prevents MyDockfinder masking)
        if (_isExpanded || _isMusicExpanded)
        {
            EnsureTopmost();
        }

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
            VolumeBarFront.Width = 100.0 * _currentVolume;
        }

        // 2. Auto-collapse logic (Essential for WS_EX_NOACTIVATE windows)
        if ((_isExpanded || _isMusicExpanded) && !_isAnimating)
        {
            // Check if left mouse button is pressed anywhere on screen
            if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0)
            {
                if (GetCursorPos(out POINT pt))
                {
                    IntPtr hWndAtPoint = WindowFromPoint(pt);
                    
                    // Collapse if click is NOT on this window or any of its child controls
                    if (hWndAtPoint != _hwnd && !IsChildWindow(_hwnd, hWndAtPoint))
                    {
                        CollapseAll();
                    }
                }
            }
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

            // Start timer for smooth UI updates
            if (_progressTimer != null && !_progressTimer.IsEnabled) 
                _progressTimer.Start();
            
            // Update playing state
            _isMediaPlaying = info.IsPlaying;
            _lastKnownDuration = info.Duration;
            
            // Check if we're in debounce period after a user seek
            bool isDebouncing = DateTime.Now < _seekDebounceUntil;
            
            if (!isDebouncing)
            {
                var apiPos = info.Position;
                bool isNewTrack = info.CurrentTrack != _lastTrackName || info.CurrentArtist != _lastArtistName;
                
                if (isNewTrack)
                {
                     // New track -> Always accept new position
                     _lastKnownPosition = apiPos;
                     _lastMediaUpdate = DateTime.Now;
                     _lastTrackName = info.CurrentTrack;
                     _lastArtistName = info.CurrentArtist;
                }
                else
                {
                    // Same track - detect seeks (both forward and backward)
                    // Calculate "simulated" position based on last known + elapsed time
                    var elapsed = DateTime.Now - _lastMediaUpdate;
                    var extrapolatedPos = _lastKnownPosition + (_isMediaPlaying ? elapsed : TimeSpan.Zero);
                    
                    // Calculate difference between API position and our extrapolated position
                    var difference = Math.Abs((apiPos - extrapolatedPos).TotalSeconds);
                    
                    // If difference > 2 seconds, this is likely a user seek (forward or backward)
                    // Accept the new position immediately
                    bool isSeek = difference > 2.0;
                    
                    // Also accept if API is ahead of our simulation (normal playback or forward seek)
                    bool isAhead = (apiPos - extrapolatedPos).TotalSeconds > -0.5;
                    
                    if (isSeek || isAhead)
                    {
                        _lastKnownPosition = apiPos;
                        _lastMediaUpdate = DateTime.Now;
                    }
                }
            }
            
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
            _progressTimer?.Stop();
            _isMediaPlaying = false;
        }
        else
        {
            // No media - reset everything
            ProgressSection.Visibility = Visibility.Collapsed;
            _progressTimer?.Stop();
            _isMediaPlaying = false;
            _seekDebounceUntil = DateTime.MinValue;
            ResetProgressUI();
        }
    }
    
    private void ResetProgressUI()
    {
        ProgressBar.Width = 0;
        CurrentTimeText.Text = "0:00";
        RemainingTimeText.Text = "0:00";
    }
    
    private void RenderProgressBar()
    {
        // Skip if animation is in progress or user is dragging
        if (_isProgressAnimating || _isDraggingProgress) return;
        
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
        
        ProgressBar.Width = ProgressBarContainer.ActualWidth * ratio;
        
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
        ProgressBar.Width = ProgressBarContainer.ActualWidth * ratio;
        
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
    
    /// <summary>
    /// Animate progress bar from 0 to current position when notch expands
    /// </summary>
    private void AnimateProgressBarOnExpand()
    {
        // Only animate if we have valid media info with timeline
        if (_currentMediaInfo == null || !_currentMediaInfo.HasTimeline || _lastKnownDuration.TotalSeconds <= 0)
            return;
        
        // Set flag to prevent RenderProgressBar from overriding
        _isProgressAnimating = true;
        
        // Delay slightly to ensure layout is complete and ActualWidth is valid
        Dispatcher.BeginInvoke(new Action(() =>
        {
            // Calculate target position
            TimeSpan displayPosition;
            if (_isMediaPlaying)
            {
                var elapsed = DateTime.Now - _lastMediaUpdate;
                displayPosition = _lastKnownPosition + elapsed;
                if (displayPosition > _lastKnownDuration) 
                    displayPosition = _lastKnownDuration;
            }
            else
            {
                displayPosition = _lastKnownPosition;
            }
            
            if (displayPosition < TimeSpan.Zero)
                displayPosition = TimeSpan.Zero;
            
            // Ensure we have valid container width
            double containerWidth = ProgressBarContainer.ActualWidth;
            if (containerWidth <= 0) containerWidth = 200; // Fallback
                
            // Calculate target width
            double ratio = displayPosition.TotalSeconds / _lastKnownDuration.TotalSeconds;
            ratio = Math.Clamp(ratio, 0, 1);
            double targetWidth = containerWidth * ratio;
            
            // Clear any existing animation first
            ProgressBar.BeginAnimation(WidthProperty, null);
            
            // Start from 0
            ProgressBar.Width = 0;
            CurrentTimeText.Text = "0:00";
            
            // Animate to target with smooth easing
            var animDuration = TimeSpan.FromMilliseconds(600);
            var easing = new ExponentialEase 
            { 
                EasingMode = EasingMode.EaseOut, 
                Exponent = 5 
            };
            
            var widthAnim = new DoubleAnimation
            {
                From = 0,
                To = targetWidth,
                Duration = animDuration,
                EasingFunction = easing
            };
            Timeline.SetDesiredFrameRate(widthAnim, 60);
            
            // When animation completes, clear flag and set final width explicitly
            widthAnim.Completed += (s, e) =>
            {
                ProgressBar.BeginAnimation(WidthProperty, null); // Clear animation
                ProgressBar.Width = targetWidth; // Set final value
                _isProgressAnimating = false; // Allow RenderProgressBar to take over
            };
            
            // Also animate time text (using timer for smooth counting)
            AnimateTimeText(displayPosition, animDuration);
            
            ProgressBar.BeginAnimation(WidthProperty, widthAnim);
        }), DispatcherPriority.Loaded);
    }
    
    /// <summary>
    /// Animate the current time text from 0:00 to target time
    /// </summary>
    private void AnimateTimeText(TimeSpan targetTime, TimeSpan duration)
    {
        var startTime = DateTime.Now;
        var endTime = startTime.Add(duration);
        
        var animTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(30) // ~33fps for text
        };
        
        animTimer.Tick += (s, e) =>
        {
            var now = DateTime.Now;
            if (now >= endTime)
            {
                CurrentTimeText.Text = FormatTime(targetTime);
                animTimer.Stop();
                return;
            }
            
            // Calculate progress with easing (exponential ease out)
            double t = (now - startTime).TotalMilliseconds / duration.TotalMilliseconds;
            t = Math.Clamp(t, 0, 1);
            double easedT = 1 - Math.Pow(1 - t, 5); // Exponential ease out
            
            var currentTime = TimeSpan.FromSeconds(targetTime.TotalSeconds * easedT);
            CurrentTimeText.Text = FormatTime(currentTime);
        };
        
        animTimer.Start();
    }
    
    #endregion
}

