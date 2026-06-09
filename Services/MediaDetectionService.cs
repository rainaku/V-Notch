using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using System.Threading.Channels;
using System.Globalization;
using Windows.Media.Control;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using NAudio.CoreAudioApi;
using VNotch.Models;

namespace VNotch.Services;

public class MediaDetectionService : IMediaDetectionService
{
    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private bool _disposed;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private readonly IMediaMetadataLookupService _metadataLookup;
    private readonly IMediaArtworkService _artworkService;
    private readonly IWindowTitleScanner _windowTitleScanner;
    private readonly MediaSessionVolumeService _volumeService;
    private readonly MediaTransportControlService _transportService;

    private readonly Channel<ChangeType> _changeChannel;
    private CancellationTokenSource? _bgCts;
    private Task? _processingTask;
    private Task? _heartbeatTask;
    private DetectionMode _currentMode = DetectionMode.Idle;
    private long _lastEventTimeTicks;
    private DateTime _startupProgressSyncUntilUtc = DateTime.MinValue;

    public event EventHandler<MediaInfo>? MediaChanged;

    private string _lastTrackSignature = "";
    private string _lastThumbTrackIdentity = "";
    private string _cachedSource = "";
    private BitmapImage? _cachedThumbnail;
    private string _cachedThumbnailSource = "";

    private string _pendingSessionAppId = "";
    private DateTime _pendingSessionStartTime = DateTime.MinValue;
    private string _pendingNewTrackKey = "";
    private DateTime _pendingNewTrackSince = DateTime.MinValue;

    private CancellationTokenSource? _thumbCts;
    private string _lastStableTrackSignature = "";
    private DateTime _emptyMetadataStartTime = DateTime.MinValue;
    private readonly MediaSessionState _sessionState = new();
    private readonly MediaSourceCache _sourceCache;
    private string _latestPlayingSessionKey = "";
    private DateTime _latestPlayingSessionStartUtc = DateTime.MinValue;

    private DateTime _lastMetadataChangeTime = DateTime.MinValue;
    private string _lastTrackName = "";
    private readonly MediaTimelineSimulator _timelineSimulator = new();
    private string _lastSoundCloudArtworkIdentity = "";
    private DateTime _lastSoundCloudArtworkAttemptTimeUtc = DateTime.MinValue;
    private string _soundCloudFetchIdentity = "";
    private int _soundCloudFetchGeneration = 0;
    private int _soundCloudFetchInFlight = 0;
    private int _thumbnailFetchGeneration = 0;
    private static readonly TimeSpan SoundCloudArtworkRetryInterval = TimeSpan.FromSeconds(1.1);

    // ─── Published-state snapshot (set by CommitPublishedState) ───
    private string _lastSource = "";
    private bool _lastIsPlaying = false;
    private bool _lastIsThrottled = false;
    private bool _lastSeekEnabled = false;
    private TimeSpan _lastPosition = TimeSpan.Zero;
    private string _lastPublishedSignature = "";
    private string _lastPublishedTrackIdentity = "";
    private string _lastPublishedTrackOnlyIdentity = "";
    private string _lastPublishedSourceAppId = "";
    private string _lastPublishedSessionInstanceKey = "";

    // ─── Source/artist stabilization state ───
    private DateTime _lastSourceConfirmedTime = DateTime.MinValue;
    private string _stableSource = "";
    private string _stableArtist = "";
    private string _stableSourceTrackIdentity = "";

    public MediaDetectionService(
        IMediaMetadataLookupService metadataLookup,
        IMediaArtworkService artworkService,
        IWindowTitleScanner windowTitleScanner)
    {
        _metadataLookup = metadataLookup;
        _artworkService = artworkService;
        _windowTitleScanner = windowTitleScanner;
        _volumeService = new MediaSessionVolumeService();
        _transportService = new MediaTransportControlService(GetActiveSession);

        _changeChannel = Channel.CreateBounded<ChangeType>(
            new BoundedChannelOptions(16)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true
            });

        _sourceCache = new MediaSourceCache();
        _sourceCache.Load();
    }
    public IMediaArtworkService ArtworkService => _artworkService;

    private GlobalSystemMediaTransportControlsSession? GetActiveSession()
    {
        // Priority: use the session that matches what's currently displayed to the user
        if (_activeDisplaySession != null)
            return _activeDisplaySession;

        // Fallback: if _activeDisplaySession hasn't been set yet (e
        if (!string.IsNullOrEmpty(_lastPublishedSourceAppId))
        {
            var match = FindSessionBySourceAppId(_lastPublishedSourceAppId);
            if (match != null)
                return match;
        }

        return _currentSession ?? _sessionManager?.GetCurrentSession();
    }

    private GlobalSystemMediaTransportControlsSession? FindSessionBySourceAppId(string sourceAppId)
    {
        try
        {
            var sessions = _sessionManager?.GetSessions();
            if (sessions == null) return null;

            foreach (var s in sessions)
            {
                var id = s.SourceAppUserModelId ?? "";
                if (string.Equals(id, sourceAppId, StringComparison.OrdinalIgnoreCase))
                {
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("MEDIA-FIND-SESSION", $"Failed to find session for {sourceAppId}: {ex.Message}");
        }
        return null;
    }

    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private GlobalSystemMediaTransportControlsSession? _activeDisplaySession; 
    private DateTime _lastSessionSwitchTime = DateTime.MinValue;

    public void Start()
    {
        if (_disposed) return;
        if (_sessionManager != null) return; // already started

        _startupProgressSyncUntilUtc = DateTime.UtcNow.AddSeconds(5);

        StartCoreAsync().SafeFireAndForget("MEDIA-START");
    }

    private async Task StartCoreAsync()
    {
        try
        {
            _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _sessionManager.CurrentSessionChanged += OnSessionChanged;
            _sessionManager.SessionsChanged += OnSessionsChanged;

            await SubscribeToCurrentSession();
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("MEDIA-INIT", $"Failed to init SMTC: {ex.Message}");
        }

        _bgCts = new CancellationTokenSource();
        var ct = _bgCts.Token;
        _processingTask = Task.Run(() => ProcessingLoopAsync(ct), ct);
        _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(ct), ct);

        _changeChannel.Writer.TryWrite(ChangeType.ForceRefresh);
        _ = Task.Run(async () =>
        {
            int[] stagedDelaysMs = { 120, 350, 800 };
            foreach (int delayMs in stagedDelaysMs)
            {
                try
                {
                    await Task.Delay(delayMs, ct);
                }
                catch
                {
                    return;
                }

                _changeChannel.Writer.TryWrite(ChangeType.ForceRefresh);
            }
        }, ct);
    }

    public void Stop()
    {
        if (_disposed) return;
        UnsubscribeFromSession();
        _bgCts?.Cancel();
    }

    private async Task SubscribeToCurrentSession()
    {
        UnsubscribeFromSession();

        if (_sessionManager == null) return;

        try
        {
            _currentSession = _sessionManager.GetCurrentSession();
            if (_currentSession != null)
            {
                _currentSession.TimelinePropertiesChanged += OnTimelineChanged;
                _currentSession.PlaybackInfoChanged += OnPlaybackChanged;
                _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
                RuntimeLog.Debug("MEDIA-SESSION", () => $"Subscribed to session: {_currentSession.SourceAppUserModelId}");
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("MEDIA-SESSION", $"Failed to subscribe to session: {ex.Message}");
        }
    }

    private void UnsubscribeFromSession()
    {
        if (_currentSession != null)
        {
            try
            {
                _currentSession.TimelinePropertiesChanged -= OnTimelineChanged;
                _currentSession.PlaybackInfoChanged -= OnPlaybackChanged;
                _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("MEDIA-UNSUBSCRIBE", ex.ToString());
            }
            _currentSession = null;
        }
    }

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        Interlocked.Exchange(ref _lastEventTimeTicks, DateTime.UtcNow.Ticks);
        _changeChannel.Writer.TryWrite(ChangeType.Timeline);
    }

    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        Interlocked.Exchange(ref _lastEventTimeTicks, DateTime.UtcNow.Ticks);
        _changeChannel.Writer.TryWrite(ChangeType.Playback);
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        Interlocked.Exchange(ref _lastEventTimeTicks, DateTime.UtcNow.Ticks);
        _changeChannel.Writer.TryWrite(ChangeType.MediaProperties);
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        Log("Session Changed", "System-wide session focus shifted");
        _changeChannel.Writer.TryWrite(ChangeType.SessionChanged);
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        Log("Sessions Changed", "Session list changed (app opened/closed)");
        _changeChannel.Writer.TryWrite(ChangeType.SessionChanged);
    }

    private void Log(string tag, string message)
    {
        RuntimeLog.Log($"MEDIA-{tag}", message);
    }

    #region Background Processing

    private async Task ProcessingLoopAsync(CancellationToken ct)
    {
        await foreach (var change in _changeChannel.Reader.ReadAllAsync(ct))
        {
            try
            {
                
                await Task.Delay(50, ct);
                var types = change;
                while (_changeChannel.Reader.TryRead(out var extra))
                {
                    types |= extra;
                }

                bool forceRefresh = types.HasFlag(ChangeType.MediaProperties)
                    || types.HasFlag(ChangeType.Playback)
                    || types.HasFlag(ChangeType.SessionChanged)
                    || types.HasFlag(ChangeType.ForceRefresh);

                if (types.HasFlag(ChangeType.SessionChanged))
                {
                    await SubscribeToCurrentSession();
                }

                await UpdateMediaInfoAsync(forceRefresh);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log("ProcessError", ex.Message);
            }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var interval = _currentMode switch
            {
                DetectionMode.Idle => TimeSpan.FromSeconds(5),
                DetectionMode.EventDriven => TimeSpan.FromSeconds(3),
                DetectionMode.ThrottledMedia => TimeSpan.FromMilliseconds(1500),
                _ => TimeSpan.FromSeconds(3)
            };

            try
            {
                await Task.Delay(interval, ct);
            }
            catch (OperationCanceledException) { break; }

            var lastEvtTicks = Interlocked.Read(ref _lastEventTimeTicks);
            if (_currentMode == DetectionMode.Idle
                && lastEvtTicks > 0
                && (DateTime.UtcNow - new DateTime(lastEvtTicks)).TotalSeconds > 10)
            {
                continue;
            }

            _changeChannel.Writer.TryWrite(ChangeType.Heartbeat);
        }
    }

    private void UpdateDetectionMode(MediaInfo info)
    {
        var oldMode = _currentMode;
        
        if (!info.IsAnyMediaPlaying || string.IsNullOrEmpty(info.CurrentTrack))
        {
            _currentMode = DetectionMode.Idle;
        }
        else if (info.IsThrottled)
        {
            _currentMode = DetectionMode.ThrottledMedia;
        }
        else
        {
            _currentMode = DetectionMode.EventDriven;
        }
        
        if (oldMode != _currentMode)
        {
            RuntimeLog.Debug("MEDIA-MODE", () => $"Detection mode: {oldMode} -> {_currentMode} (Playing={info.IsAnyMediaPlaying}, Track='{info.CurrentTrack}', Throttled={info.IsThrottled})");
        }
    }

    #endregion

    private async Task UpdateMediaInfoAsync(bool forceRefresh = false)
    {

        if (!await _updateLock.WaitAsync(forceRefresh ? 500 : 0)) return;

        try
        {
            var info = new MediaInfo();
            List<string>? windowTitles = null;

            await TryGetMediaSessionInfoAsync(info, forceRefresh, () =>
            {
                windowTitles ??= GetAllWindowTitles();
                return windowTitles;
            });

            ApplyWindowTitleFallback(info, ref windowTitles);
            ApplyVideoTimelineRecovery(info, ref windowTitles);

            TrackNameChangeBookkeeping(info);
            PreserveSoundCloudSourceIfNeeded(info, ref windowTitles);

            info.IsThrottled = _timelineSimulator.IsThrottled;
            var currentSignature = info.GetSignature();

            if (UpdateEmptyMetadataHold(info, currentSignature))
                return;

            bool isNewTrackForThumbnail = ComputeIsNewTrackForThumbnail(info);

            if (!await TryPublishMediaChangeAsync(info, currentSignature, isNewTrackForThumbnail, forceRefresh))
                return;

            StartThumbnailFetchIfNeeded(info, isNewTrackForThumbnail);
        }
        finally
        {
            _updateLock.Release();
        }
    }

    /// <summary>Resets per-track stable state whenever the SMTC track name changes.</summary>
    private void TrackNameChangeBookkeeping(MediaInfo info)
    {
        if (info.CurrentTrack == _lastTrackName) return;

        _lastTrackName = info.CurrentTrack;
        _lastMetadataChangeTime = DateTime.Now;

        if (!string.IsNullOrEmpty(info.CurrentTrack))
        {
            _stableArtist = "";
        }

        if (_timelineSimulator.IsThrottled) _timelineSimulator.Reset();
    }

    /// <summary>
    /// Keeps the SoundCloud source sticky during a track switch when window titles confirm a
    /// SoundCloud tab and rule out YouTube.
    /// </summary>
    private void PreserveSoundCloudSourceIfNeeded(MediaInfo info, ref List<string>? windowTitles)
    {
        if (!ShouldPreserveSoundCloudSourceDuringTrackSwitch(info)) return;

        windowTitles ??= GetAllWindowTitles();
        bool hasYouTubeWindowHint = MediaPlatformExtensions.ParsePlatform(DetectPlatformHint(windowTitles)) == MediaPlatform.YouTube;
        bool hasSoundCloudWindowMatch = HasReliablePlatformWindowMatch(windowTitles, info.CurrentTrack, "soundcloud");
        if (!hasYouTubeWindowHint && hasSoundCloudWindowMatch)
        {
            info.MediaSource = MediaPlatform.SoundCloud.ToDisplayString();
            info.IsSoundCloudRunning = true;
            SetSessionSourceOverride(info, MediaPlatform.SoundCloud.ToDisplayString());
        }
    }

    /// <summary>
    /// Maintains the empty-metadata grace window. Returns true when the current (empty) pass
    /// should be deferred to avoid flickering during brief metadata gaps.
    /// </summary>
    private bool UpdateEmptyMetadataHold(MediaInfo info, string currentSignature)
    {
        var r = MediaTimingDecisions.EvaluateEmptyMetadataHold(
            info.CurrentTrack, info.IsAnyMediaPlaying, currentSignature, _lastSource,
            _emptyMetadataStartTime, _lastStableTrackSignature, DateTime.Now);
        _emptyMetadataStartTime = r.emptyStart;
        _lastStableTrackSignature = r.stableSignature;
        return r.hold;
    }

    /// <summary>True when the track (ignoring artist) differs from the last published track.</summary>
    private bool ComputeIsNewTrackForThumbnail(MediaInfo info)
        => PublishedTrackMatcher.IsNewTrackForThumbnail(_lastPublishedTrackOnlyIdentity, info.CurrentTrack);

    /// <summary>
    /// Evaluates change-detection flags and, when something meaningful changed, commits the new
    /// state and fires <see cref="MediaChanged"/>. Returns false when the caller should return
    /// early (i.e. skip the thumbnail fetch) due to an empty-track hold or new-track debounce.
    /// </summary>
    private async Task<bool> TryPublishMediaChangeAsync(MediaInfo info, string currentSignature, bool isNewTrackForThumbnail, bool forceRefresh)
    {
        bool metadataChanged = currentSignature != _lastPublishedSignature;
        bool playbackChanged = info.IsPlaying != _lastIsPlaying;
        bool sourceChanged = info.MediaSource != _lastSource;
        bool seekCapabilityChanged = info.IsSeekEnabled != _lastSeekEnabled;
        bool significantJump = Math.Abs((info.Position - _lastPosition).TotalSeconds) >= (info.IsThrottled ? 5.0 : 1.5);
        bool throttleChanged = info.IsThrottled != _lastIsThrottled;
        bool inStartupSyncWindow = DateTime.UtcNow <= _startupProgressSyncUntilUtc;
        bool startupTimelineSync = inStartupSyncWindow &&
                                   info.IsPlaying &&
                                   (info.Duration.TotalSeconds > 0 || info.Position.TotalSeconds > 0) &&
                                   Math.Abs((info.Position - _lastPosition).TotalSeconds) >= 0.2;

        if (!(forceRefresh || metadataChanged || playbackChanged || sourceChanged || (significantJump && !info.IsThrottled) || seekCapabilityChanged || throttleChanged || startupTimelineSync))
        {
            return true; // nothing meaningful changed; continue to thumbnail fetch
        }

        bool shouldHoldEmptyTrack = string.IsNullOrEmpty(info.CurrentTrack) && info.IsAnyMediaPlaying;
        if (shouldHoldEmptyTrack && !string.IsNullOrEmpty(_lastPublishedSignature) && !forceRefresh)
        {
            return false;
        }

        if (ShouldDebounceNewTrack(info, forceRefresh))
        {
            return false;
        }

        CommitPublishedState(info, currentSignature);

        UpdateDetectionMode(info);

        SuppressIntermediateYouTubeThumbnail(info, isNewTrackForThumbnail);

        await FireMediaChangedAsync(info);

        if (!forceRefresh && metadataChanged && !string.IsNullOrEmpty(info.CurrentTrack))
        {
            _changeChannel.Writer.TryWrite(ChangeType.ForceRefresh);
        }

        return true;
    }

    /// <summary>
    /// Debounces a not-yet-playing new track for 600ms so a paused scrub doesn't publish
    /// prematurely. Returns true when publishing should be deferred this pass.
    /// </summary>
    private bool ShouldDebounceNewTrack(MediaInfo info, bool forceRefresh)
    {
        var r = MediaTimingDecisions.EvaluateNewTrackDebounce(
            info.CurrentTrack, info.CurrentArtist, info.IsPlaying, forceRefresh,
            _lastPublishedTrackIdentity, _pendingNewTrackKey, _pendingNewTrackSince, DateTime.UtcNow);
        _pendingNewTrackKey = r.pendingKey;
        _pendingNewTrackSince = r.pendingSince;
        return r.debounce;
    }

    /// <summary>Snapshots the just-published media state into the "last published" tracking fields.</summary>
    private void CommitPublishedState(MediaInfo info, string currentSignature)
    {
        _lastPublishedSignature = currentSignature;
        _lastTrackSignature = currentSignature;
        _lastIsPlaying = info.IsPlaying;
        _lastSource = info.MediaSource;
        _lastPosition = info.Position;
        _lastSeekEnabled = info.IsSeekEnabled;
        _lastIsThrottled = info.IsThrottled;
        _lastPublishedTrackIdentity = BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist);
        _lastPublishedTrackOnlyIdentity = BuildTrackIdentity(info.CurrentTrack, "");
        _lastPublishedSourceAppId = info.SourceAppId ?? "";
        _lastPublishedSessionInstanceKey = info.SessionInstanceKey ?? "";
    }

    /// <summary>
    /// For YouTube/Browser sources about to fetch a better thumbnail, suppresses intermediate
    /// or stale wide thumbnails to avoid flashing 2-3 different images on a publish.
    /// </summary>
    private void SuppressIntermediateYouTubeThumbnail(MediaInfo info, bool isNewTrackForThumbnail)
    {
        // For YouTube sources where we're about to fetch a better thumbnail, suppress ALL intermediate thumbnails to avoid showing 2-3 different images
        bool willFetchYouTubeThumbnail = (info.Platform == MediaPlatform.YouTube || (info.Platform == MediaPlatform.Browser && IsLikelyYouTube(info)))
            && !string.IsNullOrEmpty(info.CurrentTrack)
            && !isNewTrackForThumbnail
            && (info.Thumbnail == null || info.Thumbnail.PixelWidth < 120)
            && (_cachedThumbnail == null || _cachedThumbnail.PixelWidth < 200);

        // Don't suppress SMTC thumbnail if it's already high-quality square artwork (album art)
        bool hasGoodSmtcArtwork = false;
        if (willFetchYouTubeThumbnail && info.Thumbnail != null && info.Thumbnail.PixelWidth >= 200)
        {
            double smtcAspect = (double)info.Thumbnail.PixelWidth / info.Thumbnail.PixelHeight;
            hasGoodSmtcArtwork = smtcAspect >= 0.85 && smtcAspect <= 1.15;
        }

        if (willFetchYouTubeThumbnail && !hasGoodSmtcArtwork)
        {
            // Suppress any intermediate thumbnail until YouTube fetch completes
            if (isNewTrackForThumbnail)
            {
                info.Thumbnail = null;
            }
            else if (_cachedThumbnail != null && _cachedThumbnail.PixelWidth >= 200)
            {
                info.Thumbnail = _cachedThumbnail;
            }
            else
            {
                info.Thumbnail = _cachedThumbnail;
            }
        }

        // On new track for YouTube/Browser: suppress stale wide SMTC thumbnails (from old tab)
        if (isNewTrackForThumbnail &&
            (info.Platform == MediaPlatform.YouTube || (info.Platform == MediaPlatform.Browser && IsLikelyYouTube(info))) &&
            info.Thumbnail != null)
        {
            double thumbAspect = (double)info.Thumbnail.PixelWidth / info.Thumbnail.PixelHeight;
            if (thumbAspect > 1.3)
            {
                RuntimeLog.Debug("MEDIA-THUMB-STALE", () =>
                    $"Suppressing stale wide SMTC thumb on new track publish: " +
                    $"track='{info.CurrentTrack}' thumb={info.Thumbnail.PixelWidth}x{info.Thumbnail.PixelHeight} aspect={thumbAspect:F2}");
                info.Thumbnail = null;
            }
        }
    }

    /// <summary>Raises <see cref="MediaChanged"/> on the UI dispatcher.</summary>
    private async Task FireMediaChangedAsync(MediaInfo info)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null)
        {
            RuntimeLog.Debug("MEDIA-EVENT", () =>
                $"Firing MediaChanged: Source={info.MediaSource}, App={info.SourceAppId}, Track='{info.CurrentTrack}', Artist='{info.CurrentArtist}', " +
                $"Pos={info.Position.TotalSeconds:F3}s, Dur={info.Duration.TotalSeconds:F3}s, IsPlaying={info.IsPlaying}, " +
                $"Rate={info.PlaybackRate:F3}, LastUpdated={info.LastUpdated:O}");
            await dispatcher.InvokeAsync(() => MediaChanged?.Invoke(this, info));
        }
        else
        {
            RuntimeLog.Warn("MEDIA-EVENT", "No dispatcher, cannot fire MediaChanged event");
        }
    }


    private void ApplyWindowTitleFallback(MediaInfo info, ref List<string>? windowTitles)
    {
            bool needsFallback = !info.IsAnyMediaPlaying || (string.IsNullOrEmpty(info.CurrentTrack) && info.Platform == MediaPlatform.Browser) || info.Platform == MediaPlatform.Browser || string.IsNullOrEmpty(info.MediaSource);

            // Also scan window titles if we recently had a YouTube/Browser source but SMTC
            // session was lost (e.g. page refresh). This recovers the title from the browser
            // window while SMTC is being re-established.
            bool recentlyHadMedia = !info.IsAnyMediaPlaying &&
                                    (MediaPlatformExtensions.ParsePlatform(_lastSource) == MediaPlatform.YouTube || MediaPlatformExtensions.ParsePlatform(_lastSource) == MediaPlatform.Browser || MediaPlatformExtensions.ParsePlatform(_lastSource) == MediaPlatform.SoundCloud) &&
                                    _emptyMetadataStartTime != DateTime.MinValue &&
                                    (DateTime.Now - _emptyMetadataStartTime).TotalSeconds < 6.0;

            if (needsFallback && (info.IsAnyMediaPlaying || recentlyHadMedia))
            {
                windowTitles ??= GetAllWindowTitles();

                if (info.Platform != MediaPlatform.Spotify)
                {
                    var spotifyProcesses = Process.GetProcessesByName("Spotify");
                    if (spotifyProcesses.Length > 0)
                    {
                        foreach (var proc in spotifyProcesses)
                        {
                            string wTitle = proc.MainWindowTitle;
                            if (!string.IsNullOrEmpty(wTitle) && wTitle != "Spotify" && !wTitle.ToLower().EndsWith("spotify"))
                            {
                                info.IsSpotifyRunning = true;
                                info.IsSpotifyPlaying = true;
                                info.IsAnyMediaPlaying = true;
                                info.IsPlaying = true;
                                info.MediaSource = MediaPlatform.Spotify.ToDisplayString();
                                ParseSpotifyTitle(wTitle, info);
                                if (!string.IsNullOrEmpty(info.CurrentArtist)) _stableArtist = info.CurrentArtist;
                                break;
                            }
                        }
                    }
                }

                if (string.IsNullOrEmpty(info.CurrentTrack))
                {
                    foreach (var title in windowTitles)
                    {
                        var lowerTitle = title.ToLower();
                        
                        // YouTube detection
                        if (lowerTitle.Contains("youtube") && !lowerTitle.StartsWith("youtube -") && lowerTitle != "youtube")
                        {
                            if (!info.IsSpotifyPlaying && string.IsNullOrEmpty(info.MediaSource))
                            {
                                info.IsAnyMediaPlaying = true;
                                info.MediaSource = MediaPlatform.YouTube.ToDisplayString();
                                info.YouTubeTitle = ExtractVideoTitle(title, "YouTube");
                                info.CurrentTrack = info.YouTubeTitle;

                                info.CurrentArtist = !string.IsNullOrEmpty(_stableArtist) && (DateTime.Now - _lastSourceConfirmedTime).TotalSeconds < 15.0 ? _stableArtist : "YouTube";
                                break;
                            }
                        }
                        
                        // SoundCloud detection
                        if (lowerTitle.Contains("soundcloud") && !lowerTitle.StartsWith("soundcloud -") && lowerTitle != "soundcloud")
                        {
                            if (!info.IsSpotifyPlaying && string.IsNullOrEmpty(info.MediaSource))
                            {
                                info.IsAnyMediaPlaying = true;
                                info.MediaSource = MediaPlatform.SoundCloud.ToDisplayString();
                                info.IsSoundCloudRunning = true;

                                // Extract track info from window title
                                // Format can be either "Artist - Track" or "Track - Artist"
                                string extractedTitle = ExtractVideoTitle(title, "SoundCloud");
                                if (extractedTitle.Contains(" - "))
                                {
                                    // Use last " - " to split: artist names can contain " - "
                                    int lastSep = extractedTitle.LastIndexOf(" - ", StringComparison.Ordinal);
                                    string firstPart = extractedTitle.Substring(0, lastSep).Trim();
                                    string secondPart = extractedTitle.Substring(lastSep + 3).Trim();

                                    // ─── Smart format detection ───
                                    // Heuristic: If first part is very short (1-2 words) and second part is longer,
                                    // it's likely "Track - Artist" format
                                    int firstWordCount = firstPart.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
                                    int secondWordCount = secondPart.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;

                                    // If first part is short (≤3 words) and second part has "by" or is much longer,
                                    // assume "Track - Artist" format
                                    bool likelyTrackFirst = (firstWordCount <= 3 && secondWordCount > firstWordCount) ||
                                                           secondPart.StartsWith("by ", StringComparison.OrdinalIgnoreCase) ||
                                                           secondPart.Contains(" by ", StringComparison.OrdinalIgnoreCase);

                                    if (likelyTrackFirst)
                                    {
                                        // "Track - Artist" format
                                        info.CurrentTrack = firstPart;
                                        info.CurrentArtist = secondPart;
                                    }
                                    else
                                    {
                                        // "Artist - Track" format (traditional)
                                        info.CurrentArtist = firstPart;
                                        info.CurrentTrack = secondPart;
                                    }
                                }
                                else
                                {
                                    info.CurrentTrack = extractedTitle;
                                    info.CurrentArtist = "SoundCloud";
                                }

                                // Mark for thumbnail fetch
                                SetSessionSourceOverride(info, MediaPlatform.SoundCloud.ToDisplayString());

                                break;
                            }
                        }
                    }
                }
            }
    }

    private void ApplyVideoTimelineRecovery(MediaInfo info, ref List<string>? windowTitles)
    {
            bool isVideoSource = info.Platform == MediaPlatform.YouTube ||
                                 (info.Platform == MediaPlatform.Browser && IsLikelyYouTube(info));
            if (isVideoSource && info.IsPlaying)
            {

                _timelineSimulator.UpdateObservedPosition(info.Position);

                double progress = info.Duration.TotalSeconds > 0 ? info.Position.TotalSeconds / info.Duration.TotalSeconds : 0;
                bool positionStuck = _timelineSimulator.IsPositionStuck(TimeSpan.FromSeconds(1.5));
                bool atEndStuck = _timelineSimulator.IsAtEndStuck(progress, _lastMetadataChangeTime, TimeSpan.FromSeconds(1.2));

                // When simulated position reaches or exceeds duration while still "playing", the track has likely ended and YouTube auto-played the next one
                if (progress >= 1.0 && info.Duration.TotalSeconds > 0 && _timelineSimulator.IsThrottled)
                {
                    _timelineSimulator.Reset();
                    _changeChannel.Writer.TryWrite(ChangeType.ForceRefresh);
                }

                if (positionStuck || atEndStuck)
                {
                    bool foundRecovery = false;

                    if (!string.IsNullOrEmpty(info.CurrentTrack) && IsLikelyYouTube(info))
                    {

                        info.MediaSource = MediaPlatform.YouTube.ToDisplayString();
                        info.IsYouTubeRunning = true;

                        _timelineSimulator.ApplySimulatedTimeline(info, atEndStuck);
                        foundRecovery = true;
                    }

                    if (!foundRecovery)
                    {

                        windowTitles ??= GetAllWindowTitles();

                        foreach (var title in windowTitles)
                        {
                            var lowerWinTitle = title.ToLower();
                            if (lowerWinTitle.Contains("youtube") && !lowerWinTitle.StartsWith("youtube -") && lowerWinTitle != "youtube")
                            {
                                var extractedTitle = ExtractVideoTitle(title, "YouTube");
                                string trackName = extractedTitle;
                                string artistName = "YouTube";

                                if (extractedTitle.Contains(" - "))
                                {
                                    int lastSep = extractedTitle.LastIndexOf(" - ", StringComparison.Ordinal);
                                    string firstPart = extractedTitle.Substring(0, lastSep).Trim();
                                    string secondPart = extractedTitle.Substring(lastSep + 3).Trim();

                                    // ─── Smart format detection ───
                                    // Heuristic: Detect "Track - Artist" vs "Artist - Track" format
                                    int firstWordCount = firstPart.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
                                    int secondWordCount = secondPart.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;

                                    // If first part is short (≤3 words) and second part has "by" or is much longer,
                                    // assume "Track - Artist" format
                                    bool likelyTrackFirst = (firstWordCount <= 3 && secondWordCount > firstWordCount) ||
                                                           secondPart.StartsWith("by ", StringComparison.OrdinalIgnoreCase) ||
                                                           secondPart.Contains(" by ", StringComparison.OrdinalIgnoreCase);

                                    if (likelyTrackFirst)
                                    {
                                        // "Track - Artist" format
                                        trackName = firstPart;
                                        artistName = secondPart;
                                    }
                                    else
                                    {
                                        // "Artist - Track" format (traditional)
                                        trackName = secondPart;
                                        artistName = firstPart;
                                    }
                                }

                                bool isNewTrack = trackName != _lastTrackName && !_lastTrackName.Contains(trackName);

                                if (isNewTrack)
                                {

                                    info.CurrentTrack = trackName;
                                    info.CurrentArtist = artistName;
                                    info.MediaSource = MediaPlatform.YouTube.ToDisplayString();
                                    info.IsYouTubeRunning = true;
                                    info.IsThrottled = true;
                                    _timelineSimulator.EnterThrottledMode();

                                    _timelineSimulator.ResetRecoveredData();
                                    info.Duration = TimeSpan.Zero;

                                    info.Position = TimeSpan.FromSeconds(1.5);
                                    info.LastUpdated = DateTimeOffset.Now;
                                    foundRecovery = true;
                                    break;
                                }
                                else if (positionStuck || _timelineSimulator.IsThrottled)
                                {

                                    info.CurrentTrack = trackName;
                                    info.CurrentArtist = artistName;
                                    info.MediaSource = MediaPlatform.YouTube.ToDisplayString();
                                    info.IsYouTubeRunning = true;
                                    info.IsThrottled = true;
                                    _timelineSimulator.EnterThrottledMode();

                                    if (_timelineSimulator.RecoveredDuration.TotalSeconds > 0)
                                    {
                                        info.Duration = _timelineSimulator.RecoveredDuration;
                                    }
                                    else
                                    {

                                        info.Duration = TimeSpan.Zero;
                                    }

                                    if (_timelineSimulator.RecoveredThumbnail != null) info.Thumbnail = _timelineSimulator.RecoveredThumbnail;

                                    var timeOnTrack = DateTime.Now - _lastMetadataChangeTime;
                                    info.Position = timeOnTrack;
                                    info.LastUpdated = DateTimeOffset.Now;
                                    foundRecovery = true;
                                    break;
                                }
                            }
                        }
                    }

                    if (!foundRecovery && _timelineSimulator.IsThrottled)
                    {
                        _timelineSimulator.TryExitThrottleIfStalled(TimeSpan.FromSeconds(3.5));
                    }
                }
                else if (_timelineSimulator.IsThrottled)
                {
                    _timelineSimulator.TryExitThrottleIfPositionResumed(TimeSpan.FromMilliseconds(500));
                }
            }
            else
            {
                _timelineSimulator.Reset();
            }
    }

    private void StartThumbnailFetchIfNeeded(MediaInfo info, bool isNewTrackForThumbnail)
    {
            bool hasSoundCloudSessionOverride = !string.IsNullOrEmpty(info.SourceAppId) &&
                                                TryGetSessionSourceOverride(info, out var sourceOverride) &&
                                                MediaPlatformExtensions.ParsePlatform(sourceOverride) == MediaPlatform.SoundCloud;

            // Hard gate against SoundCloud probing: if any visible browser tab currently shows a YouTube URL, the SMTC "Browser" source is categorically YouTube
            bool browserHasYouTubeTabOpen = false;
            if (info.Platform == MediaPlatform.Browser && !hasSoundCloudSessionOverride)
            {
                try
                {
                    var browserUrl = _windowTitleScanner.TryGetMediaUrlFromAnyBrowser();
                    if (!string.IsNullOrEmpty(browserUrl) &&
                        (browserUrl!.Contains("youtube.com/", StringComparison.OrdinalIgnoreCase) ||
                         browserUrl.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase)))
                    {
                        browserHasYouTubeTabOpen = true;
                    }
                }
                catch { /* best effort */ }
            }

            var sourcePlan = ThumbnailFetchPlanner.ClassifySources(new ThumbnailSourceInputs
            {
                PlatformIsYouTube = info.Platform == MediaPlatform.YouTube,
                PlatformIsBrowser = info.Platform == MediaPlatform.Browser,
                PlatformIsSoundCloud = info.Platform == MediaPlatform.SoundCloud,
                HasSourceApp = !string.IsNullOrEmpty(info.SourceAppId),
                IsLikelyYouTube = IsLikelyYouTube(info),
                HasSoundCloudSessionOverride = hasSoundCloudSessionOverride,
                BrowserHasYouTubeTabOpen = browserHasYouTubeTabOpen,
                HasTrack = !string.IsNullOrEmpty(info.CurrentTrack),
                ThumbnailIsNullOrPlaceholder = info.Thumbnail == null || IsLikelySoundCloudPlaceholderThumbnail(info.Thumbnail),
            });
            bool isPotentialYouTube = sourcePlan.IsPotentialYouTube;
            bool isPotentialSoundCloud = sourcePlan.IsPotentialSoundCloud;

            string soundCloudTrackIdentity = isPotentialSoundCloud
                ? BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist)
                : "";

            bool forceFetchForTrackChange = isNewTrackForThumbnail;
            bool needsFetch = forceFetchForTrackChange || (info.Thumbnail == null || info.Thumbnail.PixelWidth < 120);
            if (_timelineSimulator.IsThrottled && _timelineSimulator.RecoveredThumbnail != null && !forceFetchForTrackChange) needsFetch = false;

            // ── Track-change invalidation ──────────────────────────────────── Before kicking off a YouTube fetch for a NEW track, drop any cached browser URLs and any per-track videoId entry that does not belong to the new track
            if (forceFetchForTrackChange)
            {
                _windowTitleScanner.InvalidateUrlCaches();
                ClearMismatchCache();
                ForgetVideoIdCacheExceptForTrack(BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist));
            }

            // If we already have a good cached thumbnail for the SAME track, don't re-fetch
            if (needsFetch && !forceFetchForTrackChange && _cachedThumbnail != null && _cachedThumbnail.PixelWidth >= 200)
            {
                needsFetch = false;
                // Use the cached thumbnail instead of the SMTC one
                info.Thumbnail = _cachedThumbnail;
                _timelineSimulator.RecoveredThumbnail = _cachedThumbnail;
            }

            // Skip YouTube thumbnail fetch when SMTC already provides high-quality artwork
            if (needsFetch && info.Thumbnail != null && info.Thumbnail.PixelWidth >= 200)
            {
                double thumbAspect = (double)info.Thumbnail.PixelWidth / info.Thumbnail.PixelHeight;
                bool isNearSquare = thumbAspect >= 0.85 && thumbAspect <= 1.15;
                bool isTopicChannel = !string.IsNullOrEmpty(info.CurrentArtist) &&
                                      info.CurrentArtist.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase);

                // If SMTC provides a near-square thumbnail (album art), keep it
                if (isNearSquare || isTopicChannel)
                {
                    needsFetch = false;
                    // Topic channels provide album art — always center crop. Near-square images also center crop (already album art).
                    RuntimeLog.Debug("MEDIA-THUMB-CROP", () =>
                        $"path=initial-smtc track='{info.CurrentTrack}' artist='{info.CurrentArtist}' source='{info.MediaSource}' " +
                        $"thumb={info.Thumbnail.PixelWidth}x{info.Thumbnail.PixelHeight} aspect={thumbAspect:F2} " +
                        $"isNearSquare={isNearSquare} isTopicChannel={isTopicChannel}");
                    var cropped = CropToSquare(info.Thumbnail, info.MediaSource ?? "YouTube", forceCenterCrop: isTopicChannel) ?? info.Thumbnail;
                    info.Thumbnail = cropped;
                    _cachedThumbnail = cropped;
                    _cachedThumbnailSource = MediaPlatform.YouTube.ToDisplayString();
                    _timelineSimulator.RecoveredThumbnail = cropped;
                }
            }

            if (isPotentialYouTube && !string.IsNullOrEmpty(info.CurrentTrack) && needsFetch)
            {

                _thumbCts?.Cancel();
                _thumbCts = new CancellationTokenSource();
                var token = _thumbCts.Token;
                bool shouldForceThumbFetch = forceFetchForTrackChange;

                string trackDuringFetch = info.CurrentTrack;
                string artistDuringFetch = info.CurrentArtist;
                string sourceAppDuringFetch = info.SourceAppId ?? "";
                string sessionKeyDuringFetch = info.SessionInstanceKey ?? "";
                int generationAtStart = Volatile.Read(ref _thumbnailFetchGeneration);

                bool isBrowserIcon = info.Thumbnail != null && info.Platform == MediaPlatform.Browser;
                bool needsBetterThumb = shouldForceThumbFetch || info.Thumbnail == null || info.Thumbnail.PixelWidth < 120 || isBrowserIcon;

                _ = Task.Run(() => FetchYouTubeThumbnailAsync(info, token, shouldForceThumbFetch, trackDuringFetch, artistDuringFetch, sourceAppDuringFetch, sessionKeyDuringFetch, generationAtStart, needsBetterThumb), token);
            }
            else if (isPotentialSoundCloud && !isPotentialYouTube && !string.IsNullOrEmpty(info.CurrentTrack))
            {
                bool isNewSoundCloudTrack = !string.IsNullOrEmpty(soundCloudTrackIdentity) &&
                                            !string.Equals(soundCloudTrackIdentity, _lastSoundCloudArtworkIdentity, StringComparison.Ordinal);
                bool sameTrackFetchRunning =
                    Volatile.Read(ref _soundCloudFetchInFlight) == 1 &&
                    string.Equals(_soundCloudFetchIdentity, soundCloudTrackIdentity, StringComparison.Ordinal);

                bool shouldStartSoundCloudFetch = ThumbnailFetchPlanner.ShouldStartSoundCloudFetch(new SoundCloudFetchInputs
                {
                    IsNewSoundCloudTrack = isNewSoundCloudTrack,
                    SameTrackFetchRunning = sameTrackFetchRunning,
                    ThumbnailIsNullOrPlaceholder = info.Thumbnail == null || IsLikelySoundCloudPlaceholderThumbnail(info.Thumbnail),
                    HasMismatchedThumbSource = MediaPlatformExtensions.ParsePlatform(_cachedThumbnailSource) != MediaPlatform.SoundCloud,
                    RetryIntervalElapsed = (DateTime.UtcNow - _lastSoundCloudArtworkAttemptTimeUtc) >= SoundCloudArtworkRetryInterval,
                });

                if (shouldStartSoundCloudFetch)
                {
                    _lastSoundCloudArtworkAttemptTimeUtc = DateTime.UtcNow;
                    _lastSoundCloudArtworkIdentity = soundCloudTrackIdentity;
                    _thumbCts?.Cancel();
                    _thumbCts = new CancellationTokenSource();
                    var token = _thumbCts.Token;
                    int fetchGeneration = Interlocked.Increment(ref _soundCloudFetchGeneration);
                    _soundCloudFetchIdentity = soundCloudTrackIdentity;
                    Interlocked.Exchange(ref _soundCloudFetchInFlight, 1);

                    string trackDuringFetch = info.CurrentTrack;
                    string artistDuringFetch = info.CurrentArtist;
                    string sourceAppDuringFetch = info.SourceAppId ?? "";
                    string sessionKeyDuringFetch = info.SessionInstanceKey ?? "";
                    int thumbGenAtStart = Volatile.Read(ref _thumbnailFetchGeneration);
                    
                    bool requireStrongMatch = true;

                    _ = Task.Run(() => FetchSoundCloudThumbnailAsync(info, token, trackDuringFetch, artistDuringFetch, sourceAppDuringFetch, sessionKeyDuringFetch, thumbGenAtStart, fetchGeneration, requireStrongMatch), token);
                }
            }
            else
            {

                _thumbCts?.Cancel();
                _soundCloudFetchIdentity = "";
                Interlocked.Exchange(ref _soundCloudFetchInFlight, 0);
            }
    }
    private async Task FetchYouTubeThumbnailAsync(MediaInfo info, CancellationToken token, bool shouldForceThumbFetch, string trackDuringFetch, string artistDuringFetch, string sourceAppDuringFetch, string sessionKeyDuringFetch, int generationAtStart, bool needsBetterThumb)
    {
                    try
                    {
                        string? videoId = shouldForceThumbFetch ? null : info.YouTubeVideoId;
                        // When the YouTube Data API is enabled the lookup returns a high-quality thumbnail URL (snippet
                        string? preferredThumbnailUrl = null;
                        int retryCount = 0;
                        bool titleSearchAttempted = false;

                        // Mismatch cache is intentionally NOT cleared per-pass

                        // ─── Priority 1: Extract video ID from ANY browser window URL ─── Scans all browser windows, not just foreground
                        if (string.IsNullOrEmpty(videoId))
                        {
                            videoId = TryExtractVideoIdFromAnyBrowserUrl();
                        }

                        // ─── Priority 2: Foreground browser URL (legacy, fast path) ───
                        if (string.IsNullOrEmpty(videoId))
                        {
                            videoId = TryExtractVideoIdFromBrowserUrl();
                        }

                        // ─── Priority 3: Use cached browser URL for this track ───
                        if (string.IsNullOrEmpty(videoId))
                        {
                            videoId = GetCachedVideoIdForTrack(trackDuringFetch);
                        }

                        // If the videoId we picked up (from any source) is in the per-track mismatch blocklist, skip it now so we don't burn another oEmbed/Data-API round trip just to discover the same stale id again
                        if (!string.IsNullOrEmpty(videoId) && TryGetCachedMismatchVideoId(videoId!))
                        {
                            videoId = null;
                        }

                        // If we got a video ID from browser URL, enrich with metadata via the metadata lookup service (Data API when enabled, oEmbed otherwise)
                        if (!string.IsNullOrEmpty(videoId) && videoId != info.YouTubeVideoId)
                        {
                            string ytUrl = $"https://www.youtube.com/watch?v={videoId}";
                            var urlResult = await _metadataLookup.TryGetYouTubeVideoInfoFromUrlAsync(ytUrl, token);
                            if (urlResult != null)
                            {
                                // ─── Validate: does the resolved video match the currently playing track? ───
                                bool videoMatchesTrack = urlResult.TitleMatches(trackDuringFetch) ||
                                                        (!string.IsNullOrEmpty(urlResult.Author) &&
                                                         !string.IsNullOrEmpty(artistDuringFetch) &&
                                                         urlResult.Author.Contains(artistDuringFetch, StringComparison.OrdinalIgnoreCase));

                                if (!videoMatchesTrack)
                                {
                                    RuntimeLog.Debug("META-YOUTUBE-API", () =>
                                        $"video-mismatch: api-title='{urlResult.Title}' smtc-track='{trackDuringFetch}' videoId={videoId} -> discarding stale videoId");
                                    CacheMismatchVideoId(videoId!);
                                    EvictVideoIdCacheEntry(trackDuringFetch, videoId!);
                                    videoId = null;
                                }
                                else
                                {
                                    CacheVideoIdForTrack(BuildTrackIdentity(trackDuringFetch, artistDuringFetch), videoId!);
                                    CacheVideoIdForTrack(trackDuringFetch, videoId!);

                                    info.MediaSource = MediaPlatform.YouTube.ToDisplayString();
                                    info.IsYouTubeRunning = true;
                                    SetSessionSourceOverride(info, MediaPlatform.YouTube.ToDisplayString());
                                    _sourceCache.SetBoth(trackDuringFetch, BuildTrackIdentity(trackDuringFetch, artistDuringFetch), MediaPlatform.YouTube.ToDisplayString());
                                    _sourceCache.Save();

                                    if (!string.IsNullOrEmpty(urlResult.Author) && urlResult.Author != "YouTube")
                                    {
                                        info.CurrentArtist = urlResult.Author;
                                        _stableArtist = urlResult.Author;
                                        _lastSourceConfirmedTime = DateTime.Now;
                                    }

                                    if (urlResult.Duration.TotalSeconds > 0)
                                    {
                                        _timelineSimulator.RecoveredDuration = urlResult.Duration;
                                        info.Duration = urlResult.Duration;
                                    }

                                    if (urlResult.Source == YouTubeLookupSource.DataApi &&
                                        !string.IsNullOrWhiteSpace(urlResult.ThumbnailUrl))
                                    {
                                        preferredThumbnailUrl = urlResult.ThumbnailUrl;
                                    }
                                }
                            }
                            else if (shouldForceThumbFetch)
                            {
                                // oEmbed/API failed to respond — cannot validate this videoId
                                RuntimeLog.Debug("META-YOUTUBE-API", () =>
                                    $"validation-failed: oEmbed returned null for videoId={videoId} track='{trackDuringFetch}' -> discarding unvalidated videoId");
                                videoId = null;
                            }
                        }

                        while (retryCount < 3 && !token.IsCancellationRequested)
                        {
                            // ─── Fallback: check if title itself contains a video ID or URL ───
                            if (string.IsNullOrEmpty(videoId))
                            {
                                var result = await TryGetYouTubeVideoIdWithInfoAsync(trackDuringFetch, artistDuringFetch);
                                if (result != null)
                                {
                                    videoId = result.Id;

                                    bool highConfidence = result.TitleMatches(trackDuringFetch) || info.Platform == MediaPlatform.YouTube;
                                    if (highConfidence)
                                    {
                                        info.MediaSource = MediaPlatform.YouTube.ToDisplayString();
                                        info.IsYouTubeRunning = true;

                                        SetSessionSourceOverride(info, MediaPlatform.YouTube.ToDisplayString());

                                        _sourceCache.SetBoth(trackDuringFetch, BuildTrackIdentity(trackDuringFetch, artistDuringFetch), MediaPlatform.YouTube.ToDisplayString());
                                        _sourceCache.Save();
                                    }

                                    if (!string.IsNullOrEmpty(result.Author) && result.Author != "YouTube")
                                    {
                                        info.CurrentArtist = result.Author;
                                        _stableArtist = result.Author;
                                        _lastSourceConfirmedTime = DateTime.Now;
                                    }

                                    if (result.Duration.TotalSeconds > 0)
                                    {
                                        _timelineSimulator.RecoveredDuration = result.Duration;
                                        info.Duration = result.Duration;
                                    }

                                    if (result.Source == YouTubeLookupSource.DataApi &&
                                        !string.IsNullOrWhiteSpace(result.ThumbnailUrl))
                                    {
                                        preferredThumbnailUrl = result.ThumbnailUrl;
                                    }
                                }
                            }

                            // ─── Last-resort fallback: search YouTube by track title ─── When the browser tab is in the background, HelpText never updates so URL-based extraction always returns the previous video's ID
                            if (string.IsNullOrEmpty(videoId) && !titleSearchAttempted)
                            {
                                titleSearchAttempted = true;
                                var searchResult = await _metadataLookup.TrySearchYouTubeByTitleAsync(trackDuringFetch, artistDuringFetch, token);
                                if (searchResult != null)
                                {
                                    videoId = searchResult.Id;

                                    info.MediaSource = MediaPlatform.YouTube.ToDisplayString();
                                    info.IsYouTubeRunning = true;
                                    SetSessionSourceOverride(info, MediaPlatform.YouTube.ToDisplayString());
                                    _sourceCache.SetBoth(trackDuringFetch, BuildTrackIdentity(trackDuringFetch, artistDuringFetch), MediaPlatform.YouTube.ToDisplayString());
                                    _sourceCache.Save();

                                    CacheVideoIdForTrack(BuildTrackIdentity(trackDuringFetch, artistDuringFetch), videoId!);
                                    CacheVideoIdForTrack(trackDuringFetch, videoId!);

                                    if (!string.IsNullOrEmpty(searchResult.Author) && searchResult.Author != "YouTube")
                                    {
                                        info.CurrentArtist = searchResult.Author;
                                        _stableArtist = searchResult.Author;
                                        _lastSourceConfirmedTime = DateTime.Now;
                                    }

                                    if (searchResult.Duration.TotalSeconds > 0)
                                    {
                                        _timelineSimulator.RecoveredDuration = searchResult.Duration;
                                        info.Duration = searchResult.Duration;
                                    }

                                    if (!string.IsNullOrWhiteSpace(searchResult.ThumbnailUrl))
                                    {
                                        preferredThumbnailUrl = searchResult.ThumbnailUrl;
                                    }

                                    RuntimeLog.Debug("MEDIA-YOUTUBE-FETCH", () =>
                                        $"title-search-resolved track='{trackDuringFetch}' artist='{artistDuringFetch}' videoId={videoId}");
                                }
                            }

                            if (!string.IsNullOrEmpty(videoId) && !token.IsCancellationRequested)
                            {
                                info.YouTubeVideoId = videoId;

                                if (!string.IsNullOrEmpty(_stableArtist) && 
                                    _stableArtist != "YouTube" &&
                                    IsStillSamePublishedTrack(trackDuringFetch, artistDuringFetch, sourceAppDuringFetch, sessionKeyDuringFetch))
                                {
                                    var dispatcher = System.Windows.Application.Current?.Dispatcher;
                                    if (dispatcher != null)
                                    {
                                        await dispatcher.InvokeAsync(() =>
                                        {
                                            if (!token.IsCancellationRequested &&
                                                IsStillSamePublishedTrack(trackDuringFetch, artistDuringFetch, sourceAppDuringFetch, sessionKeyDuringFetch))
                                            {
                                                _lastTrackSignature = info.GetSignature();
                                                MediaChanged?.Invoke(this, info);
                                            }
                                        });
                                    }
                                }

                                if (needsBetterThumb || info.Platform == MediaPlatform.YouTube) 
                                {
                                    BitmapImage? frameBitmap = null;
                                    string thumbnailUrl;

                                    // ─── Preferred path: thumbnail URL from YouTube Data API ─── The API already picked the highest-resolution variant that actually exists for this video, so we don't need to probe
                                    if (!string.IsNullOrWhiteSpace(preferredThumbnailUrl))
                                    {
                                        thumbnailUrl = preferredThumbnailUrl!;
                                        frameBitmap = await DownloadImageAsync(thumbnailUrl);
                                        if (frameBitmap != null && frameBitmap.PixelWidth < 200)
                                            frameBitmap = null;
                                    }

                                    // Try maxresdefault first (1280x720) - best quality for ONNX smart crop
                                    if (frameBitmap == null)
                                    {
                                        thumbnailUrl = $"https://i.ytimg.com/vi/{videoId}/maxresdefault.jpg";
                                        frameBitmap = await DownloadImageAsync(thumbnailUrl);

                                        // maxresdefault may return a small placeholder (120x90) for videos without HD thumbnail
                                        if (frameBitmap != null && frameBitmap.PixelWidth < 400)
                                            frameBitmap = null;
                                    }

                                    // Fallback to sddefault (640x480)
                                    if (frameBitmap == null)
                                    {
                                        thumbnailUrl = $"https://i.ytimg.com/vi/{videoId}/sddefault.jpg";
                                        frameBitmap = await DownloadImageAsync(thumbnailUrl);
                                        if (frameBitmap != null && frameBitmap.PixelWidth < 400)
                                            frameBitmap = null;
                                    }

                                    // Fallback to hqdefault (480x360)
                                    if (frameBitmap == null)
                                    {
                                        thumbnailUrl = $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";
                                        frameBitmap = await DownloadImageAsync(thumbnailUrl);
                                    }

                                    // Last resort: mqdefault (320x180)
                                    if (frameBitmap == null)
                                    {
                                        thumbnailUrl = $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg";
                                        frameBitmap = await DownloadImageAsync(thumbnailUrl);
                                    }

                                    if (frameBitmap != null && !token.IsCancellationRequested)
                                    {
                                        if (Volatile.Read(ref _thumbnailFetchGeneration) != generationAtStart)
                                            break; // Track/session changed since fetch started — discard

                                        if (IsStillSamePublishedTrack(trackDuringFetch, artistDuringFetch, sourceAppDuringFetch, sessionKeyDuringFetch))
                                        {
                                            bool isYtFetchTopicChannel = !string.IsNullOrEmpty(info.CurrentArtist) &&
                                                                         info.CurrentArtist.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase);
                                            RuntimeLog.Debug("MEDIA-THUMB-CROP", () =>
                                                $"path=youtube-fetch track='{info.CurrentTrack}' artist='{info.CurrentArtist}' " +
                                                $"thumb={frameBitmap.PixelWidth}x{frameBitmap.PixelHeight} isTopicChannel={isYtFetchTopicChannel}");
                                            frameBitmap = CropToSquare(frameBitmap, "YouTube", forceCenterCrop: isYtFetchTopicChannel) ?? frameBitmap;
                                            _timelineSimulator.RecoveredThumbnail = frameBitmap;
                                            _cachedThumbnail = frameBitmap;
                                            _cachedThumbnailSource = MediaPlatform.YouTube.ToDisplayString();
                                            info.Thumbnail = frameBitmap;

                                            var dispatcher = System.Windows.Application.Current?.Dispatcher;
                                            if (dispatcher != null)
                                            {
                                                await dispatcher.InvokeAsync(() =>
                                                {
                                                    if (!token.IsCancellationRequested &&
                                                        Volatile.Read(ref _thumbnailFetchGeneration) == generationAtStart &&
                                                        IsStillSamePublishedTrack(trackDuringFetch, artistDuringFetch, sourceAppDuringFetch, sessionKeyDuringFetch))
                                                    {
                                                        info.IsThumbnailOnlyUpdate = true;
                                                        _lastTrackSignature = info.GetSignature();
                                                        MediaChanged?.Invoke(this, info);
                                                        info.IsThumbnailOnlyUpdate = false;
                                                    }
                                                });
                                            }
                                            break;
                                        }
                                    }
                                }
                                else break;
                            }

                            retryCount++;
                            if (retryCount < 3 &&
                                !token.IsCancellationRequested &&
                                IsStillSamePublishedTrack(trackDuringFetch, artistDuringFetch, sourceAppDuringFetch, sessionKeyDuringFetch))
                            {
                                // When videoId was discarded due to mismatch, the browser hasn't navigated yet
                                if (videoId == null)
                                {
                                    const int pollIntervalMs = 200;
                                    const int maxPolls = 5; // 5 × 200ms = 1s — keep short since background tabs rarely update HelpText
                                    bool resolved = false;

                                    for (int poll = 0; poll < maxPolls && !token.IsCancellationRequested; poll++)
                                    {
                                        await Task.Delay(pollIntervalMs, token);
                                        if (!IsStillSamePublishedTrack(trackDuringFetch, artistDuringFetch, sourceAppDuringFetch, sessionKeyDuringFetch))
                                            break;

                                        // Force the scanner to re-read browser tabs each poll iteration
                                        _windowTitleScanner.InvalidateUrlCaches();

                                        string? polledId = TryExtractVideoIdFromAnyBrowserUrl();
                                        if (string.IsNullOrEmpty(polledId))
                                            polledId = TryExtractVideoIdFromBrowserUrl();
                                        if (string.IsNullOrEmpty(polledId))
                                            continue;

                                        // Same stale videoId — browser hasn't navigated yet
                                        if (TryGetCachedMismatchVideoId(polledId))
                                            continue;

                                        // New videoId! Validate it.
                                        string pollUrl = $"https://www.youtube.com/watch?v={polledId}";
                                        var pollResult = await _metadataLookup.TryGetYouTubeVideoInfoFromUrlAsync(pollUrl, token);
                                        if (pollResult == null)
                                            continue;

                                        bool pollMatches = pollResult.TitleMatches(trackDuringFetch) ||
                                                           (!string.IsNullOrEmpty(pollResult.Author) &&
                                                            !string.IsNullOrEmpty(artistDuringFetch) &&
                                                            pollResult.Author.Contains(artistDuringFetch, StringComparison.OrdinalIgnoreCase));

                                        if (!pollMatches)
                                        {
                                            CacheMismatchVideoId(polledId);
                                            continue;
                                        }

                                        // Match! Apply metadata.
                                        videoId = polledId;
                                        info.MediaSource = MediaPlatform.YouTube.ToDisplayString();
                                        info.IsYouTubeRunning = true;
                                        SetSessionSourceOverride(info, MediaPlatform.YouTube.ToDisplayString());
                                        _sourceCache.SetBoth(trackDuringFetch, BuildTrackIdentity(trackDuringFetch, artistDuringFetch), MediaPlatform.YouTube.ToDisplayString());
                                        _sourceCache.Save();

                                        if (!string.IsNullOrEmpty(pollResult.Author) && pollResult.Author != "YouTube")
                                        {
                                            info.CurrentArtist = pollResult.Author;
                                            _stableArtist = pollResult.Author;
                                            _lastSourceConfirmedTime = DateTime.Now;
                                        }
                                        if (pollResult.Duration.TotalSeconds > 0)
                                        {
                                            _timelineSimulator.RecoveredDuration = pollResult.Duration;
                                            info.Duration = pollResult.Duration;
                                        }
                                        if (pollResult.Source == YouTubeLookupSource.DataApi &&
                                            !string.IsNullOrWhiteSpace(pollResult.ThumbnailUrl))
                                        {
                                            preferredThumbnailUrl = pollResult.ThumbnailUrl;
                                        }
                                        resolved = true;
                                        break;
                                    }

                                    if (!resolved)
                                    {
                                        // Browser HelpText never updated (common when YouTube tab is in the background and auto-plays next track without a full page navigation)
                                        videoId = null;
                                        preferredThumbnailUrl = null;

                                        // ─── Immediate title-search: don't wait for another retry ─── The polling loop already wasted ~1s. Search by title now.
                                        if (!token.IsCancellationRequested &&
                                            !titleSearchAttempted &&
                                            IsStillSamePublishedTrack(trackDuringFetch, artistDuringFetch, sourceAppDuringFetch, sessionKeyDuringFetch))
                                        {
                                            titleSearchAttempted = true;
                                            var searchResult = await _metadataLookup.TrySearchYouTubeByTitleAsync(trackDuringFetch, artistDuringFetch, token);
                                            if (searchResult != null)
                                            {
                                                videoId = searchResult.Id;
                                                info.MediaSource = MediaPlatform.YouTube.ToDisplayString();
                                                info.IsYouTubeRunning = true;
                                                SetSessionSourceOverride(info, MediaPlatform.YouTube.ToDisplayString());
                                                _sourceCache.SetBoth(trackDuringFetch, BuildTrackIdentity(trackDuringFetch, artistDuringFetch), MediaPlatform.YouTube.ToDisplayString());
                                                _sourceCache.Save();
                                                CacheVideoIdForTrack(BuildTrackIdentity(trackDuringFetch, artistDuringFetch), videoId!);
                                                CacheVideoIdForTrack(trackDuringFetch, videoId!);

                                                if (!string.IsNullOrEmpty(searchResult.Author) && searchResult.Author != "YouTube")
                                                {
                                                    info.CurrentArtist = searchResult.Author;
                                                    _stableArtist = searchResult.Author;
                                                    _lastSourceConfirmedTime = DateTime.Now;
                                                }
                                                if (searchResult.Duration.TotalSeconds > 0)
                                                {
                                                    _timelineSimulator.RecoveredDuration = searchResult.Duration;
                                                    info.Duration = searchResult.Duration;
                                                }
                                                if (!string.IsNullOrWhiteSpace(searchResult.ThumbnailUrl))
                                                    preferredThumbnailUrl = searchResult.ThumbnailUrl;

                                                RuntimeLog.Debug("MEDIA-YOUTUBE-FETCH", () =>
                                                    $"title-search-after-poll track='{trackDuringFetch}' artist='{artistDuringFetch}' videoId={videoId}");
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    await Task.Delay(retryCount * 350, token);
                                    videoId = null;
                                    preferredThumbnailUrl = null;

                                    videoId = TryExtractVideoIdFromAnyBrowserUrl();
                                    if (string.IsNullOrEmpty(videoId))
                                        videoId = TryExtractVideoIdFromBrowserUrl();
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        RuntimeLog.Error("MEDIA-YOUTUBE-FETCH", ex.ToString());
                    }
    }

    private async Task FetchSoundCloudThumbnailAsync(MediaInfo info, CancellationToken token, string trackDuringFetch, string artistDuringFetch, string sourceAppDuringFetch, string sessionKeyDuringFetch, int thumbGenAtStart, int fetchGeneration, bool requireStrongMatch)
    {
                        try
                        {
                            // ─── Priority 1: Try to get artwork directly from browser URL ───
                            string? artworkUrl = null;
                            string? browserSoundCloudUrl = TryExtractSoundCloudUrlFromAnyBrowser();
                            if (!string.IsNullOrEmpty(browserSoundCloudUrl))
                            {
                                artworkUrl = await TryGetSoundCloudArtworkFromUrlAsync(browserSoundCloudUrl, token);
                            }

                            // ─── Priority 2: Fallback to title-based search ───
                            if (string.IsNullOrEmpty(artworkUrl) || IsLikelySoundCloudPlaceholderArtworkUrl(artworkUrl))
                            {
                                artworkUrl = await TryGetSoundCloudArtworkUrlAsync(trackDuringFetch, artistDuringFetch, requireStrongMatch, token);
                            }

                            if (string.IsNullOrEmpty(artworkUrl) ||
                                IsLikelySoundCloudPlaceholderArtworkUrl(artworkUrl) ||
                                token.IsCancellationRequested)
                            {
                                return;
                            }

                            var frameBitmap = await DownloadImageAsync(artworkUrl);
                            if (frameBitmap == null ||
                                IsLikelySoundCloudPlaceholderThumbnail(frameBitmap) ||
                                token.IsCancellationRequested)
                            {
                                return;
                            }

                            if (!IsStillSamePublishedTrack(trackDuringFetch, artistDuringFetch, sourceAppDuringFetch, sessionKeyDuringFetch))
                            {
                                return;
                            }

                            // Generation changed — track or session switched since fetch started
                            if (Volatile.Read(ref _thumbnailFetchGeneration) != thumbGenAtStart)
                            {
                                return;
                            }

                            // ── YouTube guard ────────────────────────────── If the current track was already resolved as YouTube (either via SMTC source upgrade or via a verified YouTube thumbnail in cache) by the time this background SoundCloud fetch completes, do NOT overwrite the YouTube artwork with a SoundCloud placeholder
                            if (info.Platform == MediaPlatform.YouTube ||
                                IsLikelyYouTube(info) ||
                                MediaPlatformExtensions.ParsePlatform(_cachedThumbnailSource) == MediaPlatform.YouTube)
                            {
                                RuntimeLog.Debug("MEDIA-THUMB-CROP", () =>
                                    $"soundcloud-fetch suppressed: track resolved as YouTube " +
                                    $"track='{trackDuringFetch}' cachedSource='{_cachedThumbnailSource}'");
                                return;
                            }

                            frameBitmap = CropToSquare(frameBitmap, "SoundCloud") ?? frameBitmap;
                            _timelineSimulator.RecoveredThumbnail = frameBitmap;
                            _cachedThumbnail = frameBitmap;
                            _cachedThumbnailSource = MediaPlatform.SoundCloud.ToDisplayString();
                            if (info.Platform == MediaPlatform.Browser)
                            {
                                info.MediaSource = MediaPlatform.SoundCloud.ToDisplayString();
                                info.IsSoundCloudRunning = true;
                                SetSessionSourceOverride(info, MediaPlatform.SoundCloud.ToDisplayString());

                                string currentTrackIdentity = BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist);
                                string currentTrackOnlyIdentity = BuildTrackIdentity(info.CurrentTrack, "");
                                if (!string.IsNullOrEmpty(currentTrackIdentity))
                                {
                                    _sourceCache.SetBoth(currentTrackIdentity, currentTrackOnlyIdentity, MediaPlatform.SoundCloud.ToDisplayString());
                                    _sourceCache.Save();
                                }
                            }
                            info.Thumbnail = frameBitmap;

                            var dispatcher = System.Windows.Application.Current?.Dispatcher;
                            if (dispatcher != null)
                            {
                                await dispatcher.InvokeAsync(() =>
                                {
                                    if (!token.IsCancellationRequested &&
                                        Volatile.Read(ref _thumbnailFetchGeneration) == thumbGenAtStart &&
                                        IsStillSamePublishedTrack(trackDuringFetch, artistDuringFetch, sourceAppDuringFetch, sessionKeyDuringFetch))
                                    {
                                        info.IsThumbnailOnlyUpdate = true;
                                        _lastTrackSignature = info.GetSignature();
                                        MediaChanged?.Invoke(this, info);
                                        info.IsThumbnailOnlyUpdate = false;
                                    }
                                });
                            }
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            RuntimeLog.Error("MEDIA-SOUNDCLOUD-FETCH", ex.ToString());
                        }
                        finally
                        {
                            if (fetchGeneration == Volatile.Read(ref _soundCloudFetchGeneration))
                            {
                                Interlocked.Exchange(ref _soundCloudFetchInFlight, 0);
                            }
                        }
    }

    private string GetSpotifyWindowTitle()
    {
        try
        {
            var spotifyProcesses = Process.GetProcessesByName("Spotify");
            foreach (var proc in spotifyProcesses)
            {
                string title = proc.MainWindowTitle;
                if (!string.IsNullOrEmpty(title) &&
                    title != "Spotify" &&
                    title != "Spotify Premium" &&
                    title != "Spotify Free" &&
                    !title.ToLower().EndsWith("spotify"))
                {
                    return title;
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-SPOTIFY-TITLE", ex.ToString());
        }
        return "";
    }

    private static string BuildTrackIdentity(string track, string artist)
        => MediaHeuristics.BuildTrackIdentity(track, artist);

    private bool IsStillSamePublishedTrack(string expectedTrack, string expectedArtist, string expectedSourceAppId, string expectedSessionInstanceKey = "")
        => PublishedTrackMatcher.IsSameTrack(
            new PublishedTrackSnapshot(
                _lastPublishedTrackIdentity,
                _lastPublishedTrackOnlyIdentity,
                _lastPublishedSourceAppId,
                _lastPublishedSessionInstanceKey),
            expectedTrack, expectedArtist, expectedSourceAppId, expectedSessionInstanceKey);

    private static string BuildSourceOverrideKey(string sessionInstanceKey, string sourceAppId)
        => MediaHeuristics.BuildSourceOverrideKey(sessionInstanceKey, sourceAppId);

    private static bool IsTrackCompatibleWithWindowTitle(string track, string windowTitle)
        => MediaHeuristics.IsTrackCompatibleWithWindowTitle(track, windowTitle);

    private static bool HasReliablePlatformWindowMatch(IEnumerable<string> windowTitles, string track, string platform)
    {
        return PlatformDetector.HasReliableWindowMatch(windowTitles, track, platform);
    }

    private bool TryGetSessionSourceOverride(MediaInfo info, out string sessionOverride)
    {
        sessionOverride = string.Empty;

        string key = BuildSourceOverrideKey(info.SessionInstanceKey, info.SourceAppId);
        if (string.IsNullOrEmpty(key) ||
            !_sessionState.TryGetSourceOverride(key, out string? resolvedOverride) ||
            string.IsNullOrWhiteSpace(resolvedOverride))
        {
            return false;
        }

        sessionOverride = resolvedOverride;
        return true;
    }

    private void SetSessionSourceOverride(MediaInfo info, string mediaSource)
    {
        string key = BuildSourceOverrideKey(info.SessionInstanceKey, info.SourceAppId);
        if (string.IsNullOrEmpty(key) || string.IsNullOrWhiteSpace(mediaSource))
        {
            return;
        }

        _sessionState.SetSourceOverride(key, mediaSource);
    }

    private void ClearSessionSourceOverride(string sessionInstanceKey, string sourceAppId)
    {
        string key = BuildSourceOverrideKey(sessionInstanceKey, sourceAppId);
        if (!string.IsNullOrEmpty(key))
        {
            _sessionState.RemoveSourceOverride(key);
        }
    }

    private static string BuildSessionInstanceKey(GlobalSystemMediaTransportControlsSession session)
    {
        string sourceApp = session.SourceAppUserModelId ?? "";
        
        int instanceHash = RuntimeHelpers.GetHashCode(session);
        return $"{sourceApp}|{instanceHash}";
    }

    private static bool IsSessionPlayingStatus(GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
    {
        return status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
    }

    private bool IsSessionStillPresent(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var sessions = _sessionManager?.GetSessions();
            if (sessions == null) return false;

            foreach (var s in sessions)
            {
                if (ReferenceEquals(s, session))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-SESSION-PRESENT", ex.ToString());
            // On error, assume present to avoid thrashing the fast path.
            return true;
        }

        return false;
    }

    private static bool IsBrowserSourceApp(string sourceAppId)
    {
        return PlatformDetector.IsBrowserApp(sourceAppId);
    }

    private static bool IsIgnoredSourceApp(string sourceAppId)
        => MediaHeuristics.IsIgnoredSourceApp(sourceAppId);

    private static string DetectPlatformHint(IEnumerable<string> windowTitles)
    {
        return PlatformDetector.DetectPlatformHint(windowTitles);
    }

    private static string NormalizeForLooseMatch(string value)
    {
        return PlatformDetector.NormalizeForLooseMatch(value);
    }

    private static bool IsLikelySoundCloudPlaceholderThumbnail(BitmapImage? thumbnail)
        => ThumbnailHeuristics.IsLikelyPlaceholderThumbnail(thumbnail);

    private static bool IsLikelySoundCloudArtworkCandidate(BitmapImage? thumbnail)
        => ThumbnailHeuristics.IsLikelyArtworkCandidate(thumbnail);

    private static bool IsLikelySoundCloudPlaceholderArtworkUrl(string? url)
        => MediaHeuristics.IsLikelySoundCloudPlaceholderArtworkUrl(url);

    private static bool HasLowEntropyMonochromeProfile(BitmapImage thumbnail)
        => ThumbnailHeuristics.HasLowEntropyMonochromeProfile(thumbnail);

    private async Task TryGetMediaSessionInfoAsync(MediaInfo info, bool forceRefresh, Func<List<string>> windowTitleFactory)
    {
        if (_sessionManager == null) return;

        try
        {
            var (session, spotifyGroundTruth) = await ResolveActiveSessionAsync(forceRefresh);
            if (session == null) return;

            SwitchActiveDisplaySessionIfNeeded(session);

            var sessionSourceApp = session.SourceAppUserModelId ?? "";
            info.IsAnyMediaPlaying = true;
            info.SourceAppId = sessionSourceApp;
            info.SessionInstanceKey = BuildSessionInstanceKey(session);

            ApplyMediaSourceFromAppId(info, sessionSourceApp);

            var mediaProperties = await session.TryGetMediaPropertiesAsync();
            string sessionTitle = mediaProperties?.Title ?? "";
            string sessionArtist = mediaProperties?.Artist ?? "";
            string lowerAlbum = (mediaProperties?.AlbumTitle ?? "").ToLower();

            RefineMediaSourceFromMetadata(info, sessionTitle.ToLower(), sessionArtist.ToLower(), lowerAlbum);

            var pbInfo = session.GetPlaybackInfo();
            info.IsPlaying = pbInfo != null && pbInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            info.PlaybackRate = pbInfo?.PlaybackRate ?? 1.0;
            info.IsAnyMediaPlaying = true;
            info.CurrentTrack = sessionTitle;
            info.CurrentArtist = sessionArtist;

            if (TryHandleJunkSessionTitle(info, sessionTitle, sessionArtist))
                return;

            StabilizeArtist(info);

            if (info.Platform == MediaPlatform.Spotify)
                ApplySpotifyGroundTruthCorrection(info, spotifyGroundTruth, ref mediaProperties);

            var currentSignature = info.GetSignature();
            // Use track+artist identity (without MediaSource) to decide if the *actual track* changed
            string currentTrackOnlyIdentityForThumb = BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist);
            bool trackChangedForThisPass = InvalidateThumbnailStateIfTrackChanged(currentTrackOnlyIdentityForThumb);

            ResolveBrowserMediaSource(info, sessionSourceApp, windowTitleFactory);

            await ApplySessionThumbnailAsync(info, mediaProperties, trackChangedForThisPass, forceRefresh);
            await ApplyTimelinePropertiesAsync(info, session, forceRefresh);

            ApplyFinalPlaybackInfo(info, session);

            _lastTrackSignature = currentSignature;
            _lastThumbTrackIdentity = currentTrackOnlyIdentityForThumb;
        }
        catch (Exception ex)
        {
            Log("UpdateError", ex.Message);
        }
    }

    /// <summary>
    /// Switches the actively-displayed SMTC session, re-wiring event subscriptions and
    /// clearing all per-session thumbnail/fetch state to prevent stale artwork from leaking
    /// across sessions.
    /// </summary>
    private void SwitchActiveDisplaySessionIfNeeded(GlobalSystemMediaTransportControlsSession session)
    {
        if (_activeDisplaySession == session) return;

        if (_activeDisplaySession != null)
        {
            try
            {
                _activeDisplaySession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _activeDisplaySession.PlaybackInfoChanged -= OnPlaybackChanged;
                _activeDisplaySession.TimelinePropertiesChanged -= OnTimelineChanged;
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("MEDIA-DETECT-UNSUB", ex.ToString());
            }
        }

        _activeDisplaySession = session;

        try
        {
            _activeDisplaySession.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _activeDisplaySession.PlaybackInfoChanged += OnPlaybackChanged;
            _activeDisplaySession.TimelinePropertiesChanged += OnTimelineChanged;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-DETECT-SUB", ex.ToString());
        }

        _lastTrackSignature = "";
        _lastThumbTrackIdentity = "";
        // Session switched — aggressively clear all thumbnail state to prevent stale thumbnails from a previous session leaking into the new one
        _cachedThumbnail = null;
        _cachedThumbnailSource = "";
        _timelineSimulator.RecoveredThumbnail = null;
        _thumbCts?.Cancel();
        _thumbCts = null;
        _soundCloudFetchIdentity = "";
        Interlocked.Exchange(ref _soundCloudFetchInFlight, 0);
        Interlocked.Increment(ref _thumbnailFetchGeneration);
    }

    /// <summary>Maps a session's SourceAppUserModelId to an initial <see cref="MediaInfo.MediaSource"/>.</summary>
    private void ApplyMediaSourceFromAppId(MediaInfo info, string sessionSourceApp)
        => MediaSourceClassifier.ApplyFromAppId(info, sessionSourceApp);

    /// <summary>Refines an unresolved Browser source into YouTube / Apple Music / SoundCloud using track metadata.</summary>
    private void RefineMediaSourceFromMetadata(MediaInfo info, string lowerTitle, string lowerArtist, string lowerAlbum)
        => MediaSourceClassifier.RefineFromMetadata(info, lowerTitle, lowerArtist, lowerAlbum);

    /// <summary>
    /// Detects placeholder/junk SMTC titles (app names, ads, empty). Returns true when the caller
    /// should abort this pass; for YouTube it first clears the track and tags the artist.
    /// </summary>
    private bool TryHandleJunkSessionTitle(MediaInfo info, string sessionTitle, string sessionArtist)
        => MediaSourceClassifier.TryHandleJunkTitle(info, sessionTitle, sessionArtist);

    /// <summary>Holds a recently-confirmed artist for browser/YouTube sources that briefly report a generic artist.</summary>
    private void StabilizeArtist(MediaInfo info)
    {
        var r = MediaTimingDecisions.EvaluateArtistStabilization(
            info.CurrentArtist, _stableArtist, _lastSourceConfirmedTime, DateTime.Now);
        info.CurrentArtist = r.artist;
        _stableArtist = r.stableArtist;
    }

    /// <summary>
    /// Corrects stale or mis-split Spotify SMTC metadata against the Spotify window title
    /// (ground truth). When a correction is applied, the SMTC media properties are dropped so
    /// downstream thumbnail logic refetches against the corrected track.
    /// </summary>
    private void ApplySpotifyGroundTruthCorrection(MediaInfo info, string? spotifyGroundTruth, ref GlobalSystemMediaTransportControlsSessionMediaProperties? mediaProperties)
    {
        bool isStaleSMTC = false;
        if (!string.IsNullOrEmpty(spotifyGroundTruth) &&
            !string.IsNullOrEmpty(info.CurrentTrack) &&
            !SpotifyTitleContainsTrack(spotifyGroundTruth, info.CurrentTrack))
        {
            ParseSpotifyTitle(spotifyGroundTruth, info);
            isStaleSMTC = true;
        }

        if (!isStaleSMTC && !string.IsNullOrEmpty(spotifyGroundTruth) &&
            !string.IsNullOrEmpty(info.CurrentArtist))
        {
            bool needsCorrection = false;
            string correctedTrack = "";
            string correctedArtist = "";

            if (info.CurrentArtist.Contains(" - ", StringComparison.Ordinal))
            {
                string artistPrefix = info.CurrentArtist + " - ";
                bool artistMatchesWindowTitle = spotifyGroundTruth.StartsWith(artistPrefix, StringComparison.OrdinalIgnoreCase);

                if (!artistMatchesWindowTitle)
                {
                    int firstSep = spotifyGroundTruth.IndexOf(" - ", StringComparison.Ordinal);
                    if (firstSep > 0)
                    {
                        string wtArtistFirst = spotifyGroundTruth.Substring(0, firstSep).Trim();
                        string wtTrackFirst = spotifyGroundTruth.Substring(firstSep + 3).Trim();

                        if (!wtArtistFirst.Contains(" - ", StringComparison.Ordinal) &&
                            !string.IsNullOrEmpty(wtTrackFirst))
                        {
                            correctedArtist = wtArtistFirst;
                            correctedTrack = wtTrackFirst;
                            needsCorrection = true;
                        }
                    }
                }
            }

            if (!needsCorrection && !string.IsNullOrEmpty(info.CurrentTrack))
            {
                // Use SMTC artist (which is clean) to split window title correctly
                string artistPrefix = info.CurrentArtist + " - ";
                if (spotifyGroundTruth.StartsWith(artistPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    string realTrack = spotifyGroundTruth.Substring(artistPrefix.Length).Trim();
                    if (!string.IsNullOrEmpty(realTrack) &&
                        !string.Equals(realTrack, info.CurrentTrack, StringComparison.OrdinalIgnoreCase) &&
                        realTrack.Contains(info.CurrentTrack, StringComparison.OrdinalIgnoreCase) &&
                        realTrack.Length > info.CurrentTrack.Length + 3)
                    {
                        correctedTrack = realTrack;
                        correctedArtist = info.CurrentArtist;
                        needsCorrection = true;
                    }
                }
            }

            if (needsCorrection)
            {
                info.CurrentTrack = correctedTrack;
                info.CurrentArtist = correctedArtist;
                isStaleSMTC = true;
            }
        }

        if (isStaleSMTC)
        {
            mediaProperties = null;
        }
    }

    /// <summary>
    /// Clears cached thumbnail/fetch state when the track identity differs from the last pass.
    /// Returns whether the track changed during this pass.
    /// </summary>
    private bool InvalidateThumbnailStateIfTrackChanged(string currentTrackOnlyIdentityForThumb)
    {
        bool trackChangedForThisPass = !string.Equals(currentTrackOnlyIdentityForThumb, _lastThumbTrackIdentity, StringComparison.Ordinal);
        if (trackChangedForThisPass)
        {
            _cachedThumbnail = null;
            _cachedThumbnailSource = "";
            _timelineSimulator.RecoveredThumbnail = null;
            _thumbCts?.Cancel();
            _thumbCts = null;
            Interlocked.Increment(ref _thumbnailFetchGeneration);
            RuntimeLog.Debug("MEDIA-THUMB-INVALIDATE", () =>
                $"Track changed: old='{_lastThumbTrackIdentity}' new='{currentTrackOnlyIdentityForThumb}' — cleared all thumbnail state");
        }
        return trackChangedForThisPass;
    }

    /// <summary>Re-reads the session's playback info as the authoritative final state for this pass.</summary>
    private void ApplyFinalPlaybackInfo(MediaInfo info, GlobalSystemMediaTransportControlsSession? session)
    {
        try
        {
            var playbackInfo = session?.GetPlaybackInfo();
            if (playbackInfo != null)
            {
                var status = playbackInfo.PlaybackStatus;
                info.IsPlaying = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                info.PlaybackRate = playbackInfo.PlaybackRate ?? 1.0;
                info.SourceAppId = session?.SourceAppUserModelId ?? "";

                if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing)
                {
                    info.IsIndeterminate = true;
                }

                var controls = playbackInfo.Controls;
                info.IsSeekEnabled = controls.IsPlaybackPositionEnabled;
            }
            else
            {
                info.IsPlaying = info.IsAnyMediaPlaying;
            }
        }
        catch { info.IsPlaying = info.IsAnyMediaPlaying; }
    }

    private void ResolveBrowserMediaSource(MediaInfo info, string sessionSourceApp, Func<List<string>> windowTitleFactory)
    {
            if (info.Platform == MediaPlatform.Browser || string.IsNullOrEmpty(info.MediaSource))
            {

                bool isSameSession = !string.IsNullOrEmpty(info.SessionInstanceKey) &&
                                     string.Equals(info.SessionInstanceKey, _lastPublishedSessionInstanceKey, StringComparison.Ordinal);
                string currentTrackIdentity = BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist);
                string trackOnlyIdentity = BuildTrackIdentity(info.CurrentTrack, "");
                bool sameTrackAsStable = !string.IsNullOrEmpty(currentTrackIdentity) &&
                                         currentTrackIdentity == _stableSourceTrackIdentity;
                List<string>? hintedWindowTitles = null;
                string hintedSource = "";
                bool sourceFromBrowserOverride = false;
                bool sourceFromTrackCache = false;

                string browserPlatformHint = "";
                if (!string.IsNullOrEmpty(sessionSourceApp) && IsBrowserSourceApp(sessionSourceApp))
                {
                    hintedWindowTitles ??= windowTitleFactory();
                    bool hasReliableYouTubeWindowMatch = HasReliablePlatformWindowMatch(hintedWindowTitles, info.CurrentTrack, "youtube");
                    browserPlatformHint = hasReliableYouTubeWindowMatch
                        ? "YouTube"
                        : DetectPlatformHint(hintedWindowTitles);
                }

                if (!string.IsNullOrEmpty(sessionSourceApp) &&
                    IsBrowserSourceApp(sessionSourceApp) &&
                    TryGetSessionSourceOverride(info, out var sessionOverride) &&
                    MediaPlatformExtensions.ParsePlatform(sessionOverride) == MediaPlatform.SoundCloud &&
                    !string.Equals(browserPlatformHint, "YouTube", StringComparison.OrdinalIgnoreCase))
                {
                    hintedWindowTitles ??= windowTitleFactory();
                    if (HasReliablePlatformWindowMatch(hintedWindowTitles, info.CurrentTrack, "soundcloud"))
                    {
                        info.MediaSource = MediaPlatform.SoundCloud.ToDisplayString();
                        info.IsSoundCloudRunning = true;
                        sourceFromBrowserOverride = true;
                    }
                }

                if ((info.Platform == MediaPlatform.Browser || string.IsNullOrEmpty(info.MediaSource)) &&
                    !string.IsNullOrEmpty(info.CurrentTrack))
                {
                    bool hasCachedSource = _sourceCache.TryGet(currentTrackIdentity, out var cachedSource) ||
                                           _sourceCache.TryGet(trackOnlyIdentity, out cachedSource);

                    if (hasCachedSource &&
                        string.Equals(cachedSource, "SoundCloud", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(browserPlatformHint, "YouTube", StringComparison.OrdinalIgnoreCase))
                    {
                        hintedWindowTitles ??= windowTitleFactory();
                        if (HasReliablePlatformWindowMatch(hintedWindowTitles, info.CurrentTrack, "soundcloud"))
                        {
                            info.MediaSource = MediaPlatform.SoundCloud.ToDisplayString();
                            info.IsSoundCloudRunning = true;
                            sourceFromTrackCache = true;
                        }
                    }
                }

                if (isSameSession && !string.IsNullOrEmpty(_stableSource) && MediaPlatformExtensions.ParsePlatform(_stableSource) != MediaPlatform.Browser && sameTrackAsStable)
                {
                    hintedWindowTitles = windowTitleFactory();
                    hintedSource = DetectPlatformHint(hintedWindowTitles);
                }

                if (isSameSession && !string.IsNullOrEmpty(_stableSource) && MediaPlatformExtensions.ParsePlatform(_stableSource) != MediaPlatform.Browser && sameTrackAsStable)
                {
                    bool canKeepStable = string.IsNullOrEmpty(hintedSource) ||
                                         string.Equals(hintedSource, _stableSource, StringComparison.OrdinalIgnoreCase);
                    if (canKeepStable)
                    {
                        info.MediaSource = _stableSource;
                        if (MediaPlatformExtensions.ParsePlatform(_stableSource) == MediaPlatform.YouTube) info.IsYouTubeRunning = true;
                    }
                }

                if (info.Platform == MediaPlatform.Browser ||
                    string.IsNullOrEmpty(info.MediaSource) ||
                    sourceFromBrowserOverride ||
                    sourceFromTrackCache)
                {
                    var windowTitles = hintedWindowTitles ?? windowTitleFactory();
                    string trackTitleLower = info.CurrentTrack.ToLower();
                    string trackTitleNormalized = NormalizeForLooseMatch(trackTitleLower);
                    bool hasTrack = !string.IsNullOrEmpty(trackTitleLower) && trackTitleLower != "browser" && trackTitleLower != "now playing";
                    bool hasReliableYouTubeWindowMatch = HasReliablePlatformWindowMatch(windowTitles, info.CurrentTrack, "youtube");
                    string platformHint = hasReliableYouTubeWindowMatch
                        ? "YouTube"
                        : DetectPlatformHint(windowTitles);
                    if ((!hasTrack && string.Equals(platformHint, "YouTube", StringComparison.OrdinalIgnoreCase)) ||
                        hasReliableYouTubeWindowMatch)
                    {
                        info.MediaSource = MediaPlatform.YouTube.ToDisplayString();
                        info.IsYouTubeRunning = true;
                    }

                    MediaSourceClassifier.DetectFromWindowTitles(info, windowTitles, trackTitleLower, trackTitleNormalized, hasTrack);
                }
            }

            if ((info.Platform == MediaPlatform.Browser || string.IsNullOrEmpty(info.MediaSource)) &&
                !string.IsNullOrEmpty(sessionSourceApp) &&
                IsBrowserSourceApp(sessionSourceApp) &&
                !string.IsNullOrEmpty(info.CurrentTrack) &&
                !IsLikelyYouTube(info) &&
                !IsKnownNonSpotifyTrack(info) &&
                _windowTitleScanner.IsSpotifyWebPlayerOpen())
            {
                info.MediaSource = MediaPlatform.Spotify.ToDisplayString();
                info.IsSpotifyPlaying = true;
                info.IsSpotifyRunning = true;
            }

            if (!string.IsNullOrEmpty(sessionSourceApp) &&
                IsBrowserSourceApp(sessionSourceApp) &&
                !string.IsNullOrEmpty(info.MediaSource) &&
                info.Platform != MediaPlatform.Browser)
            {
                if (info.Platform == MediaPlatform.YouTube)
                {
                    SetSessionSourceOverride(info, MediaPlatform.YouTube.ToDisplayString());
                }
                else if (!TryGetSessionSourceOverride(info, out var existingOverride) ||
                         MediaPlatformExtensions.ParsePlatform(existingOverride) != MediaPlatform.YouTube)
                {
                    SetSessionSourceOverride(info, info.MediaSource);
                }
            }

            if (info.Platform != MediaPlatform.Browser && !string.IsNullOrEmpty(info.MediaSource))
            {
                _stableSource = info.MediaSource;
                _lastSourceConfirmedTime = DateTime.Now;
                _cachedSource = info.MediaSource;
                _stableSourceTrackIdentity = BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist);

                if (!string.IsNullOrEmpty(info.CurrentTrack))
                {
                    string trackOnlyIdentity = BuildTrackIdentity(info.CurrentTrack, "");
                    bool cacheChanged = !_sourceCache.TryGet(_stableSourceTrackIdentity, out var cachedSource) ||
                                        !string.Equals(cachedSource, info.MediaSource, StringComparison.Ordinal) ||
                                        !_sourceCache.TryGet(trackOnlyIdentity, out var cachedTrackOnlySource) ||
                                        !string.Equals(cachedTrackOnlySource, info.MediaSource, StringComparison.Ordinal);
                    if (cacheChanged)
                    {
                        _sourceCache.SetBoth(_stableSourceTrackIdentity, trackOnlyIdentity, info.MediaSource);
                        if (info.Platform == MediaPlatform.YouTube || info.Platform == MediaPlatform.SoundCloud)
                        {
                            _sourceCache.Save();
                        }
                    }
                }
            }
            else
            {
                _cachedSource = MediaPlatform.Browser.ToDisplayString();
            }
    }

    private async Task ApplySessionThumbnailAsync(MediaInfo info, GlobalSystemMediaTransportControlsSessionMediaProperties? mediaProperties, bool trackChangedForThisPass, bool forceRefresh)
    {
            bool isYouTubeLikeSource = info.Platform == MediaPlatform.YouTube || (info.Platform == MediaPlatform.Browser && IsLikelyYouTube(info));
            bool hasVerifiedYouTubeThumb = MediaPlatformExtensions.ParsePlatform(_cachedThumbnailSource) == MediaPlatform.YouTube;
            bool hasVerifiedSoundCloudThumbGlobal = MediaPlatformExtensions.ParsePlatform(_cachedThumbnailSource) == MediaPlatform.SoundCloud;
            if (!trackChangedForThisPass && _cachedThumbnail != null &&
                (hasVerifiedYouTubeThumb || hasVerifiedSoundCloudThumbGlobal))
            {
                // Same track, already have a verified high-quality thumbnail from YouTube/SoundCloud API — don't let the SMTC thumbnail overwrite it
                info.Thumbnail = _cachedThumbnail;
            }
            else if (!trackChangedForThisPass && _cachedThumbnail != null)
            {
                // Same track (by title+artist) — reuse cached thumbnail regardless of forceRefresh or MediaSource flip-flop
                info.Thumbnail = _cachedThumbnail;
            }
            else
            {
                var thumbnail = mediaProperties?.Thumbnail;
                if (thumbnail != null)
                {
                    try
                    {
                        using var stream = await thumbnail.OpenReadAsync();
                        if (stream != null && stream.Size > 0)
                        {
                            var newBitmap = await ConvertToWpfBitmapAsync(stream);
                            if (newBitmap != null)
                            {
                                bool isSoundCloudSource = info.Platform == MediaPlatform.SoundCloud;
                                bool hasVerifiedSoundCloudThumb = MediaPlatformExtensions.ParsePlatform(_cachedThumbnailSource) == MediaPlatform.SoundCloud;
                                bool likelySoundCloudArtwork = IsLikelySoundCloudArtworkCandidate(newBitmap);

                                var thumbDecision = ThumbnailHeuristics.DecideSmtcThumbnail(new ThumbnailHeuristics.SmtcThumbnailInputs
                                {
                                    IsYouTubeLikeSource = isYouTubeLikeSource,
                                    IsSoundCloudSource = isSoundCloudSource,
                                    IsBrowserOrYouTubePlatform = info.Platform == MediaPlatform.YouTube || info.Platform == MediaPlatform.Browser,
                                    TrackChanged = trackChangedForThisPass,
                                    HasVerifiedYouTubeThumb = hasVerifiedYouTubeThumb,
                                    HasVerifiedSoundCloudThumb = hasVerifiedSoundCloudThumb,
                                    LikelySoundCloudArtwork = likelySoundCloudArtwork,
                                    RecentTrackChange = (DateTime.Now - _lastMetadataChangeTime).TotalSeconds < 4.0,
                                    CachedThumbnailIsNull = _cachedThumbnail == null,
                                    PixelWidth = newBitmap.PixelWidth,
                                    PixelHeight = newBitmap.PixelHeight,
                                });

                                if (thumbDecision == ThumbnailHeuristics.SmtcThumbnailDecision.Reject)
                                {
                                    RuntimeLog.Debug("MEDIA-THUMB-STALE", () =>
                                        $"Rejecting stale SMTC thumbnail on track change: " +
                                        $"track='{info.CurrentTrack}' source='{info.MediaSource}' " +
                                        $"thumb={newBitmap.PixelWidth}x{newBitmap.PixelHeight} aspect={(double)newBitmap.PixelWidth / newBitmap.PixelHeight:F2}");
                                    // Don't cache the stale thumbnail — leave info.Thumbnail as null so the verified lookup provides the correct one
                                    info.Thumbnail = null;
                                }
                                else if (thumbDecision == ThumbnailHeuristics.SmtcThumbnailDecision.Accept)
                                {
                                    bool isSmtcTopicChannel = !string.IsNullOrEmpty(info.CurrentArtist) &&
                                                              info.CurrentArtist.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase);
                                    RuntimeLog.Debug("MEDIA-THUMB-CROP", () =>
                                        $"path=smtc-update track='{info.CurrentTrack}' artist='{info.CurrentArtist}' source='{info.MediaSource}' " +
                                        $"thumb={newBitmap.PixelWidth}x{newBitmap.PixelHeight} aspect={(double)newBitmap.PixelWidth / newBitmap.PixelHeight:F2} " +
                                        $"isSmtcTopicChannel={isSmtcTopicChannel}");
                                    newBitmap = CropToSquare(newBitmap, info.MediaSource, forceCenterCrop: isSmtcTopicChannel) ?? newBitmap;
                                    _cachedThumbnail = newBitmap;
                                    if (info.Platform == MediaPlatform.SoundCloud)
                                    {
                                        if (likelySoundCloudArtwork)
                                        {
                                            _cachedThumbnailSource = MediaPlatform.SoundCloud.ToDisplayString();
                                        }
                                    }
                                    else
                                    {
                                        _cachedThumbnailSource = info.MediaSource ?? "";
                                    }
                                    info.Thumbnail = _cachedThumbnail;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        RuntimeLog.Error("MEDIA-THUMB-PROCESS", ex.ToString());
                    }
                }
            }
    }

    private async Task ApplyTimelinePropertiesAsync(MediaInfo info, GlobalSystemMediaTransportControlsSession? session, bool forceRefresh)
    {
            try
            {
                var timeline = session?.GetTimelineProperties();
                if (timeline != null)
                {
                    
                    bool isInitialOrBigChange = forceRefresh ||
                                               _lastTrackSignature == "" ||
                                               (DateTime.Now - _lastMetadataChangeTime).TotalSeconds < 4.0;

                    if (isInitialOrBigChange)
                    {
                        
                        await Task.Delay(120);
                        timeline = session?.GetTimelineProperties() ?? timeline;
                    }

                    var duration = timeline.EndTime - timeline.StartTime;
                    if (duration <= TimeSpan.Zero) duration = timeline.MaxSeekTime;

                    bool isNewTrack = !string.IsNullOrEmpty(info.CurrentTrack) &&
                        !string.IsNullOrEmpty(_lastTrackName) &&
                        info.CurrentTrack != _lastTrackName;

                    bool isBrowserTimelineTrack = IsBrowserSourceApp(info.SourceAppId) ||
                                                  info.Platform == MediaPlatform.YouTube ||
                                                  info.Platform == MediaPlatform.SoundCloud ||
                                                  info.Platform == MediaPlatform.Browser;

                    var solved = TimelinePositionSolver.Solve(new TimelineSolveInputs
                    {
                        StartTime = timeline.StartTime,
                        EndTime = timeline.EndTime,
                        MaxSeekTime = timeline.MaxSeekTime,
                        Position = timeline.Position,
                        LastUpdatedUtc = timeline.LastUpdatedTime.ToUniversalTime(),
                        IsInitialOrBigChange = isInitialOrBigChange,
                        IsNewTrack = isNewTrack,
                        IsBrowserTimelineTrack = isBrowserTimelineTrack,
                        IsPlaying = info.IsPlaying,
                        PlaybackRate = info.PlaybackRate,
                        NowUtc = DateTimeOffset.UtcNow,
                    });

                    info.Position = solved.Position;
                    info.Duration = solved.Duration;
                    info.LastUpdated = solved.LastUpdatedUtc;
                    if (solved.IsIndeterminate.HasValue)
                    {
                        info.IsIndeterminate = solved.IsIndeterminate.Value;
                    }
                }
                else
                {
                    info.IsIndeterminate = true;
                }
            }
            catch { info.IsIndeterminate = true; }
    }

    private async Task<(GlobalSystemMediaTransportControlsSession? session, string? spotifyGroundTruth)> ResolveActiveSessionAsync(bool forceRefresh)
    {
        if (_sessionManager == null) return (null, null);
            GlobalSystemMediaTransportControlsSession? session = null;
            string? spotifyGroundTruth = null;
            string osCurrentId = _sessionManager.GetCurrentSession()?.SourceAppUserModelId ?? "";

            if (_activeDisplaySession != null && !forceRefresh && !IsSessionStillPresent(_activeDisplaySession))
            {
                RuntimeLog.Debug("MEDIA-SESSION", "Active display session no longer listed (app closed) — forcing rescan.");
                _activeDisplaySession = null;
            }

            if (_activeDisplaySession != null && !forceRefresh)
            {
                try
                {
                    if (IsIgnoredSourceApp(_activeDisplaySession.SourceAppUserModelId ?? ""))
                    {
                        _activeDisplaySession = null;
                    }
                    else if (string.IsNullOrEmpty(osCurrentId) || osCurrentId == _activeDisplaySession.SourceAppUserModelId)
                    {
                        var playback = _activeDisplaySession.GetPlaybackInfo();
                        if (playback != null && playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        {
                            var props = await _activeDisplaySession.TryGetMediaPropertiesAsync();
                            if (props != null && !string.IsNullOrEmpty(props.Title))
                            {

                                if (_activeDisplaySession.SourceAppUserModelId?.Contains("Spotify", StringComparison.OrdinalIgnoreCase) == true)
                                {
                                    spotifyGroundTruth = GetSpotifyWindowTitle();
                                    if (string.IsNullOrEmpty(spotifyGroundTruth) || !SpotifyTitleContainsTrack(spotifyGroundTruth, props.Title))
                                    {

                                        session = null;
                                    }
                                    else
                                    {
                                        session = _activeDisplaySession;
                                    }
                                }
                                else
                                {
                                    session = _activeDisplaySession;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Error("MEDIA-DETECT-ACTIVE", ex.ToString());
                }
            }

            if (session == null)
            {
                spotifyGroundTruth ??= GetSpotifyWindowTitle();
                GlobalSystemMediaTransportControlsSession? bestSession = null;
                int bestScore = -1;
                bool hasAnyActiveSession = false;
                GlobalSystemMediaTransportControlsSession? fallbackActiveSession = null;

                try
                {
                    var sessions = _sessionManager.GetSessions();

                    foreach (var s in sessions)
                    {
                        try
                        {
                            var sourceApp = s.SourceAppUserModelId ?? "";
                            if (IsIgnoredSourceApp(sourceApp)) continue;

                            var status = s.GetPlaybackInfo().PlaybackStatus;
                            bool isActive = IsSessionPlayingStatus(status);
                            if (!isActive) continue;

                            hasAnyActiveSession = true;
                            if (fallbackActiveSession == null)
                            {
                                fallbackActiveSession = s;
                            }

                            if (!string.IsNullOrEmpty(osCurrentId) &&
                                string.Equals(sourceApp, osCurrentId, StringComparison.OrdinalIgnoreCase))
                            {
                                fallbackActiveSession = s;
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            RuntimeLog.Error("MEDIA-DETECT-ACTIVE-SCAN", ex.ToString());
                        }
                    }

                    foreach (var s in sessions)
                    {
                        try
                        {
                            var sourceApp = s.SourceAppUserModelId ?? "";
                            if (IsIgnoredSourceApp(sourceApp)) continue;

                            string sessionInstanceKey = BuildSessionInstanceKey(s);
                            var playbackInfo = s.GetPlaybackInfo();
                            var status = playbackInfo.PlaybackStatus;
                            var nowUtc = DateTime.UtcNow;

                            bool isActive = IsSessionPlayingStatus(status);
                            bool isPrevActive = ReferenceEquals(_activeDisplaySession, s);
                            bool wasPlaying = _sessionState.GetPlayingState(sessionInstanceKey);

                            if (isActive && !wasPlaying)
                            {
                                _sessionState.SetPlayStartTime(sessionInstanceKey, nowUtc);
                                _latestPlayingSessionKey = sessionInstanceKey;
                                _latestPlayingSessionStartUtc = nowUtc;

                                ClearSessionSourceOverride(sessionInstanceKey, sourceApp);

                                _lastTrackSignature = "";
                            }

                            _sessionState.SetPlayingState(sessionInstanceKey, isActive);

                            GlobalSystemMediaTransportControlsSessionMediaProperties? props = null;
                            for (int i = 0; i < 2; i++)
                            {
                                props = await s.TryGetMediaPropertiesAsync();
                                if (props != null && !string.IsNullOrEmpty(props.Title)) break;
                                if (i == 0) await Task.Delay(25);
                            }

                            bool hasTitle = props != null && !string.IsNullOrEmpty(props.Title);
                            bool hasArtist = props != null && !string.IsNullOrEmpty(props.Artist);

                            bool isSpotify = sourceApp.Contains("Spotify", StringComparison.OrdinalIgnoreCase);
                            bool isMusic = sourceApp.Contains("Music", StringComparison.OrdinalIgnoreCase);
                            bool isYouTube = sourceApp.Contains("YouTube", StringComparison.OrdinalIgnoreCase);
                            bool isBrowser = sourceApp.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ||
                                            sourceApp.Contains("Edge", StringComparison.OrdinalIgnoreCase) ||
                                            sourceApp.Contains("msedge", StringComparison.OrdinalIgnoreCase) ||
                                            sourceApp.Contains("Firefox", StringComparison.OrdinalIgnoreCase);

                            bool isOsCurrent = osCurrentId == sourceApp;

                            // Reduce OS-current bonus for browser sessions when a dedicated music app is actively playing
                            bool isDedicatedMusicAppPlaying = false;
                            if (isOsCurrent && (isBrowser || isYouTube))
                            {
                                foreach (var otherSession in sessions)
                                {
                                    try
                                    {
                                        var otherId = otherSession.SourceAppUserModelId ?? "";
                                        if (otherId.Contains("Spotify", StringComparison.OrdinalIgnoreCase) ||
                                            otherId.Contains("Music", StringComparison.OrdinalIgnoreCase))
                                        {
                                            if (IsSessionPlayingStatus(otherSession.GetPlaybackInfo().PlaybackStatus))
                                            {
                                                isDedicatedMusicAppPlaying = true;
                                                break;
                                            }
                                        }
                                    }
                                    catch { /* best-effort: a session may vanish mid-scan; skip it */ }
                                }
                            }

                            if (isActive)
                            {
                                _sessionState.SetLastPlayingTime(sourceApp, DateTime.Now);
                            }

                            double? playStartAge = null;
                            if (isActive && _sessionState.TryGetPlayStartTime(sessionInstanceKey, out var playStartUtc))
                            {
                                playStartAge = (nowUtc - playStartUtc).TotalSeconds;
                            }

                            double? latestPlayingAge = null;
                            if (!string.IsNullOrEmpty(_latestPlayingSessionKey) &&
                                sessionInstanceKey == _latestPlayingSessionKey)
                            {
                                latestPlayingAge = (nowUtc - _latestPlayingSessionStartUtc).TotalSeconds;
                            }

                            double? lastPlayingIdle = null;
                            if (_sessionState.TryGetLastPlayingTime(sourceApp, out var lastPlaying))
                            {
                                lastPlayingIdle = (DateTime.Now - lastPlaying).TotalSeconds;
                            }

                            double? timelineAge = null;
                            bool timelineBoost = false;
                            bool timelinePenalty = false;
                            try
                            {
                                var timeline = s.GetTimelineProperties();
                                if (timeline != null)
                                {
                                    timelineAge = (DateTimeOffset.UtcNow - timeline.LastUpdatedTime).TotalSeconds;

                                    if (isActive)
                                    {
                                        var advance = _sessionState.RecordTimelinePosition(
                                            sessionInstanceKey, timeline.Position, nowUtc);

                                        if (advance == MediaSessionState.TimelineAdvanceResult.Advanced ||
                                            _sessionState.IsRecentlyAdvancing(sessionInstanceKey, nowUtc, TimeSpan.FromSeconds(6)))
                                        {
                                            timelineBoost = true;
                                        }
                                        else if (advance == MediaSessionState.TimelineAdvanceResult.Stalled)
                                        {
                                            timelinePenalty = true;
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                RuntimeLog.Error("MEDIA-DETECT-TIMELINE", ex.ToString());
                            }

                            bool artistIsNonGeneric = hasArtist && props!.Artist != "YouTube" && props.Artist != "Browser";

                            int score = SessionScorer.Score(new SessionScoreInputs
                            {
                                HasTitle = hasTitle,
                                HasArtist = hasArtist,
                                HasThumbnail = props?.Thumbnail != null,
                                ArtistIsNonGeneric = artistIsNonGeneric,
                                IsSpotify = isSpotify,
                                IsMusic = isMusic,
                                IsYouTube = isYouTube,
                                IsBrowser = isBrowser,
                                IsOsCurrent = isOsCurrent,
                                DedicatedMusicAppPlaying = isDedicatedMusicAppPlaying,
                                IsActive = isActive,
                                PlayStartAgeSeconds = playStartAge,
                                LatestPlayingAgeSeconds = latestPlayingAge,
                                LastPlayingIdleSeconds = lastPlayingIdle,
                                TimelineAgeSeconds = timelineAge,
                                TimelineBoost = timelineBoost,
                                TimelinePenalty = timelinePenalty,
                            });

                            bool eligible = hasAnyActiveSession
                                ? isActive
                                : (isActive || isPrevActive || osCurrentId == sourceApp);

                            if (score > bestScore && eligible)
                            {
                                bestScore = score;
                                bestSession = s;
                            }
                        }
                        catch (Exception ex)
                        {
                            RuntimeLog.Error("MEDIA-DETECT-SCORE", ex.ToString());
                        }
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Error("MEDIA-DETECT-SESSIONS", ex.ToString());
                }

                if (hasAnyActiveSession && bestSession != null)
                {
                    try
                    {
                        if (!IsSessionPlayingStatus(bestSession.GetPlaybackInfo().PlaybackStatus))
                        {
                            bestSession = fallbackActiveSession ?? bestSession;
                        }
                    }
                    catch (Exception ex)
                    {
                        RuntimeLog.Error("MEDIA-DETECT-BESTCHECK", ex.ToString());
                    }
                }

                if (bestSession != null && _activeDisplaySession != null && !ReferenceEquals(bestSession, _activeDisplaySession))
                {
                    bool bestIsPlaying = false;
                    try
                    {
                        bestIsPlaying = IsSessionPlayingStatus(bestSession.GetPlaybackInfo().PlaybackStatus);
                    }
                    catch (Exception ex)
                    {
                        RuntimeLog.Error("MEDIA-DETECT-BEST-PLAYSTATE", ex.ToString());
                    }

                    if (!bestIsPlaying && IsSessionStillPresent(_activeDisplaySession))
                    {
                        bestSession = _activeDisplaySession;
                    }
                }

                if (bestSession != null && _activeDisplaySession != null && !ReferenceEquals(bestSession, _activeDisplaySession))
                {
                    string bestId = bestSession.SourceAppUserModelId ?? "";
                    string bestSessionKey = BuildSessionInstanceKey(bestSession);
                    if (bestId != _pendingSessionAppId)
                    {
                        _pendingSessionAppId = bestId;
                        _pendingSessionStartTime = DateTime.Now;
                    }

                    string currentId = _activeDisplaySession.SourceAppUserModelId ?? "";
                    bool currentIsPremium = currentId.Contains("Spotify", StringComparison.OrdinalIgnoreCase) ||
                                          currentId.Contains("Music", StringComparison.OrdinalIgnoreCase);

                    bool isOsCurrent = !string.IsNullOrEmpty(osCurrentId) && bestId == osCurrentId;
                    bool isRecentLatestPlayback = !string.IsNullOrEmpty(_latestPlayingSessionKey) &&
                                                  bestSessionKey == _latestPlayingSessionKey &&
                                                  (DateTime.UtcNow - _latestPlayingSessionStartUtc).TotalSeconds < 8;
                    bool currentStillPlaying = false;
                    try
                    {
                        currentStillPlaying = IsSessionPlayingStatus(_activeDisplaySession.GetPlaybackInfo().PlaybackStatus);
                    }
                    catch (Exception ex)
                    {
                        RuntimeLog.Error("MEDIA-DETECT-CURRENT-STATUS", ex.ToString());
                    }

                    bool hasFreshTimeline = false;
                    try
                    {
                        var bestTimeline = bestSession.GetTimelineProperties();
                        if (bestTimeline != null)
                        {
                            var timelineAge = (DateTimeOffset.UtcNow - bestTimeline.LastUpdatedTime).TotalSeconds;
                            hasFreshTimeline = timelineAge >= 0 && timelineAge < 2.0;
                            
                            var currentTimeline = _activeDisplaySession.GetTimelineProperties();
                            if (currentTimeline != null && hasFreshTimeline)
                            {
                                var currentTimelineAge = (DateTimeOffset.UtcNow - currentTimeline.LastUpdatedTime).TotalSeconds;
                                
                                if (currentTimelineAge - timelineAge > 3.0)
                                {
                                    RuntimeLog.Debug("MEDIA-SESSION", () => $"Switching session: fresh timeline New={timelineAge:F1}s vs Current={currentTimelineAge:F1}s");
                                    hasFreshTimeline = true;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        RuntimeLog.Error("MEDIA-DETECT-FRESH-TIMELINE", ex.ToString());
                    }

                    if (SessionSwitchArbiter.ShouldHoldCurrentSession(new SessionSwitchInputs
                    {
                        CurrentIsPremium = currentIsPremium,
                        BestIsOsCurrent = isOsCurrent,
                        BestIsRecentLatestPlayback = isRecentLatestPlayback,
                        CurrentStillPlaying = currentStillPlaying,
                        BestHasFreshTimeline = hasFreshTimeline,
                        PendingElapsedSeconds = (DateTime.Now - _pendingSessionStartTime).TotalSeconds,
                    }))
                    {
                        bestSession = _activeDisplaySession;
                    }
                }
                else
                {
                    _pendingSessionAppId = "";
                }

                if (bestSession != null)
                {
                    if (!ReferenceEquals(_activeDisplaySession, bestSession))
                    {
                        _lastSessionSwitchTime = DateTime.Now;
                    }
                    session = bestSession;
                }
                else
                {
                    if (hasAnyActiveSession && fallbackActiveSession != null)
                    {
                        session = fallbackActiveSession;
                    }
                    else
                    {
                        bool currentStillPlaying = false;
                        try
                        {
                            if (_activeDisplaySession != null)
                            {
                                currentStillPlaying = IsSessionPlayingStatus(_activeDisplaySession.GetPlaybackInfo().PlaybackStatus);
                            }
                        }
                        catch (Exception ex)
                        {
                            RuntimeLog.Error("MEDIA-DETECT-FALLBACK-STATUS", ex.ToString());
                        }

                        session = _sessionManager.GetCurrentSession();
                    }
                }
            }

        return (session, spotifyGroundTruth);
    }

    private Task<YouTubeLookupResult?> TryGetYouTubeVideoIdWithInfoAsync(string title, string artist = "", CancellationToken ct = default)
        => _metadataLookup.TryGetYouTubeVideoIdWithInfoAsync(title, artist, ct);

    private async Task<string?> TryGetYouTubeVideoIdAsync(string title, CancellationToken ct = default)
    {
        var res = await TryGetYouTubeVideoIdWithInfoAsync(title, "", ct);
        return res?.Id;
    }

    // ── Mismatch cache: avoid re-validating the same stale videoId during rapid polling ──
    private bool TryGetCachedMismatchVideoId(string videoId)
        => _videoIds.IsMismatch(videoId);

    private void CacheMismatchVideoId(string videoId)
        => _videoIds.MarkMismatch(videoId);

    private void ClearMismatchCache()
        => _videoIds.ClearMismatches();

    private Task<string?> TryGetSoundCloudArtworkUrlAsync(string title, string artist = "", bool requireStrongMatch = false, CancellationToken ct = default)
        => _metadataLookup.TryGetSoundCloudArtworkUrlAsync(title, artist, requireStrongMatch, ct);

    private Task<string?> TryGetSoundCloudArtworkFromUrlAsync(string soundCloudUrl, CancellationToken ct = default)
        => _metadataLookup.TryGetSoundCloudArtworkFromUrlAsync(soundCloudUrl, ct);

    private Task<BitmapImage?> DownloadImageAsync(string url, CancellationToken ct = default)
        => _artworkService.DownloadImageAsync(url, ct);

    private BitmapImage? CropToSquare(BitmapImage source, string mediaSource, bool forceCenterCrop = false)
        => _artworkService.CropToSquare(source, mediaSource, forceCenterCrop);

    private Task<BitmapImage?> ConvertToWpfBitmapAsync(Windows.Storage.Streams.IRandomAccessStreamWithContentType stream, CancellationToken ct = default)
        => _artworkService.ConvertToWpfBitmapAsync(stream, ct);

    private List<string> GetAllWindowTitles()
        => _windowTitleScanner.GetAllWindowTitles(_timelineSimulator.IsThrottled);

    private string? TryExtractVideoIdFromBrowserUrl()
    {
        try
        {
            string? url = _windowTitleScanner.TryGetBrowserUrl();
            if (string.IsNullOrEmpty(url)) return null;

            // Match youtube.com/watch?v=VIDEO_ID or youtu.be/VIDEO_ID or music.youtube.com
            var match = System.Text.RegularExpressions.Regex.Match(url,
                @"(?:youtube\.com/watch\?.*v=|youtu\.be/|music\.youtube\.com/watch\?.*v=)([a-zA-Z0-9_-]{11})");

            if (match.Success)
            {
                string videoId = match.Groups[1].Value;
                System.Diagnostics.Debug.WriteLine($"[MediaDetection] Extracted video ID from browser URL: {videoId}");
                // NOTE: Do NOT cache here
                return videoId;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaDetection] Browser URL extraction failed: {ex.Message}");
        }

        return null;
    }

    private string? TryExtractVideoIdFromAnyBrowserUrl()
    {
        try
        {
            string? url = _windowTitleScanner.TryGetMediaUrlFromAnyBrowser();
            if (string.IsNullOrEmpty(url)) return null;

            var match = System.Text.RegularExpressions.Regex.Match(url,
                @"(?:youtube\.com/watch\?.*v=|youtu\.be/|music\.youtube\.com/watch\?.*v=)([a-zA-Z0-9_-]{11})");

            if (match.Success)
            {
                string videoId = match.Groups[1].Value;
                System.Diagnostics.Debug.WriteLine($"[MediaDetection] Extracted video ID from background browser: {videoId}");
                // See TryExtractVideoIdFromBrowserUrl: do NOT cache pre-validation.
                return videoId;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaDetection] Background browser URL extraction failed: {ex.Message}");
        }

        return null;
    }

    private string? TryExtractSoundCloudUrlFromAnyBrowser()
    {
        try
        {
            string? url = _windowTitleScanner.TryGetMediaUrlFromAnyBrowser();
            if (string.IsNullOrEmpty(url)) return null;

            if (url.Contains("soundcloud.com/", StringComparison.OrdinalIgnoreCase))
            {
                // Validate it's a track URL (user/track format)
                var match = System.Text.RegularExpressions.Regex.Match(url,
                    @"soundcloud\.com/([^/\s?#]+)/([^/\s?#]+)");
                if (match.Success)
                {
                    System.Diagnostics.Debug.WriteLine($"[MediaDetection] Extracted SoundCloud URL from browser: {url}");
                    return url;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaDetection] SoundCloud browser URL extraction failed: {ex.Message}");
        }

        return null;
    }

    // ─── Video ID cache per track (persists across browser focus changes) ───
    private readonly VideoIdCache _videoIds = new();

    private void CacheVideoIdForTrack(string? trackIdentity, string videoId)
        => _videoIds.Set(trackIdentity, videoId);

    private string? GetCachedVideoIdForTrack(string? track)
        => _videoIds.Get(track);

    private void ForgetVideoIdCacheExceptForTrack(string? currentTrackIdentity)
        => _videoIds.ForgetExcept(currentTrackIdentity);

    private void EvictVideoIdCacheEntry(string? track, string staleVideoId)
        => _videoIds.Evict(track, staleVideoId);

    private bool IsLikelyYouTube(MediaInfo info)
    {
        if (info.Platform == MediaPlatform.YouTube) return true;

        if (info.Platform == MediaPlatform.Browser && !string.IsNullOrEmpty(info.SourceAppId))
        {
            if (TryGetSessionSourceOverride(info, out var sOver) && MediaPlatformExtensions.ParsePlatform(sOver) == MediaPlatform.YouTube)
            {
                if (string.IsNullOrEmpty(info.CurrentTrack))
                {
                    return true;
                }

                string trackIdentity = BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist);
                string trackOnlyIdentity = BuildTrackIdentity(info.CurrentTrack, "");
                bool hasYouTubeTrackCache = _sourceCache.HasSource(trackIdentity, trackOnlyIdentity, "YouTube");
                if (hasYouTubeTrackCache)
                {
                    return true;
                }
            }
        }

        if (!string.IsNullOrEmpty(info.CurrentArtist) &&
            info.CurrentArtist.Equals("YouTube", StringComparison.OrdinalIgnoreCase))
            return true;

        // ── Window-title fallback ───────────────────────────────────────────── SMTC reports browser-hosted media as "Browser" (not "YouTube") until we've seen a successful URL/oEmbed lookup that lets us cache an override
        if (info.Platform == MediaPlatform.Browser && !string.IsNullOrEmpty(info.CurrentTrack))
        {
            try
            {
                var titles = GetAllWindowTitles();
                if (titles != null && titles.Count > 0)
                {
                    if (PlatformDetector.HasReliableWindowMatch(titles, info.CurrentTrack, "youtube"))
                    {
                        return true;
                    }

                    if (MediaPlatformExtensions.ParsePlatform(DetectPlatformHint(titles)) == MediaPlatform.YouTube)
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // Window enumeration is best-effort; treat failure as "no hint".
            }
        }

        return false;
    }

    private bool IsKnownNonSpotifyTrack(MediaInfo info)
    {
        if (string.IsNullOrEmpty(info.CurrentTrack)) return false;

        string trackIdentity = BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist);
        string trackOnlyIdentity = BuildTrackIdentity(info.CurrentTrack, "");

        // Check source cache — populated from previous confirmed lookups
        if (_sourceCache.TryGet(trackIdentity, out var cachedSource) ||
            _sourceCache.TryGet(trackOnlyIdentity, out cachedSource))
        {
            if (!string.IsNullOrEmpty(cachedSource) &&
                !string.Equals(cachedSource, "Spotify", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(cachedSource, "Browser", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Check session override — set when a browser session was previously confirmed as YouTube/SoundCloud
        if (TryGetSessionSourceOverride(info, out var sessionOverride) &&
            !string.IsNullOrEmpty(sessionOverride) &&
            MediaPlatformExtensions.ParsePlatform(sessionOverride) != MediaPlatform.Spotify)
        {
            return true;
        }

        return false;
    }

    private bool ShouldPreserveSoundCloudSourceDuringTrackSwitch(MediaInfo info)
    {
        // The session-override lookup is the only external dependency; resolve it here (a pure read of
        // session state) and hand the result to the pure decision core.
        bool hasOverride = TryGetSessionSourceOverride(info, out var sessionOverride);
        return MediaTimingDecisions.ShouldPreserveSoundCloud(
            info.MediaSource, info.CurrentTrack, info.CurrentArtist, info.SourceAppId, info.SessionInstanceKey,
            _lastSource, _lastPublishedSessionInstanceKey, _lastMetadataChangeTime, DateTime.Now,
            hasOverride, sessionOverride);
    }

    private void ApplySimulatedTimeline(MediaInfo info, bool atEndStuck)
    {
        _timelineSimulator.ApplySimulatedTimeline(info, atEndStuck);
    }

    private void ParseSpotifyTitle(string title, MediaInfo info)
    {
        var (artist, track) = PlatformDetector.ParseSpotifyTitle(title);
        info.CurrentArtist = artist;
        info.CurrentTrack = track;
    }

    private static bool SpotifyTitleContainsTrack(string spotifyWindowTitle, string smtcTrack)
        => MediaHeuristics.SpotifyTitleContainsTrack(spotifyWindowTitle, smtcTrack);

    private static string NormalizeTrackForComparison(string text)
        => MediaHeuristics.NormalizeTrackForComparison(text);

    private static string ExtractCoreTrackName(string track)
        => MediaHeuristics.ExtractCoreTrackName(track);

    private string ExtractVideoTitle(string windowTitle, string platform)
    {
        return PlatformDetector.ExtractTitleFromWindow(windowTitle, platform);
    }

    public bool TryGetCurrentSessionVolume(out float volume, out bool isMuted)
    {
        string sourceAppId = GetActiveSourceAppId();
        return _volumeService.TryGetVolume(sourceAppId, out volume, out isMuted);
    }

    public bool TrySetCurrentSessionVolume(float volume)
    {
        string sourceAppId = GetActiveSourceAppId();
        return _volumeService.TrySetVolume(sourceAppId, volume);
    }

    public bool TryToggleCurrentSessionMute()
    {
        string sourceAppId = GetActiveSourceAppId();
        return _volumeService.TryToggleMute(sourceAppId);
    }

    private string GetActiveSourceAppId()
    {
        try
        {
            return _activeDisplaySession?.SourceAppUserModelId
                ?? _currentSession?.SourceAppUserModelId
                ?? _sessionManager?.GetCurrentSession()?.SourceAppUserModelId
                ?? "";
        }
        catch
        {
            return "";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bgCts?.Cancel();
        _changeChannel.Writer.TryComplete();

        UnsubscribeFromSession();

        if (_sessionManager != null)
        {
            _sessionManager.CurrentSessionChanged -= OnSessionChanged;
            _sessionManager.SessionsChanged -= OnSessionsChanged;
        }
    }

    public Task PlayPauseAsync() => _transportService.PlayPauseAsync();

    public Task NextTrackAsync() => _transportService.NextTrackAsync();

    public Task PreviousTrackAsync() => _transportService.PreviousTrackAsync();

    public Task SeekAsync(TimeSpan position) => _transportService.SeekAsync(position);

    public Task SeekRelativeAsync(double seconds) => _transportService.SeekRelativeAsync(seconds);

    public Task SeekToAbsoluteAsync(TimeSpan position) => _transportService.SeekToAbsoluteAsync(position);
}

[Flags]
public enum ChangeType
{
    None = 0,
    Heartbeat = 1,
    Timeline = 2,
    Playback = 4,
    MediaProperties = 8,
    SessionChanged = 16,
    ForceRefresh = 32
}

public enum DetectionMode
{
    Idle,
    EventDriven,
    ThrottledMedia
}

