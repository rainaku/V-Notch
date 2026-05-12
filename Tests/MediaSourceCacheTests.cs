using System.IO;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class MediaSourceCacheTests : IDisposable
{
    private readonly string _tempPath;
    private readonly MediaSourceCache _cache;

    public MediaSourceCacheTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), $"vnotch-test-{Guid.NewGuid()}.json");
        _cache = new MediaSourceCache(_tempPath);
    }

    public void Dispose()
    {
        if (File.Exists(_tempPath))
            File.Delete(_tempPath);
    }

    [Fact]
    public void Set_And_Get_ReturnsValue()
    {
        _cache.Set("track1|artist1", "YouTube");
        Assert.Equal("YouTube", _cache.Get("track1|artist1"));
    }

    [Fact]
    public void Get_NonExistent_ReturnsNull()
    {
        Assert.Null(_cache.Get("nonexistent"));
    }

    [Fact]
    public void TryGet_Existing_ReturnsTrueWithValue()
    {
        _cache.Set("key", "Spotify");
        Assert.True(_cache.TryGet("key", out var value));
        Assert.Equal("Spotify", value);
    }

    [Fact]
    public void TryGet_NonExistent_ReturnsFalse()
    {
        Assert.False(_cache.TryGet("missing", out _));
    }

    [Fact]
    public void SetBoth_SetsFullAndTrackOnly()
    {
        _cache.SetBoth("track|artist", "track|", "SoundCloud");
        Assert.Equal("SoundCloud", _cache.Get("track|artist"));
        Assert.Equal("SoundCloud", _cache.Get("track|"));
    }

    [Fact]
    public void HasSource_MatchesFull()
    {
        _cache.Set("track|artist", "YouTube");
        Assert.True(_cache.HasSource("track|artist", "", "YouTube"));
    }

    [Fact]
    public void HasSource_MatchesTrackOnly()
    {
        _cache.Set("track|", "YouTube");
        Assert.True(_cache.HasSource("", "track|", "YouTube"));
    }

    [Fact]
    public void HasSource_CaseInsensitive()
    {
        _cache.Set("key", "YouTube");
        Assert.True(_cache.HasSource("key", "", "youtube"));
    }

    [Fact]
    public void HasSource_WrongSource_ReturnsFalse()
    {
        _cache.Set("key", "Spotify");
        Assert.False(_cache.HasSource("key", "", "YouTube"));
    }

    [Fact]
    public void Save_And_Load_Persists()
    {
        _cache.Set("track1", "YouTube");
        _cache.Set("track2", "SoundCloud");
        _cache.Save();

        var loaded = new MediaSourceCache(_tempPath);
        loaded.Load();

        Assert.Equal("YouTube", loaded.Get("track1"));
        Assert.Equal("SoundCloud", loaded.Get("track2"));
    }

    [Fact]
    public void Save_OnlyWhenDirty()
    {
        // No changes — save should not create file
        _cache.Save();
        Assert.False(File.Exists(_tempPath));

        // After set — save should create file
        _cache.Set("key", "value");
        _cache.Save();
        Assert.True(File.Exists(_tempPath));
    }

    [Fact]
    public void Clear_RemovesAll()
    {
        _cache.Set("a", "1");
        _cache.Set("b", "2");
        _cache.Clear();

        Assert.Equal(0, _cache.Count);
        Assert.Null(_cache.Get("a"));
    }

    [Fact]
    public void Set_EmptyKey_Ignored()
    {
        _cache.Set("", "YouTube");
        _cache.Set(null!, "YouTube");
        Assert.Equal(0, _cache.Count);
    }

    [Fact]
    public void Set_EmptyValue_Ignored()
    {
        _cache.Set("key", "");
        _cache.Set("key", null!);
        Assert.Equal(0, _cache.Count);
    }

    [Fact]
    public void Set_SameValue_DoesNotMarkDirty()
    {
        _cache.Set("key", "YouTube");
        _cache.ForceSave(); // Clear dirty flag

        // Delete file to detect if Save writes
        File.Delete(_tempPath);

        _cache.Set("key", "YouTube"); // Same value
        _cache.Save(); // Should not write (not dirty)
        Assert.False(File.Exists(_tempPath));
    }
}
