using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.Modules;

/// <summary>
/// Polls live system resource usage — total CPU%, physical RAM used/total, and the
/// aggregate network up/down rate — and raises <see cref="StatsUpdated"/> once per
/// second so the notch can show an at-a-glance system monitor widget.
///
/// CPU, available RAM and network throughput are read through Windows
/// <see cref="PerformanceCounter"/>s. Total physical memory has no live counter, so it
/// is read once at start via <c>GlobalMemoryStatusEx</c>. Every counter access is
/// defensive: a missing/renamed instance (e.g. a VPN NIC that disappears) is skipped
/// rather than allowed to break the whole tick, and the base class also wraps OnTick in
/// try/catch.
/// </summary>
public sealed class SystemMonitorModule : NotchModuleBase
{
    public override string ModuleName => "SystemMonitor";

    // 1s gives a responsive-but-cheap glance. The first read of CPU / network counters
    // returns 0 by design, so the immediate tick fired on Start just primes them and the
    // first meaningful value lands one interval later.
    public override TimeSpan? TickInterval => TimeSpan.FromSeconds(1);

    private PerformanceCounter? _cpuCounter;
    private PerformanceCounter? _ramAvailableBytesCounter;
    private readonly List<PerformanceCounter> _netReceivedCounters = new();
    private readonly List<PerformanceCounter> _netSentCounters = new();

    private ulong _totalPhysicalBytes;

    public event EventHandler<SystemMonitorInfo>? StatsUpdated;

    protected override void OnInitialize()
    {
        _totalPhysicalBytes = ReadTotalPhysicalMemory();

        TryInit(() =>
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total"),
            "CPU counter");

        // "Available Bytes" lets us derive used = total - available without a second API.
        TryInit(() =>
            _ramAvailableBytesCounter = new PerformanceCounter("Memory", "Available Bytes"),
            "RAM counter");

        InitNetworkCounters();
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

    // Loopback / tunnelling pseudo-adapters report traffic that isn't real connectivity,
    // so they're excluded from the up/down totals.
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
        if (_totalPhysicalBytes > 0 && _ramAvailableBytesCounter != null)
        {
            ulong available = (ulong)Math.Max(0, SafeRead(_ramAvailableBytesCounter));
            used = available >= _totalPhysicalBytes ? _totalPhysicalBytes : _totalPhysicalBytes - available;
        }

        double down = SumCounters(_netReceivedCounters);
        double up = SumCounters(_netSentCounters);

        StatsUpdated?.Invoke(this, new SystemMonitorInfo
        {
            CpuPercent = cpu,
            RamUsedBytes = used,
            RamTotalBytes = _totalPhysicalBytes,
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
        _ramAvailableBytesCounter?.Dispose();
        foreach (var c in _netReceivedCounters) c.Dispose();
        foreach (var c in _netSentCounters) c.Dispose();
        _netReceivedCounters.Clear();
        _netSentCounters.Clear();
    }

    #region Total physical memory (GlobalMemoryStatusEx)

    private static ulong ReadTotalPhysicalMemory()
    {
        try
        {
            var status = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(status))
                return status.ullTotalPhys;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("MODULE-SystemMonitor", $"GlobalMemoryStatusEx failed: {ex.Message}");
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

    #endregion
}
