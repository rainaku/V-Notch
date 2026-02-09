using System.Text.Json.Serialization;

namespace VNotch.Models;

public class NotchSettings
{
    // Appearance
    public int Width { get; set; } = 200;
    public int Height { get; set; } = 32;
    public int CornerRadius { get; set; } = 16;
    public double Opacity { get; set; } = 1.0;
    
    // Position
    public int MonitorIndex { get; set; } = 0; // 0 = Primary

    // Behavior
    public bool AutoStart { get; set; } = false;
    public bool EnableHoverExpand { get; set; } = true;
    public bool EnableCursorBypass { get; set; } = true;
    public bool EnableAnimations { get; set; } = true;
    public bool ShowCameraIndicator { get; set; } = true;

    // Animation Settings
    public double AnimationSpeed { get; set; } = 1.0; // 0.5 = slow, 1.0 = normal, 2.0 = fast
    public bool EnableBounceEffect { get; set; } = true;

    // Hover Settings
    public int HoverExpandDelay { get; set; } = 100; // ms before expand on hover
    public int HoverCollapseDelay { get; set; } = 500; // ms before collapse on leave
    public int HoverZoneMargin { get; set; } = 60; // px around notch for hover detection

    // Expand Settings
    public double CompactExpandMultiplier { get; set; } = 1.2;
    public double MediumExpandMultiplier { get; set; } = 1.8;
    public double LargeExpandMultiplier { get; set; } = 2.5;

    // Visual Effects
    public bool EnableShadow { get; set; } = true;
    public bool EnableGlowOnHover { get; set; } = true;
    public string NotchStyle { get; set; } = "default"; // default, minimal, vibrant

    // Content Settings
    public bool ShowMusicNotifications { get; set; } = true;
    public bool ShowSystemNotifications { get; set; } = true;
    public int NotificationDuration { get; set; } = 5000; // ms

    [JsonIgnore]
    public bool IsDirty { get; set; } = false;

    /// <summary>
    /// Create a deep copy of settings
    /// </summary>
    public NotchSettings Clone()
    {
        return new NotchSettings
        {
            Width = Width,
            Height = Height,
            CornerRadius = CornerRadius,
            Opacity = Opacity,
            MonitorIndex = MonitorIndex,
            AutoStart = AutoStart,
            EnableHoverExpand = EnableHoverExpand,
            EnableCursorBypass = EnableCursorBypass,
            EnableAnimations = EnableAnimations,
            ShowCameraIndicator = ShowCameraIndicator,
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
            ShowMusicNotifications = ShowMusicNotifications,
            ShowSystemNotifications = ShowSystemNotifications,
            NotificationDuration = NotificationDuration
        };
    }
}
