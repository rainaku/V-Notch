using System;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.Modules;

public class WeatherUpdateEventArgs : EventArgs
{
    public WeatherInfo Weather { get; init; } = null!;
}

public class WeatherModule : NotchModuleBase
{
    private readonly IWeatherService _weatherService;
    private bool _isFetching;

    public WeatherModule(IWeatherService weatherService)
    {
        _weatherService = weatherService;
    }

    public override string ModuleName => "Weather";

    public override TimeSpan? TickInterval => TimeSpan.FromMinutes(15);

    public event EventHandler<WeatherUpdateEventArgs>? WeatherUpdated;

    protected override void OnTick()
    {
        _ = RefreshAsync();
    }

    private async System.Threading.Tasks.Task RefreshAsync()
    {
        if (_isFetching) return;
        _isFetching = true;
        try
        {
            var weather = await _weatherService.GetCurrentWeatherAsync().ConfigureAwait(true);
            if (weather != null)
            {
                WeatherUpdated?.Invoke(this, new WeatherUpdateEventArgs { Weather = weather });
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("MODULE-Weather", $"Refresh failed: {ex.Message}");
        }
        finally
        {
            _isFetching = false;
        }
    }
}
