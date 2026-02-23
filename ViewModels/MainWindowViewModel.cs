using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Media.Imaging;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.ViewModels;

/// <summary>
/// Main ViewModel for the Notch window.
/// Holds observable state and commands — no System.Windows references.
/// Animation orchestration stays in View code-behind.
/// </summary>
public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IMediaDetectionService _mediaService;
    private readonly ISettingsService _settingsService;
    private readonly IVolumeService _volumeService;
    private readonly IBatteryService _batteryService;
    private readonly IDispatcherService _dispatcher;

    #region Observable Properties — State

    [ObservableProperty]
    private bool _isMusicCompactMode;

    /// <summary>
    /// Callback set by View to report whether the notch is currently expanded.
    /// Used by progress tracking to decide whether to render.
    /// </summary>
    public Func<bool>? IsExpandedCheck { get; set; }

    [ObservableProperty]
    private bool _isNotchVisible = true;

    [ObservableProperty]
    private bool _isPlaying = true;

    [ObservableProperty]
    private string _trackTitle = "No media playing";

    [ObservableProperty]
    private string _trackArtist = "Open Spotify or YouTube";

    [ObservableProperty]
    private string _mediaSourceName = "Now Playing";

    [ObservableProperty]
    private string _mediaSourceIcon = "";

    [ObservableProperty]
    private BitmapImage? _currentThumbnail;

    [ObservableProperty]
    private bool _hasThumbnail;

    [ObservableProperty]
    private MediaInfo? _currentMediaInfo;

    #endregion

    #region Observable Properties — Progress

    [ObservableProperty]
    private double _progressRatio;

    [ObservableProperty]
    private string _currentTimeText = "0:00";

    [ObservableProperty]
    private string _remainingTimeText = "0:00";

    [ObservableProperty]
    private bool _isMediaPlaying;

    [ObservableProperty]
    private bool _hasTimeline;

    [ObservableProperty]
    private bool _isIndeterminate;

    [ObservableProperty]
    private bool _isSeekEnabled;

    private DateTime _lastMediaUpdate = DateTime.Now;
    private TimeSpan _lastKnownPosition = TimeSpan.Zero;
    private TimeSpan _lastKnownDuration = TimeSpan.Zero;
    private DateTime _seekDebounceUntil = DateTime.MinValue;
    private bool _isDraggingProgress = false;
    private string _lastProgressSignature = "";
    private string _lastSessionId = "";

    #endregion

    #region Observable Properties — Battery & Calendar

    [ObservableProperty]
    private string _batteryPercentText = "N/A";

    [ObservableProperty]
    private double _batteryFillWidth = 26;

    [ObservableProperty]
    private bool _isBatteryCharging;

    [ObservableProperty]
    private bool _isBatteryLow;

    [ObservableProperty]
    private string _calendarMonth = "";

    [ObservableProperty]
    private string _calendarDay = "";

    #endregion

    #region Observable Properties — Volume

    [ObservableProperty]
    private float _currentVolume = 0.5f;

    [ObservableProperty]
    private bool _isVolumeMuted;

    [ObservableProperty]
    private string _volumeIconText = "\uE995";

    #endregion

    #region Observable Properties — Settings

    [ObservableProperty]
    private NotchSettings _settings;

    [ObservableProperty]
    private double _collapsedWidth;

    [ObservableProperty]
    private double _collapsedHeight;

    [ObservableProperty]
    private double _cornerRadiusCollapsed;

    #endregion

    /// <summary>
    /// Fired when a new track is detected (signature changed) to trigger UI animations.
    /// </summary>
    public event EventHandler<MediaInfo>? NewTrackDetected;

    /// <summary>
    /// Fired on every media info update from the service.
    /// View code-behind subscribes to handle UI-specific updates.
    /// </summary>
    public event EventHandler<MediaInfo>? MediaInfoUpdated;

    /// <summary>
    /// Fired when play/pause state toggled by user command, to trigger icon animation.
    /// </summary>
    public event EventHandler<bool>? PlayPauseToggled;

    /// <summary>
    /// Fired when settings changed, to trigger visual refresh.
    /// </summary>
    public event EventHandler<NotchSettings>? SettingsApplied;

    /// <summary>
    /// Fired when session transitions, for opacity animation.
    /// </summary>
    public event EventHandler? SessionTransitioned;

    /// <summary>
    /// Fired when next track action triggered, for skip animation.
    /// </summary>
    public event EventHandler? NextTrackTriggered;

    /// <summary>
    /// Fired when previous track action triggered, for skip animation.
    /// </summary>
    public event EventHandler? PreviousTrackTriggered;

    private DateTime _lastMediaActionTime = DateTime.MinValue;
    private string _lastAnimatedTrackSignature = "";

    public MainWindowViewModel(
        IMediaDetectionService mediaService,
        ISettingsService settingsService,
        IVolumeService volumeService,
        IBatteryService batteryService,
        IDispatcherService dispatcher)
    {
        _mediaService = mediaService;
        _settingsService = settingsService;
        _volumeService = volumeService;
        _batteryService = batteryService;
        _dispatcher = dispatcher;

        _settings = _settingsService.Load();
        _collapsedWidth = _settings.Width;
        _collapsedHeight = _settings.Height;
        _cornerRadiusCollapsed = _settings.CornerRadius;

        _mediaService.MediaChanged += OnMediaChanged;
    }

    #region Initialization

    public void Initialize()
    {
        _mediaService.Start();
        UpdateBatteryInfo();
        UpdateCalendarInfo();
    }

    #endregion

    #region Media Changed Handler

    private void OnMediaChanged(object? sender, MediaInfo info)
    {
        CurrentMediaInfo = info;
        _lastMediaUpdate = DateTime.Now;

        _dispatcher.BeginInvoke(() =>
        {
            bool hasRealTrack = !string.IsNullOrEmpty(info.CurrentTrack);

            // Update media source icon
            MediaSourceIcon = hasRealTrack ? (info.MediaSource ?? "") : "";

            // Update track info
            if (hasRealTrack)
            {
                MediaSourceName = info.MediaSource;
                TrackTitle = info.CurrentTrack;

                if (!string.IsNullOrEmpty(info.CurrentArtist) &&
                    info.CurrentArtist != "YouTube" &&
                    info.CurrentArtist != "Browser" &&
                    info.CurrentArtist != "Spotify")
                {
                    TrackArtist = info.CurrentArtist;
                }
                else if (!string.IsNullOrEmpty(info.MediaSource))
                {
                    TrackArtist = info.MediaSource;
                }
                else
                {
                    TrackArtist = "Unknown Artist";
                }
            }
            else
            {
                TrackTitle = "No media playing";
                TrackArtist = "Open Spotify or YouTube";
                MediaSourceName = "Now Playing";
            }

            // Detect new track
            string currentSig = info.GetSignature();
            bool isNewTrack = currentSig != _lastAnimatedTrackSignature;

            if (hasRealTrack && info.HasThumbnail && info.Thumbnail != null)
            {
                CurrentThumbnail = info.Thumbnail;
                HasThumbnail = true;

                if (isNewTrack)
                {
                    _lastAnimatedTrackSignature = currentSig;
                    NewTrackDetected?.Invoke(this, info);
                }
            }
            else if (!hasRealTrack)
            {
                _lastAnimatedTrackSignature = "";
                CurrentThumbnail = null;
                HasThumbnail = false;
            }

            // Update play state
            if ((DateTime.Now - _lastMediaActionTime).TotalMilliseconds > 500 && IsPlaying != info.IsPlaying)
            {
                IsPlaying = info.IsPlaying;
                PlayPauseToggled?.Invoke(this, IsPlaying);
            }

            // Update progress tracking
            UpdateProgressTracking(info);

            // Update music compact mode
            bool shouldBeCompact = info != null && info.IsAnyMediaPlaying && !string.IsNullOrEmpty(info.CurrentTrack);
            if (info?.MediaSource == "Browser" && string.IsNullOrEmpty(info.CurrentTrack)) shouldBeCompact = false;

            CollapsedWidth = shouldBeCompact ? 180 : Settings.Width;
            IsMusicCompactMode = shouldBeCompact;

            // Forward to view for UI-specific handling
            MediaInfoUpdated?.Invoke(this, info!);
        });
    }

    #endregion

    #region Progress Tracking

    private void UpdateProgressTracking(MediaInfo info)
    {
        bool isSessionSwitch = !string.IsNullOrEmpty(info.SourceAppId) && info.SourceAppId != _lastSessionId;
        if (isSessionSwitch)
        {
            _lastSessionId = info.SourceAppId;
            SessionTransitioned?.Invoke(this, EventArgs.Empty);
        }

        CurrentMediaInfo = info;

        bool showProgressDetails = info.IsAnyMediaPlaying && (info.HasTimeline || info.IsIndeterminate);

        if (showProgressDetails)
        {
            if (_isDraggingProgress) return;

            string sig = $"{info.SourceAppId}|{info.MediaSource}|{info.CurrentTrack}|{info.CurrentArtist}";
            if (sig != _lastProgressSignature)
            {
                _lastProgressSignature = sig;
                _seekDebounceUntil = DateTime.MinValue;

                if (info.Duration.TotalSeconds > 0) _lastKnownDuration = info.Duration;
                _lastKnownPosition = info.Position;
                _lastMediaUpdate = DateTime.Now;
            }

            bool inSeekDebounce = DateTime.Now < _seekDebounceUntil;
            if (inSeekDebounce)
            {
                IsMediaPlaying = info.IsAnyMediaPlaying;
                if (info.Duration.TotalSeconds > 0 && info.Duration != _lastKnownDuration)
                    _lastKnownDuration = info.Duration;
                if (IsExpandedCheck?.Invoke() == true) RenderProgressBar();
                return;
            }

            if (info.HasTimeline)
            {
                if (info.Duration.TotalSeconds > 0) _lastKnownDuration = info.Duration;
                _lastKnownPosition = info.Position;
                _lastMediaUpdate = info.LastUpdated.LocalDateTime;

                IsIndeterminate = false;
                HasTimeline = true;
            }
            else if (info.IsIndeterminate)
            {
                IsIndeterminate = true;
                HasTimeline = false;
            }

            IsSeekEnabled = info.IsSeekEnabled;
            IsMediaPlaying = info.IsPlaying;

            if (IsExpandedCheck?.Invoke() == true) RenderProgressBar();
        }
        else
        {
            IsMediaPlaying = false;
            _lastSessionId = "";
            IsIndeterminate = false;
            HasTimeline = true;
            _lastKnownDuration = TimeSpan.Zero;
            _lastKnownPosition = TimeSpan.Zero;

            ProgressRatio = 0;
            CurrentTimeText = "0:00";
            RemainingTimeText = "0:00";

            if (IsExpandedCheck?.Invoke() == true) RenderProgressBar();
        }
    }

    /// <summary>
    /// Called from progress timer tick (in code-behind) to update progress bar.
    /// </summary>
    public void RenderProgressBar()
    {
        if (_isDraggingProgress || CurrentMediaInfo == null) return;

        var duration = _lastKnownDuration;
        if (duration.TotalSeconds <= 0)
        {
            CurrentTimeText = "--:--";
            RemainingTimeText = "--:--";
            ProgressRatio = 0;
            return;
        }

        TimeSpan displayPosition;
        if (IsMediaPlaying)
        {
            var timeSinceUpdate = DateTime.Now - _lastMediaUpdate;

            double capSeconds = (CurrentMediaInfo.IsThrottled) ? 3600 :
                               (CurrentMediaInfo.MediaSource == "YouTube" || CurrentMediaInfo.MediaSource == "Browser") ? 600 : 30;

            if (timeSinceUpdate > TimeSpan.FromSeconds(capSeconds))
                timeSinceUpdate = TimeSpan.FromSeconds(capSeconds);

            displayPosition = _lastKnownPosition + TimeSpan.FromTicks((long)(timeSinceUpdate.Ticks * CurrentMediaInfo.PlaybackRate));

            if (displayPosition > duration) displayPosition = duration;
        }
        else
        {
            displayPosition = _lastKnownPosition;
        }

        if (displayPosition < TimeSpan.Zero) displayPosition = TimeSpan.Zero;

        double ratio = displayPosition.TotalSeconds / duration.TotalSeconds;
        ratio = Math.Clamp(ratio, 0, 1);

        ProgressRatio = ratio;
        CurrentTimeText = FormatTime(displayPosition);
        RemainingTimeText = FormatTime(duration);
    }

    public void StartDraggingProgress() => _isDraggingProgress = true;

    public void StopDraggingProgress() => _isDraggingProgress = false;

    public void UpdateDragPosition(double ratio)
    {
        if (_lastKnownDuration.TotalSeconds <= 0) return;
        ratio = Math.Clamp(ratio, 0, 1);
        ProgressRatio = ratio;
        var position = TimeSpan.FromSeconds(_lastKnownDuration.TotalSeconds * ratio);
        CurrentTimeText = FormatTime(position);
    }

    public TimeSpan GetDragSeekPosition(double ratio)
    {
        ratio = Math.Clamp(ratio, 0, 1);
        return TimeSpan.FromSeconds(_lastKnownDuration.TotalSeconds * ratio);
    }

    public async Task SeekToPosition(TimeSpan newPos)
    {
        if (_lastKnownDuration.TotalSeconds <= 0) return;

        try
        {
            _lastKnownPosition = newPos;
            _lastMediaUpdate = DateTime.Now;
            _seekDebounceUntil = DateTime.Now.AddSeconds(2.5);
            if (IsExpandedCheck?.Invoke() == true) RenderProgressBar();

            await _mediaService.SeekAsync(newPos);
        }
        catch { }
    }

    public async Task SeekRelative(double seconds)
    {
        if (_lastKnownDuration.TotalSeconds <= 0) return;

        var elapsed = DateTime.Now - _lastMediaUpdate;
        var currentPos = _lastKnownPosition + (IsMediaPlaying ? elapsed : TimeSpan.Zero);

        var newPos = currentPos + TimeSpan.FromSeconds(seconds);

        if (newPos < TimeSpan.Zero) newPos = TimeSpan.Zero;
        if (newPos > _lastKnownDuration) newPos = _lastKnownDuration;

        _lastKnownPosition = newPos;
        _lastMediaUpdate = DateTime.Now;
        _seekDebounceUntil = DateTime.Now.AddSeconds(2.5);
        if (IsExpandedCheck?.Invoke() == true) RenderProgressBar();

        try
        {
            await _mediaService.SeekRelativeAsync(seconds);
        }
        catch { }
    }

    public bool ShouldShowProgress => CurrentMediaInfo != null && CurrentMediaInfo.IsAnyMediaPlaying &&
                                       (CurrentMediaInfo.HasTimeline || CurrentMediaInfo.IsIndeterminate);

    private static string FormatTime(TimeSpan time)
    {
        if (time.TotalHours >= 1)
            return time.ToString(@"h\:mm\:ss");
        return time.ToString(@"m\:ss");
    }

    #endregion

    #region Commands — Media Controls

    [RelayCommand]
    private async Task PlayPause()
    {
        if ((DateTime.Now - _lastMediaActionTime).TotalMilliseconds < 500) return;
        _lastMediaActionTime = DateTime.Now;

        IsPlaying = !IsPlaying;
        PlayPauseToggled?.Invoke(this, IsPlaying);

        await _mediaService.PlayPauseAsync();
    }

    [RelayCommand]
    private async Task NextTrack()
    {
        if ((DateTime.Now - _lastMediaActionTime).TotalMilliseconds < 500) return;
        _lastMediaActionTime = DateTime.Now;

        NextTrackTriggered?.Invoke(this, EventArgs.Empty);

        if (CurrentMediaInfo?.IsVideoSource == true)
        {
            await SeekRelative(15);
        }
        else
        {
            await _mediaService.NextTrackAsync();
        }
    }

    [RelayCommand]
    private async Task PreviousTrack()
    {
        if ((DateTime.Now - _lastMediaActionTime).TotalMilliseconds < 500) return;
        _lastMediaActionTime = DateTime.Now;

        PreviousTrackTriggered?.Invoke(this, EventArgs.Empty);

        if (CurrentMediaInfo?.IsVideoSource == true)
        {
            await SeekRelative(-15);
        }
        else
        {
            await _mediaService.PreviousTrackAsync();
        }
    }

    #endregion

    #region Commands — Volume

    [RelayCommand]
    private void ToggleMute()
    {
        if (_volumeService.IsAvailable)
        {
            _volumeService.ToggleMute();
            SyncVolumeFromSystem();
        }
    }

    public void SetVolumeFromRatio(float ratio)
    {
        CurrentVolume = Math.Clamp(ratio, 0f, 1f);
        UpdateVolumeIcon(CurrentVolume, false);

        if (_volumeService.IsAvailable)
        {
            _volumeService.SetVolume(CurrentVolume);
        }
    }

    public void SyncVolumeFromSystem()
    {
        if (_volumeService.IsAvailable)
        {
            CurrentVolume = _volumeService.GetVolume();
            IsVolumeMuted = _volumeService.GetMute();
            UpdateVolumeIcon(CurrentVolume, IsVolumeMuted);
        }
    }

    private void UpdateVolumeIcon(float volume, bool isMuted)
    {
        if (isMuted || volume <= 0.01f)
        {
            VolumeIconText = "\uE74F";
        }
        else if (volume < 0.33f)
        {
            VolumeIconText = "\uE993";
        }
        else if (volume < 0.66f)
        {
            VolumeIconText = "\uE994";
        }
        else
        {
            VolumeIconText = "\uE995";
        }
    }

    #endregion

    #region Commands — App Actions

    [RelayCommand]
    private void ToggleNotch()
    {
        IsNotchVisible = !IsNotchVisible;
    }

    #endregion

    #region Battery & Calendar

    public void UpdateBatteryInfo()
    {
        try
        {
            var battery = _batteryService.GetBatteryInfo();
            BatteryPercentText = battery.GetPercentageText();
            BatteryFillWidth = Math.Max(2, battery.Percentage / 100.0 * 26);
            IsBatteryCharging = battery.IsCharging;
            IsBatteryLow = battery.Percentage < 20;
        }
        catch
        {
            BatteryPercentText = "N/A";
        }
    }

    public void UpdateCalendarInfo()
    {
        var now = DateTime.Now;
        CalendarMonth = now.ToString("MMM");
        CalendarDay = now.Day.ToString();
    }

    #endregion

    #region Settings Management

    public void ApplyNewSettings(NotchSettings newSettings)
    {
        Settings = newSettings;
        _settingsService.Save(newSettings);
        CollapsedWidth = newSettings.Width;
        CollapsedHeight = newSettings.Height;
        CornerRadiusCollapsed = newSettings.CornerRadius;
        SettingsApplied?.Invoke(this, newSettings);
    }

    public NotchSettings LoadSettings() => _settingsService.Load();

    public void SaveSettings(NotchSettings settings) => _settingsService.Save(settings);

    #endregion

    #region Service Accessors (for code-behind that needs direct service access)

    public IMediaDetectionService MediaService => _mediaService;
    public IVolumeService VolumeService => _volumeService;
    public ISettingsService SettingsService => _settingsService;

    #endregion

    #region IDisposable

    public void Dispose()
    {
        _mediaService.MediaChanged -= OnMediaChanged;
        _mediaService.Dispose();
        _volumeService.Dispose();
    }

    #endregion
}
