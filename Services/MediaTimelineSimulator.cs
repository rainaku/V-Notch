using System.Windows.Media.Imaging;
using VNotch.Models;

namespace VNotch.Services;

/// <summary>
/// Simulates media timeline position when the real SMTC position is stuck/throttled.
/// This happens with YouTube in browsers where position updates stop arriving but
/// playback continues. The simulator uses wall-clock time to estimate current position.
/// 
/// Extracted from MediaDetectionService for single responsibility.
/// </summary>
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

    /// <summary>Whether the timeline is currently in throttled/simulated mode.</summary>
    public bool IsThrottled => _isThrottled;

    /// <summary>Last real position observed from SMTC before throttling began.</summary>
    public TimeSpan LastObservedPosition => _lastObservedPosition;

    /// <summary>When the last real position change was observed.</summary>
    public DateTime LastPositionChangeTime => _lastPositionChangeTime;

    /// <summary>Duration recovered from metadata lookup (YouTube/SoundCloud).</summary>
    public TimeSpan RecoveredDuration
    {
        get => _recoveredDuration;
        set => _recoveredDuration = value;
    }

    /// <summary>Thumbnail recovered from artwork lookup.</summary>
    public BitmapImage? RecoveredThumbnail
    {
        get => _recoveredThumbnail;
        set => _recoveredThumbnail = value;
    }

    // ─── Core API ───

    /// <summary>
    /// Update the observed position from SMTC. Call this every update cycle
    /// with the raw position from the media session.
    /// </summary>
    public void UpdateObservedPosition(TimeSpan position)
    {
        if (position != _lastObservedPosition)
        {
            _lastObservedPosition = position;
            _lastPositionChangeTime = DateTime.Now;
        }
    }

    /// <summary>
    /// Check if the position appears stuck (no change for the given threshold).
    /// </summary>
    public bool IsPositionStuck(TimeSpan threshold)
    {
        return (DateTime.Now - _lastPositionChangeTime).TotalSeconds > threshold.TotalSeconds;
    }

    /// <summary>
    /// Check if position is stuck at the end of a track.
    /// </summary>
    public bool IsAtEndStuck(double progress, DateTime lastMetadataChangeTime, TimeSpan threshold)
    {
        return progress > 0.98 && (DateTime.Now - lastMetadataChangeTime) > threshold;
    }

    /// <summary>
    /// Apply simulated timeline to a MediaInfo when the real position is stuck.
    /// Uses wall-clock elapsed time × playback rate to estimate current position.
    /// </summary>
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

    /// <summary>
    /// Enter throttled mode (e.g., when a new track is detected via window title
    /// but SMTC isn't providing position updates).
    /// </summary>
    public void EnterThrottledMode()
    {
        _isThrottled = true;
    }

    /// <summary>
    /// Exit throttled mode and reset all simulation state.
    /// Call when real position updates resume or media source changes.
    /// </summary>
    public void Reset()
    {
        _isThrottled = false;
        _recoveredDuration = TimeSpan.Zero;
        _recoveredThumbnail = null;
        ResetSimulation();
    }

    /// <summary>
    /// Reset only the simulation base (keep throttle state and recovered data).
    /// Useful when track changes but we're still in throttled mode.
    /// </summary>
    public void ResetSimulation()
    {
        _simBaseWallTimeUtc = DateTime.MinValue;
        _simBasePosition = TimeSpan.Zero;
        _simSignature = "";
    }

    /// <summary>
    /// Reset recovered data only (keep simulation and throttle state).
    /// Used when entering a new track in throttled mode.
    /// </summary>
    public void ResetRecoveredData()
    {
        _recoveredDuration = TimeSpan.Zero;
        _recoveredThumbnail = null;
    }

    /// <summary>
    /// Check if real position updates have resumed (position changed recently).
    /// If so, automatically exit throttled mode.
    /// Returns true if throttle was exited.
    /// </summary>
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

    /// <summary>
    /// Check if we should exit throttle due to prolonged position stall
    /// (no recovery found, position stuck for too long → media likely stopped).
    /// Returns true if throttle was exited.
    /// </summary>
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
