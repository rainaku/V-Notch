using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace VNotch.Services;

/// <summary>
/// Monitors and manages window positions relative to the notch safe area
/// Prevents windows from being dragged into the notch zone
/// Similar to macOS behavior where windows can't overlap the notch
/// </summary>
public class WindowPositionManager : IDisposable
{
    private readonly DispatcherTimer _pollTimer;
    private Rect _notchBounds;
    private Rect _safeArea;
    private bool _disposed;
    private bool _isEnabled = true;

    public event EventHandler<WindowBlockedEventArgs>? WindowBlocked;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    #region Win32 APIs

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    #endregion

    private readonly List<IntPtr> _trackedWindows = new();

    public WindowPositionManager()
    {
        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100) // Check 10 times per second
        };
        _pollTimer.Tick += PollTimer_Tick;
    }

    public void UpdateNotchBounds(double left, double top, double width, double height)
    {
        _notchBounds = new Rect(left, top, width, height);
        
        // Safe area is slightly larger than notch
        _safeArea = new Rect(
            left - 4,
            top,
            width + 8,
            height + 4
        );
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
        if (!_isEnabled) return;

        // Enumerate all visible windows and check if any overlap with notch
        EnumWindows(EnumWindowCallback, IntPtr.Zero);
    }

    private bool EnumWindowCallback(IntPtr hWnd, IntPtr lParam)
    {
        // Skip if not visible
        if (!IsWindowVisible(hWnd)) return true;

        // Skip tool windows and system windows
        int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
        int style = GetWindowLong(hWnd, GWL_STYLE);

        bool isToolWindow = (exStyle & WS_EX_TOOLWINDOW) != 0;
        bool hasCaption = (style & WS_CAPTION) != 0;

        // We only care about normal windows with captions
        if (isToolWindow || !hasCaption) return true;

        // Skip certain class names
        var className = new System.Text.StringBuilder(256);
        GetClassName(hWnd, className, className.Capacity);
        var classNameStr = className.ToString();

        if (classNameStr == "Shell_TrayWnd" ||
            classNameStr == "Shell_SecondaryTrayWnd" ||
            classNameStr == "Progman" ||
            classNameStr == "WorkerW" ||
            classNameStr == "NotifyIconOverflowWindow")
        {
            return true;
        }

        // Get window rect
        if (!GetWindowRect(hWnd, out RECT rect)) return true;

        // Check if title bar is in notch zone
        // Title bar is typically the top ~30 pixels of a window
        var titleBarRect = new Rect(rect.Left, rect.Top, rect.Right - rect.Left, 32);

        if (IsRectOverlapping(titleBarRect, _safeArea))
        {
            // Move window down to avoid notch
            int newTop = (int)_safeArea.Bottom + 2;
            MoveWindowDown(hWnd, rect, newTop);
            
            var title = new System.Text.StringBuilder(256);
            GetWindowText(hWnd, title, title.Capacity);
            
            WindowBlocked?.Invoke(this, new WindowBlockedEventArgs(hWnd, title.ToString()));
        }

        return true;
    }

    private bool IsRectOverlapping(Rect rect1, Rect rect2)
    {
        return rect1.Left < rect2.Right &&
               rect1.Right > rect2.Left &&
               rect1.Top < rect2.Bottom &&
               rect1.Bottom > rect2.Top;
    }

    private void MoveWindowDown(IntPtr hWnd, RECT currentRect, int newTop)
    {
        SetWindowPos(hWnd, IntPtr.Zero, currentRect.Left, newTop, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
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

public class WindowBlockedEventArgs : EventArgs
{
    public IntPtr WindowHandle { get; }
    public string WindowTitle { get; }

    public WindowBlockedEventArgs(IntPtr windowHandle, string windowTitle)
    {
        WindowHandle = windowHandle;
        WindowTitle = windowTitle;
    }
}
