namespace VNotch.Services;

public interface IMediaMetadataLookupService
{
    /// <summary>
    /// Resolves a YouTube video ID from a direct URL. This is the preferred method — 100% accurate.
    /// Returns video info (id, title, author, duration) via oEmbed validation.
    /// </summary>
    Task<YouTubeLookupResult?> TryGetYouTubeVideoInfoFromUrlAsync(string url, CancellationToken ct = default);

    /// <summary>
    /// Legacy fallback: tries to resolve a YouTube video ID from title/artist.
    /// Only checks if title itself is a video ID or contains a URL. No search-by-name.
    /// </summary>
    Task<YouTubeLookupResult?> TryGetYouTubeVideoIdWithInfoAsync(string title, string artist = "", CancellationToken ct = default);

    Task<string?> TryGetSoundCloudArtworkUrlAsync(string title, string artist = "", bool requireStrongMatch = false, CancellationToken ct = default);

    /// <summary>
    /// Resolves SoundCloud artwork directly from a SoundCloud track URL. 100% accurate.
    /// </summary>
    Task<string?> TryGetSoundCloudArtworkFromUrlAsync(string soundCloudUrl, CancellationToken ct = default);
}
