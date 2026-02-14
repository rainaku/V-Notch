using System.Text.Json.Serialization;

namespace VNotch.Models;

public class NotchSettings
{

    public int Width { get; set; } = 200;
    public int Height { get; set; } = 32;
    public int CornerRadius { get; set; } = 16;
    public double Opacity { get; set; } = 1.0;

    public int MonitorIndex { get; set; } = 0; 

    public bool AutoStart { get; set; } = false;
    public bool EnableHoverExpand { get; set; } = true;
    public bool EnableCursorBypass { get; set; } = true;
    public bool EnableAnimations { get; set; } = true;
    public bool ShowCameraIndicator { get; set; } = true;

    public double AnimationSpeed { get; set; } = 1.0; 
    public bool EnableBounceEffect { get; set; } = true;

    public int HoverExpandDelay { get; set; } = 100; 
    public int HoverCollapseDelay { get; set; } = 500; 
    public int HoverZoneMargin { get; set; } = 60; 

    public double CompactExpandMultiplier { get; set; } = 1.2;
    public double MediumExpandMultiplier { get; set; } = 1.8;
    public double LargeExpandMultiplier { get; set; } = 2.5;

    public bool EnableShadow { get; set; } = true;
    public bool EnableGlowOnHover { get; set; } = true;
    public string NotchStyle { get; set; } = "default"; 

    public bool ShowMusicNotifications { get; set; } = true;
    public bool ShowSystemNotifications { get; set; } = true;
    public int NotificationDuration { get; set; } = 5000; 

    [JsonIgnore]
    public bool IsDirty { get; set; } = false;

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