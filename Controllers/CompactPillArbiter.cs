using System;

namespace VNotch.Controllers;

public enum CompactPillSlot
{
    None       = 0,
    Clipboard  = 1,
    Volume     = 2,
    Bluetooth  = 3,
    Charging   = 4,
    Greeting   = 5
}

public sealed class CompactPillArbiter
{
    private readonly object _gate = new();
    private CompactPillSlot _activeSlot = CompactPillSlot.None;
    private int _activeToken = 0;
    private int _nextToken = 1;

    public CompactPillSlot ActiveSlot
    {
        get { lock (_gate) return _activeSlot; }
    }

    public int ActiveToken
    {
        get { lock (_gate) return _activeToken; }
    }

    public bool IsActive(CompactPillSlot slot)
    {
        lock (_gate) return _activeSlot == slot;
    }

    public bool CanAcquire(CompactPillSlot slot)
    {
        if (slot == CompactPillSlot.None) return false;
        lock (_gate)
        {
            return _activeSlot == CompactPillSlot.None
                || (int)slot >= (int)_activeSlot;
        }
    }

    public AcquireResult TryAcquire(CompactPillSlot slot)
    {
        if (slot == CompactPillSlot.None)
        {
            return new AcquireResult(false, CompactPillSlot.None, 0);
        }

        lock (_gate)
        {
            if (_activeSlot != CompactPillSlot.None && (int)slot < (int)_activeSlot)
            {
                return new AcquireResult(false, _activeSlot, 0);
            }

            var preempted = (_activeSlot != CompactPillSlot.None && _activeSlot != slot)
                ? _activeSlot
                : CompactPillSlot.None;

            _activeSlot = slot;
            _activeToken = _nextToken++;
            return new AcquireResult(true, preempted, _activeToken);
        }
    }

    public void Release(int token)
    {
        lock (_gate)
        {
            if (token == 0 || token != _activeToken) return;
            _activeSlot = CompactPillSlot.None;
            _activeToken = 0;
        }
    }

    public void ForceClear()
    {
        lock (_gate)
        {
            _activeSlot = CompactPillSlot.None;
            _activeToken = 0;
        }
    }

    public bool IsTokenCurrent(int token)
    {
        if (token == 0) return false;
        lock (_gate) return token == _activeToken;
    }

    public readonly struct AcquireResult
    {
        public AcquireResult(bool won, CompactPillSlot preempted, int token)
        {
            Won = won;
            Preempted = preempted;
            Token = token;
        }

        public bool Won { get; }

        public CompactPillSlot Preempted { get; }

        public int Token { get; }
    }
}
