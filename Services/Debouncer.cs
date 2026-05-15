using System.Windows.Threading;

namespace VNotch.Services;
public sealed class Debouncer : IDisposable
{
    private readonly DispatcherTimer _timer;
    private Action? _pendingAction;
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
public void Debounce(Action action)
    {
        _pendingAction = action;
        _timer.Stop();
        _timer.Start();
    }
public void Cancel()
    {
        _timer.Stop();
        _pendingAction = null;
    }
public void Flush()
    {
        if (_pendingAction != null)
        {
            _timer.Stop();
            _pendingAction.Invoke();
            _pendingAction = null;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _pendingAction = null;
    }
}

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
