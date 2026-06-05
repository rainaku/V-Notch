using System;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    // How often we re-evaluate whether the notch is empty/idle.
    private const int IdleHidePollIntervalMs = 500;

    private DispatcherTimer? _idleHideTimer;
    private DateTime _idleSinceUtc = DateTime.MinValue;

    // True while the notch is slid out of view because it has been empty/idle.
    // Composed into IsEffectivelyNotchVisible alongside _isHiddenByFullscreen.
    private bool _isHiddenByIdle = false;

    private void InitializeIdleAutoHide()
    {
        _idleHideTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(IdleHidePollIntervalMs)
        };
        _idleHideTimer.Tick += IdleHideTimer_Tick;
    }

    /// <summary>
    /// Starts or stops the idle watcher based on the current setting. Called from ApplySettings.
    /// </summary>
    private void ApplyIdleAutoHideSettings()
    {
        if (_idleHideTimer == null) return;

        if (_settings.EnableIdleAutoHide)
        {
            _idleSinceUtc = DateTime.MinValue;
            if (!_idleHideTimer.IsEnabled)
                _idleHideTimer.Start();
        }
        else
        {
            _idleHideTimer.Stop();
            _idleSinceUtc = DateTime.MinValue;

            // Feature turned off while the notch was hidden — bring it back.
            if (_isHiddenByIdle)
            {
                _isHiddenByIdle = false;
                ApplyNotchVisibilityState();
                if (IsEffectivelyNotchVisible)
                {
                    TriggerZOrderBurst(TimeSpan.FromMilliseconds(900));
                    EnsureTopmost(force: true);
                }
            }
        }
    }

    private void IdleHideTimer_Tick(object? sender, EventArgs e)
    {
        if (!_settings.EnableIdleAutoHide)
        {
            _idleHideTimer?.Stop();
            return;
        }

        bool idle = IsNotchEmptyAndIdle();

        if (!idle)
        {
            // Activity detected — reset the countdown and reveal if we had hidden.
            _idleSinceUtc = DateTime.MinValue;
            if (_isHiddenByIdle)
                WakeFromIdle();
            return;
        }

        // Already hidden, nothing more to do.
        if (_isHiddenByIdle) return;

        if (_idleSinceUtc == DateTime.MinValue)
        {
            _idleSinceUtc = DateTime.UtcNow;
            return;
        }

        if ((DateTime.UtcNow - _idleSinceUtc).TotalMilliseconds >= _settings.IdleAutoHideDelay)
        {
            HideForIdle();
        }
    }

    /// <summary>
    /// The notch counts as empty/idle only when nothing is on screen and the user isn't interacting with it.
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

    private void HideForIdle()
    {
        if (_isHiddenByIdle) return;

        _isHiddenByIdle = true;
        RuntimeLog.Log("IDLE-HIDE", $"Notch hidden after {_settings.IdleAutoHideDelay}ms idle");

        // Stop any pending hover timers so they don't fight the slide-out.
        _hoverCollapseTimer.Stop();
        _hoverThumbnailDelayTimer.Stop();

        ApplyNotchVisibilityState();
    }

    /// <summary>
    /// Immediately reveals the notch (if hidden by idle) and resets the idle countdown.
    /// Cheap to call from hot paths such as media updates and compact-pill activity.
    /// </summary>
    private void WakeFromIdle()
    {
        if (!_settings.EnableIdleAutoHide) return;

        _idleSinceUtc = DateTime.MinValue;

        if (!_isHiddenByIdle) return;

        _isHiddenByIdle = false;
        RuntimeLog.Log("IDLE-HIDE", "Notch revealed from idle (activity detected)");

        ApplyNotchVisibilityState();

        if (IsEffectivelyNotchVisible)
        {
            TriggerZOrderBurst(TimeSpan.FromMilliseconds(900));
            EnsureTopmost(force: true);
        }
    }
}
