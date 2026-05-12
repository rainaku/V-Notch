using System;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace VNotch.Services;

/// <summary>
/// Handles media transport commands (play/pause, next, previous, seek).
/// Extracted from MediaDetectionService to separate control from detection.
/// </summary>
public sealed class MediaTransportControlService
{
    private readonly Func<GlobalSystemMediaTransportControlsSession?> _getActiveSession;

    public MediaTransportControlService(Func<GlobalSystemMediaTransportControlsSession?> getActiveSession)
    {
        _getActiveSession = getActiveSession;
    }

    public async Task PlayPauseAsync()
    {
        try
        {
            var session = _getActiveSession();
            if (session != null)
            {
                await session.TryTogglePlayPauseAsync();
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-CTRL", ex, "PlayPause failed");
        }
    }

    public async Task NextTrackAsync()
    {
        try
        {
            var session = _getActiveSession();
            if (session != null)
            {
                await session.TrySkipNextAsync();
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-CTRL", ex, "NextTrack failed");
        }
    }

    public async Task PreviousTrackAsync()
    {
        try
        {
            var session = _getActiveSession();
            if (session != null)
            {
                await session.TrySkipPreviousAsync();
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-CTRL", ex, "PreviousTrack failed");
        }
    }

    public async Task SeekAsync(TimeSpan position)
    {
        try
        {
            var session = _getActiveSession();
            if (session != null)
            {
                await session.TryChangePlaybackPositionAsync(position.Ticks);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-CTRL", ex, "Seek failed");
        }
    }

    public async Task SeekRelativeAsync(double seconds)
    {
        try
        {
            var session = _getActiveSession();
            if (session != null)
            {
                var timeline = session.GetTimelineProperties();
                if (timeline != null)
                {
                    var current = timeline.Position;
                    var target = current + TimeSpan.FromSeconds(seconds);

                    if (target < TimeSpan.Zero)
                        target = TimeSpan.Zero;
                    if (timeline.EndTime > TimeSpan.Zero && target > timeline.EndTime)
                        target = timeline.EndTime;

                    await session.TryChangePlaybackPositionAsync(target.Ticks);
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-CTRL", ex, "SeekRelative failed");
        }
    }
}
