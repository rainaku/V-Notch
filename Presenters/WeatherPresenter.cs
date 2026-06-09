using System;
using System.Windows.Controls;
using VNotch.Models;
using VNotch.Modules;
using VNotch.Services;

namespace VNotch.Presenters;

public sealed record WeatherViewRefs(
    TextBlock LocationText,
    TextBlock TempText,
    TextBlock ConditionText,
    TextBlock HiLoText);

public sealed class WeatherPresenter : IDisposable
{
    private readonly WeatherModule _module;
    private readonly IDispatcherService _dispatcher;
    private readonly WeatherViewRefs _refs;
    private bool _disposed;

    public WeatherPresenter(WeatherModule module, IDispatcherService dispatcher, WeatherViewRefs refs)
    {
        _module = module ?? throw new ArgumentNullException(nameof(module));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _refs = refs ?? throw new ArgumentNullException(nameof(refs));

        _module.WeatherUpdated += OnWeatherUpdated;
    }

    private void OnWeatherUpdated(object? sender, WeatherUpdateEventArgs e)
    {
        if (_dispatcher.CheckAccess())
        {
            UpdateWeatherUI(e.Weather);
        }
        else
        {
            _dispatcher.Invoke(() => UpdateWeatherUI(e.Weather));
        }
    }

    private void UpdateWeatherUI(WeatherInfo weather)
    {
        if (weather == null) return;

        _refs.LocationText.Text = string.IsNullOrWhiteSpace(weather.City) ? "—" : weather.City;
        _refs.TempText.Text = $"{weather.Temperature}°";
        _refs.ConditionText.Text = weather.Condition;
        _refs.HiLoText.Text = $"H:{weather.High}° L:{weather.Low}°";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _module.WeatherUpdated -= OnWeatherUpdated;
    }
}
