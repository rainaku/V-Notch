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

    
    private readonly Channel<ChangeType> _changeChannel;
    private CancellationTokenSource? _bgCts;
    private Task? _processingTask;
    private Task? _heartbeatTask;
    private DetectionMode _currentMode = DetectionMode.Idle;
    private long _lastEventTimeTicks;
    private DateTime _startupProgressSyncUntilUtc = DateTime.MinValue;

    public event EventHandler<MediaInfo>? MediaChanged;

    private string _lastTrackSignature = "";
    private string _cachedSource = "";
    private BitmapImage? _cachedThumbnail;
    private string _cachedThumbnailSource = "";

    private string _pendingSessionAppId = "";
    private DateTime _pendingSessionStartTime = DateTime.MinValue;


    private CancellationTokenSource? _thumbCts;
    private string _lastStableTrackSignature = "";
    private DateTime _emptyMetadataStartTime = DateTime.MinValue;
    private readonly Dictionary<string, DateTime> _sessionLastPlayingTimes = new();
    private readonly Dictionary<string, DateTime> _sessionPlayStartTimes = new();
    private readonly Dictionary<string, bool> _sessionPlayingStates = new();
    private readonly Dictionary<string, string> _sessionSourceOverrides = new();
    private readonly Dictionary<string, string> _trackSourceCache = new();
    private readonly string _cachePath;
    private string _latestPlayingSessionKey = "";
    private DateTime _latestPlayingSessionStartUtc = DateTime.MinValue;

    private DateTime _lastMetadataChangeTime = DateTime.MinValue;
    private string _lastTrackName = "";
    private TimeSpan _lastObservedPosition = TimeSpan.Zero;
    private DateTime _lastPositionChangeTime = DateTime.MinValue;
    private bool _isThrottled;
    private TimeSpan _recoveredDuration = TimeSpan.Zero;
    private BitmapImage? _recoveredThumbnail;
    private string _lastSoundCloudArtworkIdentity = "";
    private DateTime _lastSoundCloudArtworkAttemptTimeUtc = DateTime.MinValue;
    private string _soundCloudFetchIdentity = "";
    private int _soundCloudFetchGeneration = 0;
    private int _soundCloudFetchInFlight = 0;
    private static readonly TimeSpan SoundCloudArtworkRetryInterval = TimeSpan.FromSeconds(1.1);

    private DateTime _simBaseWallTimeUtc = DateTime.MinValue;
    private TimeSpan _simBasePosition = TimeSpan.Zero;
    private double _simBasePlaybackRate = 1.0;
    private string _simSignature = "";

    public MediaDetectionService(
        IMediaMetadataLookupService metadataLookup,
        IMediaArtworkService artworkService,
        IWindowTitleScanner windowTitleScanner)
    {
        _metadataLookup = metadataLookup;
        _artworkService = artworkService;
        _windowTitleScanner = windowTitleScanner;

        _changeChannel = Channel.CreateBounded<ChangeType>(
            new BoundedChannelOptions(16)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true
            });

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "V-Notch");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        _cachePath = Path.Combine(dir, "source_cache.json");
        LoadSourceCache();
    }

    private void LoadSourceCache()
    {
        try
        {
            if (File.Exists(_cachePath))
            {
                var json = File.ReadAllText(_cachePath);
                var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (data != null)
                {
                    foreach (var kvp in data) _trackSourceCache[kvp.Key] = kvp.Value;
                }
            }
        }
        catch { }
    }

    private void SaveSourceCache()
    {
        try
        {
            var json = JsonSerializer.Serialize(_trackSourceCache);
            File.WriteAllText(_cachePath, json);
        }
        catch { }
    }



    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private GlobalSystemMediaTransportControlsSession? _activeDisplaySession; 
    private DateTime _lastSessionSwitchTime = DateTime.MinValue;
    private string _lastMatchedVolumeSessionId = "";
    private uint _lastMatchedVolumeProcessId;
    private string _lastMatchedVolumeSourceAppId = "";
    private string _cachedVolumeProcessSourceAppId = "";
    private DateTime _cachedVolumeProcessIdsAtUtc = DateTime.MinValue;
    private HashSet<uint> _cachedVolumeProcessIds = new();

    public async void Start()
    {
        _startupProgressSyncUntilUtc = DateTime.UtcNow.AddSeconds(5);

        try
        {
            _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _sessionManager.CurrentSessionChanged += OnSessionChanged;

            await SubscribeToCurrentSession();
        }
        catch (Exception)
        {
#if DEBUG
            System.Diagnostics.Debug.WriteLine("[MediaService] Failed to init SMTC");
#endif
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
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[MediaService] Subscribed to session: {_currentSession.SourceAppUserModelId}");
#endif
            }
        }
        catch (Exception)
        {
#if DEBUG
            System.Diagnostics.Debug.WriteLine("[MediaService] Failed to subscribe to session");
#endif
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
            catch { }
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
#if DEBUG
        System.Diagnostics.Debug.WriteLine($"[MediaService][{tag}] {message}");
#endif
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

            System.Diagnostics.Debug.WriteLine($"[HEARTBEAT] Mode: {_currentMode}, Interval: {interval.TotalSeconds:F1}s");

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
                System.Diagnostics.Debug.WriteLine($"[HEARTBEAT] SKIPPED: Idle mode, no events for {(DateTime.UtcNow - new DateTime(lastEvtTicks)).TotalSeconds:F1}s");
                continue;
            }

            System.Diagnostics.Debug.WriteLine($"[HEARTBEAT] Sending heartbeat");
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
            System.Diagnostics.Debug.WriteLine($"[DETECTION] Mode changed: {oldMode} -> {_currentMode} " +
                $"(IsPlaying: {info.IsAnyMediaPlaying}, Track: '{info.CurrentTrack}', Throttled: {info.IsThrottled})");
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

                                info.CurrentArtist = !string.IsNullOrEmpty(_stableArtist) && (DateTime.Now - _lastSourceConfirmedTime).TotalSeconds < 3.0 ? _stableArtist : "YouTube";
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
                                
                                // Extract track info from window title
                                // Format: "Artist - Track | SoundCloud"
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
                                if (!string.IsNullOrEmpty(info.SourceAppId) && IsBrowserSourceApp(info.SourceAppId))
                                {
                                    _sessionSourceOverrides[info.SourceAppId] = "SoundCloud";
                                }
                                
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

                if (info.Position != _lastObservedPosition)
                {
                    _lastObservedPosition = info.Position;
                    _lastPositionChangeTime = DateTime.Now;
                }

                double progress = info.Duration.TotalSeconds > 0 ? info.Position.TotalSeconds / info.Duration.TotalSeconds : 0;
                bool positionStuck = (DateTime.Now - _lastPositionChangeTime).TotalSeconds > 1.5;
                bool atEndStuck = progress > 0.98 && (DateTime.Now - _lastMetadataChangeTime).TotalSeconds > 1.2;

                if (positionStuck || atEndStuck)
                {
                    bool foundRecovery = false;

                    if (!string.IsNullOrEmpty(info.CurrentTrack) && IsLikelyYouTube(info))
                    {

                        info.MediaSource = "YouTube";
                        info.IsYouTubeRunning = true;

                        ApplySimulatedTimeline(info, atEndStuck);
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
                                    _isThrottled = true;

                                    _recoveredDuration = TimeSpan.Zero;
                                    _recoveredThumbnail = null;
                                    info.Duration = TimeSpan.Zero;

                                    info.Position = TimeSpan.FromSeconds(1.5);
                                    info.LastUpdated = DateTimeOffset.Now;
                                    foundRecovery = true;
                                    break;
                                }
                                else if (positionStuck || _isThrottled)
                                {

                                    info.CurrentTrack = trackName;
                                    info.CurrentArtist = artistName;
                                    info.MediaSource = "YouTube";
                                    info.IsYouTubeRunning = true;
                                    info.IsThrottled = true;
                                    _isThrottled = true;

                                    if (_recoveredDuration.TotalSeconds > 0)
                                    {
                                        info.Duration = _recoveredDuration;
                                    }
                                    else
                                    {

                                        info.Duration = TimeSpan.Zero;
                                    }

                                    if (_recoveredThumbnail != null) info.Thumbnail = _recoveredThumbnail;

                                    var timeOnTrack = DateTime.Now - _lastMetadataChangeTime;
                                    info.Position = timeOnTrack;
                                    info.LastUpdated = DateTimeOffset.Now;
                                    foundRecovery = true;
                                    break;
                                }
                            }
                        }
                    }

                    if (!foundRecovery && _isThrottled)
                    {

                        if ((DateTime.Now - _lastPositionChangeTime).TotalSeconds > 3.5)
                        {
                            _isThrottled = false;
                            _recoveredDuration = TimeSpan.Zero;
                            _recoveredThumbnail = null;

                            _simBaseWallTimeUtc = DateTime.MinValue;
                            _simBasePosition = TimeSpan.Zero;
                            _simSignature = "";
                        }
                    }
                }
                else if (_isThrottled)
                {

                    if ((DateTime.Now - _lastPositionChangeTime).TotalMilliseconds < 500)
                    {
                        _isThrottled = false;
                        _recoveredDuration = TimeSpan.Zero;
                        _recoveredThumbnail = null;

                        _simBaseWallTimeUtc = DateTime.MinValue;
                        _simBasePosition = TimeSpan.Zero;
                        _simSignature = "";
                    }
                }
            }
            else
            {
                _isThrottled = false;
                _recoveredDuration = TimeSpan.Zero;
                _recoveredThumbnail = null;

                _simBaseWallTimeUtc = DateTime.MinValue;
                _simBasePosition = TimeSpan.Zero;
                _simSignature = "";
            }

            if (info.CurrentTrack != _lastTrackName)
            {
                _lastTrackName = info.CurrentTrack;
                _lastMetadataChangeTime = DateTime.Now;

                if (_isThrottled) _isThrottled = false;
            }

            if (ShouldPreserveSoundCloudSourceDuringTrackSwitch(info))
            {
                info.MediaSource = "SoundCloud";
                info.IsSoundCloudRunning = true;
                if (!string.IsNullOrEmpty(info.SourceAppId) && IsBrowserSourceApp(info.SourceAppId))
                {
                    _sessionSourceOverrides[info.SourceAppId] = "SoundCloud";
                }
            }

            info.IsThrottled = _isThrottled;
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

                
                UpdateDetectionMode(info);

                
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[MEDIA] Firing MediaChanged event: Track='{info.CurrentTrack}', " +
                        $"Pos={info.Position.TotalSeconds:F1}s, IsPlaying={info.IsPlaying}");
                    await dispatcher.InvokeAsync(() => MediaChanged?.Invoke(this, info));
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[MEDIA] WARNING: No dispatcher, cannot fire MediaChanged event!");
                }

                
                
                if (!forceRefresh && metadataChanged && !string.IsNullOrEmpty(info.CurrentTrack))
                {
                    _changeChannel.Writer.TryWrite(ChangeType.ForceRefresh);
                }
            }

            bool isPotentialYouTube = info.MediaSource == "YouTube" || (info.MediaSource == "Browser" && IsLikelyYouTube(info));
            bool hasSoundCloudSessionOverride = !string.IsNullOrEmpty(info.SourceAppId) &&
                                                _sessionSourceOverrides.TryGetValue(info.SourceAppId, out var sourceOverride) &&
                                                string.Equals(sourceOverride, "SoundCloud", StringComparison.OrdinalIgnoreCase);
            
            // Improved SoundCloud detection from Browser source
            // Trigger if: Browser source + not YouTube + has track + (no thumbnail OR placeholder OR session override)
            bool shouldProbeSoundCloudFromBrowser = info.MediaSource == "Browser" &&
                                                    !IsLikelyYouTube(info) &&
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
            if (_isThrottled && _recoveredThumbnail != null && !forceFetchForTrackChange) needsFetch = false;

            if (isPotentialYouTube && !string.IsNullOrEmpty(info.CurrentTrack) && needsFetch)
            {

                _thumbCts?.Cancel();
                _thumbCts = new CancellationTokenSource();
                var token = _thumbCts.Token;
                bool shouldForceThumbFetch = forceFetchForTrackChange;

                string trackDuringFetch = info.CurrentTrack;
                string artistDuringFetch = info.CurrentArtist;
                string sourceAppDuringFetch = info.SourceAppId ?? "";

                bool isBrowserIcon = info.Thumbnail != null && info.MediaSource == "Browser";
                bool needsBetterThumb = shouldForceThumbFetch || info.Thumbnail == null || info.Thumbnail.PixelWidth < 120 || isBrowserIcon;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        string? videoId = shouldForceThumbFetch ? null : info.YouTubeVideoId;
                        int retryCount = 0;

                        while (retryCount < 3 && !token.IsCancellationRequested)
                        {
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

                                        if (!string.IsNullOrEmpty(info.SourceAppId) && IsBrowserSourceApp(info.SourceAppId))
                                        {
                                            _sessionSourceOverrides[info.SourceAppId] = "YouTube";
                                        }

                                        _trackSourceCache[trackDuringFetch] = "YouTube";
                                        _trackSourceCache[BuildTrackIdentity(trackDuringFetch, artistDuringFetch)] = "YouTube";
                                        SaveSourceCache();
                                    }

                                    if (!string.IsNullOrEmpty(result.Author) && result.Author != "YouTube")
                                    {
                                        info.CurrentArtist = result.Author;
                                        _stableArtist = result.Author;
                                    }

                                    if (result.Duration.TotalSeconds > 0)
                                    {
                                        _recoveredDuration = result.Duration;
                                        info.Duration = result.Duration;
                                    }
                                }
                            }

                            if (!string.IsNullOrEmpty(videoId) && !token.IsCancellationRequested)
                            {
                                info.YouTubeVideoId = videoId;

                                if (needsBetterThumb || info.MediaSource == "YouTube") 
                                {
                                    string thumbnailUrl = $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";
                                    var frameBitmap = await DownloadImageAsync(thumbnailUrl);

                                    if (frameBitmap == null)
                                    {
                                        thumbnailUrl = $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg";
                                        frameBitmap = await DownloadImageAsync(thumbnailUrl);
                                    }

                                    if (frameBitmap != null && !token.IsCancellationRequested)
                                    {
                                        if (IsStillSamePublishedTrack(trackDuringFetch, artistDuringFetch, sourceAppDuringFetch))
                                        {
                                            frameBitmap = CropToSquare(frameBitmap, "YouTube") ?? frameBitmap;
                                            _recoveredThumbnail = frameBitmap;
                                            _cachedThumbnail = frameBitmap;
                                            _cachedThumbnailSource = "YouTube";
                                            info.Thumbnail = frameBitmap;

                                            var dispatcher = System.Windows.Application.Current?.Dispatcher;
                                            if (dispatcher != null)
                                            {
                                                await dispatcher.InvokeAsync(() =>
                                                {
                                                    if (!token.IsCancellationRequested &&
                                                        IsStillSamePublishedTrack(trackDuringFetch, artistDuringFetch, sourceAppDuringFetch))
                                                    {
                                                        _lastTrackSignature = info.GetSignature();
                                                        MediaChanged?.Invoke(this, info);
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
                                IsStillSamePublishedTrack(trackDuringFetch, artistDuringFetch, sourceAppDuringFetch))
                            {
                                await Task.Delay(retryCount * 350, token);
                                videoId = null;
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch { }
                }, token);
            }
            else if (isPotentialSoundCloud && !string.IsNullOrEmpty(info.CurrentTrack))
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
                    
                    bool requireStrongMatch = true;

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var artworkUrl = await TryGetSoundCloudArtworkUrlAsync(trackDuringFetch, artistDuringFetch, requireStrongMatch, token);
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

                            if (!IsStillSamePublishedTrack(trackDuringFetch, artistDuringFetch, sourceAppDuringFetch))
                            {
                                return;
                            }

                            frameBitmap = CropToSquare(frameBitmap, "SoundCloud") ?? frameBitmap;
                            _recoveredThumbnail = frameBitmap;
                            _cachedThumbnail = frameBitmap;
                            _cachedThumbnailSource = "SoundCloud";
                            if (info.MediaSource == "Browser")
                            {
                                info.MediaSource = "SoundCloud";
                                info.IsSoundCloudRunning = true;
                                if (!string.IsNullOrEmpty(info.SourceAppId) && IsBrowserSourceApp(info.SourceAppId))
                                {
                                    _sessionSourceOverrides[info.SourceAppId] = "SoundCloud";
                                }

                                string currentTrackIdentity = BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist);
                                string currentTrackOnlyIdentity = BuildTrackIdentity(info.CurrentTrack, "");
                                if (!string.IsNullOrEmpty(currentTrackIdentity))
                                {
                                    _trackSourceCache[currentTrackIdentity] = "SoundCloud";
                                    _trackSourceCache[currentTrackOnlyIdentity] = "SoundCloud";
                                    SaveSourceCache();
                                }
                            }
                            info.Thumbnail = frameBitmap;

                            var dispatcher = System.Windows.Application.Current?.Dispatcher;
                            if (dispatcher != null)
                            {
                                await dispatcher.InvokeAsync(() =>
                                {
                                    if (!token.IsCancellationRequested &&
                                        IsStillSamePublishedTrack(trackDuringFetch, artistDuringFetch, sourceAppDuringFetch))
                                    {
                                        _lastTrackSignature = info.GetSignature();
                                        MediaChanged?.Invoke(this, info);
                                    }
                                });
                            }
                        }
                        catch (OperationCanceledException) { }
                        catch { }
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
        catch { }
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

    private bool IsStillSamePublishedTrack(string expectedTrack, string expectedArtist, string expectedSourceAppId)
    {
        if (!string.IsNullOrEmpty(expectedSourceAppId) &&
            !string.Equals(_lastPublishedSourceAppId, expectedSourceAppId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string expectedIdentity = BuildTrackIdentity(expectedTrack, expectedArtist);
        string expectedTrackOnly = BuildTrackIdentity(expectedTrack, "");
        return string.Equals(_lastPublishedTrackIdentity, expectedIdentity, StringComparison.Ordinal) ||
               string.Equals(_lastPublishedTrackOnlyIdentity, expectedTrackOnly, StringComparison.Ordinal);
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
        return sourceAppId.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ||
               sourceAppId.Contains("Edge", StringComparison.OrdinalIgnoreCase) ||
               sourceAppId.Contains("Firefox", StringComparison.OrdinalIgnoreCase) ||
               sourceAppId.Contains("MS-Edge", StringComparison.OrdinalIgnoreCase) ||
               sourceAppId.Contains("msedge", StringComparison.OrdinalIgnoreCase) ||
               sourceAppId.Contains("Opera", StringComparison.OrdinalIgnoreCase) ||
               sourceAppId.Contains("Brave", StringComparison.OrdinalIgnoreCase) ||
               sourceAppId.Contains("Vivaldi", StringComparison.OrdinalIgnoreCase) ||
               sourceAppId.Contains("Coccoc", StringComparison.OrdinalIgnoreCase) ||
               sourceAppId.Contains("Arc", StringComparison.OrdinalIgnoreCase) ||
               sourceAppId.Contains("Sidekick", StringComparison.OrdinalIgnoreCase) ||
               sourceAppId.Contains("Browser", StringComparison.OrdinalIgnoreCase);
    }

    private static string DetectPlatformHint(IEnumerable<string> windowTitles)
    {
        foreach (var title in windowTitles)
        {
            var lower = title.ToLower();
            if (lower.Contains("soundcloud")) return "SoundCloud";
            if (lower.Contains("youtube") && !lower.StartsWith("youtube -") && lower != "youtube") return "YouTube";
            if (lower.Contains("apple music") || lower.Contains("music.apple.com")) return "Apple Music";
            if (lower.Contains("facebook") && (lower.Contains("watch") || lower.Contains("video"))) return "Facebook";
            if (lower.Contains("tiktok") && lower.Contains(" | ")) return "TikTok";
            if (lower.Contains("instagram") && (lower.Contains("reel") || lower.Contains("video"))) return "Instagram";
            if ((lower.Contains("twitter") || lower.Contains(" / x")) && (lower.Contains("video") || lower.Contains("watch"))) return "Twitter";
        }

        return "";
    }

    private static string NormalizeForLooseMatch(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string folded = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(folded.Length);
        bool lastWasSpace = false;

        foreach (var ch in folded)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }

        if (sb.Length > 0 && sb[sb.Length - 1] == ' ')
        {
            sb.Length--;
        }

        return sb.ToString();
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
                    if (string.IsNullOrEmpty(osCurrentId) || osCurrentId == _activeDisplaySession.SourceAppUserModelId)
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
                catch { }
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
                        catch { }
                    }

                    foreach (var s in sessions)
                    {
                        try
                        {
                            var sourceApp = s.SourceAppUserModelId ?? "";
                            string sessionInstanceKey = BuildSessionInstanceKey(s);
                            var playbackInfo = s.GetPlaybackInfo();
                            var status = playbackInfo.PlaybackStatus;
                            var nowUtc = DateTime.UtcNow;

                            bool isActive = IsSessionPlayingStatus(status);
                            bool isPrevActive = ReferenceEquals(_activeDisplaySession, s);
                            bool wasPlaying = _sessionPlayingStates.TryGetValue(sessionInstanceKey, out var prevPlaying) && prevPlaying;

                            if (isActive && !wasPlaying)
                            {
                                _sessionPlayStartTimes[sessionInstanceKey] = nowUtc;
                                _latestPlayingSessionKey = sessionInstanceKey;
                                _latestPlayingSessionStartUtc = nowUtc;

                                if (IsBrowserSourceApp(sourceApp))
                                {
                                    _sessionSourceOverrides.Remove(sourceApp);
                                }

                                _lastTrackSignature = "";
                                _cachedThumbnail = null;
                                _cachedThumbnailSource = "";
                            }

                            _sessionPlayingStates[sessionInstanceKey] = isActive;

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
                                score += 1000;
                            }

                            if (isActive)
                            {
                                score += 500;
                                if (osCurrentId == sourceApp) score += 1000;
                                _sessionLastPlayingTimes[sourceApp] = DateTime.Now;
                            }

                            if (isActive && _sessionPlayStartTimes.TryGetValue(sessionInstanceKey, out var playStartUtc))
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

                            if (_sessionLastPlayingTimes.TryGetValue(sourceApp, out var lastPlaying))
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
                            catch { }

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
                        catch { }
                    }
                }
                catch { }

                if (hasAnyActiveSession && bestSession != null)
                {
                    try
                    {
                        if (!IsSessionPlayingStatus(bestSession.GetPlaybackInfo().PlaybackStatus))
                        {
                            bestSession = fallbackActiveSession ?? bestSession;
                        }
                    }
                    catch { }
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
                    catch { }

                    
                    
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
                                    System.Diagnostics.Debug.WriteLine($"[DETECTION] Switching session due to fresh timeline: " +
                                        $"New={timelineAge:F1}s old, Current={currentTimelineAge:F1}s old");
                                    hasFreshTimeline = true;
                                }
                            }
                        }
                    }
                    catch { }

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
                        catch { }

                        
                        
                        
                        
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
                    catch { }
                }

                _activeDisplaySession = session;

                try
                {
                    _activeDisplaySession.MediaPropertiesChanged += OnMediaPropertiesChanged;
                    _activeDisplaySession.PlaybackInfoChanged += OnPlaybackChanged;
                    _activeDisplaySession.TimelinePropertiesChanged += OnTimelineChanged;
                }
                catch { }

                _lastTrackSignature = "";
            }

            var sessionSourceApp = session.SourceAppUserModelId ?? "";
            info.IsAnyMediaPlaying = true; 
            info.SourceAppId = sessionSourceApp; 

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
                !string.IsNullOrEmpty(_stableArtist) && (DateTime.Now - _lastSourceConfirmedTime).TotalSeconds < 5.0)
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
            bool trackChangedForThisPass = currentSignature != _lastTrackSignature;

            if (trackChangedForThisPass)
            {
                _cachedThumbnail = null;
                _cachedThumbnailSource = "";
            }

            if (info.MediaSource == "Browser" || string.IsNullOrEmpty(info.MediaSource))
            {

                bool isSameSession = _activeDisplaySession != null && session != null &&
                                   _activeDisplaySession.SourceAppUserModelId == session.SourceAppUserModelId;
                string currentTrackIdentity = BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist);
                string trackOnlyIdentity = BuildTrackIdentity(info.CurrentTrack, "");
                bool sameTrackAsStable = !string.IsNullOrEmpty(currentTrackIdentity) &&
                                         currentTrackIdentity == _stableSourceTrackIdentity;
                List<string>? hintedWindowTitles = null;
                string hintedSource = "";
                bool sourceFromBrowserOverride = false;
                bool sourceFromTrackCache = false;

                if (!string.IsNullOrEmpty(sessionSourceApp) &&
                    IsBrowserSourceApp(sessionSourceApp) &&
                    _sessionSourceOverrides.TryGetValue(sessionSourceApp, out var sessionOverride) &&
                    string.Equals(sessionOverride, "SoundCloud", StringComparison.OrdinalIgnoreCase))
                {
                    info.MediaSource = "SoundCloud";
                    info.IsSoundCloudRunning = true;
                    sourceFromBrowserOverride = true;
                }

                if ((info.MediaSource == "Browser" || string.IsNullOrEmpty(info.MediaSource)) &&
                    !string.IsNullOrEmpty(info.CurrentTrack))
                {
                    bool hasCachedSource = _trackSourceCache.TryGetValue(currentTrackIdentity, out var cachedSource) ||
                                           _trackSourceCache.TryGetValue(trackOnlyIdentity, out cachedSource);

                    if (hasCachedSource && string.Equals(cachedSource, "SoundCloud", StringComparison.OrdinalIgnoreCase))
                    {
                        info.MediaSource = "SoundCloud";
                        info.IsSoundCloudRunning = true;
                        sourceFromTrackCache = true;
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

                    foreach (var title in windowTitles)
                    {
                        var winTitleLower = title.ToLower();
                        bool trackMatch = winTitleLower.Contains(trackTitleLower);

                        if (!trackMatch && !string.IsNullOrEmpty(trackTitleNormalized))
                        {
                            var winTitleNormalized = NormalizeForLooseMatch(winTitleLower);
                            trackMatch = winTitleNormalized.Contains(trackTitleNormalized, StringComparison.Ordinal);
                        }

                        if (hasTrack && !trackMatch)
                        {
                            bool hasPlatformHint = winTitleLower.Contains("youtube") ||
                                                   winTitleLower.Contains("soundcloud") ||
                                                   winTitleLower.Contains("apple music") ||
                                                   winTitleLower.Contains("music.apple.com") ||
                                                   winTitleLower.Contains("facebook") ||
                                                   winTitleLower.Contains("tiktok") ||
                                                   winTitleLower.Contains("instagram") ||
                                                   winTitleLower.Contains("twitter") ||
                                                   winTitleLower.Contains(" / x");
                            if (!hasPlatformHint)
                                continue;
                        }

                        if (winTitleLower.Contains("youtube") && !winTitleLower.StartsWith("youtube -") && winTitleLower != "youtube")
                        {
                            info.MediaSource = "YouTube";
                            info.IsYouTubeRunning = true;
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
                _sessionSourceOverrides[sessionSourceApp] = info.MediaSource;
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
                    bool cacheChanged = !_trackSourceCache.TryGetValue(_stableSourceTrackIdentity, out var cachedSource) ||
                                        !string.Equals(cachedSource, info.MediaSource, StringComparison.Ordinal) ||
                                        !_trackSourceCache.TryGetValue(trackOnlyIdentity, out var cachedTrackOnlySource) ||
                                        !string.Equals(cachedTrackOnlySource, info.MediaSource, StringComparison.Ordinal);
                    if (cacheChanged)
                    {
                        _trackSourceCache[_stableSourceTrackIdentity] = info.MediaSource;
                        _trackSourceCache[trackOnlyIdentity] = info.MediaSource;
                        if (info.MediaSource == "YouTube" || info.MediaSource == "SoundCloud")
                        {
                            SaveSourceCache();
                        }
                    }
                }
            }
            else
            {
                _cachedSource = "Browser";
            }

            if (!forceRefresh && currentSignature == _lastTrackSignature && _cachedThumbnail != null)
            {
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
                                                                            !hasVerifiedSoundCloudThumb;
                                if (skipSmtcThumbForFreshSoundCloudTrack)
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
                                    bool isGenericIcon = info.MediaSource == "YouTube" || info.MediaSource == "Browser" || isLikelySoundCloudPlaceholder;

                                    if (!(isSquare && isGenericIcon))
                                    {
                                        newBitmap = CropToSquare(newBitmap, info.MediaSource) ?? newBitmap;
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
                    catch { }
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

                    bool isSoundCloudTrack = string.Equals(info.MediaSource, "SoundCloud", StringComparison.OrdinalIgnoreCase);
                    if (isSoundCloudTrack && isNewTrack && info.IsPlaying)
                    {
                        TimeSpan timelineAge = DateTimeOffset.UtcNow - timeline.LastUpdatedTime.ToUniversalTime();
                        if (timelineAge < TimeSpan.Zero) timelineAge = TimeSpan.Zero;

                        bool suspiciousCarryOverPosition =
                            chosenPosition.TotalSeconds > 20 &&
                            (duration.TotalSeconds <= 0 ||
                             chosenPosition.TotalSeconds > duration.TotalSeconds * 0.2);
                        bool staleTimelineAtTrackStart = timelineAge > TimeSpan.FromMilliseconds(900);

                        if (suspiciousCarryOverPosition || staleTimelineAtTrackStart)
                        {
                            chosenPosition = TimeSpan.Zero;
                            forceStartPosition = true;
                        }
                    }


                    var timelineUpdatedUtc = timeline.LastUpdatedTime.ToUniversalTime();

                    // FIX: Don't compensate for latency - let ProgressEngine handle prediction
                    // Using raw timeline data ensures timestamp matches position
                    // ProgressEngine will predict forward from this base point accurately
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

    private Task<string?> TryGetSoundCloudArtworkUrlAsync(string title, string artist = "", bool requireStrongMatch = false, CancellationToken ct = default)
        => _metadataLookup.TryGetSoundCloudArtworkUrlAsync(title, artist, requireStrongMatch, ct);

    private Task<BitmapImage?> DownloadImageAsync(string url, CancellationToken ct = default)
        => _artworkService.DownloadImageAsync(url, ct);

    private BitmapImage? CropToSquare(BitmapImage source, string mediaSource)
        => _artworkService.CropToSquare(source, mediaSource);

    private Task<BitmapImage?> ConvertToWpfBitmapAsync(Windows.Storage.Streams.IRandomAccessStreamWithContentType stream, CancellationToken ct = default)
        => _artworkService.ConvertToWpfBitmapAsync(stream, ct);

    private List<string> GetAllWindowTitles()
        => _windowTitleScanner.GetAllWindowTitles(_isThrottled);

    private bool IsLikelyYouTube(MediaInfo info)
    {
        if (info.MediaSource == "YouTube") return true;

        
        if (info.MediaSource == "Browser" && !string.IsNullOrEmpty(info.SourceAppId))
        {
            if (_sessionSourceOverrides.TryGetValue(info.SourceAppId, out var sOver) && sOver == "YouTube")
            {
                if (string.IsNullOrEmpty(info.CurrentTrack))
                {
                    return true;
                }

                string trackIdentity = BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist);
                string trackOnlyIdentity = BuildTrackIdentity(info.CurrentTrack, "");
                bool hasYouTubeTrackCache = (_trackSourceCache.TryGetValue(trackIdentity, out var cachedSource) &&
                                             string.Equals(cachedSource, "YouTube", StringComparison.OrdinalIgnoreCase)) ||
                                            (_trackSourceCache.TryGetValue(trackOnlyIdentity, out cachedSource) &&
                                             string.Equals(cachedSource, "YouTube", StringComparison.OrdinalIgnoreCase));
                if (hasYouTubeTrackCache)
                {
                    return true;
                }
            }
        }

        
        if (!string.IsNullOrEmpty(info.CurrentArtist) &&
            info.CurrentArtist.Equals("YouTube", StringComparison.OrdinalIgnoreCase))
            return true;

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
            !IsBrowserSourceApp(info.SourceAppId))
        {
            return false;
        }

        if (!string.Equals(_lastPublishedSourceAppId, info.SourceAppId, StringComparison.OrdinalIgnoreCase))
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

        if (_sessionSourceOverrides.TryGetValue(info.SourceAppId, out var sessionOverride) &&
            !string.IsNullOrEmpty(sessionOverride) &&
            !string.Equals(sessionOverride, "SoundCloud", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private void ApplySimulatedTimeline(MediaInfo info, bool atEndStuck)
    {
        var nowUtc = DateTime.UtcNow;

        
        var sig = info.GetSignature();
        if (_simSignature != sig || _simBaseWallTimeUtc == DateTime.MinValue)
        {
            _simSignature = sig;
            _simBaseWallTimeUtc = nowUtc;

            
            _simBasePosition = _lastObservedPosition != TimeSpan.Zero ? _lastObservedPosition : info.Position;

            _simBasePlaybackRate = info.PlaybackRate > 0 ? info.PlaybackRate : 1.0;
        }

        var elapsed = nowUtc - _simBaseWallTimeUtc;
        var sim = _simBasePosition + TimeSpan.FromSeconds(elapsed.TotalSeconds * _simBasePlaybackRate);

        
        if (!atEndStuck && info.Duration > TimeSpan.Zero && sim > info.Duration)
            sim = info.Duration;

        info.Position = sim;

        info.IsThrottled = true;
        _isThrottled = true;

        
        if (atEndStuck)
        {
            if (_recoveredDuration > TimeSpan.Zero) info.Duration = _recoveredDuration;
            else info.Duration = TimeSpan.Zero;
        }
        else
        {
            if (info.Duration <= TimeSpan.Zero && _recoveredDuration > TimeSpan.Zero)
                info.Duration = _recoveredDuration;
        }

        if (info.Thumbnail == null && _recoveredThumbnail != null)
            info.Thumbnail = _recoveredThumbnail;

        info.LastUpdated = DateTimeOffset.Now;
    }

    private void ParseSpotifyTitle(string title, MediaInfo info)
    {
        var parts = title.Split(" - ", 2);
        if (parts.Length == 2)
        {
            info.CurrentArtist = parts[0].Trim();
            info.CurrentTrack = parts[1].Trim();
        }
        else
        {
            info.CurrentTrack = title;
            info.CurrentArtist = "Spotify";
        }
    }

    private string ExtractVideoTitle(string windowTitle, string platform)
    {
        var title = windowTitle;

        
        
        title = Regex.Replace(title, @"^\(\d+\+?\)\s*", "");
        title = Regex.Replace(title, @"^[▶⏸▶️⏸️\s]*", "");

        
        title = Regex.Replace(title, @"^[▶⏸\s\d:]+\|", "").Trim();

        
        var separators = new[] {
            " - YouTube", " – YouTube", " - SoundCloud", " | Facebook",
            " - TikTok", " / X", " | TikTok", " • Instagram",
            " - Apple Music", " – Apple Music",
            " - Google Chrome", " - Microsoft​ Edge", " - Microsoft Edge",
            " - Mozilla Firefox", " - Opera", " - Brave", " - Cốc Cốc",
            " - Browser", " – Current browser"
        };

        foreach (var sep in separators)
        {
            int idx = title.IndexOf(sep, StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                title = title.Substring(0, idx);
            }
        }

        title = title.Trim();

        
        title = Regex.Replace(title, @"\s+[\-\|–•]\s*$", "");

        if (title.Length > 60)
        {
            title = title.Substring(0, 57) + "...";
        }

        return string.IsNullOrEmpty(title) ? platform : title;
    }

    public bool TryGetCurrentSessionVolume(out float volume, out bool isMuted)
    {
        float resolvedVolume = 0f;
        bool resolvedMuted = false;

        bool success = TryWithCurrentAudioSession(session =>
        {
            using var simpleVolume = session.SimpleAudioVolume;
            resolvedVolume = Math.Clamp(simpleVolume.Volume, 0f, 1f);
            resolvedMuted = simpleVolume.Mute;
            return true;
        });

        volume = resolvedVolume;
        isMuted = resolvedMuted;
        return success;
    }

    public bool TrySetCurrentSessionVolume(float volume)
    {
        float target = Math.Clamp(volume, 0f, 1f);

        return TryWithCurrentAudioSession(session =>
        {
            using var simpleVolume = session.SimpleAudioVolume;
            simpleVolume.Volume = target;
            if (target > 0.001f && simpleVolume.Mute)
            {
                simpleVolume.Mute = false;
            }
            return true;
        });
    }

    public bool TryToggleCurrentSessionMute()
    {
        return TryWithCurrentAudioSession(session =>
        {
            using var simpleVolume = session.SimpleAudioVolume;
            simpleVolume.Mute = !simpleVolume.Mute;
            return true;
        });
    }

    private bool TryWithCurrentAudioSession(Func<AudioSessionControl, bool> action)
    {
        string sourceAppId = GetActiveSourceAppId();
        if (string.IsNullOrWhiteSpace(sourceAppId))
        {
            return false;
        }

        try
        {
            using var deviceEnumerator = new MMDeviceEnumerator();
            using var defaultDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = defaultDevice.AudioSessionManager.Sessions;
            if (sessions == null || sessions.Count == 0)
            {
                return false;
            }

            var candidateProcessNames = GetProcessNameCandidates(sourceAppId);
            var candidateProcessIds = GetCachedProcessIdsForSourceApp(sourceAppId, candidateProcessNames);
            AudioSessionControl? targetSession = null;
            double bestScore = double.MinValue;

            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (session == null || session.IsSystemSoundsSession)
                {
                    continue;
                }

                uint processId = session.GetProcessID;
                bool matchedByProcess = candidateProcessIds.Contains(processId);
                bool matchedByMetadata = SessionMatchesSourceAppId(session, sourceAppId);
                if (!matchedByProcess && !matchedByMetadata)
                {
                    continue;
                }

                double score = 0;
                if (matchedByProcess) score += 1000;
                if (matchedByMetadata) score += 200;

                if (string.Equals(sourceAppId, _lastMatchedVolumeSourceAppId, StringComparison.OrdinalIgnoreCase))
                {
                    if (processId != 0 && processId == _lastMatchedVolumeProcessId) score += 300;
                    if (string.Equals(session.GetSessionIdentifier, _lastMatchedVolumeSessionId, StringComparison.OrdinalIgnoreCase)) score += 400;
                }

                try
                {
                    score += session.AudioMeterInformation.MasterPeakValue * 100;
                }
                catch { }

                if (score > bestScore)
                {
                    bestScore = score;
                    targetSession = session;
                }
            }

            if (targetSession == null)
            {
                return false;
            }

            _lastMatchedVolumeSourceAppId = sourceAppId;
            _lastMatchedVolumeProcessId = targetSession.GetProcessID;
            _lastMatchedVolumeSessionId = targetSession.GetSessionIdentifier ?? "";

            return action(targetSession);
        }
        catch
        {
            return false;
        }
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

    private static bool SessionMatchesSourceAppId(AudioSessionControl session, string sourceAppId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppId))
        {
            return false;
        }

        return ContainsEitherWay(session.DisplayName, sourceAppId) ||
               ContainsEitherWay(session.GetSessionIdentifier, sourceAppId) ||
               ContainsEitherWay(session.GetSessionInstanceIdentifier, sourceAppId);
    }

    private static bool ContainsEitherWay(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return left.Contains(right, StringComparison.OrdinalIgnoreCase) ||
               right.Contains(left, StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> GetProcessNameCandidates(string sourceAppId)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(sourceAppId))
        {
            return candidates;
        }

        foreach (Match match in Regex.Matches(sourceAppId, @"([A-Za-z0-9_\-]+)\.exe", RegexOptions.IgnoreCase))
        {
            string processName = match.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(processName))
            {
                candidates.Add(processName);
            }
        }

        AddProcessAliasIfContains(sourceAppId, candidates, "spotify", "Spotify");
        AddProcessAliasIfContains(sourceAppId, candidates, "msedge", "msedge");
        AddProcessAliasIfContains(sourceAppId, candidates, "edge", "msedge");
        AddProcessAliasIfContains(sourceAppId, candidates, "chrome", "chrome");
        AddProcessAliasIfContains(sourceAppId, candidates, "firefox", "firefox");
        AddProcessAliasIfContains(sourceAppId, candidates, "opera", "opera");
        AddProcessAliasIfContains(sourceAppId, candidates, "brave", "brave");
        AddProcessAliasIfContains(sourceAppId, candidates, "vivaldi", "vivaldi");
        AddProcessAliasIfContains(sourceAppId, candidates, "coccoc", "browser");
        AddProcessAliasIfContains(sourceAppId, candidates, "arc", "arc");
        AddProcessAliasIfContains(sourceAppId, candidates, "sidekick", "sidekick");
        AddProcessAliasIfContains(sourceAppId, candidates, "applemusic", "AppleMusic");
        AddProcessAliasIfContains(sourceAppId, candidates, "apple music", "AppleMusic");

        return candidates;
    }

    private static void AddProcessAliasIfContains(string sourceAppId, ISet<string> candidates, string token, string processName)
    {
        if (sourceAppId.Contains(token, StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(processName);
        }
    }

    private HashSet<uint> GetCachedProcessIdsForSourceApp(string sourceAppId, IEnumerable<string> processNames)
    {
        bool canUseCache =
            string.Equals(sourceAppId, _cachedVolumeProcessSourceAppId, StringComparison.OrdinalIgnoreCase) &&
            (DateTime.UtcNow - _cachedVolumeProcessIdsAtUtc).TotalMilliseconds < 1200;

        if (canUseCache)
        {
            return _cachedVolumeProcessIds;
        }

        _cachedVolumeProcessSourceAppId = sourceAppId;
        _cachedVolumeProcessIds = GetProcessIds(processNames);
        _cachedVolumeProcessIdsAtUtc = DateTime.UtcNow;
        return _cachedVolumeProcessIds;
    }

    private static HashSet<uint> GetProcessIds(IEnumerable<string> processNames)
    {
        var processIds = new HashSet<uint>();

        foreach (string processName in processNames)
        {
            if (string.IsNullOrWhiteSpace(processName))
            {
                continue;
            }

            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch
            {
                continue;
            }

            foreach (var process in processes)
            {
                try
                {
                    processIds.Add((uint)process.Id);
                }
                catch { }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return processIds;
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

    public async Task PlayPauseAsync()
    {
        if (_sessionManager == null) return;

        try
        {

            var session = _activeDisplaySession ?? _sessionManager.GetCurrentSession();
            if (session != null)
            {
                await session.TryTogglePlayPauseAsync();
            }
        }
        catch { }
    }

    public async Task NextTrackAsync()
    {
        if (_sessionManager == null) return;

        try
        {

            var session = _activeDisplaySession ?? _sessionManager.GetCurrentSession();
            if (session != null)
            {
                await session.TrySkipNextAsync();
            }
        }
        catch { }
    }

    public async Task PreviousTrackAsync()
    {
        if (_sessionManager == null) return;

        try
        {

            var session = _activeDisplaySession ?? _sessionManager.GetCurrentSession();
            if (session != null)
            {
                await session.TrySkipPreviousAsync();
            }
        }
        catch { }
    }

    public async Task SeekAsync(TimeSpan position)
    {
        if (_sessionManager == null) return;

        try
        {

            var session = _activeDisplaySession ?? _sessionManager.GetCurrentSession();
            if (session != null)
            {
                await session.TryChangePlaybackPositionAsync(position.Ticks);
            }
        }
        catch { }
    }

    public async Task SeekRelativeAsync(double seconds)
    {
        if (_sessionManager == null) return;

        try
        {
            var session = _activeDisplaySession ?? _sessionManager.GetCurrentSession();
            if (session != null)
            {
                var timeline = session.GetTimelineProperties();
                if (timeline != null)
                {
                    var playbackInfo = session.GetPlaybackInfo();
                    bool isPlaying = playbackInfo?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                    var timeSinceUpdate = DateTimeOffset.Now - timeline.LastUpdatedTime;
                    var currentPos = timeline.Position;

                    if (isPlaying && timeSinceUpdate > TimeSpan.Zero && timeSinceUpdate < TimeSpan.FromHours(1))
                    {
                        currentPos += timeSinceUpdate;
                    }

                    var newPosTicks = currentPos.Ticks + TimeSpan.FromSeconds(seconds).Ticks;

                    var duration = timeline.EndTime - timeline.StartTime;
                    if (duration <= TimeSpan.Zero) duration = timeline.MaxSeekTime;

                    if (newPosTicks < 0) newPosTicks = 0;
                    if (duration.Ticks > 0 && newPosTicks > duration.Ticks) newPosTicks = duration.Ticks;

                    await session.TryChangePlaybackPositionAsync(newPosTicks);
                }
            }
        }
        catch { }
    }
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


