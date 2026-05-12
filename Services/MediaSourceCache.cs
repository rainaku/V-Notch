using System.IO;
using System.Text.Json;

namespace VNotch.Services;

/// <summary>
/// Persists track → source platform mappings to disk so that platform detection
/// survives app restarts. Extracted from MediaDetectionService for single responsibility.
/// 
/// Thread safety: all public methods are safe to call from any thread.
/// The cache is stored as a simple JSON dictionary at %APPDATA%/V-Notch/source_cache.json.
/// </summary>
public class MediaSourceCache
{
    private readonly string _cachePath;
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private bool _isDirty;

    public MediaSourceCache()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "V-Notch");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        _cachePath = Path.Combine(dir, "source_cache.json");
    }

    /// <summary>
    /// Constructor with explicit path (for testing).
    /// </summary>
    public MediaSourceCache(string cachePath)
    {
        _cachePath = cachePath;
        var dir = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    /// <summary>Load cache from disk. Safe to call multiple times.</summary>
    public void Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_cachePath)) return;

                var json = File.ReadAllText(_cachePath);
                var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (data != null)
                {
                    _cache.Clear();
                    foreach (var kvp in data)
                        _cache[kvp.Key] = kvp.Value;
                }

                _isDirty = false;
            }
            catch (Exception ex)
            {
                RuntimeLog.Log("SOURCE-CACHE-LOAD", ex.ToString());
            }
        }
    }

    /// <summary>Save cache to disk (only if dirty).</summary>
    public void Save()
    {
        lock (_lock)
        {
            if (!_isDirty) return;

            try
            {
                var json = JsonSerializer.Serialize(_cache);
                File.WriteAllText(_cachePath, json);
                _isDirty = false;
            }
            catch (Exception ex)
            {
                RuntimeLog.Log("SOURCE-CACHE-SAVE", ex.ToString());
            }
        }
    }

    /// <summary>Force save regardless of dirty flag.</summary>
    public void ForceSave()
    {
        lock (_lock)
        {
            try
            {
                var json = JsonSerializer.Serialize(_cache);
                File.WriteAllText(_cachePath, json);
                _isDirty = false;
            }
            catch (Exception ex)
            {
                RuntimeLog.Log("SOURCE-CACHE-SAVE", ex.ToString());
            }
        }
    }

    /// <summary>Get the cached source for a track identity. Returns null if not found.</summary>
    public string? Get(string trackIdentity)
    {
        if (string.IsNullOrEmpty(trackIdentity)) return null;

        lock (_lock)
        {
            return _cache.TryGetValue(trackIdentity, out var source) ? source : null;
        }
    }

    /// <summary>Try to get the cached source for a track identity.</summary>
    public bool TryGet(string trackIdentity, out string source)
    {
        source = string.Empty;
        if (string.IsNullOrEmpty(trackIdentity)) return false;

        lock (_lock)
        {
            if (_cache.TryGetValue(trackIdentity, out var cached))
            {
                source = cached;
                return true;
            }
            return false;
        }
    }

    /// <summary>Set the source for a track identity. Marks cache as dirty.</summary>
    public void Set(string trackIdentity, string source)
    {
        if (string.IsNullOrEmpty(trackIdentity) || string.IsNullOrEmpty(source)) return;

        lock (_lock)
        {
            if (_cache.TryGetValue(trackIdentity, out var existing) &&
                string.Equals(existing, source, StringComparison.Ordinal))
                return; // No change

            _cache[trackIdentity] = source;
            _isDirty = true;
        }
    }

    /// <summary>
    /// Set source for both full identity (track+artist) and track-only identity.
    /// Convenience method for the common pattern.
    /// </summary>
    public void SetBoth(string fullIdentity, string trackOnlyIdentity, string source)
    {
        if (string.IsNullOrEmpty(source)) return;

        lock (_lock)
        {
            bool changed = false;

            if (!string.IsNullOrEmpty(fullIdentity))
            {
                if (!_cache.TryGetValue(fullIdentity, out var existing) ||
                    !string.Equals(existing, source, StringComparison.Ordinal))
                {
                    _cache[fullIdentity] = source;
                    changed = true;
                }
            }

            if (!string.IsNullOrEmpty(trackOnlyIdentity))
            {
                if (!_cache.TryGetValue(trackOnlyIdentity, out var existing) ||
                    !string.Equals(existing, source, StringComparison.Ordinal))
                {
                    _cache[trackOnlyIdentity] = source;
                    changed = true;
                }
            }

            if (changed) _isDirty = true;
        }
    }

    /// <summary>
    /// Check if a track identity has a specific source cached.
    /// Checks both full identity and track-only identity.
    /// </summary>
    public bool HasSource(string fullIdentity, string trackOnlyIdentity, string expectedSource)
    {
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(fullIdentity) &&
                _cache.TryGetValue(fullIdentity, out var s1) &&
                string.Equals(s1, expectedSource, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.IsNullOrEmpty(trackOnlyIdentity) &&
                _cache.TryGetValue(trackOnlyIdentity, out var s2) &&
                string.Equals(s2, expectedSource, StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }
    }

    /// <summary>Number of entries in the cache.</summary>
    public int Count
    {
        get { lock (_lock) return _cache.Count; }
    }

    /// <summary>Clear all cached entries.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _cache.Clear();
            _isDirty = true;
        }
    }
}
