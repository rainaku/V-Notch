using System.Windows.Media.Imaging;
using VNotch.Models;

namespace VNotch.Services;
public class MediaTimelineSimulator
{
    // ─── Simulation state ───
    private DateTime _simBaseWallTimeUtc = DateTime.MinValue;
    private TimeSpan _simBasePosition = TimeSpan.Zero;
    private double _simBasePlaybackRate = 1.0;
    private string _simSignature = "";

    // ─── Throttle tracking ───
    private bool _isThrottled;
    private TimeSpan _lastObservedPosition = TimeSpan.Zero;
    private DateTime _lastPositionChangeTime = DateTime.MinValue;

    // ─── Recovered data (set externally when thumbnail/duration lookups succeed) ───
    private TimeSpan _recoveredDuration = TimeSpan.Zero;
    private BitmapImage? _recoveredThumbnail;

    // ─── Public properties ───
    public bool IsThrottled => _isThrottled;
    public TimeSpan RecoveredDuration
    {
        get => _recoveredDuration;
        set => _recoveredDuration = value;
    }
    public BitmapImage? RecoveredThumbnail
    {
        get => _recoveredThumbnail;
        set => _recoveredThumbnail = value;
    }
public void UpdateObservedPosition(TimeSpan position)
    {
        if (position != _lastObservedPosition)
        {
            _lastObservedPosition = position;
            _lastPositionChangeTime = DateTime.Now;
        }
    }
public bool IsPositionStuck(TimeSpan threshold)
    {
        return (DateTime.Now - _lastPositionChangeTime).TotalSeconds > threshold.TotalSeconds;
    }
public bool IsAtEndStuck(double progress, DateTime lastMetadataChangeTime, TimeSpan threshold)
    {
        return progress > 0.98 && (DateTime.Now - lastMetadataChangeTime) > threshold;
    }
public void ApplySimulatedTimeline(MediaInfo info, bool atEndStuck)
    {
        var nowUtc = DateTime.UtcNow;

        // Reset simulation base if track signature changed or first call
        var sig = info.GetSignature();
        if (_simSignature != sig || _simBaseWallTimeUtc == DateTime.MinValue)
        {
            _simSignature = sig;
            _simBaseWallTimeUtc = nowUtc;
            _simBasePosition = _lastObservedPosition != TimeSpan.Zero ? _lastObservedPosition : info.Position;
            _simBasePlaybackRate = info.PlaybackRate > 0 ? info.PlaybackRate : 1.0;
        }

        var elapsed = nowUtc - _simBaseWallTimeUtc;
        var sim = _simBasePosition + TimeSpan.FromSeconds(elapsed.TotalSeconds * _simBasePlaybackRate);

        // Clamp to duration if not at-end-stuck
        if (!atEndStuck && info.Duration > TimeSpan.Zero && sim > info.Duration)
            sim = info.Duration;

        info.Position = sim;
        info.IsThrottled = true;
        _isThrottled = true;

        // Apply recovered duration
        if (atEndStuck)
        {
            info.Duration = _recoveredDuration > TimeSpan.Zero ? _recoveredDuration : TimeSpan.Zero;
        }
        else
        {
            if (info.Duration <= TimeSpan.Zero && _recoveredDuration > TimeSpan.Zero)
                info.Duration = _recoveredDuration;
        }

        // Apply recovered thumbnail
        if (info.Thumbnail == null && _recoveredThumbnail != null)
            info.Thumbnail = _recoveredThumbnail;

        info.LastUpdated = DateTimeOffset.Now;
    }
public void EnterThrottledMode()
    {
        _isThrottled = true;
    }
public void Reset()
    {
        _isThrottled = false;
        _recoveredDuration = TimeSpan.Zero;
        _recoveredThumbnail = null;
        ResetSimulation();
    }
public void ResetSimulation()
    {
        _simBaseWallTimeUtc = DateTime.MinValue;
        _simBasePosition = TimeSpan.Zero;
        _simSignature = "";
    }
public void ResetRecoveredData()
    {
        _recoveredDuration = TimeSpan.Zero;
        _recoveredThumbnail = null;
    }
public bool TryExitThrottleIfPositionResumed(TimeSpan resumeThreshold)
    {
        if (!_isThrottled) return false;

        if ((DateTime.Now - _lastPositionChangeTime) < resumeThreshold)
        {
            Reset();
            return true;
        }

        return false;
    }
public bool TryExitThrottleIfStalled(TimeSpan stallThreshold)
    {
        if (!_isThrottled) return false;

        if ((DateTime.Now - _lastPositionChangeTime).TotalSeconds > stallThreshold.TotalSeconds)
        {
            Reset();
            return true;
        }

        return false;
    }
}
