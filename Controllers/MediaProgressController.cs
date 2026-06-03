using System;
using System.Threading;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.Controllers;

public sealed class MediaProgressController
{
    private readonly ProgressEngine _engine = new();
    private long _sequence;

    public UiProgressFrame GetUiFrame() => _engine.GetUiFrame();

    public void Reset()
    {
        _engine.Reset();
        Interlocked.Exchange(ref _sequence, 0);
    }

    public void NotifyUserSeek(TimeSpan position) => _engine.NotifyUserSeek(position);

    public void NotifyUserPlayPause(bool isPlaying) => _engine.NotifyUserPlayPause(isPlaying);

    public void PublishMediaSnapshot(MediaInfo info, bool isBrowserSource, bool isSeekEnabled)
    {
        if (info == null) throw new ArgumentNullException(nameof(info));

        var snapshot = new ProgressSnapshot
        {
            Position = info.Position,
            Duration = info.Duration,
            IsPlaying = info.IsPlaying,
            IsYouTube = isBrowserSource,
            PlaybackRate = info.PlaybackRate,
            IsSeekEnabled = isSeekEnabled,
            IsIndeterminate = info.IsIndeterminate,
            Timestamp = info.LastUpdated.UtcDateTime,
            SequenceNumber = Interlocked.Increment(ref _sequence)
        };

        _engine.OnMediaSnapshot(snapshot);
    }
}
