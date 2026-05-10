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
using static VNotch.Services.Win32Interop;
using POINT = VNotch.Services.Win32Interop.POINT;
using static VNotch.Services.AnimationPrimitives;
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
    private DateTime _allowProgressBackwardRenderUntil = DateTime.MinValue;
    private Point _mouseDownPoint;
    private const double DRAG_THRESHOLD = 3.0;  

    // Spring render loop is driven by a dedicated helper; this partial keeps the
    // per-frame ratios/velocity in its own fields (and pushes them into the
    // renderer state every time the spring restarts) because many sites across
    // Progress.cs mutate them directly (click-seek pending, drag snap-back,
    // catch-up animation etc.).
    private ProgressSpringRenderer? _springRenderer;
    private ProgressSpringRenderer Spring => _springRenderer ??= new ProgressSpringRenderer(
        applyRatio: r =>
        {
            _progressDisplayRatio = r;
            ProgressBarScale.ScaleX = r;
        },
        shouldRender: () => (_isExpanded || _isMusicExpanded) && !_isDraggingProgress,
        getPlaybackRate: () => _currentMediaInfo?.PlaybackRate ?? 1.0);

    
    private const double NORMAL_LERP_SPEED = 14.0;
    private const double SOURCE_IGNORE_SECONDS = 0.30;
    private const double SOURCE_SMOOTH_SECONDS = 2.0;
    

    
    
    
    
    
    private void StartSpringRenderLoop()
    {
        // Keep the renderer's mirror of the physics state in sync with the
        // fields the rest of this partial mutates directly.
        Spring.DisplayRatio = _progressDisplayRatio;
        Spring.TargetRatio = _progressTargetRatio;
        Spring.SpringTargetRatio = _progressSpringTargetRatio;
        Spring.Velocity = _progressVelocity;
        Spring.SettleFrames = _springSettleFrames;
        Spring.Start();
    }

    private void StopSpringRenderLoop()
    {
        if (_springRenderer == null) return;
        Spring.Stop();
        // Pull the final state back into the partial's fields so the rest of
        // the code keeps seeing consistent numbers.
        _progressDisplayRatio = Spring.DisplayRatio;
        _progressVelocity = Spring.Velocity;
        _springSettleFrames = Spring.SettleFrames;
        _isSeekSpringActive = Spring.IsActive;
    }

    private void SyncSpringStateFromFields()
    {
        if (_springRenderer == null) return;
        Spring.DisplayRatio = _progressDisplayRatio;
        Spring.TargetRatio = _progressTargetRatio;
        Spring.SpringTargetRatio = _progressSpringTargetRatio;
        Spring.Velocity = _progressVelocity;
        Spring.SettleFrames = _springSettleFrames;
    }

    private void ProgressTimer_Tick(object? sender, EventArgs e)
    {
        // Always render progress bar when expanded, regardless of play state
        // This ensures UI updates even when user is seeking or paused
        if (_isExpanded || _isMusicExpanded)
        {
            if (_currentMediaInfo != null)
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
        NormalizeStartupSnapshotTimestamp(info);

        ProgressSection.Visibility = Visibility.Visible;
        ProgressSection.Opacity = 1;

        bool showProgressDetails = info.IsAnyMediaPlaying || info.Duration.TotalSeconds > 0;

        if (showProgressDetails || info.HasTimeline || info.IsIndeterminate)
        {
            if (_isDraggingProgress) return;

            
            
            
            
            // Track identity should not include duration jitter.
            // Browser timelines often report tiny duration deltas for the same song,
            // which previously caused false track-change resets and progress freeze/jump.
            string newSignature = $"{info.SourceAppId}|{info.CurrentTrack}|{info.CurrentArtist}";
            bool isTrackChanged = newSignature != _lastProgressSignature;
            bool isSessionSwitch = !string.IsNullOrEmpty(info.SourceAppId) && info.SourceAppId != _lastSessionId;
            
            if (isTrackChanged)
            {
                _lastProgressSignature = newSignature;
                
                
                if (isSessionSwitch)
                {
                    _lastSessionId = info.SourceAppId;
                    HandleSessionTransition();
                }
                
                
                _progressEngine.Reset();
                StopCatchUpAnimation();
                ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _progressDisplayRatio = 0;
                _progressTargetRatio = 0;
                _progressSpringTargetRatio = 0;
                _progressVelocity = 0;
                _springSettleFrames = 0;
                _isSeekSpringActive = false;
                _lastRenderedRatio = 0;
                _lastRenderTime = DateTime.MinValue;
                _lastRenderedDuration = TimeSpan.Zero;
                _lastDisplayedSecond = -1;
                _progressSnapshotSequence = 0;
                _lastProgressTimelineUpdated = DateTimeOffset.MinValue;
                ProgressBarScale.ScaleX = 0;
                CurrentTimeText.Text = "0:00";
                RemainingTimeText.Text = info.Duration.TotalSeconds > 0 ? FormatTime(info.Duration) : "--:--";
                StopSpringRenderLoop();
            }
            else if (isSessionSwitch)
            {
                _lastSessionId = info.SourceAppId;
                _lastProgressTimelineUpdated = DateTimeOffset.MinValue;  
            }

            // Timeline key keeps duration only at second granularity to absorb ms-level noise.
            string timelineKey = $"{info.SourceAppId}|{info.CurrentTrack}|{info.CurrentArtist}|{Math.Round(info.Duration.TotalSeconds):F0}";
            if (timelineKey != _lastProgressTimelineKey)
            {
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
                    UpdateProgressTimerState();
                    if (_isExpanded || _isMusicExpanded) RenderProgressBar();
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

            if (_isExpanded || _isMusicExpanded)
            {
                RenderProgressBar();
                Dispatcher.BeginInvoke(new Action(RenderProgressBar), DispatcherPriority.Render);
            }
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
            if (_isExpanded || _isMusicExpanded) RenderProgressBar();
        }
    }

    private static void NormalizeStartupSnapshotTimestamp(MediaInfo info) =>
        MediaProgressHelpers.NormalizeStartupSnapshotTimestamp(info);

    private static bool IsLikelyBrowserProgressSource(MediaInfo info) =>
        MediaProgressHelpers.IsLikelyBrowserProgressSource(info);

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
    private DateTime _lastProgressMovementUtc = DateTime.UtcNow;
    private double _lastProgressMovementRatio = -1;
    private DateTime _lastFreezeDebugLogUtc = DateTime.MinValue;

    private void RenderProgressBar()
    {
        // Allow rendering during seek spring animation, but not during manual drag
        if (_isDraggingProgress || _currentMediaInfo == null) return;

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
            // If position jumped more than 1% (external seek detected)
            // This is adaptive: ~24 seconds for 40-min video, ~4 seconds for 6-min video
            if (ratioDiff > 0.01)
            {
                StopCatchUpAnimation();
                
                // Immediately snap to the new position after external seek
                _progressDisplayRatio = engineRatio;
                _progressTargetRatio = engineRatio;
                _progressSpringTargetRatio = engineRatio;
                ProgressBarScale.ScaleX = engineRatio;
                CurrentTimeText.Text = FormatTime(frame.Position);
                _lastRenderedRatio = engineRatio;
            }
            else
            {
                // Still animating, don't update
                _lastRenderedRatio = engineRatio;
                return;
            }
        }

        _lastRenderedRatio = engineRatio;

        
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
            // Safety: if spring is active but render hook was not running, restart it.
            if (!Spring.IsHooked)
            {
                StartSpringRenderLoop();
            }

             
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

            
            
            
            
            
            
            
            
            
            
            
            
            
            
            
            
            
            
            
            
            
            
            

            double rawTargetRatio = engineRatio;
            double rawRatioDiff = Math.Abs(rawTargetRatio - _progressDisplayRatio);
            double rawDiffSeconds = rawRatioDiff * frame.Duration.TotalSeconds;

            // Large jumps must snap to the real engine position before any
            // backward-jitter cap runs, otherwise a track change can inherit
            // the previous song's bar state and slowly crawl backward.
            if (rawDiffSeconds >= SOURCE_SMOOTH_SECONDS)
            {
                _progressDisplayRatio = rawTargetRatio;
                _progressTargetRatio = rawTargetRatio;
                _progressSpringTargetRatio = rawTargetRatio;
                _progressVelocity = 0;
                ProgressBarScale.ScaleX = _progressDisplayRatio;
            }
            else
            {
                _progressTargetRatio = rawTargetRatio;

                
                
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
                
                bool isUserSeekWindow = frame.State == ProgressState.Seeking || DateTime.Now < _allowProgressBackwardRenderUntil;

                if (_progressTargetRatio < _progressDisplayRatio)
                {
                    double backwardSeconds = (_progressDisplayRatio - _progressTargetRatio) * frame.Duration.TotalSeconds;

                    // Tiny backward corrections from browser/native timeline jitter
                    // make the bar visibly "breathe" even while playback time is correct.
                    // Ignore those micro backsteps during normal playback so the visual
                    // progress stays smooth and mostly monotonic.
                    if (isRealtimeProgressing && backwardSeconds <= SOURCE_IGNORE_SECONDS && !isUserSeekWindow)
                    {
                        _progressTargetRatio = _progressDisplayRatio;
                    }
                    else if (backwardSeconds > backwardThreshold && !isUserSeekWindow)
                    {
                        // Avoid hard-freeze: cap backward correction per frame instead of bailing out.
                        double maxBackwardStepSeconds = 0.22;
                        double maxBackwardRatioStep = maxBackwardStepSeconds / frame.Duration.TotalSeconds;
                        double cappedTarget = Math.Max(_progressTargetRatio, _progressDisplayRatio - maxBackwardRatioStep);
                        _progressTargetRatio = cappedTarget;
                    }
                    else if (backwardSeconds > 0.1)
                    {
                    }
                }

                double ratioDiff = Math.Abs(_progressTargetRatio - _progressDisplayRatio);
                double diffSeconds = ratioDiff * frame.Duration.TotalSeconds;

                if (diffSeconds > 0.05)  
                {
                    double error = _progressTargetRatio - _progressDisplayRatio;
                    
                    
                    // Improved correction speed for browser sources
                    // Forward: fast (0.9)
                    // Backward: medium-fast (0.6 for browser, 0.3 for native)
                    double backwardCorrectionSpeed = 0.14;  // Default for native
                    if (_currentMediaInfo != null)
                    {
                        bool isBrowserSource = string.Equals(_currentMediaInfo.MediaSource, "Browser", StringComparison.OrdinalIgnoreCase) ||
                                              string.Equals(_currentMediaInfo.MediaSource, "YouTube", StringComparison.OrdinalIgnoreCase) ||
                                              string.Equals(_currentMediaInfo.MediaSource, "SoundCloud", StringComparison.OrdinalIgnoreCase);
                        if (isBrowserSource)
                        {
                            backwardCorrectionSpeed = 0.22;  // Slightly faster, but still visually stable
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

        if (!_isDraggingProgress && frame.Duration.TotalSeconds > 0 && frame.State == ProgressState.Playing)
        {
            if (_lastProgressMovementRatio < 0 || Math.Abs(_progressDisplayRatio - _lastProgressMovementRatio) > 0.0015)
            {
                _lastProgressMovementRatio = _progressDisplayRatio;
                _lastProgressMovementUtc = DateTime.UtcNow;
            }
            else
            {
                var stuckFor = DateTime.UtcNow - _lastProgressMovementUtc;
                var visualDrift = Math.Abs(engineRatio - _progressDisplayRatio);
                if (stuckFor.TotalMilliseconds > 900 && visualDrift > 0.02)
                {
                    _lastProgressMovementUtc = DateTime.UtcNow;
                }
            }
        }

        
        int currentSecond = (int)frame.Position.TotalSeconds;
        if (currentSecond != _lastDisplayedSecond)
        {
            _lastDisplayedSecond = currentSecond;
            CurrentTimeText.Text = FormatTime(frame.Position);
            RemainingTimeText.Text = FormatTime(frame.Duration);
        }
    }

    private string FormatTime(TimeSpan time) => MediaProgressHelpers.FormatTime(time);

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

    private void AnimateSeekProgressTo(double targetRatio)
    {
        targetRatio = Math.Clamp(targetRatio, 0, 1);
        ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);

        _progressTargetRatio = targetRatio;
        _progressSpringTargetRatio = targetRatio;
        _progressVelocity *= 0.35;
        _springSettleFrames = 0;
        _isSeekSpringActive = true;
        _seekSpringStartTime = DateTime.Now;
        StartSpringRenderLoop();
    }

    private async Task SeekToPosition(TimeSpan newPos)
    {
        var duration = _progressEngine.GetUiFrame().Duration;
        if (duration.TotalSeconds <= 0) return;

        try 
        {
            _allowProgressBackwardRenderUntil = DateTime.Now.AddSeconds(3);
            _progressEngine.NotifyUserSeek(newPos);

            
            bool seekToTrackEnd = duration.TotalSeconds > 0 && newPos >= duration - TimeSpan.FromMilliseconds(900);
            if (seekToTrackEnd)
            {
                _lastProgressTimelineUpdated = DateTimeOffset.MinValue;
                _lastProgressTimelineKey = "";
                _allowProgressBackwardRenderUntil = DateTime.Now.AddSeconds(6);
            }

            double targetRatio = newPos.TotalSeconds / duration.TotalSeconds;
            targetRatio = Math.Clamp(targetRatio, 0, 1);
            AnimateSeekProgressTo(targetRatio);
            CurrentTimeText.Text = FormatTime(newPos);
            _lastDisplayedSecond = (int)newPos.TotalSeconds;

            await _mediaService.SeekAsync(newPos);
            
            // Ensure progress timer is running after seek
            UpdateProgressTimerState();
        } 
        catch (Exception ex)
        {
            RuntimeLog.Log("PROGRESS-SEEK", ex.ToString());
        }
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
            _allowProgressBackwardRenderUntil = DateTime.Now.AddSeconds(3);
            _progressEngine.NotifyUserSeek(newPos);

            
            bool seekToTrackEnd = duration.TotalSeconds > 0 && newPos >= duration - TimeSpan.FromMilliseconds(900);
            if (seekToTrackEnd)
            {
                _lastProgressTimelineUpdated = DateTimeOffset.MinValue;
                _lastProgressTimelineKey = "";
                _allowProgressBackwardRenderUntil = DateTime.Now.AddSeconds(6);
            }

            double targetRatio = newPos.TotalSeconds / duration.TotalSeconds;
            targetRatio = Math.Clamp(targetRatio, 0, 1);
            AnimateSeekProgressTo(targetRatio);
            CurrentTimeText.Text = FormatTime(newPos);
            _lastDisplayedSecond = (int)newPos.TotalSeconds;

            if (_isExpanded || _isMusicExpanded) RenderProgressBar();

            await _mediaService.SeekRelativeAsync(seconds);
            
            // Ensure progress timer is running after seek
            UpdateProgressTimerState();
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("PROGRESS-SEEK-RELATIVE", ex.ToString());
        }
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
        
        bool hasTimeline = _currentMediaInfo != null &&
            (_currentMediaInfo.HasTimeline ||
             _currentMediaInfo.IsIndeterminate ||
             _currentMediaInfo.Duration.TotalSeconds > 0 ||
             _currentMediaInfo.IsAnyMediaPlaying);
        bool shouldRunProgress = isExpanded && hasTimeline;

        // Use 60fps for smooth progress updates
        _progressTimer.Interval = TimeSpan.FromMilliseconds(16);

        if (shouldRunProgress)
        {
            if (!_progressTimer.IsEnabled)
            {
                _progressTimer.Start();
            }
        }
        else
        {
            if (_progressTimer.IsEnabled)
            {
                _progressTimer.Stop();
            }
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

        // Prevent reopen-jump effect:
        // if UI already has meaningful progress, do not reset to 0 and animate again.
        // Catch-up should only run for fresh/near-zero visual state.
        if (_progressDisplayRatio > 0.02)
        {
            return;
        }

        // Only animate if position is meaningful and not too far into the video
        // If position < 1%, skip animation (too early)
        // If position > 20%, snap immediately (too far to animate smoothly)
        if (targetRatio < 0.01)
        {
            return;
        }
        
        if (targetRatio > 0.20)
        {
            // Snap immediately instead of animating
            _progressDisplayRatio = targetRatio;
            _progressTargetRatio = targetRatio;
            _progressSpringTargetRatio = targetRatio;
            ProgressBarScale.ScaleX = targetRatio;
            CurrentTimeText.Text = FormatTime(frame.Position);
            return;
        }

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
