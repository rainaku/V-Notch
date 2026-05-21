using System;
using VNotch.Models;
using VNotch.Services;
using static VNotch.Services.Win32Interop;

namespace VNotch.Controllers;

/// <summary>
/// Manages fullscreen auto-hide decision logic: determines whether the notch
/// should be hidden based on foreground window state, settings, and cooldowns.
/// No WPF/UI references — pure Win32 + decision logic.
/// </summary>
public sealed class FullscreenAutoHideController
{
    private static readonly TimeSpan FullscreenStateCooldown = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan ThrottleInterval = TimeSpan.FromMilliseconds(50);

    private readonly Func<IntPtr> _getNotchHwnd;
    private readonly NotchSettings _settings;

    private DateTime _lastCheckUtc = DateTime.MinValue;
    private DateTime _lastStateChangeUtc = DateTime.MinValue;
    private bool _isHiddenByFullscreen;

    // ─── Public State ───

    public bool IsHiddenByFullscreen => _isHiddenByFullscreen;

    // ─── Events ───

    /// <summary>Fired when the hide state changes. Args: shouldHide.</summary>
    public event Action<bool>? HideStateChanged;

    /// <summary>Fired when a recheck should be scheduled (cooldown prevented immediate transition).</summary>
    public event Action? RecheckNeeded;

    // ─── Constructor ───

    public FullscreenAutoHideController(Func<IntPtr> getNotchHwnd, NotchSettings settings)
    {
        _getNotchHwnd = getNotchHwnd ?? throw new ArgumentNullException(nameof(getNotchHwnd));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    // ─── Core Logic ───

    /// <summary>
    /// Evaluates whether the notch should be hidden based on the current foreground window.
    /// Call this from WndProc, foreground change hooks, or periodic checks.
    /// </summary>
    /// <param name="foregroundHwnd">The foreground window handle (0 = auto-detect).</param>
    /// <param name="force">Bypass throttle and cooldown.</param>
    /// <returns>True if the hide state changed.</returns>
    public bool Evaluate(IntPtr foregroundHwnd = default, bool force = false)
    {
        IntPtr notchHwnd = _getNotchHwnd();
        if (notchHwnd == IntPtr.Zero) return false;

        // Bail when both auto-hide options are off
        if (!_settings.HideOnExclusiveFullscreen && !_settings.HideOnWindowedFullscreen)
        {
            if (_isHiddenByFullscreen)
            {
                _isHiddenByFullscreen = false;
                _lastStateChangeUtc = DateTime.UtcNow;
                HideStateChanged?.Invoke(false);
                return true;
            }
            return false;
        }

        // Throttle
        var now = DateTime.UtcNow;
        if (!force && (now - _lastCheckUtc) < ThrottleInterval)
            return false;

        _lastCheckUtc = now;

        var targetHwnd = foregroundHwnd == IntPtr.Zero ? GetForegroundWindow() : foregroundHwnd;
        bool shouldHide = ShouldHideForFullscreen(targetHwnd, notchHwnd);

        if (shouldHide == _isHiddenByFullscreen)
            return false;

        // Cooldown only on show→hide transition
        if (!force && shouldHide && (now - _lastStateChangeUtc) < FullscreenStateCooldown)
        {
            RecheckNeeded?.Invoke();
            return false;
        }

        _isHiddenByFullscreen = shouldHide;
        _lastStateChangeUtc = now;
        HideStateChanged?.Invoke(shouldHide);
        return true;
    }

    /// <summary>
    /// Force-resets the hidden state (e.g., when settings change).
    /// </summary>
    public void Reset()
    {
        if (_isHiddenByFullscreen)
        {
            _isHiddenByFullscreen = false;
            _lastStateChangeUtc = DateTime.UtcNow;
            HideStateChanged?.Invoke(false);
        }
    }

    // ─── Decision Logic ───

    private bool ShouldHideForFullscreen(IntPtr hwnd, IntPtr notchHwnd)
    {
        IntPtr notchMonitor = FullscreenDetector.GetWindowMonitor(notchHwnd);

        if (hwnd == notchHwnd)
            return CheckFullscreenBehind(notchHwnd, notchMonitor, notchHwnd);

        if (hwnd != IntPtr.Zero)
        {
            GetWindowThreadProcessId(hwnd, out uint fgPid);
            GetWindowThreadProcessId(notchHwnd, out uint myPid);
            if (fgPid == myPid)
                return CheckFullscreenBehind(hwnd, notchMonitor, notchHwnd);
        }

        return IsForegroundWindowFullscreen(hwnd, notchMonitor, notchHwnd);
    }

    private bool CheckFullscreenBehind(IntPtr startHwnd, IntPtr notchMonitor, IntPtr notchHwnd)
    {
        var next = Win32Interop.GetWindow(startHwnd, GW_HWNDNEXT);
        const int maxWalk = 24;
        GetWindowThreadProcessId(notchHwnd, out uint myPid);

        for (int i = 0; i < maxWalk && next != IntPtr.Zero; i++)
        {
            if (IsWindowVisible(next) && !IsIconic(next))
            {
                GetWindowThreadProcessId(next, out uint nextPid);
                if (nextPid != myPid)
                {
                    var type = FullscreenDetector.DetectFullscreenType(next, notchHwnd, notchMonitor);
                    if (type != FullscreenType.None)
                        return ShouldHideForType(type);
                }
            }
            next = Win32Interop.GetWindow(next, GW_HWNDNEXT);
        }
        return false;
    }

    private bool IsForegroundWindowFullscreen(IntPtr hwnd, IntPtr notchMonitor, IntPtr notchHwnd)
    {
        var type = FullscreenDetector.DetectFullscreenType(hwnd, notchHwnd, notchMonitor);
        return ShouldHideForType(type);
    }

    private bool ShouldHideForType(FullscreenType type) => type switch
    {
        FullscreenType.ExclusiveFullscreen => _settings.HideOnExclusiveFullscreen,
        FullscreenType.WindowedFullscreen => _settings.HideOnWindowedFullscreen,
        _ => false
    };
}
