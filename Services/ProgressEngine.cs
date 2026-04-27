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


    private DateTime _lastSnapshotTimestampUtc = DateTime.MinValue;
    private long _lastSnapshotSequence = -1;  
    private DateTime _seekDebounceEndUtc = DateTime.MinValue;
    private DateTime _allowBackwardUntilUtc = DateTime.MinValue;

    
    
    private static readonly TimeSpan SnapshotOutOfOrderTolerance = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan PauseBackstepTolerance = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan ResumeBackstepTolerance = TimeSpan.FromMilliseconds(350);
    
    
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

            
            var predictedBeforeSnapshot = PredictPosition(nowUtc);
            System.Diagnostics.Debug.WriteLine($"[ENGINE] Snapshot: pos={snapshot.Position.TotalSeconds:F3}s, " +
                $"ts={snapshotTsUtc:HH:mm:ss.fff}, " +
                $"now={nowUtc:HH:mm:ss.fff}, " +
                $"latency={(nowUtc - snapshotTsUtc).TotalMilliseconds:F0}ms, " +
                $"isPlaying={snapshot.IsPlaying}, " +
                $"predicted={predictedBeforeSnapshot.TotalSeconds:F3}s, " +
                $"diff={(snapshot.Position - predictedBeforeSnapshot).TotalSeconds:F3}s");

            
            
            if (snapshot.SequenceNumber > 0 && snapshot.SequenceNumber <= _lastSnapshotSequence)
            {
                System.Diagnostics.Debug.WriteLine($"[ENGINE] REJECTED: Old sequence {snapshot.SequenceNumber} <= {_lastSnapshotSequence}");
                
                return;
            }

            
            
            if (_lastSnapshotTimestampUtc != DateTime.MinValue &&
                snapshotTsUtc < _lastSnapshotTimestampUtc - SnapshotOutOfOrderTolerance)
            {
                System.Diagnostics.Debug.WriteLine($"[ENGINE] REJECTED: Old timestamp {snapshotTsUtc:HH:mm:ss.fff} < {_lastSnapshotTimestampUtc:HH:mm:ss.fff}");
                
                return;
            }

            
            
            
            
            if (_baseTimeUtc != DateTime.MinValue && _isPlaying && _duration > TimeSpan.Zero)
            {
                var currentPredicted = PredictPosition(nowUtc);
                var snapshotPredicted = snapshot.Position + TimeSpan.FromSeconds((nowUtc - snapshotTsUtc).TotalSeconds * snapshot.PlaybackRate);
                var backwardDiff = currentPredicted - snapshotPredicted;
                
                
                bool likelySessionSwitch = snapshot.Duration > TimeSpan.Zero && 
                    Math.Abs((snapshot.Duration - _duration).TotalSeconds) > Math.Max(5.0, _duration.TotalSeconds * 0.1);
                
                
                bool likelyUserSeekBackward = backwardDiff.TotalSeconds > 2.0 && 
                    Math.Abs((snapshot.Position - _basePosition).TotalSeconds) > 2.0;
                
                if (backwardDiff.TotalSeconds > 0.5 && !likelySessionSwitch && !likelyUserSeekBackward)
                {
                    System.Diagnostics.Debug.WriteLine($"[ENGINE] REJECTED: Snapshot too far behind. " +
                        $"Current={currentPredicted.TotalSeconds:F3}s, " +
                        $"Snapshot={snapshotPredicted.TotalSeconds:F3}s, " +
                        $"Diff={backwardDiff.TotalSeconds:F3}s");
                    return;
                }
                else if (likelySessionSwitch)
                {
                    System.Diagnostics.Debug.WriteLine($"[ENGINE] ACCEPTED: Session switch detected. " +
                        $"Duration changed from {_duration.TotalSeconds:F1}s to {snapshot.Duration.TotalSeconds:F1}s");
                }
                else if (likelyUserSeekBackward)
                {
                    System.Diagnostics.Debug.WriteLine($"[ENGINE] ACCEPTED: User seek backward detected. " +
                        $"Position changed from {_basePosition.TotalSeconds:F1}s to {snapshot.Position.TotalSeconds:F1}s");
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

            TimeSpan previousDuration = _duration;
            if (snapshot.Duration.TotalSeconds > 0)
            {
                _duration = snapshot.Duration;
            }

            
            
            _isIndeterminate = snapshot.IsIndeterminate;
            _isSeekEnabled = snapshot.IsSeekEnabled;

            
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

            
            
            bool durationChanged = DidDurationChange(previousDuration, _duration);
            _durationJustChanged = durationChanged;  
            
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
                
                _seekDebounceEndUtc = DateTime.MinValue;
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

            
            
            
            
            if (absDiffSeconds < IgnoreCorrectionThreshold.TotalSeconds)
            {
                return;
            }

            if (absDiffSeconds < SmoothCorrectionThreshold.TotalSeconds)
            {
                
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
            _durationJustChanged = false;  
            _lastSnapshotTimestampUtc = DateTime.MinValue;
            _lastSnapshotSequence = -1;  
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
