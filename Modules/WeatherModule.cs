using System;
using System.Threading;
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
    private readonly ISettingsService _settingsService;
    private bool _isFetching;
    private CancellationTokenSource? _cts;
    private WeatherInfo? _lastWeather;

    public WeatherModule(IWeatherService weatherService, ISettingsService settingsService)
    {
        _weatherService = weatherService;
        _settingsService = settingsService;
    }

    public override string ModuleName => "Weather";

    public override TimeSpan? TickInterval => TimeSpan.FromMinutes(15);

    public event EventHandler<WeatherUpdateEventArgs>? WeatherUpdated;

    /// <summary>
    /// Called by MainWindow when settings change to enable/disable weather at runtime.
    /// </summary>
    public void OnSettingsChanged(NotchSettings settings)
    {
        if (!settings.EnableWeather)
        {
            // Stop timer, cancel any pending request, clear cached UI
            Stop();
            CancelPendingRequest();
            ClearWeatherData();
        }
        else if (!IsRunning)
        {
            // Weather was just enabled — start the module and do an immediate refresh
            Start();
            _ = RefreshAsync();
        }
    }

    private void CancelPendingRequest()
    {
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch (ObjectDisposedException) { }
        _cts = null;
        _isFetching = false;
    }

    private void ClearWeatherData()
    {
        _lastWeather = null;
        // Notify UI to clear with a null weather
        WeatherUpdated?.Invoke(this, new WeatherUpdateEventArgs { Weather = null! });
    }

    protected override void OnTick()
    {
        _ = RefreshAsync();
    }

    private async System.Threading.Tasks.Task RefreshAsync()
    {
        var settings = _settingsService.Load();
        if (!settings.EnableWeather)
        {
            // Safety check — should not happen if module is stopped when disabled
            return;
        }

        if (_isFetching) return;
        _isFetching = true;

        _cts = new CancellationTokenSource();
        try
        {
            string? manualCity = string.IsNullOrWhiteSpace(settings.ManualCity) ? null : settings.ManualCity;
            var weather = await _weatherService.GetCurrentWeatherAsync(manualCity, _cts.Token).ConfigureAwait(true);
            if (weather != null)
            {
                _lastWeather = weather;
                WeatherUpdated?.Invoke(this, new WeatherUpdateEventArgs { Weather = weather });
            }
        }
        catch (OperationCanceledException)
        {
            // Request was cancelled — expected when disabling weather
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

    protected override void OnDispose()
    {
        CancelPendingRequest();
        base.OnDispose();
    }
}