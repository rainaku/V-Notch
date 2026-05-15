using System;
using VNotch.Services;

namespace VNotch.Modules;

/// <summary>
/// Module that wraps BluetoothMonitorService and exposes device connection events.
/// Uses DeviceWatcher (event-driven), so TickInterval is null (no polling needed).
/// </summary>
public class BluetoothModule : NotchModuleBase
{
    public override string ModuleName => "Bluetooth";

    /// <summary>No polling needed — DeviceWatcher is event-driven.</summary>
    public override TimeSpan? TickInterval => null;

    private readonly BluetoothMonitorService _bluetoothService;

    /// <summary>Raised when a Bluetooth device connects.</summary>
    public event EventHandler<BluetoothDeviceInfo>? DeviceConnected;

    /// <summary>Raised when a Bluetooth device disconnects.</summary>
    public event EventHandler<BluetoothDeviceInfo>? DeviceDisconnected;

    public BluetoothModule(BluetoothMonitorService bluetoothService)
    {
        _bluetoothService = bluetoothService;
    }

    protected override void OnStart()
    {
        _bluetoothService.DeviceConnected += OnDeviceConnected;
        _bluetoothService.DeviceDisconnected += OnDeviceDisconnected;
        _bluetoothService.Start();
    }

    protected override void OnStop()
    {
        _bluetoothService.DeviceConnected -= OnDeviceConnected;
        _bluetoothService.DeviceDisconnected -= OnDeviceDisconnected;
        _bluetoothService.Stop();
    }

    protected override void OnTick()
    {
        // No-op: event-driven via DeviceWatcher
    }

    protected override void OnDispose()
    {
        _bluetoothService.Dispose();
    }

    private void OnDeviceConnected(object? sender, BluetoothDeviceInfo info)
    {
        DeviceConnected?.Invoke(this, info);
    }

    private void OnDeviceDisconnected(object? sender, BluetoothDeviceInfo info)
    {
        DeviceDisconnected?.Invoke(this, info);
    }
}
