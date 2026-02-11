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
    private BitmapImage? _cachedThumbnail;

    // Window title caching
    private List<string> _cachedWindowTitles = new();
    private DateTime _lastWindowEnumTime = DateTime.MinValue;
    private static readonly string[] _platformKeywords = { 
        "spotify", "youtube", "soundcloud", "facebook", "tiktok", "instagram", "twitter", " / x"
    };

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
#if DEBUG
            System.Diagnostics.Debug.WriteLine($"[MediaService] Failed to init SMTC: {ex.Message}");
#endif
        }
        
        _pollTimer.Start();
        
        // Initial detection with a slight delay to ensure SMTC is ready
        await Task.Delay(1000);
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
            // Clear cache to force fresh metadata fetch
            _lastTrackSignature = "";
            // Force refresh because metadata (like thumbnail) might have just arrived
            await UpdateMediaInfoAsync(forceRefresh: true);
        });
    }

    private async void OnSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null)
        {
            await dispatcher.InvokeAsync(async () =>
            {
                await SubscribeToCurrentSession();
                await UpdateMediaInfoAsync();
            });
        }
    }

    private async void PollTimer_Tick(object? sender, EventArgs e)
    {
        await UpdateMediaInfoAsync();
    }

    private async Task UpdateMediaInfoAsync(bool forceRefresh = false)
    {
        var info = new MediaInfo();
        List<string>? windowTitles = null;
        
        // First try to get info from Windows Media Session (includes thumbnail!)
        // Pass a factory to fetch titles only if needed
        await TryGetMediaSessionInfoAsync(info, forceRefresh, () => {
            windowTitles ??= GetAllWindowTitles();
            return windowTitles;
        });

        // OPTIMIZATION: If we already found valid media from SMTC (Modern API), 
        // skip the expensive EnumWindows call (Win32 API).
        // Only fallback to window titles if SMTC returned nothing or generic info.
        if (!info.IsAnyMediaPlaying || string.IsNullOrEmpty(info.CurrentTrack) || info.MediaSource == "Browser")
        {
            // Also check window titles for additional context
            windowTitles ??= GetAllWindowTitles();

        // Check Spotify process for playing state - Cache this once to avoid expensive Win32 calls in the loop
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
                        info.IsAnyMediaPlaying = true;
                        info.IsPlaying = true; 
                        info.MediaSource = "Spotify";
                        ParseSpotifyTitle(proc.MainWindowTitle, info);
                    }
                    break; // Found playing session, no need to check other spotify procs
                }
            }
            catch { }
        }

        foreach (var title in windowTitles)
        {
            var lowerTitle = title.ToLower();

            // Spotify detection (keywords)
            if (lowerTitle.Contains("spotify"))
            {
                info.IsSpotifyRunning = true;
            }
            
            // Check other platforms
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

    private async Task TryGetMediaSessionInfoAsync(MediaInfo info, bool forceRefresh, Func<List<string>> windowTitleFactory)
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
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[MediaService] Found {sessions.Count} sessions");
#endif
                
                foreach (var s in sessions)
                {
                    try 
                    {
                        var sourceApp = s.SourceAppUserModelId ?? "";
                        var playbackInfo = s.GetPlaybackInfo();
                        var isPlaying = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                        
#if DEBUG
                        System.Diagnostics.Debug.WriteLine($"[MediaService] Session: {sourceApp}, Playing: {isPlaying}");
#endif
                        
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
                                
#if DEBUG
                                System.Diagnostics.Debug.WriteLine($"[MediaService] Session {sourceApp}: Title='{props.Title}', Artist='{props.Artist}', Score={score}");
#endif
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
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[MediaService] Active display session changed: {session.SourceAppUserModelId}");
#endif
                
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
                         sessionSourceApp.Contains("msedge", StringComparison.OrdinalIgnoreCase) ||
                         sessionSourceApp.Contains("Opera", StringComparison.OrdinalIgnoreCase) ||
                         sessionSourceApp.Contains("Brave", StringComparison.OrdinalIgnoreCase) ||
                         sessionSourceApp.Contains("Vivaldi", StringComparison.OrdinalIgnoreCase) ||
                         sessionSourceApp.Contains("Coccoc", StringComparison.OrdinalIgnoreCase))
                {
                    // Browser - could be YouTube, etc.
                    if (string.IsNullOrEmpty(info.MediaSource))
                    {
                        info.MediaSource = "Browser";
                    }
                    
                    // Quick check on title/artist for YouTube hints in SMTC metadata itself
                    if (mediaProperties.Title?.Contains("YouTube", StringComparison.OrdinalIgnoreCase) == true || 
                        mediaProperties.Artist?.Contains("YouTube", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        info.MediaSource = "YouTube";
                    }
                }
            }
            
            // REFINEMENT: If source is "Browser", try to be more specific by checking window titles
            if (info.MediaSource == "Browser" || string.IsNullOrEmpty(info.MediaSource))
            {
                var windowTitles = windowTitleFactory();
                foreach (var title in windowTitles)
                {
                    var lowerTitle = title.ToLower();
                    
                    // Specific Platform Checks for Browser Sessions (Loosened matching)
                    if (lowerTitle.Contains("youtube") && !lowerTitle.StartsWith("youtube -") && lowerTitle != "youtube")
                    {
                         info.MediaSource = "YouTube";
                         info.IsYouTubeRunning = true;
                         break;
                    }
                    else if (lowerTitle.Contains("soundcloud") && lowerTitle.Contains(" - "))
                    {
                         info.MediaSource = "SoundCloud";
                         info.IsSoundCloudRunning = true;
                         break;
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
                                // Crop to square, removing bars/branding
                                newBitmap = CropToSquare(newBitmap, info.MediaSource) ?? newBitmap;
                                _cachedThumbnail = newBitmap;
                                _lastTrackSignature = currentSignature;
                                info.Thumbnail = _cachedThumbnail;
                            }
                        }
                    }
                    catch { }
                }

                // REFINEMENT: If it's YouTube, try to get the high-quality thumbnail
                if (info.MediaSource == "YouTube" && !string.IsNullOrEmpty(info.CurrentTrack))
                {
                    try
                    {
                        string? videoId = await TryGetYouTubeVideoIdAsync(info.CurrentTrack);
                        if (!string.IsNullOrEmpty(videoId))
                        {
                            string thumbnailUrl = $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg";
                            var frameBitmap = await DownloadImageAsync(thumbnailUrl);
                            if (frameBitmap != null)
                            {
                                // Crop to center square for YouTube thumbnails
                                frameBitmap = CropToSquare(frameBitmap, "YouTube") ?? frameBitmap;
                                info.Thumbnail = frameBitmap;
                                _cachedThumbnail = frameBitmap; // Cache the better one
                                _lastTrackSignature = currentSignature;
                            }
                        }
                    }
                    catch { }
                }
            }

            // Get timeline properties for progress bar
            try
            {
                var timeline = session.GetTimelineProperties();
                if (timeline != null)
                {
                    // SMTC Position is a snapshot at LastUpdatedTime. 
                    // To get the actual current position, we must add the elapsed time.
                    var timeSinceUpdate = DateTimeOffset.Now - timeline.LastUpdatedTime;
                    
                    // Only add elapsed time if the media is actually playing
                    var playbackInfo = session.GetPlaybackInfo();
                    bool isActuallyPlaying = playbackInfo != null && playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    
                    if (isActuallyPlaying && timeSinceUpdate > TimeSpan.Zero && timeSinceUpdate < TimeSpan.FromMinutes(5)) // Sanity check
                    {
                        info.Position = timeline.Position + timeSinceUpdate;
                    }
                    else
                    {
                        info.Position = timeline.Position;
                    }
                    
                    // Try EndTime - StartTime first, fallback to MaxSeekTime
                    var duration = timeline.EndTime - timeline.StartTime;
                    if (duration <= TimeSpan.Zero)
                    {
                        duration = timeline.MaxSeekTime;
                    }
                    info.Duration = duration;
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[MediaService] Timeline error: {ex.Message}");
#endif
            }

            
            // Get playback status from SMTC - this determines if media is truly playing vs paused
            try
            {
                var playbackInfo = session.GetPlaybackInfo();
                if (playbackInfo != null)
                {
                    info.IsPlaying = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
#if DEBUG
                    System.Diagnostics.Debug.WriteLine($"[MediaService] PlaybackStatus: {playbackInfo.PlaybackStatus}, IsPlaying: {info.IsPlaying}");
#endif
                }
                else
                {
                    // Fallback: assume playing if we have active media
                    info.IsPlaying = info.IsAnyMediaPlaying;
                }
            }
            catch (Exception ex) 
            { 
                // Fallback: assume playing if we have active media
                info.IsPlaying = info.IsAnyMediaPlaying;
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[MediaService] Playback error: {ex.Message}");
#endif
            }
        }
        catch
        {
            // Session not available
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
            
            if (!isSpotify && Math.Abs(width - height) < 2) return source;
            
            int squareSize;
            int offsetX, offsetY;
            
            if (isSpotify)
            {
                // Spotify: crop off bottom 20% (branding strip with Spotify logo)
                int contentHeight = (int)(height * 0.80);
                squareSize = Math.Min(width, contentHeight);
                offsetX = (width - squareSize) / 2;
                offsetY = (contentHeight - squareSize) / 2; // Center within content area
            }
            else if (width > height)
            {
                // Landscape (e.g. YouTube) - crop center square
                squareSize = height;
                offsetX = (width - squareSize) / 2;
                offsetY = 0;
            }
            else
            {
                // Portrait - crop from TOP
                squareSize = width;
                offsetX = 0;
                offsetY = 0;
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
        // Cache results for 5 seconds to reduce Win32 calls
        if ((DateTime.Now - _lastWindowEnumTime).TotalSeconds < 5)
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
