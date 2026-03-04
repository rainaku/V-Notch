using System;
using System.Diagnostics;
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
}

public struct UiProgressFrame
{
    public TimeSpan Position;
    public TimeSpan Duration;
    public bool ShowIndeterminate;
    public ProgressState State;
}

public class ProgressEngine
{
    private ProgressState _state = ProgressState.Idle;
    
    private TimeSpan _anchorPosition = TimeSpan.Zero;
    private TimeSpan _duration = TimeSpan.Zero;
    private DateTime _anchorTimestamp = DateTime.MinValue;
    private Stopwatch _stopwatch = new Stopwatch();
    
    // For seek debounce and drift
    private TimeSpan _lastDisplayedPosition = TimeSpan.Zero;
    private DateTime _seekDebounceEndTime = DateTime.MinValue;
    private DateTime _lastSyncTime = DateTime.MinValue;
    
    // Config
    private readonly TimeSpan _driftTolerance = TimeSpan.FromMilliseconds(500); // 500-700ms
    private readonly TimeSpan _minSyncInterval = TimeSpan.FromMilliseconds(1000); // 1-2s
    private readonly TimeSpan _backwardsTolerance = TimeSpan.FromMilliseconds(250); // 250-500ms
    private readonly TimeSpan _stabilizationWindow = TimeSpan.FromMilliseconds(500); // 300-800ms
    private readonly TimeSpan _minStalenessCompensation = TimeSpan.FromMilliseconds(280);
    private readonly TimeSpan _maxStalenessCompensation = TimeSpan.FromMilliseconds(1200);
    
    private double _playbackRate = 1.0;
    private bool _isIndeterminate = false;
    private bool _isSeekEnabled = false;

    private DateTime _firstSnapshotTime = DateTime.MinValue;
    private bool _isInitialWarmup = true;
    private readonly TimeSpan _initialWarmupDuration = TimeSpan.FromSeconds(4);
    private DateTime _lastSnapshotTimestampUtc = DateTime.MinValue;

    private readonly object _lock = new object();

    public void OnMediaSnapshot(ProgressSnapshot snapshot)
    {
        lock (_lock)
        {
            if (snapshot.Duration.TotalSeconds > 0)
            {
                _duration = snapshot.Duration;
            }

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
            _isIndeterminate = snapshot.IsIndeterminate;
            _isSeekEnabled = snapshot.IsSeekEnabled;

            bool isNowPlaying = snapshot.IsPlaying;
            DateTime now = DateTime.UtcNow;
            DateTime snapshotTsUtc = snapshot.Timestamp.Kind == DateTimeKind.Utc
                ? snapshot.Timestamp
                : snapshot.Timestamp.ToUniversalTime();

            // Ignore out-of-order snapshots to prevent startup rewind/jitter.
            if (_lastSnapshotTimestampUtc != DateTime.MinValue &&
                snapshotTsUtc < _lastSnapshotTimestampUtc - TimeSpan.FromMilliseconds(250))
            {
                return;
            }
            if (snapshotTsUtc > _lastSnapshotTimestampUtc)
            {
                _lastSnapshotTimestampUtc = snapshotTsUtc;
            }

            if (_firstSnapshotTime == DateTime.MinValue && isNowPlaying)
            {
                _firstSnapshotTime = now;
                _isInitialWarmup = true;
            }

            TimeSpan staleness = now - snapshotTsUtc;

            if (staleness < TimeSpan.Zero || staleness > TimeSpan.FromMinutes(5)) 
                staleness = TimeSpan.Zero;

            bool startupWarmupActive = _isInitialWarmup &&
                                       _firstSnapshotTime != DateTime.MinValue &&
                                       (now - _firstSnapshotTime) < _initialWarmupDuration;

            TimeSpan actualCurrentPos = snapshot.Position;
            if (isNowPlaying && !snapshot.IsYouTube)
            {
                // MediaDetectionService already normalizes most snapshots to "now",
                // so we only apply extra staleness compensation for non-YouTube sources
                // and only when the snapshot is meaningfully old.
                var stalenessCap = startupWarmupActive
                    ? _maxStalenessCompensation
                    : TimeSpan.FromMilliseconds(450);
                var effectiveStaleness = staleness > stalenessCap ? stalenessCap : staleness;
                if (effectiveStaleness > _minStalenessCompensation)
                {
                    actualCurrentPos += TimeSpan.FromSeconds(effectiveStaleness.TotalSeconds * _playbackRate);
                }
            }

            if (!isNowPlaying)
            {
                TimeSpan pausedPos = snapshot.Position;

                // Prevent visible rewind on pause when the arriving snapshot is stale.
                if (_state == ProgressState.Playing || _state == ProgressState.Seeking)
                {
                    TimeSpan predictedPauseNow = GetPredictedPosition();
                    TimeSpan maxAllowedBackstep = TimeSpan.FromMilliseconds(450);
                    if (pausedPos < predictedPauseNow - maxAllowedBackstep)
                    {
                        pausedPos = predictedPauseNow;
                    }
                }

                if (_duration > TimeSpan.Zero && pausedPos > _duration)
                {
                    pausedPos = _duration;
                }
                if (pausedPos < TimeSpan.Zero)
                {
                    pausedPos = TimeSpan.Zero;
                }

                _state = ProgressState.Paused;
                _stopwatch.Stop();
                _anchorPosition = pausedPos;
                _lastDisplayedPosition = pausedPos;
                _anchorTimestamp = snapshotTsUtc;
                _lastSyncTime = now;
                return;
            }

            // Initial warmup: accept large drift and aggressively sync
            bool inWarmup = _isInitialWarmup && (now - _firstSnapshotTime) < _initialWarmupDuration;
            bool allowWarmupBackwardsRecovery = !snapshot.IsYouTube && inWarmup;

            if (_state != ProgressState.Playing && _state != ProgressState.Seeking)
            {
                TimeSpan startPos = actualCurrentPos;
                if (_state == ProgressState.Paused)
                {
                    // Resume snapshots can lag behind the paused anchor; avoid rewind on play.
                    TimeSpan maxResumeBackstep = TimeSpan.FromMilliseconds(350);
                    if (startPos < _anchorPosition - maxResumeBackstep)
                    {
                        startPos = _anchorPosition;
                    }
                }

                _state = ProgressState.Playing;
                _anchorPosition = startPos;
                _anchorTimestamp = snapshotTsUtc;
                _stopwatch.Restart();
                _lastSyncTime = now;
                _lastDisplayedPosition = startPos;
                return;
            }

            TimeSpan observedPos = actualCurrentPos;

            if (now < _seekDebounceEndTime)
                return;

            if (_state == ProgressState.Seeking)
                _state = ProgressState.Playing;

            // Seek detection
            TimeSpan seekThreshold = TimeSpan.FromMilliseconds(Math.Max(2000, _duration.TotalMilliseconds * 0.01));
            TimeSpan predictedNow = GetPredictedPosition();
            bool likelyRestartAtTrackEnd =
                _duration.TotalSeconds > 0 &&
                _lastDisplayedPosition.TotalSeconds > (_duration.TotalSeconds * 0.8) &&
                observedPos.TotalSeconds < Math.Min(5.0, _duration.TotalSeconds * 0.2);
            bool freshBackwardSnapshot = staleness <=
                (snapshot.IsYouTube ? TimeSpan.FromMilliseconds(420) : TimeSpan.FromMilliseconds(650));
            bool largeBackwardSeekLike = (predictedNow - observedPos).TotalMilliseconds >
                Math.Max(seekThreshold.TotalMilliseconds, snapshot.IsYouTube ? 7000 : 3500);

            double jumpMs = (observedPos - _lastDisplayedPosition).TotalMilliseconds;
            if (Math.Abs(jumpMs) > seekThreshold.TotalMilliseconds)
            {
                bool backwardJump = jumpMs < 0;
                if (backwardJump &&
                    !likelyRestartAtTrackEnd &&
                    !(freshBackwardSnapshot && largeBackwardSeekLike))
                {
                    return;
                }

                _anchorPosition = observedPos;
                _anchorTimestamp = snapshotTsUtc;
                _stopwatch.Restart();
                _seekDebounceEndTime = now + _stabilizationWindow;
                _lastSyncTime = now;
                _lastDisplayedPosition = observedPos;
                return;
            }

            // Reject unexpected small backward jumps (after seek detection).
            TimeSpan effectiveBackwardTolerance = snapshot.IsYouTube
                ? TimeSpan.FromMilliseconds(650)
                : _backwardsTolerance;
            if (observedPos < predictedNow - effectiveBackwardTolerance)
            {
                if (!likelyRestartAtTrackEnd && !allowWarmupBackwardsRecovery)
                    return;
            }

            // During warmup: sync more aggressively, allow larger drift
            TimeSpan effectiveDriftTolerance = inWarmup 
                ? (snapshot.IsYouTube ? TimeSpan.FromMilliseconds(1200) : TimeSpan.FromSeconds(2))
                : _driftTolerance;

            TimeSpan effectiveSyncInterval = inWarmup 
                ? (snapshot.IsYouTube ? TimeSpan.FromMilliseconds(300) : TimeSpan.FromMilliseconds(300))
                : _minSyncInterval;

            // Drift correction
            if (now - _lastSyncTime >= effectiveSyncInterval)
            {
                TimeSpan predicted = predictedNow;
                double driftMs = (observedPos - predicted).TotalMilliseconds;

                if (Math.Abs(driftMs) > effectiveDriftTolerance.TotalMilliseconds)
                {
                    _anchorPosition = observedPos;
                    _anchorTimestamp = snapshotTsUtc;
                    _stopwatch.Restart();
                    _lastSyncTime = now;

                    if (inWarmup && Math.Abs(driftMs) < 3000) // nếu drift nhỏ dần → kết thúc warmup sớm
                    {
                        if ((now - _firstSnapshotTime).TotalSeconds > 1.5)
                            _isInitialWarmup = false;
                    }
                }
            }

            // Kết thúc warmup nếu đã qua thời gian hoặc drift ổn định
            if (inWarmup && (now - _firstSnapshotTime) >= _initialWarmupDuration)
            {
                _isInitialWarmup = false;
            }
        }
    }

    public void NotifyUserSeek(TimeSpan position)
    {
        lock (_lock)
        {
            _state = ProgressState.Seeking;
            _anchorPosition = position;
            _stopwatch.Restart();
            DateTime now = DateTime.UtcNow;
            _anchorTimestamp = now;
            _seekDebounceEndTime = now + TimeSpan.FromMilliseconds(2500); // Add a larger debounce for user manual seek
            _lastDisplayedPosition = position;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _state = ProgressState.Idle;
            _stopwatch.Stop();
            _stopwatch.Reset();
            _anchorPosition = TimeSpan.Zero;
            _duration = TimeSpan.Zero;
            _lastDisplayedPosition = TimeSpan.Zero;
            _seekDebounceEndTime = DateTime.MinValue;
            _isIndeterminate = false;
            _firstSnapshotTime = DateTime.MinValue;
            _isInitialWarmup = true;
            _lastSnapshotTimestampUtc = DateTime.MinValue;
        }
    }

    public UiProgressFrame GetUiFrame()
    {
        lock (_lock)
        {
            TimeSpan pos = GetPredictedPosition();

            pos = TimeSpan.FromSeconds(Math.Max(0, pos.TotalSeconds));

            // Clamp max
            if (_duration.TotalSeconds > 0 && pos > _duration)
            {
                pos = _duration;
            }

            // Anti-backward protection for UI display
            if (_state == ProgressState.Playing && pos < _lastDisplayedPosition && pos.TotalSeconds > 0)
            {
                pos = _lastDisplayedPosition;
            }
            else
            {
                _lastDisplayedPosition = pos;
            }

            return new UiProgressFrame
            {
                Position = pos,
                Duration = _duration,
                ShowIndeterminate = _isIndeterminate || (_duration.TotalSeconds <= 0 && pos.TotalSeconds > 0 && !_isSeekEnabled),
                State = _state
            };
        }
    }

    private TimeSpan GetPredictedPosition()
    {
        if (_state == ProgressState.Playing || _state == ProgressState.Seeking)
        {
            // predicted = anchor + elapsed
            return _anchorPosition + TimeSpan.FromMilliseconds(_stopwatch.ElapsedMilliseconds * _playbackRate);
        }
        return _anchorPosition;
    }
}
