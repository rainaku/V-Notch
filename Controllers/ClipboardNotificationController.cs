using System;

namespace VNotch.Controllers;

public sealed class ClipboardNotificationController
{
    private DateTime _lastFlashUtc = DateTime.MinValue;
    private readonly TimeSpan _cooldown;

    public ClipboardNotificationController() : this(TimeSpan.FromMilliseconds(400))
    {
    }

    public ClipboardNotificationController(TimeSpan cooldown)
    {
        _cooldown = cooldown;
    }

    public bool IsActive { get; private set; }
    public int Token { get; private set; }

    public bool TryAcceptUpdate(DateTime utcNow, bool isVisible)
    {
        if (!isVisible) return false;
        if ((utcNow - _lastFlashUtc) < _cooldown) return false;

        _lastFlashUtc = utcNow;
        return true;
    }

    public bool TryBegin(ICompactPillCoordinator compactPillCoordinator)
    {
        if (compactPillCoordinator == null) throw new ArgumentNullException(nameof(compactPillCoordinator));
        if (!compactPillCoordinator.TryAcquire(CompactPillSlot.Clipboard).Won)
        {
            return false;
        }

        var token = compactPillCoordinator.ActiveToken;
        if (token == 0)
        {
            return false;
        }

        IsActive = true;
        Token = token;
        return true;
    }

    public int Complete(ICompactPillCoordinator compactPillCoordinator)
    {
        if (compactPillCoordinator == null) throw new ArgumentNullException(nameof(compactPillCoordinator));

        var token = Token;
        IsActive = false;
        Token = 0;
        compactPillCoordinator.Release(token);
        return token;
    }

    public void CancelPreempted()
    {
        IsActive = false;
        Token = 0;
    }
}
