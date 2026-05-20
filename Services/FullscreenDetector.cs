using System;
using System.Runtime.InteropServices;
using System.Text;
using static VNotch.Services.Win32Interop;

namespace VNotch.Services;

internal enum FullscreenType
{
    None,
    ExclusiveFullscreen,
    WindowedFullscreen
}

internal static class FullscreenDetector
{
    // Class names that must never trigger hide. Covers shell surfaces, IME,
    // start menu, notification flyouts, and other transient overlays that
    // some apps create at near-fullscreen sizes.
    private static readonly string[] BlockedClassNamesExact = new[]
    {
        "Progman",                   // Desktop
        "WorkerW",                   // Desktop wallpaper worker
        "Shell_TrayWnd",             // Primary taskbar
        "Shell_SecondaryTrayWnd",    // Secondary taskbar
        "NotifyIconOverflowWindow",  // Tray overflow
        "TaskListThumbnailWnd",      // Alt-Tab / hover thumbnails
        "MultitaskingViewFrame",     // Task View
        "ForegroundStaging",         // System staging surface
        "XamlExplorerHostIslandWindow",
        "Windows.UI.Input.InputSite.WindowClass",
        "Search_app",                // Old Search overlay
        "Tooltips_Class32",          // Native tooltips
        "TaskSwitcherWnd",           // Win+Tab
        "TaskSwitcherOverlayWnd",
        "Windows.Internal.Shell.TabProxyWindow",
        "Microsoft.UI.Content.DesktopChildSiteBridge"
    };

    private static readonly string[] BlockedClassNamePrefixes = new[]
    {
        "Windows.UI.Core",   // Start, Action Center, etc.
        "ImmersiveLauncher",
        "TaskListOverlay",
        "MSCTFIME"
    };

    public static FullscreenType DetectFullscreenType(IntPtr hwnd, IntPtr notchHwnd)
    {
        return DetectFullscreenType(hwnd, notchHwnd, IntPtr.Zero);
    }

    /// <summary>
    /// Detect whether <paramref name="hwnd"/> is a fullscreen window. When
    /// <paramref name="notchMonitor"/> is non-zero, windows on a different
    /// monitor are ignored so a fullscreen app on another display does not
    /// hide the notch.
    /// </summary>
    public static FullscreenType DetectFullscreenType(IntPtr hwnd, IntPtr notchHwnd, IntPtr notchMonitor)
    {
        if (hwnd == IntPtr.Zero || hwnd == notchHwnd)
        {
            return FullscreenType.None;
        }

        if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || IsWindowCloaked(hwnd))
        {
            return FullscreenType.None;
        }

        if (!TryGetWindowBounds(hwnd, out var windowRect))
        {
            return FullscreenType.None;
        }

        int width = windowRect.Right - windowRect.Left;
        int height = windowRect.Bottom - windowRect.Top;
        // Reject obviously sub-fullscreen windows. Floor lifted from 200x120 to
        // 480x320 so small overlays / popups never qualify.
        if (width < 480 || height < 320)
        {
            return FullscreenType.None;
        }

        if (IsBlockedClass(hwnd))
        {
            return FullscreenType.None;
        }

        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return FullscreenType.None;
        }

        // Same-monitor gate: only hide when the fullscreen window lives on the
        // notch's monitor. This is the single biggest correctness fix for
        // multi-monitor users (a fullscreen game on display 2 must not affect
        // a notch on display 1).
        if (notchMonitor != IntPtr.Zero && monitor != notchMonitor)
        {
            return FullscreenType.None;
        }

        var monitorInfo = new MONITORINFO
        {
            cbSize = Marshal.SizeOf<MONITORINFO>()
        };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return FullscreenType.None;
        }

        var monitorRect = monitorInfo.rcMonitor;

        // Slightly looser tolerance helps on high-DPI / fractional scaling
        // setups where DwmGetWindowAttribute can be off by 1-2px from the
        // monitor edge.
        const int fullscreenTolerancePx = 6;

        // Get window placement once (used by both branches)
        var placement = new WINDOWPLACEMENT
        {
            length = Marshal.SizeOf<WINDOWPLACEMENT>()
        };
        bool isMaximized = GetWindowPlacement(hwnd, ref placement) && placement.showCmd == SW_SHOWMAXIMIZED;

        int style = GetWindowLong(hwnd, GWL_STYLE);
        bool hasCaption = (style & WS_CAPTION) == WS_CAPTION;
        bool hasResizeFrame = (style & WS_THICKFRAME) != 0;

        if (RectCoversArea(windowRect, monitorRect, fullscreenTolerancePx))
        {
            // Window covers entire monitor.
            // A normal maximized window with caption/border is NOT fullscreen.
            // Only borderless windows covering the full monitor are fullscreen.
            if (hasCaption || hasResizeFrame)
            {
                // Maximized windows are ignored — the user can interact normally.
                if (isMaximized)
                {
                    return FullscreenType.None;
                }

                // Borderless covering everything (e.g. browser F11) — windowed FS.
                return FullscreenType.WindowedFullscreen;
            }
            return FullscreenType.ExclusiveFullscreen;
        }

        // Windowed fullscreen detection: borderless window matching work area
        // (taskbar visible) is treated as windowed fullscreen.
        const int workAreaTolerancePx = 8;
        bool matchesWorkArea = RectMatchesArea(windowRect, monitorInfo.rcWork, workAreaTolerancePx);
        bool isBorderless = !hasCaption && !hasResizeFrame;
        bool isWindowedFullscreen = matchesWorkArea && (isBorderless || (isMaximized && !hasCaption));

        return isWindowedFullscreen ? FullscreenType.WindowedFullscreen : FullscreenType.None;
    }

    public static bool IsForegroundWindowFullscreen(IntPtr hwnd, IntPtr notchHwnd)
    {
        return DetectFullscreenType(hwnd, notchHwnd) != FullscreenType.None;
    }

    public static IntPtr GetWindowMonitor(IntPtr hwnd)
    {
        return hwnd == IntPtr.Zero
            ? IntPtr.Zero
            : MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
    }

    public static string TryGetProcessName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return string.Empty;
        }

        _ = GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0)
        {
            return string.Empty;
        }

        try
        {
            return System.Diagnostics.Process.GetProcessById((int)processId).ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsBlockedClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(160);
        if (GetClassName(hwnd, sb, sb.Capacity) <= 0) return false;
        string className = sb.ToString();

        for (int i = 0; i < BlockedClassNamesExact.Length; i++)
        {
            if (string.Equals(className, BlockedClassNamesExact[i], StringComparison.Ordinal))
            {
                return true;
            }
        }

        for (int i = 0; i < BlockedClassNamePrefixes.Length; i++)
        {
            if (className.StartsWith(BlockedClassNamePrefixes[i], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetWindowBounds(IntPtr hwnd, out RECT rect)
    {
        if (DwmGetWindowAttributeRect(
                hwnd,
                DWMWA_EXTENDED_FRAME_BOUNDS,
                out rect,
                Marshal.SizeOf<RECT>()) == 0)
        {
            return true;
        }

        return GetWindowRect(hwnd, out rect);
    }

    private static bool IsWindowCloaked(IntPtr hwnd)
    {
        return DwmGetWindowAttributeInt(
                   hwnd,
                   DWMWA_CLOAKED,
                   out int cloaked,
                   sizeof(int)) == 0 &&
               cloaked != 0;
    }

    private static bool RectCoversArea(RECT rect, RECT area, int tolerancePx)
    {
        return rect.Left <= area.Left + tolerancePx &&
               rect.Top <= area.Top + tolerancePx &&
               rect.Right >= area.Right - tolerancePx &&
               rect.Bottom >= area.Bottom - tolerancePx;
    }

    private static bool RectMatchesArea(RECT rect, RECT area, int tolerancePx)
    {
        return Math.Abs(rect.Left - area.Left) <= tolerancePx &&
               Math.Abs(rect.Top - area.Top) <= tolerancePx &&
               Math.Abs(rect.Right - area.Right) <= tolerancePx &&
               Math.Abs(rect.Bottom - area.Bottom) <= tolerancePx;
    }
}
