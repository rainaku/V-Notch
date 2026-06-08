// JOIN: call InitializeWeatherPresenter() in ctor, DisposeWeatherPresenter() in PerformCleanup.
// When wiring the presenter in, replace the existing
//   _weatherModule.WeatherUpdated += WeatherModule_WeatherUpdated;   (ctor)
//   _weatherModule.WeatherUpdated -= WeatherModule_WeatherUpdated;   (PerformCleanup)
// with the Initialize/Dispose calls below, then the bridge handler
// (WeatherModule_WeatherUpdated / UpdateWeatherUI) becomes dead and can be deleted.
using VNotch.Models;
using VNotch.Modules;
using VNotch.Presenters;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    #region Weather Widget

    // Owned weather presenter. Null until InitializeWeatherPresenter() runs (JOIN step).
    private WeatherPresenter? _weatherPresenter;

    /// <summary>
    /// Constructs the <see cref="WeatherPresenter"/>, handing it the weather module, a dispatcher,
    /// and the typed view-refs for the XAML-named labels it owns. The presenter subscribes to the
    /// module on construction. Idempotent: a second call is a no-op.
    /// </summary>
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

    /// <summary>Disposes the weather presenter (unsubscribes from the module). Idempotent.</summary>
    internal void DisposeWeatherPresenter()
    {
        _weatherPresenter?.Dispose();
        _weatherPresenter = null;
    }

    // --- Bridge (active until the JOIN step rewires the ctor onto the presenter) -------------
    // The constructor and PerformCleanup in MainWindow.xaml.cs still reference this handler, so it
    // stays functional to keep the app building and behaving identically until JOIN switches over.
    // The routing logic now also lives in WeatherPresenter (the future single owner).

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
