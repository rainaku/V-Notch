using System;
using System.Windows.Threading;
using VNotch.Contracts;

namespace VNotch.Modules;

public class CalendarUpdateEventArgs : EventArgs
{
    public DateTime Now { get; set; }
}

public class CalendarModule : INotchModule
{
    public string ModuleName => "Calendar";

    private DispatcherTimer? _timer;
    public event EventHandler<CalendarUpdateEventArgs>? CalendarUpdated;

    public CalendarModule()
    {
    }

    public void Initialize()
    {
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _timer.Tick += Timer_Tick;
    }

    public void Start()
    {
        Update();
        _timer?.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        Update();
    }

    private void Update()
    {
        CalendarUpdated?.Invoke(this, new CalendarUpdateEventArgs { Now = DateTime.Now });
    }
}
