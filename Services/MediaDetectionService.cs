using System.Diagnostics;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using System.Threading.Channels;
using Windows.Media.Control;
using Windows.Storage.Streams;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Text.Json;
using VNotch.Models;

namespace VNotch.Services;

public class MediaDetectionService : IMediaDetectionService
{
    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private bool _disposed;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private static readonly HttpClient _httpClient = new();

    // Event-driven architecture - replaces DispatcherTimer polling
    private readonly Channel<ChangeType> _changeChannel;
    private CancellationTokenSource? _bgCts;
    private Task? _processingTask;
    private Task? _heartbeatTask;
    private DetectionMode _currentMode = DetectionMode.Idle;
    private long _lastEventTimeTicks;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    public event EventHandler<MediaInfo>? MediaChanged;

    private string _lastTrackSignature = "";
    private string _cachedSource = "";
    private BitmapImage? _cachedThumbnail;

    private string _pendingSessionAppId = "";
    private DateTime _pendingSessionStartTime = DateTime.MinValue;

    private List<string> _cachedWindowTitles = new();
    private DateTime _lastWindowEnumTime = DateTime.MinValue;

    private static readonly string[] _platformKeywords = {
        "spotify", "youtube", "soundcloud", "facebook", "tiktok", "instagram", "twitter", " / x", "apple music", "apple", "music"
    };


    private CancellationTokenSource? _thumbCts;
    private string _lastStableTrackSignature = "";
    private DateTime _emptyMetadataStartTime = DateTime.MinValue;
    private readonly Dictionary<string, DateTime> _sessionLastPlayingTimes = new();
    private readonly Dictionary<string, string> _sessionSourceOverrides = new();
    private readonly Dictionary<string, string> _trackSourceCache = new();
    private readonly string _cachePath;

    private DateTime _lastMetadataChangeTime = DateTime.MinValue;
    private string _lastTrackName = "";
    private TimeSpan _lastObservedPosition = TimeSpan.Zero;
    private DateTime _lastPositionChangeTime = DateTime.MinValue;
    private bool _isThrottled;
    private TimeSpan _recoveredDuration = TimeSpan.Zero;
    private BitmapImage? _recoveredThumbnail;

    private DateTime _simBaseWallTimeUtc = DateTime.MinValue;
    private TimeSpan _simBasePosition = TimeSpan.Zero;
    private double _simBasePlaybackRate = 1.0;
    private string _simSignature = "";

    public MediaDetectionService()
    {

        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }

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

    public async void Start()
    {
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

        // Start background processing loops
        _bgCts = new CancellationTokenSource();
        var ct = _bgCts.Token;
        _processingTask = Task.Run(() => ProcessingLoopAsync(ct), ct);
        _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(ct), ct);

        // Initial update after brief delay
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(1500, ct); } catch { return; }
            _changeChannel.Writer.TryWrite(ChangeType.ForceRefresh);
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
                // Debounce: wait 50ms, then drain all queued events
                await Task.Delay(50, ct);
                var types = change;
                while (_changeChannel.Reader.TryRead(out var extra))
                {
                    types |= extra;
                }

                bool forceRefresh = types.HasFlag(ChangeType.MediaProperties)
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

            // Safety: if truly idle and no events for a while, skip
            var lastEvtTicks = Interlocked.Read(ref _lastEventTimeTicks);
            if (_currentMode == DetectionMode.Idle
                && lastEvtTicks > 0
                && (DateTime.UtcNow - new DateTime(lastEvtTicks)).TotalSeconds > 10)
                continue;

            _changeChannel.Writer.TryWrite(ChangeType.Heartbeat);
        }
    }

    private void UpdateDetectionMode(MediaInfo info)
    {
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

            if (info.MediaSource == "Browser" && !string.IsNullOrEmpty(info.SourceAppId) && _sessionSourceOverrides.TryGetValue(info.SourceAppId, out var sOver))
            {
                info.MediaSource = sOver;
                if (sOver == "YouTube") info.IsYouTubeRunning = true;
            }

            if (info.MediaSource == "Browser" && !string.IsNullOrEmpty(info.CurrentTrack) && _trackSourceCache.TryGetValue(info.CurrentTrack, out var tOver))
            {
                info.MediaSource = tOver;
                if (tOver == "YouTube") info.IsYouTubeRunning = true;
            }

            bool needsFallback = !info.IsAnyMediaPlaying || (string.IsNullOrEmpty(info.CurrentTrack) && info.MediaSource == "Browser") || info.MediaSource == "Browser" || string.IsNullOrEmpty(info.MediaSource);

            if (needsFallback)
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

                    }
                }
            }

            var currentSignature = info.GetSignature();

            bool isVideoSource = info.MediaSource is "YouTube" or "Browser";
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

            info.IsThrottled = _isThrottled;
            currentSignature = info.GetSignature(); 

            if (string.IsNullOrEmpty(info.CurrentTrack))
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
                _lastStableTrackSignature = currentSignature;
            }

            bool metadataChanged = currentSignature != _lastTrackSignature;
            bool playbackChanged = info.IsPlaying != _lastIsPlaying;
            bool sourceChanged = info.MediaSource != _lastSource;
            bool seekCapabilityChanged = info.IsSeekEnabled != _lastSeekEnabled;

            bool significantJump = Math.Abs((info.Position - _lastPosition).TotalSeconds) >= (info.IsThrottled ? 5.0 : 1.5);
            bool throttleChanged = info.IsThrottled != _lastIsThrottled;

            if (forceRefresh || metadataChanged || playbackChanged || sourceChanged || (significantJump && !info.IsThrottled) || seekCapabilityChanged || throttleChanged)
            {
                if (string.IsNullOrEmpty(info.CurrentTrack) && !string.IsNullOrEmpty(_lastTrackSignature) && !forceRefresh)
                {
                    return;
                }

                _lastTrackSignature = currentSignature;
                _lastIsPlaying = info.IsPlaying;
                _lastSource = info.MediaSource;
                _lastPosition = info.Position;
                _lastSeekEnabled = info.IsSeekEnabled;
                _lastIsThrottled = info.IsThrottled;

                // Update detection mode for adaptive heartbeat
                UpdateDetectionMode(info);

                // Marshal event to UI thread
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher != null)
                {
                    await dispatcher.InvokeAsync(() => MediaChanged?.Invoke(this, info));
                }
            }

            bool isPotentialYouTube = info.MediaSource == "YouTube" || info.MediaSource == "Browser";

            bool needsFetch = (info.Thumbnail == null || info.Thumbnail.PixelWidth < 120);
            if (_isThrottled && _recoveredThumbnail != null) needsFetch = false;

            if (isPotentialYouTube && !string.IsNullOrEmpty(info.CurrentTrack) && needsFetch)
            {

                _thumbCts?.Cancel();
                _thumbCts = new CancellationTokenSource();
                var token = _thumbCts.Token;

                string trackDuringFetch = info.CurrentTrack;
                string artistDuringFetch = info.CurrentArtist;

                bool isBrowserIcon = info.Thumbnail != null && info.MediaSource == "Browser";
                bool needsBetterThumb = info.Thumbnail == null || info.Thumbnail.PixelWidth < 120 || isBrowserIcon;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        string? videoId = info.YouTubeVideoId;
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
                                    if (highConfidence && info.MediaSource == "Browser")
                                    {
                                        info.MediaSource = "YouTube";
                                        info.IsYouTubeRunning = true;

                                        if (!string.IsNullOrEmpty(info.SourceAppId))
                                        {
                                            _sessionSourceOverrides[info.SourceAppId] = "YouTube";
                                        }

                                        _trackSourceCache[trackDuringFetch] = "YouTube";
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

                                        string currentSig = _lastTrackSignature;
                                        if (currentSig.Contains(trackDuringFetch, StringComparison.OrdinalIgnoreCase))
                                        {
                                            frameBitmap = CropToSquare(frameBitmap, "YouTube") ?? frameBitmap;
                                            _recoveredThumbnail = frameBitmap; 
                                            info.Thumbnail = frameBitmap;

                                            var dispatcher = System.Windows.Application.Current?.Dispatcher;
                                            if (dispatcher != null)
                                            {
                                                await dispatcher.InvokeAsync(() =>
                                                {
                                                    if (!token.IsCancellationRequested && _lastTrackSignature.Contains(trackDuringFetch, StringComparison.OrdinalIgnoreCase))
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
                            if (retryCount < 3 && !token.IsCancellationRequested && _lastTrackSignature.Contains(trackDuringFetch, StringComparison.OrdinalIgnoreCase))
                            {
                                await Task.Delay(retryCount * 800, token);
                                videoId = null;
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch { }
                }, token);
            }
            else
            {

                _thumbCts?.Cancel();
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

    private async Task TryGetMediaSessionInfoAsync(MediaInfo info, bool forceRefresh, Func<List<string>> windowTitleFactory)
    {
        if (_sessionManager == null) return;

        try
        {

            GlobalSystemMediaTransportControlsSession? session = null;
            string? spotifyGroundTruth = null;

            if (_activeDisplaySession != null && !forceRefresh)
            {
                try
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
                catch { }
            }

            if (session == null)
            {

                spotifyGroundTruth ??= GetSpotifyWindowTitle();
                GlobalSystemMediaTransportControlsSession? bestSession = null;
                int bestScore = -1;

                try
                {
                    var sessions = _sessionManager.GetSessions();

                    foreach (var s in sessions)
                    {
                        try
                        {
                            var sourceApp = s.SourceAppUserModelId ?? "";
                            var playbackInfo = s.GetPlaybackInfo();
                            var status = playbackInfo.PlaybackStatus;

                            bool isActive = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ||
                                           status == (GlobalSystemMediaTransportControlsSessionPlaybackStatus)4 || 
                                           status == (GlobalSystemMediaTransportControlsSessionPlaybackStatus)5;   

                            bool isPrevActive = _activeDisplaySession != null && s.SourceAppUserModelId == _activeDisplaySession.SourceAppUserModelId;

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

                            if (isActive)
                            {
                                score += 500; 
                                _sessionLastPlayingTimes[sourceApp] = DateTime.Now;
                            }

                            if (_sessionLastPlayingTimes.TryGetValue(sourceApp, out var lastPlaying))
                            {
                                var idleSeconds = (DateTime.Now - lastPlaying).TotalSeconds;
                                if (idleSeconds < 30) score += (int)((30 - idleSeconds) * 10);
                            }

                            if (props != null && !string.IsNullOrEmpty(props.Title))
                            {
                                score += 1500; 
                                if (!string.IsNullOrEmpty(props.Artist) && props.Artist != "YouTube" && props.Artist != "Browser") score += 200;
                            }

                            if (score > bestScore && (isActive || isPrevActive))
                            {
                                bestScore = score;
                                bestSession = s;
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                if (bestSession != null && _activeDisplaySession != null && bestSession.SourceAppUserModelId != _activeDisplaySession.SourceAppUserModelId)
                {
                    string bestId = bestSession.SourceAppUserModelId ?? "";
                    if (bestId != _pendingSessionAppId)
                    {
                        _pendingSessionAppId = bestId;
                        _pendingSessionStartTime = DateTime.Now;
                    }

                    string currentId = _activeDisplaySession.SourceAppUserModelId ?? "";
                    bool currentIsPremium = currentId.Contains("Spotify", StringComparison.OrdinalIgnoreCase) ||
                                          currentId.Contains("Music", StringComparison.OrdinalIgnoreCase);

                    double holdTime = currentIsPremium ? 4.0 : 1.5;

                    if ((DateTime.Now - _pendingSessionStartTime).TotalSeconds < holdTime)
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
                    if (_activeDisplaySession == null || bestSession.SourceAppUserModelId != _activeDisplaySession.SourceAppUserModelId)
                    {
                        _lastSessionSwitchTime = DateTime.Now;
                    }
                    session = bestSession;
                }
                else
                {

                    if (_activeDisplaySession != null && (DateTime.Now - _lastSessionSwitchTime).TotalSeconds < 3.0)
                        session = _activeDisplaySession;
                    else
                        session = _sessionManager.GetCurrentSession();
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
                else if (sessionSourceApp.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ||
                         sessionSourceApp.Contains("Edge", StringComparison.OrdinalIgnoreCase) ||
                         sessionSourceApp.Contains("Firefox", StringComparison.OrdinalIgnoreCase) ||
                         sessionSourceApp.Contains("MS-Edge", StringComparison.OrdinalIgnoreCase) ||
                         sessionSourceApp.Contains("msedge", StringComparison.OrdinalIgnoreCase) ||
                         sessionSourceApp.Contains("Opera", StringComparison.OrdinalIgnoreCase) ||
                         sessionSourceApp.Contains("Brave", StringComparison.OrdinalIgnoreCase) ||
                         sessionSourceApp.Contains("Vivaldi", StringComparison.OrdinalIgnoreCase) ||
                         sessionSourceApp.Contains("Coccoc", StringComparison.OrdinalIgnoreCase) ||
                         sessionSourceApp.Contains("Arc", StringComparison.OrdinalIgnoreCase) ||
                         sessionSourceApp.Contains("Sidekick", StringComparison.OrdinalIgnoreCase) ||
                         sessionSourceApp.Contains("Browser", StringComparison.OrdinalIgnoreCase))
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

                if (info.MediaSource == "Browser" && _sessionSourceOverrides.TryGetValue(sessionSourceApp, out var overriden))
                {
                    info.MediaSource = overriden;
                    if (overriden == "YouTube") info.IsYouTubeRunning = true;
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
            info.IsAnyMediaPlaying = true;

            info.CurrentTrack = sessionTitle;
            info.CurrentArtist = sessionArtist;

            // Timeline properties read once later (avoid duplicate GetTimelineProperties call)

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

            if (currentSignature != _lastTrackSignature)
            {
                _cachedThumbnail = null;
            }

            if (info.MediaSource == "Browser" || string.IsNullOrEmpty(info.MediaSource))
            {

                bool isSameSession = _activeDisplaySession != null && session != null &&
                                   _activeDisplaySession.SourceAppUserModelId == session.SourceAppUserModelId;

                if (isSameSession && !string.IsNullOrEmpty(_stableSource) && _stableSource != "Browser" &&
                    (DateTime.Now - _lastSourceConfirmedTime).TotalSeconds < 5.0)
                {
                    info.MediaSource = _stableSource;
                }
                else
                {
                    var windowTitles = windowTitleFactory();
                    string trackTitleLower = info.CurrentTrack.ToLower();
                    bool hasTrack = !string.IsNullOrEmpty(trackTitleLower) && trackTitleLower != "browser" && trackTitleLower != "now playing";

                    foreach (var title in windowTitles)
                    {
                        var winTitleLower = title.ToLower();

                        if (hasTrack && !winTitleLower.Contains(trackTitleLower) && !winTitleLower.Contains("youtube"))
                            continue;

                        if (winTitleLower.Contains("youtube") && !winTitleLower.StartsWith("youtube -") && winTitleLower != "youtube")
                        {
                            info.MediaSource = "YouTube";
                            info.IsYouTubeRunning = true;
                            break;
                        }
                        else if (winTitleLower.Contains("soundcloud") && winTitleLower.Contains(" - "))
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

            if (info.MediaSource != "Browser" && !string.IsNullOrEmpty(info.MediaSource))
            {
                _stableSource = info.MediaSource;
                _lastSourceConfirmedTime = DateTime.Now;
                _cachedSource = info.MediaSource;
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

                                double aspect = (double)newBitmap.PixelWidth / newBitmap.PixelHeight;
                                bool isSquare = Math.Abs(aspect - 1.0) < 0.05; 

                                bool isGenericIcon = info.MediaSource == "YouTube" || info.MediaSource == "SoundCloud" || info.MediaSource == "Browser";

                                if (isSquare && isGenericIcon)
                                {

                                }
                                else
                                {

                                    newBitmap = CropToSquare(newBitmap, info.MediaSource) ?? newBitmap;
                                    _cachedThumbnail = newBitmap;
                                    info.Thumbnail = _cachedThumbnail;
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
                    // Try to get fresher position on initial load or after big change
                    bool isInitialOrBigChange = forceRefresh ||
                                               _lastTrackSignature == "" ||
                                               (DateTime.Now - _lastMetadataChangeTime).TotalSeconds < 4.0;

                    if (isInitialOrBigChange)
                    {
                        // Wait a tiny bit and try again to get fresher timeline
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

                    if (isNewTrack && isNearlyEnd)
                    {
                        chosenPosition = TimeSpan.Zero;
                        info.IsIndeterminate = false;
                    }
                    else
                    {
                        chosenPosition = timeline.Position;
                    }

                    // Extra safety: if position looks unreasonably old on initial load
                    if (isInitialOrBigChange && chosenPosition.TotalSeconds > 5)
                    {
                        var playback = session?.GetPlaybackInfo();
                        if (playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        {
                            // Assume it has been playing for at least a few seconds
                            chosenPosition = chosenPosition + TimeSpan.FromSeconds(1.5);
                        }
                    }

                    info.Position = chosenPosition;
                    info.Duration = duration;
                    info.LastUpdated = timeline.LastUpdatedTime;

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

    private class YouTubeResult
    {
        public string? Id { get; set; }
        public string? Author { get; set; }
        public string? Title { get; set; }
        public TimeSpan Duration { get; set; }

        public bool TitleMatches(string otherTitle)
        {
            if (string.IsNullOrEmpty(Title) || string.IsNullOrEmpty(otherTitle)) return false;
            string t1 = Title.ToLower();
            string t2 = otherTitle.ToLower();
            return t1.Contains(t2) || t2.Contains(t1);
        }
    }

    private async Task<YouTubeResult?> TryGetYouTubeVideoIdWithInfoAsync(string title, string artist = "")
    {
        try
        {
            string cleanTitle = title;

            if (title.Length != 11)
            {

                var parts = title.Split(new[] { " - ", " | ", " – " }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2) cleanTitle = parts[0].Trim();
            }

            if (cleanTitle.Length == 11 && Regex.IsMatch(cleanTitle, @"^[a-zA-Z0-9_-]{11}$"))
                return new YouTubeResult { Id = cleanTitle };

            string searchQuery = cleanTitle;
            if (!string.IsNullOrEmpty(artist) && artist != "YouTube" && artist != "Browser")
            {

                searchQuery = $"{cleanTitle} {artist}";
            }
            else if (cleanTitle.Contains(" - "))
            {

                searchQuery = cleanTitle;
            }

            // Removed _youtubeService dependency

            string searchUrl = $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(title)}";
            string html = await _httpClient.GetStringAsync(searchUrl);

            var match = Regex.Match(html, @"/watch\?v=([a-zA-Z0-9_-]{11})");
            if (match.Success) return new YouTubeResult { Id = match.Groups[1].Value };

            // Fallback: search for "videoId":"..." in the raw HTML (often in ytInitialData)
            match = Regex.Match(html, @"""videoId"":""([a-zA-Z0-9_-]{11})""");
            if (match.Success) return new YouTubeResult { Id = match.Groups[1].Value };
        }
        catch { }
        return null;
    }

    private async Task<string?> TryGetYouTubeVideoIdAsync(string title)
    {
        var res = await TryGetYouTubeVideoIdWithInfoAsync(title);
        return res?.Id;
    }

    private async Task<BitmapImage?> DownloadImageAsync(string url)
    {
        try
        {
            var bytes = await _httpClient.GetByteArrayAsync(url);
            using var ms = new MemoryStream(bytes);

            BitmapImage? bitmap = null;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null)
            {
                await dispatcher.InvokeAsync(() =>
                {
                    bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.StreamSource = new MemoryStream(bytes); // Need a fresh stream
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();
                });
            }
            return bitmap;
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Crop a BitmapImage to a square region.
    /// - Spotify: crop off bottom 20% (branding strip), then square from top
    /// - Landscape (YouTube 16:9): crop center square
    /// - Portrait: crop from TOP
    /// </summary>
    private BitmapImage? CropToSquare(BitmapImage source, string mediaSource)
    {
        try
        {
            int width = source.PixelWidth;
            int height = source.PixelHeight;

            // For Spotify: always crop bottom branding (even if image is square)
            bool isSpotify = mediaSource.Contains("Spotify", StringComparison.OrdinalIgnoreCase);

            // Zoom factor to hide minor edge artifacts/black borders (3% total crop)
            double zoom = 0.97;
            int squareSize;
            int offsetX, offsetY;

            if (isSpotify)
            {
                // Spotify: crop off bottom 20% (branding strip) then zoom center of remaining
                int contentHeight = (int)(height * 0.80);
                squareSize = (int)(Math.Min(width, contentHeight) * zoom);
                offsetX = (width - squareSize) / 2;
                offsetY = (contentHeight - squareSize) / 2;
            }
            else
            {
                // General: Take center square and zoom in
                squareSize = (int)(Math.Min(width, height) * zoom);
                offsetX = (width - squareSize) / 2;
                offsetY = (height - squareSize) / 2;
            }

            var rect = new System.Windows.Int32Rect(offsetX, offsetY, squareSize, squareSize);
            var cropped = new CroppedBitmap(source, rect);

            // Convert CroppedBitmap back to BitmapImage for consistency
            BitmapImage? result = null;
            using (var ms = new MemoryStream())
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(cropped));
                encoder.Save(ms);
                ms.Position = 0;

                result = new BitmapImage();
                result.BeginInit();
                result.StreamSource = ms;
                result.CacheOption = BitmapCacheOption.OnLoad;
                result.EndInit();
                result.Freeze();
            }
            return result;
        }
        catch
        {
            return null;
        }
    }

    private async Task<BitmapImage?> ConvertToWpfBitmapAsync(IRandomAccessStreamWithContentType stream)
    {
        try
        {
            using (var memoryStream = new MemoryStream())
            {
                using (var reader = new DataReader(stream))
                {
                    await reader.LoadAsync((uint)stream.Size);
                    var bytes = new byte[stream.Size];
                    reader.ReadBytes(bytes);
                    memoryStream.Write(bytes, 0, bytes.Length);
                }

                memoryStream.Position = 0;

                BitmapImage? bitmap = null;

                // Must create BitmapImage on UI thread
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.StreamSource = memoryStream;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad; // Copies data, safe to dispose stream
                    bitmap.EndInit();
                    bitmap.Freeze();
                });

                return bitmap;
            }
        }
        catch
        {
            return null;
        }
    }

    private List<string> GetAllWindowTitles()
    {
        // Adaptive cache: shorter when throttled (need window titles), longer otherwise
        var cacheDuration = _isThrottled ? 500 : 1500;
        if ((DateTime.Now - _lastWindowEnumTime).TotalMilliseconds < cacheDuration)
        {
            return _cachedWindowTitles;
        }

        var titles = new List<string>();

        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd))
                return true;

            int length = GetWindowTextLength(hWnd);
            if (length == 0)
                return true;

            // Pre-allocate buffer
            var sb = new StringBuilder(length + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            var title = sb.ToString();

            if (!string.IsNullOrWhiteSpace(title))
            {
                // Optimization: Only keep titles that might be relevant to our platforms
                string lowerTitle = title.ToLower();
                bool isRelevant = false;
                foreach (var kw in _platformKeywords)
                {
                    if (lowerTitle.Contains(kw))
                    {
                        isRelevant = true;
                        break;
                    }
                }

                if (isRelevant)
                {
                    titles.Add(title);
                }
            }

            return true;
        }, IntPtr.Zero);

        _cachedWindowTitles = titles;
        _lastWindowEnumTime = DateTime.Now;
        return titles;
    }

    private bool IsLikelyYouTube(MediaInfo info)
    {
        if (info.MediaSource == "YouTube") return true;

        // If Browser but we already learned/overrode it is YouTube
        if (info.MediaSource == "Browser" && !string.IsNullOrEmpty(info.SourceAppId))
        {
            if (_sessionSourceOverrides.TryGetValue(info.SourceAppId, out var sOver) && sOver == "YouTube")
                return true;
        }

        // Track persistent cache says YouTube
        if (info.MediaSource == "Browser" && !string.IsNullOrEmpty(info.CurrentTrack))
        {
            if (_trackSourceCache.TryGetValue(info.CurrentTrack, out var tOver) && tOver == "YouTube")
                return true;
        }

        // Heuristic: SMTC metadata sometimes uses Artist="YouTube" for browser sessions
        if (!string.IsNullOrEmpty(info.CurrentArtist) &&
            info.CurrentArtist.Equals("YouTube", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private void ApplySimulatedTimeline(MediaInfo info, bool atEndStuck)
    {
        var nowUtc = DateTime.UtcNow;

        // Reset base if track/source changed
        var sig = info.GetSignature();
        if (_simSignature != sig || _simBaseWallTimeUtc == DateTime.MinValue)
        {
            _simSignature = sig;
            _simBaseWallTimeUtc = nowUtc;

            // Prefer last observed position (more stable than a frozen SMTC position)
            _simBasePosition = _lastObservedPosition != TimeSpan.Zero ? _lastObservedPosition : info.Position;

            _simBasePlaybackRate = info.PlaybackRate > 0 ? info.PlaybackRate : 1.0;
        }

        var elapsed = nowUtc - _simBaseWallTimeUtc;
        var sim = _simBasePosition + TimeSpan.FromSeconds(elapsed.TotalSeconds * _simBasePlaybackRate);

        // If we're "stuck at end", duration is likely stale -> avoid clamping to it.
        if (!atEndStuck && info.Duration > TimeSpan.Zero && sim > info.Duration)
            sim = info.Duration;

        info.Position = sim;

        info.IsThrottled = true;
        _isThrottled = true;

        // Keep cache duration/thumbnail if we have them (avoid 3:15/3:15 end-stuck visuals)
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

        // 1. Common Prefix Removals (Notifications, Play symbol, dynamic timestamps)
        // Remove prefixes like "(1) ", "(99+) ", "▶ ", "⏸ "
        title = Regex.Replace(title, @"^\(\d+\+?\)\s*", "");
        title = Regex.Replace(title, @"^[▶⏸▶️⏸️\s]*", "");

        // Remove SoundCloud/Generic dynamic prefix (e.g. "1:23 | ", "▶ 1:23:45 | ")
        title = Regex.Replace(title, @"^[▶⏸\s\d:]+\|", "").Trim();

        // 2. Suffix Removals (Platform branding)
        var separators = new[] {
            " - YouTube", " – YouTube", " - SoundCloud", " | Facebook",
            " - TikTok", " / X", " | TikTok", " • Instagram",
            " - Apple Music", " – Apple Music",
            " - Google Chrome", " - Microsoft​ Edge", " - Microsoft Edge",
            " - Mozilla Firefox", " - Opera", " - Brave", " - Cốc Cốc",
            " - Vivaldi", " – Vivaldi"
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

        // 3. Final cleanup: Remove trailing " - " or " | " if any
        title = Regex.Replace(title, @"\s+[\-\|–•]\s*$", "");

        if (title.Length > 60)
        {
            title = title.Substring(0, 57) + "...";
        }

        return string.IsNullOrEmpty(title) ? platform : title;
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