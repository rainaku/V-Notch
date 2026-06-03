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
        var result = _compactPillCoordinator.TryAcquire(slot);
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

        NotchBorder.BeginAnimation(WidthProperty, null);

        var anim = new DoubleAnimation
        {
            To = targetWidth,
            Duration = duration,
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(anim, VNotch.Services.AnimationConfig.TargetFps);

        anim.Completed += (_, _) =>
        {
            if (version != _compactWidthAnimationVersion) return;
            if (token != 0 && !_compactPillCoordinator.IsTokenCurrent(token)) return;

            NotchBorder.BeginAnimation(WidthProperty, null);
            NotchBorder.Width = targetWidth;
        };

        NotchBorder.BeginAnimation(WidthProperty, anim);
    }

    private bool IsCompactSlotStale(int token) => !_compactPillCoordinator.IsTokenCurrent(token);
}

