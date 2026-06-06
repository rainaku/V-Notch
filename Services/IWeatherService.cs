using System.Threading;
using System.Threading.Tasks;
using VNotch.Models;

namespace VNotch.Services;

public interface IWeatherService
{
    /// <summary>
    /// Detects the user's approximate location (via IP geolocation) and fetches the
    /// current weather for it. Returns <c>null</c> when the location or weather could
    /// not be resolved (offline, blocked, rate-limited, etc.).
    /// </summary>
    Task<WeatherInfo?> GetCurrentWeatherAsync(CancellationToken cancellationToken = default);
}
