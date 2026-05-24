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
                RuntimeLog.Log("MEDIA-SESSION", $"Subscribed to session: {_currentSession.SourceAppUserModelId}");
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
                RuntimeLog.Log("MEDIA-UNSUBSCRIBE", ex.ToString());
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
            RuntimeLog.Log("MEDIA-MODE", $"Detection mode: {oldMode} -> {_currentMode} (Playing={info.IsAnyMediaPlaying}, Track='{info.CurrentTrack}', Throttled={info.IsThrottled})");
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

            bool needsFallback = !info.IsAnyMediaPlaying || (string.IsNullOrEmpty(info.CurrentTrack) && info.MediaSource == "Browser") || info.MediaSource == "Browser" || string.IsNullOrEmpty(info.MediaSource);

            if (needsFallback && info.IsAnyMediaPlaying)
            {
                windowTitles ??= GetAllWindowTitles();

                if (info.MediaSource != "Spotify")
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
                                info.MediaSource = "Spotify";
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
                                info.MediaSource = "YouTube";
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
                                info.MediaSource = "SoundCloud";
                                info.IsSoundCloudRunning = true;
                                
                                // Extract track info from window title Format: "Artist - Track | SoundCloud"
                                string extractedTitle = ExtractVideoTitle(title, "SoundCloud");
                                if (extractedTitle.Contains(" - "))
                                {
                                    var parts = extractedTitle.Split(" - ", 2);
                                    info.CurrentArtist = parts[0].Trim();
                                    info.CurrentTrack = parts[1].Trim();
                                }
                                else
                                {
                                    info.CurrentTrack = extractedTitle;
                                    info.CurrentArtist = "SoundCloud";
                                }
                                
                                // Mark for thumbnail fetch
                                SetSessionSourceOverride(info, "SoundCloud");
                                
                                break;
                            }
                        }
                    }
                }
            }

            var currentSignature = info.GetSignature();

            bool isVideoSource = info.MediaSource == "YouTube" ||
                                 (info.MediaSource == "Browser" && IsLikelyYouTube(info));
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

                        info.MediaSource = "YouTube";
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
                                    var parts = extractedTitle.Split(" - ", 2);
                                    trackName = parts[1].Trim();
                                    artistName = parts[0].Trim();
                                }

                                bool isNewTrack = trackName != _lastTrackName && !_lastTrackName.Contains(trackName);

                                if (isNewTrack)
                                {

                                    info.CurrentTrack = trackName;
                                    info.CurrentArtist = artistName;
                                    info.MediaSource = "YouTube";
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
                                    info.MediaSource = "YouTube";
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

            if (info.CurrentTrack != _lastTrackName)
            {
                _lastTrackName = info.CurrentTrack;
                _lastMetadataChangeTime = DateTime.Now;

                // Reset _stableArtist on genuine track change so the previous track's artist
                // doesn't bleed into the new track via the stabilization logic.
                if (!string.IsNullOrEmpty(info.CurrentTrack))
                {
                    _stableArtist = "";
                }

                if (_timelineSimulator.IsThrottled) _timelineSimulator.Reset();
            }

            if (ShouldPreserveSoundCloudSourceDuringTrackSwitch(info))
            {
                windowTitles ??= GetAllWindowTitles();
                bool hasYouTubeWindowHint = string.Equals(DetectPlatformHint(windowTitles), "YouTube", StringComparison.OrdinalIgnoreCase);
                bool hasSoundCloudWindowMatch = HasReliablePlatformWindowMatch(windowTitles, info.CurrentTrack, "soundcloud");
                if (!hasYouTubeWindowHint && hasSoundCloudWindowMatch)
                {
                    info.MediaSource = "SoundCloud";
                    info.IsSoundCloudRunning = true;
                    SetSessionSourceOverride(info, "SoundCloud");
                }
            }

            info.IsThrottled = _timelineSimulator.IsThrottled;
            currentSignature = info.GetSignature(); 

            if (string.IsNullOrEmpty(info.CurrentTrack))
            {
                
                if (info.IsAnyMediaPlaying)
                {
                    if (_emptyMetadataStartTime == DateTime.MinValue)
                    {
                        _emptyMetadataStartTime = DateTime.Now;
                    }
                    if ((DateTime.Now - _emptyMetadataStartTime).TotalSeconds < 2.5 && !string.IsNullOrEmpty(_lastStableTrackSignature))
                    {
                        return;
                    }
                }
                else
                {
                    _emptyMetadataStartTime = DateTime.MinValue;
                    _lastStableTrackSignature = "";
                }
            }
            else
            {
                _emptyMetadataStartTime = DateTime.MinValue;
                _lastStableTrackSignature = currentSignature;
            }

            bool metadataChanged = currentSignature != _lastPublishedSignature;
            bool playbackChanged = info.IsPlaying != _lastIsPlaying;
            bool sourceChanged = info.MediaSource != _lastSource;
            bool seekCapabilityChanged = info.IsSeekEnabled != _lastSeekEnabled;
            string prevPublishedTrackOnlyIdentity = _lastPublishedTrackOnlyIdentity;
            bool isNewTrackForThumbnail = !string.IsNullOrEmpty(info.CurrentTrack) &&
                                          !string.Equals(
                                              BuildTrackIdentity(info.CurrentTrack, ""),
                                              prevPublishedTrackOnlyIdentity,
                                              StringComparison.Ordinal);

            bool significantJump = Math.Abs((info.Position - _lastPosition).TotalSeconds) >= (info.IsThrottled ? 5.0 : 1.5);
            bool throttleChanged = info.IsThrottled != _lastIsThrottled;
            bool inStartupSyncWindow = DateTime.UtcNow <= _startupProgressSyncUntilUtc;
            bool startupTimelineSync = inStartupSyncWindow &&
                                       info.IsPlaying &&
                                       (info.Duration.TotalSeconds > 0 || info.Position.TotalSeconds > 0) &&
                                       Math.Abs((info.Position - _lastPosition).TotalSeconds) >= 0.2;

            if (forceRefresh || metadataChanged || playbackChanged || sourceChanged || (significantJump && !info.IsThrottled) || seekCapabilityChanged || throttleChanged || startupTimelineSync)
            {
                bool shouldHoldEmptyTrack = string.IsNullOrEmpty(info.CurrentTrack) && info.IsAnyMediaPlaying;
                if (shouldHoldEmptyTrack && !string.IsNullOrEmpty(_lastPublishedSignature) && !forceRefresh)
                {
                    return;
                }

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

                UpdateDetectionMode(info);

                // For YouTube sources where we're about to fetch a better thumbnail, suppress ALL intermediate thumbnails to avoid showing 2-3 different images
                bool willFetchYouTubeThumbnail = (info.MediaSource == "YouTube" || (info.MediaSource == "Browser" && IsLikelyYouTube(info)))
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

                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher != null)
                {
                    RuntimeLog.Log("MEDIA-EVENT",
                        $"Firing MediaChanged: Source={info.MediaSource}, App={info.SourceAppId}, Track='{info.CurrentTrack}', Artist='{info.CurrentArtist}', " +
                        $"Pos={info.Position.TotalSeconds:F3}s, Dur={info.Duration.TotalSeconds:F3}s, IsPlaying={info.IsPlaying}, " +
                        $"Rate={info.PlaybackRate:F3}, LastUpdated={info.LastUpdated:O}");
                    await dispatcher.InvokeAsync(() => MediaChanged?.Invoke(this, info));
                }
                else
                {
                    RuntimeLog.Log("MEDIA-EVENT", "WARNING: No dispatcher, cannot fire MediaChanged event");
                }

                if (!forceRefresh && metadataChanged && !string.IsNullOrEmpty(info.CurrentTrack))
                {
                    _changeChannel.Writer.TryWrite(ChangeType.ForceRefresh);
                }
            }

            bool isPotentialYouTube = info.MediaSource == "YouTube" || (info.MediaSource == "Browser" && IsLikelyYouTube(info));

            // ── Critical fix: Browser source from a known browser app should ALWAYS attempt YouTube fetch first
            bool isBrowserApp = info.MediaSource == "Browser" && !string.IsNullOrEmpty(info.SourceAppId);
            if (isBrowserApp && !isPotentialYouTube)
            {
                // Treat any browser-sourced media as potential YouTube unless we have a confirmed SoundCloud session override.
                bool hasSoundCloudOverride = TryGetSessionSourceOverride(info, out var scOver) &&
                                             string.Equals(scOver, "SoundCloud", StringComparison.OrdinalIgnoreCase);
                if (!hasSoundCloudOverride)
                {
                    isPotentialYouTube = true;
                }
            }

            bool hasSoundCloudSessionOverride = !string.IsNullOrEmpty(info.SourceAppId) &&
                                                TryGetSessionSourceOverride(info, out var sourceOverride) &&
                                                string.Equals(sourceOverride, "SoundCloud", StringComparison.OrdinalIgnoreCase);

            // Hard gate against SoundCloud probing: if any visible browser tab currently shows a YouTube URL, the SMTC "Browser" source is categorically YouTube
            bool browserHasYouTubeTabOpen = false;
            if (info.MediaSource == "Browser" && !hasSoundCloudSessionOverride)
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

            // Improved SoundCloud detection from Browser source Trigger if: Browser source + not YouTube + has track + (no thumbnail OR placeholder OR session override)
            bool shouldProbeSoundCloudFromBrowser = info.MediaSource == "Browser" &&
                                                    !IsLikelyYouTube(info) &&
                                                    !browserHasYouTubeTabOpen &&
                                                    !string.IsNullOrEmpty(info.CurrentTrack) &&
                                                    (info.Thumbnail == null || 
                                                     IsLikelySoundCloudPlaceholderThumbnail(info.Thumbnail) || 
                                                     hasSoundCloudSessionOverride);
            
            bool isPotentialSoundCloud = info.MediaSource == "SoundCloud" || shouldProbeSoundCloudFromBrowser;
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
                    RuntimeLog.Log("MEDIA-THUMB-CROP",
                        $"path=initial-smtc track='{info.CurrentTrack}' artist='{info.CurrentArtist}' source='{info.MediaSource}' " +
                        $"thumb={info.Thumbnail.PixelWidth}x{info.Thumbnail.PixelHeight} aspect={thumbAspect:F2} " +
                        $"isNearSquare={isNearSquare} isTopicChannel={isTopicChannel}");
                    var cropped = CropToSquare(info.Thumbnail, info.MediaSource ?? "YouTube", forceCenterCrop: isTopicChannel) ?? info.Thumbnail;
                    info.Thumbnail = cropped;
                    _cachedThumbnail = cropped;
                    _cachedThumbnailSource = "YouTube";
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

                bool isBrowserIcon = info.Thumbnail != null && info.MediaSource == "Browser";
                bool needsBetterThumb = shouldForceThumbFetch || info.Thumbnail == null || info.Thumbnail.PixelWidth < 120 || isBrowserIcon;

                _ = Task.Run(async () =>
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
                                    RuntimeLog.Log("META-YOUTUBE-API",
                                        $"video-mismatch: api-title='{urlResult.Title}' smtc-track='{trackDuringFetch}' videoId={videoId} -> discarding stale videoId");
                                    CacheMismatchVideoId(videoId!);
                                    EvictVideoIdCacheEntry(trackDuringFetch, videoId!);
                                    videoId = null;
                                }
                                else
                                {
                                    CacheVideoIdForTrack(BuildTrackIdentity(trackDuringFetch, artistDuringFetch), videoId!);
                                    CacheVideoIdForTrack(trackDuringFetch, videoId!);

                                    info.MediaSource = "YouTube";
                                    info.IsYouTubeRunning = true;
                                    SetSessionSourceOverride(info, "YouTube");
                                    _sourceCache.SetBoth(trackDuringFetch, BuildTrackIdentity(trackDuringFetch, artistDuringFetch), "YouTube");
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
                                RuntimeLog.Log("META-YOUTUBE-API",
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

                                    bool highConfidence = result.TitleMatches(trackDuringFetch) || info.MediaSource == "YouTube";
                                    if (highConfidence)
                                    {
                                        info.MediaSource = "YouTube";
                                        info.IsYouTubeRunning = true;

                                        SetSessionSourceOverride(info, "YouTube");

                                        _sourceCache.SetBoth(trackDuringFetch, BuildTrackIdentity(trackDuringFetch, artistDuringFetch), "YouTube");
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

                                    info.MediaSource = "YouTube";
                                    info.IsYouTubeRunning = true;
                                    SetSessionSourceOverride(info, "YouTube");
                                    _sourceCache.SetBoth(trackDuringFetch, BuildTrackIdentity(trackDuringFetch, artistDuringFetch), "YouTube");
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

                                    RuntimeLog.Log("MEDIA-YOUTUBE-FETCH",
                                        $"title-search-resolved track='{trackDuringFetch}' artist='{artistDuringFetch}' videoId={videoId}");
                                }
                            }

                            if (!string.IsNullOrEmpty(videoId) && !token.IsCancellationRequested)
                            {
                                info.YouTubeVideoId = videoId;

                                // Fire MediaChanged immediately if artist was resolved from API/oEmbed
                                // so the UI updates without waiting for the thumbnail fetch
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

                                if (needsBetterThumb || info.MediaSource == "YouTube") 
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
                                            RuntimeLog.Log("MEDIA-THUMB-CROP",
                                                $"path=youtube-fetch track='{info.CurrentTrack}' artist='{info.CurrentArtist}' " +
                                                $"thumb={frameBitmap.PixelWidth}x{frameBitmap.PixelHeight} isTopicChannel={isYtFetchTopicChannel}");
                                            frameBitmap = CropToSquare(frameBitmap, "YouTube", forceCenterCrop: isYtFetchTopicChannel) ?? frameBitmap;
                                            _timelineSimulator.RecoveredThumbnail = frameBitmap;
                                            _cachedThumbnail = frameBitmap;
                                            _cachedThumbnailSource = "YouTube";
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
                                        info.MediaSource = "YouTube";
                                        info.IsYouTubeRunning = true;
                                        SetSessionSourceOverride(info, "YouTube");
                                        _sourceCache.SetBoth(trackDuringFetch, BuildTrackIdentity(trackDuringFetch, artistDuringFetch), "YouTube");
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
                                                info.MediaSource = "YouTube";
                                                info.IsYouTubeRunning = true;
                                                SetSessionSourceOverride(info, "YouTube");
                                                _sourceCache.SetBoth(trackDuringFetch, BuildTrackIdentity(trackDuringFetch, artistDuringFetch), "YouTube");
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

                                                RuntimeLog.Log("MEDIA-YOUTUBE-FETCH",
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
                        RuntimeLog.Log("MEDIA-YOUTUBE-FETCH", ex.ToString());
                    }
                }, token);
            }
            else if (isPotentialSoundCloud && !isPotentialYouTube && !string.IsNullOrEmpty(info.CurrentTrack))
            {
                bool isNewSoundCloudTrack = !string.IsNullOrEmpty(soundCloudTrackIdentity) &&
                                            !string.Equals(soundCloudTrackIdentity, _lastSoundCloudArtworkIdentity, StringComparison.Ordinal);
                bool hasMismatchedThumbSource = !string.Equals(_cachedThumbnailSource, "SoundCloud", StringComparison.OrdinalIgnoreCase);
                bool shouldRetryPlaceholder = (info.Thumbnail == null ||
                                              IsLikelySoundCloudPlaceholderThumbnail(info.Thumbnail) ||
                                              hasMismatchedThumbSource) &&
                                              (DateTime.UtcNow - _lastSoundCloudArtworkAttemptTimeUtc) >= SoundCloudArtworkRetryInterval;
                bool sameTrackFetchRunning =
                    Volatile.Read(ref _soundCloudFetchInFlight) == 1 &&
                    string.Equals(_soundCloudFetchIdentity, soundCloudTrackIdentity, StringComparison.Ordinal);

                if (!isNewSoundCloudTrack && sameTrackFetchRunning)
                {
                    
                }
                else if (isNewSoundCloudTrack || shouldRetryPlaceholder)
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

                    _ = Task.Run(async () =>
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
                            if (info.MediaSource == "YouTube" ||
                                IsLikelyYouTube(info) ||
                                string.Equals(_cachedThumbnailSource, "YouTube", StringComparison.OrdinalIgnoreCase))
                            {
                                RuntimeLog.Log("MEDIA-THUMB-CROP",
                                    $"soundcloud-fetch suppressed: track resolved as YouTube " +
                                    $"track='{trackDuringFetch}' cachedSource='{_cachedThumbnailSource}'");
                                return;
                            }

                            frameBitmap = CropToSquare(frameBitmap, "SoundCloud") ?? frameBitmap;
                            _timelineSimulator.RecoveredThumbnail = frameBitmap;
                            _cachedThumbnail = frameBitmap;
                            _cachedThumbnailSource = "SoundCloud";
                            if (info.MediaSource == "Browser")
                            {
                                info.MediaSource = "SoundCloud";
                                info.IsSoundCloudRunning = true;
                                SetSessionSourceOverride(info, "SoundCloud");

                                string currentTrackIdentity = BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist);
                                string currentTrackOnlyIdentity = BuildTrackIdentity(info.CurrentTrack, "");
                                if (!string.IsNullOrEmpty(currentTrackIdentity))
                                {
                                    _sourceCache.SetBoth(currentTrackIdentity, currentTrackOnlyIdentity, "SoundCloud");
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
                            RuntimeLog.Log("MEDIA-SOUNDCLOUD-FETCH", ex.ToString());
                        }
                        finally
                        {
                            if (fetchGeneration == Volatile.Read(ref _soundCloudFetchGeneration))
                            {
                                Interlocked.Exchange(ref _soundCloudFetchInFlight, 0);
                            }
                        }
                    }, token);
                }
            }
            else
            {

                _thumbCts?.Cancel();
                _soundCloudFetchIdentity = "";
                Interlocked.Exchange(ref _soundCloudFetchInFlight, 0);
            }
        }
        finally
        {
            _updateLock.Release();
        }
    }

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
            RuntimeLog.Log("MEDIA-SPOTIFY-TITLE", ex.ToString());
        }
        return "";
    }

    private DateTime _lastSourceConfirmedTime = DateTime.MinValue;
    private string _stableSource = "";
    private string _stableArtist = "";
    private string _stableSourceTrackIdentity = "";

    private static string BuildTrackIdentity(string track, string artist)
    {
        return $"{track.Trim().ToLowerInvariant()}|{artist.Trim().ToLowerInvariant()}";
    }

    private bool IsStillSamePublishedTrack(string expectedTrack, string expectedArtist, string expectedSourceAppId, string expectedSessionInstanceKey = "")
    {
        // Session key is the strongest guard — if the session changed, the track is definitely not the same context even if title matches
        if (!string.IsNullOrEmpty(expectedSessionInstanceKey) &&
            !string.Equals(_lastPublishedSessionInstanceKey, expectedSessionInstanceKey, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(expectedSourceAppId) &&
            !string.Equals(_lastPublishedSourceAppId, expectedSourceAppId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string expectedIdentity = BuildTrackIdentity(expectedTrack, expectedArtist);
        // Primary check: full track+artist identity must match
        if (string.Equals(_lastPublishedTrackIdentity, expectedIdentity, StringComparison.Ordinal))
        {
            return true;
        }

        // Fallback: track-only match, but ONLY if the artist was unknown at fetch time
        if (string.IsNullOrEmpty(expectedArtist))
        {
            string expectedTrackOnly = BuildTrackIdentity(expectedTrack, "");
            return string.Equals(_lastPublishedTrackOnlyIdentity, expectedTrackOnly, StringComparison.Ordinal);
        }

        return false;
    }

    private static string BuildSourceOverrideKey(string sessionInstanceKey, string sourceAppId)
    {
        if (!string.IsNullOrWhiteSpace(sessionInstanceKey))
        {
            return sessionInstanceKey;
        }

        return IsBrowserSourceApp(sourceAppId) ? string.Empty : sourceAppId ?? string.Empty;
    }

    private static bool IsTrackCompatibleWithWindowTitle(string track, string windowTitle)
    {
        if (string.IsNullOrWhiteSpace(track) || string.IsNullOrWhiteSpace(windowTitle))
            return false;

        string normalizedTrack = PlatformDetector.NormalizeForLooseMatch(track);
        string normalizedWindowTitle = PlatformDetector.NormalizeForLooseMatch(windowTitle);
        return !string.IsNullOrEmpty(normalizedTrack) &&
               normalizedWindowTitle.Contains(normalizedTrack, StringComparison.Ordinal);
    }

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

    private static bool IsBrowserSourceApp(string sourceAppId)
    {
        return PlatformDetector.IsBrowserApp(sourceAppId);
    }

    private static bool IsIgnoredSourceApp(string sourceAppId)
    {
        return sourceAppId.Contains("Discord", StringComparison.OrdinalIgnoreCase);
    }

    private static string DetectPlatformHint(IEnumerable<string> windowTitles)
    {
        return PlatformDetector.DetectPlatformHint(windowTitles);
    }

    private static string NormalizeForLooseMatch(string value)
    {
        return PlatformDetector.NormalizeForLooseMatch(value);
    }

    private static bool IsLikelySoundCloudPlaceholderThumbnail(BitmapImage? thumbnail)
    {
        if (thumbnail == null || thumbnail.PixelWidth <= 0 || thumbnail.PixelHeight <= 0)
        {
            return true;
        }

        double aspect = (double)thumbnail.PixelWidth / thumbnail.PixelHeight;
        bool isSquare = Math.Abs(aspect - 1.0) < 0.06;
        if (!isSquare)
        {
            return false;
        }

        if (thumbnail.PixelWidth <= 320 || thumbnail.PixelHeight <= 320)
        {
            return true;
        }

        return HasLowEntropyMonochromeProfile(thumbnail);
    }

    private static bool IsLikelySoundCloudArtworkCandidate(BitmapImage? thumbnail)
    {
        if (thumbnail == null || thumbnail.PixelWidth <= 0 || thumbnail.PixelHeight <= 0)
        {
            return false;
        }

        double aspect = (double)thumbnail.PixelWidth / thumbnail.PixelHeight;
        bool isSquare = Math.Abs(aspect - 1.0) < 0.08;
        if (!isSquare)
        {
            return false;
        }

        return thumbnail.PixelWidth >= 360 &&
               thumbnail.PixelHeight >= 360 &&
               !IsLikelySoundCloudPlaceholderThumbnail(thumbnail);
    }

    private static bool IsLikelySoundCloudPlaceholderArtworkUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return true;
        }

        string normalized = url.Replace("\\u0026", "&").Replace("\\/", "/").ToLowerInvariant();
        return normalized.Contains("default_avatar", StringComparison.Ordinal) ||
               normalized.Contains("/images/default_", StringComparison.Ordinal) ||
               normalized.Contains("default-soundcloud", StringComparison.Ordinal) ||
               normalized.Contains("/avatars-", StringComparison.Ordinal);
    }

    private static bool HasLowEntropyMonochromeProfile(BitmapImage thumbnail)
    {
        try
        {
            int sampleSize = 24;
            double scale = Math.Min(
                1.0,
                (double)sampleSize / Math.Max(thumbnail.PixelWidth, thumbnail.PixelHeight));

            BitmapSource source = thumbnail;
            if (scale < 1.0)
            {
                var transformed = new TransformedBitmap(thumbnail, new ScaleTransform(scale, scale));
                transformed.Freeze();
                source = transformed;
            }

            var formatted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            formatted.Freeze();

            int width = Math.Max(1, formatted.PixelWidth);
            int height = Math.Max(1, formatted.PixelHeight);
            int stride = width * 4;
            byte[] pixels = new byte[stride * height];
            formatted.CopyPixels(pixels, stride, 0);

            var quantizedBins = new HashSet<int>();
            double saturationSum = 0;
            int nearGrayCount = 0;
            int brightCount = 0;
            int pixelCount = width * height;

            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte b = pixels[i];
                byte g = pixels[i + 1];
                byte r = pixels[i + 2];

                int max = Math.Max(r, Math.Max(g, b));
                int min = Math.Min(r, Math.Min(g, b));
                int delta = max - min;

                if (delta <= 12) nearGrayCount++;
                if (max >= 220 && min >= 220) brightCount++;

                quantizedBins.Add(((r >> 5) << 6) | ((g >> 5) << 3) | (b >> 5));
                saturationSum += max == 0 ? 0 : (double)delta / max;
            }

            double avgSaturation = saturationSum / pixelCount;
            double grayRatio = (double)nearGrayCount / pixelCount;
            double brightRatio = (double)brightCount / pixelCount;

            return quantizedBins.Count <= 18 &&
                   avgSaturation <= 0.08 &&
                   grayRatio >= 0.84 &&
                   brightRatio >= 0.02;
        }
        catch
        {
            return false;
        }
    }

    private async Task TryGetMediaSessionInfoAsync(MediaInfo info, bool forceRefresh, Func<List<string>> windowTitleFactory)
    {
        if (_sessionManager == null) return;

        try
        {

            GlobalSystemMediaTransportControlsSession? session = null;
            string? spotifyGroundTruth = null;
            string osCurrentId = _sessionManager.GetCurrentSession()?.SourceAppUserModelId ?? "";

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
                        if (playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        {
                            var props = await _activeDisplaySession.TryGetMediaPropertiesAsync();
                            if (props != null && !string.IsNullOrEmpty(props.Title))
                            {

                                if (_activeDisplaySession.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
                                {
                                    spotifyGroundTruth = GetSpotifyWindowTitle();
                                    if (string.IsNullOrEmpty(spotifyGroundTruth) || !spotifyGroundTruth.Contains(props.Title, StringComparison.OrdinalIgnoreCase))
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
                    RuntimeLog.Log("MEDIA-DETECT-ACTIVE", ex.ToString());
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
                            RuntimeLog.Log("MEDIA-DETECT-ACTIVE-SCAN", ex.ToString());
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
                                _lastThumbTrackIdentity = "";
                                // Only clear thumbnail cache if this is a different session starting playback — not the current active session resuming.
                                if (!ReferenceEquals(s, _activeDisplaySession))
                                {
                                    _cachedThumbnail = null;
                                    _cachedThumbnailSource = "";
                                }
                            }

                            _sessionState.SetPlayingState(sessionInstanceKey, isActive);

                            int score = 0;
                            GlobalSystemMediaTransportControlsSessionMediaProperties? props = null;
                            for (int i = 0; i < 2; i++)
                            {
                                props = await s.TryGetMediaPropertiesAsync();
                                if (props != null && !string.IsNullOrEmpty(props.Title)) break;
                                if (i == 0) await Task.Delay(25);
                            }

                            if (props != null && !string.IsNullOrEmpty(props.Title))
                            {
                                score += 50;
                                if (!string.IsNullOrEmpty(props.Artist)) score += 20;
                                if (props.Thumbnail != null) score += 10;
                            }

                            bool isSpotify = sourceApp.Contains("Spotify", StringComparison.OrdinalIgnoreCase);
                            bool isMusic = sourceApp.Contains("Music", StringComparison.OrdinalIgnoreCase);
                            bool isYouTube = sourceApp.Contains("YouTube", StringComparison.OrdinalIgnoreCase);
                            bool isBrowser = sourceApp.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ||
                                            sourceApp.Contains("Edge", StringComparison.OrdinalIgnoreCase) ||
                                            sourceApp.Contains("msedge", StringComparison.OrdinalIgnoreCase) ||
                                            sourceApp.Contains("Firefox", StringComparison.OrdinalIgnoreCase);

                            if (isSpotify) score += 400;
                            else if (isMusic) score += 400;
                            else if (isYouTube) score += 350;
                            else if (isBrowser) score += 100;

                            if (osCurrentId == sourceApp)
                            {
                                // Reduce OS-current bonus for browser sessions when a dedicated music app is actively playing
                                bool isDedicatedMusicAppPlaying = false;
                                if (isBrowser || isYouTube)
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
                                        catch { }
                                    }
                                }

                                score += isDedicatedMusicAppPlaying ? 200 : 1000;
                            }

                            if (isActive)
                            {
                                score += 500;
                                if (osCurrentId == sourceApp && !(isBrowser || isYouTube)) score += 1000;
                                _sessionState.SetLastPlayingTime(sourceApp, DateTime.Now);
                            }

                            if (isActive && _sessionState.TryGetPlayStartTime(sessionInstanceKey, out var playStartUtc))
                            {
                                var ageSeconds = (nowUtc - playStartUtc).TotalSeconds;
                                if (ageSeconds >= 0 && ageSeconds < 45)
                                {
                                    score += (int)Math.Max(0, 2600 - (ageSeconds * 45));
                                }
                            }

                            if (!string.IsNullOrEmpty(_latestPlayingSessionKey) &&
                                sessionInstanceKey == _latestPlayingSessionKey)
                            {
                                var latestAgeSeconds = (nowUtc - _latestPlayingSessionStartUtc).TotalSeconds;
                                if (latestAgeSeconds >= 0 && latestAgeSeconds < 30)
                                {
                                    score += (int)Math.Max(0, 2200 - (latestAgeSeconds * 60));
                                }
                            }

                            if (_sessionState.TryGetLastPlayingTime(sourceApp, out var lastPlaying))
                            {
                                var idleSeconds = (DateTime.Now - lastPlaying).TotalSeconds;
                                if (idleSeconds < 30) score += (int)((30 - idleSeconds) * 10);
                            }

                            try
                            {
                                var timeline = s.GetTimelineProperties();
                                if (timeline != null)
                                {
                                    var timelineAge = (DateTimeOffset.UtcNow - timeline.LastUpdatedTime).TotalSeconds;
                                    if (timelineAge >= 0 && timelineAge < 20)
                                    {
                                        score += (int)Math.Max(0, 200 - (timelineAge * 8));
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                RuntimeLog.Log("MEDIA-DETECT-TIMELINE", ex.ToString());
                            }

                            if (props != null && !string.IsNullOrEmpty(props.Title))
                            {
                                score += 1500;
                                if (!string.IsNullOrEmpty(props.Artist) && props.Artist != "YouTube" && props.Artist != "Browser") score += 200;
                            }

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
                            RuntimeLog.Log("MEDIA-DETECT-SCORE", ex.ToString());
                        }
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Log("MEDIA-DETECT-SESSIONS", ex.ToString());
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
                        RuntimeLog.Log("MEDIA-DETECT-BESTCHECK", ex.ToString());
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

                    double holdTime = currentIsPremium ? 4.0 : 1.5;

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
                        RuntimeLog.Log("MEDIA-DETECT-CURRENT-STATUS", ex.ToString());
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
                                    RuntimeLog.Log("MEDIA-SESSION", $"Switching session: fresh timeline New={timelineAge:F1}s vs Current={currentTimelineAge:F1}s");
                                    hasFreshTimeline = true;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        RuntimeLog.Log("MEDIA-DETECT-FRESH-TIMELINE", ex.ToString());
                    }

                    if (!isRecentLatestPlayback &&
                        !isOsCurrent &&
                        !hasFreshTimeline &&  
                        currentStillPlaying &&
                        (DateTime.Now - _pendingSessionStartTime).TotalSeconds < holdTime)
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
                            RuntimeLog.Log("MEDIA-DETECT-FALLBACK-STATUS", ex.ToString());
                        }

                        session = _sessionManager.GetCurrentSession();
                    }
                }
            }

            if (session == null) return;

            if (_activeDisplaySession != session)
            {

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
                        RuntimeLog.Log("MEDIA-DETECT-UNSUB", ex.ToString());
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
                    RuntimeLog.Log("MEDIA-DETECT-SUB", ex.ToString());
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

            var sessionSourceApp = session.SourceAppUserModelId ?? "";
            string activeSessionInstanceKey = BuildSessionInstanceKey(session);
            info.IsAnyMediaPlaying = true; 
            info.SourceAppId = sessionSourceApp; 
            info.SessionInstanceKey = activeSessionInstanceKey;

            if (!string.IsNullOrEmpty(sessionSourceApp))
            {
                if (sessionSourceApp.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
                {
                    info.MediaSource = "Spotify";
                    info.IsSpotifyPlaying = true;
                    info.IsSpotifyRunning = true;
                }
                else if (sessionSourceApp.Contains("YouTube", StringComparison.OrdinalIgnoreCase))
                {
                    info.MediaSource = "YouTube";
                    info.IsYouTubeRunning = true;
                }
                else if (IsBrowserSourceApp(sessionSourceApp))
                {
                    info.MediaSource = "Browser";
                }
                else if (sessionSourceApp.Contains("Music", StringComparison.OrdinalIgnoreCase) ||
                         sessionSourceApp.Contains("Apple", StringComparison.OrdinalIgnoreCase) ||
                         sessionSourceApp.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase))
                {
                    info.MediaSource = "Apple Music";
                    info.IsAppleMusicRunning = true;
                }
                else
                {

                    info.MediaSource = "Browser";
                }

            }

            var mediaProperties = await session.TryGetMediaPropertiesAsync();

            string sessionTitle = mediaProperties?.Title ?? "";
            string lowerTitle = sessionTitle.ToLower();
            string sessionArtist = mediaProperties?.Artist ?? "";
            string lowerArtist = sessionArtist.ToLower();
            string lowerAlbum = (mediaProperties?.AlbumTitle ?? "").ToLower();

            if (info.MediaSource == "Browser" || string.IsNullOrEmpty(info.MediaSource))
            {
                bool isYouTube = lowerArtist.Contains("youtube") ||
                                 lowerTitle.Contains("youtube") ||
                                 lowerTitle.EndsWith("- youtube") ||
                                 lowerTitle.EndsWith("– youtube") ||
                                 lowerAlbum.Contains("youtube");

                if (isYouTube)
                {
                    info.MediaSource = "YouTube";
                    info.IsYouTubeRunning = true;
                }
                else if (lowerArtist.Contains("apple music") || lowerTitle.Contains("apple music") || lowerAlbum.Contains("apple music") || lowerAlbum.Contains("music.apple.com"))
                {
                    info.MediaSource = "Apple Music";
                    info.IsAppleMusicRunning = true;
                }
                else if (lowerArtist.Contains("soundcloud") || lowerTitle.Contains("soundcloud") || lowerAlbum.Contains("soundcloud"))
                {
                    info.MediaSource = "SoundCloud";
                    info.IsSoundCloudRunning = true;
                }
            }

            bool isJunkTitle = string.IsNullOrEmpty(sessionTitle) ||
                               lowerTitle == "spotify" ||
                               lowerTitle == "advertisement" ||
                               lowerTitle == "windows media player" ||
                               lowerTitle == "spotify free" ||
                               lowerTitle == "spotify premium" ||
                               lowerTitle == "chrome" ||
                               lowerTitle == "edge" ||
                               lowerTitle == "brave" ||
                               lowerTitle == "opera" ||
                               lowerTitle == "firefox" ||
                               (lowerTitle == "youtube" && (string.IsNullOrEmpty(sessionArtist) || lowerArtist == "youtube"));

            var pbInfo = session.GetPlaybackInfo();
            info.IsPlaying = pbInfo != null && pbInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            info.PlaybackRate = pbInfo?.PlaybackRate ?? 1.0;
            info.IsAnyMediaPlaying = true;

            info.CurrentTrack = sessionTitle;
            info.CurrentArtist = sessionArtist;

            if (isJunkTitle)
            {

                if (info.MediaSource == "YouTube")
                {
                    info.CurrentTrack = ""; 
                    info.CurrentArtist = "YouTube";
                }
                else if (info.MediaSource == "Browser")
                {

                    if (!string.IsNullOrEmpty(_lastStableTrackSignature) && (DateTime.Now - _emptyMetadataStartTime).TotalSeconds < 2.5)
                        return;

                    return;
                }

                return;
            }

            if ((info.CurrentArtist == "YouTube" || info.CurrentArtist == "Browser") &&
                !string.IsNullOrEmpty(_stableArtist) && (DateTime.Now - _lastSourceConfirmedTime).TotalSeconds < 15.0)
            {
                info.CurrentArtist = _stableArtist;
            }
            else if (!string.IsNullOrEmpty(info.CurrentArtist) && info.CurrentArtist != "YouTube" && info.CurrentArtist != "Browser")
            {
                _stableArtist = info.CurrentArtist;
            }

            if (info.MediaSource == "Spotify")
            {
                bool isStaleSMTC = false;
                if (!string.IsNullOrEmpty(spotifyGroundTruth) &&
                    !string.IsNullOrEmpty(info.CurrentTrack) &&
                    !spotifyGroundTruth.Contains(info.CurrentTrack, StringComparison.OrdinalIgnoreCase))
                {
                    ParseSpotifyTitle(spotifyGroundTruth, info);
                    isStaleSMTC = true;
                }

                if (isStaleSMTC)
                {
                    mediaProperties = null; 
                }
            }

            var currentSignature = info.GetSignature();
            // Use track+artist identity (without MediaSource) to decide if the *actual track* changed
            string currentTrackOnlyIdentityForThumb = BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist);
            bool trackChangedForThisPass = !string.Equals(currentTrackOnlyIdentityForThumb, _lastThumbTrackIdentity, StringComparison.Ordinal);

            if (trackChangedForThisPass)
            {
                _cachedThumbnail = null;
                _cachedThumbnailSource = "";
                _timelineSimulator.RecoveredThumbnail = null;
                Interlocked.Increment(ref _thumbnailFetchGeneration);
            }

            if (info.MediaSource == "Browser" || string.IsNullOrEmpty(info.MediaSource))
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
                    string.Equals(sessionOverride, "SoundCloud", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(browserPlatformHint, "YouTube", StringComparison.OrdinalIgnoreCase))
                {
                    hintedWindowTitles ??= windowTitleFactory();
                    if (HasReliablePlatformWindowMatch(hintedWindowTitles, info.CurrentTrack, "soundcloud"))
                    {
                        info.MediaSource = "SoundCloud";
                        info.IsSoundCloudRunning = true;
                        sourceFromBrowserOverride = true;
                    }
                }

                if ((info.MediaSource == "Browser" || string.IsNullOrEmpty(info.MediaSource)) &&
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
                            info.MediaSource = "SoundCloud";
                            info.IsSoundCloudRunning = true;
                            sourceFromTrackCache = true;
                        }
                    }
                }

                if (isSameSession && !string.IsNullOrEmpty(_stableSource) && _stableSource != "Browser" && sameTrackAsStable)
                {
                    hintedWindowTitles = windowTitleFactory();
                    hintedSource = DetectPlatformHint(hintedWindowTitles);
                }

                if (isSameSession && !string.IsNullOrEmpty(_stableSource) && _stableSource != "Browser" && sameTrackAsStable)
                {
                    bool canKeepStable = string.IsNullOrEmpty(hintedSource) ||
                                         string.Equals(hintedSource, _stableSource, StringComparison.OrdinalIgnoreCase);
                    if (canKeepStable)
                    {
                        info.MediaSource = _stableSource;
                        if (_stableSource == "YouTube") info.IsYouTubeRunning = true;
                    }
                }

                if (info.MediaSource == "Browser" ||
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
                        info.MediaSource = "YouTube";
                        info.IsYouTubeRunning = true;
                    }

                    foreach (var title in windowTitles)
                    {
                        if (string.Equals(info.MediaSource, "YouTube", StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }

                        var winTitleLower = title.ToLower();
                        bool trackMatch = winTitleLower.Contains(trackTitleLower);

                        if (!trackMatch && !string.IsNullOrEmpty(trackTitleNormalized))
                        {
                            var winTitleNormalized = NormalizeForLooseMatch(winTitleLower);
                            trackMatch = winTitleNormalized.Contains(trackTitleNormalized, StringComparison.Ordinal);
                        }

                        if (hasTrack && !trackMatch)
                        {
                            continue;
                        }

                        if (winTitleLower.Contains("youtube") && !winTitleLower.StartsWith("youtube -") && winTitleLower != "youtube")
                        {
                            info.MediaSource = "YouTube";
                            info.IsYouTubeRunning = true;
                            string extractedYouTubeTitle = ExtractVideoTitle(title, "YouTube");
                            if (!string.IsNullOrWhiteSpace(extractedYouTubeTitle) &&
                                extractedYouTubeTitle.Length > info.CurrentTrack.Length &&
                                NormalizeForLooseMatch(extractedYouTubeTitle).Contains(NormalizeForLooseMatch(info.CurrentTrack), StringComparison.Ordinal))
                            {
                                info.CurrentTrack = extractedYouTubeTitle;
                            }
                            break;
                        }
                        else if (winTitleLower.Contains("soundcloud"))
                        {
                            info.MediaSource = "SoundCloud";
                            info.IsSoundCloudRunning = true;
                            break;
                        }
                        else if (winTitleLower.Contains("apple music") || winTitleLower.Contains("music.apple.com") ||
                                 (winTitleLower.Contains("apple") && winTitleLower.Contains("music")))
                        {
                            info.MediaSource = "Apple Music";
                            info.IsAppleMusicRunning = true;
                            break;
                        }
                        else if (winTitleLower.Contains("facebook") && (winTitleLower.Contains("watch") || winTitleLower.Contains("video")))
                        {
                            info.MediaSource = "Facebook";
                            info.IsFacebookRunning = true;
                            break;
                        }
                        else if (winTitleLower.Contains("tiktok") && winTitleLower.Contains(" | "))
                        {
                            info.MediaSource = "TikTok";
                            info.IsTikTokRunning = true;
                            break;
                        }
                        else if (winTitleLower.Contains("instagram") && (winTitleLower.Contains("reel") || winTitleLower.Contains("video")))
                        {
                            info.MediaSource = "Instagram";
                            info.IsInstagramRunning = true;
                            break;
                        }
                        else if ((winTitleLower.Contains("twitter") || winTitleLower.Contains(" / x")) && (winTitleLower.Contains("video") || winTitleLower.Contains("watch")))
                        {
                            info.MediaSource = "Twitter";
                            info.IsTwitterRunning = true;
                            break;
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(sessionSourceApp) &&
                IsBrowserSourceApp(sessionSourceApp) &&
                !string.IsNullOrEmpty(info.MediaSource) &&
                info.MediaSource != "Browser")
            {
                if (info.MediaSource == "YouTube")
                {
                    SetSessionSourceOverride(info, "YouTube");
                }
                else if (!TryGetSessionSourceOverride(info, out var existingOverride) ||
                         !string.Equals(existingOverride, "YouTube", StringComparison.OrdinalIgnoreCase))
                {
                    SetSessionSourceOverride(info, info.MediaSource);
                }
            }

            if (info.MediaSource != "Browser" && !string.IsNullOrEmpty(info.MediaSource))
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
                        if (info.MediaSource == "YouTube" || info.MediaSource == "SoundCloud")
                        {
                            _sourceCache.Save();
                        }
                    }
                }
            }
            else
            {
                _cachedSource = "Browser";
            }

            bool isYouTubeLikeSource = info.MediaSource == "YouTube" || (info.MediaSource == "Browser" && IsLikelyYouTube(info));
            bool hasVerifiedYouTubeThumb = string.Equals(_cachedThumbnailSource, "YouTube", StringComparison.OrdinalIgnoreCase);
            bool hasVerifiedSoundCloudThumbGlobal = string.Equals(_cachedThumbnailSource, "SoundCloud", StringComparison.OrdinalIgnoreCase);
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
                                bool isSoundCloudSource = string.Equals(info.MediaSource, "SoundCloud", StringComparison.OrdinalIgnoreCase);
                                bool hasVerifiedSoundCloudThumb = string.Equals(_cachedThumbnailSource, "SoundCloud", StringComparison.OrdinalIgnoreCase);
                                bool likelySoundCloudArtwork = IsLikelySoundCloudArtworkCandidate(newBitmap);
                                bool skipSmtcThumbForFreshSoundCloudTrack = isSoundCloudSource &&
                                                                            trackChangedForThisPass &&
                                                                            !hasVerifiedSoundCloudThumb &&
                                                                            !likelySoundCloudArtwork;
                                // Never suppress SMTC thumbnail on track change — always show it immediately so the user sees the thumbnail update
                                bool skipSmtcThumbForFreshYouTubeTrack = false;
                                if (skipSmtcThumbForFreshSoundCloudTrack || skipSmtcThumbForFreshYouTubeTrack)
                                {
                                    
                                    info.Thumbnail = _cachedThumbnail;
                                }
                                else
                                {
                                    double aspect = (double)newBitmap.PixelWidth / newBitmap.PixelHeight;
                                    bool isSquare = Math.Abs(aspect - 1.0) < 0.05;

                                    bool isLikelySoundCloudPlaceholder =
                                        info.MediaSource == "SoundCloud" &&
                                        isSquare &&
                                        (newBitmap.PixelWidth <= 320 || newBitmap.PixelHeight <= 320);
                                    // Browser source SMTC thumbnail is typically the browser icon/favicon (not the actual video thumbnail) when the tab is in the background
                                    bool isGenericIcon = (info.MediaSource == "YouTube" || info.MediaSource == "Browser" || isLikelySoundCloudPlaceholder)
                                                         && isSquare && newBitmap.PixelWidth <= 300;
                                    // On track change, always accept the SMTC thumbnail immediately so the UI updates right away
                                    bool shouldPreferVerifiedYouTubeLookup = isYouTubeLikeSource &&
                                                                            !hasVerifiedYouTubeThumb &&
                                                                            !trackChangedForThisPass;

                                    if (!(isSquare && isGenericIcon) && !shouldPreferVerifiedYouTubeLookup)
                                    {
                                        bool isSmtcTopicChannel = !string.IsNullOrEmpty(info.CurrentArtist) &&
                                                                  info.CurrentArtist.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase);
                                        RuntimeLog.Log("MEDIA-THUMB-CROP",
                                            $"path=smtc-update track='{info.CurrentTrack}' artist='{info.CurrentArtist}' source='{info.MediaSource}' " +
                                            $"thumb={newBitmap.PixelWidth}x{newBitmap.PixelHeight} aspect={aspect:F2} " +
                                            $"isSquare={isSquare} isSmtcTopicChannel={isSmtcTopicChannel}");
                                        newBitmap = CropToSquare(newBitmap, info.MediaSource, forceCenterCrop: isSmtcTopicChannel) ?? newBitmap;
                                        _cachedThumbnail = newBitmap;
                                        if (string.Equals(info.MediaSource, "SoundCloud", StringComparison.OrdinalIgnoreCase))
                                        {
                                            if (likelySoundCloudArtwork)
                                            {
                                                _cachedThumbnailSource = "SoundCloud";
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
                    }
                    catch (Exception ex)
                    {
                        RuntimeLog.Log("MEDIA-THUMB-PROCESS", ex.ToString());
                    }
                }
            }

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

                    bool isNearlyEnd = duration.TotalSeconds > 0 &&
                        (duration - timeline.Position).TotalMilliseconds < 800;
                    bool isNewTrack = !string.IsNullOrEmpty(info.CurrentTrack) &&
                        !string.IsNullOrEmpty(_lastTrackName) &&
                        info.CurrentTrack != _lastTrackName;

                    TimeSpan chosenPosition;
                    bool forceStartPosition = false;

                    if (isNewTrack && isNearlyEnd)
                    {
                        chosenPosition = TimeSpan.Zero;
                        info.IsIndeterminate = false;
                        forceStartPosition = true;
                    }
                    else
                    {
                        chosenPosition = timeline.Position;
                    }

                    bool isBrowserTimelineTrack = IsBrowserSourceApp(info.SourceAppId) ||
                                                  info.MediaSource == "YouTube" ||
                                                  info.MediaSource == "SoundCloud" ||
                                                  info.MediaSource == "Browser";
                    if (isBrowserTimelineTrack && isNewTrack && info.IsPlaying)
                    {
                        TimeSpan timelineAge = DateTimeOffset.UtcNow - timeline.LastUpdatedTime.ToUniversalTime();
                        if (timelineAge < TimeSpan.Zero) timelineAge = TimeSpan.Zero;

                        bool suspiciousCarryOverPosition =
                            chosenPosition.TotalSeconds > 20 &&
                            (duration.TotalSeconds <= 0 ||
                             chosenPosition.TotalSeconds > duration.TotalSeconds * 0.2);
                        bool staleTimelineAtTrackStart = timelineAge > TimeSpan.FromMilliseconds(900);

                        if (suspiciousCarryOverPosition && staleTimelineAtTrackStart)
                        {
                            chosenPosition = TimeSpan.Zero;
                            forceStartPosition = true;
                        }
                    }

                    var rawTimelineUpdatedUtc = timeline.LastUpdatedTime.ToUniversalTime();
                    var timelineUpdatedUtc = forceStartPosition
                        ? DateTimeOffset.UtcNow
                        : rawTimelineUpdatedUtc;

                    if (!forceStartPosition && info.IsPlaying)
                    {
                        var nowUpdatedUtc = DateTimeOffset.UtcNow;
                        var timelineLatency = nowUpdatedUtc - rawTimelineUpdatedUtc;
                        var maxCompensationWindow = TimeSpan.FromSeconds(15);
                        if (isBrowserTimelineTrack)
                        {
                            if (isInitialOrBigChange)
                            {
                                var durationWindow = duration > TimeSpan.Zero
                                    ? duration + TimeSpan.FromSeconds(5)
                                    : TimeSpan.FromMinutes(10);
                                maxCompensationWindow = durationWindow < TimeSpan.FromHours(4)
                                    ? durationWindow
                                    : TimeSpan.FromHours(4);
                            }
                            else
                            {
                                maxCompensationWindow = TimeSpan.FromMinutes(2);
                            }
                        }

                        bool validTimelineTimestamp =
                            rawTimelineUpdatedUtc > DateTimeOffset.MinValue &&
                            rawTimelineUpdatedUtc <= nowUpdatedUtc.AddMilliseconds(250) &&
                            timelineLatency >= TimeSpan.Zero &&
                            timelineLatency <= maxCompensationWindow;

                        if (validTimelineTimestamp && timelineLatency.TotalMilliseconds > 100)
                        {
                            double playbackRate = info.PlaybackRate > 0 ? info.PlaybackRate : 1.0;
                            var compensatedPosition = chosenPosition + TimeSpan.FromSeconds(timelineLatency.TotalSeconds * playbackRate);

                            // Don't let compensation push position to or past duration
                            if (duration > TimeSpan.Zero)
                            {
                                TimeSpan maxAllowed = TimeSpan.FromSeconds(duration.TotalSeconds * 0.95);
                                if (compensatedPosition > maxAllowed)
                                    compensatedPosition = maxAllowed;
                            }

                            if (compensatedPosition > chosenPosition)
                            {
                                chosenPosition = compensatedPosition;
                                timelineUpdatedUtc = nowUpdatedUtc;
                            }
                        }
                    }

                    if (chosenPosition < TimeSpan.Zero)
                    {
                        chosenPosition = TimeSpan.Zero;
                    }
                    if (duration > TimeSpan.Zero && chosenPosition > duration)
                    {
                        chosenPosition = duration;
                    }

                    info.Position = chosenPosition;
                    info.Duration = duration;
                    info.LastUpdated = timelineUpdatedUtc;

                    if (info.Duration <= TimeSpan.Zero || info.Duration.TotalDays > 30)
                    {
                        info.IsIndeterminate = true;
                    }
                }
                else
                {
                    info.IsIndeterminate = true;
                }
            }
            catch { info.IsIndeterminate = true; }

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

            _lastTrackSignature = currentSignature;
            _lastThumbTrackIdentity = currentTrackOnlyIdentityForThumb;
        }
        catch (Exception ex)
        {
            Log("UpdateError", ex.Message);
        }
    }

    private Task<YouTubeLookupResult?> TryGetYouTubeVideoIdWithInfoAsync(string title, string artist = "", CancellationToken ct = default)
        => _metadataLookup.TryGetYouTubeVideoIdWithInfoAsync(title, artist, ct);

    private async Task<string?> TryGetYouTubeVideoIdAsync(string title, CancellationToken ct = default)
    {
        var res = await TryGetYouTubeVideoIdWithInfoAsync(title, "", ct);
        return res?.Id;
    }

    // ── Mismatch cache: avoid re-validating the same stale videoId during rapid polling ──
    private readonly HashSet<string> _mismatchVideoIds = new(StringComparer.Ordinal);

    private bool TryGetCachedMismatchVideoId(string videoId)
    {
        lock (_mismatchVideoIds) return _mismatchVideoIds.Contains(videoId);
    }

    private void CacheMismatchVideoId(string videoId)
    {
        lock (_mismatchVideoIds) _mismatchVideoIds.Add(videoId);
    }

    private void ClearMismatchCache()
    {
        lock (_mismatchVideoIds) _mismatchVideoIds.Clear();
    }

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
    private readonly Dictionary<string, string> _videoIdCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _videoIdCacheLock = new();

    private void CacheVideoIdForTrack(string? trackIdentity, string videoId)
    {
        if (string.IsNullOrEmpty(trackIdentity)) return;
        lock (_videoIdCacheLock)
        {
            _videoIdCache[trackIdentity] = videoId;
            // Keep cache bounded
            if (_videoIdCache.Count > 50)
            {
                var firstKey = _videoIdCache.Keys.First();
                _videoIdCache.Remove(firstKey);
            }
        }
    }

    private string? GetCachedVideoIdForTrack(string? track)
    {
        if (string.IsNullOrEmpty(track)) return null;
        lock (_videoIdCacheLock)
        {
            // Try exact track name match
            if (_videoIdCache.TryGetValue(track, out var id))
                return id;
            // Try track identity match (track + artist combined key)
            string trackIdentity = BuildTrackIdentity(track, "");
            if (_videoIdCache.TryGetValue(trackIdentity, out id))
                return id;
            return null;
        }
    }

    private void ForgetVideoIdCacheExceptForTrack(string? currentTrackIdentity)
    {
        lock (_videoIdCacheLock)
        {
            if (_videoIdCache.Count == 0) return;
            if (string.IsNullOrEmpty(currentTrackIdentity))
            {
                _videoIdCache.Clear();
                return;
            }

            var doomed = new List<string>();
            foreach (var key in _videoIdCache.Keys)
            {
                if (!string.Equals(key, currentTrackIdentity, StringComparison.OrdinalIgnoreCase))
                {
                    doomed.Add(key);
                }
            }
            foreach (var key in doomed)
            {
                _videoIdCache.Remove(key);
            }
        }
    }

    private void EvictVideoIdCacheEntry(string? track, string staleVideoId)
    {
        if (string.IsNullOrEmpty(track) || string.IsNullOrEmpty(staleVideoId)) return;
        lock (_videoIdCacheLock)
        {
            string trackIdentity = BuildTrackIdentity(track, "");
            if (_videoIdCache.TryGetValue(track, out var v1) &&
                string.Equals(v1, staleVideoId, StringComparison.Ordinal))
            {
                _videoIdCache.Remove(track);
            }
            if (_videoIdCache.TryGetValue(trackIdentity, out var v2) &&
                string.Equals(v2, staleVideoId, StringComparison.Ordinal))
            {
                _videoIdCache.Remove(trackIdentity);
            }
        }
    }

    private bool IsLikelyYouTube(MediaInfo info)
    {
        if (info.MediaSource == "YouTube") return true;

        if (info.MediaSource == "Browser" && !string.IsNullOrEmpty(info.SourceAppId))
        {
            if (TryGetSessionSourceOverride(info, out var sOver) && sOver == "YouTube")
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
        if (info.MediaSource == "Browser" && !string.IsNullOrEmpty(info.CurrentTrack))
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

                    if (string.Equals(DetectPlatformHint(titles), "YouTube", StringComparison.OrdinalIgnoreCase))
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

    private bool ShouldPreserveSoundCloudSourceDuringTrackSwitch(MediaInfo info)
    {
        if (!string.Equals(info.MediaSource, "Browser", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(_lastSource, "SoundCloud", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(info.CurrentTrack) ||
            string.IsNullOrWhiteSpace(info.SourceAppId) ||
            !IsBrowserSourceApp(info.SourceAppId) ||
            string.IsNullOrWhiteSpace(info.SessionInstanceKey))
        {
            return false;
        }

        if (!string.Equals(_lastPublishedSessionInstanceKey, info.SessionInstanceKey, StringComparison.Ordinal))
        {
            return false;
        }

        if ((DateTime.Now - _lastMetadataChangeTime).TotalSeconds > 3.0)
        {
            return false;
        }

        bool hasYouTubeHint = info.CurrentTrack.Contains("youtube", StringComparison.OrdinalIgnoreCase) ||
                              info.CurrentArtist.Contains("youtube", StringComparison.OrdinalIgnoreCase);
        if (hasYouTubeHint)
        {
            return false;
        }

        if (TryGetSessionSourceOverride(info, out var sessionOverride) &&
            !string.IsNullOrEmpty(sessionOverride) &&
            !string.Equals(sessionOverride, "SoundCloud", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
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

        if (_sessionManager != null)
        {
            _sessionManager.CurrentSessionChanged -= OnSessionChanged;
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

