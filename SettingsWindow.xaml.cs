using System.Windows;
using VNotch.Models;
using VNotch.Services;

namespace VNotch;

public partial class SettingsWindow : Window
{
    private readonly NotchSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly IUpdateService _updateService;
    private UpdateInfo? _availableUpdate;

    public event EventHandler<NotchSettings>? SettingsChanged;

    public SettingsWindow(NotchSettings settings, SettingsService settingsService)
    {
        InitializeComponent();

        _settings = settings.Clone();
        _settingsService = settingsService;
        _updateService = new UpdateService();

        LoadSettings();
        _ = CheckForUpdatesAsync();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void LoadSettings()
    {

        WidthSlider.Value = _settings.Width;
        HeightSlider.Value = _settings.Height;
        RadiusSlider.Value = _settings.CornerRadius;
        OpacitySlider.Value = _settings.Opacity * 100;

        HoverExpandCheck.IsChecked = _settings.EnableHoverExpand;
        HoverDelaySlider.Value = _settings.HoverExpandDelay;

        var monitors = NotchManager.GetMonitorNames();
        MonitorCombo.ItemsSource = monitors;
        MonitorCombo.SelectedIndex = Math.Min(_settings.MonitorIndex, monitors.Length - 1);

        AutoStartCheck.IsChecked = StartupManager.IsAutoStartEnabled();
        MusicNotifyCheck.IsChecked = _settings.ShowMusicNotifications;
        SystemNotifyCheck.IsChecked = _settings.ShowSystemNotifications;
    }

    #region Slider Value Changed Handlers

    private void WidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (WidthValue != null)
            WidthValue.Text = ((int)e.NewValue).ToString();
    }

    private void HeightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (HeightValue != null)
            HeightValue.Text = ((int)e.NewValue).ToString();
    }

    private void RadiusSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RadiusValue != null)
            RadiusValue.Text = ((int)e.NewValue).ToString();
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityValue != null)
            OpacityValue.Text = ((int)e.NewValue).ToString();
    }

    private void HoverDelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (HoverDelayValue != null)
            HoverDelayValue.Text = ((int)e.NewValue).ToString();
    }

    #endregion

    #region Button Handlers

    private void Reset_Click(object sender, RoutedEventArgs e)
    {

        var defaults = new NotchSettings();

        WidthSlider.Value = defaults.Width;
        HeightSlider.Value = defaults.Height;
        RadiusSlider.Value = defaults.CornerRadius;
        OpacitySlider.Value = defaults.Opacity * 100;

        HoverExpandCheck.IsChecked = defaults.EnableHoverExpand;
        HoverDelaySlider.Value = defaults.HoverExpandDelay;

        MusicNotifyCheck.IsChecked = defaults.ShowMusicNotifications;
        SystemNotifyCheck.IsChecked = defaults.ShowSystemNotifications;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {

        _settings.Width = (int)WidthSlider.Value;
        _settings.Height = (int)HeightSlider.Value;
        _settings.CornerRadius = (int)RadiusSlider.Value;
        _settings.Opacity = OpacitySlider.Value / 100.0;

        _settings.EnableHoverExpand = HoverExpandCheck.IsChecked ?? true;
        _settings.HoverExpandDelay = (int)HoverDelaySlider.Value;

        _settings.MonitorIndex = MonitorCombo.SelectedIndex;
        _settings.AutoStart = AutoStartCheck.IsChecked ?? false;
        _settings.ShowMusicNotifications = MusicNotifyCheck.IsChecked ?? true;
        _settings.ShowSystemNotifications = SystemNotifyCheck.IsChecked ?? true;

        _settingsService.Save(_settings);

        StartupManager.SetAutoStart(_settings.AutoStart);

        SettingsChanged?.Invoke(this, _settings);

        Close();
    }

    #endregion

    #region Update Handlers

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            UpdateStatusText.Text = "Checking for updates...";
            CheckUpdateButton.IsEnabled = false;
            DownloadUpdateButton.Visibility = Visibility.Collapsed;

            _availableUpdate = await _updateService.CheckForUpdatesAsync();

            if (_availableUpdate == null)
            {
                UpdateStatusText.Text = "Unable to check for updates";
                CheckUpdateButton.IsEnabled = true;
                return;
            }

            if (_availableUpdate.IsNewerVersion)
            {
                UpdateStatusText.Text = $"New version {_availableUpdate.Version} available!";
                DownloadUpdateButton.Visibility = Visibility.Visible;
            }
            else
            {
                UpdateStatusText.Text = "You're up to date";
            }

            CheckUpdateButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Error: {ex.Message}";
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesAsync();
    }

    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate == null) return;

        var result = MessageBox.Show(
            $"Do you want to download and install version {_availableUpdate.Version}?\n\n" +
            $"Release Notes:\n{_availableUpdate.ReleaseNotes.Substring(0, Math.Min(200, _availableUpdate.ReleaseNotes.Length))}...\n\n" +
            "The application will close and the installer will run.",
            "Update Available",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            UpdateStatusText.Text = "Downloading update...";
            DownloadUpdateButton.IsEnabled = false;

            var success = await _updateService.DownloadAndInstallUpdateAsync(_availableUpdate);

            if (!success)
            {
                UpdateStatusText.Text = "Failed to download update";
                DownloadUpdateButton.IsEnabled = true;
                MessageBox.Show("Failed to download or install the update. Please try again later.", "Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    #endregion
}