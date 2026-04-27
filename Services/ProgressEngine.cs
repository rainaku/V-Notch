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
    public bool IsYouTube;
    public double PlaybackRate;
    public bool IsSeekEnabled;
    public bool IsIndeterminate;
    public DateTime Timestamp;
    public long SequenceNumber;  // Monotonic sequence for strict ordering
}

public struct UiProgressFrame
{
    public TimeSpan Position;
    public TimeSpan Duration;
    public bool ShowIndeterminate;
    public ProgressState State;
    public bool DurationJustChanged;  // NEW: Signals UI that duration changed significantly
}

public class ProgressEngine
{
    private ProgressState _state = ProgressState.Idle;

    // Canonical state for prediction:
    // displayPosition = basePosition + (now - baseTime) * playbackRate
    private TimeSpan _basePosition = TimeSpan.Zero;
    private DateTime _baseTimeUtc = DateTime.MinValue;
    private TimeSpan _duration = TimeSpan.Zero;
    private bool _isPlaying;

    private double _playbackRate = 1.0;
    private bool _isIndeterminate;
    private bool _isSeekEnabled;
    private bool _durationJustChanged = false;  // Flag to signal UI about duration changes


    private DateTime _lastSnapshotTimestampUtc = DateTime.MinValue;
    private long _lastSnapshotSequence = -1;  // Track sequence for strict monotonic ordering
    private DateTime _seekDebounceEndUtc = DateTime.MinValue;
    private DateTime _allowBackwardUntilUtc = DateTime.MinValue;

    // Reduced tolerance from 250ms to 50ms for stricter out-of-order rejection.
    // This prevents stale snapshots from causing progress jumps during session transitions.
    private static readonly TimeSpan SnapshotOutOfOrderTolerance = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan PauseBackstepTolerance = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan ResumeBackstepTolerance = TimeSpan.FromMilliseconds(350);
    // IMPROVED: Reduced from 300ms to 150ms - with accurate timestamps from Phase 1 fix,
    // we can afford tighter tolerance for better responsiveness
    private static readonly TimeSpan IgnoreCorrectionThreshold = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan SmoothCorrectionThreshold = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SmoothBackwardCap = TimeSpan.FromMilliseconds(120);

    private static readonly TimeSpan DurationChangeSeekWindow = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan UserSeekDebounceDuration = TimeSpan.FromMilliseconds(2500);

    private readonly object _lock = new object();

    public void OnMediaSnapshot(ProgressSnapshot snapshot)
    {
        lock (_lock)
        {
            DateTime nowUtc = DateTime.UtcNow;
            DateTime snapshotTsUtc = snapshot.Timestamp.Kind == DateTimeKind.Utc
                ? snapshot.Timestamp
                : snapshot.Timestamp.ToUniversalTime();

            // DEBUG: Log snapshot details
            System.Diagnostics.Debug.WriteLine($"[ENGINE] Snapshot: pos={snapshot.Position.TotalSeconds:F3}s, " +
                $"ts={snapshotTsUtc:HH:mm:ss.fff}, " +
                $"now={nowUtc:HH:mm:ss.fff}, " +
                $"latency={(nowUtc - snapshotTsUtc).TotalMilliseconds:F0}ms, " +
                $"isPlaying={snapshot.IsPlaying}");

            // Strict monotonic ordering using sequence numbers.
            // Reject any snapshot with a sequence number we've already processed.
            if (snapshot.SequenceNumber > 0 && snapshot.SequenceNumber <= _lastSnapshotSequence)
            {
                // Already processed this or a newer snapshot - reject immediately
                return;
            }

            // Timestamp-based validation as fallback (for backwards compatibility).
            // Reduced tolerance to 50ms to prevent stale snapshots from causing jumps.
            if (_lastSnapshotTimestampUtc != DateTime.MinValue &&
                snapshotTsUtc < _lastSnapshotTimestampUtc - SnapshotOutOfOrderTolerance)
            {
                // Snapshot is too old - reject to prevent progress jumping backwards
                return;
            }

            // Update tracking - accept this snapshot
            if (snapshotTsUtc > _lastSnapshotTimestampUtc)
            {
                _lastSnapshotTimestampUtc = snapshotTsUtc;
            }
            
            if (snapshot.SequenceNumber > _lastSnapshotSequence)
            {
                _lastSnapshotSequence = snapshot.SequenceNumber;
            }

            TimeSpan previousDuration = _duration;
            if (snapshot.Duration.TotalSeconds > 0)
            {
                _duration = snapshot.Duration;
            }

            // Always update these properties, even during seek debounce.
            // They represent media capabilities/state, not position.
            _isIndeterminate = snapshot.IsIndeterminate;
            _isSeekEnabled = snapshot.IsSeekEnabled;

            // Process playback rate - always update, even during debounce
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

            // Centralized duration change detection - single source of truth.
            // This prevents conflicts between ProgressEngine and UI layer.
            bool durationChanged = DidDurationChange(previousDuration, _duration);
            _durationJustChanged = durationChanged;  // Signal UI layer
            
            if (durationChanged && snapshot.IsPlaying)
            {
                TimeSpan predictedAtDurationChange = ClampPosition(PredictPosition(nowUtc));
                bool likelyTrackRestart = snapshot.Position.TotalSeconds <=
                    Math.Min(5.0, _duration.TotalSeconds > 0 ? _duration.TotalSeconds * 0.15 : 5.0);
                bool predictedOutsideNewDuration =
                    _duration.TotalSeconds > 0 &&
                    predictedAtDurationChange > _duration + TimeSpan.FromSeconds(1);

                if (likelyTrackRestart || predictedOutsideNewDuration)
                {
                    TimeSpan resetPos = ClampPosition(snapshot.Position);
                    _basePosition = resetPos;
                    _baseTimeUtc = nowUtc;
                    _isPlaying = true;
                    _state = ProgressState.Playing;
                    _seekDebounceEndUtc = nowUtc + DurationChangeSeekWindow;
                    _allowBackwardUntilUtc = nowUtc + DurationChangeSeekWindow;
                    return;
                }
            }

            // Handle pause state changes - ALWAYS process these, even during seek debounce.
            // Critical: User may seek then immediately pause, we must respect that.
            if (!snapshot.IsPlaying)
            {
                TimeSpan pausedPos = ClampPosition(snapshot.Position);

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
                // Clear seek debounce when pausing - user action takes priority
                _seekDebounceEndUtc = DateTime.MinValue;
                return;
            }

            // Handle play/resume state changes - ALWAYS process these, even during seek debounce.
            // Critical: User may seek then immediately play, we must respect that.
            if (!_isPlaying || (_state != ProgressState.Playing && _state != ProgressState.Seeking))
            {
                TimeSpan startPos = ClampPosition(snapshot.Position);
                if (_state == ProgressState.Paused && startPos < _basePosition - ResumeBackstepTolerance)
                {
                    startPos = _basePosition;
                }

                _isPlaying = true;
                _state = ProgressState.Playing;
                _basePosition = startPos;
                _baseTimeUtc = nowUtc;
                // Don't clear seek debounce here - we want to debounce position updates after resume
                return;
            }

            // Seek debounce - ONLY applies to position updates while playing.
            // State changes (pause/play) and property updates (rate/indeterminate/seekEnabled) 
            // are processed above and bypass this check.
            if (nowUtc < _seekDebounceEndUtc)
            {
                // Skip position updates during debounce to prevent jitter after user seek
                return;
            }

            if (_state == ProgressState.Seeking)
            {
                _state = ProgressState.Playing;
            }

            TimeSpan observedPos = ClampPosition(snapshot.Position);
            TimeSpan predictedNow = ClampPosition(PredictPosition(nowUtc));
            TimeSpan diff = observedPos - predictedNow;
            double absDiffSeconds = Math.Abs(diff.TotalSeconds);

            // Anti-jitter:
            // < 0.15s: ignore update (reduced from 0.3s for better accuracy)
            // < 2.0s: smooth correction (increased factor from 0.35 to 0.55 for faster convergence)
            // >= 2.0s: snap immediately (seek-like jump)
            if (absDiffSeconds < IgnoreCorrectionThreshold.TotalSeconds)
            {
                return;
            }

            if (absDiffSeconds < SmoothCorrectionThreshold.TotalSeconds)
            {
                // IMPROVED: Increased from 0.35 to 0.55 for faster convergence with less jitter
                const double correctionFactor = 0.55;
                double correctedSeconds = predictedNow.TotalSeconds + (diff.TotalSeconds * correctionFactor);

                if (diff < TimeSpan.Zero)
                {
                    double minAllowedBackward = predictedNow.TotalSeconds - SmoothBackwardCap.TotalSeconds;
                    if (correctedSeconds < minAllowedBackward)
                    {
                        correctedSeconds = minAllowedBackward;
                    }
                }

                _basePosition = ClampPosition(TimeSpan.FromSeconds(correctedSeconds));
                _baseTimeUtc = nowUtc;
                return;
            }

            _basePosition = observedPos;
            _baseTimeUtc = nowUtc;
            _state = ProgressState.Playing;
            _allowBackwardUntilUtc = nowUtc + TimeSpan.FromMilliseconds(900);
            _seekDebounceEndUtc = nowUtc + TimeSpan.FromMilliseconds(600);
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
            _allowBackwardUntilUtc = nowUtc + TimeSpan.FromMilliseconds(900);
        }
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
            _durationJustChanged = false;  // Clear duration change flag
            _lastSnapshotTimestampUtc = DateTime.MinValue;
            _lastSnapshotSequence = -1;  // Reset sequence tracking
            _seekDebounceEndUtc = DateTime.MinValue;
            _allowBackwardUntilUtc = DateTime.MinValue;
        }
    }

    public UiProgressFrame GetUiFrame()
    {
        lock (_lock)
        {
            DateTime nowUtc = DateTime.UtcNow;
            TimeSpan pos = ClampPosition(PredictPosition(nowUtc));

            // Capture and clear the duration changed flag
            bool durationChanged = _durationJustChanged;
            _durationJustChanged = false;  // Clear after reading (one-shot flag)

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

    /// <summary>
    /// Centralized duration change detection - single source of truth.
    /// Uses adaptive threshold: minimum 1 second OR 2% of previous duration.
    /// This prevents false positives from minor metadata corrections while catching real track changes.
    /// </summary>
    /// <param name="previous">Previous duration</param>
    /// <param name="current">Current duration</param>
    /// <returns>True if duration changed significantly</returns>
    private static bool DidDurationChange(TimeSpan previous, TimeSpan current)
    {
        // Ignore changes when either duration is invalid
        if (previous <= TimeSpan.Zero || current <= TimeSpan.Zero)
        {
            return false;
        }

        double diffSec = Math.Abs((current - previous).TotalSeconds);
        
        // Adaptive threshold: 1 second minimum, or 2% of duration for longer tracks
        // Examples:
        // - 30s track: threshold = 1s (3.3%)
        // - 180s track: threshold = 3.6s (2%)
        // - 3600s track: threshold = 72s (2%)
        double minMeaningfulDelta = Math.Max(1.0, previous.TotalSeconds * 0.02);
        
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
