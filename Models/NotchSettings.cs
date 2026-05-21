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

    public bool HideOnExclusiveFullscreen { get; set; } = true;
    public bool HideOnWindowedFullscreen { get; set; } = true;

    public bool ShowMusicNotifications { get; set; } = true;
    public bool ShowSystemNotifications { get; set; } = true;
    public int NotificationDuration { get; set; } = 5000; 

    public bool EnableSmartCrop { get; set; } = true;

    public bool IsShelfUploadLimitUnlocked { get; set; } = true;

    public bool EnableYouTubeApi { get; set; } = false;
    public string YouTubeApiKey { get; set; } = "";

    public string Language { get; set; } = "en"; // "en" or "vi"

    [JsonIgnore]
    public bool IsDirty { get; set; } = false;

    public NotchSettings Clone()
    {
        return new NotchSettings
        {
            SettingsVersion = SettingsVersion,
            Width = Width,
            Height = Height,
            CornerRadius = CornerRadius,
            Opacity = Opacity,
            MediaBlurBrightnessBoost = MediaBlurBrightnessBoost,
            MediaBlurDarkOverlay = MediaBlurDarkOverlay,
            MonitorIndex = MonitorIndex,
            AutoStart = AutoStart,
            EnableHoverExpand = EnableHoverExpand,
            EnableCursorBypass = EnableCursorBypass,
            EnableAnimations = EnableAnimations,
            DisableMouseLeaveAutoClose = DisableMouseLeaveAutoClose,
            AnimationSpeed = AnimationSpeed,
            EnableBounceEffect = EnableBounceEffect,
            HoverExpandDelay = HoverExpandDelay,
            HoverCollapseDelay = HoverCollapseDelay,
            HoverZoneMargin = HoverZoneMargin,
            CompactExpandMultiplier = CompactExpandMultiplier,
            MediumExpandMultiplier = MediumExpandMultiplier,
            LargeExpandMultiplier = LargeExpandMultiplier,
            EnableShadow = EnableShadow,
            EnableGlowOnHover = EnableGlowOnHover,
            NotchStyle = NotchStyle,
            HideOnExclusiveFullscreen = HideOnExclusiveFullscreen,
            HideOnWindowedFullscreen = HideOnWindowedFullscreen,
            ShowMusicNotifications = ShowMusicNotifications,
            ShowSystemNotifications = ShowSystemNotifications,
            NotificationDuration = NotificationDuration,
            EnableSmartCrop = EnableSmartCrop,
            IsShelfUploadLimitUnlocked = IsShelfUploadLimitUnlocked,
            EnableYouTubeApi = EnableYouTubeApi,
            YouTubeApiKey = YouTubeApiKey,
            Language = Language
        };
    }
}
