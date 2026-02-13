using System.Diagnostics;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace VNotch.Services;

public class MediaInfo
{
    public bool IsSpotifyRunning { get; set; }
    public bool IsSpotifyPlaying { get; set; }
    public bool IsYouTubeRunning { get; set; }
    public bool IsSoundCloudRunning { get; set; }
    public bool IsFacebookRunning { get; set; }
    public bool IsTikTokRunning { get; set; }
    public bool IsInstagramRunning { get; set; }
    public bool IsTwitterRunning { get; set; }
    public bool IsAppleMusicRunning { get; set; }

    public bool IsAnyMediaPlaying { get; set; }
    public bool IsPlaying { get; set; } // True if actually playing (not paused)
    public double PlaybackRate { get; set; } = 1.0;
    
    public string CurrentTrack { get; set; } = "";
    public string CurrentArtist { get; set; } = "";
    public string YouTubeTitle { get; set; } = "";
    public string MediaSource { get; set; } = "";
    public string SourceAppId { get; set; } = "";
    public BitmapImage? Thumbnail { get; set; }
    public bool HasThumbnail => Thumbnail != null;
    
    // Timeline properties for progress bar
    public TimeSpan Position { get; set; } = TimeSpan.Zero;
    public TimeSpan Duration { get; set; } = TimeSpan.Zero;
    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.Now;
    public bool IsIndeterminate { get; set; }
    public bool IsSeekEnabled { get; set; }
    
    public double Progress => Duration.TotalSeconds > 0 ? Position.TotalSeconds / Duration.TotalSeconds : 0;
    public bool HasTimeline => Duration.TotalSeconds > 0 && !IsIndeterminate;
    
    // Helper to check if this is a video source (supports seeking vs track skip)
    public bool IsVideoSource => MediaSource is "YouTube" or "Browser" or "Facebook" or "TikTok" or "Instagram" or "Twitter";

    public string GetSignature() => $"{CurrentTrack}|{CurrentArtist}|{MediaSource}|{Duration.TotalSeconds}";
}

public class MediaDetectionService : IDisposable
{
    private readonly DispatcherTimer _pollTimer;
    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private bool _disposed;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private static readonly HttpClient _httpClient = new();

    // Win32 APIs for enumerating windows
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
    
    // Caching to prevent reprocessing the same media repeatedly
    private string _lastTrackSignature = "";
    private string _cachedSource = "";
    private BitmapImage? _cachedThumbnail;

    // Session switching state
    private string _pendingSessionAppId = "";
    private DateTime _pendingSessionStartTime = DateTime.MinValue;

    // Window title caching
    private List<string> _cachedWindowTitles = new();
    private DateTime _lastWindowEnumTime = DateTime.MinValue;
    private static readonly string[] _platformKeywords = { 
        "spotify", "youtube", "soundcloud", "facebook", "tiktok", "instagram", "twitter", " / x"
    };

    private DateTime _lastMetadataEventTime = DateTime.MinValue;
    private CancellationTokenSource? _metadataCts;
    private string _lastStableTrackSignature = "";
    private DateTime _emptyMetadataStartTime = DateTime.MinValue;

    public MediaDetectionService()
    {
        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _pollTimer.Tick += PollTimer_Tick;
    }

    private async void PollTimer_Tick(object? sender, EventArgs e)
    {
        // Don't poll if we just had a metadata event (respect the debounce)
        if ((DateTime.Now - _lastMetadataEventTime).TotalMilliseconds < 400) return;
        
        await UpdateMediaInfoAsync();
    }

    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private GlobalSystemMediaTransportControlsSession? _activeDisplaySession; // Session đang được hiển thị trên UI
    private DateTime _lastSessionSwitchTime = DateTime.MinValue;

    public async void Start()
    {
        try
        {
            _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _sessionManager.CurrentSessionChanged += OnSessionChanged;
            
            // Subscribe to current session events
            await SubscribeToCurrentSession();
        }
        catch (Exception ex)
        {
#if DEBUG
            System.Diagnostics.Debug.WriteLine($"[MediaService] Failed to init SMTC: {ex.Message}");
#endif
        }
        
        _pollTimer.Start();
        
        // Initial detection with a shorter delay to be ready faster
        await Task.Delay(500);
        await UpdateMediaInfoAsync(forceRefresh: true);
    }

    public void Stop()
    {
        UnsubscribeFromSession();
        _pollTimer.Stop();
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
        catch (Exception ex)
        {
#if DEBUG
            System.Diagnostics.Debug.WriteLine($"[MediaService] Failed to subscribe: {ex.Message}");
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
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(async () =>
        {
            await UpdateMediaInfoAsync();
        });
    }
    
    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(async () =>
        {
            await UpdateMediaInfoAsync();
        });
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        dispatcher?.BeginInvoke(async () =>
        {
            // Cancel any pending update to start a fresh debounce period
            _metadataCts?.Cancel();
            _metadataCts = new CancellationTokenSource();
            var token = _metadataCts.Token;

            try
            {
                // TIGHT DEBOUNCE: 120ms to allow multi-event packets from Windows to settle
                await Task.Delay(120, token);

                if (!token.IsCancellationRequested)
                {
                    await UpdateMediaInfoAsync(forceRefresh: true);
                }
            }
            catch (TaskCanceledException) { }
        });
    }

    private async void OnSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null)
        {
            await dispatcher.InvokeAsync(async () =>
            {
                Log("Session Changed", "System-wide session focus shifted");
                await SubscribeToCurrentSession();
                await UpdateMediaInfoAsync(forceRefresh: true);
            });
        }
    }

    private void Log(string tag, string message)
    {
#if DEBUG
        System.Diagnostics.Debug.WriteLine($"[MediaService][{tag}] {message}");
#endif
    }

    private async Task UpdateMediaInfoAsync(bool forceRefresh = false)
    {
        // Avoid parallel updates that cause flickering and race conditions
        if (!await _updateLock.WaitAsync(forceRefresh ? 500 : 0)) return;
        
        try
        {
            var info = new MediaInfo();
            List<string>? windowTitles = null;
            
            // First try to get info from Windows Media Session (includes thumbnail!)
            await TryGetMediaSessionInfoAsync(info, forceRefresh, () => {
                windowTitles ??= GetAllWindowTitles();
                return windowTitles;
            });

            // OPTIMIZATION: Fallback to process/window detection
            // Only fall back if we don't have a solid SMTC session or it's just a generic browser.
            // But if SMTC reported a source like "Spotify", we trust it and DON'T look at window titles.
            bool needsFallback = !info.IsAnyMediaPlaying || (string.IsNullOrEmpty(info.CurrentTrack) && info.MediaSource == "Browser") || info.MediaSource == "Browser" || string.IsNullOrEmpty(info.MediaSource);

            if (needsFallback)
            {
                windowTitles ??= GetAllWindowTitles();
                
                // Spotify process check (only if not already found by SMTC)
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
                                info.CurrentArtist = "YouTube";
                                break;
                            }
                        }
                        // Other platform checks...
                    }
                }
            }

            // Sync with final metadata
            var currentSignature = info.GetSignature();

            // GRACE PERIOD: If metadata is currently empty, don't show "No media playing" immediately.
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

            // DEBOUNCE: Only notify if something actually changed
            bool metadataChanged = currentSignature != _lastTrackSignature;
            bool playbackChanged = info.IsPlaying != _lastIsPlaying;
            bool sourceChanged = info.MediaSource != _lastSource;
            bool seekCapabilityChanged = info.IsSeekEnabled != _lastSeekEnabled;
            
            bool significantJump = Math.Abs((info.Position - _lastPosition).TotalSeconds) > 1.5;

            if (forceRefresh || metadataChanged || playbackChanged || sourceChanged || significantJump || seekCapabilityChanged)
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
                
                MediaChanged?.Invoke(this, info);
            }
        }
        finally
        {
            _updateLock.Release();
        }
    }

    private string _lastSource = "";
    private bool _lastIsPlaying = false;
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

    private async Task TryGetMediaSessionInfoAsync(MediaInfo info, bool forceRefresh, Func<List<string>> windowTitleFactory)
    {
        if (_sessionManager == null) return;

        try
        {
            GlobalSystemMediaTransportControlsSession? session = null;
            string? spotifyGroundTruth = null;

            // HOT PATH: If we have an active session and it is playing, check it first before enumerating all.
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
                            // If it's Spotify, we still want to verify it isn't stale
                            if (_activeDisplaySession.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
                            {
                                spotifyGroundTruth = GetSpotifyWindowTitle();
                                if (string.IsNullOrEmpty(spotifyGroundTruth) || !spotifyGroundTruth.Contains(props.Title, StringComparison.OrdinalIgnoreCase))
                                {
                                    // SMTC might be stale or Spotify window title changed, fall back to full check
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
                // FALLBACK: Full enumeration and scoring
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
                            
                            // Treat Buffering/Opening as "Playing" for scoring
                            bool isActive = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing || 
                                           status == (GlobalSystemMediaTransportControlsSessionPlaybackStatus)4 || // Buffering
                                           status == (GlobalSystemMediaTransportControlsSessionPlaybackStatus)5;   // Opening

                            // Check if this is the session we think is active
                            bool isPrevActive = _activeDisplaySession != null && s.SourceAppUserModelId == _activeDisplaySession.SourceAppUserModelId;

                            int score = 0;
                            
                            // Try to get metadata with a very short retry for transient states
                            GlobalSystemMediaTransportControlsSessionMediaProperties? props = null;
                            for(int i=0; i<2; i++) {
                                props = await s.TryGetMediaPropertiesAsync();
                                if (props != null && !string.IsNullOrEmpty(props.Title)) break;
                                if (i == 0) await Task.Delay(25); 
                            }

                            if (props != null && !string.IsNullOrEmpty(props.Title))
                            {
                                score += 50; // Increased metadata weight
                                if (!string.IsNullOrEmpty(props.Artist)) score += 20;
                                if (props.Thumbnail != null) score += 10;
                            }

                            // App Priority
                            bool isSpotify = sourceApp.Contains("Spotify", StringComparison.OrdinalIgnoreCase);
                            if (isSpotify) score += 200;
                            else if (sourceApp.Contains("Music", StringComparison.OrdinalIgnoreCase)) score += 100;
                            
                            // Play state weight
                            if (isActive) score += 300; // Prefer currently playing apps strongly

                            // STICKINESS: Massive boost for the CURRENT session to prevent flickering
                            if (isPrevActive) score += 1000; 

                            // Preferred App Lock: If we have an active Spotify/Music session, 
                            // we give it absolute priority. DO NOT switch unless it's gone for a while.
                            if (isPrevActive && (isSpotify || sourceApp.Contains("Music", StringComparison.OrdinalIgnoreCase))) 
                            {
                                score += 10000; // Absolute stickiness for premium apps
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

                // SESSION HYSTERESIS: Prevent rapid flipping between sessions
                if (bestSession != null && _activeDisplaySession != null && bestSession.SourceAppUserModelId != _activeDisplaySession.SourceAppUserModelId)
                {
                    string bestId = bestSession.SourceAppUserModelId;
                    if (bestId != _pendingSessionAppId)
                    {
                        _pendingSessionAppId = bestId;
                        _pendingSessionStartTime = DateTime.Now;
                    }

                    string currentId = _activeDisplaySession.SourceAppUserModelId ?? "";
                    bool currentIsPremium = currentId.Contains("Spotify", StringComparison.OrdinalIgnoreCase) || 
                                          currentId.Contains("Music", StringComparison.OrdinalIgnoreCase);

                    // If we are switching away from a Premium app, wait 4 seconds.
                    // Otherwise, wait 1.5 seconds to ensure the new session is stable.
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
                    // If everything is gone, keep the old session for a 3s grace period
                    if (_activeDisplaySession != null && (DateTime.Now - _lastSessionSwitchTime).TotalSeconds < 3.0)
                        session = _activeDisplaySession;
                    else
                        session = _sessionManager.GetCurrentSession();
                }
            }

            if (session == null) return;

            // Sync display session first so we have the right source app even if properties fail
            if (_activeDisplaySession != session)
            {
                // Unsubscribe from old
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
                
                // Subscribe to new
                try
                {
                    _activeDisplaySession.MediaPropertiesChanged += OnMediaPropertiesChanged;
                    _activeDisplaySession.PlaybackInfoChanged += OnPlaybackChanged;
                    _activeDisplaySession.TimelinePropertiesChanged += OnTimelineChanged;
                }
                catch { }
                
                _lastTrackSignature = "";
            }

            // Populate source before checking metadata to block fallback logic
            var sessionSourceApp = session.SourceAppUserModelId ?? "";
            info.IsAnyMediaPlaying = true; // We HAVE a session, so we are playing/paused something
            
            if (!string.IsNullOrEmpty(sessionSourceApp))
            {
                if (sessionSourceApp.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
                {
                    info.MediaSource = "Spotify";
                    info.IsSpotifyPlaying = true;
                    info.IsSpotifyRunning = true;
                }
                else if (sessionSourceApp.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ||
                         sessionSourceApp.Contains("Edge", StringComparison.OrdinalIgnoreCase) ||
                         sessionSourceApp.Contains("Firefox", StringComparison.OrdinalIgnoreCase) ||
                         sessionSourceApp.Contains("msedge", StringComparison.OrdinalIgnoreCase) ||
                         sessionSourceApp.Contains("Opera", StringComparison.OrdinalIgnoreCase) ||
                         sessionSourceApp.Contains("Brave", StringComparison.OrdinalIgnoreCase) ||
                         sessionSourceApp.Contains("Vivaldi", StringComparison.OrdinalIgnoreCase) ||
                         sessionSourceApp.Contains("Coccoc", StringComparison.OrdinalIgnoreCase))
                {
                    info.MediaSource = "Browser";
                }
                else if (sessionSourceApp.Contains("Music", StringComparison.OrdinalIgnoreCase))
                {
                    info.MediaSource = "Apple Music";
                    info.IsAppleMusicRunning = true;
                }
            }

            var mediaProperties = await session.TryGetMediaPropertiesAsync();
            
            // TRACK FILTERING: Block generic system/app titles that aren't real songs
            string sessionTitle = mediaProperties?.Title ?? "";
            string lowerTitle = sessionTitle.ToLower();
            bool isJunkTitle = string.IsNullOrEmpty(sessionTitle) || 
                               lowerTitle == "spotify" || 
                               lowerTitle == "youtube" ||
                               lowerTitle == "advertisement" ||
                               lowerTitle == "windows media player" ||
                               lowerTitle == "spotify free" ||
                               lowerTitle == "spotify premium" ||
                               lowerTitle == "chrome" ||
                               lowerTitle == "edge";

            if (isJunkTitle)
            {
                // If it's a junk title, we still have info.MediaSource set.
                // This will prevent the window-title fallback.
                return;
            }

            // Sync playback state
            var pbInfo = session.GetPlaybackInfo();
            info.IsPlaying = pbInfo != null && pbInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            info.IsAnyMediaPlaying = true;

            // Get track info
            info.CurrentTrack = mediaProperties.Title ?? "";
            info.CurrentArtist = mediaProperties.Artist ?? "";

            // FINAL VERIFICATION for Spotify: If SMTC title isn't in our ground truth, trust ground truth
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

                // CRITICAL: If SMTC title disagrees with real window title, 
                // then the SMTC Thumbnail is DEFINITELY old/wrong. 
                if (isStaleSMTC)
                {
                    mediaProperties = null; // Forces clearing thumbnail logic below
                }
            }
            
            // Track signature
            var currentSignature = $"{info.CurrentTrack}|{info.CurrentArtist}";

            // CACHE MANAGEMENT: If track changed, clear the thumbnail cache immediately
            if (currentSignature != _lastTrackSignature)
            {
                _cachedThumbnail = null;
            }

            // REFINEMENT: If source is "Browser", try to be more specific
            if (info.MediaSource == "Browser" || string.IsNullOrEmpty(info.MediaSource))
            {
                if (currentSignature == _lastTrackSignature && !string.IsNullOrEmpty(_cachedSource) && _cachedSource != "Browser")
                {
                    info.MediaSource = _cachedSource;
                }
                else
                {
                    var windowTitles = windowTitleFactory();
                    foreach (var title in windowTitles)
                    {
                        var winTitleLower = title.ToLower();
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


            // Update source cache if we found something better than "Browser" or if the track changed
            if (info.MediaSource != "Browser" && !string.IsNullOrEmpty(info.MediaSource))
            {
                _cachedSource = info.MediaSource;
            }
            else if (currentSignature != _lastTrackSignature)
            {
                // Reset if it's a new track from a generic browser source
                _cachedSource = "Browser";
            }

            if (!forceRefresh && currentSignature == _lastTrackSignature && _cachedThumbnail != null)
            {
                // Reuse cache
                info.Thumbnail = _cachedThumbnail;
            }
            else
            {
                // New track or first load (or stale metadata cleared mediaProperties)
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
                                // Crop to square, removing bars/branding
                                newBitmap = CropToSquare(newBitmap, info.MediaSource) ?? newBitmap;
                                _cachedThumbnail = newBitmap;
                                info.Thumbnail = _cachedThumbnail;
                            }
                        }
                    }
                    catch { }
                }

                // REFINEMENT: If it's YouTube and we DON'T have a good thumbnail, fetch in background
                if (info.MediaSource == "YouTube" && !string.IsNullOrEmpty(info.CurrentTrack) && (info.Thumbnail == null || info.Thumbnail.PixelWidth < 100))
                {
                    string trackDuringFetch = info.CurrentTrack;
                    _ = Task.Run(async () => {
                        try
                        {
                            string? videoId = await TryGetYouTubeVideoIdAsync(trackDuringFetch);
                            if (!string.IsNullOrEmpty(videoId))
                            {
                                string thumbnailUrl = $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg";
                                var frameBitmap = await DownloadImageAsync(thumbnailUrl);
                                if (frameBitmap != null)
                                {
                                    frameBitmap = CropToSquare(frameBitmap, "YouTube") ?? frameBitmap;
                                    
                                    // VERY IMPORTANT: Check if the track actually CHANGED while we were downloading
                                    // We use _lastTrackSignature to verify our "current description" still matches
                                    if (_lastTrackSignature.StartsWith(trackDuringFetch, StringComparison.OrdinalIgnoreCase))
                                    {
                                        info.Thumbnail = frameBitmap;
                                        _cachedThumbnail = frameBitmap;
                                        System.Windows.Application.Current.Dispatcher.BeginInvoke(() => {
                                            // Only notify if we are STILL on that track
                                            if (_lastTrackSignature.StartsWith(trackDuringFetch, StringComparison.OrdinalIgnoreCase))
                                                MediaChanged?.Invoke(this, info);
                                        });
                                    }
                                }
                            }
                        }
                        catch { }
                    });
                }
            }

            // Get timeline properties for progress bar
            try
            {
                var timeline = session.GetTimelineProperties();
                if (timeline != null)
                {
                    info.Position = timeline.Position;
                    info.LastUpdated = timeline.LastUpdatedTime;
                    
                    var duration = timeline.EndTime - timeline.StartTime;
                    if (duration <= TimeSpan.Zero) duration = timeline.MaxSeekTime;
                    info.Duration = duration;

                    // If duration is still zero or suspicious, mark as indeterminate
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

            // Get playback info and capabilities
            try
            {
                var playbackInfo = session.GetPlaybackInfo();
                if (playbackInfo != null)
                {
                    info.IsPlaying = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    info.PlaybackRate = playbackInfo.PlaybackRate ?? 1.0;
                    info.SourceAppId = session.SourceAppUserModelId ?? "";
                    
                    // Check seeking capability
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

    private async Task<string?> TryGetYouTubeVideoIdAsync(string title)
    {
        try
        {
            // Search YouTube for the title to find the video ID
            string searchUrl = $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(title)}";
            string html = await _httpClient.GetStringAsync(searchUrl);
            
            // Regex to find videoId in YouTube's initial data JSON blob
            var match = Regex.Match(html, @"/watch\?v=([a-zA-Z0-9_-]{11})");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }
        catch { }
        return null;
    }

    private async Task<BitmapImage?> DownloadImageAsync(string url)
    {
        try
        {
            var bytes = await _httpClient.GetByteArrayAsync(url);
            using var ms = new MemoryStream(bytes);
            
            BitmapImage? bitmap = null;
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.StreamSource = new MemoryStream(bytes); // Need a fresh stream
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
            });
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
        // Cache results for a short time to reduce Win32 calls but stay responsive
        if ((DateTime.Now - _lastWindowEnumTime).TotalMilliseconds < 300)
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
        
        // Remove SoundCloud specific dynamic prefix (e.g. "▶ 1:23 | ", "▶ 1:23:45 | ")
        if (platform == "SoundCloud" || title.Contains("SoundCloud", StringComparison.OrdinalIgnoreCase))
        {
            // First handle "(1) ▶ " notification count prefixes
            title = Regex.Replace(title, @"^\(\d+\)\s*[▶\s]*", "").Trim();

            // Next handle SoundCloud specific dynamic prefix (e.g. "▶ 1:23 | ", "▶ 1:23:45 | ")
            // This now works because the notification count was stripped
            title = Regex.Replace(title, @"^[▶\s\d:]+\|", "").Trim();
        }

        var separators = new[] { 
            " - YouTube", " – YouTube", " - SoundCloud", " | Facebook", 
            " - TikTok", " / X", " | TikTok", " • Instagram",
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
        if (title.Length > 40)
        {
            title = title.Substring(0, 37) + "...";
        }

        return string.IsNullOrEmpty(title) ? platform : title;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer.Stop();
        
        if (_sessionManager != null)
        {
            _sessionManager.CurrentSessionChanged -= OnSessionChanged;
        }
    }

    // Media Control Methods using Windows Media Session API
    public async Task PlayPauseAsync()
    {
        if (_sessionManager == null) return;
        
        try
        {
            // Ưu tiên session đang hiển thị, fallback về current session
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
            // Ưu tiên session đang hiển thị, fallback về current session
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
            // Ưu tiên session đang hiển thị, fallback về current session
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
            // Ưu tiên session đang hiển thị, fallback về current session
            var session = _activeDisplaySession ?? _sessionManager.GetCurrentSession();
            if (session != null)
            {
                await session.TryChangePlaybackPositionAsync(position.Ticks);
            }
        }
        catch { }
    }

    /// <summary>
    /// Tua tương đối (cộng/trừ giây) mà không cần focus vào ứng dụng
    /// </summary>
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
                    
                    // Extrapolate if playing to get the REAL current position
                    if (isPlaying && timeSinceUpdate > TimeSpan.Zero && timeSinceUpdate < TimeSpan.FromHours(1))
                    {
                        currentPos += timeSinceUpdate;
                    }

                    var newPosTicks = currentPos.Ticks + TimeSpan.FromSeconds(seconds).Ticks;
                    
                    // Giới hạn trong khoảng [0, Duration]
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
