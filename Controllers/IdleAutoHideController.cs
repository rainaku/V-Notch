using System;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.Controllers;

/// <summary>
/// Owns the idle auto-hide watcher: the poll timer, the idle countdown, and the
/// hide/reveal decisions that slide the notch out of view once it has been empty
/// and untouched for the configured delay.
///
/// This is a behavior-preserving extraction of the logic that previously lived in
/// <c>MainWindow.IdleAutoHide.cs</c>. The hidden-state flag is routed through the
/// shared <see cref="NotchShellState"/> (Tier 2 owner) so <c>IsEffectivelyNotchVisible</c>
/// keeps composing idle-hide alongside fullscreen-hide exactly as before.
///
/// The controller stays free of any direct <c>Window</c> / visual-tree access: everything
/// that reads <c>MainWindow</c> state or touches the visual tree is supplied as a callback
/// at construction, so the shell partial keeps only thin delegating hooks.
/// </summary>
public sealed class IdleAutoHideController : IDisposable
{
    // How often we re-evaluate whether the notch is empty/idle.
    private const int IdleHidePollIntervalMs = 500;

    private readonly NotchShellState _shellState;
    private readonly Func<NotchSettings> _getSettings;
    private readonly Func<bool> _isNotchEmptyAndIdle;
    private readonly Action _applyVisibilityState;
    private readonly Action _stopHoverTimers;
    private readonly Action _onRevealed;

    private readonly DispatcherTimer _timer;
    private DateTime _idleSinceUtc = DateTime.MinValue;
    private bool _disposed;

    /// <param name="shellState">Shared shell state; owns the <c>IsHiddenByIdle</c> flag.</param>
    /// <param name="getSettings">Provides the current (live) settings on each evaluation.</param>
    /// <param name="isNotchEmptyAndIdle">Predicate that inspects the live notch state (expanded/media/pills/hover/cursor).</param>
    /// <param name="applyVisibilityState">Applies the resulting visibility (slide in/out).</param>
    /// <param name="stopHoverTimers">Stops pending hover timers so they don't fight the slide-out.</param>
    /// <param name="onRevealed">Runs the z-order burst / topmost re-assertion after a reveal.</param>
    public IdleAutoHideController(
        NotchShellState shellState,
        Func<NotchSettings> getSettings,
        Func<bool> isNotchEmptyAndIdle,
        Action applyVisibilityState,
        Action stopHoverTimers,
        Action onRevealed)
    {
        _shellState = shellState ?? throw new ArgumentNullException(nameof(shellState));
        _getSettings = getSettings ?? throw new ArgumentNullException(nameof(getSettings));
        _isNotchEmptyAndIdle = isNotchEmptyAndIdle ?? throw new ArgumentNullException(nameof(isNotchEmptyAndIdle));
        _applyVisibilityState = applyVisibilityState ?? throw new ArgumentNullException(nameof(applyVisibilityState));
        _stopHoverTimers = stopHoverTimers ?? throw new ArgumentNullException(nameof(stopHoverTimers));
        _onRevealed = onRevealed ?? throw new ArgumentNullException(nameof(onRevealed));

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(IdleHidePollIntervalMs)
        };
        _timer.Tick += IdleHideTimer_Tick;
    }

    /// <summary>
    /// The poll timer. Exposed so the existing shell cleanup touchpoint (which stops
    /// <c>_idleHideTimer</c>) keeps working unchanged via a property shim on the shell.
    /// </summary>
    public DispatcherTimer Timer => _timer;

    // True while the notch is slid out of view because it has been empty/idle.
    // Single owner = NotchShellState (composed into IsEffectivelyNotchVisible).
    private bool IsHiddenByIdle
    {
        get => _shellState.IsHiddenByIdle;
        set => _shellState.IsHiddenByIdle = value;
    }

    /// <summary>
    /// Starts or stops the idle watcher based on the current setting. Called from ApplySettings.
    /// </summary>
    public void ApplySettings()
    {
        var settings = _getSettings();

        if (settings.EnableIdleAutoHide)
        {
            _idleSinceUtc = DateTime.MinValue;
            if (!_timer.IsEnabled)
                _timer.Start();
        }
        else
        {
            _timer.Stop();
            _idleSinceUtc = DateTime.MinValue;

            // Feature turned off while the notch was hidden — bring it back.
            if (IsHiddenByIdle)
            {
                IsHiddenByIdle = false;
                _applyVisibilityState();
                if (_shellState.IsEffectivelyNotchVisible)
                    _onRevealed();
            }
        }
    }

    private void IdleHideTimer_Tick(object? sender, EventArgs e)
    {
        var settings = _getSettings();

        if (!settings.EnableIdleAutoHide)
        {
            _timer.Stop();
            return;
        }

        bool idle = _isNotchEmptyAndIdle();

        if (!idle)
        {
            // Activity detected — reset the countdown and reveal if we had hidden.
            _idleSinceUtc = DateTime.MinValue;
            if (IsHiddenByIdle)
                Wake();
            return;
        }

        // Already hidden, nothing more to do.
        if (IsHiddenByIdle) return;

        if (_idleSinceUtc == DateTime.MinValue)
        {
            _idleSinceUtc = DateTime.UtcNow;
            return;
        }

        if ((DateTime.UtcNow - _idleSinceUtc).TotalMilliseconds >= settings.IdleAutoHideDelay)
        {
            HideForIdle();
        }
    }

    private void HideForIdle()
    {
        if (IsHiddenByIdle) return;

        IsHiddenByIdle = true;
        RuntimeLog.Log("IDLE-HIDE", $"Notch hidden after {_getSettings().IdleAutoHideDelay}ms idle");

        // Stop any pending hover timers so they don't fight the slide-out.
        _stopHoverTimers();

        _applyVisibilityState();
    }

    /// <summary>
    /// Immediately reveals the notch (if hidden by idle) and resets the idle countdown.
    /// Cheap to call from hot paths such as media updates and compact-pill activity.
    /// </summary>
    public void Wake()
    {
        if (!_getSettings().EnableIdleAutoHide) return;

        _idleSinceUtc = DateTime.MinValue;

        if (!IsHiddenByIdle) return;

        IsHiddenByIdle = false;
        RuntimeLog.Log("IDLE-HIDE", "Notch revealed from idle (activity detected)");

        _applyVisibilityState();

        if (_shellState.IsEffectivelyNotchVisible)
            _onRevealed();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer.Stop();
        _timer.Tick -= IdleHideTimer_Tick;
    }
}
