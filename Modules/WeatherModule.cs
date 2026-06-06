using System;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.Modules;

public class WeatherUpdateEventArgs : EventArgs
{
    public WeatherInfo Weather { get; init; } = null!;
}

/// <summary>
/// Periodically refreshes the current-location weather and raises <see cref="WeatherUpdated"/>.
/// Refreshes immediately on start, then every 15 minutes.
/// </summary>
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
        // OnTick runs on the UI dispatcher thread (DispatcherTimer). Fire-and-forget the
        // async fetch; the continuation resumes on the UI thread so the event handler can
        // touch UI elements directly.
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
