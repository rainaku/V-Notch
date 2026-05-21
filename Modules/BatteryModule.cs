using System;
using VNotch.Services;
using VNotch.Models;

namespace VNotch.Modules;

public class BatteryModule : NotchModuleBase
{
    public override string ModuleName => "Battery";

    public override TimeSpan? TickInterval => TimeSpan.FromSeconds(1);

    private readonly IBatteryService _batteryService;

    public event EventHandler<BatteryInfo>? BatteryUpdated;

    public BatteryModule(IBatteryService batteryService)
    {
        _batteryService = batteryService;
    }

    protected override void OnTick()
    {
        var info = _batteryService.GetBatteryInfo();
        BatteryUpdated?.Invoke(this, info);
    }
}
