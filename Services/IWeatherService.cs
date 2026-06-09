using System.Threading;
using System.Threading.Tasks;
using VNotch.Models;

namespace VNotch.Services;

public interface IWeatherService
{
    Task<WeatherInfo?> GetCurrentWeatherAsync(CancellationToken cancellationToken = default);
}
