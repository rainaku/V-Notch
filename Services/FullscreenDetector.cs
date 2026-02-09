using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace VNotch.Services;

/// <summary>
/// Detects fullscreen applications and manages notch visibility accordingly
/// Similar to macOS behavior where menu bar hides in fullscreen mode
/// </summary>
public class FullscreenDetector : IDisposable
{
    private readonly DispatcherTimer _pollTimer;
    private bool _isFullscreenActive;
    private bool _disposed;
    private IntPtr _lastFullscreenWindow;

    public event EventHandler<FullscreenChangedEventArgs>? FullscreenChanged;
    public event EventHandler? AppFullscreenEntered;
    public event EventHandler? AppFullscreenExited;

    public bool IsFullscreenActive => _isFullscreenActive;

    #region Win32 APIs

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_MAXIMIZE = 0x01000000;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    #endregion

    public FullscreenDetector()
    {
        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500) // Check every 500ms
        };
        _pollTimer.Tick += PollTimer_Tick;
    }

    public void Start()
    {
        if (!_disposed)
        {
            _pollTimer.Start();
        }
    }

    public void Stop()
    {
        _pollTimer.Stop();
    }

    private void PollTimer_Tick(object? sender, EventArgs e)
    {
        var foregroundWindow = GetForegroundWindow();
        
        if (foregroundWindow == IntPtr.Zero) return;

        bool isFullscreen = IsWindowFullscreen(foregroundWindow);
        
        if (isFullscreen != _isFullscreenActive)
        {
            _isFullscreenActive = isFullscreen;
            _lastFullscreenWindow = isFullscreen ? foregroundWindow : IntPtr.Zero;
            
            FullscreenChanged?.Invoke(this, new FullscreenChangedEventArgs(isFullscreen, foregroundWindow));
            
            if (isFullscreen)
            {
                AppFullscreenEntered?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                AppFullscreenExited?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private bool IsWindowFullscreen(IntPtr hWnd)
    {
        // Skip certain system windows
        var className = new System.Text.StringBuilder(256);
        GetClassName(hWnd, className, className.Capacity);
        var classNameStr = className.ToString();
        
        // Skip taskbar, start menu, and other shell windows
        if (classNameStr == "Shell_TrayWnd" || 
            classNameStr == "Shell_SecondaryTrayWnd" ||
            classNameStr == "Progman" ||
            classNameStr == "WorkerW")
        {
            return false;
        }

        // Get window style
        int style = GetWindowLong(hWnd, GWL_STYLE);
        
        // Some fullscreen apps remove caption and thick frame
        bool hasNoCaption = (style & WS_CAPTION) == 0;
        
        // Get window rect
        if (!GetWindowRect(hWnd, out RECT windowRect)) return false;

        // Get monitor info for the window
        IntPtr monitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return false;

        // Check if window covers entire monitor
        var monitorRect = monitorInfo.rcMonitor;
        
        bool coversFullMonitor = 
            windowRect.Left <= monitorRect.Left &&
            windowRect.Top <= monitorRect.Top &&
            windowRect.Right >= monitorRect.Right &&
            windowRect.Bottom >= monitorRect.Bottom;

        // Consider it fullscreen if it covers the monitor and has no caption
        // OR if it's maximized and covers the work area fully
        return coversFullMonitor || (hasNoCaption && coversFullMonitor);
    }

    /// <summary>
    /// Get the handle of the current fullscreen window
    /// </summary>
    public IntPtr GetFullscreenWindow()
    {
        return _lastFullscreenWindow;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _pollTimer.Stop();
            _disposed = true;
        }
    }
}

public class FullscreenChangedEventArgs : EventArgs
{
    public bool IsFullscreen { get; }
    public IntPtr WindowHandle { get; }

    public FullscreenChangedEventArgs(bool isFullscreen, IntPtr windowHandle)
    {
        IsFullscreen = isFullscreen;
        WindowHandle = windowHandle;
    }
}
