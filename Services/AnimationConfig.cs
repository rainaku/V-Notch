using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace VNotch.Services;

/// <summary>
/// Central source for the animation frame rate. Animations across the app cap their
/// <see cref="System.Windows.Media.Animation.Timeline.DesiredFrameRateProperty"/> to
/// <see cref="TargetFps"/> so users can trade motion smoothness for lower render/GPU load.
/// </summary>
internal static class AnimationConfig
{
    // Floor keeps motion smooth if a panel reports an odd low rate; ceiling matches the
    // previous hardcoded maximum so high-refresh users see no change.
    public const int MinFps = 60;
    public const int MaxFps = 144;
    private const int FallbackFps = 144;

    private static int _targetFps = FallbackFps;
    private static int _configuredFps = FallbackFps;
    private static string? _deviceName;
    private static bool _hooked;

    /// <summary>Frame rate cap all animations should use.</summary>
    public static int TargetFps => _targetFps;

    /// <summary>Apply the user-selected animation frame cap.</summary>
    public static void Configure(int animationFps)
    {
        _configuredFps = Math.Clamp(animationFps, MinFps, MaxFps);
        Recompute();
    }

    /// <summary>
    /// Recompute <see cref="TargetFps"/> and subscribe to display-setting changes once.
    /// The device name is kept so older call sites can still notify this config when the
    /// notch monitor changes.
    /// </summary>
    public static void Refresh(string? deviceName = null)
    {
        if (deviceName != null) _deviceName = deviceName;

        if (!_hooked)
        {
            _hooked = true;
            SystemEvents.DisplaySettingsChanged += (_, _) => Recompute();
        }

        Recompute();
    }

    private static void Recompute()
    {
        _targetFps = Math.Clamp(_configuredFps, MinFps, MaxFps);
    }

    private static int? DetectRefreshHz(string? deviceName)
    {
        try
        {
            var dm = new Win32Interop.DEVMODE { dmSize = (ushort)Marshal.SizeOf<Win32Interop.DEVMODE>() };
            if (Win32Interop.EnumDisplaySettings(deviceName, Win32Interop.ENUM_CURRENT_SETTINGS, ref dm))
            {
                // 0 and 1 are legacy "default/unknown" sentinels for dmDisplayFrequency.
                if (dm.dmDisplayFrequency > 1) return (int)dm.dmDisplayFrequency;
            }
        }
        catch
        {
            // Fall through to fallback on any interop failure.
        }

        return null;
    }
}
