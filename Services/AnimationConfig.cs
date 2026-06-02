using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace VNotch.Services;

/// <summary>
/// Central source for the animation frame rate. Animations across the app cap their
/// <see cref="System.Windows.Media.Animation.Timeline.DesiredFrameRateProperty"/> to
/// <see cref="TargetFps"/> so the WPF render thread never produces more frames than the
/// active display can show. On a 60 Hz panel this roughly halves render/GPU load versus
/// the previous hardcoded 120/144; on high-refresh panels behavior is unchanged.
/// </summary>
internal static class AnimationConfig
{
    // Floor keeps motion smooth if a panel reports an odd low rate; ceiling matches the
    // previous hardcoded maximum so high-refresh users see no change.
    private const int MinFps = 60;
    private const int MaxFps = 144;
    private const int FallbackFps = 144;

    private static int _targetFps = FallbackFps;
    private static string? _deviceName;
    private static bool _hooked;

    /// <summary>Frame rate cap all animations should use, derived from the active monitor.</summary>
    public static int TargetFps => _targetFps;

    /// <summary>
    /// Recompute <see cref="TargetFps"/> from the given monitor's refresh rate.
    /// Pass the WinForms <c>Screen.DeviceName</c> of the monitor the notch lives on
    /// (null uses the primary display). Also subscribes to display-setting changes once.
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
        _targetFps = DetectRefreshHz(_deviceName) is int hz && hz > 0
            ? Math.Clamp(hz, MinFps, MaxFps)
            : FallbackFps;
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
