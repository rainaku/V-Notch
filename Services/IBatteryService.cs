using VNotch.Models;

namespace VNotch.Services;

/// <summary>
/// Interface for battery information retrieval.
/// </summary>
public interface IBatteryService
{
    /// <summary>
    /// Get current battery information.
    /// </summary>
    BatteryInfo GetBatteryInfo();
}
