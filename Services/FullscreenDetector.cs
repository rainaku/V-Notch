using System;
using System.Runtime.InteropServices;
using System.Text;
using static VNotch.Services.Win32Interop;

namespace VNotch.Services;

/// <summary>
/// Decides whether a given top-level window is in real fullscreen (covers its
/// monitor) or windowed-fullscreen (borderless + matches work area). Used by
/// the notch to auto-hide while games / videos are fullscreened.
///
/// Extracted from <c>MainWindow.xaml.cs</c>. All methods are pure and depend
/// only on <see cref="Win32Interop"/>.
/// </summary>
internal static class FullscreenDetector
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="hwnd"/> is the desktop's
    /// fullscreen window (fullscreen game, fullscreen video, borderless-
    /// maximized app) and therefore should cause the notch to hide.
    /// <paramref name="notchHwnd"/> is the notch's own handle and is always
    /// excluded from the check.
    /// </summary>
    public static bool IsForegroundWindowFullscreen(IntPtr hwnd, IntPtr notchHwnd)
    {
        if (hwnd == IntPtr.Zero || hwnd == notchHwnd)
        {
            return false;
        }

        if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || IsWindowCloaked(hwnd))
        {
            return false;
        }

        if (!TryGetWindowBounds(hwnd, out var windowRect))
        {
            return false;
        }

        int width = windowRect.Right - windowRect.Left;
        int height = windowRect.Bottom - windowRect.Top;
        if (width < 200 || height < 120)
        {
            return false;
        }

        var classNameBuilder = new StringBuilder(128);
        if (GetClassName(hwnd, classNameBuilder, classNameBuilder.Capacity) > 0)
        {
            string className = classNameBuilder.ToString();
            if (string.Equals(className, "Progman", StringComparison.Ordinal) ||
                string.Equals(className, "WorkerW", StringComparison.Ordinal) ||
                string.Equals(className, "Shell_TrayWnd", StringComparison.Ordinal))
            {
                return false;
            }
        }

        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var monitorInfo = new MONITORINFO
        {
            cbSize = Marshal.SizeOf<MONITORINFO>()
        };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return false;
        }

        var monitorRect = monitorInfo.rcMonitor;
        const int fullscreenTolerancePx = 4;
        if (RectCoversArea(windowRect, monitorRect, fullscreenTolerancePx))
        {
            return true;
        }

        int style = GetWindowLong(hwnd, GWL_STYLE);
        bool hasCaption = (style & WS_CAPTION) == WS_CAPTION;
        bool hasResizeFrame = (style & WS_THICKFRAME) != 0;
        bool isBorderless = !hasCaption && !hasResizeFrame;

        var placement = new WINDOWPLACEMENT
        {
            length = Marshal.SizeOf<WINDOWPLACEMENT>()
        };
        bool isMaximized = GetWindowPlacement(hwnd, ref placement) && placement.showCmd == SW_SHOWMAXIMIZED;

        const int workAreaTolerancePx = 6;
        bool matchesWorkArea = RectMatchesArea(windowRect, monitorInfo.rcWork, workAreaTolerancePx);
        bool isWindowedFullscreen = matchesWorkArea && (isBorderless || (isMaximized && !hasCaption));

        return isWindowedFullscreen;
    }

    /// <summary>
    /// Returns the process name for <paramref name="hwnd"/>, or empty string
    /// on any failure. Never throws.
    /// </summary>
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
