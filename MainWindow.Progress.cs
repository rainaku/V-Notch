using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Runtime.InteropServices;
using VNotch.Models;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    private bool _isDraggingProgress = false; 
    private TimeSpan _dragSeekPosition = TimeSpan.Zero; 

    private void ProgressTimer_Tick(object? sender, EventArgs e)
    {

        if (_currentMediaInfo != null && _currentMediaInfo.IsAnyMediaPlaying)
        {
            if (_isExpanded)
            {
                RenderProgressBar();
            }
        }

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

        if ((_isExpanded || _isMusicExpanded) && !_isAnimating)
        {

            bool isDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;

            if (isDown && !_wasLButtonDown) 
            {
                if (GetCursorPos(out POINT pt))
                {
                    IntPtr hWndAtPoint = WindowFromPoint(pt);

                    if (hWndAtPoint != _hwnd && !IsChildWindow(_hwnd, hWndAtPoint))
                    {
                        if (_isSecondaryView)
                        {

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

    private void UpdateProgressTracking(MediaInfo info)
    {

        bool isSessionSwitch = !string.IsNullOrEmpty(info.SourceAppId) && info.SourceAppId != _lastSessionId;
        if (isSessionSwitch)
        {
            _lastSessionId = info.SourceAppId;
            HandleSessionTransition();
        }

        _currentMediaInfo = info;

        ProgressSection.Visibility = Visibility.Visible;
        ProgressSection.Opacity = 1;

        bool showProgressDetails = info.IsAnyMediaPlaying && (info.HasTimeline || info.IsIndeterminate);

        if (showProgressDetails)
        {

            if (_isDraggingProgress) return;

            string sig = $"{info.SourceAppId}|{info.MediaSource}|{info.CurrentTrack}|{info.CurrentArtist}";
            if (sig != _lastProgressSignature)
            {
                _lastProgressSignature = sig;
                _seekDebounceUntil = DateTime.MinValue;

                if (info.Duration.TotalSeconds > 0) _lastKnownDuration = info.Duration;
                _lastKnownPosition = info.Position;
                _lastMediaUpdate = DateTime.Now;
            }

            bool inSeekDebounce = DateTime.Now < _seekDebounceUntil;
            if (inSeekDebounce)
            {

                _isMediaPlaying = info.IsAnyMediaPlaying;
                if (info.Duration.TotalSeconds > 0 && info.Duration != _lastKnownDuration)
                    _lastKnownDuration = info.Duration;

                if (_isExpanded) RenderProgressBar();
                return;
            }

            if (info.HasTimeline)
            {

                if (info.MediaSource == "YouTube")
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

            ProgressBarContainer.Cursor = info.IsSeekEnabled ? Cursors.Hand : Cursors.Arrow;

            UpdateProgressTimerState();
            _isMediaPlaying = info.IsPlaying;

            if (_isExpanded) RenderProgressBar();
        }
        else
        {

            UpdateProgressTimerState();
            _isMediaPlaying = false;
            _lastSessionId = "";

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


    private void UpdateYouTubeProgress(MediaInfo info)
    {

        bool isNewTrack = info.CurrentTrack != _lastAnimatedTrackSignature.Split('|')[0];
        if (isNewTrack || info.IsThrottled)
        {
            if (info.Duration.TotalSeconds > 0) 
            {
                _lastKnownDuration = info.Duration;
            }
            else 
            {

                _lastKnownDuration = TimeSpan.Zero;
                _lastKnownPosition = TimeSpan.Zero;
            }
        }
        else if (info.Duration.TotalSeconds > 0)
        {
            _lastKnownDuration = info.Duration;
        }

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

        e.Handled = true;

        _isDraggingProgress = true;
        ProgressBarContainer.CaptureMouse();

        UpdateProgressFromMouse(e);
    }

    private void ProgressBar_MouseMove(object sender, MouseEventArgs e)
    {

        e.Handled = true;

        if (_isDraggingProgress && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateProgressFromMouse(e);
        }
    }

    private async void ProgressBar_MouseUp(object sender, MouseButtonEventArgs e)
    {

        e.Handled = true;

        if (_isDraggingProgress)
        {
            _isDraggingProgress = false;
            ProgressBarContainer.ReleaseMouseCapture();

            await SeekToPosition(_dragSeekPosition);
        }
    }

    private void UpdateProgressFromMouse(MouseEventArgs e)
    {
        if (_lastKnownDuration.TotalSeconds <= 0) return;

        var position = e.GetPosition(ProgressBarContainer);
        double ratio = position.X / ProgressBarContainer.ActualWidth;
        ratio = Math.Clamp(ratio, 0, 1);

        ProgressBarScale.ScaleX = ratio;

        _dragSeekPosition = TimeSpan.FromSeconds(_lastKnownDuration.TotalSeconds * ratio);

        CurrentTimeText.Text = FormatTime(_dragSeekPosition);
    }

    private async Task SeekToPosition(TimeSpan newPos)
    {
        if (_lastKnownDuration.TotalSeconds <= 0) return;

        try 
        {

            _lastKnownPosition = newPos;
            _lastMediaUpdate = DateTime.Now;
            _seekDebounceUntil = DateTime.Now.AddSeconds(2.5); 
            if (_isExpanded) RenderProgressBar();

            await _mediaService.SeekAsync(newPos);
        } 
        catch { }
    }

    private async Task SeekRelative(double seconds)
    {
        if (_lastKnownDuration.TotalSeconds <= 0) return;

        var elapsed = DateTime.Now - _lastMediaUpdate;
        var currentPos = _lastKnownPosition + (_isMediaPlaying ? elapsed : TimeSpan.Zero);

        var newPos = currentPos + TimeSpan.FromSeconds(seconds);

        if (newPos < TimeSpan.Zero) newPos = TimeSpan.Zero;
        if (newPos > _lastKnownDuration) newPos = _lastKnownDuration;

        _lastKnownPosition = newPos;
        _lastMediaUpdate = DateTime.Now;
        _seekDebounceUntil = DateTime.Now.AddSeconds(2.5); 
        if (_isExpanded) RenderProgressBar();

        try
        {
            await _mediaService.SeekRelativeAsync(seconds);
        }
        catch { }
    }

    #endregion

    #region Progress Bar Animation on Expand

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