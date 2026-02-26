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
    private bool _isProgressBarExpanded = false;
    private bool _isReleasingMouseCapture = false; 
    private int _lastDisplayedSecond = -1;
    private TimeSpan _dragSeekPosition = TimeSpan.Zero; 
    
    private readonly ProgressEngine _progressEngine = new ProgressEngine();

    // ── Spring-based progress bar interpolation ──
    // Instead of WPF DoubleAnimation, we drive the bar position manually each tick.
    // This eliminates jitter from animation restarts and gives buttery smooth seeks.
    private double _progressDisplayRatio = 0;   // Current displayed ratio (what ScaleX is set to)
    private double _progressTargetRatio = 0;    // Where we want to be
    private double _progressVelocity = 0;       // Spring velocity (units/sec)
    private bool _isSeekSpringActive = false;    // True while a seek spring is in flight
    private DateTime _seekSpringStartTime = DateTime.MinValue;
    private bool _isClickSeekPending = false;   // True between MouseDown and first MouseMove
    private Point _mouseDownPoint;              // For drag detection
    private const double DRAG_THRESHOLD = 3.0;  // Pixels before considered dragging

    // High-fps spring rendering via CompositionTarget.Rendering
    // + dummy animation to force WPF compositor to run at 144fps
    private bool _springRenderHooked = false;
    private DoubleAnimation? _fpsBoostAnim;
    private readonly TranslateTransform _fpsBoostTarget = new(); // Dummy target for fps boost anim
    private readonly System.Diagnostics.Stopwatch _springStopwatch = new();

    // Spring constants — tuned for fast, fluid 144fps animation
    private const double SPRING_STIFFNESS = 220.0;   // Fast snappy response
    private const double SPRING_DAMPING = 28.0;       // Smooth deceleration, no wobble
    private const double SPRING_SETTLE_THRESHOLD = 0.0002; // Sub-pixel precision settle
    private const double NORMAL_LERP_SPEED = 20.0;   // For normal playback (very fast catch-up)
    private const double SEEK_DETECT_SECONDS = 1.2;   // Jump bigger than this = seek
    private const double SEEK_DETECT_RATIO = 0.05;    // Jump bigger than this ratio = seek

    /// <summary>
    /// Start 144fps spring render loop using CompositionTarget.Rendering.
    /// A dummy animation with DesiredFrameRate=144 forces WPF's compositor
    /// to run at that frame rate (bypassing the default 60fps cap).
    /// </summary>
    private void StartSpringRenderLoop()
    {
        if (_springRenderHooked) return;
        _springRenderHooked = true;
        _springStopwatch.Restart();

        // Force WPF compositor to 144fps with a dummy animation on an invisible transform
        if (_fpsBoostAnim == null)
        {
            _fpsBoostAnim = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            Timeline.SetDesiredFrameRate(_fpsBoostAnim, 144);
            _fpsBoostAnim.Freeze();
        }
        _fpsBoostTarget.BeginAnimation(TranslateTransform.XProperty, _fpsBoostAnim);

        CompositionTarget.Rendering += SpringRender_Tick;
    }

    private void StopSpringRenderLoop()
    {
        if (!_springRenderHooked) return;
        _springRenderHooked = false;
        _springStopwatch.Stop();

        CompositionTarget.Rendering -= SpringRender_Tick;
        _fpsBoostTarget.BeginAnimation(TranslateTransform.XProperty, null);
    }

    private void SpringRender_Tick(object? sender, EventArgs e)
    {
        if (!_isSeekSpringActive)
        {
            StopSpringRenderLoop();
            return;
        }

        if (_isExpanded && !_isDraggingProgress)
        {
            RenderSpringFrame();
        }
    }

    /// <summary>
    /// Render only the spring interpolation step at 120fps.
    /// Separated from RenderProgressBar to avoid re-reading engine state 120 times/sec.
    /// </summary>
    private void RenderSpringFrame()
    {
        if (!_isSeekSpringActive) return;

        // Precision dt from Stopwatch (sub-ms accuracy)
        double dt = _springStopwatch.Elapsed.TotalSeconds;
        _springStopwatch.Restart();
        dt = Math.Clamp(dt, 0.001, 0.05);

        double error = _progressTargetRatio - _progressDisplayRatio;

        // Critically-damped spring: F = stiffness * error - damping * velocity
        double springForce = SPRING_STIFFNESS * error - SPRING_DAMPING * _progressVelocity;
        _progressVelocity += springForce * dt;
        _progressDisplayRatio += _progressVelocity * dt;

        // Settle check
        if (Math.Abs(error) < SPRING_SETTLE_THRESHOLD && Math.Abs(_progressVelocity) < 0.005)
        {
            _progressDisplayRatio = _progressTargetRatio;
            _progressVelocity = 0;
            _isSeekSpringActive = false;
            StopSpringRenderLoop();
        }

        // Safety timeout — force settle after 1.2s
        if ((DateTime.Now - _seekSpringStartTime).TotalMilliseconds > 1200)
        {
            _progressDisplayRatio = _progressTargetRatio;
            _progressVelocity = 0;
            _isSeekSpringActive = false;
            StopSpringRenderLoop();
        }

        // Apply directly — no WPF animation overhead
        _progressDisplayRatio = Math.Clamp(_progressDisplayRatio, 0, 1);
        ProgressBarScale.ScaleX = _progressDisplayRatio;
    }

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

    private void GlobalMouseHook_MouseLeftButtonDown(object? sender, InputMonitorService.POINT pt)
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
        // Clear any lingering WPF animation
        ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ProgressBarScale.ScaleX = 0;
        _progressDisplayRatio = 0;
        _progressTargetRatio = 0;
        _progressVelocity = 0;
        _isSeekSpringActive = false;
        _seekSpringStartTime = DateTime.MinValue;
        _lastDisplayedSecond = -1;
        CurrentTimeText.Text = "0:00";
        RemainingTimeText.Text = "0:00";
        IndeterminateProgress.BeginAnimation(OpacityProperty, null);
        IndeterminateProgress.Visibility = Visibility.Collapsed;
    }

    private DateTime _lastRenderTime = DateTime.MinValue;

    private void RenderProgressBar()
    {
        if ((_isDraggingProgress && !_isSeekSpringActive) || _currentMediaInfo == null) return;

        var frame = _progressEngine.GetUiFrame();

        if (frame.Duration.TotalSeconds <= 0 && !frame.ShowIndeterminate)
        {
            CurrentTimeText.Text = "--:--";
            RemainingTimeText.Text = "--:--";
            ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressBarScale.ScaleX = 0;
            _progressDisplayRatio = 0;
            _progressTargetRatio = 0;
            _progressVelocity = 0;
            _isSeekSpringActive = false;
            StopSpringRenderLoop();
            _lastDisplayedSecond = -1;
            return;
        }

        // Calculate target ratio from engine
        double engineRatio = 0;
        if (frame.Duration.TotalSeconds > 0)
        {
            engineRatio = frame.Position.TotalSeconds / frame.Duration.TotalSeconds;
            engineRatio = Math.Clamp(engineRatio, 0, 1);
        }

        // Detect seek jump
        double ratioDiff = Math.Abs(engineRatio - _progressTargetRatio);
        double diffSeconds = ratioDiff * frame.Duration.TotalSeconds;
        bool isJump = diffSeconds > SEEK_DETECT_SECONDS || ratioDiff > SEEK_DETECT_RATIO;

        if (isJump)
        {
            // Start spring — 120fps timer will handle the interpolation
            _isSeekSpringActive = true;
            _seekSpringStartTime = DateTime.Now;
            _progressTargetRatio = engineRatio;
            StartSpringRenderLoop();
        }
        else
        {
            // Update target for spring redirect or normal lerp
            _progressTargetRatio = engineRatio;
        }

        // When spring is active, the 120fps timer handles ScaleX updates
        // Here we only handle normal playback lerp
        if (!_isSeekSpringActive)
        {
            DateTime now = DateTime.Now;
            double dt = _lastRenderTime == DateTime.MinValue ? 0.016 : (now - _lastRenderTime).TotalSeconds;
            dt = Math.Clamp(dt, 0.001, 0.1);
            _lastRenderTime = now;

            double error = _progressTargetRatio - _progressDisplayRatio;
            _progressVelocity = 0;
            double lerpFactor = 1.0 - Math.Exp(-NORMAL_LERP_SPEED * dt);
            _progressDisplayRatio += error * lerpFactor;

            if (Math.Abs(error) < 0.0001)
            {
                _progressDisplayRatio = _progressTargetRatio;
            }

            _progressDisplayRatio = Math.Clamp(_progressDisplayRatio, 0, 1);
            ProgressBarScale.ScaleX = _progressDisplayRatio;
        }

        // Update time text (always, regardless of spring vs normal)
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

        // Don't set _isDraggingProgress yet — wait for actual drag movement.
        // This allows click-to-seek to trigger spring animation via RenderProgressBar.
        _isClickSeekPending = true;
        _mouseDownPoint = e.GetPosition(ProgressBarContainer);
        ProgressBarContainer.CaptureMouse();

        // Immediately start spring toward clicked position
        var duration = _progressEngine.GetUiFrame().Duration;
        if (duration.TotalSeconds <= 0) return;

        double ratio = _mouseDownPoint.X / ProgressBarContainer.ActualWidth;
        ratio = Math.Clamp(ratio, 0, 1);

        _dragSeekPosition = TimeSpan.FromSeconds(duration.TotalSeconds * ratio);
        _progressTargetRatio = ratio;
        _isSeekSpringActive = true;
        _seekSpringStartTime = DateTime.Now;
        StartSpringRenderLoop();

        CurrentTimeText.Text = FormatTime(_dragSeekPosition);
    }

    private void ProgressBar_MouseMove(object sender, MouseEventArgs e)
    {
        e.Handled = true;

        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (!_isClickSeekPending && !_isDraggingProgress) return;

        var currentPos = e.GetPosition(ProgressBarContainer);

        // Check if the mouse has moved enough to be considered a drag
        if (_isClickSeekPending && !_isDraggingProgress)
        {
            double dist = Math.Abs(currentPos.X - _mouseDownPoint.X);
            if (dist < DRAG_THRESHOLD) return; // Not dragging yet

            // Transition from click-seek to drag mode
            _isClickSeekPending = false;
            _isDraggingProgress = true;
            _isSeekSpringActive = false;  // Stop spring, snap during drag
            _progressVelocity = 0;
            StopSpringRenderLoop();
            MusicViz.IsBuffering = true;
        }

        if (_isDraggingProgress)
        {
            UpdateProgressFromMouse(e);
        }
    }

    private async void ProgressBar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        bool wasDragging = _isDraggingProgress;
        bool wasClickSeek = _isClickSeekPending;
        bool prevExpanded = _isProgressBarExpanded;
        MusicViz.IsBuffering = false;

        _isDraggingProgress = false;
        _isClickSeekPending = false;

        // Block bất kỳ MouseLeave nào do ReleaseMouseCapture() gây ra.
        // WPF có thể defer MouseLeave đến sau khi MouseUp handler trả về,
        // nên kiểm tra flag trong MouseLeave có thể không đủ. Flag này chận tất cả các collapse
        // trong suốt quá trình release, riêng Dispatcher.BeginInvoke rồi mới quyết định.
        _isReleasingMouseCapture = true;
        ProgressBarContainer.ReleaseMouseCapture();

        // Sau khi tất cả Input-priority events (bao gồm MouseLeave defer) được xử lý xong,
        // mới quyết định collapse hay giữ trạng thái hover dựa trên IsMouseOver thực tế.
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, (System.Action)(() =>
        {
            _isReleasingMouseCapture = false;

            if (prevExpanded && !ProgressBarContainer.IsMouseOver)
            {
                // Mouse đã rời khỏi container → collapse về default
                _isProgressBarExpanded = false;
                AnimateProgressBarHover(false);
            }
            // Nếu mouse vẫn ở trên container → giữ nguyên hover state
        }));

        if (wasDragging || wasClickSeek)
        {
            await SeekToPosition(_dragSeekPosition);
        }
    }

    private void UpdateProgressFromMouse(MouseEventArgs e)
    {
        var duration = _progressEngine.GetUiFrame().Duration;
        if (duration.TotalSeconds <= 0) return;

        var position = e.GetPosition(ProgressBarContainer);
        double ratio = position.X / ProgressBarContainer.ActualWidth;
        ratio = Math.Clamp(ratio, 0, 1);

        // Dragging: snap directly (no spring)
        ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _progressDisplayRatio = ratio;
        _progressTargetRatio = ratio;
        _progressVelocity = 0;
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

            // Set up the spring so the bar smoothly animates to the final position
            double targetRatio = newPos.TotalSeconds / duration.TotalSeconds;
            targetRatio = Math.Clamp(targetRatio, 0, 1);
            _progressTargetRatio = targetRatio;
            _isSeekSpringActive = true;
            _seekSpringStartTime = DateTime.Now;
            StartSpringRenderLoop();

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

            // Activate spring for smooth animation
            double targetRatio = newPos.TotalSeconds / duration.TotalSeconds;
            targetRatio = Math.Clamp(targetRatio, 0, 1);
            _progressTargetRatio = targetRatio;
            _isSeekSpringActive = true;
            _seekSpringStartTime = DateTime.Now;
            StartSpringRenderLoop();

            if (_isExpanded) RenderProgressBar();

            await _mediaService.SeekRelativeAsync(seconds);
        }
        catch { }
    }

    private DispatcherTimer? _progressHoverTimer;

    private void ProgressBar_MouseEnter(object sender, MouseEventArgs e)
    {
 
    }

    private void ProgressBar_MouseLeave(object sender, MouseEventArgs e)
    {
       
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
            InputMonitorService.Start();
        }
        else
        {
            InputMonitorService.Stop();
        }
    }

    #endregion
}