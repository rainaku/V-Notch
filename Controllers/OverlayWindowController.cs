using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using VNotch.Services;
using static VNotch.Services.Win32Interop;

namespace VNotch.Controllers;

/// <summary>Owns HWND hook, overlay styles, keyboard activation, and screen placement.</summary>
public sealed class OverlayWindowController : IDisposable
{
    private const double HorizontalPadding = 96;
    private readonly Window _window;
    private readonly NotchShellState _state;
    private readonly Func<bool> _isVisible;
    private readonly Func<bool> _stayBehindWindows;
    private readonly Action _ensureTopmost;
    private readonly Action _onAppDeactivated;
    private readonly Action _onDisplayChanged;
    private readonly Action _onClipboardUpdated;
    private HwndSource? _source;

    public OverlayWindowController(
        Window window,
        NotchShellState state,
        Func<bool> isVisible,
        Func<bool> stayBehindWindows,
        Action ensureTopmost,
        Action onAppDeactivated,
        Action onDisplayChanged,
        Action onClipboardUpdated)
    {
        _window = window;
        _state = state;
        _isVisible = isVisible;
        _stayBehindWindows = stayBehindWindows;
        _ensureTopmost = ensureTopmost;
        _onAppDeactivated = onAppDeactivated;
        _onDisplayChanged = onDisplayChanged;
        _onClipboardUpdated = onClipboardUpdated;
    }

    public void Initialize()
    {
        if (_source != null) return;
        _state.Hwnd = new WindowInteropHelper(_window).Handle;
        _source = HwndSource.FromHwnd(_state.Hwnd);
        _source?.AddHook(WndProc);
    }

    public void ConfigureOverlay()
    {
        var exStyle = GetWindowLong(_state.Hwnd, GWL_EXSTYLE);
        var topmostStyle = _stayBehindWindows() ? 0 : WS_EX_TOPMOST;
        SetWindowLong(_state.Hwnd, GWL_EXSTYLE,
            (exStyle & ~WS_EX_TOPMOST) | WS_EX_TOOLWINDOW | topmostStyle | WS_EX_NOACTIVATE | WS_EX_LAYERED);
        _ensureTopmost();
    }

    public void SetKeyboardInput(bool enabled)
    {
        if (_state.Hwnd == IntPtr.Zero) return;
        var exStyle = GetWindowLong(_state.Hwnd, GWL_EXSTYLE);
        SetWindowLong(_state.Hwnd, GWL_EXSTYLE,
            enabled ? exStyle & ~WS_EX_NOACTIVATE : exStyle | WS_EX_NOACTIVATE);
        if (enabled) _window.Activate();
    }

    public void PositionAtTop(double surfaceWidth, double expandedHeight)
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen == null) return;

        double dpiScale = GetDpiScale();
        double widthDip = surfaceWidth + HorizontalPadding;
        double heightDip = expandedHeight + 80;
        var bounds = CalculateCenteredBounds(
            screen.Bounds.Left, screen.Bounds.Width, widthDip, heightDip, dpiScale);

        _state.FixedX = bounds.X;
        _state.FixedY = 0;
        _state.WindowWidth = bounds.Width;
        _state.WindowHeight = bounds.Height;
        _window.Width = widthDip;
        _window.Height = heightDip;
        SetWindowPos(_state.Hwnd, PreferredZOrder, bounds.X, 0, bounds.Width, bounds.Height, SWP_NOACTIVATE);
    }

    public void ResizeHeight(double heightDip)
    {
        _window.Height = heightDip;
        _state.WindowHeight = (int)Math.Round(heightDip * GetDpiScale());
        ReassertBounds();
    }

    public void ReassertBounds()
    {
        if (_state.Hwnd != IntPtr.Zero)
            SetWindowPos(_state.Hwnd, PreferredZOrder, _state.FixedX, _state.FixedY,
                _state.WindowWidth, _state.WindowHeight, SWP_NOACTIVATE);
    }

    private IntPtr PreferredZOrder => _stayBehindWindows()
        ? GetDesktopLayerInsertAfter(_state.Hwnd)
        : HWND_TOPMOST;

    public IntPtr GetForegroundWindowHandle() => GetForegroundWindow();

    public double DpiScale => GetDpiScale();

    public (double Left, double Top, double Width, double Height, double CornerRadius) GetNotchScreenRect(
        double notchWidth, double notchHeight, double cornerRadius)
    {
        double dpiScale = GetDpiScale();
        double windowLeft = _state.FixedX / dpiScale;
        double windowTop = _state.FixedY / dpiScale;
        double windowWidth = _state.WindowWidth / dpiScale;
        return (windowLeft + (windowWidth - notchWidth) / 2.0,
            windowTop, notchWidth, notchHeight, cornerRadius);
    }

    internal static (int X, int Width, int Height) CalculateCenteredBounds(
        int screenLeft, int screenWidth, double widthDip, double heightDip, double dpiScale)
    {
        int width = (int)Math.Round(widthDip * dpiScale);
        int height = (int)Math.Round(heightDip * dpiScale);
        return (screenLeft + (screenWidth - width) / 2, width, height);
    }

    private double GetDpiScale()
    {
        if (_state.Hwnd == IntPtr.Zero) return 1.0;
        uint dpi = GetDpiForWindow(_state.Hwnd);
        return dpi > 0 ? dpi / 96.0 : 1.0;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_WINDOWPOSCHANGING when lParam != IntPtr.Zero && _state.FixedY >= 0:
                var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                pos.y = _state.FixedY;
                pos.x = _state.FixedX;
                pos.hwndInsertAfter = PreferredZOrder;
                Marshal.StructureToPtr(pos, lParam, false);
                break;
            case WM_ACTIVATE when _isVisible():
                SetWindowPos(_state.Hwnd, PreferredZOrder, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                break;
            case WM_ACTIVATEAPP when wParam == IntPtr.Zero:
                _window.Dispatcher.BeginInvoke(_onAppDeactivated);
                break;
            case WM_DISPLAYCHANGE:
            case WM_DPICHANGED:
                _window.Dispatcher.BeginInvoke(_onDisplayChanged);
                break;
            case WM_CLIPBOARDUPDATE:
                _onClipboardUpdated();
                break;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _source?.RemoveHook(WndProc);
        _source = null;
    }
}
