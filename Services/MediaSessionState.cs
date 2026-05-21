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
    }
}
