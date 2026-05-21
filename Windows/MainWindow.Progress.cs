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
    private DateTime _blockBackwardAfterSeekUntil = DateTime.MinValue;
    private Point _mouseDownPoint;
    private const double DRAG_THRESHOLD = 3.0;  
    private DateTime _suppressExternalSeekDetectionUntil = DateTime.MinValue;

    // Spring render loop is driven by a dedicated helper; this partial keeps the per-frame ratios/velocity in its own fields (and pushes them into the renderer state every time the spring restarts) because many sites across Progress
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
        // Keep the renderer's mirror of the physics state in sync with the fields the rest of this partial mutates directly.
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
        // Pull the final state back into the partial's fields so the rest of the code keeps seeing consistent numbers.
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

    private DateTime _lastVolumeSyncUtc = DateTime.MinValue;

    private void ProgressTimer_Tick(object? sender, EventArgs e)
    {
        // Always render progress bar when expanded, regardless of play state This ensures UI updates even when user is seeking or paused
        if (_isExpanded || _isMusicExpanded)
        {
            if (_currentMediaInfo != null)
            {
                RenderProgressBar();
            }
        }

        // Update synced lyrics display
        if (_isExpanded && _isLyricsActive)
        {
            UpdateLyricsDisplay();
        }

        // Throttle volume sync to ~2Hz (every 500ms) instead of 60fps
        if (_isExpanded && _isMusicExpanded && !_isDraggingVolume)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastVolumeSyncUtc).TotalMilliseconds >= 500)
            {
                _lastVolumeSyncUtc = now;
                if (_mediaService.TryGetCurrentSessionVolume(out float volume, out bool isMuted))
                {
                    _currentVolume = volume;
                    VolumeBarScale.ScaleX = _currentVolume;
                    UpdateVolumeIcon(_currentVolume, isMuted);
                }
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

        // Thumbnail-only updates carry stale Position/LastUpdated from when the background fetch started
        if (info.IsThumbnailOnlyUpdate)
        {
            UpdateProgressTimerState();
            if (_isExpanded || _isMusicExpanded) RenderProgressBar();
            return;
        }

        ProgressSection.Visibility = Visibility.Visible;
        ProgressSection.Opacity = 1;

        bool showProgressDetails = info.IsAnyMediaPlaying || info.Duration.TotalSeconds > 0;

        if (showProgressDetails || info.HasTimeline || info.IsIndeterminate)
        {
            if (_isDraggingProgress) return;

            // Track identity should not include duration jitter
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
        _blockBackwardAfterSeekUntil = DateTime.MinValue;
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
        // While the rewind animation is running, the WPF DoubleAnimation owns ProgressBarScale
        if (_isRewindAnimating) return;

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

        // Detect external seek (e
        if (_isCatchUpAnimating)
        {
            double ratioDiff = Math.Abs(engineRatio - _lastRenderedRatio);
            // If position jumped more than 1% (external seek detected) This is adaptive: ~24 seconds for 40-min video, ~4 seconds for 6-min video
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
            // If spring has finished (settled or timed out), transition to normal rendering
            if (!Spring.IsActive)
            {
                _isSeekSpringActive = false;
                _progressDisplayRatio = Spring.DisplayRatio;
                _progressVelocity = 0;
                _springSettleFrames = 0;
                RuntimeLog.Log("PROGRESS-SPRING", $"settled at ratio={_progressDisplayRatio:F4} engineRatio={engineRatio:F4}");
                // Fall through to normal rendering below
            }
            else
            {
                // Safety: if spring is active but render hook was not running, restart it.
                if (!Spring.IsHooked)
                {
                    StartSpringRenderLoop();
                }

                // Don't overwrite spring target from engine during pending click seek (engine hasn't been notified yet)
                if (!_isClickSeekPending)
                {
                    _progressTargetRatio = engineRatio;
                    if (Math.Abs(_progressTargetRatio - _progressSpringTargetRatio) > 0.12)
                    {
                        _seekSpringStartTime = DateTime.Now;
                    }
                }
                _lastRenderedDuration = frame.Duration;  
                // Update time text while spring is animating
                if (frame.Duration.TotalSeconds > 0)
                {
                    var pos = TimeSpan.FromSeconds(_progressDisplayRatio * frame.Duration.TotalSeconds);
                    int sec = (int)pos.TotalSeconds;
                    if (sec != _lastDisplayedSecond)
                    {
                        _lastDisplayedSecond = sec;
                        CurrentTimeText.Text = FormatTime(pos);
                        RemainingTimeText.Text = FormatTime(frame.Duration - pos);
                    }
                }
                return;
            }
        }

        if (!_isSeekSpringActive)
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

            // ── External seek detection ───────────────────────────────────── User scrubs from outside the notch (YouTube player, taskbar SMTC controls, keyboard media keys)
            if (!_isDraggingProgress &&
                !_isRewindAnimating &&
                !_isCatchUpAnimating &&
                !_isClickSeekPending &&
                !_isSeekSpringActive &&
                frame.Duration.TotalSeconds > 0 &&
                _progressDisplayRatio > 0 &&
                DateTime.Now >= _suppressExternalSeekDetectionUntil)
            {
                bool playing = frame.State == ProgressState.Playing ||
                               frame.State == ProgressState.Seeking;
                bool forwardJump = rawTargetRatio > _progressDisplayRatio &&
                                   rawDiffSeconds >= 1.2;
                bool backwardJump = rawTargetRatio < _progressDisplayRatio &&
                                    rawDiffSeconds >= 0.6;

                // Log every potential seek detection attempt for diagnostics.
                if (rawDiffSeconds >= 0.6)
                {
                    RuntimeLog.Log("PROGRESS-SEEK",
                        $"check from={_progressDisplayRatio:F4} to={rawTargetRatio:F4} " +
                        $"diffSec={rawDiffSeconds:F2} state={frame.State} playing={playing} " +
                        $"forward={forwardJump} backward={backwardJump}");
                }

                if (playing && (forwardJump || backwardJump))
                {
                    AnimateExternalSeekTo(rawTargetRatio, frame);
                    _lastRenderTime = now;
                    _lastRenderedDuration = frame.Duration;
                    return;
                }
            }

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

                // Platform-aware backward jump threshold Browser sources have higher latency but still need responsive correction Balance between: accepting late snapshots vs
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
                bool isPostSeekStabilization = DateTime.Now < _blockBackwardAfterSeekUntil;

                if (_progressTargetRatio < _progressDisplayRatio)
                {
                    double backwardSeconds = (_progressDisplayRatio - _progressTargetRatio) * frame.Duration.TotalSeconds;

                    if (isUserSeekWindow)
                    {
                        // User is seeking — allow backward movement freely
                    }
                    else if (isPostSeekStabilization)
                    {
                        // Just finished a seek — block backward snap to prevent jitter on pause
                        _progressTargetRatio = _progressDisplayRatio;
                    }
                    else if (isRealtimeProgressing)
                    {
                        // During normal playback, block backward jumps to prevent visible snapping
                        bool isBrowserSourceForBackward = _currentMediaInfo != null &&
                            (string.Equals(_currentMediaInfo.MediaSource, "Browser", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(_currentMediaInfo.MediaSource, "YouTube", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(_currentMediaInfo.MediaSource, "SoundCloud", StringComparison.OrdinalIgnoreCase));

                        double allowedBackward = isBrowserSourceForBackward ? 0.5 : 0.08;
                        if (backwardSeconds > allowedBackward)
                        {
                            _progressTargetRatio = _progressDisplayRatio;
                        }
                    }
                    else if (backwardSeconds > backwardThreshold)
                    {
                        // Paused + large backward: cap step per frame
                        double maxBackwardStepSeconds = 0.22;
                        double maxBackwardRatioStep = maxBackwardStepSeconds / frame.Duration.TotalSeconds;
                        double cappedTarget = Math.Max(_progressTargetRatio, _progressDisplayRatio - maxBackwardRatioStep);
                        _progressTargetRatio = cappedTarget;
                    }
                    // else: paused + small backward — allow correction via lerp
                }

                // Jump directly to target — no lerp/smoothing animation
                _progressDisplayRatio = _progressTargetRatio;
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
            if (wasClickSeek && !wasDragging)
            {
                // Click seek (no drag): spring is already animating to target
                _allowProgressBackwardRenderUntil = DateTime.Now.AddSeconds(3);
                _blockBackwardAfterSeekUntil = DateTime.Now.AddSeconds(3.5);
                _suppressExternalSeekDetectionUntil = DateTime.Now.AddSeconds(3);
                _progressEngine.NotifyUserSeek(_dragSeekPosition);

                var duration2 = _progressEngine.GetUiFrame().Duration;
                if (duration2.TotalSeconds > 0)
                {
                    bool seekToEnd = _dragSeekPosition >= duration2 - TimeSpan.FromMilliseconds(900);
                    if (seekToEnd)
                    {
                        _lastProgressTimelineUpdated = DateTimeOffset.MinValue;
                        _lastProgressTimelineKey = "";
                        _allowProgressBackwardRenderUntil = DateTime.Now.AddSeconds(6);
                    }
                }

                _lastDisplayedSecond = (int)_dragSeekPosition.TotalSeconds;
                CurrentTimeText.Text = FormatTime(_dragSeekPosition);
                UpdateProgressTimerState();

                await _mediaService.SeekAsync(_dragSeekPosition);
            }
            else
            {
                await SeekToPosition(_dragSeekPosition);
            }
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

        // Jump directly — no spring animation
        _progressDisplayRatio = targetRatio;
        _progressTargetRatio = targetRatio;
        _progressSpringTargetRatio = targetRatio;
        _progressVelocity = 0;
        _springSettleFrames = 0;
        _isSeekSpringActive = false;
        StopSpringRenderLoop();
        ProgressBarScale.ScaleX = targetRatio;
    }

    private DispatcherTimer? _rewindTextTimer;
    private bool _isRewindAnimating = false;

    private void AnimateProgressRewindTo(double targetRatio)
    {
        targetRatio = Math.Clamp(targetRatio, 0, 1);

        double fromRatio = Math.Clamp(_progressDisplayRatio, 0, 1);
        double delta = fromRatio - targetRatio;

        // Cancel any in-flight rewind text timer before starting a new one.
        StopRewindTextAnimation();

        // No meaningful distance — just commit the target.
        if (delta <= 0.005)
        {
            ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressBarScale.ScaleX = targetRatio;
            _progressDisplayRatio = targetRatio;
            _progressTargetRatio = targetRatio;
            _progressSpringTargetRatio = targetRatio;
            _progressVelocity = 0;
            _springSettleFrames = 0;
            _isSeekSpringActive = false;
            CurrentTimeText.Text = FormatTime(GetPositionForRatio(targetRatio));
            return;
        }

        // Suspend the spring/seek system so it doesn't fight the rewind anim.
        _isSeekSpringActive = false;
        _springSettleFrames = 0;
        _progressVelocity = 0;
        StopSpringRenderLoop();
        _isRewindAnimating = true;

        // Duration scales with distance: short rewinds feel zippy, long rewinds get a touch more time. Clamp so it never drags.
        var duration = TimeSpan.FromMilliseconds(Math.Clamp(220 + delta * 320, 260, 480));

        var anim = new DoubleAnimation(fromRatio, targetRatio, new Duration(duration))
        {
            EasingFunction = _easeExpOut6,
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(anim, 144);

        anim.Completed += (s, e) =>
        {
            ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressBarScale.ScaleX = targetRatio;
            _progressDisplayRatio = targetRatio;
            _progressTargetRatio = targetRatio;
            _progressSpringTargetRatio = targetRatio;
            _lastRenderedRatio = targetRatio;
            _lastDisplayedSecond = (int)GetPositionForRatio(targetRatio).TotalSeconds;
            CurrentTimeText.Text = FormatTime(GetPositionForRatio(targetRatio));
            StopRewindTextAnimation();
            _isRewindAnimating = false;
        };

        // Mirror the WPF animation in the engine state so RenderProgressBar (which can be invoked by ProgressTimer between frames) does not overwrite ScaleX with a competing value
        _progressTargetRatio = targetRatio;
        _progressSpringTargetRatio = targetRatio;
        _progressDisplayRatio = targetRatio;

        ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);

        // Drive the time-text update from a parallel timer so the text glides alongside the bar
        var startTime = DateTime.UtcNow;
        var totalMs = duration.TotalMilliseconds;
        _rewindTextTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _rewindTextTimer.Tick += (s, e) =>
        {
            double t = (DateTime.UtcNow - startTime).TotalMilliseconds / totalMs;
            if (t >= 1.0)
            {
                StopRewindTextAnimation();
                return;
            }
            // Mirror ExponentialEase EaseOut, exponent=6.
            double eased = 1 - Math.Pow(2, -6 * 10 * t);
            // Normalize so eased(0)=0, eased(1)=1.
            double maxEased = 1 - Math.Pow(2, -60);
            eased = Math.Clamp(eased / maxEased, 0, 1);
            double ratio = fromRatio + (targetRatio - fromRatio) * eased;
            var pos = GetPositionForRatio(ratio);
            int sec = (int)pos.TotalSeconds;
            if (sec != _lastDisplayedSecond)
            {
                _lastDisplayedSecond = sec;
                CurrentTimeText.Text = FormatTime(pos);
            }
        };
        _rewindTextTimer.Start();
    }

    private void StopRewindTextAnimation()
    {
        if (_rewindTextTimer != null)
        {
            _rewindTextTimer.Stop();
            _rewindTextTimer = null;
        }
    }

    private TimeSpan GetPositionForRatio(double ratio)
    {
        var duration = _progressEngine.GetUiFrame().Duration;
        if (duration.TotalSeconds <= 0) return TimeSpan.Zero;
        return TimeSpan.FromSeconds(Math.Clamp(ratio, 0, 1) * duration.TotalSeconds);
    }

    private async Task SeekToPosition(TimeSpan newPos)
    {
        var duration = _progressEngine.GetUiFrame().Duration;
        if (duration.TotalSeconds <= 0) return;

        try 
        {
            _allowProgressBackwardRenderUntil = DateTime.Now.AddSeconds(3);
            _blockBackwardAfterSeekUntil = DateTime.Now.AddSeconds(3.5);
            _suppressExternalSeekDetectionUntil = DateTime.Now.AddSeconds(3);
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
            _blockBackwardAfterSeekUntil = DateTime.Now.AddSeconds(3.5);
            _suppressExternalSeekDetectionUntil = DateTime.Now.AddSeconds(3);
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

            // Use smooth animation instead of snap for seek controls
            AnimateExternalSeekTo(targetRatio, frame);

            CurrentTimeText.Text = FormatTime(newPos);
            _lastDisplayedSecond = (int)newPos.TotalSeconds;

            // Use absolute seek with the engine's predicted position to avoid drift between UI (predicted) and SMTC (stale timeline position)
            await _mediaService.SeekToAbsoluteAsync(newPos);
            
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
        var anim = new System.Windows.Media.Animation.DoubleAnimation(1.8, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        ProgressBarMainScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void ProgressBar_MouseLeave(object sender, MouseEventArgs e)
    {
        var anim = new System.Windows.Media.Animation.DoubleAnimation(1.0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        ProgressBarMainScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
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
        if (_currentMediaInfo == null) return;

        var frame = _progressEngine.GetUiFrame();
        if (frame.Duration.TotalSeconds <= 0) return;

        double targetRatio = frame.Position.TotalSeconds / frame.Duration.TotalSeconds;
        targetRatio = Math.Clamp(targetRatio, 0, 1);

        // Animate from the last known position to the current real position
        double fromRatio = Math.Clamp(_progressDisplayRatio, 0, 1);
        if (Math.Abs(fromRatio - targetRatio) < 0.005)
        {
            // Already at target — just set directly
            ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressBarScale.ScaleX = targetRatio;
            _progressDisplayRatio = targetRatio;
            _progressTargetRatio = targetRatio;
            _progressSpringTargetRatio = targetRatio;
            _lastRenderedRatio = targetRatio;
            _progressVelocity = 0;
            CurrentTimeText.Text = FormatTime(frame.Position);
            RemainingTimeText.Text = FormatTime(frame.Duration);
            return;
        }

        _progressTargetRatio = targetRatio;
        _progressSpringTargetRatio = targetRatio;
        _progressVelocity = 0;
        ProgressBarScale.ScaleX = fromRatio;

        var catchUpDuration = TimeSpan.FromMilliseconds(550);
        var catchUpAnim = new DoubleAnimation(fromRatio, targetRatio, new Duration(catchUpDuration))
        {
            EasingFunction = _easeExpOut6,
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(catchUpAnim, 144);

        catchUpAnim.Completed += (s, e) =>
        {
            ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressBarScale.ScaleX = targetRatio;
            _progressDisplayRatio = targetRatio;
            _lastRenderedRatio = targetRatio;
        };

        ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, catchUpAnim);
        CurrentTimeText.Text = FormatTime(frame.Position);
        RemainingTimeText.Text = FormatTime(frame.Duration);
    }

    private void StopCatchUpAnimation()
    {
        // No-op: catch-up animation has been removed (progress jumps directly)
        _isCatchUpAnimating = false;
        if (_catchUpTimer != null)
        {
            _catchUpTimer.Stop();
            _catchUpTimer = null;
        }
    }

    private void AnimateExternalSeekTo(double targetRatio, UiProgressFrame frame)
    {
        targetRatio = Math.Clamp(targetRatio, 0, 1);
        double fromRatio = Math.Clamp(_progressDisplayRatio, 0, 1);
        double delta = Math.Abs(fromRatio - targetRatio);

        StopRewindTextAnimation();

        // Tiny delta — no animation worth running, commit and return.
        if (delta <= 0.005)
        {
            RuntimeLog.Log("PROGRESS-SEEK", $"animate skipped (delta={delta:F4})");
            ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressBarScale.ScaleX = targetRatio;
            _progressDisplayRatio = targetRatio;
            _progressTargetRatio = targetRatio;
            _progressSpringTargetRatio = targetRatio;
            _progressVelocity = 0;
            _springSettleFrames = 0;
            _isSeekSpringActive = false;
            CurrentTimeText.Text = FormatTime(GetPositionForRatio(targetRatio));
            _lastRenderedRatio = targetRatio;
            return;
        }

        // Suspend competing systems so the WPF animation owns ScaleX while it runs.
        _isSeekSpringActive = false;
        _springSettleFrames = 0;
        _progressVelocity = 0;
        StopSpringRenderLoop();
        _isRewindAnimating = true; // share the same render-skip guard

        // Duration scales with distance, capped to stay snappy
        var duration = TimeSpan.FromMilliseconds(Math.Clamp(220 + delta * 320, 240, 420));

        RuntimeLog.Log("PROGRESS-SEEK",
            $"animate start from={fromRatio:F4} to={targetRatio:F4} delta={delta:F4} dur={duration.TotalMilliseconds:F0}ms");

        var anim = new DoubleAnimation(fromRatio, targetRatio, new Duration(duration))
        {
            EasingFunction = _easeExpOut6,
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(anim, 144);

        anim.Completed += (s, e) =>
        {
            RuntimeLog.Log("PROGRESS-SEEK", $"animate completed -> {targetRatio:F4}");
            ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressBarScale.ScaleX = targetRatio;
            _progressDisplayRatio = targetRatio;
            _progressTargetRatio = targetRatio;
            _progressSpringTargetRatio = targetRatio;
            _lastRenderedRatio = targetRatio;
            _lastDisplayedSecond = (int)GetPositionForRatio(targetRatio).TotalSeconds;
            CurrentTimeText.Text = FormatTime(GetPositionForRatio(targetRatio));
            StopRewindTextAnimation();
            _isRewindAnimating = false;
            _lastRenderTime = DateTime.Now;
            _blockBackwardAfterSeekUntil = DateTime.Now.AddSeconds(3.5);
        };

        // IMPORTANT: do NOT pre-commit `_progressDisplayRatio = targetRatio` here
        _progressTargetRatio = targetRatio;
        _progressSpringTargetRatio = targetRatio;

        // Force the animation to take over the DP
        ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ProgressBarScale.ScaleX = fromRatio;
        ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);

        // Glide the time text alongside the bar.
        var startTime = DateTime.UtcNow;
        var totalMs = duration.TotalMilliseconds;
        _rewindTextTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _rewindTextTimer.Tick += (s, e) =>
        {
            double t = (DateTime.UtcNow - startTime).TotalMilliseconds / totalMs;
            if (t >= 1.0)
            {
                CurrentTimeText.Text = FormatTime(GetPositionForRatio(targetRatio));
                StopRewindTextAnimation();
                return;
            }
            // Match ExponentialEase EaseOut, exponent=6.
            double eased = 1 - Math.Pow(2, -6 * 10 * t);
            double current = fromRatio + (targetRatio - fromRatio) * eased;
            CurrentTimeText.Text = FormatTime(GetPositionForRatio(current));
        };
        _rewindTextTimer.Start();
    }

    private double EaseCubicInOut(double t)
    {
        return t < 0.5
            ? 4 * t * t * t
            : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    }

    #endregion
}

