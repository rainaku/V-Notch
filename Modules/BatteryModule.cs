using System;
using System.Windows.Threading;
using VNotch.Contracts;
using VNotch.Services;
using VNotch.Models;

namespace VNotch.Modules;

public class BatteryModule : INotchModule
{
    public string ModuleName => "Battery";

    private readonly IBatteryService _batteryService;
    private DispatcherTimer? _timer;

    public event EventHandler<BatteryInfo>? BatteryUpdated;

    public BatteryModule(IBatteryService batteryService)
    {
        _batteryService = batteryService;
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
        try
        {
            var info = _batteryService.GetBatteryInfo();
            BatteryUpdated?.Invoke(this, info);
        }
        catch { }
    }
}
