using System.Windows.Media;

namespace VNotch.Models;

/// <summary>
/// Content that can be displayed in the expanded notch
/// Similar to Dynamic Island content providers
/// </summary>
public class NotchContent
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public NotchContentType ContentType { get; set; } = NotchContentType.Info;
    public NotchContentPriority Priority { get; set; } = NotchContentPriority.Normal;
    public ImageSource? Icon { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public TimeSpan? Duration { get; set; }
    public object? Data { get; set; }

    // Visual settings
    public Color? AccentColor { get; set; }
    public bool ShowProgress { get; set; } = false;
    public double Progress { get; set; } = 0;

    // Interaction settings
    public Action? OnTap { get; set; }
    public Action? OnLongPress { get; set; }
    public bool IsInteractive { get; set; } = false;
}

/// <summary>
/// Types of content the notch can display
/// </summary>
public enum NotchContentType
{
    /// <summary>Simple info display (title + subtitle)</summary>
    Info,
    
    /// <summary>Music player with controls</summary>
    MusicPlayer,
    
    /// <summary>Timer/Stopwatch</summary>
    Timer,
    
    /// <summary>Notification alert</summary>
    Notification,
    
    /// <summary>Call/Meeting indicator</summary>
    Call,
    
    /// <summary>Download/Upload progress</summary>
    Progress,
    
    /// <summary>System status (battery, wifi, etc.)</summary>
    SystemStatus,
    
    /// <summary>Custom content</summary>
    Custom
}

/// <summary>
/// Priority levels for content display
/// Higher priority content will be shown over lower priority
/// </summary>
public enum NotchContentPriority
{
    /// <summary>Low priority, shown when nothing else</summary>
    Low = 0,
    
    /// <summary>Normal priority</summary>
    Normal = 1,
    
    /// <summary>High priority, important info</summary>
    High = 2,
    
    /// <summary>Critical priority, always shown</summary>
    Critical = 3
}

/// <summary>
/// Music player specific content
/// </summary>
public class MusicPlayerContent : NotchContent
{
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public ImageSource? AlbumArt { get; set; }
    public bool IsPlaying { get; set; } = false;
    public TimeSpan CurrentPosition { get; set; }
    public TimeSpan TotalDuration { get; set; }

    public MusicPlayerContent()
    {
        ContentType = NotchContentType.MusicPlayer;
        ShowProgress = true;
    }

    public double ProgressPercentage => TotalDuration.TotalSeconds > 0 
        ? CurrentPosition.TotalSeconds / TotalDuration.TotalSeconds * 100 
        : 0;
}

/// <summary>
/// Timer specific content
/// </summary>
public class TimerContent : NotchContent
{
    public TimeSpan TimeRemaining { get; set; }
    public TimeSpan TotalTime { get; set; }
    public bool IsPaused { get; set; } = false;

    public TimerContent()
    {
        ContentType = NotchContentType.Timer;
        ShowProgress = true;
        Priority = NotchContentPriority.High;
    }

    public double ProgressPercentage => TotalTime.TotalSeconds > 0
        ? (1 - TimeRemaining.TotalSeconds / TotalTime.TotalSeconds) * 100
        : 0;
}

/// <summary>
/// Notification specific content
/// </summary>
public class NotificationContent : NotchContent
{
    public string AppName { get; set; } = string.Empty;
    public ImageSource? AppIcon { get; set; }
    public string Message { get; set; } = string.Empty;

    public NotificationContent()
    {
        ContentType = NotchContentType.Notification;
        Priority = NotchContentPriority.High;
        Duration = TimeSpan.FromSeconds(5);
    }
}
