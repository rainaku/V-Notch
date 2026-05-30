using System.Reflection;
using System.Text.Json.Serialization;

namespace VNotch.Models;

public class NotchSettings
{
    
    public int SettingsVersion { get; set; } = 0;

    public int Width { get; set; } = 230;
    public int Height { get; set; } = 32;
    public int CornerRadius { get; set; } = 8;
    public double Opacity { get; set; } = 1.0;
    public double MediaBlurBrightnessBoost { get; set; } = 2.0;
    public double MediaBlurDarkOverlay { get; set; } = 0.0;

    public int MonitorIndex { get; set; } = 0; 

    public bool AutoStart { get; set; } = true;
    public bool EnableHoverExpand { get; set; } = false;
    public bool EnableCursorBypass { get; set; } = false;
    public bool EnableAnimations { get; set; } = true;
    public bool DisableMouseLeaveAutoClose { get; set; } = true;

    public double AnimationSpeed { get; set; } = 2.0;
    public bool EnableBounceEffect { get; set; } = true;

    public int HoverExpandDelay { get; set; } = 0; 
    public int HoverCollapseDelay { get; set; } = 500; 
    public int HoverZoneMargin { get; set; } = 60; 

    public double CompactExpandMultiplier { get; set; } = 1.2;
    public double MediumExpandMultiplier { get; set; } = 1.8;
    public double LargeExpandMultiplier { get; set; } = 2.5;

    public bool EnableShadow { get; set; } = true;
    public bool EnableGlowOnHover { get; set; } = true;
    public string NotchStyle { get; set; } = "default"; 

    /// <summary>
    /// When true, the notch detaches from the top edge of the screen and renders
    /// as a free-floating rounded rectangle (Dynamic Island style) with all four
    /// corners rounded and a small gap from the top of the display.
    /// </summary>
    public bool EnableDynamicIslandMode { get; set; } = false;

    public bool HideOnExclusiveFullscreen { get; set; } = true;
    public bool HideOnWindowedFullscreen { get; set; } = true;

    public bool ShowMusicNotifications { get; set; } = true;
    public bool ShowSystemNotifications { get; set; } = true;
    public int NotificationDuration { get; set; } = 5000; 

    public bool EnableSmartCrop { get; set; } = true;

    /// <summary>
    /// Use the on-device YOLOv8n model to detect the dominant subject in artwork
    /// and produce a "spotlight" background blur (Apple Music style) instead of a
    /// uniform blur. Falls back to uniform blur when no subject is found or the
    /// model isn't available.
    /// </summary>
    public bool EnableSubjectBlur { get; set; } = true;

    public bool EnableGestureControls { get; set; } = true;

    public bool EnableHelloGreeting { get; set; } = true;

    public bool EnableSpotifyLyrics { get; set; } = true;

    public bool EnableYouTubeSubtitles { get; set; } = true;

    public bool IsShelfUploadLimitUnlocked { get; set; } = true;

    public bool EnableYouTubeApi { get; set; } = false;
    public string YouTubeApiKey { get; set; } = "";

    public string Language { get; set; } = "en"; // "en" or "vi"

    [JsonIgnore]
    public bool IsDirty { get; set; } = false;

    // ─── Cached property list for reflection-based Clone ───
    private static readonly PropertyInfo[] _cloneableProperties = Array.FindAll(
        typeof(NotchSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance),
        p => p.CanRead && p.CanWrite && p.GetCustomAttribute<JsonIgnoreAttribute>() == null);

    /// <summary>
    /// Creates a deep copy of this settings instance.
    /// Uses cached reflection to automatically copy all serializable properties —
    /// new properties are included without manual maintenance.
    /// </summary>
    public NotchSettings Clone()
    {
        var clone = new NotchSettings();
        for (int i = 0; i < _cloneableProperties.Length; i++)
        {
            var prop = _cloneableProperties[i];
            prop.SetValue(clone, prop.GetValue(this));
        }
        return clone;
    }
}
