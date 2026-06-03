using System.IO;
using System.Text.Json;

namespace VNotch.Services;
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
public MediaSourceCache(string cachePath)
    {
        _cachePath = cachePath;
        var dir = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }
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
public void Set(string key, string source)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(source)) return;

        lock (_lock)
        {
            if (!_cache.TryGetValue(key, out var existing) ||
                !string.Equals(existing, source, StringComparison.Ordinal))
            {
                _cache[key] = source;
                _isDirty = true;
            }
        }
    }
public string? Get(string key)
    {
        return TryGet(key, out var value) ? value : null;
    }
public void ForceSave()
    {
        lock (_lock)
        {
            _isDirty = true;
        }

        Save();
    }public bool HasSource(string fullIdentity, string trackOnlyIdentity, string expectedSource)
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
public int Count
    {
        get { lock (_lock) return _cache.Count; }
    }
public void Clear()
    {
        lock (_lock)
        {
            _cache.Clear();
            _isDirty = true;
        }
    }

    public void Load()
    {
        if (!File.Exists(_cachePath)) return;
        try
        {
            var json = File.ReadAllText(_cachePath);
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (data != null)
            {
                lock (_lock)
                {
                    _cache.Clear();
                    foreach (var kvp in data)
                        _cache[kvp.Key] = kvp.Value;
                    _isDirty = false;
                }
            }
        }
        catch {  }
    }

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
            catch {  }
        }
    }

    public bool TryGet(string key, out string? value)
    {
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(key) && _cache.TryGetValue(key, out var v))
            {
                value = v;
                return true;
            }
            value = null;
            return false;
        }
    }
}

