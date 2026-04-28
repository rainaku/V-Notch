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
    private long _progressSnapshotSequence = 0;  

    
    
    
    private double _progressDisplayRatio = 0;   
    private double _progressTargetRatio = 0;    
    private double _progressSpringTargetRatio = 0; 
    private double _progressVelocity = 0;       
    private bool _isSeekSpringActive = false;    
    private int _springSettleFrames = 0;
    private DateTime _seekSpringStartTime = DateTime.MinValue;
    private bool _isClickSeekPending = false;   
    private Point _mouseDownPoint;              
    private const double DRAG_THRESHOLD = 3.0;  

    
    
    private bool _springRenderHooked = false;
    private DoubleAnimation? _fpsBoostAnim;
    private readonly TranslateTransform _fpsBoostTarget = new(); 
    private readonly System.Diagnostics.Stopwatch _springStopwatch = new();

    
    private const double SPRING_STIFFNESS = 170.0;
    private const double SPRING_DAMPING = 34.0;
    private const double SPRING_SETTLE_THRESHOLD = 0.0012;
    private const double SPRING_TARGET_FOLLOW_SPEED = 30.0;
    private const double SPRING_MAX_VELOCITY = 4.2;
    private const double SPRING_MAX_STEP_PER_FRAME = 0.055;
    private const int SPRING_SETTLE_FRAMES_REQUIRED = 3;
    private const int SPRING_TIMEOUT_MS = 1400;
    private const double NORMAL_LERP_SPEED = 14.0;
    private const double SOURCE_IGNORE_SECONDS = 0.30;
    private const double SOURCE_SMOOTH_SECONDS = 2.0;
    

    
    
    
    
    
    private void StartSpringRenderLoop()
    {
        if (_springRenderHooked) return;
        _springRenderHooked = true;
        _springStopwatch.Restart();

        
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

    
    
    
    
    private void RenderSpringFrame()
    {
        if (!_isSeekSpringActive) return;

        
        double dt = _springStopwatch.Elapsed.TotalSeconds;
        _springStopwatch.Restart();
        dt = Math.Clamp(dt, 0.001, 0.033);

        
        double effectivePlaybackRate = 1.0;
        if (_currentMediaInfo != null &&
            !double.IsNaN(_currentMediaInfo.PlaybackRate) &&
            !double.IsInfinity(_currentMediaInfo.PlaybackRate) &&
            _currentMediaInfo.PlaybackRate > 0)
        {
            effectivePlaybackRate = Math.Clamp(_currentMediaInfo.PlaybackRate, 0.5, 3.0);
        }

        
        double scaledDt = dt * effectivePlaybackRate;

        
        double targetFollow = 1.0 - Math.Exp(-SPRING_TARGET_FOLLOW_SPEED * scaledDt);
        _progressSpringTargetRatio += (_progressTargetRatio - _progressSpringTargetRatio) * targetFollow;

        double error = _progressSpringTargetRatio - _progressDisplayRatio;

        
        double springForce = SPRING_STIFFNESS * error - SPRING_DAMPING * _progressVelocity;
        _progressVelocity += springForce * scaledDt;
        _progressVelocity = Math.Clamp(_progressVelocity, -SPRING_MAX_VELOCITY, SPRING_MAX_VELOCITY);

        double prevDisplay = _progressDisplayRatio;
        _progressDisplayRatio += _progressVelocity * scaledDt;

        double step = _progressDisplayRatio - prevDisplay;
        if (Math.Abs(step) > SPRING_MAX_STEP_PER_FRAME)
        {
            _progressDisplayRatio = prevDisplay + Math.Sign(step) * SPRING_MAX_STEP_PER_FRAME;
        }

        
        if ((prevDisplay - _progressSpringTargetRatio) * (_progressDisplayRatio - _progressSpringTargetRatio) < 0)
        {
            _progressDisplayRatio = _progressSpringTargetRatio;
            _progressVelocity = 0;
        }

        
        if (Math.Abs(_progressTargetRatio - _progressDisplayRatio) < SPRING_SETTLE_THRESHOLD &&
            Math.Abs(_progressVelocity) < 0.004)
        {
            _springSettleFrames++;
        }
        else
        {
            _springSettleFrames = 0;
        }

        if (_springSettleFrames >= SPRING_SETTLE_FRAMES_REQUIRED)
        {
            _progressDisplayRatio = _progressTargetRatio;
            _progressVelocity = 0;
            _springSettleFrames = 0;
            _isSeekSpringActive = false;
            StopSpringRenderLoop();
        }

        
        if ((DateTime.Now - _seekSpringStartTime).TotalMilliseconds > SPRING_TIMEOUT_MS)
        {
            _progressDisplayRatio = _progressTargetRatio;
            _progressVelocity = 0;
            _springSettleFrames = 0;
            _isSeekSpringActive = false;
            StopSpringRenderLoop();
        }

        
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

        if (_isExpanded && _isMusicExpanded && !_isDraggingVolume)
        {
            if (_mediaService.TryGetCurrentSessionVolume(out float volume, out bool isMuted))
            {
                _currentVolume = volume;
                VolumeBarScale.ScaleX = _currentVolume;
                UpdateVolumeIcon(_currentVolume, isMuted);
            }
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
    private string _lastProgressTimelineKey = "";
    private DateTimeOffset _lastProgressTimelineUpdated = DateTimeOffset.MinValue;

    private void UpdateProgressTracking(MediaInfo info)
    {
        _currentMediaInfo = info;

        ProgressSection.Visibility = Visibility.Visible;
        ProgressSection.Opacity = 1;

        bool showProgressDetails = info.IsAnyMediaPlaying || info.Duration.TotalSeconds > 0;

        if (showProgressDetails || info.HasTimeline || info.IsIndeterminate)
        {
            if (_isDraggingProgress) return;

            
            
            
            
            string newSignature = $"{info.SourceAppId}|{info.CurrentTrack}";
            bool isTrackChanged = newSignature != _lastProgressSignature;
            bool isSessionSwitch = !string.IsNullOrEmpty(info.SourceAppId) && info.SourceAppId != _lastSessionId;
            
            System.Diagnostics.Debug.WriteLine($"[PROGRESS] Session: {info.SourceAppId}, Track: {info.CurrentTrack}, " +
                $"isTrackChanged: {isTrackChanged}, isSessionSwitch: {isSessionSwitch}");
            
            
            if (isTrackChanged)
            {
                System.Diagnostics.Debug.WriteLine($"[PROGRESS] RESET: Track changed from '{_lastProgressSignature}' to '{newSignature}'");
                _lastProgressSignature = newSignature;
                
                
                if (isSessionSwitch)
                {
                    _lastSessionId = info.SourceAppId;
                    HandleSessionTransition();
                }
                
                
                _progressEngine.Reset();
                _progressVelocity = 0;
                _springSettleFrames = 0;
                _isSeekSpringActive = false;
                _lastRenderedDuration = TimeSpan.Zero;
                _progressSnapshotSequence = 0;  
                _lastProgressTimelineUpdated = DateTimeOffset.MinValue;  
                StopSpringRenderLoop();
            }
            else if (isSessionSwitch)
            {
                System.Diagnostics.Debug.WriteLine($"[PROGRESS] Session switch without track change: {info.SourceAppId}");
                
                
                _lastSessionId = info.SourceAppId;
                _lastProgressTimelineUpdated = DateTimeOffset.MinValue;  
            }

            string timelineKey = $"{info.SourceAppId}|{info.CurrentTrack}";
            if (timelineKey != _lastProgressTimelineKey)
            {
                System.Diagnostics.Debug.WriteLine($"[PROGRESS] Timeline key changed: '{_lastProgressTimelineKey}' -> '{timelineKey}'");
                _lastProgressTimelineKey = timelineKey;
                _lastProgressTimelineUpdated = DateTimeOffset.MinValue;  
            }

            
            
            
            if (_lastProgressTimelineUpdated != DateTimeOffset.MinValue &&
                timelineKey == _lastProgressTimelineKey)  
            {
                
                var infoUpdatedUtc = info.LastUpdated.ToUniversalTime();
                var lastUpdatedUtc = _lastProgressTimelineUpdated.ToUniversalTime();
                
                if (infoUpdatedUtc < lastUpdatedUtc)
                {
                    System.Diagnostics.Debug.WriteLine($"[PROGRESS] REJECTED: Stale snapshot for same timeline. " +
                        $"LastUpdated={infoUpdatedUtc:HH:mm:ss.fff} < {lastUpdatedUtc:HH:mm:ss.fff}");
                    
                    UpdateProgressTimerState();
                    if (_isExpanded) RenderProgressBar();
                    return;
                }
            }

            var snapshot = new ProgressSnapshot
            {
                Position = info.Position,
                Duration = info.Duration,
                IsPlaying = info.IsPlaying,
                
                
                IsYouTube = IsLikelyBrowserProgressSource(info),
                PlaybackRate = info.PlaybackRate,
                IsSeekEnabled = info.IsSeekEnabled,
                IsIndeterminate = info.IsIndeterminate,
                Timestamp = info.LastUpdated.UtcDateTime,
                SequenceNumber = System.Threading.Interlocked.Increment(ref _progressSnapshotSequence)
            };
            
            _progressEngine.OnMediaSnapshot(snapshot);
            if (info.LastUpdated > _lastProgressTimelineUpdated)
            {
                _lastProgressTimelineUpdated = info.LastUpdated;
            }

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
            _lastProgressTimelineKey = "";
            _lastProgressTimelineUpdated = DateTimeOffset.MinValue;
            _lastRenderedDuration = TimeSpan.Zero;
            ResetProgressUI();
            if (_isExpanded) RenderProgressBar();
        }
    }

    private static bool IsLikelyBrowserProgressSource(MediaInfo info)
    {
        // YouTube detection
        if (string.Equals(info.MediaSource, "YouTube", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(info.YouTubeVideoId))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(info.SourceAppId) &&
            info.SourceAppId.Contains("YouTube", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // SoundCloud detection
        if (string.Equals(info.MediaSource, "SoundCloud", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Generic browser detection (covers Spotify Web, Apple Music Web, etc.)
        if (string.Equals(info.MediaSource, "Browser", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Chrome/Edge browser detection
        if (!string.IsNullOrWhiteSpace(info.SourceAppId) &&
            (info.SourceAppId.Contains("chrome", StringComparison.OrdinalIgnoreCase) ||
             info.SourceAppId.Contains("edge", StringComparison.OrdinalIgnoreCase) ||
             info.SourceAppId.Contains("msedge", StringComparison.OrdinalIgnoreCase) ||
             info.SourceAppId.Contains("firefox", StringComparison.OrdinalIgnoreCase) ||
             info.SourceAppId.Contains("brave", StringComparison.OrdinalIgnoreCase) ||
             info.SourceAppId.Contains("opera", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
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
        _progressDisplayRatio = 0;
        _progressTargetRatio = 0;
        _progressSpringTargetRatio = 0;
        _progressVelocity = 0;
        _springSettleFrames = 0;
        _isSeekSpringActive = false;
        _seekSpringStartTime = DateTime.MinValue;
        _lastRenderTime = DateTime.MinValue;
        _lastRenderedDuration = TimeSpan.Zero;
        _lastDisplayedSecond = -1;
        CurrentTimeText.Text = "0:00";
        RemainingTimeText.Text = "0:00";
        IndeterminateProgress.BeginAnimation(OpacityProperty, null);
        IndeterminateProgress.Visibility = Visibility.Collapsed;
    }

    private DateTime _lastRenderTime = DateTime.MinValue;
    private TimeSpan _lastRenderedDuration = TimeSpan.Zero;
    private double _lastRenderedRatio = 0;

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
            _progressSpringTargetRatio = 0;
            _progressVelocity = 0;
            _springSettleFrames = 0;
            _isSeekSpringActive = false;
            StopSpringRenderLoop();
            _lastRenderTime = DateTime.MinValue;
            _lastRenderedDuration = TimeSpan.Zero;
            _lastDisplayedSecond = -1;
            return;
        }

        
        double engineRatio = 0;
        if (frame.Duration.TotalSeconds > 0)
        {
            engineRatio = frame.Position.TotalSeconds / frame.Duration.TotalSeconds;
            engineRatio = Math.Clamp(engineRatio, 0, 1);
        }

        // Detect external seek (e.g., user seeking from YouTube player)
        // If there's a large jump in position while catch-up is animating, stop the animation
        if (_isCatchUpAnimating)
        {
            double ratioDiff = Math.Abs(engineRatio - _lastRenderedRatio);
            // If position jumped more than 0.5% (external seek detected)
            // This is ~12 seconds for a 40-minute video, ~3 seconds for a 10-minute video
            if (ratioDiff > 0.005)
            {
                System.Diagnostics.Debug.WriteLine($"[CATCHUP] External seek detected, stopping animation. Diff={ratioDiff:F4}");
                StopCatchUpAnimation();
            }
            else
            {
                // Still animating, don't update
                _lastRenderedRatio = engineRatio;
                return;
            }
        }

        _lastRenderedRatio = engineRatio;

        
        System.Diagnostics.Debug.WriteLine($"[RENDER] Engine pos={frame.Position.TotalSeconds:F3}s, " +
            $"duration={frame.Duration.TotalSeconds:F1}s, " +
            $"state={frame.State}, " +
            $"engineRatio={engineRatio:F4}, " +
            $"displayRatio={_progressDisplayRatio:F4}, " +
            $"diff={(engineRatio - _progressDisplayRatio):F4}");

        
        
        if (frame.DurationJustChanged)
        {
            
            _isSeekSpringActive = false;
            _springSettleFrames = 0;
            _progressVelocity = 0;
            StopSpringRenderLoop();

            _progressDisplayRatio = engineRatio;
            _progressTargetRatio = engineRatio;
            _progressSpringTargetRatio = engineRatio;
            ProgressBarScale.ScaleX = engineRatio;
            _lastRenderTime = DateTime.Now;
            _lastRenderedDuration = frame.Duration;
        }
        else if (_isSeekSpringActive)
        {
            
            _progressTargetRatio = engineRatio;
            if (Math.Abs(_progressTargetRatio - _progressSpringTargetRatio) > 0.12)
            {
                _seekSpringStartTime = DateTime.Now;
            }
            _lastRenderedDuration = frame.Duration;  
        }
        else
        {
            DateTime now = DateTime.Now;
            double dt = _lastRenderTime == DateTime.MinValue ? 0.016 : (now - _lastRenderTime).TotalSeconds;
            dt = Math.Clamp(dt, 0.001, 0.1);
            _lastRenderTime = now;
            _lastRenderedDuration = frame.Duration;  

            bool isRealtimeProgressing = frame.State == ProgressState.Playing || frame.State == ProgressState.Seeking;
            double effectivePlaybackRate = 1.0;
            if (_currentMediaInfo != null &&
                !double.IsNaN(_currentMediaInfo.PlaybackRate) &&
                !double.IsInfinity(_currentMediaInfo.PlaybackRate) &&
                _currentMediaInfo.PlaybackRate > 0)
            {
                effectivePlaybackRate = Math.Clamp(_currentMediaInfo.PlaybackRate, 0.5, 3.0);
            }

            
            
            
            
            
            
            
            
            
            
            
            
            
            
            
            
            
            
            
            
            
            
            

            _progressTargetRatio = engineRatio;
            double ratioDiff = Math.Abs(_progressTargetRatio - _progressDisplayRatio);
            double diffSeconds = ratioDiff * frame.Duration.TotalSeconds;

            
            
            // Platform-aware backward jump threshold
            // Browser sources have higher latency but still need responsive correction
            // Balance between: accepting late snapshots vs. responsive progress
            double backwardThreshold = 0.5;  // Default for native apps
            if (_currentMediaInfo != null)
            {
                bool isBrowserSource = string.Equals(_currentMediaInfo.MediaSource, "Browser", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(_currentMediaInfo.MediaSource, "YouTube", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(_currentMediaInfo.MediaSource, "SoundCloud", StringComparison.OrdinalIgnoreCase) ||
                                      (!string.IsNullOrEmpty(_currentMediaInfo.SourceAppId) && 
                                       ((_currentMediaInfo.SourceAppId.Contains("chrome", StringComparison.OrdinalIgnoreCase)) ||
                                        (_currentMediaInfo.SourceAppId.Contains("edge", StringComparison.OrdinalIgnoreCase)) ||
                                        (_currentMediaInfo.SourceAppId.Contains("firefox", StringComparison.OrdinalIgnoreCase)) ||
                                        (_currentMediaInfo.SourceAppId.Contains("brave", StringComparison.OrdinalIgnoreCase)) ||
                                        (_currentMediaInfo.SourceAppId.Contains("opera", StringComparison.OrdinalIgnoreCase))));
                
                if (isBrowserSource)
                {
                    backwardThreshold = 1.5;  // Balanced threshold for browser sources
                }
            }
            
            if (_progressTargetRatio < _progressDisplayRatio)
            {
                double backwardSeconds = (_progressDisplayRatio - _progressTargetRatio) * frame.Duration.TotalSeconds;
                if (backwardSeconds > backwardThreshold)
                {
                    System.Diagnostics.Debug.WriteLine($"[RENDER] IGNORED: Backward jump {backwardSeconds:F3}s (threshold={backwardThreshold}s, source={_currentMediaInfo?.MediaSource})");
                    
                    return;
                }
                else if (backwardSeconds > 0.1)
                {
                    System.Diagnostics.Debug.WriteLine($"[RENDER] ACCEPTED: Backward jump {backwardSeconds:F3}s (threshold={backwardThreshold}s, source={_currentMediaInfo?.MediaSource})");
                }
            }

            if (diffSeconds >= SOURCE_SMOOTH_SECONDS)
            {
                
                System.Diagnostics.Debug.WriteLine($"[RENDER] SNAP: Large jump {diffSeconds:F3}s");
                _progressDisplayRatio = _progressTargetRatio;
                _progressSpringTargetRatio = _progressTargetRatio;
                _progressVelocity = 0;
            }
            else
            {
                
                
                if (diffSeconds > 0.05)  
                {
                    double error = _progressTargetRatio - _progressDisplayRatio;
                    
                    
                    // Improved correction speed for browser sources
                    // Forward: fast (0.9)
                    // Backward: medium-fast (0.6 for browser, 0.3 for native)
                    double backwardCorrectionSpeed = 0.3;  // Default for native
                    if (_currentMediaInfo != null)
                    {
                        bool isBrowserSource = string.Equals(_currentMediaInfo.MediaSource, "Browser", StringComparison.OrdinalIgnoreCase) ||
                                              string.Equals(_currentMediaInfo.MediaSource, "YouTube", StringComparison.OrdinalIgnoreCase) ||
                                              string.Equals(_currentMediaInfo.MediaSource, "SoundCloud", StringComparison.OrdinalIgnoreCase);
                        if (isBrowserSource)
                        {
                            backwardCorrectionSpeed = 0.6;  // Faster backward correction for browser
                        }
                    }
                    
                    double correctionSpeed = error > 0 ? 0.9 : backwardCorrectionSpeed;
                    double correctionLerp = 1.0 - Math.Exp(-(NORMAL_LERP_SPEED * correctionSpeed) * dt);
                    _progressDisplayRatio += error * correctionLerp;

                    if (Math.Abs(error) < 0.0001)
                    {
                        _progressDisplayRatio = _progressTargetRatio;
                    }
                }
                else
                {
                    
                    _progressDisplayRatio = _progressTargetRatio;
                }
            }

            _progressDisplayRatio = Math.Clamp(_progressDisplayRatio, 0, 1);
            ProgressBarScale.ScaleX = _progressDisplayRatio;
        }

        
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

        // Stop catch-up animation if running
        StopCatchUpAnimation();

        
        
        _isClickSeekPending = true;
        _mouseDownPoint = e.GetPosition(ProgressBarContainer);
        ProgressBarContainer.CaptureMouse();

        
        var duration = _progressEngine.GetUiFrame().Duration;
        if (duration.TotalSeconds <= 0) return;

        double ratio = _mouseDownPoint.X / ProgressBarContainer.ActualWidth;
        ratio = Math.Clamp(ratio, 0, 1);

        _dragSeekPosition = TimeSpan.FromSeconds(duration.TotalSeconds * ratio);
        _progressTargetRatio = ratio;
        _progressSpringTargetRatio = _progressDisplayRatio;
        _progressVelocity = 0;
        _springSettleFrames = 0;
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

        
        if (_isClickSeekPending && !_isDraggingProgress)
        {
            double dist = Math.Abs(currentPos.X - _mouseDownPoint.X);
            if (dist < DRAG_THRESHOLD) return; 

            
            _isClickSeekPending = false;
            _isDraggingProgress = true;
            _isSeekSpringActive = false;  
            _progressVelocity = 0;
            _springSettleFrames = 0;
            StopSpringRenderLoop();
            StopCatchUpAnimation(); // Also stop catch-up if somehow still running
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

        
        
        
        
        _isReleasingMouseCapture = true;
        ProgressBarContainer.ReleaseMouseCapture();

        
        
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, (System.Action)(() =>
        {
            _isReleasingMouseCapture = false;

            if (prevExpanded && !ProgressBarContainer.IsMouseOver)
            {
                
                _isProgressBarExpanded = false;
                AnimateProgressBarHover(false);
            }
            
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

        
        ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _progressDisplayRatio = ratio;
        _progressTargetRatio = ratio;
        _progressSpringTargetRatio = ratio;
        _progressVelocity = 0;
        _springSettleFrames = 0;
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

            
            double targetRatio = newPos.TotalSeconds / duration.TotalSeconds;
            targetRatio = Math.Clamp(targetRatio, 0, 1);
            _progressTargetRatio = targetRatio;
            _progressSpringTargetRatio = _progressDisplayRatio;
            _progressVelocity = 0;
            _springSettleFrames = 0;
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

            
            double targetRatio = newPos.TotalSeconds / duration.TotalSeconds;
            targetRatio = Math.Clamp(targetRatio, 0, 1);
            _progressTargetRatio = targetRatio;
            _progressSpringTargetRatio = _progressDisplayRatio;
            _progressVelocity = 0;
            _springSettleFrames = 0;
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

        _progressTimer.Interval = TimeSpan.FromMilliseconds(16);

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

    #region Progress Catch-Up Animation

    private bool _isCatchUpAnimating = false;
    private DispatcherTimer? _catchUpTimer;
    private double _catchUpTargetRatio = 0;
    private TimeSpan _catchUpTargetPosition = TimeSpan.Zero;

    private void StartProgressCatchUpAnimation()
    {
        if (_currentMediaInfo == null || _isCatchUpAnimating) return;

        var frame = _progressEngine.GetUiFrame();
        if (frame.Duration.TotalSeconds <= 0) return;

        double targetRatio = frame.Position.TotalSeconds / frame.Duration.TotalSeconds;
        targetRatio = Math.Clamp(targetRatio, 0, 1);

        // Only animate if there's a meaningful difference
        if (targetRatio < 0.01) return;

        _isCatchUpAnimating = true;
        _catchUpTargetRatio = targetRatio;
        _catchUpTargetPosition = frame.Position;

        // Reset progress bar to 0
        ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ProgressBarScale.ScaleX = 0;
        _progressDisplayRatio = 0;
        CurrentTimeText.Text = "0:00";

        // Create smooth ease-in-out animation (slow - fast - slow)
        var catchUpDuration = TimeSpan.FromMilliseconds(Math.Min(800, 400 + targetRatio * 600));
        var catchUpAnim = new DoubleAnimation(0, targetRatio, catchUpDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        Timeline.SetDesiredFrameRate(catchUpAnim, 60);

        // Animate time text during catch-up
        _catchUpTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        var startTime = DateTime.Now;
        _catchUpTimer.Tick += (s, e) =>
        {
            if (!_isCatchUpAnimating)
            {
                _catchUpTimer?.Stop();
                return;
            }

            var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
            var progress = Math.Min(1.0, elapsed / catchUpDuration.TotalMilliseconds);
            
            // Apply easing to time display
            var easedProgress = EaseCubicInOut(progress);
            var currentSeconds = _catchUpTargetPosition.TotalSeconds * easedProgress;
            CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(currentSeconds));
        };
        _catchUpTimer.Start();

        catchUpAnim.Completed += (s, e) =>
        {
            if (!_isCatchUpAnimating) return; // Already stopped

            _isCatchUpAnimating = false;
            _catchUpTimer?.Stop();
            _catchUpTimer = null;
            
            // Sync with CURRENT actual progress (not the old captured value)
            var currentFrame = _progressEngine.GetUiFrame();
            if (currentFrame.Duration.TotalSeconds > 0)
            {
                double currentRatio = currentFrame.Position.TotalSeconds / currentFrame.Duration.TotalSeconds;
                currentRatio = Math.Clamp(currentRatio, 0, 1);
                
                _progressDisplayRatio = currentRatio;
                _progressTargetRatio = currentRatio;
                _progressSpringTargetRatio = currentRatio;
                ProgressBarScale.ScaleX = currentRatio;
                CurrentTimeText.Text = FormatTime(currentFrame.Position);
            }
            
            // Resume normal progress tracking
            RenderProgressBar();
        };

        ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, catchUpAnim);
    }

    private void StopCatchUpAnimation()
    {
        if (!_isCatchUpAnimating) return;

        _isCatchUpAnimating = false;
        
        // Stop timer
        if (_catchUpTimer != null)
        {
            _catchUpTimer.Stop();
            _catchUpTimer = null;
        }

        // Stop animation
        ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        
        // Sync to current actual position
        var frame = _progressEngine.GetUiFrame();
        if (frame.Duration.TotalSeconds > 0)
        {
            double currentRatio = frame.Position.TotalSeconds / frame.Duration.TotalSeconds;
            currentRatio = Math.Clamp(currentRatio, 0, 1);
            
            _progressDisplayRatio = currentRatio;
            _progressTargetRatio = currentRatio;
            _progressSpringTargetRatio = currentRatio;
            ProgressBarScale.ScaleX = currentRatio;
            CurrentTimeText.Text = FormatTime(frame.Position);
        }
    }

    private double EaseCubicInOut(double t)
    {
        return t < 0.5
            ? 4 * t * t * t
            : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    }

    #endregion
}
