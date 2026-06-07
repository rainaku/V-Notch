using System.IO;
using System.Text.Json;

namespace VNotch.Services;
public class MediaSourceCache
{
    private const int DefaultCapacity = 500;

    private readonly string _cachePath;
    private readonly int _capacity;

    // Stores key -> source value.
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    // Tracks usage recency: front = most recently used, back = least recently used.
    private readonly LinkedList<string> _lru = new();
    // Maps a key to its node in the LRU list for O(1) moves/removals.
    private readonly Dictionary<string, LinkedListNode<string>> _nodes = new(StringComparer.Ordinal);

    private readonly object _lock = new();
    private bool _isDirty;

    public MediaSourceCache(int capacity = DefaultCapacity)
    {
        _capacity = capacity > 0 ? capacity : DefaultCapacity;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "V-Notch");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        _cachePath = Path.Combine(dir, "source_cache.json");
    }

    public MediaSourceCache(string cachePath, int capacity = DefaultCapacity)
    {
        _capacity = capacity > 0 ? capacity : DefaultCapacity;
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
                changed |= SetEntry(fullIdentity, source);

            if (!string.IsNullOrEmpty(trackOnlyIdentity))
                changed |= SetEntry(trackOnlyIdentity, source);

            if (changed)
            {
                _isDirty = true;
                Evict();
            }
        }
    }

    public bool HasSource(string fullIdentity, string trackOnlyIdentity, string expectedSource)
    {
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(fullIdentity) &&
                _cache.TryGetValue(fullIdentity, out var s1) &&
                string.Equals(s1, expectedSource, StringComparison.OrdinalIgnoreCase))
            {
                Touch(fullIdentity);
                return true;
            }

            if (!string.IsNullOrEmpty(trackOnlyIdentity) &&
                _cache.TryGetValue(trackOnlyIdentity, out var s2) &&
                string.Equals(s2, expectedSource, StringComparison.OrdinalIgnoreCase))
            {
                Touch(trackOnlyIdentity);
                return true;
            }

            return false;
        }
    }

    public int Count
    {
        get { lock (_lock) return _cache.Count; }
    }

    public int Capacity => _capacity;

    public void Clear()
    {
        lock (_lock)
        {
            _cache.Clear();
            _lru.Clear();
            _nodes.Clear();
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
                    _lru.Clear();
                    _nodes.Clear();
                    foreach (var kvp in data)
                        SetEntry(kvp.Key, kvp.Value);
                    Evict();
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
                Touch(key);
                value = v;
                return true;
            }
            value = null;
            return false;
        }
    }

    // Adds or updates an entry and marks it most recently used.
    // Caller must hold _lock. Returns true if the stored value changed.
    private bool SetEntry(string key, string source)
    {
        bool changed = !_cache.TryGetValue(key, out var existing) ||
                       !string.Equals(existing, source, StringComparison.Ordinal);

        _cache[key] = source;
        Touch(key);
        return changed;
    }

    // Marks a key as most recently used. Caller must hold _lock.
    private void Touch(string key)
    {
        if (_nodes.TryGetValue(key, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
        }
        else
        {
            _nodes[key] = _lru.AddFirst(key);
        }
    }

    // Evicts least-recently-used entries until within capacity. Caller must hold _lock.
    private void Evict()
    {
        while (_cache.Count > _capacity && _lru.Last is { } last)
        {
            _lru.RemoveLast();
            _nodes.Remove(last.Value);
            _cache.Remove(last.Value);
            _isDirty = true;
        }
    }
}
