using System;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    // ─────────────────────────────────────────────────────────────────────────
    // Idle auto-hide is owned by IdleAutoHideController (VNotch.Controllers).
    // This partial keeps only thin hooks: it constructs the controller, wires the
    // shell-state callbacks the controller needs (the empty/idle predicate, the
    // visibility apply, the hover-timer stop, and the post-reveal z-order burst),
    // and forwards the lifecycle calls the shell ctor / ApplySettings make.
    //
    // The hidden-by-idle flag still lives on _shellState (Tier 2 owner, Task 1),
    // so IsEffectivelyNotchVisible composes idle-hide exactly as before.
    // ─────────────────────────────────────────────────────────────────────────

    private IdleAutoHideController? _idleAutoHide;

    // Shim so the existing shell cleanup touchpoint (`_idleHideTimer?.Stop()` in
    // PerformCleanup) keeps compiling and behaves identically — it now stops the
    // timer owned by the controller. Kept read-only; the controller owns the timer.
    private DispatcherTimer? _idleHideTimer => _idleAutoHide?.Timer;

    /// <summary>
    /// Preserved name: the shell constructor calls this. Delegates to the controller
    /// initializer so existing wiring is unchanged.
    /// </summary>
    private void InitializeIdleAutoHide() => InitializeIdleAutoHideController();

    /// <summary>
    /// Constructs the idle auto-hide controller, supplying the shell-side callbacks it
    /// needs. Keeps all live-state inspection and visual-tree access in the shell.
    /// </summary>
    internal void InitializeIdleAutoHideController()
    {
        _idleAutoHide = new IdleAutoHideController(
            _shellState,
            getSettings: () => _settings,
            isNotchEmptyAndIdle: IsNotchEmptyAndIdle,
            applyVisibilityState: ApplyNotchVisibilityState,
            stopHoverTimers: () =>
            {
                _hoverCollapseTimer.Stop();
                _hoverThumbnailDelayTimer.Stop();
            },
            onRevealed: () =>
            {
                TriggerZOrderBurst(TimeSpan.FromMilliseconds(900));
                EnsureTopmost(force: true);
            });
    }

    /// <summary>
    /// Starts or stops the idle watcher based on the current setting. Called from ApplySettings.
    /// Preserved name; delegates to the controller.
    /// </summary>
    private void ApplyIdleAutoHideSettings() => _idleAutoHide?.ApplySettings();

    /// <summary>
    /// Immediately reveals the notch (if hidden by idle) and resets the idle countdown.
    /// Cheap to call from hot paths such as media updates and compact-pill activity.
    /// Preserved name; delegates to the controller.
    /// </summary>
    private void WakeFromIdle() => _idleAutoHide?.Wake();

    /// <summary>
    /// Tears down the idle auto-hide controller (stops the timer, unsubscribes Tick).
    /// </summary>
    // JOIN: add `DisposeIdleAutoHide();` to PerformCleanup in MainWindow.xaml.cs.
    // The existing `_idleHideTimer?.Stop()` line already stops the timer through the
    // shim above, so behavior is preserved even before the join wires full disposal.
    internal void DisposeIdleAutoHide()
    {
        _idleAutoHide?.Dispose();
        _idleAutoHide = null;
    }

    /// <summary>
    /// The notch counts as empty/idle only when nothing is on screen and the user isn't
    /// interacting with it. Stays in the shell: it reads live notch/window state and is
    /// passed to the controller as the idle predicate.
    /// </summary>
    private bool IsNotchEmptyAndIdle()
    {
        // Any expanded / transitioning surface is active content.
        if (_isExpanded || _isMusicExpanded || _isTimerView || _isAnimating)
            return false;

        // Music thumbnail pill showing, or media actively playing.
        if (_isMusicCompactMode)
            return false;
        if (_currentMediaInfo != null && _currentMediaInfo.IsAnyMediaPlaying)
            return false;

        // A transient compact pill is on screen (clipboard / volume / bluetooth / charging / greeting).
        if (_compactPillArbiter.ActiveSlot != CompactPillSlot.None)
            return false;

        // A countdown is running or its completion flash is showing.
        if (_isCountdownRunning || IsCountdownCompletionVisualActive)
            return false;

        // Don't hide while the user is hovering / interacting with the notch.
        if (NotchWrapper.IsMouseOver)
            return false;
        if (_hwnd != IntPtr.Zero && IsCursorInsideWindow())
            return false;

        return true;
    }
}
