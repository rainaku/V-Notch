using System;
using System.Collections.Generic;

namespace VNotch.Services;

public sealed class MediaSessionState
{
    private readonly Dictionary<string, SessionEntry> _sessions = new();
    private readonly Dictionary<string, string> _sourceOverrides = new();

    public SessionEntry GetOrCreate(string sessionKey)
    {
        if (!_sessions.TryGetValue(sessionKey, out var entry))
        {
            entry = new SessionEntry();
            _sessions[sessionKey] = entry;
        }
        return entry;
    }

    public bool TryGet(string sessionKey, out SessionEntry entry)
    {
        return _sessions.TryGetValue(sessionKey, out entry!);
    }

    // ─── Last Playing Times (keyed by source app ID) ───

    public void SetLastPlayingTime(string sourceAppId, DateTime time)
    {
        GetOrCreate(sourceAppId).LastPlayingTime = time;
    }

    public bool TryGetLastPlayingTime(string sourceAppId, out DateTime time)
    {
        if (_sessions.TryGetValue(sourceAppId, out var entry) && entry.LastPlayingTime.HasValue)
        {
            time = entry.LastPlayingTime.Value;
            return true;
        }
        time = default;
        return false;
    }

    // ─── Play Start Times (keyed by session instance key) ───

    public void SetPlayStartTime(string sessionKey, DateTime time)
    {
        GetOrCreate(sessionKey).PlayStartTime = time;
    }

    public bool TryGetPlayStartTime(string sessionKey, out DateTime time)
    {
        if (_sessions.TryGetValue(sessionKey, out var entry) && entry.PlayStartTime.HasValue)
        {
            time = entry.PlayStartTime.Value;
            return true;
        }
        time = default;
        return false;
    }

    // ─── Playing States (keyed by session instance key) ───

    public void SetPlayingState(string sessionKey, bool isPlaying)
    {
        GetOrCreate(sessionKey).IsPlaying = isPlaying;
    }

    public bool GetPlayingState(string sessionKey)
    {
        return _sessions.TryGetValue(sessionKey, out var entry) && entry.IsPlaying;
    }


    public enum TimelineAdvanceResult
    {
        Indeterminate,
        Advanced,
        Stalled
    }

    private static readonly TimeSpan MinAdvanceSampleInterval = TimeSpan.FromMilliseconds(350);

    public TimelineAdvanceResult RecordTimelinePosition(string sessionKey, TimeSpan position, DateTime nowUtc)
    {
        var entry = GetOrCreate(sessionKey);

        if (!entry.LastTimelineSampleUtc.HasValue || !entry.LastTimelinePosition.HasValue)
        {
            entry.LastTimelinePosition = position;
            entry.LastTimelineSampleUtc = nowUtc;
            return TimelineAdvanceResult.Indeterminate;
        }

        var elapsed = nowUtc - entry.LastTimelineSampleUtc.Value;
        if (elapsed < MinAdvanceSampleInterval)
        {
            // Keep the baseline; not enough time to judge yet.
            return TimelineAdvanceResult.Indeterminate;
        }

        var delta = position - entry.LastTimelinePosition.Value;

        // Re-baseline for the next comparison.
        entry.LastTimelinePosition = position;
        entry.LastTimelineSampleUtc = nowUtc;

        if (delta >= TimeSpan.FromTicks((long)(elapsed.Ticks * 0.4)))
        {
            entry.LastPositionAdvanceUtc = nowUtc;
            return TimelineAdvanceResult.Advanced;
        }

        return TimelineAdvanceResult.Stalled;
    }

    public bool IsRecentlyAdvancing(string sessionKey, DateTime nowUtc, TimeSpan window)
    {
        if (_sessions.TryGetValue(sessionKey, out var entry) && entry.LastPositionAdvanceUtc.HasValue)
        {
            var age = nowUtc - entry.LastPositionAdvanceUtc.Value;
            return age >= TimeSpan.Zero && age <= window;
        }
        return false;
    }

    // ─── Source Overrides (keyed by composite key) ───

    public void SetSourceOverride(string key, string source)
    {
        if (!string.IsNullOrEmpty(key))
            _sourceOverrides[key] = source;
    }

    public bool TryGetSourceOverride(string key, out string source)
    {
        return _sourceOverrides.TryGetValue(key, out source!);
    }

    public void RemoveSourceOverride(string key)
    {
        if (!string.IsNullOrEmpty(key))
            _sourceOverrides.Remove(key);
    }

    public void Clear()
    {
        _sessions.Clear();
        _sourceOverrides.Clear();
    }

    public sealed class SessionEntry
    {
        public DateTime? LastPlayingTime { get; set; }

        public DateTime? PlayStartTime { get; set; }

        public bool IsPlaying { get; set; }

        public TimeSpan? LastTimelinePosition { get; set; }

        public DateTime? LastTimelineSampleUtc { get; set; }

        public DateTime? LastPositionAdvanceUtc { get; set; }
    }
}
