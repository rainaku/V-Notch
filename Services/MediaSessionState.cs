using System;
using System.Collections.Generic;

namespace VNotch.Services;

/// <summary>
/// Consolidates per-session tracking state that was previously scattered across
/// multiple Dictionary&lt;string, T&gt; fields in MediaDetectionService.
/// Each media session (identified by its instance key or app ID) gets one entry.
/// </summary>
public sealed class MediaSessionState
{
    private readonly Dictionary<string, SessionEntry> _sessions = new();
    private readonly Dictionary<string, string> _sourceOverrides = new();

    /// <summary>
    /// Gets or creates the session entry for the given session key.
    /// </summary>
    public SessionEntry GetOrCreate(string sessionKey)
    {
        if (!_sessions.TryGetValue(sessionKey, out var entry))
        {
            entry = new SessionEntry();
            _sessions[sessionKey] = entry;
        }
        return entry;
    }

    /// <summary>
    /// Tries to get an existing session entry without creating one.
    /// </summary>
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

    // ─── Timeline Position Advancement (keyed by session instance key) ───
    //
    // The only reliable way to tell which of several "Playing" sessions is truly
    // producing audio/video is to observe whether its playback position keeps
    // moving forward. Two browser tabs share the same SourceAppUserModelId, and a
    // backgrounded tab can keep reporting a stale "Playing" status — but only the
    // audible one advances its position.

    public enum TimelineAdvanceResult
    {
        /// <summary>Not enough time elapsed since the last sample to judge — ignore.</summary>
        Indeterminate,
        /// <summary>Position moved forward roughly in line with elapsed time.</summary>
        Advanced,
        /// <summary>Position did not move (paused / stale "Playing" status).</summary>
        Stalled
    }

    // Minimum wall-clock gap between two samples before advancement can be judged.
    // Below this, a genuinely-playing video may not have moved enough to distinguish
    // from a frozen one, so we keep the existing baseline and report Indeterminate.
    private static readonly TimeSpan MinAdvanceSampleInterval = TimeSpan.FromMilliseconds(350);

    /// <summary>
    /// Records a timeline position sample and reports whether the position advanced
    /// forward in proportion to the elapsed wall-clock time. Only re-baselines the
    /// stored sample once <see cref="MinAdvanceSampleInterval"/> has elapsed, so
    /// rapid back-to-back scans don't produce false "stalled" readings.
    /// </summary>
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

        // A truly playing session advances roughly in step with elapsed time
        // (allowing for playback rate and reporting jitter). Require at least
        // ~40% of elapsed time as forward movement to count as advancing.
        if (delta >= TimeSpan.FromTicks((long)(elapsed.Ticks * 0.4)))
        {
            entry.LastPositionAdvanceUtc = nowUtc;
            return TimelineAdvanceResult.Advanced;
        }

        return TimelineAdvanceResult.Stalled;
    }

    /// <summary>
    /// Returns true if the session's position was observed advancing within the
    /// given time window.
    /// </summary>
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

    /// <summary>
    /// Removes all tracked state. Call on full reset.
    /// </summary>
    public void Clear()
    {
        _sessions.Clear();
        _sourceOverrides.Clear();
    }

    /// <summary>
    /// Per-session tracking data consolidated into a single object.
    /// </summary>
    public sealed class SessionEntry
    {
        /// <summary>Last time this session/app was actively playing.</summary>
        public DateTime? LastPlayingTime { get; set; }

        /// <summary>When playback started for this session (for recency scoring).</summary>
        public DateTime? PlayStartTime { get; set; }

        /// <summary>Whether this session is currently in a playing state.</summary>
        public bool IsPlaying { get; set; }

        /// <summary>Last observed timeline position (for advancement detection).</summary>
        public TimeSpan? LastTimelinePosition { get; set; }

        /// <summary>When the last timeline position sample was taken.</summary>
        public DateTime? LastTimelineSampleUtc { get; set; }

        /// <summary>Last time the position was observed moving forward.</summary>
        public DateTime? LastPositionAdvanceUtc { get; set; }
    }
}
