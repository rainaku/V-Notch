using System.Windows.Threading;

namespace VNotch.Services;

/// <summary>
/// A simple debouncer that delays execution until a quiet period has elapsed.
/// Useful for throttling frequent UI updates (e.g., media changes, resize events).
/// Thread-safe for WPF dispatcher thread usage.
/// </summary>
public sealed class Debouncer : IDisposable
{
    private readonly DispatcherTimer _timer;
    private Action? _pendingAction;

    /// <summary>
    /// Creates a debouncer with the specified delay.
    /// </summary>
    /// <param name="delay">How long to wait after the last call before executing.</param>
    /// <param name="priority">Dispatcher priority for the callback.</param>
    public Debouncer(TimeSpan delay, DispatcherPriority priority = DispatcherPriority.Normal)
    {
        _timer = new DispatcherTimer(priority)
        {
            Interval = delay
        };
        _timer.Tick += (s, e) =>
        {
            _timer.Stop();
            _pendingAction?.Invoke();
            _pendingAction = null;
        };
    }

    /// <summary>
    /// Schedule an action. If called again before the delay expires,
    /// the previous call is cancelled and the timer resets.
    /// </summary>
    public void Debounce(Action action)
    {
        _pendingAction = action;
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>
    /// Cancel any pending debounced action.
    /// </summary>
    public void Cancel()
    {
        _timer.Stop();
        _pendingAction = null;
    }

    /// <summary>
    /// Execute the pending action immediately (if any) without waiting for the delay.
    /// </summary>
    public void Flush()
    {
        if (_pendingAction != null)
        {
            _timer.Stop();
            _pendingAction.Invoke();
            _pendingAction = null;
        }
    }

    /// <summary>Whether there's a pending action waiting to execute.</summary>
    public bool IsPending => _timer.IsEnabled;

    public void Dispose()
    {
        _timer.Stop();
        _pendingAction = null;
    }
}

/// <summary>
/// A throttler that ensures an action executes at most once per interval.
/// Unlike Debouncer, the first call executes immediately, then subsequent
/// calls within the interval are batched to execute at the end.
/// </summary>
public sealed class Throttler : IDisposable
{
    private readonly DispatcherTimer _timer;
    private Action? _pendingAction;
    private DateTime _lastExecutionUtc = DateTime.MinValue;
    private readonly TimeSpan _interval;

    public Throttler(TimeSpan interval, DispatcherPriority priority = DispatcherPriority.Normal)
    {
        _interval = interval;
        _timer = new DispatcherTimer(priority)
        {
            Interval = interval
        };
        _timer.Tick += (s, e) =>
        {
            _timer.Stop();
            if (_pendingAction != null)
            {
                _lastExecutionUtc = DateTime.UtcNow;
                var action = _pendingAction;
                _pendingAction = null;
                action.Invoke();
            }
        };
    }

    /// <summary>
    /// Throttle an action. First call executes immediately.
    /// Subsequent calls within the interval are deferred to the end of the interval.
    /// </summary>
    public void Throttle(Action action)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastExecutionUtc) >= _interval)
        {
            // Enough time has passed — execute immediately
            _lastExecutionUtc = now;
            _timer.Stop();
            _pendingAction = null;
            action.Invoke();
        }
        else
        {
            // Within throttle window — defer
            _pendingAction = action;
            if (!_timer.IsEnabled)
                _timer.Start();
        }
    }

    /// <summary>Cancel any pending throttled action.</summary>
    public void Cancel()
    {
        _timer.Stop();
        _pendingAction = null;
    }

    public void Dispose()
    {
        _timer.Stop();
        _pendingAction = null;
    }
}
