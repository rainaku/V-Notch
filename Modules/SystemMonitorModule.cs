using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.Modules;

public sealed class SystemMonitorModule : NotchModuleBase
{
    public override string ModuleName => "SystemMonitor";

    public override TimeSpan? TickInterval => TimeSpan.FromSeconds(1);

    private PerformanceCounter? _cpuCounter;
    private readonly List<PerformanceCounter> _netReceivedCounters = new();
    private readonly List<PerformanceCounter> _netSentCounters = new();

    private ulong _usablePhysicalBytes;
    private ulong _installedPhysicalBytes;

    public event EventHandler<SystemMonitorInfo>? StatsUpdated;

    protected override void OnInitialize()
    {
        _usablePhysicalBytes = ReadUsablePhysicalMemory();
        _installedPhysicalBytes = ReadInstalledPhysicalMemory();
        if (_installedPhysicalBytes == 0) _installedPhysicalBytes = _usablePhysicalBytes;

        InitCpuCounter();
        InitNetworkCounters();
    }

    private void InitCpuCounter()
    {
        try
        {
            _cpuCounter = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total");
            _cpuCounter.NextValue();
            return;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("MODULE-SystemMonitor", $"% Processor Utility unavailable, falling back: {ex.Message}");
            _cpuCounter?.Dispose();
            _cpuCounter = null;
        }

        TryInit(() =>
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total"),
            "CPU counter");
    }

    private void InitNetworkCounters()
    {
        try
        {
            var category = new PerformanceCounterCategory("Network Interface");
            foreach (var instance in category.GetInstanceNames())
            {
                if (IsPseudoInterface(instance)) continue;

                try
                {
                    _netReceivedCounters.Add(
                        new PerformanceCounter("Network Interface", "Bytes Received/sec", instance));
                    _netSentCounters.Add(
                        new PerformanceCounter("Network Interface", "Bytes Sent/sec", instance));
                }
                catch (Exception ex)
                {
                    RuntimeLog.Log("MODULE-SystemMonitor", $"Skip NIC '{instance}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("MODULE-SystemMonitor", $"Network counter init failed: {ex.Message}");
        }
    }

    private static bool IsPseudoInterface(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        return name.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
            || name.Contains("isatap", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Teredo", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Pseudo-Interface", StringComparison.OrdinalIgnoreCase);
    }

    protected override void OnTick()
    {
        double cpu = SafeRead(_cpuCounter);
        cpu = Math.Clamp(cpu, 0, 100);

        ulong used = 0;
        double ramPercent = 0;
        var mem = ReadMemoryStatus();
        if (mem != null)
        {
            ulong usable = mem.ullTotalPhys > 0 ? mem.ullTotalPhys : _usablePhysicalBytes;
            ulong available = mem.ullAvailPhys;
            used = available >= usable ? usable : usable - available;
            ramPercent = Math.Clamp(mem.dwMemoryLoad, 0, 100);
        }

        double down = SumCounters(_netReceivedCounters);
        double up = SumCounters(_netSentCounters);

        StatsUpdated?.Invoke(this, new SystemMonitorInfo
        {
            CpuPercent = cpu,
            RamUsedBytes = used,
            RamTotalBytes = _installedPhysicalBytes,
            RamPercent = ramPercent,
            NetDownBytesPerSec = down,
            NetUpBytesPerSec = up
        });
    }

    private static double SumCounters(List<PerformanceCounter> counters)
    {
        double total = 0;
        foreach (var c in counters)
            total += SafeRead(c);
        return total;
    }

    private static double SafeRead(PerformanceCounter? counter)
    {
        if (counter == null) return 0;
        try
        {
            return counter.NextValue();
        }
        catch
        {
            return 0;
        }
    }

    private static void TryInit(Action init, string what)
    {
        try
        {
            init();
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("MODULE-SystemMonitor", $"{what} init failed: {ex.Message}");
        }
    }

    protected override void OnDispose()
    {
        _cpuCounter?.Dispose();
        foreach (var c in _netReceivedCounters) c.Dispose();
        foreach (var c in _netSentCounters) c.Dispose();
        _netReceivedCounters.Clear();
        _netSentCounters.Clear();
    }

    #region Physical memory (GlobalMemoryStatusEx / GetPhysicallyInstalledSystemMemory)

    private static MEMORYSTATUSEX? ReadMemoryStatus()
    {
        try
        {
            var status = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(status))
                return status;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("MODULE-SystemMonitor", $"GlobalMemoryStatusEx failed: {ex.Message}");
        }
        return null;
    }

    private static ulong ReadUsablePhysicalMemory() => ReadMemoryStatus()?.ullTotalPhys ?? 0;

    private static ulong ReadInstalledPhysicalMemory()
    {
        try
        {
            if (GetPhysicallyInstalledSystemMemory(out ulong totalKb) && totalKb > 0)
                return totalKb * 1024UL;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("MODULE-SystemMonitor", $"GetPhysicallyInstalledSystemMemory failed: {ex.Message}");
        }
        return 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;

        public MEMORYSTATUSEX()
        {
            dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        }
    }

    [return: MarshalAs(UnmanagedType.Bool)]
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    [return: MarshalAs(UnmanagedType.Bool)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    #endregion
}
