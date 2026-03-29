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

    // Canonical state for prediction:
    // displayPosition = basePosition + (now - baseTime) * playbackRate
    private TimeSpan _basePosition = TimeSpan.Zero;
    private DateTime _baseTimeUtc = DateTime.MinValue;
    private TimeSpan _duration = TimeSpan.Zero;
    private bool _isPlaying;

    private double _playbackRate = 1.0;
    private bool _isIndeterminate;
    private bool _isSeekEnabled;

    private TimeSpan _lastDisplayedPosition = TimeSpan.Zero;
    private DateTime _lastSnapshotTimestampUtc = DateTime.MinValue;
    private DateTime _seekDebounceEndUtc = DateTime.MinValue;
    private DateTime _allowBackwardUntilUtc = DateTime.MinValue;

    private static readonly TimeSpan SnapshotOutOfOrderTolerance = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PauseBackstepTolerance = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan ResumeBackstepTolerance = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan IgnoreCorrectionThreshold = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan SmoothCorrectionThreshold = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SmoothBackwardCap = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan AntiBackwardSlack = TimeSpan.FromMilliseconds(80);
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

            if (_lastSnapshotTimestampUtc != DateTime.MinValue &&
                snapshotTsUtc < _lastSnapshotTimestampUtc - SnapshotOutOfOrderTolerance)
            {
                return;
            }

            if (snapshotTsUtc > _lastSnapshotTimestampUtc)
            {
                _lastSnapshotTimestampUtc = snapshotTsUtc;
            }

            TimeSpan previousDuration = _duration;
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

            bool durationChanged = DidDurationChange(previousDuration, _duration);
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
                    _lastDisplayedPosition = resetPos;
                    return;
                }
            }

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
                _lastDisplayedPosition = pausedPos;
                return;
            }

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
                _lastDisplayedPosition = startPos;
                return;
            }

            if (nowUtc < _seekDebounceEndUtc)
            {
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
            // < 0.3s: ignore update
            // < 2.0s: smooth correction
            // >= 2.0s: snap immediately (seek-like jump)
            if (absDiffSeconds < IgnoreCorrectionThreshold.TotalSeconds)
            {
                return;
            }

            if (absDiffSeconds < SmoothCorrectionThreshold.TotalSeconds)
            {
                const double correctionFactor = 0.35;
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
            _lastDisplayedPosition = observedPos;
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
            _lastDisplayedPosition = clampedPos;
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
            _lastDisplayedPosition = TimeSpan.Zero;
            _lastSnapshotTimestampUtc = DateTime.MinValue;
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

            bool allowBackward = nowUtc <= _allowBackwardUntilUtc;
            if (_state == ProgressState.Playing &&
                !allowBackward &&
                pos + AntiBackwardSlack < _lastDisplayedPosition &&
                pos > TimeSpan.Zero)
            {
                pos = _lastDisplayedPosition;
                _basePosition = pos;
                _baseTimeUtc = nowUtc;
            }

            _lastDisplayedPosition = pos;

            return new UiProgressFrame
            {
                Position = pos,
                Duration = _duration,
                ShowIndeterminate = _isIndeterminate || (_duration.TotalSeconds <= 0 && pos.TotalSeconds > 0 && !_isSeekEnabled),
                State = _state
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

    private static bool DidDurationChange(TimeSpan previous, TimeSpan current)
    {
        if (previous <= TimeSpan.Zero || current <= TimeSpan.Zero)
        {
            return false;
        }

        double diffSec = Math.Abs((current - previous).TotalSeconds);
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
