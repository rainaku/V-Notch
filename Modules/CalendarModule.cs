using System;
using VNotch.Contracts;

namespace VNotch.Modules;

public class CalendarUpdateEventArgs : EventArgs
{
    public DateTime Now { get; set; }
}

public class CalendarModule : NotchModuleBase
{
    public override string ModuleName => "Calendar";

    public override TimeSpan? TickInterval => TimeSpan.FromSeconds(30);

    public event EventHandler<CalendarUpdateEventArgs>? CalendarUpdated;

    protected override void OnTick()
    {
        CalendarUpdated?.Invoke(this, new CalendarUpdateEventArgs { Now = DateTime.Now });
    }
}
