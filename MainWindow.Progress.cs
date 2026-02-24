using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using VNotch.Models;
using VNotch.Services;
namespace VNotch;

public partial class MainWindow
{
    private bool _isDraggingProgress = false; 
    private int _lastDisplayedSecond = -1;
    private TimeSpan _dragSeekPosition = TimeSpan.Zero; 
    
    private readonly ProgressEngine _progressEngine = new ProgressEngine();

    private void ProgressTimer_Tick(object? sender, EventArgs e)
    {
        if (_currentMediaInfo != null && (_currentMediaInfo.IsAnyMediaPlaying || _progressEngine.GetUiFrame().State == ProgressState.Playing))
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

    private void GlobalMouseHook_MouseLeftButtonDown(object? sender, GlobalMouseHook.POINT pt)
    {
        Dispatcher.Invoke(() =>
        {
            if ((_isExpanded || _isMusicExpanded) && !_isAnimating)
            {
                IntPtr hWndAtPoint = WindowFromPoint(new POINT { X = pt.x, Y = pt.y });

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
        });
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

    private string _lastSessionId = "";
    private string _lastProgressSignature = "";

    private void UpdateProgressTracking(MediaInfo info)
    {
        bool isSessionSwitch = !string.IsNullOrEmpty(info.SourceAppId) && info.SourceAppId != _lastSessionId;
        if (isSessionSwitch)
        {
            _lastSessionId = info.SourceAppId;
            HandleSessionTransition();
            _progressEngine.Reset();
        }

        _currentMediaInfo = info;

        ProgressSection.Visibility = Visibility.Visible;
        ProgressSection.Opacity = 1;

        bool showProgressDetails = info.IsAnyMediaPlaying || info.Duration.TotalSeconds > 0;

        if (showProgressDetails || info.HasTimeline || info.IsIndeterminate)
        {
            if (_isDraggingProgress) return;

            string sig = $"{info.SourceAppId}|{info.MediaSource}|{info.CurrentTrack}|{info.CurrentArtist}";
            if (sig != _lastProgressSignature)
            {
                _lastProgressSignature = sig;
                _progressEngine.Reset();
            }

            var snapshot = new ProgressSnapshot
            {
                Position = info.Position,
                Duration = info.Duration,
                IsPlaying = info.IsPlaying,
                IsYouTube = info.MediaSource == "YouTube" || info.MediaSource == "Browser",
                PlaybackRate = info.PlaybackRate,
                IsSeekEnabled = info.IsSeekEnabled,
                IsIndeterminate = info.IsIndeterminate,
                Timestamp = info.LastUpdated.UtcDateTime
            };
            
            _progressEngine.OnMediaSnapshot(snapshot);

            if (info.IsIndeterminate)
            {
                IndeterminateProgress.Visibility = Visibility.Visible;
                ProgressBar.Visibility = Visibility.Collapsed;
                StartIndeterminateAnimation();
            }
            else
            {
                IndeterminateProgress.Visibility = Visibility.Collapsed;
                ProgressBar.Visibility = Visibility.Visible;
            }

            ProgressBarContainer.Cursor = info.IsSeekEnabled ? Cursors.Hand : Cursors.Arrow;

            UpdateProgressTimerState();

            if (_isExpanded) RenderProgressBar();
        }
        else
        {
            UpdateProgressTimerState();
            _lastSessionId = "";
            
            IndeterminateProgress.Visibility = Visibility.Collapsed;
            ProgressBar.Visibility = Visibility.Visible;
            ProgressBarContainer.Cursor = Cursors.Arrow;

            _progressEngine.Reset();
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
        Timeline.SetDesiredFrameRate(anim, 15);
        IndeterminateProgress.BeginAnimation(OpacityProperty, anim);
    }

    private void ResetProgressUI()
    {
        ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ProgressBarScale.ScaleX = 0;
        CurrentTimeText.Text = "0:00";
        RemainingTimeText.Text = "0:00";
        IndeterminateProgress.BeginAnimation(OpacityProperty, null);
        IndeterminateProgress.Visibility = Visibility.Collapsed;
    }

    private void RenderProgressBar()
    {
        if (_isDraggingProgress || _currentMediaInfo == null) return;

        var frame = _progressEngine.GetUiFrame();

        if (frame.Duration.TotalSeconds <= 0 && !frame.ShowIndeterminate)
        {
            CurrentTimeText.Text = "--:--";
            RemainingTimeText.Text = "--:--";
            ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressBarScale.ScaleX = 0;
            _lastDisplayedSecond = -1;
            return;
        }

        double ratio = 0;
        if (frame.Duration.TotalSeconds > 0)
        {
            ratio = frame.Position.TotalSeconds / frame.Duration.TotalSeconds;
            ratio = Math.Clamp(ratio, 0, 1);
        }

        ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ProgressBarScale.ScaleX = ratio;

        int currentSecond = (int)frame.Position.TotalSeconds;
        if (currentSecond != _lastDisplayedSecond)
        {
            _lastDisplayedSecond = currentSecond;
            CurrentTimeText.Text = FormatTime(frame.Position);
            RemainingTimeText.Text = FormatTime(frame.Duration);
        }
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
        MusicViz.IsBuffering = true;
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
            MusicViz.IsBuffering = false;
            ProgressBarContainer.ReleaseMouseCapture();

            await SeekToPosition(_dragSeekPosition);
        }
    }

    private void UpdateProgressFromMouse(MouseEventArgs e)
    {
        var duration = _progressEngine.GetUiFrame().Duration;
        if (duration.TotalSeconds <= 0) return;

        ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);

        var position = e.GetPosition(ProgressBarContainer);
        double ratio = position.X / ProgressBarContainer.ActualWidth;
        ratio = Math.Clamp(ratio, 0, 1);

        ProgressBarScale.ScaleX = ratio;

        _dragSeekPosition = TimeSpan.FromSeconds(duration.TotalSeconds * ratio);

        CurrentTimeText.Text = FormatTime(_dragSeekPosition);
    }

    private async Task SeekToPosition(TimeSpan newPos)
    {
        var duration = _progressEngine.GetUiFrame().Duration;
        if (duration.TotalSeconds <= 0) return;

        try 
        {
            _progressEngine.NotifyUserSeek(newPos);
            if (_isExpanded) RenderProgressBar();

            await _mediaService.SeekAsync(newPos);
        } 
        catch { }
    }

    private async Task SeekRelative(double seconds)
    {
        var frame = _progressEngine.GetUiFrame();
        var duration = frame.Duration;
        if (duration.TotalSeconds <= 0) return;

        var currentPos = frame.Position;
        var newPos = currentPos + TimeSpan.FromSeconds(seconds);

        if (newPos < TimeSpan.Zero) newPos = TimeSpan.Zero;
        if (newPos > duration) newPos = duration;

        try
        {
            _progressEngine.NotifyUserSeek(newPos);
            if (_isExpanded) RenderProgressBar();

            await _mediaService.SeekRelativeAsync(seconds);
        }
        catch { }
    }

    #endregion

    #region Progress Bar Animation on Expand

    private void UpdateProgressTimerState()
    {
        if (_progressTimer == null) return;

        bool isExpanded = _isExpanded || _isMusicExpanded;
        
        bool hasTimeline = _currentMediaInfo != null && (_currentMediaInfo.HasTimeline || _currentMediaInfo.IsIndeterminate);
        bool shouldRunProgress = isExpanded && hasTimeline;

        _progressTimer.Interval = TimeSpan.FromMilliseconds(50);

        if (shouldRunProgress)
        {
            if (!_progressTimer.IsEnabled) _progressTimer.Start();
        }
        else
        {
            if (_progressTimer.IsEnabled) _progressTimer.Stop();
        }

        if (isExpanded)
        {
            GlobalMouseHook.Start();
        }
        else
        {
            GlobalMouseHook.Stop();
        }
    }

    #endregion
}