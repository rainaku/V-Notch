using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Controllers;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private int _compactWidthAnimationVersion = 0;

    private bool TryAcquireCompactSlot(CompactPillSlot slot, out int token)
    {
        // A compact pill wants the stage — reveal the notch if it's hidden by idle.
        WakeFromIdle();

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
                break;
        }
    }

    private void AnimateCompactWidth(double targetWidth, TimeSpan duration, IEasingFunction ease, int token)
        => AnimateCompactWidth(targetWidth, new Duration(duration), ease, token);

    private void AnimateCompactWidth(double targetWidth, Duration duration, IEasingFunction ease, int token)
    {
        int version = ++_compactWidthAnimationVersion;

        var anim = new DoubleAnimation
        {
            // Start from the actual current width (the live animated value) rather than
            // clearing to the base property first — otherwise, if a collapse from the
            // clock/audio view is still in flight, the base Width is the wide view width
            // and clearing snaps the notch out to that width for a frame before shrinking.
            From = NotchBorder.ActualWidth,
            To = targetWidth,
            Duration = duration,
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(anim, VNotch.Services.AnimationConfig.TargetFps);

        anim.Completed += (_, _) =>
        {
            if (version != _compactWidthAnimationVersion) return;
            if (token != 0 && !_compactPillArbiter.IsTokenCurrent(token)) return;

            NotchBorder.BeginAnimation(WidthProperty, null);
            NotchBorder.Width = targetWidth;
        };

        NotchBorder.BeginAnimation(WidthProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    private bool IsCompactSlotStale(int token) => !_compactPillArbiter.IsTokenCurrent(token);
}
