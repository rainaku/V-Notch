namespace VNotch.Services;

/// <summary>
/// Thread-safe, in-memory cache of resolved YouTube video ids keyed by track (identity), plus a
/// blocklist of video ids already found to mismatch their track. Extracted from
/// <see cref="MediaDetectionService"/> so the bounded-eviction and lookup-fallback rules can be
/// unit-tested without any SMTC/Win32 dependency.
/// </summary>
internal sealed class VideoIdCache
{
    private const int Capacity = 50;

    private readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    // Video ids that were validated against a track and found stale; skipped during rapid polling.
    private readonly HashSet<string> _mismatch = new(StringComparer.Ordinal);

    /// <summary>Number of cached track→id entries (test/observability hook).</summary>
    public int Count
    {
        get { lock (_lock) return _cache.Count; }
    }

    /// <summary>Stores a video id for a track identity, evicting an arbitrary entry past capacity.</summary>
    public void Set(string? trackIdentity, string videoId)
    {
        if (string.IsNullOrEmpty(trackIdentity)) return;
        lock (_lock)
        {
            _cache[trackIdentity] = videoId;
            // Keep cache bounded
            if (_cache.Count > Capacity)
            {
                var firstKey = _cache.Keys.First();
                _cache.Remove(firstKey);
            }
        }
    }

    /// <summary>Looks up by exact track name first, then by the track-only identity key.</summary>
    public string? Get(string? track)
    {
        if (string.IsNullOrEmpty(track)) return null;
        lock (_lock)
        {
            // Try exact track name match
            if (_cache.TryGetValue(track, out var id))
                return id;
            // Try track identity match (track + artist combined key)
            string trackIdentity = MediaHeuristics.BuildTrackIdentity(track, "");
            if (_cache.TryGetValue(trackIdentity, out id))
                return id;
            return null;
        }
    }

    /// <summary>Drops every entry except the one for the given identity (or clears all when null/empty).</summary>
    public void ForgetExcept(string? currentTrackIdentity)
    {
        lock (_lock)
        {
            if (_cache.Count == 0) return;
            if (string.IsNullOrEmpty(currentTrackIdentity))
            {
                _cache.Clear();
                return;
            }

            var doomed = new List<string>();
            foreach (var key in _cache.Keys)
            {
                if (!string.Equals(key, currentTrackIdentity, StringComparison.OrdinalIgnoreCase))
                {
                    doomed.Add(key);
                }
            }
            foreach (var key in doomed)
            {
                _cache.Remove(key);
            }
        }
    }

    /// <summary>Removes the cached id for a track (both exact and identity keys) only if it matches the stale id.</summary>
    public void Evict(string? track, string staleVideoId)
    {
        if (string.IsNullOrEmpty(track) || string.IsNullOrEmpty(staleVideoId)) return;
        lock (_lock)
        {
            string trackIdentity = MediaHeuristics.BuildTrackIdentity(track, "");
            if (_cache.TryGetValue(track, out var v1) &&
                string.Equals(v1, staleVideoId, StringComparison.Ordinal))
            {
                _cache.Remove(track);
            }
            if (_cache.TryGetValue(trackIdentity, out var v2) &&
                string.Equals(v2, staleVideoId, StringComparison.Ordinal))
            {
                _cache.Remove(trackIdentity);
            }
        }
    }

    // ── Mismatch blocklist ──

    public bool IsMismatch(string videoId)
    {
        lock (_mismatch) return _mismatch.Contains(videoId);
    }

    public void MarkMismatch(string videoId)
    {
        lock (_mismatch) _mismatch.Add(videoId);
    }

    public void ClearMismatches()
    {
        lock (_mismatch) _mismatch.Clear();
    }
}
