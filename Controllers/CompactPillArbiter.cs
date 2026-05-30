using System;

namespace VNotch.Controllers;

/// <summary>
/// Identifies a transient overlay that competes for the compact pill area.
/// Higher numeric value = higher priority. The arbiter ensures only one slot
/// is active at a time and that lower-priority requests are rejected when a
/// higher one is showing.
/// </summary>
public enum CompactPillSlot
{
    None       = 0,
    Clipboard  = 1, // "Copied" flash
    Volume     = 2, // Volume scrub indicator
    Bluetooth  = 3, // Connect / disconnect toast
    Charging   = 4, // Plugged / unplugged glance
    Greeting   = 5  // Startup "Hello" handwriting
}

/// <summary>
/// Single source of truth for which compact-pill overlay currently owns the
/// notch's collapsed area. Each show path requests a slot via <see cref="TryAcquire"/>;
/// the arbiter reports back whether the request won, what was preempted, and a
/// monotonic <c>token</c> the caller must check inside any deferred animation
/// callbacks before mutating UI state.
///
/// Tokens are also bumped when the slot is released so a late-firing animation
/// completion handler from a previous owner can detect that it's stale.
/// </summary>
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

    /// <summary>
    /// Returns true if a slot of the given priority should be allowed to show.
    /// A request wins when no slot is active, when it has strictly higher priority
    /// than the active one, or when it's the same slot (refresh).
    /// </summary>
    public bool CanAcquire(CompactPillSlot slot)
    {
        if (slot == CompactPillSlot.None) return false;
        lock (_gate)
        {
            return _activeSlot == CompactPillSlot.None
                || (int)slot >= (int)_activeSlot;
        }
    }

    /// <summary>
    /// Attempts to take ownership for <paramref name="slot"/>. Returns the result
    /// describing whether it won, what was preempted (so the caller can cancel its
    /// animations), and a fresh token for guarding deferred callbacks.
    /// </summary>
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
                // A higher-priority overlay is already showing — reject.
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

    /// <summary>
    /// Releases ownership when <paramref name="token"/> matches the active token.
    /// Stale releases are ignored so a late dismiss from a preempted overlay
    /// can't clear a slot that has since been taken by a newer overlay.
    /// </summary>
    public void Release(int token)
    {
        lock (_gate)
        {
            if (token == 0 || token != _activeToken) return;
            _activeSlot = CompactPillSlot.None;
            _activeToken = 0;
        }
    }

    /// <summary>
    /// Forcefully releases the active slot (used by global cancel paths such as
    /// the notch entering full expand mode).
    /// </summary>
    public void ForceClear()
    {
        lock (_gate)
        {
            _activeSlot = CompactPillSlot.None;
            _activeToken = 0;
        }
    }

    /// <summary>
    /// True when a deferred callback's <paramref name="token"/> still matches the
    /// active slot — i.e. the callback's animation hasn't been preempted.
    /// </summary>
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

        /// <summary>True if the caller may now show its overlay.</summary>
        public bool Won { get; }

        /// <summary>
        /// The slot that was active before this acquire and must be visually
        /// cancelled by the caller. <see cref="CompactPillSlot.None"/> means
        /// nothing was preempted.
        /// </summary>
        public CompactPillSlot Preempted { get; }

        /// <summary>
        /// Token to capture and check inside Completed handlers before committing
        /// state changes. 0 when the request was rejected.
        /// </summary>
        public int Token { get; }
    }
}
