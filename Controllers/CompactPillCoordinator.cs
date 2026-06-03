using System;

namespace VNotch.Controllers;

public interface ICompactPillCoordinator
{
    CompactPillSlot ActiveSlot { get; }
    int ActiveToken { get; }
    bool CanAcquire(CompactPillSlot slot);
    CompactPillAcquireResult TryAcquire(CompactPillSlot slot);
    void Release(int token);
    void ForceClear();
    bool IsTokenCurrent(int token);
}

public sealed class CompactPillCoordinator : ICompactPillCoordinator
{
    private readonly CompactPillArbiter _arbiter;

    public CompactPillCoordinator() : this(new CompactPillArbiter())
    {
    }

    public CompactPillCoordinator(CompactPillArbiter arbiter)
    {
        _arbiter = arbiter ?? throw new ArgumentNullException(nameof(arbiter));
    }

    public CompactPillSlot ActiveSlot => _arbiter.ActiveSlot;
    public int ActiveToken => _arbiter.ActiveToken;

    public bool CanAcquire(CompactPillSlot slot) => _arbiter.CanAcquire(slot);

    public CompactPillAcquireResult TryAcquire(CompactPillSlot slot)
    {
        var result = _arbiter.TryAcquire(slot);
        return new CompactPillAcquireResult(result.Won, result.Preempted, result.Token);
    }

    public void Release(int token) => _arbiter.Release(token);
    public void ForceClear() => _arbiter.ForceClear();
    public bool IsTokenCurrent(int token) => _arbiter.IsTokenCurrent(token);
}

public readonly struct CompactPillAcquireResult
{
    public CompactPillAcquireResult(bool won, CompactPillSlot preempted, int token)
    {
        Won = won;
        Preempted = preempted;
        Token = token;
    }

    public bool Won { get; }
    public CompactPillSlot Preempted { get; }
    public int Token { get; }
}
