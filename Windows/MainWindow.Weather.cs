using VNotch.Models;
using VNotch.Modules;
using VNotch.Presenters;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    #region Weather Widget

    private WeatherPresenter? _weatherPresenter;

    internal void InitializeWeatherPresenter()
    {
        if (_weatherPresenter != null) return;

        var refs = new WeatherViewRefs(
            WeatherLocationText,
            WeatherTempText,
            WeatherConditionText,
            WeatherHiLoText);

        _weatherPresenter = new WeatherPresenter(_weatherModule, new DispatcherService(Dispatcher), refs);
    }

    internal void DisposeWeatherPresenter()
    {
        _weatherPresenter?.Dispose();
        _weatherPresenter = null;
    }

    private void WeatherModule_WeatherUpdated(object? sender, WeatherUpdateEventArgs e)
    {
        if (e.Weather == null)
        {
            // Weather was disabled — clear cached data from UI
            Dispatcher.Invoke(() =>
            {
                WeatherLocationText.Text = "—";
                WeatherTempText.Text = "—°";
                WeatherConditionText.Text = "";
                WeatherHiLoText.Text = "";
            });
            return;
        }

        UpdateWeatherUI(e.Weather);
    }

    private void UpdateWeatherUI(WeatherInfo weather)
    {
        if (weather == null) return;

        WeatherLocationText.Text = string.IsNullOrWhiteSpace(weather.City) ? "—" : weather.City;
        WeatherTempText.Text = $"{weather.Temperature}°";
        WeatherConditionText.Text = weather.Condition;
        WeatherHiLoText.Text = $"H:{weather.High}° L:{weather.Low}°";
    }

    #endregion
}