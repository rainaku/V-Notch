using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Models;
using VNotch.Modules;
using VNotch.Presenters;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    #region Weather Widget

    private WeatherPresenter? _weatherPresenter;
    private bool _hasWeatherData;
    private int _weatherRevealVersion;

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
        Dispatcher.Invoke(() =>
        {
            var weather = e.Weather;
            if (weather == null)
            {
                ShowWeatherStatus(_settings.EnableWeather);
                return;
            }

            UpdateWeatherUI(weather);
        });
    }

    private void ShowWeatherStatus(bool isEnabled)
    {
        _hasWeatherData = false;
        _weatherRevealVersion++;
        WeatherWidgetContent.BeginAnimation(OpacityProperty, null);
        WeatherWidgetTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        WeatherLocationText.Text = Loc.Get(isEnabled ? "weather.unavailable" : "weather.disabled");
        WeatherTempText.Text = "\u2014\u00b0";
        WeatherConditionText.Text = Loc.Get(isEnabled ? "weather.retryLater" : "weather.enableInSettings");
        WeatherHiLoText.Text = string.Empty;
        WeatherWidgetContent.Opacity = 1;
        WeatherWidgetTranslate.Y = 0;
    }

    private void UpdateWeatherUI(WeatherInfo weather)
    {
        bool shouldReveal = !_hasWeatherData;
        WeatherLocationText.Text = string.IsNullOrWhiteSpace(weather.City) ? "\u2014" : weather.City;
        WeatherTempText.Text = $"{weather.Temperature}\u00b0";
        WeatherConditionText.Text = WeatherConditionFormatter.Format(weather.WeatherCode);
        WeatherHiLoText.Text = Loc.Get("weather.highLow", weather.High, weather.Low);
        _hasWeatherData = true;

        if (shouldReveal)
            RevealWeatherContent();
    }

    private void RevealWeatherContent()
    {
        int transitionVersion = ++_weatherRevealVersion;
        WeatherWidgetContent.BeginAnimation(OpacityProperty, null);
        WeatherWidgetTranslate.BeginAnimation(TranslateTransform.YProperty, null);

        if (AnimationConfig.ReduceMotion)
        {
            WeatherWidgetContent.Opacity = 1;
            WeatherWidgetTranslate.Y = 0;
            return;
        }

        WeatherWidgetContent.Opacity = 0;
        WeatherWidgetTranslate.Y = 8;

        var duration = new Duration(TimeSpan.FromMilliseconds(420));
        var ease = new ExponentialEase { Exponent = 5, EasingMode = EasingMode.EaseOut };
        var fadeIn = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };
        var slideIn = new DoubleAnimation(8, 0, duration) { EasingFunction = ease };

        fadeIn.Completed += (_, _) =>
        {
            if (!_hasWeatherData || transitionVersion != _weatherRevealVersion)
                return;

            WeatherWidgetContent.BeginAnimation(OpacityProperty, null);
            WeatherWidgetTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            WeatherWidgetContent.Opacity = 1;
            WeatherWidgetTranslate.Y = 0;
        };

        Timeline.SetDesiredFrameRate(fadeIn, AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(slideIn, AnimationConfig.TargetFps);
        WeatherWidgetContent.BeginAnimation(OpacityProperty, fadeIn);
        WeatherWidgetTranslate.BeginAnimation(TranslateTransform.YProperty, slideIn);
    }

    #endregion
}
