using System.Windows;
using VNotch.Models;
using VNotch.Services;

namespace VNotch;

public partial class SettingsWindow : Window
{
    private readonly NotchSettings _settings;
    private readonly SettingsService _settingsService;

    public event EventHandler<NotchSettings>? SettingsChanged;

    public SettingsWindow(NotchSettings settings, SettingsService settingsService)
    {
        InitializeComponent();

        _settings = settings.Clone();
        _settingsService = settingsService;

        LoadSettings();
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
}