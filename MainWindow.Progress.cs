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
                    // Same track -> STRICT FORWARD ONLY
                    // Calculate "simulated" position
                    var elapsed = DateTime.Now - _lastMediaUpdate;
                    var extrapolatedPos = _lastKnownPosition + (_isMediaPlaying ? elapsed : TimeSpan.Zero);
                    
                    // We only update if the API reports a time that is AHEAD of our simulation.
                    // If API is behind (lag/glitch), we IGNORE it and keep simulating forward.
                    // This prevents "jerk back" but means backward seeks via external UI won't sync immediately.
                    bool isAhead = (apiPos - extrapolatedPos).TotalSeconds > -0.5;
                    
                    if (isAhead)
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
        RemainingTimeText.Text = "-0:00";
    }
    
    private void RenderProgressBar()
    {
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
        CurrentTimeText.Text = displayPosition.ToString(@"m\:ss");
        
        var remaining = duration - displayPosition;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        RemainingTimeText.Text = "-" + remaining.ToString(@"m\:ss");
    }
    
    #region Progress Bar Click-to-Seek
    
    private async void ProgressBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Prevent event from bubbling up to parent (which would collapse notch)
        e.Handled = true;
        
        // Click to seek - immediate action
        await SeekToMousePosition(e);
    }

    private void ProgressBar_MouseMove(object sender, MouseEventArgs e)
    {
        // Prevent bubbling
        e.Handled = true;
    }

    private void ProgressBar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // Prevent bubbling
        e.Handled = true;
    }
    
    private async Task SeekToMousePosition(MouseEventArgs e)
    {
        if (_lastKnownDuration.TotalSeconds <= 0) return;
        
        var position = e.GetPosition(ProgressBarContainer);
        double ratio = position.X / ProgressBarContainer.ActualWidth;
        ratio = Math.Max(0, Math.Min(1, ratio));
        
        // Update UI immediately
        ProgressBar.Width = ProgressBarContainer.ActualWidth * ratio;
        
        // Calculate new time position
        var newPos = TimeSpan.FromSeconds(_lastKnownDuration.TotalSeconds * ratio);
        
        // Update displayed time
        CurrentTimeText.Text = newPos.ToString(@"m\:ss");
        var remaining = _lastKnownDuration - newPos;
        RemainingTimeText.Text = "-" + remaining.ToString(@"m\:ss");
        
        // Send seek command
        try 
        {
            // Fallback to Windows Media Session API
            await _mediaService.SeekAsync(newPos);
            
            // Update local state
            _lastKnownPosition = newPos;
            _lastMediaUpdate = DateTime.Now;
            
            // Set debounce - ignore API position updates for 2 seconds
            // This prevents the "jump back" effect when API returns stale data
            _seekDebounceUntil = DateTime.Now.AddSeconds(2);
            System.Diagnostics.Debug.WriteLine($"[Progress] Seek to {newPos}, debouncing until {_seekDebounceUntil}");
        } 
        catch { }
    }
    
    #endregion
}

