namespace VNotch.Models;

public sealed class SystemMonitorInfo
{
    public double CpuPercent { get; init; }

    public ulong RamUsedBytes { get; init; }

    public ulong RamTotalBytes { get; init; }

    public double RamPercent { get; init; }

    public double NetDownBytesPerSec { get; init; }

    public double NetUpBytesPerSec { get; init; }
}
