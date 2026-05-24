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
    private DateTime _protectSpringTargetUntil = DateTime.MinValue;

    // Spring render loop is driven by a dedicated helper; this partial keeps the per-frame ratios/velocity in its own fields (and pushes them into the renderer state every time the spring restarts) because many sites across Progress
    private ProgressSpringRenderer? _springRenderer;
    private ProgressSpringRenderer Spring => _springRenderer ??= new ProgressSpringRenderer(
        applyRatio: r =>
        {
            _progressDisplayRatio = r;
            ProgressBarScale.ScaleX = r;
        },
        shouldRender: () => !_isDraggingProgress,
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
                    // Double-check: if the click point is inside our window rect, don't collapse.
                    // WindowFromPoint can miss layered/transparent windows on first render.
                    if (_hwnd != IntPtr.Zero &&
                        GetWindowRect(_hwnd, out var rc) &&
                        pt.x >= rc.Left && pt.x <= rc.Right &&
                        pt.y >= rc.Top && pt.y <= rc.Bottom)
                    {
                        return;
                    }

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
    private long _trackChangeSequence = 0;

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
            bool isFirstEverTrack = string.IsNullOrEmpty(_lastProgressSignature);
            
            if (isTrackChanged)
            {
                _lastProgressSignature = newSignature;
                
                if (isSessionSwitch)
                {
                    _lastSessionId = info.SourceAppId;
                    HandleSessionTransition();
                }
                
                // Increment sequence FIRST so any in-flight animation Completed handlers become stale
                _trackChangeSequence++;
                
                _progressEngine.Reset();
                StopCatchUpAnimation();
                StopRewindTextAnimation();

                // If a rewind animation is already running toward 0 (e.g., external seek detection
                // fired when position jumped to 0 before track metadata changed, or
                // OptimisticPrepareForPreviousTrack already started a rewind), let it finish
                // instead of starting a duplicate rewind.
                bool alreadyRewindingToZero = _isRewindAnimating && _progressTargetRatio <= 0.01;

                _isRewindAnimating = false;
                ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _progressVelocity = 0;
                _springSettleFrames = 0;
                _isSeekSpringActive = false;
                _lastRenderTime = DateTime.MinValue;
                _lastRenderedDuration = TimeSpan.Zero;
                _lastDisplayedSecond = -1;
                _progressSnapshotSequence = 0;
                _lastProgressTimelineUpdated = DateTimeOffset.MinValue;
                StopSpringRenderLoop();

                // Use the visual ScaleX value (after cancelling animations) as the rewind start point.
                // _progressDisplayRatio may already be 0 from a previous rewind's early assignment,
                // but the visual bar could still be at the old position due to FillBehavior.Stop reverting
                // to the local value set before the animation started.
                double fromRatio = Math.Clamp(ProgressBarScale.ScaleX, 0, 1);
                _progressDisplayRatio = fromRatio;
                _progressTargetRatio = 0;
                _progressSpringTargetRatio = 0;
                _lastRenderedRatio = 0;
                CurrentTimeText.Text = "0:00";
                RemainingTimeText.Text = info.Duration.TotalSeconds > 0 ? FormatTime(info.Duration) : "--:--";

                if (alreadyRewindingToZero)
                {
                    // A rewind-to-zero animation was already in progress (triggered by external
                    // seek detection when position jumped to 0 before metadata changed).
                    // Just commit to 0 — no need for a second visual rewind.
                    _progressDisplayRatio = 0;
                    ProgressBarScale.ScaleX = 0;
                }
                else if (isFirstEverTrack)
                {
                    // First track after app launch — no meaningful prior state to rewind from.
                    // Snap to 0 to avoid a spurious rewind animation caused by metadata
                    // stabilization firing multiple track-change events on boot.
                    _progressDisplayRatio = 0;
                    ProgressBarScale.ScaleX = 0;
                }
                else if (fromRatio > 0.97)
                {
                    // Track ended naturally (progress was near 100%) — snap to 0 immediately.
                    // A rewind animation from the very end looks unnatural since the user
                    // expects the bar to simply reset when a song finishes.
                    _progressDisplayRatio = 0;
                    ProgressBarScale.ScaleX = 0;
                }
                else if (fromRatio > 0.01 && (_isExpanded || _isMusicExpanded))
                {
                    // Animate rewind from current position to 0 — only when expanded
                    // (progress bar is visible). When in compact pill state the bar is
                    // hidden, so running the animation is pointless and would be seen
                    // as a "reverse" glitch if the user expands mid-rewind.
                    AnimateTrackChangeRewindToZero(fromRatio, info.Duration);
                }
                else
                {
                    _progressDisplayRatio = 0;
                    ProgressBarScale.ScaleX = 0;
                }

                // Suppress external seek detection briefly after track change to prevent
                // stale timestamps from triggering false "jump to full" animations
                _suppressExternalSeekDetectionUntil = DateTime.Now.AddSeconds(2);
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
                
                // Animate to the new position after external seek (spring)
                _progressTargetRatio = engineRatio;
                _progressSpringTargetRatio = _progressDisplayRatio;
                _progressVelocity = 0;
                _springSettleFrames = 0;
                _isSeekSpringActive = true;
                _seekSpringStartTime = DateTime.Now;
                _protectSpringTargetUntil = DateTime.UtcNow.AddSeconds(1.5);
                StartSpringRenderLoop();
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
                
                // Snap to engine position and suppress external seek detection briefly
                // to prevent the gap between spring-settled position and engine from
                // triggering a false "external seek" animation.
                _progressDisplayRatio = engineRatio;
                ProgressBarScale.ScaleX = engineRatio;
                _suppressExternalSeekDetectionUntil = DateTime.Now.AddSeconds(1.5);
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
                if (!_isClickSeekPending && DateTime.UtcNow >= _protectSpringTargetUntil)
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
                        RemainingTimeText.Text = FormatTime(frame.Duration);
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
                _progressDisplayRatio > 0.005 &&
                DateTime.Now >= _suppressExternalSeekDetectionUntil)
            {
                bool playing = frame.State == ProgressState.Playing ||
                               frame.State == ProgressState.Seeking;
                bool forwardJump = rawTargetRatio > _progressDisplayRatio &&
                                   rawDiffSeconds >= 1.2;
                bool backwardJump = rawTargetRatio < _progressDisplayRatio &&
                                    rawDiffSeconds >= 0.6;

                // When progress is near the end (last ~5%) and target jumps to near 0,
                // this is almost certainly a track skip — not a user seek. Suppress the
                // external seek animation and let the track change handler deal with it.
                bool isLikelyTrackSkip = _progressDisplayRatio > 0.92 &&
                                         rawTargetRatio < 0.05;

                // Log every potential seek detection attempt for diagnostics.
                if (rawDiffSeconds >= 0.6)
                {
                    RuntimeLog.Log("PROGRESS-SEEK",
                        $"check from={_progressDisplayRatio:F4} to={rawTargetRatio:F4} " +
                        $"diffSec={rawDiffSeconds:F2} state={frame.State} playing={playing} " +
                        $"forward={forwardJump} backward={backwardJump} likelySkip={isLikelyTrackSkip}");
                }

                if (playing && (forwardJump || backwardJump) && !isLikelyTrackSkip)
                {
                    AnimateExternalSeekTo(rawTargetRatio, frame);
                    // Suppress re-detection while the animation runs to prevent
                    // multiple triggers from SMTC latency jitter.
                    _suppressExternalSeekDetectionUntil = DateTime.Now.AddMilliseconds(800);
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
        // Only reset spring origin and velocity if spring is not already active (avoid jitter on rapid clicks)
        if (!_isSeekSpringActive)
        {
            _progressSpringTargetRatio = _progressDisplayRatio;
            _progressVelocity = 0;
        }
        _springSettleFrames = 0;
        _isSeekSpringActive = true;
        _seekSpringStartTime = DateTime.Now;
        // Reset protection so the new target takes effect immediately
        _protectSpringTargetUntil = DateTime.UtcNow.AddSeconds(1.5);
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
            // While spring animation is running from click seek, require much larger movement
            // to initiate drag — prevents accidental drag from small mouse jitter
            double threshold = _isSeekSpringActive ? 25.0 : DRAG_THRESHOLD;
            if (dist < threshold) return; 

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

        ProgressBarContainer.ReleaseMouseCapture();

        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, (System.Action)(() =>
        {

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
                // Protect spring target from being overwritten by stale engine position
                // until the engine reports the new seek position
                _protectSpringTargetUntil = DateTime.UtcNow.AddSeconds(1.5);
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

        // Use spring animation to reach target (same as click seek)
        _progressTargetRatio = targetRatio;
        if (!_isSeekSpringActive)
        {
            _progressSpringTargetRatio = _progressDisplayRatio;
            _progressVelocity = 0;
        }
        _springSettleFrames = 0;
        _isSeekSpringActive = true;
        _seekSpringStartTime = DateTime.Now;
        _protectSpringTargetUntil = DateTime.UtcNow.AddSeconds(1.5);
        StartSpringRenderLoop();
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

        // Capture sequence so Completed handler becomes a no-op if track changed mid-animation
        long seqAtStart = _trackChangeSequence;

        var anim = new DoubleAnimation(fromRatio, targetRatio, new Duration(duration))
        {
            EasingFunction = _easeExpOut6,
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(anim, 144);

        anim.Completed += (s, e) =>
        {
            // If a track change happened while this animation was running, discard the result
            if (_trackChangeSequence != seqAtStart) return;

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

    private void ProgressBar_MouseEnter(object sender, MouseEventArgs e)
    {
        _isProgressBarExpanded = true;
        AnimateProgressBarHover(true);
    }

    private void ProgressBar_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isDraggingProgress && !_isClickSeekPending)
        {
            _isProgressBarExpanded = false;
            AnimateProgressBarHover(false);
        }
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
    private TimeSpan _catchUpTargetPosition = TimeSpan.Zero;

    private void StartProgressCatchUpAnimation()
    {
        if (_currentMediaInfo == null) return;

        var frame = _progressEngine.GetUiFrame();
        if (frame.Duration.TotalSeconds <= 0) return;

        double targetRatio = frame.Position.TotalSeconds / frame.Duration.TotalSeconds;
        targetRatio = Math.Clamp(targetRatio, 0, 1);

        // Cancel any lingering rewind animation from a track change that started while collapsed
        if (_isRewindAnimating)
        {
            _isRewindAnimating = false;
            StopRewindTextAnimation();
            ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        }

        // If spring seek animation is still in progress, let it finish naturally
        // (it now continues running even when collapsed)
        if (_isSeekSpringActive && Spring.IsActive)
        {
            return;
        }

        // Animate from the last known position to the current real position
        double fromRatio = Math.Clamp(_progressDisplayRatio, 0, 1);

        // Already at target — just set directly
        if (Math.Abs(fromRatio - targetRatio) < 0.005)
        {
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

        // Scale duration with gap size: short gaps get snappy animation, large gaps get slightly longer
        double gapRatio = Math.Abs(targetRatio - fromRatio);
        int durationMs = (int)Math.Clamp(400 + gapRatio * 300, 400, 650);
        var catchUpDuration = TimeSpan.FromMilliseconds(durationMs);
        var catchUpAnim = new DoubleAnimation(fromRatio, targetRatio, new Duration(catchUpDuration))
        {
            EasingFunction = _easeExpOut6,
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(catchUpAnim, 144);

        // Capture sequence so Completed handler becomes a no-op if track changed mid-animation
        long seqAtStart = _trackChangeSequence;

        catchUpAnim.Completed += (s, e) =>
        {
            if (_trackChangeSequence != seqAtStart) return;

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

    private void AnimateTrackChangeRewindToZero(double fromRatio, TimeSpan newDuration)
    {
        fromRatio = Math.Clamp(fromRatio, 0, 1);
        double targetRatio = 0;

        // Suspend competing systems
        _isSeekSpringActive = false;
        _springSettleFrames = 0;
        _progressVelocity = 0;
        StopSpringRenderLoop();
        _isRewindAnimating = true;

        // Duration scales with how far we need to rewind (~300-450ms)
        var duration = TimeSpan.FromMilliseconds(Math.Clamp(280 + fromRatio * 180, 300, 450));

        long seqAtStart = _trackChangeSequence;

        var anim = new DoubleAnimation(fromRatio, targetRatio, new Duration(duration))
        {
            EasingFunction = _easeExpOut6,
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(anim, 144);

        anim.Completed += (s, e) =>
        {
            if (_trackChangeSequence != seqAtStart) return;

            ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressBarScale.ScaleX = 0;
            _progressDisplayRatio = 0;
            _progressTargetRatio = 0;
            _progressSpringTargetRatio = 0;
            _lastRenderedRatio = 0;
            _lastDisplayedSecond = 0;
            _isSeekSpringActive = false;
            _isClickSeekPending = false;
            _progressVelocity = 0;
            _springSettleFrames = 0;
            StopSpringRenderLoop();
            CurrentTimeText.Text = "0:00";
            RemainingTimeText.Text = newDuration.TotalSeconds > 0 ? FormatTime(newDuration) : "--:--";
            StopRewindTextAnimation();
            _isRewindAnimating = false;
            _lastRenderTime = DateTime.Now;
        };

        _progressTargetRatio = 0;
        _progressSpringTargetRatio = 0;
        _progressDisplayRatio = 0;

        ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ProgressBarScale.ScaleX = fromRatio;
        ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
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

        // Capture sequence so Completed handler becomes a no-op if track changed mid-animation
        long seqAtStart = _trackChangeSequence;

        anim.Completed += (s, e) =>
        {
            // If a track change happened while this animation was running, discard the result
            if (_trackChangeSequence != seqAtStart) return;

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

