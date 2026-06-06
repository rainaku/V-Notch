using VNotch.Models;
using VNotch.Modules;

namespace VNotch;

public partial class MainWindow
{
    #region Weather Widget

    private void WeatherModule_WeatherUpdated(object? sender, WeatherUpdateEventArgs e)
    {
        UpdateWeatherUI(e.Weather);
    }

    /// <summary>
    /// Populates the weather widget's labels. Visibility of the widget itself is owned
    /// by <see cref="ApplyExpandedWidgetMode"/> (it is one of the selectable
    /// expanded-notch widgets), so this only refreshes the content.
    /// </summary>
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
