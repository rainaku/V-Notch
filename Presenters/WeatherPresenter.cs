using System;
using System.Windows.Controls;
using VNotch.Models;
using VNotch.Modules;
using VNotch.Services;

namespace VNotch.Presenters;

/// <summary>
/// Typed view-contract for the weather widget. Holds the XAML-named <see cref="TextBlock"/>
/// elements the presenter mutates. Constructed by the <c>MainWindow</c> partial (which has
/// <c>x:Name</c> access) and passed in once, so the presenter never reaches back into the window.
/// </summary>
public sealed record WeatherViewRefs(
    TextBlock LocationText,
    TextBlock TempText,
    TextBlock ConditionText,
    TextBlock HiLoText);

/// <summary>
/// Thin module bridge: subscribes to <see cref="WeatherModule.WeatherUpdated"/> and routes the
/// payload to the weather widget's labels. This is a pure relocation of the logic that previously
/// lived in <c>MainWindow.Weather.cs</c>; visibility of the widget itself remains owned by the
/// shell's <c>ApplyExpandedWidgetMode</c>, so this only refreshes content.
/// </summary>
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
        // The module raises this on the UI dispatcher thread (DispatcherTimer continuation),
        // so on the normal path CheckAccess() is true and the update runs inline exactly as the
        // original code-behind handler did. The Invoke fallback only guards an off-thread call.
        if (_dispatcher.CheckAccess())
        {
            UpdateWeatherUI(e.Weather);
        }
        else
        {
            _dispatcher.Invoke(() => UpdateWeatherUI(e.Weather));
        }
    }

    /// <summary>
    /// Populates the weather widget's labels. Identical to the former
    /// <c>MainWindow.UpdateWeatherUI</c> routing.
    /// </summary>
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
