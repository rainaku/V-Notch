namespace VNotch;

public partial class MainWindow
{
    private void StartCoreModules()
    {
        _batteryModule.Start();
        _bluetoothModule.Start();
        _privacyModule.Start();
    }

    private void EnsureCalendarFeatureLoaded()
    {
        InitializeCalendarPresenter();
        if (!_calendarModule.IsRunning)
        {
            _calendarModule.Start();
        }
    }

    private void EnsureActiveExpandedWidgetFeatureLoaded()
    {
        if (!_isExpanded)
        {
            return;
        }

        if (IsWeatherWidgetMode && _settings.EnableWeather && !_weatherModule.IsRunning)
        {
            _weatherModule.Start();
        }

        if (IsSystemMonitorWidgetMode && !_systemMonitorModule.IsRunning)
        {
            _systemMonitorModule.Start();
        }
    }
}
