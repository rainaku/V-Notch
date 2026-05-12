using System;
using System.Collections.Generic;
using System.Windows.Threading;

namespace VNotch.Controllers;

/// <summary>
/// Centralizes DispatcherTimer management to reduce timer proliferation.
/// Provides named timers with start/stop/restart semantics and automatic cleanup.
/// </summary>
public sealed class TimerManager : IDisposable
{
    private readonly Dictionary<string, ManagedTimer> _timers = new();
    private readonly Dispatcher _dispatcher;

    public TimerManager(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Registers a named timer. If already registered, updates the interval and handler.
    /// </summary>
    public void Register(string name, TimeSpan interval, Action handler)
    {
        if (_timers.TryGetValue(name, out var existing))
        {
            existing.Timer.Stop();
            existing.Timer.Tick -= existing.Handler;
            existing.Timer.Interval = interval;
            var newHandler = new EventHandler((s, e) => handler());
            existing.Timer.Tick += newHandler;
            _timers[name] = new ManagedTimer(existing.Timer, newHandler);
            return;
        }

        var timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
        {
            Interval = interval
        };
        var eventHandler = new EventHandler((s, e) => handler());
        timer.Tick += eventHandler;
        _timers[name] = new ManagedTimer(timer, eventHandler);
    }

    /// <summary>
    /// Registers a one-shot timer that stops itself after firing once.
    /// </summary>
    public void RegisterOneShot(string name, TimeSpan delay, Action handler)
    {
        Register(name, delay, () =>
        {
            Stop(name);
            handler();
        });
    }

    /// <summary>Starts the named timer.</summary>
    public void Start(string name)
    {
        if (_timers.TryGetValue(name, out var managed))
            managed.Timer.Start();
    }

    /// <summary>Stops the named timer.</summary>
    public void Stop(string name)
    {
        if (_timers.TryGetValue(name, out var managed))
            managed.Timer.Stop();
    }

    /// <summary>Restarts the named timer (stop + start).</summary>
    public void Restart(string name)
    {
        if (_timers.TryGetValue(name, out var managed))
        {
            managed.Timer.Stop();
            managed.Timer.Start();
        }
    }

    /// <summary>Restarts with a new interval.</summary>
    public void Restart(string name, TimeSpan newInterval)
    {
        if (_timers.TryGetValue(name, out var managed))
        {
            managed.Timer.Stop();
            managed.Timer.Interval = newInterval;
            managed.Timer.Start();
        }
    }

    /// <summary>Returns true if the named timer is currently running.</summary>
    public bool IsRunning(string name)
    {
        return _timers.TryGetValue(name, out var managed) && managed.Timer.IsEnabled;
    }

    /// <summary>Updates the interval of a registered timer without stopping it.</summary>
    public void SetInterval(string name, TimeSpan interval)
    {
        if (_timers.TryGetValue(name, out var managed))
            managed.Timer.Interval = interval;
    }

    /// <summary>Stops and removes a named timer.</summary>
    public void Unregister(string name)
    {
        if (_timers.TryGetValue(name, out var managed))
        {
            managed.Timer.Stop();
            managed.Timer.Tick -= managed.Handler;
            _timers.Remove(name);
        }
    }

    /// <summary>Stops all timers.</summary>
    public void StopAll()
    {
        foreach (var managed in _timers.Values)
            managed.Timer.Stop();
    }

    public void Dispose()
    {
        foreach (var managed in _timers.Values)
        {
            managed.Timer.Stop();
            managed.Timer.Tick -= managed.Handler;
        }
        _timers.Clear();
    }

    private record ManagedTimer(DispatcherTimer Timer, EventHandler Handler);
}
