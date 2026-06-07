namespace VNotch.Models;

/// <summary>
/// A single snapshot of the machine's live resource usage, emitted by
/// <see cref="VNotch.Modules.SystemMonitorModule"/> on every tick. All values are
/// already computed/normalised so the UI layer can bind them directly without any
/// further maths.
/// </summary>
public sealed class SystemMonitorInfo
{
    /// <summary>Total CPU utilisation across all cores, 0–100.</summary>
    public double CpuPercent { get; init; }

    /// <summary>Physical memory currently in use, in bytes.</summary>
    public ulong RamUsedBytes { get; init; }

    /// <summary>Total installed physical memory, in bytes.</summary>
    public ulong RamTotalBytes { get; init; }

    /// <summary>Used memory as a percentage of total, 0–100.</summary>
    public double RamPercent =>
        RamTotalBytes == 0 ? 0 : (double)RamUsedBytes / RamTotalBytes * 100.0;

    /// <summary>Current download rate (bytes received per second) summed over active NICs.</summary>
    public double NetDownBytesPerSec { get; init; }

    /// <summary>Current upload rate (bytes sent per second) summed over active NICs.</summary>
    public double NetUpBytesPerSec { get; init; }
}
