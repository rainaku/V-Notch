using VNotch.Services;
using static VNotch.Services.Win32Interop;

namespace VNotch.Controllers;

public sealed class ClipboardListenerController : IDisposable
{
    private static readonly TimeSpan NotificationCooldown = TimeSpan.FromMilliseconds(400);
    private readonly Func<IntPtr> _getHwnd;
    private readonly Action _onClipboardChanged;
    private DateTime _lastNotificationUtc = DateTime.MinValue;
    private bool _registered;

    public ClipboardListenerController(Func<IntPtr> getHwnd, Action onClipboardChanged)
    {
        _getHwnd = getHwnd;
        _onClipboardChanged = onClipboardChanged;
    }

    public void Start()
    {
        var hwnd = _getHwnd();
        if (!_registered && hwnd != IntPtr.Zero)
            _registered = AddClipboardFormatListener(hwnd);
    }

    public void NotifyClipboardUpdated()
    {
        var now = DateTime.UtcNow;
        if (now - _lastNotificationUtc < NotificationCooldown) return;
        _lastNotificationUtc = now;
        _onClipboardChanged();
    }

    public void Dispose()
    {
        var hwnd = _getHwnd();
        if (_registered && hwnd != IntPtr.Zero)
            RemoveClipboardFormatListener(hwnd);
        _registered = false;
    }
}