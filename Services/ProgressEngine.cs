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
    
    private double _playbackRate = 1.0;
    private bool _isIndeterminate = false;
    private bool _isSeekEnabled = false;

    private readonly object _lock = new object();

    public void OnMediaSnapshot(ProgressSnapshot snapshot)
    {
        lock (_lock)
        {
            if (snapshot.Duration.TotalSeconds > 0)
            {
                _duration = snapshot.Duration;
            }

            _playbackRate = snapshot.PlaybackRate > 0 ? snapshot.PlaybackRate : 1.0;
            _isIndeterminate = snapshot.IsIndeterminate;
            _isSeekEnabled = snapshot.IsSeekEnabled;

            bool isNowPlaying = snapshot.IsPlaying;
            DateTime now = DateTime.UtcNow;

            TimeSpan staleness = TimeSpan.Zero;
            if (snapshot.Timestamp > DateTime.MinValue && snapshot.Timestamp.Kind == DateTimeKind.Utc)
            {
                staleness = now - snapshot.Timestamp;
            }
            else if (snapshot.Timestamp > DateTime.MinValue)
            {
                staleness = now - snapshot.Timestamp.ToUniversalTime();
            }
            
            if (staleness.TotalSeconds < 0 || staleness.TotalDays > 1) staleness = TimeSpan.Zero;

            // SMTC gives us the position AT the Timestamp. The real current position is Position + Staleness.
            TimeSpan actualCurrentPos = snapshot.Position;
            if (isNowPlaying)
            {
                actualCurrentPos += TimeSpan.FromSeconds(staleness.TotalSeconds * _playbackRate);
            }

            if (!isNowPlaying)
            {
                _state = ProgressState.Paused;
                _stopwatch.Stop();
                _anchorPosition = snapshot.Position;
                _anchorTimestamp = snapshot.Timestamp;
                return;
            }

            if (_state != ProgressState.Playing && _state != ProgressState.Seeking)
            {
                _state = ProgressState.Playing;
                _anchorPosition = actualCurrentPos;
                _anchorTimestamp = snapshot.Timestamp;
                _stopwatch.Restart();
                _lastSyncTime = now;
                _lastDisplayedPosition = actualCurrentPos;
                return;
            }

            // We are playing or seeking.
            TimeSpan observedPos = actualCurrentPos;

            // Check if we are in seek debounce
            if (now < _seekDebounceEndTime)
            {
                // In stabilization window, ignore incoming samples to avoid jitter
                return;
            }

            if (_state == ProgressState.Seeking)
            {
                _state = ProgressState.Playing;
            }

            // Real seek detection
            TimeSpan seekThreshold = TimeSpan.FromMilliseconds(Math.Max(2000, _duration.TotalMilliseconds * 0.01));
            if (Math.Abs((observedPos - _lastDisplayedPosition).TotalMilliseconds) > seekThreshold.TotalMilliseconds)
            {
                // Real seek detected
                _anchorPosition = observedPos;
                _anchorTimestamp = snapshot.Timestamp;
                _stopwatch.Restart();
                _seekDebounceEndTime = now + _stabilizationWindow;
                _lastSyncTime = now;
                _lastDisplayedPosition = observedPos;
                return;
            }

            // Anti-backwards when playing
            if (observedPos < _lastDisplayedPosition - _backwardsTolerance)
            {
                // Drop sample (it's slightly in the past, maybe stale)
                return;
            }

            // Soft sync logic (drift correction)
            if (now - _lastSyncTime >= _minSyncInterval)
            {
                TimeSpan predictedPos = GetPredictedPosition();
                double driftMs = (observedPos - predictedPos).TotalMilliseconds;

                if (Math.Abs(driftMs) > _driftTolerance.TotalMilliseconds)
                {
                    // Update anchor
                    _anchorPosition = observedPos;
                    _anchorTimestamp = snapshot.Timestamp;
                    _stopwatch.Restart();
                    _lastSyncTime = now;
                }
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
        }
    }

    public UiProgressFrame GetUiFrame()
    {
        lock (_lock)
        {
            TimeSpan pos = GetPredictedPosition();

            // Floor to avoid jumping around X.99 and Y.00
            double seconds = Math.Floor(Math.Max(0, pos.TotalSeconds));
            pos = TimeSpan.FromSeconds(seconds);

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
