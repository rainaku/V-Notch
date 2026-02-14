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

    private void LoadSettings()
    {
        // Appearance
        WidthSlider.Value = _settings.Width;
        HeightSlider.Value = _settings.Height;
        RadiusSlider.Value = _settings.CornerRadius;
        OpacitySlider.Value = _settings.Opacity * 100;

        // Visual Effects
        ShadowCheck.IsChecked = _settings.EnableShadow;
        GlowCheck.IsChecked = _settings.EnableGlowOnHover;
        CameraCheck.IsChecked = _settings.ShowCameraIndicator;

        // Animations
        AnimationCheck.IsChecked = _settings.EnableAnimations;
        BounceCheck.IsChecked = _settings.EnableBounceEffect;
        AnimSpeedSlider.Value = _settings.AnimationSpeed;

        // Behavior
        HoverExpandCheck.IsChecked = _settings.EnableHoverExpand;
        CursorBypassCheck.IsChecked = _settings.EnableCursorBypass;
        HoverDelaySlider.Value = _settings.HoverExpandDelay;

        // Monitor
        var monitors = NotchManager.GetMonitorNames();
        MonitorCombo.ItemsSource = monitors;
        MonitorCombo.SelectedIndex = Math.Min(_settings.MonitorIndex, monitors.Length - 1);

        // System
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

    private void AnimSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AnimSpeedValue != null)
            AnimSpeedValue.Text = e.NewValue.ToString("F1");
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
        // Reset to defaults
        var defaults = new NotchSettings();

        WidthSlider.Value = defaults.Width;
        HeightSlider.Value = defaults.Height;
        RadiusSlider.Value = defaults.CornerRadius;
        OpacitySlider.Value = defaults.Opacity * 100;

        ShadowCheck.IsChecked = defaults.EnableShadow;
        GlowCheck.IsChecked = defaults.EnableGlowOnHover;
        CameraCheck.IsChecked = defaults.ShowCameraIndicator;

        AnimationCheck.IsChecked = defaults.EnableAnimations;
        BounceCheck.IsChecked = defaults.EnableBounceEffect;
        AnimSpeedSlider.Value = defaults.AnimationSpeed;

        HoverExpandCheck.IsChecked = defaults.EnableHoverExpand;
        CursorBypassCheck.IsChecked = defaults.EnableCursorBypass;
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
        // Update settings from UI
        _settings.Width = (int)WidthSlider.Value;
        _settings.Height = (int)HeightSlider.Value;
        _settings.CornerRadius = (int)RadiusSlider.Value;
        _settings.Opacity = OpacitySlider.Value / 100.0;

        _settings.EnableShadow = ShadowCheck.IsChecked ?? true;
        _settings.EnableGlowOnHover = GlowCheck.IsChecked ?? true;
        _settings.ShowCameraIndicator = CameraCheck.IsChecked ?? true;

        _settings.EnableAnimations = AnimationCheck.IsChecked ?? true;
        _settings.EnableBounceEffect = BounceCheck.IsChecked ?? true;
        _settings.AnimationSpeed = AnimSpeedSlider.Value;

        _settings.EnableHoverExpand = HoverExpandCheck.IsChecked ?? true;
        _settings.EnableCursorBypass = CursorBypassCheck.IsChecked ?? true;
        _settings.HoverExpandDelay = (int)HoverDelaySlider.Value;

        _settings.MonitorIndex = MonitorCombo.SelectedIndex;
        _settings.AutoStart = AutoStartCheck.IsChecked ?? false;
        _settings.ShowMusicNotifications = MusicNotifyCheck.IsChecked ?? true;
        _settings.ShowSystemNotifications = SystemNotifyCheck.IsChecked ?? true;

        // Save to file
        _settingsService.Save(_settings);

        // Update auto start
        StartupManager.SetAutoStart(_settings.AutoStart);

        // Notify main window
        SettingsChanged?.Invoke(this, _settings);

        Close();
    }

    #endregion
}
