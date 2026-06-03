using System.Reflection;
using System.Text.Json.Serialization;

namespace VNotch.Models;

public class NotchSettings
{
    public const string SystemBatteryDeviceId = "";
    public const string AutoBluetoothBatteryDeviceId = "__auto_bluetooth__";
    
    public int SettingsVersion { get; set; } = 0;

    public string LastRunVersion { get; set; } = "";

    public int Width { get; set; } = 230;
    public int DynamicIslandWidth { get; set; } = 260;
    public int Height { get; set; } = 32;
    public int CornerRadius { get; set; } = 8;
    public double Opacity { get; set; } = 1.0;
    public double MediaBlurBrightnessBoost { get; set; } = 2.0;
    public double MediaBlurDarkOverlay { get; set; } = 0.0;
    public bool EnableBlurEffects { get; set; } = true;
    public int AnimationFps { get; set; } = 144;

    public int MonitorIndex { get; set; } = 0; 

    public string CameraDeviceId { get; set; } = ""; // Empty = auto-detect first available
    public string VisualizerAudioDeviceId { get; set; } = ""; // Empty = default render endpoint
    public string BatteryDeviceId { get; set; } = SystemBatteryDeviceId; // Empty = system battery

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

    public bool EnableDynamicIslandMode { get; set; } = false;

    public bool HideOnExclusiveFullscreen { get; set; } = true;
    public bool HideOnWindowedFullscreen { get; set; } = true;

    public bool ShowMusicNotifications { get; set; } = true;
    public bool ShowSystemNotifications { get; set; } = true;

    public bool ShowBatteryIndicator { get; set; } = true;
    public int NotificationDuration { get; set; } = 5000; 

    public bool EnableSmartCrop { get; set; } = true;

    public bool EnableSubjectBlur { get; set; } = true;

    public bool EnableGestureControls { get; set; } = true;

    public bool EnableHelloGreeting { get; set; } = true;

    public bool EnableSpotifyLyrics { get; set; } = true;

    public bool EnableYouTubeSubtitles { get; set; } = true;

    public bool IsShelfUploadLimitUnlocked { get; set; } = true;

    public bool CopyShelfFilesToClipboard { get; set; } = false;

    public bool EnableYouTubeApi { get; set; } = false;
    public string YouTubeApiKey { get; set; } = "";

    public string SubtitlePriority { get; set; } = "native,english,auto";

    public string Language { get; set; } = "en"; // "en" or "vi"

    [JsonIgnore]
    public bool IsDirty { get; set; } = false;

    // ─── Cached property list for reflection-based Clone ───
    private static readonly PropertyInfo[] _cloneableProperties = Array.FindAll(
        typeof(NotchSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance),
        p => p.CanRead && p.CanWrite && p.GetCustomAttribute<JsonIgnoreAttribute>() == null);

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
