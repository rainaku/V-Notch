using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Controllers;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

/// <summary>
/// Compact-pill overlay coordination layer. Centralizes:
///  • preemption of other overlays when a higher-priority one is shown,
///  • the single width animation entry point for compact mode,
///  • backwards-compatible <c>_isXxxVisible</c> derived flags.
/// </summary>
public partial class MainWindow
{
    private int _compactWidthAnimationVersion = 0;

    /// <summary>
    /// Returns true if a slot was acquired and the caller should proceed to
    /// show its UI. Out-parameter <paramref name="token"/> must be captured
    /// and checked in any Completed handlers before committing state.
    /// </summary>
    private bool TryAcquireCompactSlot(CompactPillSlot slot, out int token)
    {
        var result = _compactPillArbiter.TryAcquire(slot);
        token = result.Token;
        if (!result.Won)
        {
            return false;
        }

        if (result.Preempted != CompactPillSlot.None)
        {
            CancelCompactSlotImmediate(result.Preempted);
        }
        return true;
    }

    /// <summary>
    /// Synchronously tears down whatever overlay was active for <paramref name="slot"/>
    /// without running its dismiss animation. Used when a higher-priority overlay
    /// is taking over and the user needs a clean handoff.
    /// </summary>
    private void CancelCompactSlotImmediate(CompactPillSlot slot)
    {
        switch (slot)
        {
            case CompactPillSlot.Clipboard:
                CancelClipboardPeekImmediate();
                break;
            case CompactPillSlot.Volume:
                DismissVolumeIndicatorImmediate();
                break;
            case CompactPillSlot.Bluetooth:
                CancelBluetoothNotificationImmediate();
                break;
            case CompactPillSlot.Charging:
                CancelChargingGlanceImmediate();
                break;
            case CompactPillSlot.Greeting:
                // Greeting has its own lifecycle controlled by HelloPath animations.
                // We never preempt the greeting (it sits at the highest priority).
                break;
        }
    }

    /// <summary>
    /// Single entry point for compact-pill width animations. Cancels any previous
    /// width animation before scheduling a new one and gates the Completed
    /// handler on the arbiter's token so a stale completion can't override the
    /// width chosen by a newer overlay.
    /// </summary>
    private void AnimateCompactWidth(double targetWidth, TimeSpan duration, IEasingFunction ease, int token)
        => AnimateCompactWidth(targetWidth, new Duration(duration), ease, token);

    private void AnimateCompactWidth(double targetWidth, Duration duration, IEasingFunction ease, int token)
    {
        int version = ++_compactWidthAnimationVersion;

        NotchBorder.BeginAnimation(WidthProperty, null);

        var anim = new DoubleAnimation
        {
            To = targetWidth,
            Duration = duration,
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(anim, 144);

        anim.Completed += (_, _) =>
        {
            // Two guards: width version (was the animation superseded?)
            // and arbiter token (is the original requester still in charge?).
            if (version != _compactWidthAnimationVersion) return;
            if (token != 0 && !_compactPillArbiter.IsTokenCurrent(token)) return;

            NotchBorder.BeginAnimation(WidthProperty, null);
            NotchBorder.Width = targetWidth;
        };

        NotchBorder.BeginAnimation(WidthProperty, anim);
    }

    /// <summary>
    /// True when a deferred Completed handler should abort because its slot has
    /// been preempted or released since the animation started.
    /// </summary>
    private bool IsCompactSlotStale(int token) => !_compactPillArbiter.IsTokenCurrent(token);
}
