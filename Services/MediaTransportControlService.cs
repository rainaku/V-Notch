using System;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace VNotch.Services;

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
            bool success = false;
            if (session != null)
            {
                success = await session.TryTogglePlayPauseAsync();
            }
            if (!success)
            {
                SendMediaKey(Win32Interop.VK_MEDIA_PLAY_PAUSE);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-CTRL", ex, "PlayPause failed");
            SendMediaKey(Win32Interop.VK_MEDIA_PLAY_PAUSE);
        }
    }

    public async Task NextTrackAsync()
    {
        try
        {
            var session = _getActiveSession();
            bool success = false;
            if (session != null)
            {
                success = await session.TrySkipNextAsync();
            }
            if (!success)
            {
                SendMediaKey(Win32Interop.VK_MEDIA_NEXT_TRACK);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-CTRL", ex, "NextTrack failed");
            SendMediaKey(Win32Interop.VK_MEDIA_NEXT_TRACK);
        }
    }

    public async Task PreviousTrackAsync()
    {
        try
        {
            var session = _getActiveSession();
            bool success = false;
            if (session != null)
            {
                success = await session.TrySkipPreviousAsync();
            }
            if (!success)
            {
                SendMediaKey(Win32Interop.VK_MEDIA_PREV_TRACK);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-CTRL", ex, "PreviousTrack failed");
            SendMediaKey(Win32Interop.VK_MEDIA_PREV_TRACK);
        }
    }

    private static void SendMediaKey(byte key)
    {
        Win32Interop.keybd_event(key, 0, Win32Interop.KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        Win32Interop.keybd_event(key, 0, Win32Interop.KEYEVENTF_EXTENDEDKEY | Win32Interop.KEYEVENTF_KEYUP, UIntPtr.Zero);
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

    public async Task SeekToAbsoluteAsync(TimeSpan position)
    {
        try
        {
            var session = _getActiveSession();
            if (session != null)
            {
                var timeline = session.GetTimelineProperties();
                if (timeline != null)
                {
                    var target = position;
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
            RuntimeLog.Error("MEDIA-CTRL", ex, "SeekToAbsolute failed");
        }
    }
}
