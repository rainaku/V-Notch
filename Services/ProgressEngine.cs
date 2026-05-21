using System;
using VNotch.Models;

namespace VNotch.Services;

public enum ProgressState
{
    Idle,
    Loading,
    Playing,
    Paused,
    Seeking,
    Stopped,
    Unknown
}

public struct ProgressSnapshot
{
    public TimeSpan Position;
    public TimeSpan Duration;
    public bool IsPlaying;
    public bool IsYouTube;  // Actually means "IsBrowserSource" - includes YouTube, SoundCloud, Spotify Web, and all browser-based media
    public double PlaybackRate;
    public bool IsSeekEnabled;
    public bool IsIndeterminate;
    public DateTime Timestamp;
    public long SequenceNumber;  
}

public struct UiProgressFrame
{
    public TimeSpan Position;
    public TimeSpan Duration;
    public bool ShowIndeterminate;
    public ProgressState State;
    public bool DurationJustChanged;  
}

public class ProgressEngine
{
    private ProgressState _state = ProgressState.Idle;

    private TimeSpan _basePosition = TimeSpan.Zero;
    private DateTime _baseTimeUtc = DateTime.MinValue;
    private TimeSpan _duration = TimeSpan.Zero;
    private bool _isPlaying;

    private double _playbackRate = 1.0;
    private bool _isIndeterminate;
    private bool _isSeekEnabled;
    private bool _durationJustChanged = false;  
    private bool _isYouTube = false;  // Track if source is browser-based  

    private DateTime _lastSnapshotTimestampUtc = DateTime.MinValue;
    private long _lastSnapshotSequence = -1;  
    private TimeSpan _lastSnapshotPosition = TimeSpan.Zero;  // Track last snapshot position
    private DateTime _seekDebounceEndUtc = DateTime.MinValue;
    private DateTime _allowBackwardUntilUtc = DateTime.MinValue;
    private DateTime _seekPauseGraceUntilUtc = DateTime.MinValue;
    private bool _pendingPauseConfirmation = false;
    private DateTime _pendingPauseStartedUtc = DateTime.MinValue;
    private TimeSpan _pendingPausePosition = TimeSpan.Zero;

    private static readonly TimeSpan SnapshotOutOfOrderTolerance = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan PauseBackstepTolerance = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan ResumeBackstepTolerance = TimeSpan.FromMilliseconds(350);
    
    private static readonly TimeSpan IgnoreCorrectionThreshold = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan SmoothCorrectionThreshold = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SmoothBackwardCap = TimeSpan.FromMilliseconds(400);

    private static readonly TimeSpan DurationChangeSeekWindow = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan UserSeekDebounceDuration = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan UserSeekPauseGraceDuration = TimeSpan.FromMilliseconds(3200);
    private static readonly TimeSpan PauseConfirmationWindow = TimeSpan.FromMilliseconds(700);

    private readonly object _lock = new object();

    public void OnMediaSnapshot(ProgressSnapshot snapshot)
    {
        lock (_lock)
        {
            DateTime nowUtc = DateTime.UtcNow;
            DateTime snapshotTsUtc = snapshot.Timestamp.Kind == DateTimeKind.Utc
                ? snapshot.Timestamp
                : snapshot.Timestamp.ToUniversalTime();
            TimeSpan effectiveSnapshotPosition = GetEffectiveSnapshotPosition(snapshot, snapshotTsUtc, nowUtc);

            if (snapshot.SequenceNumber > 0 && snapshot.SequenceNumber <= _lastSnapshotSequence)
            {
                return;
            }

            if (_lastSnapshotTimestampUtc != DateTime.MinValue &&
                snapshotTsUtc < _lastSnapshotTimestampUtc - SnapshotOutOfOrderTolerance)
            {
                return;
            }
            
            // Reject duplicate snapshots (same timestamp AND same position) This prevents stale snapshots from being reused and causing backward jumps
            if (_lastSnapshotTimestampUtc != DateTime.MinValue &&
                snapshotTsUtc == _lastSnapshotTimestampUtc &&
                Math.Abs((snapshot.Position - _lastSnapshotPosition).TotalSeconds) < 0.01)
            {
                return;
            }

            if (_baseTimeUtc != DateTime.MinValue && _isPlaying && _duration > TimeSpan.Zero)
            {
                var currentPredicted = PredictPosition(nowUtc);
                var snapshotPredicted = effectiveSnapshotPosition;
                var backwardDiff = currentPredicted - snapshotPredicted;
                
                bool likelySessionSwitch = snapshot.Duration > TimeSpan.Zero && 
                    Math.Abs((snapshot.Duration - _duration).TotalSeconds) > Math.Max(5.0, _duration.TotalSeconds * 0.1);
                
                // User seek detection: large backward jump in position Must be more tolerant than backward threshold to avoid false rejections
                bool likelyUserSeekBackward = backwardDiff.TotalSeconds > 5.0 && 
                    Math.Abs((effectiveSnapshotPosition - _basePosition).TotalSeconds) > 5.0;
                
                // Platform-aware backward threshold Browser sources have higher latency but need balance Too high (3
                double backwardThreshold = snapshot.IsYouTube ? 2.0 : 0.5;
                
                if (backwardDiff.TotalSeconds > backwardThreshold && !likelySessionSwitch && !likelyUserSeekBackward)
                {
                    return;
                }
            }

            if (snapshotTsUtc > _lastSnapshotTimestampUtc)
            {
                _lastSnapshotTimestampUtc = snapshotTsUtc;
            }
            
            if (snapshot.SequenceNumber > _lastSnapshotSequence)
            {
                _lastSnapshotSequence = snapshot.SequenceNumber;
            }
            
            // Track last snapshot position for duplicate detection
            _lastSnapshotPosition = snapshot.Position;

            TimeSpan previousDuration = _duration;
            if (snapshot.Duration.TotalSeconds > 0)
            {
                _duration = snapshot.Duration;
            }

            _isIndeterminate = snapshot.IsIndeterminate;
            _isSeekEnabled = snapshot.IsSeekEnabled;
            _isYouTube = snapshot.IsYouTube;  // Update platform flag

            var reportedRate = snapshot.PlaybackRate;
            if (double.IsNaN(reportedRate) || double.IsInfinity(reportedRate) || reportedRate <= 0)
            {
                reportedRate = 1.0;
            }

            reportedRate = Math.Clamp(reportedRate, 0.5, 2.5);
            if (snapshot.IsYouTube && Math.Abs(reportedRate - 1.0) <= 0.12)
            {
                reportedRate = 1.0;
            }

            _playbackRate = Math.Abs(reportedRate - 1.0) <= 0.035 ? 1.0 : reportedRate;

            if (snapshot.IsPlaying && _pendingPauseConfirmation)
            {
                _pendingPauseConfirmation = false;
                _pendingPauseStartedUtc = DateTime.MinValue;
            }

            bool durationChanged = DidDurationChange(previousDuration, _duration);
            _durationJustChanged = durationChanged;  
            
            if (durationChanged && snapshot.IsPlaying)
            {
                TimeSpan predictedAtDurationChange = ClampPosition(PredictPosition(nowUtc));
                bool likelyTrackRestart = effectiveSnapshotPosition.TotalSeconds <=
                    Math.Min(5.0, _duration.TotalSeconds > 0 ? _duration.TotalSeconds * 0.15 : 5.0);
                bool predictedOutsideNewDuration =
                    _duration.TotalSeconds > 0 &&
                    predictedAtDurationChange > _duration + TimeSpan.FromSeconds(1);

                if (likelyTrackRestart || predictedOutsideNewDuration)
                {
                    TimeSpan resetPos = ClampPosition(effectiveSnapshotPosition);
                    _basePosition = resetPos;
                    _baseTimeUtc = nowUtc;
                    _isPlaying = true;
                    _state = ProgressState.Playing;
                    _seekDebounceEndUtc = nowUtc + DurationChangeSeekWindow;
                    _allowBackwardUntilUtc = nowUtc + DurationChangeSeekWindow;
                    return;
                }
            }

            if (!snapshot.IsPlaying)
            {
                // Core fix: many sources briefly report "paused/changing" right after seek while playback actually continues
                bool inSeekStabilizationWindow = nowUtc < _seekDebounceEndUtc || nowUtc < _seekPauseGraceUntilUtc;
                if (inSeekStabilizationWindow && _state == ProgressState.Seeking)
                {
                    TimeSpan observedDuringSeek = ClampPosition(snapshot.Position);
                    if (observedDuringSeek > _basePosition)
                    {
                        _basePosition = observedDuringSeek;
                        _baseTimeUtc = nowUtc;
                    }
                    return;
                }

                TimeSpan pausedPos = ClampPosition(snapshot.Position);

                // Additional core fix: debounce transient single-frame pause glitches (commonly observed on Spotify/browser bridge) before committing Paused state
                if (_isPlaying || _state == ProgressState.Playing || _state == ProgressState.Seeking)
                {
                    TimeSpan predictedAtPending = ClampPosition(PredictPosition(nowUtc));
                    if (pausedPos < predictedAtPending - PauseBackstepTolerance)
                    {
                        pausedPos = predictedAtPending;
                    }

                    if (!_pendingPauseConfirmation)
                    {
                        _pendingPauseConfirmation = true;
                        _pendingPauseStartedUtc = nowUtc;
                        _pendingPausePosition = pausedPos;

                        // Soft-freeze: lock prediction to the current position so the UI stops advancing immediately on the first pause snapshot.
                        _isPlaying = false;
                        _basePosition = pausedPos;
                        _baseTimeUtc = nowUtc;
                        return;
                    }

                    _pendingPausePosition = pausedPos;
                    // Keep the soft-freeze anchor up to date so any jitter doesn't leak through.
                    _isPlaying = false;
                    _basePosition = pausedPos;
                    _baseTimeUtc = nowUtc;

                    var pendingAge = nowUtc - _pendingPauseStartedUtc;
                    if (pendingAge < PauseConfirmationWindow)
                    {
                        return;
                    }

                    pausedPos = _pendingPausePosition;
                }
                else
                {
                    _pendingPauseConfirmation = false;
                    _pendingPauseStartedUtc = DateTime.MinValue;
                }

                if (_isPlaying || _state == ProgressState.Playing || _state == ProgressState.Seeking)
                {
                    TimeSpan predictedPauseNow = ClampPosition(PredictPosition(nowUtc));
                    if (pausedPos < predictedPauseNow - PauseBackstepTolerance)
                    {
                        pausedPos = predictedPauseNow;
                    }
                }

                _isPlaying = false;
                _state = ProgressState.Paused;
                _basePosition = pausedPos;
                _baseTimeUtc = nowUtc;
                
                _seekDebounceEndUtc = DateTime.MinValue;
                _seekPauseGraceUntilUtc = DateTime.MinValue;
                _pendingPauseConfirmation = false;
                _pendingPauseStartedUtc = DateTime.MinValue;
                return;
            }

            if (!_isPlaying || (_state != ProgressState.Playing && _state != ProgressState.Seeking))
            {
                TimeSpan startPos = ClampPosition(effectiveSnapshotPosition);
                if (_state == ProgressState.Paused && startPos < _basePosition - ResumeBackstepTolerance)
                {
                    startPos = _basePosition;
                }

                _isPlaying = true;
                _state = ProgressState.Playing;
                _basePosition = startPos;
                _baseTimeUtc = nowUtc;
                _seekPauseGraceUntilUtc = DateTime.MinValue;
                _pendingPauseConfirmation = false;
                _pendingPauseStartedUtc = DateTime.MinValue;
                
                return;
            }

            if (nowUtc < _seekDebounceEndUtc)
            {
                
                return;
            }

            if (_state == ProgressState.Seeking)
            {
                _state = ProgressState.Playing;
                _seekPauseGraceUntilUtc = DateTime.MinValue;
            }

            TimeSpan observedPos = ClampPosition(effectiveSnapshotPosition);
            TimeSpan predictedNow = ClampPosition(PredictPosition(nowUtc));
            TimeSpan diff = observedPos - predictedNow;
            double absDiffSeconds = Math.Abs(diff.TotalSeconds);

            if (absDiffSeconds < IgnoreCorrectionThreshold.TotalSeconds)
            {
                return;
            }

            if (absDiffSeconds < SmoothCorrectionThreshold.TotalSeconds)
            {
                // For non-browser sources (Spotify), never pull position backward during playback
                if (!_isYouTube && diff < TimeSpan.Zero)
                {
                    // Instead of snapping backward, slightly slow down by nudging base forward less aggressively
                    double slowdownFactor = 0.15;
                    double nudge = diff.TotalSeconds * slowdownFactor;
                    double correctedSeconds = predictedNow.TotalSeconds + nudge;
                    // Never go below current predicted minus a tiny threshold
                    double minAllowed = predictedNow.TotalSeconds - 0.05;
                    if (correctedSeconds < minAllowed)
                        correctedSeconds = minAllowed;
                    _basePosition = ClampPosition(TimeSpan.FromSeconds(correctedSeconds));
                    _baseTimeUtc = nowUtc;
                    return;
                }

                const double correctionFactor = 0.9;
                double correctedSeconds2 = predictedNow.TotalSeconds + (diff.TotalSeconds * correctionFactor);

                if (diff < TimeSpan.Zero)
                {
                    double minAllowedBackward = predictedNow.TotalSeconds - SmoothBackwardCap.TotalSeconds;
                    if (correctedSeconds2 < minAllowedBackward)
                    {
                        correctedSeconds2 = minAllowedBackward;
                    }
                }

                _basePosition = ClampPosition(TimeSpan.FromSeconds(correctedSeconds2));
                _baseTimeUtc = nowUtc;
                return;
            }

            _basePosition = observedPos;
            _baseTimeUtc = nowUtc;
            _state = ProgressState.Playing;
            _allowBackwardUntilUtc = nowUtc + TimeSpan.FromMilliseconds(900);
            _seekDebounceEndUtc = nowUtc + TimeSpan.FromMilliseconds(600);
            _seekPauseGraceUntilUtc = DateTime.MinValue;
            _pendingPauseConfirmation = false;
            _pendingPauseStartedUtc = DateTime.MinValue;
        }
    }

    public void NotifyUserSeek(TimeSpan position)
    {
        lock (_lock)
        {
            DateTime nowUtc = DateTime.UtcNow;
            TimeSpan clampedPos = ClampPosition(position);

            _state = ProgressState.Seeking;
            _isPlaying = true;
            _basePosition = clampedPos;
            _baseTimeUtc = nowUtc;
            _seekDebounceEndUtc = nowUtc + UserSeekDebounceDuration;
            _seekPauseGraceUntilUtc = nowUtc + UserSeekPauseGraceDuration;
            _allowBackwardUntilUtc = nowUtc + TimeSpan.FromMilliseconds(900);
            _pendingPauseConfirmation = false;
            _pendingPauseStartedUtc = DateTime.MinValue;
        }
    }

    public void NotifyUserPlayPause(bool isPlaying)
    {
        lock (_lock)
        {
            DateTime nowUtc = DateTime.UtcNow;

            if (!isPlaying && _isPlaying)
            {
                // User pressed pause — freeze at current predicted position
                _basePosition = ClampPosition(PredictPosition(nowUtc));
                _baseTimeUtc = nowUtc;
                _isPlaying = false;
                _state = ProgressState.Paused;
                _pendingPauseConfirmation = false;
                _pendingPauseStartedUtc = DateTime.MinValue;
            }
            else if (isPlaying && !_isPlaying)
            {
                // User pressed play — resume from current position
                _baseTimeUtc = nowUtc;
                _isPlaying = true;
                _state = ProgressState.Playing;
                _pendingPauseConfirmation = false;
                _pendingPauseStartedUtc = DateTime.MinValue;
            }
        }
    }

    private static TimeSpan GetEffectiveSnapshotPosition(ProgressSnapshot snapshot, DateTime snapshotTsUtc, DateTime nowUtc)
    {
        TimeSpan effectivePosition = snapshot.Position;

        if (!snapshot.IsPlaying)
        {
            return effectivePosition;
        }

        if (snapshotTsUtc == DateTime.MinValue || snapshotTsUtc > nowUtc.AddMilliseconds(250))
        {
            return effectivePosition;
        }

        TimeSpan snapshotAge = nowUtc - snapshotTsUtc;
        if (snapshotAge <= TimeSpan.FromMilliseconds(100))
        {
            return effectivePosition;
        }

        // For native apps, cap compensation at a shorter window since MediaDetectionService already handles most compensation
        TimeSpan maxCompensationWindow = snapshot.IsYouTube
            ? TimeSpan.FromMinutes(2)
            : TimeSpan.FromSeconds(10);

        if (snapshot.IsYouTube && snapshot.Duration > TimeSpan.Zero)
        {
            TimeSpan durationWindow = snapshot.Duration + TimeSpan.FromSeconds(5);
            maxCompensationWindow = durationWindow < TimeSpan.FromHours(4)
                ? durationWindow
                : TimeSpan.FromHours(4);
        }

        if (snapshotAge > maxCompensationWindow)
        {
            return effectivePosition;
        }

        double playbackRate = snapshot.PlaybackRate;
        if (double.IsNaN(playbackRate) || double.IsInfinity(playbackRate) || playbackRate <= 0)
        {
            playbackRate = 1.0;
        }

        playbackRate = Math.Clamp(playbackRate, 0.5, 2.5);
        effectivePosition += TimeSpan.FromSeconds(snapshotAge.TotalSeconds * playbackRate);

        if (snapshot.Duration > TimeSpan.Zero && effectivePosition > snapshot.Duration)
        {
            effectivePosition = snapshot.Duration;
        }

        return effectivePosition;
    }

    public void Reset()
    {
        lock (_lock)
        {
            _state = ProgressState.Idle;
            _basePosition = TimeSpan.Zero;
            _baseTimeUtc = DateTime.MinValue;
            _duration = TimeSpan.Zero;
            _isPlaying = false;
            _playbackRate = 1.0;
            _isIndeterminate = false;
            _isSeekEnabled = false;
            _durationJustChanged = false;  
            _isYouTube = false;  // Reset platform flag
            _lastSnapshotTimestampUtc = DateTime.MinValue;
            _lastSnapshotSequence = -1;  
            _lastSnapshotPosition = TimeSpan.Zero;  // Reset last position
            _seekDebounceEndUtc = DateTime.MinValue;
            _allowBackwardUntilUtc = DateTime.MinValue;
            _seekPauseGraceUntilUtc = DateTime.MinValue;
            _pendingPauseConfirmation = false;
            _pendingPauseStartedUtc = DateTime.MinValue;
            _pendingPausePosition = TimeSpan.Zero;
        }
    }

    public UiProgressFrame GetUiFrame()
    {
        lock (_lock)
        {
            DateTime nowUtc = DateTime.UtcNow;
            TimeSpan pos = ClampPosition(PredictPosition(nowUtc));

            bool durationChanged = _durationJustChanged;
            _durationJustChanged = false;  

            return new UiProgressFrame
            {
                Position = pos,
                Duration = _duration,
                ShowIndeterminate = _isIndeterminate || (_duration.TotalSeconds <= 0 && pos.TotalSeconds > 0 && !_isSeekEnabled),
                State = _state,
                DurationJustChanged = durationChanged
            };
        }
    }

    private TimeSpan PredictPosition(DateTime nowUtc)
    {
        if (!_isPlaying || (_state != ProgressState.Playing && _state != ProgressState.Seeking))
        {
            return _basePosition;
        }

        if (_baseTimeUtc == DateTime.MinValue)
        {
            return _basePosition;
        }

        TimeSpan elapsed = nowUtc - _baseTimeUtc;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return _basePosition + TimeSpan.FromSeconds(elapsed.TotalSeconds * _playbackRate);
    }

    private bool DidDurationChange(TimeSpan previous, TimeSpan current)
    {
        
        if (previous <= TimeSpan.Zero || current <= TimeSpan.Zero)
        {
            return false;
        }

        double diffSec = Math.Abs((current - previous).TotalSeconds);
        
        // Platform-aware threshold: Browser sources have unstable duration → higher threshold (5% or 3s) Native apps have stable duration → lower threshold (2% or 1s)
        double percentThreshold = _isYouTube ? 0.05 : 0.02;  // 5% for browser, 2% for native
        double absoluteThreshold = _isYouTube ? 3.0 : 1.0;   // 3s for browser, 1s for native
        
        double minMeaningfulDelta = Math.Max(absoluteThreshold, previous.TotalSeconds * percentThreshold);
        
        return diffSec >= minMeaningfulDelta;
    }

    private TimeSpan ClampPosition(TimeSpan pos)
    {
        if (pos < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (_duration > TimeSpan.Zero && pos > _duration)
        {
            return _duration;
        }

        return pos;
    }
}
