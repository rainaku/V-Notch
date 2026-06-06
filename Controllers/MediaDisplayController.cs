using System;
using System.Windows.Media;
using VNotch.Models;

namespace VNotch.Controllers;

public sealed class MediaDisplayController
{
    private static readonly string[] GenericTitles =
    {
        "Spotify", "Spotify Premium", "Spotify Free", "YouTube", "SoundCloud", "Browser"
    };

    // ─── State ───

    private string _lastAnimatedTrackSignature = "";
    private string _lastColorTrackSignature = "";
    private string _lastRenderedMediaSource = "";
    private ImageSource? _lastAnimatedThumbnail;
    private ImageSource? _lastBackgroundThumbnail;
    private bool _thumbnailShownForCurrentTrack = false;
    private bool _trackChangeBounceNeeded = false;
    private int _thumbnailSwitchGeneration = 0;

    // ─── Public State Queries ───

    public string LastAnimatedTrackSignature => _lastAnimatedTrackSignature;
    public string LastColorTrackSignature => _lastColorTrackSignature;
    public string LastRenderedMediaSource => _lastRenderedMediaSource;
    public ImageSource? LastAnimatedThumbnail => _lastAnimatedThumbnail;
    public bool ThumbnailShownForCurrentTrack => _thumbnailShownForCurrentTrack;
    public bool TrackChangeBounceNeeded => _trackChangeBounceNeeded;
    public int ThumbnailSwitchGeneration => _thumbnailSwitchGeneration;

    // ─── Events ───

#pragma warning disable CS0067
    public event Action<TrackChangeInfo>? NewTrackDetected;

    public event Action<ThumbnailUpdateInfo>? ThumbnailUpdateDetected;

    public event Action? MediaCleared;

    public event Action<MediaInfo>? BackgroundUpdateNeeded;
#pragma warning restore CS0067

    // ─── Core Logic ───

    public MediaDisplayResult ProcessMediaUpdate(MediaInfo info, bool isExpanded, bool isMusicExpanded, bool isMusicCompactMode, bool isAnimating)
    {
        var result = new MediaDisplayResult();

        // Handle thumbnail-only updates: reject if stale
        if (info.IsThumbnailOnlyUpdate)
        {
            string incomingTrackId = $"{info.CurrentTrack}|{info.CurrentArtist}";
            if (!string.IsNullOrEmpty(_lastAnimatedTrackSignature) &&
                incomingTrackId != _lastAnimatedTrackSignature)
            {
                result.Action = MediaDisplayAction.Ignore;
                return result;
            }
        }

        bool hasRealTrack = !string.IsNullOrEmpty(info.CurrentTrack);

        // ─── Source Stabilization ───
        string incomingSource = hasRealTrack ? (info.MediaSource ?? "") : "";
        string currentTrackKey = $"{info.CurrentTrack}|{info.CurrentArtist}";
        bool sameTrackAsBefore = hasRealTrack && currentTrackKey == _lastAnimatedTrackSignature;

        string renderedSource = StabilizeSource(incomingSource, sameTrackAsBefore);
        result.RenderedSource = renderedSource;
        result.SourceChanged = renderedSource != _lastRenderedMediaSource;

        // ─── Track Identity ───
        string trackIdentity = $"{info.CurrentTrack}|{info.CurrentArtist}";
        bool isNewTrack = trackIdentity != _lastAnimatedTrackSignature;

        if (isNewTrack && hasRealTrack && !string.IsNullOrEmpty(_lastAnimatedTrackSignature))
        {
            string titlePrefix = $"{info.CurrentTrack}|";
            if (_lastAnimatedTrackSignature.StartsWith(titlePrefix, StringComparison.Ordinal))
            {
                isNewTrack = false;
                _lastAnimatedTrackSignature = trackIdentity;

                if (_lastColorTrackSignature.StartsWith(titlePrefix, StringComparison.Ordinal))
                {
                    _lastColorTrackSignature = trackIdentity;
                }
            }
        }

        result.IsNewTrack = isNewTrack;
        result.TrackIdentity = trackIdentity;

        // ─── Display Text ───
        result.DisplayText = ResolveDisplayText(info, hasRealTrack, renderedSource);

        // ─── Thumbnail Decision ───
        if (hasRealTrack)
        {
            result.HasRealTrack = true;

            if (info.HasThumbnail && info.Thumbnail != null)
            {
                result.HasThumbnail = true;

                if (isNewTrack)
                {
                    bool isFirstEverTrack = string.IsNullOrEmpty(_lastAnimatedTrackSignature);
                    result.ThumbnailAction = isFirstEverTrack
                        ? ThumbnailAction.RevealFirst
                        : ThumbnailAction.AnimateSwitch;

                    _lastAnimatedTrackSignature = trackIdentity;
                    _lastAnimatedThumbnail = info.Thumbnail;
                    _thumbnailShownForCurrentTrack = true;
                    _trackChangeBounceNeeded = false;
                }
                else
                {
                    // Same track, thumbnail may have changed (async fetch)
                    result.ThumbnailAction = ResolveSameTrackThumbnailAction(info);
                }

                // YouTube can upgrade the thumbnail asynchronously for the same track.
                // Keep the expanded blur/palette keyed to artwork too, not just track id.
                if (isNewTrack || _lastColorTrackSignature != trackIdentity || !ReferenceEquals(info.Thumbnail, _lastBackgroundThumbnail))
                {
                    _lastColorTrackSignature = trackIdentity;
                    _lastBackgroundThumbnail = info.Thumbnail;
                    result.NeedsBackgroundUpdate = true;
                }
            }
            else if (isNewTrack)
            {
                // New track without thumbnail — defer bounce until thumbnail arrives
                _lastAnimatedTrackSignature = trackIdentity;
                _lastAnimatedThumbnail = null;
                _thumbnailShownForCurrentTrack = false;
                _trackChangeBounceNeeded = true;
                result.ThumbnailAction = ThumbnailAction.ShowFallback;
            }

            result.Action = MediaDisplayAction.Update;
        }
        else
        {
            // No real track
            result.HasRealTrack = false;

            if (!info.IsAnyMediaPlaying)
            {
                _lastColorTrackSignature = "";
                result.Action = MediaDisplayAction.Clear;
            }
            else
            {
                result.Action = MediaDisplayAction.Update;
            }
        }

        _lastRenderedMediaSource = renderedSource;
        return result;
    }

    public bool ShouldAnimateCompactThumbnail(MediaInfo info)
    {
        if (info?.Thumbnail == null) return false;

        string compactTrackIdentity = $"{info.CurrentTrack}|{info.CurrentArtist}";
        if (compactTrackIdentity != _lastAnimatedTrackSignature)
        {
            _lastAnimatedTrackSignature = compactTrackIdentity;
            return true;
        }
        return false;
    }

    public bool ShouldBeCompactMode(MediaInfo? info)
    {
        if (info == null) return false;
        if (!info.IsAnyMediaPlaying || string.IsNullOrEmpty(info.CurrentTrack)) return false;
        if (info.MediaSource == "Browser" && string.IsNullOrEmpty(info.CurrentTrack)) return false;
        return true;
    }

    public int IncrementGeneration() => ++_thumbnailSwitchGeneration;

    public void MarkThumbnailShown()
    {
        _thumbnailShownForCurrentTrack = true;
    }

    public void SetLastAnimatedThumbnail(ImageSource? thumb)
    {
        _lastAnimatedThumbnail = thumb;
    }

    public void Reset()
    {
        _lastAnimatedTrackSignature = "";
        _lastColorTrackSignature = "";
        _lastRenderedMediaSource = "";
        _lastAnimatedThumbnail = null;
        _lastBackgroundThumbnail = null;
        _thumbnailShownForCurrentTrack = false;
        _trackChangeBounceNeeded = false;
        _thumbnailSwitchGeneration++;
    }

    // ─── Private Helpers ───

    private string StabilizeSource(string incomingSource, bool sameTrackAsBefore)
    {
        string renderedSource = incomingSource;

        if (sameTrackAsBefore &&
            !string.IsNullOrEmpty(_lastRenderedMediaSource) &&
            _lastRenderedMediaSource != "Browser" &&
            (incomingSource == "" || incomingSource == "Browser"))
        {
            renderedSource = _lastRenderedMediaSource;
        }

        return renderedSource;
    }

    private static DisplayTextResult ResolveDisplayText(MediaInfo info, bool hasRealTrack, string renderedSource)
    {
        if (!hasRealTrack)
        {
            return new DisplayTextResult("No media playing", "Artist name");
        }

        string titleText = info.CurrentTrack;
        string artistText;

        if (!string.IsNullOrEmpty(info.CurrentArtist) &&
            info.CurrentArtist != "YouTube" &&
            info.CurrentArtist != "Browser" &&
            info.CurrentArtist != "Spotify")
        {
            artistText = info.CurrentArtist;
            // Strip YouTube Music's " - Topic" suffix from channel names
            if (artistText.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase))
            {
                artistText = artistText[..^" - Topic".Length].Trim();
            }
        }
        else if (!string.IsNullOrEmpty(renderedSource))
        {
            artistText = renderedSource;
        }
        else
        {
            artistText = "Unknown Artist";
        }

        return new DisplayTextResult(titleText, artistText);
    }

    private ThumbnailAction ResolveSameTrackThumbnailAction(MediaInfo info)
    {
        if (ReferenceEquals(info.Thumbnail, _lastAnimatedThumbnail))
            return ThumbnailAction.None;

        if (_thumbnailShownForCurrentTrack && !info.IsThumbnailOnlyUpdate)
            return ThumbnailAction.None;

        // Genuine new thumbnail for this track (async fetch completed)
        _lastAnimatedThumbnail = info.Thumbnail;
        _thumbnailShownForCurrentTrack = true;

        if (_trackChangeBounceNeeded)
        {
            _trackChangeBounceNeeded = false;
            return ThumbnailAction.AnimateSwitch;
        }

        return ThumbnailAction.AnimateUpdate;
    }
}

// ─── Result Types ───

public enum MediaDisplayAction
{
    Ignore,
    Update,
    Clear
}

public enum ThumbnailAction
{
    None,
    RevealFirst,
    AnimateSwitch,
    AnimateUpdate,
    ShowFallback
}

public record DisplayTextResult(string Title, string Artist);

public record TrackChangeInfo(string TrackIdentity, string Title, string Artist, bool IsFirstTrack);

public record ThumbnailUpdateInfo(string TrackIdentity, ImageSource Thumbnail);

public sealed class MediaDisplayResult
{
    public MediaDisplayAction Action { get; set; } = MediaDisplayAction.Ignore;
    public bool IsNewTrack { get; set; }
    public bool HasRealTrack { get; set; }
    public bool HasThumbnail { get; set; }
    public bool NeedsBackgroundUpdate { get; set; }
    public bool SourceChanged { get; set; }
    public string TrackIdentity { get; set; } = "";
    public string RenderedSource { get; set; } = "";
    public ThumbnailAction ThumbnailAction { get; set; } = ThumbnailAction.None;
    public DisplayTextResult DisplayText { get; set; } = new("", "");
}
