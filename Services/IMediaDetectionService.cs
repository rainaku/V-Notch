using VNotch.Models;

namespace VNotch.Services;

/// <summary>
/// Interface for media detection and control operations.
/// Wraps SMTC (System Media Transport Controls) and provides unified media info.
/// </summary>
public interface IMediaDetectionService : IDisposable
{
    /// <summary>
    /// Fired when media info changes (track, playback state, position, etc.)
    /// </summary>
    event EventHandler<MediaInfo>? MediaChanged;

    /// <summary>
    /// Start monitoring media sessions.
    /// </summary>
    void Start();

    /// <summary>
    /// Stop monitoring media sessions.
    /// </summary>
    void Stop();

    /// <summary>
    /// Toggle play/pause on the current media session.
    /// </summary>
    Task PlayPauseAsync();

    /// <summary>
    /// Skip to next track.
    /// </summary>
    Task NextTrackAsync();

    /// <summary>
    /// Go to previous track.
    /// </summary>
    Task PreviousTrackAsync();

    /// <summary>
    /// Seek to an absolute position.
    /// </summary>
    Task SeekAsync(TimeSpan position);

    /// <summary>
    /// Seek relative to current position.
    /// </summary>
    Task SeekRelativeAsync(double seconds);
}
