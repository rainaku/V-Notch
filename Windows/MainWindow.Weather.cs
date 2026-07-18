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
        var weather = e.Weather;
        if (weather == null)
        {
            Dispatcher.Invoke(() => ShowWeatherStatus(_settings.EnableWeather));
            return;
        }

        UpdateWeatherUI(weather);
    }

    private void ShowWeatherStatus(bool isEnabled)
    {
        WeatherLocationText.Text = Loc.Get(isEnabled ? "weather.unavailable" : "weather.disabled");
        WeatherTempText.Text = "\u2014\u00b0";
        WeatherConditionText.Text = Loc.Get(isEnabled ? "weather.retryLater" : "weather.enableInSettings");
        WeatherHiLoText.Text = string.Empty;
    }

    private void UpdateWeatherUI(WeatherInfo weather)
    {
        WeatherLocationText.Text = string.IsNullOrWhiteSpace(weather.City) ? "\u2014" : weather.City;
        WeatherTempText.Text = $"{weather.Temperature}\u00b0";
        WeatherConditionText.Text = weather.Condition;
        WeatherHiLoText.Text = Loc.Get("weather.highLow", weather.High, weather.Low);
    }

    #endregion
}
