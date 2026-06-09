using VNotch.Models;

namespace VNotch.Services;

/// <summary>
/// Resolved timeline values and context for <see cref="TimelinePositionSolver.Solve"/>. The caller
/// reads the raw SMTC timeline (start/end/seek/position/last-updated) and the surrounding context
/// (new-track / browser-source / playback flags) and supplies an explicit clock; the solver itself is
/// pure so the position/duration/latency-compensation math is deterministic and unit-testable.
/// </summary>
internal readonly struct TimelineSolveInputs
{
    // ─── Raw SMTC timeline values (already converted to UTC where applicable) ───
    public TimeSpan StartTime { get; init; }
    public TimeSpan EndTime { get; init; }
    public TimeSpan MaxSeekTime { get; init; }
    public TimeSpan Position { get; init; }
    public DateTimeOffset LastUpdatedUtc { get; init; }

    // ─── Context ───
    /// <summary>Initial pass, track signature reset, or within ~4s of a metadata change.</summary>
    public bool IsInitialOrBigChange { get; init; }
    /// <summary>The current track differs from the previously-seen track name.</summary>
    public bool IsNewTrack { get; init; }
    /// <summary>Source is a browser/video platform (looser timeline-latency window applies).</summary>
    public bool IsBrowserTimelineTrack { get; init; }
    public bool IsPlaying { get; init; }
    public double PlaybackRate { get; init; }
    public DateTimeOffset NowUtc { get; init; }
}

/// <summary>
/// Outcome of <see cref="TimelinePositionSolver.Solve"/>. <see cref="Position"/>, <see cref="Duration"/>
/// and <see cref="LastUpdatedUtc"/> are written to the <see cref="MediaInfo"/>; <see cref="IsIndeterminate"/>
/// is applied only when non-null (null = leave the existing value untouched).
/// </summary>
internal readonly struct TimelineSolveResult
{
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTimeOffset LastUpdatedUtc { get; init; }
    public bool? IsIndeterminate { get; init; }
}

/// <summary>
/// Pure timeline position/duration resolver extracted from <see cref="MediaDetectionService"/>'s
/// <c>ApplyTimelinePropertiesAsync</c>. It derives the displayed playback position from the SMTC
/// timeline by: computing duration (falling back to max-seek time), snapping a near-end new track
/// back to the start, discarding a suspicious carried-over position on a browser track start, applying
/// latency compensation for the gap between the timeline's last-updated timestamp and "now", and
/// clamping the result into <c>[0, duration]</c>.
/// </summary>
internal static class TimelinePositionSolver
{
    public static TimelineSolveResult Solve(in TimelineSolveInputs x)
    {
        var duration = x.EndTime - x.StartTime;
        if (duration <= TimeSpan.Zero) duration = x.MaxSeekTime;

        bool isNearlyEnd = duration.TotalSeconds > 0 &&
            (duration - x.Position).TotalMilliseconds < 800;

        TimeSpan chosenPosition;
        bool forceStartPosition = false;
        bool? indeterminate = null;

        if (x.IsNewTrack && isNearlyEnd)
        {
            chosenPosition = TimeSpan.Zero;
            indeterminate = false;
            forceStartPosition = true;
        }
        else
        {
            chosenPosition = x.Position;
        }

        if (x.IsBrowserTimelineTrack && x.IsNewTrack && x.IsPlaying)
        {
            TimeSpan timelineAge = x.NowUtc - x.LastUpdatedUtc;
            if (timelineAge < TimeSpan.Zero) timelineAge = TimeSpan.Zero;

            bool suspiciousCarryOverPosition =
                chosenPosition.TotalSeconds > 20 &&
                (duration.TotalSeconds <= 0 ||
                 chosenPosition.TotalSeconds > duration.TotalSeconds * 0.2);
            bool staleTimelineAtTrackStart = timelineAge > TimeSpan.FromMilliseconds(900);

            if (suspiciousCarryOverPosition && staleTimelineAtTrackStart)
            {
                chosenPosition = TimeSpan.Zero;
                forceStartPosition = true;
            }
        }

        var rawTimelineUpdatedUtc = x.LastUpdatedUtc;
        var timelineUpdatedUtc = forceStartPosition
            ? x.NowUtc
            : rawTimelineUpdatedUtc;

        if (!forceStartPosition && x.IsPlaying)
        {
            var nowUpdatedUtc = x.NowUtc;
            var timelineLatency = nowUpdatedUtc - rawTimelineUpdatedUtc;
            var maxCompensationWindow = TimeSpan.FromSeconds(15);
            if (x.IsBrowserTimelineTrack)
            {
                if (x.IsInitialOrBigChange)
                {
                    maxCompensationWindow = DurationCompensationWindow(duration);
                }
                else
                {
                    maxCompensationWindow = TimeSpan.FromMinutes(2);
                }
            }
            else if (x.IsInitialOrBigChange)
            {
                maxCompensationWindow = DurationCompensationWindow(duration);
            }

            bool validTimelineTimestamp =
                rawTimelineUpdatedUtc > DateTimeOffset.MinValue &&
                rawTimelineUpdatedUtc <= nowUpdatedUtc.AddMilliseconds(250) &&
                timelineLatency >= TimeSpan.Zero &&
                timelineLatency <= maxCompensationWindow;

            if (validTimelineTimestamp && timelineLatency.TotalMilliseconds > 100)
            {
                double playbackRate = x.PlaybackRate > 0 ? x.PlaybackRate : 1.0;
                var compensatedPosition = chosenPosition + TimeSpan.FromSeconds(timelineLatency.TotalSeconds * playbackRate);

                // Don't let compensation push position to or past duration
                if (duration > TimeSpan.Zero)
                {
                    TimeSpan maxAllowed = TimeSpan.FromSeconds(duration.TotalSeconds * 0.95);
                    if (compensatedPosition > maxAllowed)
                        compensatedPosition = maxAllowed;
                }

                if (compensatedPosition > chosenPosition)
                {
                    chosenPosition = compensatedPosition;
                    timelineUpdatedUtc = nowUpdatedUtc;
                }
            }
        }

        if (chosenPosition < TimeSpan.Zero)
        {
            chosenPosition = TimeSpan.Zero;
        }
        if (duration > TimeSpan.Zero && chosenPosition > duration)
        {
            chosenPosition = duration;
        }

        if (duration <= TimeSpan.Zero || duration.TotalDays > 30)
        {
            indeterminate = true;
        }

        return new TimelineSolveResult
        {
            Position = chosenPosition,
            Duration = duration,
            LastUpdatedUtc = timelineUpdatedUtc,
            IsIndeterminate = indeterminate,
        };
    }

    /// <summary>
    /// Latency-compensation window for an "initial / big change" pass: the track duration plus 5s,
    /// falling back to 10 minutes when the duration is unknown, capped at 4 hours.
    /// </summary>
    private static TimeSpan DurationCompensationWindow(TimeSpan duration)
    {
        var durationWindow = duration > TimeSpan.Zero
            ? duration + TimeSpan.FromSeconds(5)
            : TimeSpan.FromMinutes(10);
        return durationWindow < TimeSpan.FromHours(4)
            ? durationWindow
            : TimeSpan.FromHours(4);
    }
}
