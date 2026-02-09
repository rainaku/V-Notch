using System.Diagnostics;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

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
    
    public string CurrentTrack { get; set; } = "";
    public string CurrentArtist { get; set; } = "";
    public string YouTubeTitle { get; set; } = "";
    public string MediaSource { get; set; } = "";
    public BitmapImage? Thumbnail { get; set; }
    public bool HasThumbnail => Thumbnail != null;
    
    // Timeline properties for progress bar
    public TimeSpan Position { get; set; } = TimeSpan.Zero;
    public TimeSpan Duration { get; set; } = TimeSpan.Zero;
    public double Progress => Duration.TotalSeconds > 0 ? Position.TotalSeconds / Duration.TotalSeconds : 0;
    public bool HasTimeline => Duration.TotalSeconds > 0;
    
    // Helper to check if this is a video source (supports seeking vs track skip)
    public bool IsVideoSource => MediaSource is "YouTube" or "Browser" or "Facebook" or "TikTok" or "Instagram" or "Twitter";
}

public class MediaDetectionService : IDisposable
{
    private readonly DispatcherTimer _pollTimer;
    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private bool _disposed;

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
    private BitmapImage? _cachedThumbnail;
    private MediaInfo? _lastMediaInfo;

    public MediaDetectionService()
    {
        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1) // Update every 1 second for smooth progress bar
        };
        _pollTimer.Tick += PollTimer_Tick;
    }

    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private GlobalSystemMediaTransportControlsSession? _activeDisplaySession; // Session đang được hiển thị trên UI

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
            System.Diagnostics.Debug.WriteLine($"[MediaService] Failed to init SMTC: {ex.Message}");
        }
        
        _pollTimer.Start();
        await UpdateMediaInfoAsync();
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
                System.Diagnostics.Debug.WriteLine($"[MediaService] Subscribed to session: {_currentSession.SourceAppUserModelId}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaService] Failed to subscribe: {ex.Message}");
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
        System.Diagnostics.Debug.WriteLine("[MediaService] Timeline changed event!");
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(async () =>
        {
            await UpdateMediaInfoAsync();
        });
    }
    
    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        System.Diagnostics.Debug.WriteLine("[MediaService] Playback changed event!");
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(async () =>
        {
            await UpdateMediaInfoAsync();
        });
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        System.Diagnostics.Debug.WriteLine($"[MediaService] Media properties changed event from: {sender.SourceAppUserModelId}");
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(async () =>
        {
            // Clear cache to force fresh metadata fetch
            _lastTrackSignature = "";
            // Force refresh because metadata (like thumbnail) might have just arrived
            await UpdateMediaInfoAsync(forceRefresh: true);
        });
    }

    private async void OnSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        System.Diagnostics.Debug.WriteLine("[MediaService] Session changed!");
        await System.Windows.Application.Current?.Dispatcher.InvokeAsync(async () =>
        {
            await SubscribeToCurrentSession();
            await UpdateMediaInfoAsync();
        });
    }

    private async void PollTimer_Tick(object? sender, EventArgs e)
    {
        await UpdateMediaInfoAsync();
    }

    private async Task UpdateMediaInfoAsync(bool forceRefresh = false)
    {
        var info = new MediaInfo();
        
        // First try to get info from Windows Media Session (includes thumbnail!)
        await TryGetMediaSessionInfoAsync(info, forceRefresh);

        // OPTIMIZATION: If we already found valid media from SMTC (Modern API), 
        // skip the expensive EnumWindows call (Win32 API).
        // Only fallback to window titles if SMTC returned nothing or generic info.
        if (!info.IsAnyMediaPlaying || string.IsNullOrEmpty(info.CurrentTrack))
        {
            // Also check window titles for additional context
            var windowTitles = GetAllWindowTitles();

        foreach (var title in windowTitles)
        {
            var lowerTitle = title.ToLower();

            // Spotify detection
            if (lowerTitle.Contains("spotify"))
            {
                info.IsSpotifyRunning = true;
            }
            
            // Check Spotify process for playing state
            var spotifyProcesses = Process.GetProcessesByName("Spotify");
            foreach (var proc in spotifyProcesses)
            {
                try
                {
                    if (!string.IsNullOrEmpty(proc.MainWindowTitle) && 
                        proc.MainWindowTitle != "Spotify" &&
                        proc.MainWindowTitle != "Spotify Premium" &&
                        proc.MainWindowTitle != "Spotify Free" &&
                        !proc.MainWindowTitle.ToLower().EndsWith("spotify"))
                    {
                        info.IsSpotifyPlaying = true;
                        info.IsSpotifyRunning = true;
                        if (string.IsNullOrEmpty(info.MediaSource))
                        {
                    info.IsAnyMediaPlaying = true; // This tracks "Active Session"
                    
                    // Fallback approximation for window-based detection
                    // We assume if title is present, it might be playing, but we can't be sure without SMTC
                    info.IsPlaying = true; 
                    
                    info.MediaSource = "Spotify";
                    ParseSpotifyTitle(proc.MainWindowTitle, info);
                        }
                    }
                }
                catch { }
            }

            // YouTube detection
            if (lowerTitle.Contains("youtube") && !lowerTitle.StartsWith("youtube -") && 
                lowerTitle != "youtube")
            {
                info.IsYouTubeRunning = true;
                if (!info.IsSpotifyPlaying && string.IsNullOrEmpty(info.MediaSource))
                {
                    info.IsAnyMediaPlaying = true;
                    info.MediaSource = "YouTube";
                    info.YouTubeTitle = ExtractVideoTitle(title, "YouTube");
                    info.CurrentTrack = info.YouTubeTitle;
                    info.CurrentArtist = "YouTube";
                }
            }

            // SoundCloud
            if (lowerTitle.Contains("soundcloud") && lowerTitle.Contains(" - "))
            {
                info.IsSoundCloudRunning = true;
                if (string.IsNullOrEmpty(info.MediaSource))
                {
                    info.IsAnyMediaPlaying = true;
                    info.MediaSource = "SoundCloud";
                    info.CurrentTrack = ExtractVideoTitle(title, "SoundCloud");
                    info.CurrentArtist = "SoundCloud";
                }
            }

            // Facebook Watch/Video
            if (lowerTitle.Contains("facebook") && (lowerTitle.Contains("watch") || lowerTitle.Contains("video")))
            {
                info.IsFacebookRunning = true;
                if (string.IsNullOrEmpty(info.MediaSource))
                {
                    info.IsAnyMediaPlaying = true;
                    info.MediaSource = "Facebook";
                    info.CurrentTrack = ExtractVideoTitle(title, "Facebook");
                    info.CurrentArtist = "Facebook";
                }
            }

            // TikTok
            if (lowerTitle.Contains("tiktok") && lowerTitle.Contains(" | "))
            {
                info.IsTikTokRunning = true;
                if (string.IsNullOrEmpty(info.MediaSource))
                {
                    info.IsAnyMediaPlaying = true;
                    info.MediaSource = "TikTok";
                    info.CurrentTrack = ExtractVideoTitle(title, "TikTok");
                    info.CurrentArtist = "TikTok";
                }
            }

            // Instagram
            if (lowerTitle.Contains("instagram") && (lowerTitle.Contains("reel") || lowerTitle.Contains("video")))
            {
                info.IsInstagramRunning = true;
                if (string.IsNullOrEmpty(info.MediaSource))
                {
                    info.IsAnyMediaPlaying = true;
                    info.MediaSource = "Instagram";
                    info.CurrentTrack = "Instagram Reel";
                    info.CurrentArtist = "Instagram";
                }
            }

            // Twitter/X
            if ((lowerTitle.Contains("twitter") || lowerTitle.Contains(" / x")) && 
                (lowerTitle.Contains("video") || lowerTitle.Contains("watch")))
            {
                info.IsTwitterRunning = true;
                if (string.IsNullOrEmpty(info.MediaSource))
                {
                    info.IsAnyMediaPlaying = true;
                    info.MediaSource = "Twitter";
                    info.CurrentTrack = ExtractVideoTitle(title, "X");
                    info.CurrentArtist = "Twitter/X";
                }
            }
        }
    }


        MediaChanged?.Invoke(this, info);
    }

    private async Task TryGetMediaSessionInfoAsync(MediaInfo info, bool forceRefresh)
    {
        if (_sessionManager == null) return;

        try
        {
            // Session variable to hold result
            GlobalSystemMediaTransportControlsSession? session = null;

            // Try to find the BEST playing session
            // Priority: Playing > Paused (but we only look for playing here usually, or fallback to current)
            // Then: Metadata quality (Title + Artist > Title only)
            
            GlobalSystemMediaTransportControlsSession? bestSession = null;
            int bestScore = -1;

            try 
            {
                var sessions = _sessionManager.GetSessions();
                System.Diagnostics.Debug.WriteLine($"[MediaService] Found {sessions.Count} sessions");
                
                foreach (var s in sessions)
                {
                    try 
                    {
                        var sourceApp = s.SourceAppUserModelId ?? "";
                        var playbackInfo = s.GetPlaybackInfo();
                        var isPlaying = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                        
                        System.Diagnostics.Debug.WriteLine($"[MediaService] Session: {sourceApp}, Playing: {isPlaying}");
                        
                        if (isPlaying)
                        {
                            int score = 0;
                            
                            // Ưu tiên Spotify vì đây là music app chính
                            if (sourceApp.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
                            {
                                score += 20; // Spotify gets priority
                            }
                            
                            // Check metadata quality
                            var props = await s.TryGetMediaPropertiesAsync();
                            if (props != null)
                            {
                                if (!string.IsNullOrEmpty(props.Title)) score += 10;
                                if (!string.IsNullOrEmpty(props.Artist)) score += 5;
                                if (props.Thumbnail != null) score += 2;
                                
                                System.Diagnostics.Debug.WriteLine($"[MediaService] Session {sourceApp}: Title='{props.Title}', Artist='{props.Artist}', Score={score}");
                            }
                            
                            if (score > bestScore)
                            {
                                bestScore = score;
                                bestSession = s;
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            // If we found a playing session, use it.
            if (bestSession != null)
            {
                session = bestSession;
            }
            else
            {
                // Fallback to whatever Windows thinks is "Current"
                session = _sessionManager.GetCurrentSession();
            }

            if (session == null) return;
            
            // Lưu session đang hiển thị để control methods sử dụng
            // Chỉ update nếu session thay đổi
            if (_activeDisplaySession != session)
            {
                System.Diagnostics.Debug.WriteLine($"[MediaService] Active display session changed: {session.SourceAppUserModelId}");
                
                // Unsubscribe from old active session
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
                
                // Subscribe to new active session (nếu khác với _currentSession)
                if (_activeDisplaySession != _currentSession)
                {
                    try
                    {
                        _activeDisplaySession.MediaPropertiesChanged += OnMediaPropertiesChanged;
                        _activeDisplaySession.PlaybackInfoChanged += OnPlaybackChanged;
                        _activeDisplaySession.TimelinePropertiesChanged += OnTimelineChanged;
                    }
                    catch { }
                }
                
                // Clear cache khi session mới
                _lastTrackSignature = "";
            }

            var mediaProperties = await session.TryGetMediaPropertiesAsync();
            if (mediaProperties == null) return;

            // Get track info
            if (!string.IsNullOrEmpty(mediaProperties.Title))
            {
                info.CurrentTrack = mediaProperties.Title;
                info.IsAnyMediaPlaying = true;
            }
            if (!string.IsNullOrEmpty(mediaProperties.Artist))
            {
                info.CurrentArtist = mediaProperties.Artist;
            }

            // Get source app
            var sessionSourceApp = session.SourceAppUserModelId;
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
                         sessionSourceApp.Contains("msedge", StringComparison.OrdinalIgnoreCase))
                {
                    // Browser - could be YouTube, etc.
                    if (string.IsNullOrEmpty(info.MediaSource))
                    {
                        info.MediaSource = "Browser";
                    }
                }
            }
            
            // REFINEMENT: If source is "Browser", try to be more specific by checking window titles
            if (info.MediaSource == "Browser" || string.IsNullOrEmpty(info.MediaSource))
            {
                var windowTitles = GetAllWindowTitles();
                foreach (var title in windowTitles)
                {
                    var lowerTitle = title.ToLower();
                    
                    // Specific Platform Checks for Browser Sessions
                    if (lowerTitle.Contains("youtube") && !lowerTitle.StartsWith("youtube -") && lowerTitle != "youtube")
                    {
                         // If the title matches roughly, it's likely this tab
                         if (string.IsNullOrEmpty(info.CurrentTrack) || lowerTitle.Contains(info.CurrentTrack.ToLower()))
                         {
                             info.MediaSource = "YouTube";
                             info.IsYouTubeRunning = true;
                             break;
                         }
                    }
                    else if (lowerTitle.Contains("soundcloud") && lowerTitle.Contains(" - "))
                    {
                         if (string.IsNullOrEmpty(info.CurrentTrack) || lowerTitle.Contains(info.CurrentTrack.ToLower()))
                         {
                             info.MediaSource = "SoundCloud";
                             info.IsSoundCloudRunning = true;
                             break;
                         }
                    }
                    else if (lowerTitle.Contains("facebook") && (lowerTitle.Contains("watch") || lowerTitle.Contains("video")))
                    {
                        info.MediaSource = "Facebook";
                        info.IsFacebookRunning = true;
                        break;
                    }
                    else if (lowerTitle.Contains("tiktok") && lowerTitle.Contains(" | "))
                    {
                        info.MediaSource = "TikTok";
                        info.IsTikTokRunning = true;
                        break;
                    }
                    else if (lowerTitle.Contains("instagram") && (lowerTitle.Contains("reel") || lowerTitle.Contains("video")))
                    {
                         info.MediaSource = "Instagram";
                         info.IsInstagramRunning = true;
                         break;
                    }
                    else if ((lowerTitle.Contains("twitter") || lowerTitle.Contains(" / x")) && (lowerTitle.Contains("video") || lowerTitle.Contains("watch")))
                    {
                         info.MediaSource = "Twitter";
                         info.IsTwitterRunning = true;
                         break;
                    }
                }
            }

            // Check signature to see if we need to update cached thumbnail
            var currentSignature = $"{info.CurrentTrack}|{info.CurrentArtist}";
            
            if (!forceRefresh && currentSignature == _lastTrackSignature && _cachedThumbnail != null)
            {
                // Reuse cache
                info.Thumbnail = _cachedThumbnail;
            }
            else
            {
                // New track or first load
                var thumbnail = mediaProperties.Thumbnail;
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
                                _cachedThumbnail = newBitmap;
                                _lastTrackSignature = currentSignature;
                                info.Thumbnail = _cachedThumbnail;
                            }
                        }
                    }
                    catch
                    {
                        // Thumbnail fetch failed
                    }
                }
            }
            
            // Get timeline properties for progress bar
            try
            {
                var timeline = session.GetTimelineProperties();
                if (timeline != null)
                {
                    info.Position = timeline.Position;
                    
                    // Try EndTime - StartTime first, fallback to MaxSeekTime
                    var duration = timeline.EndTime - timeline.StartTime;
                    if (duration <= TimeSpan.Zero)
                    {
                        duration = timeline.MaxSeekTime;
                    }
                    info.Duration = duration;
                    
                    // Debug output
                    System.Diagnostics.Debug.WriteLine($"[MediaService] Position: {info.Position}, Duration: {info.Duration}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MediaService] Timeline error: {ex.Message}");
            }

            
            // Get playback status - default to true if we have active media
            info.IsPlaying = info.IsAnyMediaPlaying; // sensible default
            try
            {
                var playbackInfo = session.GetPlaybackInfo();
                if (playbackInfo != null)
                {
                    info.IsPlaying = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    System.Diagnostics.Debug.WriteLine($"[MediaService] PlaybackStatus: {playbackInfo.PlaybackStatus}, IsPlaying: {info.IsPlaying}");
                }
            }
            catch (Exception ex) 
            { 
                System.Diagnostics.Debug.WriteLine($"[MediaService] Playback error: {ex.Message}");
            }
        }
        catch
        {
            // Session not available
        }
    }

    private async Task<BitmapImage?> ConvertToWpfBitmapAsync(IRandomAccessStreamWithContentType stream)
    {
        try
        {
            var memoryStream = new MemoryStream();
            
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
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
            });

            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private List<string> GetAllWindowTitles()
    {
        var titles = new List<string>();

        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd))
                return true;

            int length = GetWindowTextLength(hWnd);
            if (length == 0)
                return true;

            var sb = new StringBuilder(length + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            var title = sb.ToString();
            
            if (!string.IsNullOrWhiteSpace(title))
            {
                titles.Add(title);
            }

            return true;
        }, IntPtr.Zero);

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
        
        var separators = new[] { 
            " - YouTube", " – YouTube", " - SoundCloud", " | Facebook", 
            " - TikTok", " / X", " | TikTok", " • Instagram",
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
                    var currentPos = timeline.Position;
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
