using System.Windows.Threading;
using VNotch.Models;

namespace VNotch.Services;

/// <summary>
/// Manages content displayed in the notch
/// Handles content queue, priority, and auto-dismiss
/// </summary>
public class ContentManager : IDisposable
{
    private readonly List<NotchContent> _contentQueue = new();
    private readonly DispatcherTimer _autoDismissTimer;
    private NotchContent? _currentContent;
    private bool _disposed;

    public event EventHandler<NotchContent?>? ContentChanged;
    public event EventHandler<NotchContent>? ContentAdded;
    public event EventHandler<NotchContent>? ContentRemoved;

    public NotchContent? CurrentContent => _currentContent;
    public IReadOnlyList<NotchContent> Queue => _contentQueue.AsReadOnly();

    public ContentManager()
    {
        _autoDismissTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _autoDismissTimer.Tick += AutoDismissTimer_Tick;
        _autoDismissTimer.Start();
    }

    /// <summary>
    /// Add content to the display queue
    /// </summary>
    public void AddContent(NotchContent content)
    {
        _contentQueue.Add(content);
        _contentQueue.Sort((a, b) => b.Priority.CompareTo(a.Priority)); // Sort by priority desc
        ContentAdded?.Invoke(this, content);
        UpdateCurrentContent();
    }

    /// <summary>
    /// Remove content from the queue
    /// </summary>
    public void RemoveContent(string contentId)
    {
        var content = _contentQueue.FirstOrDefault(c => c.Id == contentId);
        if (content != null)
        {
            _contentQueue.Remove(content);
            ContentRemoved?.Invoke(this, content);
            UpdateCurrentContent();
        }
    }

    /// <summary>
    /// Remove content by type
    /// </summary>
    public void RemoveContentByType(NotchContentType type)
    {
        var toRemove = _contentQueue.Where(c => c.ContentType == type).ToList();
        foreach (var content in toRemove)
        {
            _contentQueue.Remove(content);
            ContentRemoved?.Invoke(this, content);
        }
        UpdateCurrentContent();
    }

    /// <summary>
    /// Clear all content
    /// </summary>
    public void ClearAll()
    {
        _contentQueue.Clear();
        _currentContent = null;
        ContentChanged?.Invoke(this, null);
    }

    /// <summary>
    /// Update content (e.g., music progress)
    /// </summary>
    public void UpdateContent(string contentId, Action<NotchContent> updateAction)
    {
        var content = _contentQueue.FirstOrDefault(c => c.Id == contentId);
        if (content != null)
        {
            updateAction(content);
            if (_currentContent?.Id == contentId)
            {
                ContentChanged?.Invoke(this, _currentContent);
            }
        }
    }

    /// <summary>
    /// Show music player content
    /// </summary>
    public string ShowMusicPlayer(string title, string artist, bool isPlaying)
    {
        var content = new MusicPlayerContent
        {
            Title = title,
            Artist = artist,
            Subtitle = artist,
            IsPlaying = isPlaying,
            Priority = NotchContentPriority.Normal
        };
        AddContent(content);
        return content.Id;
    }

    /// <summary>
    /// Show timer content
    /// </summary>
    public string ShowTimer(TimeSpan duration, string title = "Timer")
    {
        var content = new TimerContent
        {
            Title = title,
            TimeRemaining = duration,
            TotalTime = duration,
            Subtitle = FormatTimeSpan(duration),
            Priority = NotchContentPriority.High
        };
        AddContent(content);
        return content.Id;
    }

    /// <summary>
    /// Show notification content
    /// </summary>
    public string ShowNotification(string appName, string title, string message, TimeSpan? duration = null)
    {
        var content = new NotificationContent
        {
            AppName = appName,
            Title = title,
            Subtitle = message,
            Message = message,
            Duration = duration ?? TimeSpan.FromSeconds(5),
            Priority = NotchContentPriority.High
        };
        AddContent(content);
        return content.Id;
    }

    /// <summary>
    /// Show simple info content
    /// </summary>
    public string ShowInfo(string title, string subtitle, TimeSpan? duration = null)
    {
        var content = new NotchContent
        {
            Title = title,
            Subtitle = subtitle,
            ContentType = NotchContentType.Info,
            Duration = duration,
            Priority = NotchContentPriority.Normal
        };
        AddContent(content);
        return content.Id;
    }

    private void UpdateCurrentContent()
    {
        var newCurrent = _contentQueue.FirstOrDefault();
        if (_currentContent?.Id != newCurrent?.Id)
        {
            _currentContent = newCurrent;
            ContentChanged?.Invoke(this, _currentContent);
        }
    }

    private void AutoDismissTimer_Tick(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        var toRemove = new List<NotchContent>();

        foreach (var content in _contentQueue)
        {
            if (content.Duration.HasValue)
            {
                var elapsed = now - content.CreatedAt;
                if (elapsed >= content.Duration.Value)
                {
                    toRemove.Add(content);
                }
            }
        }

        foreach (var content in toRemove)
        {
            _contentQueue.Remove(content);
            ContentRemoved?.Invoke(this, content);
        }

        if (toRemove.Count > 0)
        {
            UpdateCurrentContent();
        }
    }

    private static string FormatTimeSpan(TimeSpan time)
    {
        if (time.TotalHours >= 1)
        {
            return $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}";
        }
        return $"{time.Minutes}:{time.Seconds:D2}";
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _autoDismissTimer.Stop();
            _disposed = true;
        }
    }
}
