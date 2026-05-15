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
public static FullscreenType DetectFullscreenType(IntPtr hwnd, IntPtr notchHwnd)
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
        if (width < 200 || height < 120)
        {
            return FullscreenType.None;
        }

        var classNameBuilder = new StringBuilder(128);
        if (GetClassName(hwnd, classNameBuilder, classNameBuilder.Capacity) > 0)
        {
            string className = classNameBuilder.ToString();
            if (string.Equals(className, "Progman", StringComparison.Ordinal) ||
                string.Equals(className, "WorkerW", StringComparison.Ordinal) ||
                string.Equals(className, "Shell_TrayWnd", StringComparison.Ordinal) ||
                string.Equals(className, "MultitaskingViewFrame", StringComparison.Ordinal) ||
                string.Equals(className, "ForegroundStaging", StringComparison.Ordinal) ||
                string.Equals(className, "XamlExplorerHostIslandWindow", StringComparison.Ordinal) ||
                className.StartsWith("Windows.UI.Core", StringComparison.Ordinal))
            {
                return FullscreenType.None;
            }
        }

        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
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
        const int fullscreenTolerancePx = 4;

        // Get window placement once (used by both branches)
        var placement = new WINDOWPLACEMENT
        {
            length = Marshal.SizeOf<WINDOWPLACEMENT>()
        };
        bool isMaximized = GetWindowPlacement(hwnd, ref placement) && placement.showCmd == SW_SHOWMAXIMIZED;

        if (RectCoversArea(windowRect, monitorRect, fullscreenTolerancePx))
        {
            // Window covers entire monitor - check if it's truly exclusive or windowed fullscreen
            int style = GetWindowLong(hwnd, GWL_STYLE);
            bool hasCaption = (style & WS_CAPTION) == WS_CAPTION;
            bool hasResizeFrame = (style & WS_THICKFRAME) != 0;

            // A normal maximized window with caption/border is NOT fullscreen.
            // Only borderless windows covering the full monitor are fullscreen.
            if (hasCaption || hasResizeFrame)
            {
                // Normal maximized windows should NOT trigger fullscreen hide.
                // Only non-maximized windows covering the full monitor are windowed fullscreen
                // (e.g., browsers in F11 mode that retain some styles).
                if (isMaximized)
                {
                    return FullscreenType.None;
                }

                return FullscreenType.WindowedFullscreen;
            }
            return FullscreenType.ExclusiveFullscreen;
        }

        int styleWf = GetWindowLong(hwnd, GWL_STYLE);
        bool hasCaptionWf = (styleWf & WS_CAPTION) == WS_CAPTION;
        bool hasResizeFrameWf = (styleWf & WS_THICKFRAME) != 0;
        bool isBorderless = !hasCaptionWf && !hasResizeFrameWf;

        const int workAreaTolerancePx = 6;
        bool matchesWorkArea = RectMatchesArea(windowRect, monitorInfo.rcWork, workAreaTolerancePx);
        bool isWindowedFullscreen = matchesWorkArea && (isBorderless || (isMaximized && !hasCaptionWf));

        return isWindowedFullscreen ? FullscreenType.WindowedFullscreen : FullscreenType.None;
    }

    public static bool IsForegroundWindowFullscreen(IntPtr hwnd, IntPtr notchHwnd)
    {
        return DetectFullscreenType(hwnd, notchHwnd) != FullscreenType.None;
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
