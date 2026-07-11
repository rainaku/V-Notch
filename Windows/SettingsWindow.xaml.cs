using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Modules;
using VNotch.Services;

namespace VNotch;

public partial class SettingsWindow : Window
{
    private readonly NotchSettings _settings;
    private NotchSettings _originalSettings;
    private readonly SettingsService _settingsService;
    private readonly BluetoothModule? _bluetoothModule;
    private readonly IUpdateService _updateService;
    private UpdateInfo? _availableUpdate;
    private bool _isLoadingSettings = true;
    private DispatcherTimer? _livePreviewDebounce;

    public event EventHandler<NotchSettings>? SettingsChanged;
public event EventHandler? AnimatedClosing;

    public SettingsWindow(NotchSettings settings, SettingsService settingsService, BluetoothModule? bluetoothModule = null)
    {
        InitializeComponent();
        AnimationPrimitives.ApplyFpsToTree(this);

        _settings = settings.Clone();
        _originalSettings = settings.Clone();
        _settingsService = settingsService;
        _bluetoothModule = bluetoothModule;
        _updateService = new UpdateService();

        InitializeNavigation();
        LoadSettings();
        CheckForUpdatesAsync().SafeFireAndForget("SETTINGS-UPDATE-CHECK");
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PlayEntranceAnimation();
        LoadVisualizerAudioDevices().SafeFireAndForget("SETTINGS-VIS-AUDIO");
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void WindowSurface_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (IsInteractiveElement(e.OriginalSource as DependencyObject))
        {
            return;
        }

        DragMove();
    }

    private static bool IsInteractiveElement(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is ButtonBase or Slider or Thumb or ComboBox or ComboBoxItem or CheckBox or ScrollBar or TextBox or PasswordBox)
            {
                return true;
            }

            if (source is FrameworkElement fe)
            {
                if (fe.Name == "SubtitlePriorityItems")
                    return true;

                if (fe is Border border && border.Tag is string tag &&
                    (tag == "Searching" || tag == "Appearance" || tag == "Skins" || tag == "Behavior" || tag == "Devices" ||
                     tag == "System" || tag == "Advanced" || tag == "Performance" ||
                     tag == "Donating" || tag == "Updates"))
                {
                    return true;
                }
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void LoadSettings()
    {
        _isLoadingSettings = true;

        // Apply tooltips to UI elements
        ApplyTooltips();

        WidthSlider.Value = _settings.Width;
        DynamicIslandWidthSlider.Value = _settings.DynamicIslandWidth;
        DynamicIslandHeightSlider.Value = _settings.DynamicIslandHeight;
        HeightSlider.Value = _settings.Height;
        RadiusSlider.Value = _settings.CornerRadius;
        OpacitySlider.Value = _settings.Opacity * 100;
        BlurBrightnessSlider.Value = _settings.MediaBlurBrightnessBoost * 100;
        BlurDarkOverlaySlider.Value = _settings.MediaBlurDarkOverlay * 100;
        AnimationFpsSlider.Value = _settings.AnimationFps;
        EnableBlurEffectsCheck.IsChecked = _settings.EnableBlurEffects;
        MediaArtBackgroundCheck.IsChecked = _settings.ShowMediaArtBackground;
        LoadLiquidGlassUi();
        EnableSubjectBlurCheck.IsChecked = _settings.EnableSubjectBlur;
        EnableSmartCropCheck.IsChecked = _settings.EnableSmartCrop;
        UpdatePerformanceDependentControls(_settings.EnableBlurEffects);
        EnableSpotifyLyricsCheck.IsChecked = _settings.EnableSpotifyLyrics;
        UpdateLyricsDependentControls(_settings.EnableSpotifyLyrics);
        EnableYouTubeSubtitlesCheck.IsChecked = _settings.EnableYouTubeSubtitles;
        UpdateYouTubeSubtitlesDependentControls(_settings.EnableYouTubeSubtitles);

        LoadSubtitlePriority();

        DynamicIslandModeCheck.IsChecked = _settings.EnableDynamicIslandMode;
        UpdateDynamicIslandDependentControls(_settings.EnableDynamicIslandMode);

        HoverExpandCheck.IsChecked = _settings.EnableHoverExpand;
        HoverDelaySlider.Value = _settings.HoverExpandDelay;
        HoverDelaySlider.IsEnabled = _settings.EnableHoverExpand;
        HoverDelaySlider.Opacity = _settings.EnableHoverExpand ? 1.0 : 0.4;
        DisableMouseLeaveAutoCloseCheck.IsChecked = _settings.DisableMouseLeaveAutoClose;
        ReopenLastViewCheck.IsChecked = _settings.ReopenLastViewOnExpand;

        var monitors = NotchManager.GetMonitorNames();
        MonitorCombo.ItemsSource = monitors;
        MonitorCombo.SelectedIndex = Math.Min(_settings.MonitorIndex, monitors.Length - 1);

        LoadCameraDevices().SafeFireAndForget("SETTINGS-CAMERA-DEVICES");
        SetVisualizerAudioDevicePlaceholder();

        AutoStartCheck.IsChecked = StartupManager.IsAutoStartEnabled();
        HelloGreetingCheck.IsChecked = _settings.EnableHelloGreeting;
        HideOnExclusiveFullscreenCheck.IsChecked = _settings.HideOnExclusiveFullscreen;
        HideOnWindowedFullscreenCheck.IsChecked = _settings.HideOnWindowedFullscreen;
        IdleAutoHideCheck.IsChecked = _settings.EnableIdleAutoHide;
        IdleAutoHideDelaySlider.Value = Math.Max(2, _settings.IdleAutoHideDelay / 1000.0);
        IdleAutoHideDelaySlider.IsEnabled = _settings.EnableIdleAutoHide;
        IdleAutoHideDelaySlider.Opacity = _settings.EnableIdleAutoHide ? 1.0 : 0.4;
        MusicNotifyCheck.IsChecked = _settings.ShowMusicNotifications;
        SystemNotifyCheck.IsChecked = _settings.ShowSystemNotifications;
        ShelfUnlockCheck.IsChecked = _settings.IsShelfUploadLimitUnlocked;
        CopyShelfClipboardCheck.IsChecked = _settings.CopyShelfFilesToClipboard;
        ShowBatteryCheck.IsChecked = _settings.ShowBatteryIndicator;

        LanguageCombo.Items.Clear();
        var availableLanguages = Loc.GetAvailableLanguages();
        int selectedIndex = 0;
        for (int i = 0; i < availableLanguages.Count; i++)
        {
            var lang = availableLanguages[i];
            LanguageCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = lang.Name, Tag = lang.Code });
            if (lang.Code == _settings.Language)
            {
                selectedIndex = i;
            }
        }
        LanguageCombo.SelectedIndex = selectedIndex;

        PopulateWidgetCombo();

        YouTubeApiCheck.IsChecked = _settings.EnableYouTubeApi;
        YouTubeApiKeyPasswordBox.Password = _settings.YouTubeApiKey;
        YouTubeApiKeyTextBox.Text = _settings.YouTubeApiKey;
        YouTubeApiKeyRow.Visibility = _settings.EnableYouTubeApi ? Visibility.Visible : Visibility.Collapsed;
        UpdateYouTubeApiKeyStatus();

        EnableWeatherCheck.IsChecked = _settings.EnableWeather;
        ManualCityTextBox.Text = _settings.ManualCity;
        UpdateWeatherDependentControls(_settings.EnableWeather);

        _isLoadingSettings = false;
        ApplyLocalization();
    }

    private static string GetAppVersion()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return v != null ? FormatVersion(v) : "1.7.0";
    }

    private static string FormatVersion(Version v)
    {
        return v.Revision > 0
            ? $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}"
            : $"{v.Major}.{v.Minor}.{v.Build}";
    }
    private void ApplyTooltips()
    {
        // Header tooltips
        TooltipHelper.SetLocalizedTooltip(SocialWebsite, "tooltip.website");
        TooltipHelper.SetLocalizedTooltip(SocialGitHub, "tooltip.github");
        TooltipHelper.SetLocalizedTooltip(SocialFacebook, "tooltip.facebook");
        TooltipHelper.SetLocalizedTooltip(SocialDiscord, "tooltip.discord");

        // Navigation tooltips
        TooltipHelper.SetLocalizedTooltip(NavAppearance, "tooltip.nav.appearance");
        TooltipHelper.SetLocalizedTooltip(NavSkins, "tooltip.nav.skins");
        TooltipHelper.SetLocalizedTooltip(NavBehavior, "tooltip.nav.behavior");
        TooltipHelper.SetLocalizedTooltip(NavDevices, "tooltip.nav.devices");
        TooltipHelper.SetLocalizedTooltip(NavSystem, "tooltip.nav.system");
        TooltipHelper.SetLocalizedTooltip(NavAdvanced, "tooltip.nav.advanced");
        TooltipHelper.SetLocalizedTooltip(NavPerformance, "tooltip.nav.performance");
        TooltipHelper.SetLocalizedTooltip(NavUpdates, "tooltip.nav.updates");
        TooltipHelper.SetLocalizedTooltip(NavDonating, "tooltip.nav.donating");

        // Button tooltips
        TooltipHelper.SetLocalizedTooltip(CheckUpdateButton, "tooltip.checkUpdates");
        TooltipHelper.SetLocalizedTooltip(ResetButton, "tooltip.resetSettings");
        TooltipHelper.SetLocalizedTooltip(ApplyButton, "tooltip.applySettings");
        TooltipHelper.SetLocalizedTooltip(SaveButton, "tooltip.saveSettings");

        // Checkbox tooltips
        TooltipHelper.SetLocalizedTooltip(AutoStartCheck, "tooltip.autoStart");
        TooltipHelper.SetLocalizedTooltip(EnableBlurEffectsCheck, "tooltip.blurEffects");
        TooltipHelper.SetLocalizedTooltip(MediaArtBackgroundCheck, "tooltip.mediaArtBackground");
        TooltipHelper.SetLocalizedTooltip(EnableSubjectBlurCheck, "tooltip.subjectBlur");
        TooltipHelper.SetLocalizedTooltip(EnableSmartCropCheck, "tooltip.smartCrop");
        TooltipHelper.SetLocalizedTooltip(DynamicIslandModeCheck, "tooltip.dynamicIsland");
        TooltipHelper.SetLocalizedTooltip(HoverExpandCheck, "tooltip.hoverExpand");
        TooltipHelper.SetLocalizedTooltip(DisableMouseLeaveAutoCloseCheck, "tooltip.disableAutoClose");
        TooltipHelper.SetLocalizedTooltip(ReopenLastViewCheck, "tooltip.reopenLastView");
        TooltipHelper.SetLocalizedTooltip(IdleAutoHideCheck, "tooltip.idleAutoHide");
        TooltipHelper.SetLocalizedTooltip(EnableSpotifyLyricsCheck, "tooltip.spotifyLyrics");
        TooltipHelper.SetLocalizedTooltip(EnableYouTubeSubtitlesCheck, "tooltip.youtubeSubtitles");
        TooltipHelper.SetLocalizedTooltip(YouTubeApiCheck, "tooltip.youtubeApi");
        TooltipHelper.SetLocalizedTooltip(HideOnExclusiveFullscreenCheck, "tooltip.hideExclusiveFs");
        TooltipHelper.SetLocalizedTooltip(HideOnWindowedFullscreenCheck, "tooltip.hideWindowedFs");
        TooltipHelper.SetLocalizedTooltip(MusicNotifyCheck, "tooltip.musicNotify");
        TooltipHelper.SetLocalizedTooltip(SystemNotifyCheck, "tooltip.systemNotify");
        TooltipHelper.SetLocalizedTooltip(ShowBatteryCheck, "tooltip.showBattery");
        TooltipHelper.SetLocalizedTooltip(ShelfUnlockCheck, "tooltip.shelfUnlock");
        TooltipHelper.SetLocalizedTooltip(CopyShelfClipboardCheck, "tooltip.copyShelfClipboard");
        TooltipHelper.SetLocalizedTooltip(HelloGreetingCheck, "tooltip.helloGreeting");

        // Combo box tooltips
        TooltipHelper.SetLocalizedTooltip(WidgetCombo, "tooltip.widgetCombo");
        TooltipHelper.SetLocalizedTooltip(MonitorCombo, "tooltip.monitorCombo");
        TooltipHelper.SetLocalizedTooltip(CameraCombo, "tooltip.cameraCombo");
        TooltipHelper.SetLocalizedTooltip(VisualizerAudioCombo, "tooltip.visualizerAudioCombo");
        TooltipHelper.SetLocalizedTooltip(LanguageCombo, "tooltip.languageCombo");
        TooltipHelper.SetLocalizedTooltip(SkinCombo, "tooltip.skinCombo");
        TooltipHelper.SetLocalizedTooltip(GlassPresetCombo, "tooltip.glassPresetCombo");
    }

    private void ApplyLocalization()
    {
        ApplyTooltips();
        SettingsTitleText.Text = Loc.Get("settings.title");
        SettingsSubtitleText.Text = Loc.Get("settings.subtitle");
        SearchPlaceholder.Text = Loc.Get("settings.searchPlaceholder");

        AppearanceHeader.Text = Loc.Get("settings.appearance");
        BehaviorHeader.Text = Loc.Get("settings.behavior");
        UpdatesHeader.Text = Loc.Get("settings.updates");
        DonatingHeader.Text = Loc.Get("settings.donating");
        PerformanceHeader.Text = Loc.Get("settings.performance");
        DisplayHeader.Text = Loc.Get("settings.display");
        SystemHeader.Text = Loc.Get("settings.system");
        SearchingHeader.Text = Loc.Get("settings.searching");
        SearchingEmptyText.Text = Loc.Get("settings.search.noResults");

        NavSearchingText.Text = Loc.Get("settings.searching");
        NavAppearanceText.Text = Loc.Get("settings.nav.appearance");
        NavSkinsText.Text = Loc.Get("settings.nav.skins");
        NavBehaviorText.Text = Loc.Get("settings.nav.behavior");
        NavDevicesText.Text = Loc.Get("settings.nav.devices");
        NavSystemText.Text = Loc.Get("settings.nav.system");
        NavAdvancedText.Text = Loc.Get("settings.nav.advanced");
        NavPerformanceText.Text = Loc.Get("settings.nav.performance");
        NavDonatingText.Text = Loc.Get("settings.nav.donating");
        NavUpdatesText.Text = Loc.Get("settings.nav.updates");

        ExpandedWidgetLabel.Text = Loc.Get("settings.expandedWidget");
        ExpandedWidgetHint.Text = Loc.Get("settings.expandedWidget.hint");
        RepopulateWidgetComboPreservingSelection();
        WidthLabel.Text = Loc.Get("settings.width");
        WidthSlider.Label = Loc.Get("settings.width");
        WidthSlider.Description = Loc.Get("settings.width.hint");
        DynamicIslandWidthLabel.Text = Loc.Get("settings.dynamicIslandWidth");
        DynamicIslandWidthSlider.Label = Loc.Get("settings.dynamicIslandWidth");
        DynamicIslandWidthSlider.Description = Loc.Get("settings.dynamicIslandWidth.hint");
        DynamicIslandHeightLabel.Text = Loc.Get("settings.dynamicIslandHeight");
        DynamicIslandHeightSlider.Label = Loc.Get("settings.dynamicIslandHeight");
        DynamicIslandHeightSlider.Description = Loc.Get("settings.dynamicIslandHeight.hint");
        HeightLabel.Text = Loc.Get("settings.height");
        HeightSlider.Label = Loc.Get("settings.height");
        HeightSlider.Description = Loc.Get("settings.height.hint");
        RadiusLabel.Text = Loc.Get("settings.cornerRadius");
        RadiusSlider.Label = Loc.Get("settings.cornerRadius");
        RadiusSlider.Description = Loc.Get("settings.cornerRadius.hint");
        OpacityLabel.Text = Loc.Get("settings.opacity");
        OpacitySlider.Label = Loc.Get("settings.opacity");
        OpacitySlider.Description = Loc.Get("settings.opacity.hint");
        BlurLabel.Text = Loc.Get("settings.blurBrightness");
        BlurBrightnessSlider.Label = Loc.Get("settings.blurBrightness");
        BlurBrightnessSlider.Description = Loc.Get("settings.blurBrightness.hint");
        DarkOverlayLabel.Text = Loc.Get("settings.lyricsDarkOverlay");
        BlurDarkOverlaySlider.Label = Loc.Get("settings.lyricsDarkOverlay");
        BlurDarkOverlaySlider.Description = Loc.Get("settings.lyricsDarkOverlay.hint");
        EnableSpotifyLyricsCheck.Content = Loc.Get("settings.enableSpotifyLyrics");
        EnableSpotifyLyricsHint.Text = Loc.Get("settings.enableSpotifyLyrics.hint");
        EnableYouTubeSubtitlesLabel.Text = Loc.Get("settings.enableYouTubeSubtitles");
        if (YouTubeSubtitlesAlphaBadge != null) YouTubeSubtitlesAlphaBadge.Text = Loc.Get("settings.badge.alpha");
        EnableYouTubeSubtitlesHint.Text = Loc.Get("settings.enableYouTubeSubtitles.hint");

        SubtitlePriorityLabel.Text = Loc.Get("settings.subtitlePriority");
        SubtitlePriorityHint.Text = Loc.Get("settings.subtitlePriority.hint");
        LoadSubtitlePriority();

        DynamicIslandModeCheck.Content = Loc.Get("settings.dynamicIslandMode");
        DynamicIslandModeHint.Text = Loc.Get("settings.dynamicIslandMode.hint");

        HoverExpandCheck.Content = Loc.Get("settings.hoverExpand");
        HoverExpandHint.Text = Loc.Get("settings.hoverExpand.hint");
        ExpandDelayLabel.Text = Loc.Get("settings.expandDelay");
        HoverDelaySlider.Label = Loc.Get("settings.expandDelay");
        HoverDelaySlider.Description = Loc.Get("settings.expandDelay.hint");
        DisableMouseLeaveAutoCloseCheck.Content = Loc.Get("settings.disableAutoClose");
        DisableMouseLeaveAutoCloseHint.Text = Loc.Get("settings.disableAutoClose.hint");
        ReopenLastViewCheck.Content = Loc.Get("settings.reopenLastView");
        ReopenLastViewHint.Text = Loc.Get("settings.reopenLastView.hint");
        IdleAutoHideCheck.Content = Loc.Get("settings.idleAutoHide");
        IdleAutoHideHint.Text = Loc.Get("settings.idleAutoHide.hint");
        IdleAutoHideKeywords.Text = Loc.Get("settings.idleAutoHide.keywords");
        IdleAutoHideDelaySlider.Label = Loc.Get("settings.idleAutoHideDelay");
        IdleAutoHideDelaySlider.Description = Loc.Get("settings.idleAutoHideDelay.hint");
        IdleAutoHideDelayKeywords.Text = Loc.Get("settings.idleAutoHideDelay.keywords");

        CheckUpdateButton.Content = Loc.Get("settings.checkUpdate");
        UpdateStatusText.Text = Loc.Get("settings.upToDate");
        CurrentVersionText.Text = Loc.Get("settings.currentVersion", GetAppVersion());
        ViewChangelogButton.Content = Loc.Get("settings.btn.changelog");
        ReportBugLabel.Text = Loc.Get("settings.reportBug");
        ReportBugHint.Text = Loc.Get("settings.reportBug.hint");
        RequestFeatureLabel.Text = Loc.Get("settings.requestFeature");
        RequestFeatureHint.Text = Loc.Get("settings.requestFeature.hint");
        ClearCacheLabel.Text = Loc.Get("settings.clearCache");
        ClearCacheHint.Text = Loc.Get("settings.clearCache.hint");

        MonitorLabel.Text = Loc.Get("settings.activeMonitor");
        MonitorHint.Text = Loc.Get("settings.activeMonitor.hint");
        int monitorIdx = MonitorCombo.SelectedIndex;
        var monitors = NotchManager.GetMonitorNames();
        MonitorCombo.ItemsSource = monitors;
        MonitorCombo.SelectedIndex = Math.Min(monitorIdx < 0 ? _settings.MonitorIndex : monitorIdx, monitors.Length - 1);

        CameraLabel.Text = Loc.Get("settings.camera");
        CameraHint.Text = Loc.Get("settings.camera.hint");
        VisualizerAudioLabel.Text = Loc.Get("settings.visualizerAudio");
        VisualizerAudioHint.Text = Loc.Get("settings.visualizerAudio.hint");
        if (IsLoaded)
        {
            Dispatcher.BeginInvoke(new Action(() => LoadVisualizerAudioDevices().SafeFireAndForget("SETTINGS-VIS-AUDIO")), DispatcherPriority.Background);
        }
        else
        {
            SetVisualizerAudioDevicePlaceholder();
        }

        ResetButton.Content = Loc.Get("settings.btn.reset");
        ApplyButton.Content = Loc.Get("settings.btn.apply");
        SaveButton.Content = Loc.Get("settings.btn.save");

        HelloGreetingCheck.Content = Loc.Get("settings.helloGreeting");
        HelloGreetingHint.Text = Loc.Get("settings.helloGreeting.hint");
        AutoStartCheck.Content = Loc.Get("settings.autoStart");
        AutoStartHint.Text = Loc.Get("settings.autoStart.hint");
        HideOnExclusiveFullscreenCheck.Content = Loc.Get("settings.hideExclusiveFs");
        HideOnExclusiveFullscreenHint.Text = Loc.Get("settings.hideExclusiveFs.hint");
        HideOnWindowedFullscreenCheck.Content = Loc.Get("settings.hideWindowedFs");
        HideOnWindowedFullscreenHint.Text = Loc.Get("settings.hideWindowedFs.hint");
        MusicNotifyCheck.Content = Loc.Get("settings.musicNotify");
        MusicNotifyHint.Text = Loc.Get("settings.musicNotify.hint");
        SystemNotifyCheck.Content = Loc.Get("settings.systemNotify");
        SystemNotifyHint.Text = Loc.Get("settings.systemNotify.hint");
        ShelfUnlockCheck.Content = Loc.Get("settings.shelfUnlock");
        ShelfUnlockHint.Text = Loc.Get("settings.shelfUnlock.hint");
        CopyShelfClipboardCheck.Content = Loc.Get("settings.copyShelfClipboard");
        CopyShelfClipboardHint.Text = Loc.Get("settings.copyShelfClipboard.hint");
        ShowBatteryCheck.Content = Loc.Get("settings.showBattery");
        ShowBatteryHint.Text = Loc.Get("settings.showBattery.hint");
        LanguageLabel.Text = Loc.Get("settings.language");
        LanguageHint.Text = Loc.Get("settings.language.hint");
        AdvancedHeader.Text = Loc.Get("settings.advanced");
        YouTubeApiCheck.Content = Loc.Get("settings.youtubeApi");
        YouTubeApiHint.Text = Loc.Get("settings.youtubeApi.hint");
        YouTubeApiKeyLabel.Text = Loc.Get("settings.youtubeApiKey");
        YouTubeApiKeyHint.Text = Loc.Get("settings.youtubeApiKey.hint");

        AnimationFpsLabel.Text = Loc.Get("settings.animationFps");
        AnimationFpsSlider.Label = Loc.Get("settings.animationFps");
        AnimationFpsSlider.Description = Loc.Get("settings.animationFps.hint");
        EnableBlurEffectsCheck.Content = Loc.Get("settings.enableBlurEffects");
        EnableBlurEffectsHint.Text = Loc.Get("settings.enableBlurEffects.hint");
        MediaArtBackgroundCheck.Content = Loc.Get("settings.mediaArtBackground");
        MediaArtBackgroundHint.Text = Loc.Get("settings.mediaArtBackground.hint");
        ApplyLiquidGlassLocalization();
        EnableSubjectBlurCheck.Content = Loc.Get("settings.enableSubjectBlur");
        EnableSubjectBlurHint.Text = Loc.Get("settings.enableSubjectBlur.hint");
        EnableSmartCropCheck.Content = Loc.Get("settings.enableSmartCrop");
        EnableSmartCropHint.Text = Loc.Get("settings.enableSmartCrop.hint");

        DonatingTitle.Text = Loc.Get("settings.donating.title");
        DonatingDescription.Text = Loc.Get("settings.donating.description");
        DonatePaypalButton.Content = Loc.Get("settings.donating.paypal");
        DonatingBankTitle.Text = Loc.Get("settings.donating.bank");
        DonatingBankHint.Text = Loc.Get("settings.donating.bank.hint");
    }

    #region Slider Value Changed Handlers

    private void WidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (WidthValue != null)
            WidthValue.Text = ((int)e.NewValue).ToString();
        PushLivePreview();
    }

    private void DynamicIslandWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DynamicIslandWidthValue != null)
            DynamicIslandWidthValue.Text = ((int)e.NewValue).ToString();
        PushLivePreview();
    }

    private void DynamicIslandHeightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DynamicIslandHeightValue != null)
            DynamicIslandHeightValue.Text = ((int)e.NewValue).ToString();
        PushLivePreview();
    }

    private void HeightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (HeightValue != null)
            HeightValue.Text = ((int)e.NewValue).ToString();
        PushLivePreview();
    }

    private void RadiusSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RadiusValue != null)
            RadiusValue.Text = ((int)e.NewValue).ToString();
        PushLivePreview();
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityValue != null)
            OpacityValue.Text = ((int)e.NewValue).ToString();
        PushLivePreview();
    }

    private void BlurBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BlurBrightnessValue != null)
            BlurBrightnessValue.Text = ((int)e.NewValue).ToString();
        PushLivePreview();
    }

    private void BlurDarkOverlaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BlurDarkOverlayValue != null)
            BlurDarkOverlayValue.Text = ((int)e.NewValue).ToString();
        PushLivePreview();
    }

    private void AnimationFpsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AnimationFpsValue != null)
            AnimationFpsValue.Text = ((int)Math.Round(e.NewValue)).ToString();
        PushLivePreview();
    }

    private void EnableSpotifyLyricsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        bool enabled = EnableSpotifyLyricsCheck.IsChecked ?? true;
        UpdateLyricsDependentControls(enabled);
        PushLivePreview();
    }

    private void EnableYouTubeSubtitlesCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        UpdateYouTubeSubtitlesDependentControls(EnableYouTubeSubtitlesCheck.IsChecked ?? true);
        PushLivePreview();
    }

    #region Subtitle Priority

    private class SubtitlePriorityItem
    {
        public string Key { get; set; } = "";
        public string DisplayName { get; set; } = "";
    }

    private readonly System.Collections.ObjectModel.ObservableCollection<SubtitlePriorityItem> _subtitleItems = new();
    private Point _subtitleDragStart;
    private bool _subtitleIsDragging;

    private void LoadSubtitlePriority()
    {
        _subtitleItems.Clear();

        var keys = (_settings.SubtitlePriority ?? "native,english,auto")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var allKeys = new[] { "native", "english", "auto" };
        var ordered = keys.Where(k => allKeys.Contains(k)).ToList();
        foreach (var k in allKeys)
        {
            if (!ordered.Contains(k)) ordered.Add(k);
        }

        foreach (var key in ordered)
        {
            _subtitleItems.Add(new SubtitlePriorityItem
            {
                Key = key,
                DisplayName = GetSubtitleModeName(key)
            });
        }

        SubtitlePriorityItems.ItemsSource = _subtitleItems;
    }

    private static string GetSubtitleModeName(string key) => key switch
    {
        "native" => Loc.Get("settings.subtitleMode.native"),
        "english" => Loc.Get("settings.subtitleMode.english"),
        "auto" => Loc.Get("settings.subtitleMode.auto"),
        _ => key
    };

    private void SubtitlePriorityItem_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _subtitleDragStart = e.GetPosition(null);
        _subtitleIsDragging = false;
        e.Handled = true;
    }

    private void SubtitlePriorityItem_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        if (_subtitleIsDragging) return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _subtitleDragStart.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(pos.Y - _subtitleDragStart.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            _subtitleIsDragging = true;

            if (sender is FrameworkElement fe && fe.DataContext is SubtitlePriorityItem item)
            {
                var data = new DataObject("SubtitlePriorityItem", item);
                DragDrop.DoDragDrop(fe, data, DragDropEffects.Move);
            }

            _subtitleIsDragging = false;
        }
    }

    private void SubtitlePriorityItem_GiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        e.UseDefaultCursors = true;
        e.Handled = true;
    }

    private void SubtitlePriority_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("SubtitlePriorityItem"))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void SubtitlePriority_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("SubtitlePriorityItem")) return;

        var draggedItem = e.Data.GetData("SubtitlePriorityItem") as SubtitlePriorityItem;
        if (draggedItem == null) return;

        var dropPos = e.GetPosition(SubtitlePriorityItems);
        int newIndex = GetSubtitleDropIndex(dropPos);

        int oldIndex = _subtitleItems.IndexOf(draggedItem);
        if (oldIndex < 0 || oldIndex == newIndex) return;

        var positions = new Dictionary<SubtitlePriorityItem, double>();
        for (int i = 0; i < _subtitleItems.Count; i++)
        {
            var container = SubtitlePriorityItems.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
            if (container != null)
                positions[_subtitleItems[i]] = container.TranslatePoint(new Point(0, 0), SubtitlePriorityItems).Y;
        }

        _subtitleItems.Move(oldIndex, newIndex);

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            for (int i = 0; i < _subtitleItems.Count; i++)
            {
                var container = SubtitlePriorityItems.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
                if (container == null) continue;

                var item = _subtitleItems[i];
                double newY = container.TranslatePoint(new Point(0, 0), SubtitlePriorityItems).Y;

                if (positions.TryGetValue(item, out double oldY) && Math.Abs(oldY - newY) > 1)
                {
                    var translate = container.RenderTransform as TranslateTransform;
                    if (translate == null)
                    {
                        translate = new TranslateTransform();
                        container.RenderTransform = translate;
                    }

                    translate.Y = oldY - newY;
                    var anim = new DoubleAnimation(oldY - newY, 0, TimeSpan.FromMilliseconds(250))
                    {
                        EasingFunction = ease
                    };
                    System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(anim, VNotch.Services.AnimationConfig.TargetFps);
                    translate.BeginAnimation(TranslateTransform.YProperty, anim);
                }

                if (item == draggedItem)
                {
                    var scale = container.RenderTransform as ScaleTransform;
                    if (container.RenderTransform is TranslateTransform)
                    {
                        var group = new TransformGroup();
                        group.Children.Add(container.RenderTransform);
                        var sc = new ScaleTransform(1, 1);
                        group.Children.Add(sc);
                        container.RenderTransformOrigin = new Point(0.5, 0.5);
                        container.RenderTransform = group;

                        var scaleAnim = new DoubleAnimation(1.03, 1.0, TimeSpan.FromMilliseconds(200))
                        {
                            EasingFunction = ease
                        };
                        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(scaleAnim, VNotch.Services.AnimationConfig.TargetFps);
                        sc.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
                        sc.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
                    }
                }
            }
        });

        ApplySettingsFromUi(persist: true);
    }

    private int GetSubtitleDropIndex(Point dropPoint)
    {
        double y = 0;
        for (int i = 0; i < _subtitleItems.Count; i++)
        {
            var container = SubtitlePriorityItems.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
            if (container == null) continue;

            double itemHeight = container.ActualHeight;
            if (dropPoint.Y < y + itemHeight / 2)
                return i;
            y += itemHeight;
        }
        return _subtitleItems.Count - 1;
    }

    private string GetSubtitlePriorityString()
    {
        return string.Join(",", _subtitleItems.Select(i => i.Key));
    }

    #endregion

    private void DynamicIslandModeCheck_Changed(object sender, RoutedEventArgs e)
    {
        UpdateDynamicIslandDependentControls(DynamicIslandModeCheck.IsChecked ?? false);
        if (_isLoadingSettings) return;
        PushLivePreview();
    }

    private void UpdateDynamicIslandDependentControls(bool islandEnabled)
    {
        if (DynamicIslandWidthSlider == null || DynamicIslandHeightSlider == null) return;

        double targetOpacity = islandEnabled ? 1.0 : 0.4;
        DynamicIslandWidthSlider.IsEnabled = islandEnabled;
        DynamicIslandWidthSlider.Opacity = targetOpacity;
        DynamicIslandHeightSlider.IsEnabled = islandEnabled;
        DynamicIslandHeightSlider.Opacity = targetOpacity;
    }

    private void UpdateLyricsDependentControls(bool lyricsEnabled)
    {
        if (DarkOverlayLabel == null) return;

        double targetOpacity = lyricsEnabled ? 1.0 : 0.45;
        DarkOverlayLabel.Opacity = targetOpacity;
        if (DarkOverlayHint != null) DarkOverlayHint.Opacity = targetOpacity;
        if (BlurDarkOverlaySlider != null)
        {
            BlurDarkOverlaySlider.Opacity = targetOpacity;
            BlurDarkOverlaySlider.IsEnabled = lyricsEnabled;
        }
    }

    private void UpdateYouTubeSubtitlesDependentControls(bool subtitlesEnabled)
    {
        if (SubtitlePriorityRow == null) return;

        SubtitlePriorityRow.Opacity = subtitlesEnabled ? 1.0 : 0.45;
        SubtitlePriorityRow.IsEnabled = subtitlesEnabled;
    }

    private void PerformanceSetting_Changed(object sender, RoutedEventArgs e)
    {
        bool blurEnabled = EnableBlurEffectsCheck.IsChecked ?? true;
        UpdatePerformanceDependentControls(blurEnabled);
        if (_isLoadingSettings) return;
        PushLivePreview();
    }

    private void UpdatePerformanceDependentControls(bool blurEnabled)
    {
        if (SubjectBlurRow == null) return;

        SubjectBlurRow.Opacity = blurEnabled ? 1.0 : 0.45;
        SubjectBlurRow.IsEnabled = blurEnabled;

        double blurSliderOpacity = blurEnabled ? 1.0 : 0.45;
        if (BlurBrightnessSlider != null)
        {
            BlurBrightnessSlider.Opacity = blurSliderOpacity;
            BlurBrightnessSlider.IsEnabled = blurEnabled;
        }
    }

    private void HoverDelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (HoverDelayValue != null)
            HoverDelayValue.Text = ((int)e.NewValue).ToString();
        PushLivePreview();
    }

    private void HoverExpandCheck_Changed(object sender, RoutedEventArgs e)
    {
        bool enabled = HoverExpandCheck.IsChecked ?? false;
        HoverDelaySlider.IsEnabled = enabled;
        HoverDelaySlider.Opacity = enabled ? 1.0 : 0.4;
    }

    private void IdleAutoHideCheck_Changed(object sender, RoutedEventArgs e)
    {
        bool enabled = IdleAutoHideCheck.IsChecked ?? false;
        IdleAutoHideDelaySlider.IsEnabled = enabled;
        IdleAutoHideDelaySlider.Opacity = enabled ? 1.0 : 0.4;
    }

    private void LanguageCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings) return;
        if (LanguageCombo.SelectedItem is System.Windows.Controls.ComboBoxItem item && item.Tag is string lang)
        {
            if (lang == _settings.Language) return;

            _settings.Language = lang;
            Loc.SetLanguage(lang);
            _settingsService.Save(_settings);
            _originalSettings = _settings.Clone();
            AnimateLocalizationChange();
            SettingsChanged?.Invoke(this, _settings);
        }
    }

    private void PopulateWidgetCombo()
    {
        WidgetCombo.Items.Clear();
        WidgetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.widget.calendar"), Tag = "calendar" });
        WidgetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.widget.clock"), Tag = "clock" });
        WidgetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.widget.wordclock"), Tag = "wordclock" });
        WidgetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.widget.weather"), Tag = "weather" });
        WidgetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.widget.sysmon"), Tag = "sysmon" });
        WidgetCombo.SelectedIndex = _settings.ExpandedWidget switch
        {
            "clock" => 1,
            "wordclock" => 2,
            "weather" => 3,
            "sysmon" => 4,
            _ => 0
        };
    }

    private void RepopulateWidgetComboPreservingSelection()
    {
        if (WidgetCombo == null) return;

        bool wasLoading = _isLoadingSettings;
        _isLoadingSettings = true;
        PopulateWidgetCombo();
        _isLoadingSettings = wasLoading;
    }

    private void WidgetCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings) return;
        if (WidgetCombo.SelectedItem is System.Windows.Controls.ComboBoxItem item && item.Tag is string widget)
        {
            if (widget == _settings.ExpandedWidget) return;

            _settings.ExpandedWidget = widget;
            PushLivePreview();
        }
    }

    #region Liquid Glass skin

    // Snapshot of the user's manually-tuned glass values, preserved so the
    // "Custom Settings" preset can always restore exactly what they had.
    private Models.LiquidGlassConfig? _customGlassSnapshot;
    private bool _suppressGlassPresetChange;

    private static Models.LiquidGlassConfig FrostedGlassPreset() => new()
    {
        BlurAmount = 0.25,
        Refraction = 0.6,
        ChromaticAberration = 0.06,
        EdgeHighlight = 0.35,
        Specular = 0.20,
        Fresnel = 0.25,
        Distortion = 0.03,
        ZRadius = 0.35,
        Opacity = 1.0,
        Saturation = -0.30,
        Brightness = -0.30,
        ShadowOpacity = 0.50,
        ShadowSpread = 18,
        BevelMode = 0
    };

    private static Models.LiquidGlassConfig DarkGlassPreset() => new()
    {
        BlurAmount = 0.25,
        Refraction = 1.0,
        ChromaticAberration = 0.10,
        EdgeHighlight = 0.12,
        Specular = 0.35,
        Fresnel = 0.45,
        Distortion = 0.06,
        ZRadius = 0.45,
        Opacity = 1.0,
        Saturation = -0.50,
        Brightness = -0.30,
        ShadowOpacity = 0.80,
        ShadowSpread = 30,
        BevelMode = 1
    };

    private void EnsureGlassPresetItems()
    {
        if (GlassPresetCombo.Items.Count > 0) return;
        GlassPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.glass.preset.custom"), Tag = "custom" });
        GlassPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.glass.preset.frosted"), Tag = "frosted" });
        GlassPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.glass.preset.dark"), Tag = "dark" });
    }

    private Models.LiquidGlassConfig ReadGlassConfigFromSliders() => new()
    {
        BlurAmount = GlassBlurSlider.Value / 100.0,
        Refraction = GlassRefractionSlider.Value / 100.0,
        ChromaticAberration = GlassChromSlider.Value / 100.0,
        EdgeHighlight = GlassEdgeHighlightSlider.Value / 100.0,
        Specular = GlassSpecularSlider.Value / 100.0,
        Fresnel = GlassFresnelSlider.Value / 100.0,
        Distortion = GlassDistortionSlider.Value / 100.0,
        ZRadius = GlassZRadiusSlider.Value / 100.0,
        Opacity = GlassOpacitySlider.Value / 100.0,
        Saturation = GlassSaturationSlider.Value / 100.0,
        Brightness = GlassBrightnessSlider.Value / 100.0,
        ShadowOpacity = GlassShadowOpacitySlider.Value / 100.0,
        ShadowSpread = (int)Math.Round(GlassShadowSpreadSlider.Value),
        BevelMode = (int)Math.Round(GlassBevelModeSlider.Value)
    };

    private void ApplyGlassConfigToSliders(Models.LiquidGlassConfig c)
    {
        bool prev = _isLoadingSettings;
        _isLoadingSettings = true;
        try
        {
            GlassBlurSlider.Value = Math.Round(c.BlurAmount * 100);
            GlassRefractionSlider.Value = Math.Round(c.Refraction * 100);
            GlassChromSlider.Value = Math.Round(c.ChromaticAberration * 100);
            GlassEdgeHighlightSlider.Value = Math.Round(c.EdgeHighlight * 100);
            GlassSpecularSlider.Value = Math.Round(c.Specular * 100);
            GlassFresnelSlider.Value = Math.Round(c.Fresnel * 100);
            GlassDistortionSlider.Value = Math.Round(c.Distortion * 100);
            GlassZRadiusSlider.Value = Math.Round(c.ZRadius * 100);
            GlassOpacitySlider.Value = Math.Round(c.Opacity * 100);
            GlassSaturationSlider.Value = Math.Round(c.Saturation * 100);
            GlassBrightnessSlider.Value = Math.Round(c.Brightness * 100);
            GlassShadowOpacitySlider.Value = Math.Round(c.ShadowOpacity * 100);
            GlassShadowSpreadSlider.Value = c.ShadowSpread;
            GlassBevelModeSlider.Value = c.BevelMode;
        }
        finally
        {
            _isLoadingSettings = prev;
        }
    }

    private void SelectGlassPreset(string tag)
    {
        EnsureGlassPresetItems();
        for (int i = 0; i < GlassPresetCombo.Items.Count; i++)
        {
            if (GlassPresetCombo.Items[i] is System.Windows.Controls.ComboBoxItem item &&
                (item.Tag as string) == tag)
            {
                _suppressGlassPresetChange = true;
                GlassPresetCombo.SelectedIndex = i;
                _suppressGlassPresetChange = false;
                return;
            }
        }
    }

    private void LoadLiquidGlassUi()
    {
        bool prev = _isLoadingSettings;
        _isLoadingSettings = true;
        try
        {
            if (SkinCombo.Items.Count == 0)
            {
                PopulateSkinItems();
            }

            EnsureGlassPresetItems();

            bool glass = string.Equals(_settings.NotchStyle, "liquidglass", StringComparison.OrdinalIgnoreCase);
            SkinCombo.SelectedIndex = glass ? 1 : 0;

            var c = _settings.LiquidGlass ?? new Models.LiquidGlassConfig();
            ApplyGlassConfigToSliders(c);
            if (GpuRefractionCheck != null)
                GpuRefractionCheck.IsChecked = c.UseGpuRefraction;

            // The user's tuned values live in their own persistent slot. If it's
            // missing (first run / upgrade), seed it from whatever is currently
            // active so the very first values are never lost.
            _settings.LiquidGlassCustom ??= c.Clone();
            _customGlassSnapshot = _settings.LiquidGlassCustom.Clone();

            string preset = string.IsNullOrWhiteSpace(_settings.LiquidGlassPreset) ? "custom" : _settings.LiquidGlassPreset;
            SelectGlassPreset(preset);

            LiquidGlassConfigPanel.Visibility = glass ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _isLoadingSettings = prev;
        }
    }

    private void SaveLiquidGlassUi()
    {
        _settings.NotchStyle = (SkinCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "default";

        var c = _settings.LiquidGlass ??= new Models.LiquidGlassConfig();
        var ui = ReadGlassConfigFromSliders();
        c.BlurAmount = ui.BlurAmount;
        c.Refraction = ui.Refraction;
        c.ChromaticAberration = ui.ChromaticAberration;
        c.EdgeHighlight = ui.EdgeHighlight;
        c.Specular = ui.Specular;
        c.Fresnel = ui.Fresnel;
        c.Distortion = ui.Distortion;
        c.ZRadius = ui.ZRadius;
        c.Opacity = ui.Opacity;
        c.Saturation = ui.Saturation;
        c.Brightness = ui.Brightness;
        c.ShadowOpacity = ui.ShadowOpacity;
        c.ShadowSpread = ui.ShadowSpread;
        c.BevelMode = ui.BevelMode;
        c.UseGpuRefraction = GpuRefractionCheck?.IsChecked ?? false;

        // Persist which preset is active and the user's custom slot. A built-in
        // preset being active must NOT overwrite the custom slot.
        _settings.LiquidGlassPreset = (GlassPresetCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "custom";
        if (_customGlassSnapshot != null)
            _settings.LiquidGlassCustom = _customGlassSnapshot.Clone();
    }

    private void GlassPresetCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings || _suppressGlassPresetChange) return;
        if (GlassPresetCombo.SelectedItem is not System.Windows.Controls.ComboBoxItem item) return;

        switch (item.Tag as string)
        {
            case "frosted":
                ApplyGlassConfigToSliders(FrostedGlassPreset());
                break;
            case "dark":
                ApplyGlassConfigToSliders(DarkGlassPreset());
                break;
            case "custom":
            default:
                if (_customGlassSnapshot != null)
                    ApplyGlassConfigToSliders(_customGlassSnapshot);
                break;
        }

        PushLivePreview();
    }

    private void SkinCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (LiquidGlassConfigPanel != null && SkinCombo.SelectedItem is System.Windows.Controls.ComboBoxItem item)
        {
            bool glass = (item.Tag as string) == "liquidglass";
            LiquidGlassConfigPanel.Visibility = glass ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_isLoadingSettings) return;
        PushLivePreview();
    }

    // Populates the skin selector. Plain string items so the selection box renders
    // the name directly (a custom ItemTemplate isn't applied to the selection box
    // by this combo's template). The "alpha" badge lives next to the section title.
    private void PopulateSkinItems()
    {
        SkinCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.skin.default"), Tag = "default" });
        SkinCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.skin.liquidglass"), Tag = "liquidglass" });
    }

    private void GlassConfigSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoadingSettings) return;

        // A manual slider tweak means the values no longer match a named preset —
        // capture them as the custom snapshot and reflect that in the dropdown.
        _customGlassSnapshot = ReadGlassConfigFromSliders();
        SelectGlassPreset("custom");

        PushLivePreview();
    }

    private void GlassConfigCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        PushLivePreview();
    }

    private void GpuRefractionCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        PushLivePreview();
    }

    private void ApplyLiquidGlassLocalization()
    {
        if (SkinLabel == null) return;

        if (SkinHeader != null) SkinHeader.Text = Loc.Get("settings.skins");
        SkinLabel.Text = Loc.Get("settings.skin");
        if (SkinAlphaBadge != null) SkinAlphaBadge.Text = Loc.Get("settings.badge.alpha");
        SkinHint.Text = Loc.Get("settings.skin.hint");

        int idx = SkinCombo.SelectedIndex;
        bool prev = _isLoadingSettings;
        _isLoadingSettings = true;
        SkinCombo.Items.Clear();
        PopulateSkinItems();
        SkinCombo.SelectedIndex = idx < 0 ? 0 : idx;
        _isLoadingSettings = prev;

        if (GlassPresetLabel != null) GlassPresetLabel.Text = Loc.Get("settings.glass.preset");
        if (GlassAdvancedWarning != null) GlassAdvancedWarning.Text = Loc.Get("settings.glass.advancedWarning");
        int presetIdx = GlassPresetCombo.SelectedIndex;
        _suppressGlassPresetChange = true;
        GlassPresetCombo.Items.Clear();
        GlassPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.glass.preset.custom"), Tag = "custom" });
        GlassPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.glass.preset.frosted"), Tag = "frosted" });
        GlassPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.glass.preset.dark"), Tag = "dark" });
        GlassPresetCombo.SelectedIndex = presetIdx < 0 ? 0 : presetIdx;
        _suppressGlassPresetChange = false;

        GlassBlurSlider.Label = Loc.Get("settings.glass.blur");
        GlassRefractionSlider.Label = Loc.Get("settings.glass.refraction");
        GlassChromSlider.Label = Loc.Get("settings.glass.chrom");
        GlassEdgeHighlightSlider.Label = Loc.Get("settings.glass.edgeHighlight");
        GlassSpecularSlider.Label = Loc.Get("settings.glass.specular");
        GlassFresnelSlider.Label = Loc.Get("settings.glass.fresnel");
        GlassDistortionSlider.Label = Loc.Get("settings.glass.distortion");
        GlassZRadiusSlider.Label = Loc.Get("settings.glass.zRadius");
        GlassOpacitySlider.Label = Loc.Get("settings.glass.opacity");
        GlassSaturationSlider.Label = Loc.Get("settings.glass.saturation");
        GlassBrightnessSlider.Label = Loc.Get("settings.glass.brightness");
        GlassShadowOpacitySlider.Label = Loc.Get("settings.glass.shadowOpacity");
        GlassShadowSpreadSlider.Label = Loc.Get("settings.glass.shadowSpread");
        GlassBevelModeSlider.Label = Loc.Get("settings.glass.bevelMode");
    }

    #endregion

    private void EnableWeatherCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        bool enabled = EnableWeatherCheck.IsChecked ?? false;
        UpdateWeatherDependentControls(enabled);
        PushLivePreview();
    }

    private void UpdateWeatherDependentControls(bool enabled)
    {
        double targetOpacity = enabled ? 1.0 : 0.45;
        ManualCityLabel.Opacity = targetOpacity;
        ManualCityHint.Opacity = targetOpacity;
        ManualCityTextBox.Opacity = targetOpacity;
        ManualCityTextBox.IsEnabled = enabled;
    }

    private void ManualCityTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_isLoadingSettings) return;
        PushLivePreview();
    }

    private void YouTubeApiCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        bool enabled = YouTubeApiCheck.IsChecked ?? false;
        YouTubeApiKeyRow.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void YouTubeApiKeyPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        if (YouTubeApiKeyTextBox.Visibility == Visibility.Collapsed)
            YouTubeApiKeyTextBox.Text = YouTubeApiKeyPasswordBox.Password;
        UpdateYouTubeApiKeyStatus();
    }

    private void YouTubeApiKeyTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_isLoadingSettings) return;
        if (YouTubeApiKeyTextBox.Visibility == Visibility.Visible)
            YouTubeApiKeyPasswordBox.Password = YouTubeApiKeyTextBox.Text;
        UpdateYouTubeApiKeyStatus();
    }

    private bool _isKeyVisible = false;

    private void ToggleKeyVisibility_Click(object sender, RoutedEventArgs e)
    {
        _isKeyVisible = !_isKeyVisible;

        var duration = TimeSpan.FromMilliseconds(200);
        var easeOut = new QuadraticEase { EasingMode = EasingMode.EaseOut };

        if (_isKeyVisible)
        {
            YouTubeApiKeyTextBox.Text = YouTubeApiKeyPasswordBox.Password;
            YouTubeApiKeyPasswordBox.Visibility = Visibility.Collapsed;
            YouTubeApiKeyTextBox.Visibility = Visibility.Visible;

            var fadeOutOpen = new DoubleAnimation(1, 0, duration) { EasingFunction = easeOut };
            var fadeInClosed = new DoubleAnimation(0, 1, duration) { EasingFunction = easeOut, BeginTime = TimeSpan.FromMilliseconds(100) };
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeOutOpen, VNotch.Services.AnimationConfig.TargetFps);
            EyeOpenIcon.BeginAnimation(OpacityProperty, fadeOutOpen);
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeInClosed, VNotch.Services.AnimationConfig.TargetFps);
            EyeClosedIcon.BeginAnimation(OpacityProperty, fadeInClosed);
        }
        else
        {
            YouTubeApiKeyPasswordBox.Password = YouTubeApiKeyTextBox.Text;
            YouTubeApiKeyTextBox.Visibility = Visibility.Collapsed;
            YouTubeApiKeyPasswordBox.Visibility = Visibility.Visible;

            var fadeOutClosed = new DoubleAnimation(1, 0, duration) { EasingFunction = easeOut };
            var fadeInOpen = new DoubleAnimation(0, 1, duration) { EasingFunction = easeOut, BeginTime = TimeSpan.FromMilliseconds(100) };
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeOutClosed, VNotch.Services.AnimationConfig.TargetFps);
            EyeClosedIcon.BeginAnimation(OpacityProperty, fadeOutClosed);
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeInOpen, VNotch.Services.AnimationConfig.TargetFps);
            EyeOpenIcon.BeginAnimation(OpacityProperty, fadeInOpen);
        }
    }

    private void UpdateYouTubeApiKeyStatus()
    {
        string key = YouTubeApiKeyPasswordBox.Password?.Trim() ?? "";
        if (string.IsNullOrEmpty(key))
        {
            YouTubeApiKeyStatus.Text = "";
            YouTubeApiKeyStatus.Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128));
        }
        else if (key.Length < 30)
        {
            YouTubeApiKeyStatus.Text = "✗ Invalid key format — too short";
            YouTubeApiKeyStatus.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
        }
        else if (!key.StartsWith("AIza", StringComparison.Ordinal))
        {
            YouTubeApiKeyStatus.Text = "✗ Invalid key format — must start with AIza";
            YouTubeApiKeyStatus.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
        }
        else if (key.Length >= 35 && key.Length <= 45)
        {
            YouTubeApiKeyStatus.Text = "✓ Key format looks valid";
            YouTubeApiKeyStatus.Foreground = new SolidColorBrush(Color.FromRgb(74, 222, 128));
        }
        else
        {
            YouTubeApiKeyStatus.Text = "⚠ Unexpected key length";
            YouTubeApiKeyStatus.Foreground = new SolidColorBrush(Color.FromRgb(234, 179, 8));
        }
    }

    private void AnimateLocalizationChange()
    {
        var easeOut = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        int fps = VNotch.Services.AnimationConfig.TargetFps;
        const double slideDist = 3.0;
        int staggerMs = 0;
        const int staggerStep = 12;

        var textUpdates = new (FrameworkElement element, Action update)[]
        {
            (SettingsTitleText, () => SettingsTitleText.Text = Loc.Get("settings.title")),
            (SettingsSubtitleText, () => SettingsSubtitleText.Text = Loc.Get("settings.subtitle")),
            (SearchPlaceholder, () => SearchPlaceholder.Text = Loc.Get("settings.searchPlaceholder")),

            (AppearanceHeader, () => AppearanceHeader.Text = Loc.Get("settings.appearance")),
            (BehaviorHeader, () => BehaviorHeader.Text = Loc.Get("settings.behavior")),
            (UpdatesHeader, () => UpdatesHeader.Text = Loc.Get("settings.updates")),
            (DonatingHeader, () => DonatingHeader.Text = Loc.Get("settings.donating")),
            (PerformanceHeader, () => PerformanceHeader.Text = Loc.Get("settings.performance")),
            (DisplayHeader, () => DisplayHeader.Text = Loc.Get("settings.display")),
            (SystemHeader, () => SystemHeader.Text = Loc.Get("settings.system")),
            (SearchingHeader, () => SearchingHeader.Text = Loc.Get("settings.searching")),
            (SearchingEmptyText, () => SearchingEmptyText.Text = Loc.Get("settings.search.noResults")),

            (NavSearchingText, () => NavSearchingText.Text = Loc.Get("settings.searching")),
            (NavAppearanceText, () => NavAppearanceText.Text = Loc.Get("settings.nav.appearance")),
            (NavSkinsText, () => NavSkinsText.Text = Loc.Get("settings.nav.skins")),
            (NavBehaviorText, () => NavBehaviorText.Text = Loc.Get("settings.nav.behavior")),
            (NavDevicesText, () => NavDevicesText.Text = Loc.Get("settings.nav.devices")),
            (NavSystemText, () => NavSystemText.Text = Loc.Get("settings.nav.system")),
            (NavAdvancedText, () => NavAdvancedText.Text = Loc.Get("settings.nav.advanced")),
            (NavPerformanceText, () => NavPerformanceText.Text = Loc.Get("settings.nav.performance")),
            (NavDonatingText, () => NavDonatingText.Text = Loc.Get("settings.nav.donating")),
            (NavUpdatesText, () => NavUpdatesText.Text = Loc.Get("settings.nav.updates")),

            (ExpandedWidgetLabel, () => { ExpandedWidgetLabel.Text = Loc.Get("settings.expandedWidget"); ExpandedWidgetHint.Text = Loc.Get("settings.expandedWidget.hint"); RepopulateWidgetComboPreservingSelection(); }),
            (WidthLabel, () => { WidthLabel.Text = Loc.Get("settings.width"); WidthSlider.Label = Loc.Get("settings.width"); WidthSlider.Description = Loc.Get("settings.width.hint"); }),
            (DynamicIslandWidthLabel, () => { DynamicIslandWidthLabel.Text = Loc.Get("settings.dynamicIslandWidth"); DynamicIslandWidthSlider.Label = Loc.Get("settings.dynamicIslandWidth"); DynamicIslandWidthSlider.Description = Loc.Get("settings.dynamicIslandWidth.hint"); }),
            (DynamicIslandHeightLabel, () => { DynamicIslandHeightLabel.Text = Loc.Get("settings.dynamicIslandHeight"); DynamicIslandHeightSlider.Label = Loc.Get("settings.dynamicIslandHeight"); DynamicIslandHeightSlider.Description = Loc.Get("settings.dynamicIslandHeight.hint"); }),
            (HeightLabel, () => { HeightLabel.Text = Loc.Get("settings.height"); HeightSlider.Label = Loc.Get("settings.height"); HeightSlider.Description = Loc.Get("settings.height.hint"); }),
            (RadiusLabel, () => { RadiusLabel.Text = Loc.Get("settings.cornerRadius"); RadiusSlider.Label = Loc.Get("settings.cornerRadius"); RadiusSlider.Description = Loc.Get("settings.cornerRadius.hint"); }),
            (OpacityLabel, () => { OpacityLabel.Text = Loc.Get("settings.opacity"); OpacitySlider.Label = Loc.Get("settings.opacity"); OpacitySlider.Description = Loc.Get("settings.opacity.hint"); }),
            (BlurLabel, () => { BlurLabel.Text = Loc.Get("settings.blurBrightness"); BlurBrightnessSlider.Label = Loc.Get("settings.blurBrightness"); BlurBrightnessSlider.Description = Loc.Get("settings.blurBrightness.hint"); }),
            (DarkOverlayLabel, () => { DarkOverlayLabel.Text = Loc.Get("settings.lyricsDarkOverlay"); BlurDarkOverlaySlider.Label = Loc.Get("settings.lyricsDarkOverlay"); BlurDarkOverlaySlider.Description = Loc.Get("settings.lyricsDarkOverlay.hint"); }),
            (EnableSpotifyLyricsHint, () => EnableSpotifyLyricsHint.Text = Loc.Get("settings.enableSpotifyLyrics.hint")),
            (EnableYouTubeSubtitlesHint, () => EnableYouTubeSubtitlesHint.Text = Loc.Get("settings.enableYouTubeSubtitles.hint")),
            (DynamicIslandModeCheck, () => DynamicIslandModeCheck.Content = Loc.Get("settings.dynamicIslandMode")),
            (DynamicIslandModeHint, () => DynamicIslandModeHint.Text = Loc.Get("settings.dynamicIslandMode.hint")),

            (HoverExpandHint, () => HoverExpandHint.Text = Loc.Get("settings.hoverExpand.hint")),
            (ExpandDelayLabel, () => { ExpandDelayLabel.Text = Loc.Get("settings.expandDelay"); HoverDelaySlider.Label = Loc.Get("settings.expandDelay"); HoverDelaySlider.Description = Loc.Get("settings.expandDelay.hint"); }),
            (DisableMouseLeaveAutoCloseHint, () => DisableMouseLeaveAutoCloseHint.Text = Loc.Get("settings.disableAutoClose.hint")),
            (ReopenLastViewHint, () => ReopenLastViewHint.Text = Loc.Get("settings.reopenLastView.hint")),
            (IdleAutoHideHint, () => IdleAutoHideHint.Text = Loc.Get("settings.idleAutoHide.hint")),
            (IdleAutoHideDelaySlider, () => { IdleAutoHideDelaySlider.Label = Loc.Get("settings.idleAutoHideDelay"); IdleAutoHideDelaySlider.Description = Loc.Get("settings.idleAutoHideDelay.hint"); }),

            (UpdateStatusText, () => UpdateStatusText.Text = Loc.Get("settings.upToDate")),
            (CurrentVersionText, () => { CurrentVersionText.Text = Loc.Get("settings.currentVersion", GetAppVersion()); ViewChangelogButton.Content = Loc.Get("settings.btn.changelog"); }),
            (ReportBugLabel, () => ReportBugLabel.Text = Loc.Get("settings.reportBug")),
            (ReportBugHint, () => ReportBugHint.Text = Loc.Get("settings.reportBug.hint")),
            (RequestFeatureLabel, () => RequestFeatureLabel.Text = Loc.Get("settings.requestFeature")),
            (RequestFeatureHint, () => RequestFeatureHint.Text = Loc.Get("settings.requestFeature.hint")),
            (ClearCacheLabel, () => ClearCacheLabel.Text = Loc.Get("settings.clearCache")),
            (ClearCacheHint, () => ClearCacheHint.Text = Loc.Get("settings.clearCache.hint")),

            (MonitorLabel, () => MonitorLabel.Text = Loc.Get("settings.activeMonitor")),
            (MonitorHint, () =>
            {
                MonitorHint.Text = Loc.Get("settings.activeMonitor.hint");
                int monitorIdx = MonitorCombo.SelectedIndex;
                var monitors = NotchManager.GetMonitorNames();
                MonitorCombo.ItemsSource = monitors;
                MonitorCombo.SelectedIndex = Math.Min(monitorIdx < 0 ? _settings.MonitorIndex : monitorIdx, monitors.Length - 1);
            }),
            (CameraLabel, () => CameraLabel.Text = Loc.Get("settings.camera")),
            (CameraHint, () =>
            {
                CameraHint.Text = Loc.Get("settings.camera.hint");
                LoadCameraDevices().SafeFireAndForget("SETTINGS-CAMERA-DEVICES");
            }),
            (VisualizerAudioLabel, () => VisualizerAudioLabel.Text = Loc.Get("settings.visualizerAudio")),
            (VisualizerAudioHint, () =>
            {
                VisualizerAudioHint.Text = Loc.Get("settings.visualizerAudio.hint");
                LoadVisualizerAudioDevices().SafeFireAndForget("SETTINGS-VIS-AUDIO");
            }),

            (AutoStartHint, () => AutoStartHint.Text = Loc.Get("settings.autoStart.hint")),
            (HelloGreetingHint, () => HelloGreetingHint.Text = Loc.Get("settings.helloGreeting.hint")),
            (HideOnExclusiveFullscreenHint, () => HideOnExclusiveFullscreenHint.Text = Loc.Get("settings.hideExclusiveFs.hint")),
            (HideOnWindowedFullscreenHint, () => HideOnWindowedFullscreenHint.Text = Loc.Get("settings.hideWindowedFs.hint")),
            (MusicNotifyHint, () => MusicNotifyHint.Text = Loc.Get("settings.musicNotify.hint")),
            (SystemNotifyHint, () => SystemNotifyHint.Text = Loc.Get("settings.systemNotify.hint")),
            (ShelfUnlockHint, () => ShelfUnlockHint.Text = Loc.Get("settings.shelfUnlock.hint")),
            (ShowBatteryHint, () => ShowBatteryHint.Text = Loc.Get("settings.showBattery.hint")),
            (LanguageLabel, () => LanguageLabel.Text = Loc.Get("settings.language")),
            (LanguageHint, () => LanguageHint.Text = Loc.Get("settings.language.hint")),

            (AdvancedHeader, () => AdvancedHeader.Text = Loc.Get("settings.advanced")),
            (YouTubeApiHint, () => YouTubeApiHint.Text = Loc.Get("settings.youtubeApi.hint")),
            (YouTubeApiKeyLabel, () => YouTubeApiKeyLabel.Text = Loc.Get("settings.youtubeApiKey")),
            (YouTubeApiKeyHint, () => YouTubeApiKeyHint.Text = Loc.Get("settings.youtubeApiKey.hint")),

            (AnimationFpsLabel, () => { AnimationFpsLabel.Text = Loc.Get("settings.animationFps"); AnimationFpsSlider.Label = Loc.Get("settings.animationFps"); AnimationFpsSlider.Description = Loc.Get("settings.animationFps.hint"); }),
            (EnableBlurEffectsHint, () => EnableBlurEffectsHint.Text = Loc.Get("settings.enableBlurEffects.hint")),
            (EnableSubjectBlurHint, () => EnableSubjectBlurHint.Text = Loc.Get("settings.enableSubjectBlur.hint")),
            (EnableSmartCropHint, () => EnableSmartCropHint.Text = Loc.Get("settings.enableSmartCrop.hint")),
            (MediaArtBackgroundHint, () => { MediaArtBackgroundCheck.Content = Loc.Get("settings.mediaArtBackground"); MediaArtBackgroundHint.Text = Loc.Get("settings.mediaArtBackground.hint"); }),

            (DonatingTitle, () => DonatingTitle.Text = Loc.Get("settings.donating.title")),
            (DonatingDescription, () => DonatingDescription.Text = Loc.Get("settings.donating.description")),
            (DonatingBankTitle, () => DonatingBankTitle.Text = Loc.Get("settings.donating.bank")),
            (DonatingBankHint, () => DonatingBankHint.Text = Loc.Get("settings.donating.bank.hint")),
        };

        AnimateContentChange(CheckUpdateButton, () => CheckUpdateButton.Content = Loc.Get("settings.checkUpdate"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(DonatePaypalButton, () => DonatePaypalButton.Content = Loc.Get("settings.donating.paypal"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(ResetButton, () => ResetButton.Content = Loc.Get("settings.btn.reset"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(ApplyButton, () => ApplyButton.Content = Loc.Get("settings.btn.apply"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(SaveButton, () => SaveButton.Content = Loc.Get("settings.btn.save"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;

        AnimateContentChange(AutoStartCheck, () => AutoStartCheck.Content = Loc.Get("settings.autoStart"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(HelloGreetingCheck, () => HelloGreetingCheck.Content = Loc.Get("settings.helloGreeting"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(HideOnExclusiveFullscreenCheck, () => HideOnExclusiveFullscreenCheck.Content = Loc.Get("settings.hideExclusiveFs"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(HideOnWindowedFullscreenCheck, () => HideOnWindowedFullscreenCheck.Content = Loc.Get("settings.hideWindowedFs"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(MusicNotifyCheck, () => MusicNotifyCheck.Content = Loc.Get("settings.musicNotify"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(SystemNotifyCheck, () => SystemNotifyCheck.Content = Loc.Get("settings.systemNotify"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(ShelfUnlockCheck, () => ShelfUnlockCheck.Content = Loc.Get("settings.shelfUnlock"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(CopyShelfClipboardCheck, () => CopyShelfClipboardCheck.Content = Loc.Get("settings.copyShelfClipboard"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(ShowBatteryCheck, () => ShowBatteryCheck.Content = Loc.Get("settings.showBattery"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(YouTubeApiCheck, () => YouTubeApiCheck.Content = Loc.Get("settings.youtubeApi"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(HoverExpandCheck, () => HoverExpandCheck.Content = Loc.Get("settings.hoverExpand"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(DisableMouseLeaveAutoCloseCheck, () => DisableMouseLeaveAutoCloseCheck.Content = Loc.Get("settings.disableAutoClose"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(ReopenLastViewCheck, () => ReopenLastViewCheck.Content = Loc.Get("settings.reopenLastView"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(IdleAutoHideCheck, () => IdleAutoHideCheck.Content = Loc.Get("settings.idleAutoHide"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(EnableSpotifyLyricsCheck, () => EnableSpotifyLyricsCheck.Content = Loc.Get("settings.enableSpotifyLyrics"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(EnableYouTubeSubtitlesCheck, () => EnableYouTubeSubtitlesLabel.Text = Loc.Get("settings.enableYouTubeSubtitles"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(EnableBlurEffectsCheck, () => EnableBlurEffectsCheck.Content = Loc.Get("settings.enableBlurEffects"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(EnableSubjectBlurCheck, () => EnableSubjectBlurCheck.Content = Loc.Get("settings.enableSubjectBlur"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(EnableSmartCropCheck, () => EnableSmartCropCheck.Content = Loc.Get("settings.enableSmartCrop"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;

        foreach (var (element, update) in textUpdates)
        {
            if (element == null) continue;
            AnimateTextSwap(element, update, staggerMs, easeOut, fps, slideDist);
            staggerMs += staggerStep;
        }

        ApplyLiquidGlassLocalization();
    }

    private void AnimateTextSwap(FrameworkElement element, Action updateText, int delayMs, IEasingFunction easing, int fps, double slideDist)
    {
        var translate = element.RenderTransform as TranslateTransform;
        if (translate == null)
        {
            translate = new TranslateTransform(0, 0);
            element.RenderTransform = translate;
        }

        element.BeginAnimation(OpacityProperty, null);
        translate.BeginAnimation(TranslateTransform.XProperty, null);

        var fadeOut = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(100),
            EasingFunction = easing,
            BeginTime = TimeSpan.FromMilliseconds(delayMs)
        };
        Timeline.SetDesiredFrameRate(fadeOut, VNotch.Services.AnimationConfig.TargetFps);

        var slideOut = new DoubleAnimation
        {
            To = -10,
            Duration = TimeSpan.FromMilliseconds(100),
            EasingFunction = easing,
            BeginTime = TimeSpan.FromMilliseconds(delayMs)
        };
        Timeline.SetDesiredFrameRate(slideOut, VNotch.Services.AnimationConfig.TargetFps);

        fadeOut.Completed += (s, e) =>
        {
            updateText();

            translate.X = 14;

            var fadeIn = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = easing
            };
            Timeline.SetDesiredFrameRate(fadeIn, VNotch.Services.AnimationConfig.TargetFps);

            var slideIn = new DoubleAnimation
            {
                From = 14,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = easing
            };
            Timeline.SetDesiredFrameRate(slideIn, VNotch.Services.AnimationConfig.TargetFps);

            slideIn.Completed += (s2, e2) =>
            {
                translate.BeginAnimation(TranslateTransform.XProperty, null);
                translate.X = 0;
            };

            element.BeginAnimation(OpacityProperty, fadeIn);
            translate.BeginAnimation(TranslateTransform.XProperty, slideIn);
        };

        element.BeginAnimation(OpacityProperty, fadeOut);
        translate.BeginAnimation(TranslateTransform.XProperty, slideOut);
    }

    private void AnimateContentChange(FrameworkElement element, Action updateContent, int delayMs, IEasingFunction easing, int fps, double slideDist)
    {
        element.BeginAnimation(OpacityProperty, null);

        var fadeOut = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(120),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            BeginTime = TimeSpan.FromMilliseconds(delayMs)
        };
        Timeline.SetDesiredFrameRate(fadeOut, fps);

        fadeOut.Completed += (s, e) =>
        {
            updateContent();

            var fadeIn = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(280),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Timeline.SetDesiredFrameRate(fadeIn, fps);

            element.BeginAnimation(OpacityProperty, fadeIn);
        };

        element.BeginAnimation(OpacityProperty, fadeOut);
    }

private void PushLivePreview()
    {
        if (_isLoadingSettings) return;
        if (!IsLoaded) return;

        if (_livePreviewDebounce == null)
        {
            _livePreviewDebounce = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(32)
            };
            _livePreviewDebounce.Tick += (s, e) =>
            {
                _livePreviewDebounce.Stop();
                ApplySettingsFromUi(persist: false);
            };
        }

        _livePreviewDebounce.Stop();
        _livePreviewDebounce.Start();
    }

    #endregion

    #region Entrance Animation

    private void PlayEntranceAnimation()
    {
        var easeOut = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };
        var easeOutStrong = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 7 };
        var itemEase = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };
        int fps = VNotch.Services.AnimationConfig.TargetFps;

        var totalDur = TimeSpan.FromMilliseconds(650);

        double notchLeft = 0, notchTop = 0, notchW = 230, notchH = 32, notchRadius = 8;
        if (Owner is MainWindow mainWindow)
        {
            var rect = mainWindow.GetNotchScreenRect();
            notchLeft = rect.Left;
            notchTop = rect.Top;
            notchW = rect.Width;
            notchH = rect.Height;
            notchRadius = rect.CornerRadius;
        }

        double shellWidth = ActualWidth > 0 ? ActualWidth - 36 : 824;
        double shellHeight = ActualHeight > 0 ? ActualHeight - 36 : 584;
        double startScaleX = Math.Max(0.02, notchW / shellWidth);
        double startScaleY = Math.Max(0.02, notchH / shellHeight);
        double startRadius = Math.Max(notchRadius, 12);

        MainShell.Opacity = 1.0;
        MainShell.RenderTransformOrigin = new Point(0.5, 0.0);
        MainShell.Effect = null;

        ShellContent.CacheMode = new System.Windows.Media.BitmapCache { RenderAtScale = 1.0 };
        ShellScale.ScaleX = startScaleX;
        ShellScale.ScaleY = startScaleY;
        ShellTranslate.Y = 0;
        MainShell.CornerRadius = new CornerRadius(startRadius);
        FooterBar.CornerRadius = new CornerRadius(0, 0, startRadius, startRadius);

        double finalLeft = Left;
        double finalTop = Top;

        Left = notchLeft + notchW / 2.0 - ActualWidth / 2.0;
        Top = notchTop;

        var expandX = new DoubleAnimation(startScaleX, 1.0, totalDur)
        {
            EasingFunction = easeOutStrong
        };
        Timeline.SetDesiredFrameRate(expandX, fps);

        var expandY = new DoubleAnimation(startScaleY, 1.0, totalDur)
        {
            EasingFunction = easeOutStrong
        };
        Timeline.SetDesiredFrameRate(expandY, fps);

        _shellCornerRadius = startRadius;
        var cornerAnim = new DoubleAnimation(startRadius, 24, totalDur)
        {
            EasingFunction = easeOut
        };
        Timeline.SetDesiredFrameRate(cornerAnim, fps);

        var moveTop = new DoubleAnimation(Top, finalTop, totalDur)
        {
            EasingFunction = easeOutStrong
        };
        Timeline.SetDesiredFrameRate(moveTop, fps);

        var moveLeft = new DoubleAnimation(Left, finalLeft, totalDur)
        {
            EasingFunction = easeOutStrong
        };
        Timeline.SetDesiredFrameRate(moveLeft, fps);

        expandX.Completed += (s, e) =>
        {
            ShellContent.CacheMode = null;
            MainShell.RenderTransformOrigin = new Point(0.5, 0.5);

            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                var shadow = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = System.Windows.Media.Colors.Black,
                    BlurRadius = 30,
                    ShadowDepth = 0,
                    Opacity = 0.0
                };
                MainShell.Effect = shadow;

                var shadowFade = new DoubleAnimation(0.0, 0.42, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = easeOut
                };
                Timeline.SetDesiredFrameRate(shadowFade, fps);
                shadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, shadowFade);
            }));
        };

        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, expandX);
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, expandY);
        this.BeginAnimation(ShellCornerRadiusProperty, cornerAnim);
        this.BeginAnimation(TopProperty, moveTop);
        this.BeginAnimation(LeftProperty, moveLeft);

        int contentDelay = 250;
        AnimateEntranceItem(SettingsHeader, HeaderTranslate, contentDelay);

        int socialDelay = contentDelay + 80;
        AnimateSocialIcon(SocialWebsite, SocialWebsiteTranslate, socialDelay);
        AnimateSocialIcon(SocialGitHub, SocialGitHubTranslate, socialDelay + 60);
        AnimateSocialIcon(SocialFacebook, SocialFacebookTranslate, socialDelay + 120);
        AnimateSocialIcon(SocialDiscord, SocialDiscordTranslate, socialDelay + 180);

        AnimateEntranceItem(NavPanel, NavPanelTranslate, contentDelay + 40);

        AnimateActivePanel(_activeNav);

        AnimateEntranceItem(FooterBar, FooterTranslate, contentDelay + 160);

        void AnimateSocialIcon(UIElement element, TranslateTransform translate, int delayMs)
        {
            var fade = CreateAnimation(0, 1, 350, itemEase);
            fade.BeginTime = TimeSpan.FromMilliseconds(delayMs);
            element.BeginAnimation(OpacityProperty, fade);

            var slide = CreateAnimation(6, 0, 400, itemEase);
            slide.BeginTime = TimeSpan.FromMilliseconds(delayMs);
            translate.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        void AnimateEntranceItem(UIElement element, TranslateTransform translate, int delayMs)
        {
            var fade = CreateAnimation(0, 1, 420, itemEase);
            fade.BeginTime = TimeSpan.FromMilliseconds(delayMs);
            element.BeginAnimation(OpacityProperty, fade);

            var slide = CreateAnimation(12, 0, 520, itemEase);
            slide.BeginTime = TimeSpan.FromMilliseconds(delayMs);
            translate.BeginAnimation(TranslateTransform.YProperty, slide);
        }
    }

    private static DoubleAnimation CreateAnimation(double from, double to, int durationMs, IEasingFunction easing)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = easing
        };

        Timeline.SetDesiredFrameRate(animation, VNotch.Services.AnimationConfig.TargetFps);
        return animation;
    }

    #endregion

    #region Button Handlers

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        bool confirmed = VNotch.Windows.ConfirmationDialog.Show(
            this,
            Loc.Get("settings.reset.confirm"),
            Loc.Get("settings.reset.title"),
            Loc.Get("dialog.confirm"),
            Loc.Get("dialog.cancel"),
            VNotch.Windows.ConfirmationDialog.DialogIcon.Warning,
            VNotch.Windows.ConfirmationDialog.DialogStyle.Danger);

        if (!confirmed) return;

        var defaults = new NotchSettings();

        WidthSlider.Value = defaults.Width;
        DynamicIslandWidthSlider.Value = defaults.DynamicIslandWidth;
        DynamicIslandHeightSlider.Value = defaults.DynamicIslandHeight;
        HeightSlider.Value = defaults.Height;
        RadiusSlider.Value = defaults.CornerRadius;
        OpacitySlider.Value = defaults.Opacity * 100;
        BlurBrightnessSlider.Value = defaults.MediaBlurBrightnessBoost * 100;
        BlurDarkOverlaySlider.Value = defaults.MediaBlurDarkOverlay * 100;
        AnimationFpsSlider.Value = defaults.AnimationFps;
        EnableBlurEffectsCheck.IsChecked = defaults.EnableBlurEffects;
        MediaArtBackgroundCheck.IsChecked = defaults.ShowMediaArtBackground;
        _settings.NotchStyle = defaults.NotchStyle;
        _settings.LiquidGlass = (defaults.LiquidGlass ?? new Models.LiquidGlassConfig()).Clone();
        LoadLiquidGlassUi();
        EnableSubjectBlurCheck.IsChecked = defaults.EnableSubjectBlur;
        EnableSmartCropCheck.IsChecked = defaults.EnableSmartCrop;
        UpdatePerformanceDependentControls(defaults.EnableBlurEffects);
        EnableSpotifyLyricsCheck.IsChecked = defaults.EnableSpotifyLyrics;
        EnableYouTubeSubtitlesCheck.IsChecked = defaults.EnableYouTubeSubtitles;
        UpdateLyricsDependentControls(defaults.EnableSpotifyLyrics);
        UpdateYouTubeSubtitlesDependentControls(defaults.EnableYouTubeSubtitles);

        _settings.SubtitlePriority = defaults.SubtitlePriority;
        LoadSubtitlePriority();

        DynamicIslandModeCheck.IsChecked = defaults.EnableDynamicIslandMode;
        UpdateDynamicIslandDependentControls(defaults.EnableDynamicIslandMode);

        HoverExpandCheck.IsChecked = defaults.EnableHoverExpand;
        HoverDelaySlider.Value = defaults.HoverExpandDelay;
        HoverDelaySlider.IsEnabled = defaults.EnableHoverExpand;
        HoverDelaySlider.Opacity = defaults.EnableHoverExpand ? 1.0 : 0.4;
        DisableMouseLeaveAutoCloseCheck.IsChecked = defaults.DisableMouseLeaveAutoClose;
        ReopenLastViewCheck.IsChecked = defaults.ReopenLastViewOnExpand;

        MusicNotifyCheck.IsChecked = defaults.ShowMusicNotifications;
        SystemNotifyCheck.IsChecked = defaults.ShowSystemNotifications;
        ShelfUnlockCheck.IsChecked = defaults.IsShelfUploadLimitUnlocked;
        CopyShelfClipboardCheck.IsChecked = defaults.CopyShelfFilesToClipboard;
        ShowBatteryCheck.IsChecked = defaults.ShowBatteryIndicator;
        _settings.BatteryDeviceId = defaults.BatteryDeviceId;
        HideOnExclusiveFullscreenCheck.IsChecked = defaults.HideOnExclusiveFullscreen;
        HideOnWindowedFullscreenCheck.IsChecked = defaults.HideOnWindowedFullscreen;
        IdleAutoHideCheck.IsChecked = defaults.EnableIdleAutoHide;
        IdleAutoHideDelaySlider.Value = Math.Max(2, defaults.IdleAutoHideDelay / 1000.0);
        IdleAutoHideDelaySlider.IsEnabled = defaults.EnableIdleAutoHide;
        IdleAutoHideDelaySlider.Opacity = defaults.EnableIdleAutoHide ? 1.0 : 0.4;
        int defLangIndex = 0;
        for (int i = 0; i < LanguageCombo.Items.Count; i++)
        {
            if (LanguageCombo.Items[i] is System.Windows.Controls.ComboBoxItem item && item.Tag as string == defaults.Language)
            {
                defLangIndex = i;
                break;
            }
        }
        LanguageCombo.SelectedIndex = defLangIndex;
        _settings.ExpandedWidget = defaults.ExpandedWidget;
        WidgetCombo.SelectedIndex = defaults.ExpandedWidget switch
        {
            "clock" => 1,
            "wordclock" => 2,
            "weather" => 3,
            "sysmon" => 4,
            _ => 0
        };
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        RevertLivePreviewIfNeeded();
        CloseWithAnimation();
    }
private void RevertLivePreviewIfNeeded()
    {
        SettingsChanged?.Invoke(this, _originalSettings.Clone());
    }

    private void SocialLink_Website_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "https://v-notch.vercel.app/",
            UseShellExecute = true
        });
    }

    private void SocialLink_GitHub_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "https://github.com/rainaku/V-Notch",
            UseShellExecute = true
        });
    }

    private void SocialLink_Facebook_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "https://www.facebook.com/rain.107/",
            UseShellExecute = true
        });
    }

    private void SocialLink_Discord_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "https://www.facebook.com/rain.107/",
            UseShellExecute = true
        });
    }

    private void DonatePaypal_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "https://www.paypal.com/paypalme/PhuocLe678",
            UseShellExecute = true
        });
    }

    private void ReportBug_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "https://github.com/rainaku/V-Notch/issues/new",
            UseShellExecute = true
        });
    }

    private void RequestFeature_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "https://github.com/rainaku/V-Notch/issues/new?labels=enhancement&template=feature_request.md",
            UseShellExecute = true
        });
    }

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        bool confirmed = VNotch.Windows.ConfirmationDialog.Show(
            this,
            Loc.Get("settings.clearCache.confirm"),
            Loc.Get("settings.clearCache.title"),
            Loc.Get("dialog.confirm"),
            Loc.Get("dialog.cancel"),
            VNotch.Windows.ConfirmationDialog.DialogIcon.Question,
            VNotch.Windows.ConfirmationDialog.DialogStyle.Normal,
            Loc.Get("settings.clearCache.detail"));

        if (!confirmed) return;

        int deletedCount = 0;
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "V-Notch");
        var baseDir = AppContext.BaseDirectory;

        var filesToDelete = new[]
        {
            Path.Combine(appData, "source_cache.json"),
            Path.Combine(baseDir, "vnotch-debug.log"),
            Path.Combine(baseDir, "vnotch-debug.log.old"),
        };

        foreach (var file in filesToDelete)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                    deletedCount++;
                }
            }
            catch { }
        }

        try
        {
            foreach (var corrupt in Directory.GetFiles(appData, "settings.corrupt-*.json"))
            {
                try { File.Delete(corrupt); deletedCount++; } catch { }
            }
        }
        catch (Exception ex)
        {
            VNotch.Services.RuntimeLog.Warn("SETTINGS", $"Failed to enumerate corrupt backups: {ex.Message}");
        }

        ClearCacheHint.Text = deletedCount > 0
            ? Loc.Get("settings.clearCache.done", deletedCount)
            : Loc.Get("settings.clearCache.clean");
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        ApplySettingsFromUi(persist: true);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ApplySettingsFromUi(persist: true);
        CloseWithAnimation();
    }
private void CloseWithAnimation()
    {
        if (_isClosing) return;
        _isClosing = true;

        AnimatedClosing?.Invoke(this, EventArgs.Empty);

        var easeIn = new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = 6 };
        var easeInStrong = new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = 7 };
        var itemEase = new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = 5 };
        int fps = VNotch.Services.AnimationConfig.TargetFps;

        var totalDur = TimeSpan.FromMilliseconds(650);

        double currentTop = Top;
        double currentLeft = Left;

        MainShell.BeginAnimation(OpacityProperty, null);
        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ShellTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        ShellTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        this.BeginAnimation(TopProperty, null);
        this.BeginAnimation(LeftProperty, null);
        this.BeginAnimation(ShellCornerRadiusProperty, null);

        Top = currentTop;
        Left = currentLeft;

        MainShell.Opacity = 1.0;
        ShellScale.ScaleX = 1.0;
        ShellScale.ScaleY = 1.0;
        ShellTranslate.X = 0;
        ShellTranslate.Y = 0;
        MainShell.RenderTransformOrigin = new Point(0.5, 0.0);

        MainShell.Effect = null;

        // --- Performance Optimizations ---
        // 1. Enable Bitmap Cache to render the entire shell on GPU during scaling
        MainShell.CacheMode = new BitmapCache { EnableClearType = false, RenderAtScale = 1.0 };
        // 2. Disable pixel snapping and layout rounding during animation to prevent animation jitter
        MainShell.SnapsToDevicePixels = false;
        MainShell.UseLayoutRounding = false;
        // 3. Set scaling mode to LowQuality (bilinear) for faster scaling animation on the GPU
        RenderOptions.SetBitmapScalingMode(MainShell, BitmapScalingMode.LowQuality);

        AnimateExitItem(FooterBar, FooterTranslate, 0);
        AnimateExitItem(NavPanel, NavPanelTranslate, 40);

        UIElement? activeCard = _activeNav switch
        {
            "Appearance" => AppearanceCard,
            "Behavior" => BehaviorCard,
            "Devices" => DisplayCard,
            "System" => SystemCard,
            "Advanced" => AdvancedCard,
            "Performance" => PerformanceCard,
            "Donating" => DonatingCard,
            "Updates" => UpdatesCard,
            "Searching" => SearchingCard,
            _ => null
        };
        TranslateTransform? activeTranslate = _activeNav switch
        {
            "Appearance" => AppearanceCardTranslate,
            "Behavior" => BehaviorCardTranslate,
            "Devices" => DisplayCardTranslate,
            "System" => SystemCardTranslate,
            "Advanced" => AdvancedCardTranslate,
            "Performance" => PerformanceCardTranslate,
            "Donating" => DonatingCardTranslate,
            "Updates" => UpdatesCardTranslate,
            "Searching" => SearchingCardTranslate,
            _ => null
        };
        if (activeCard != null && activeTranslate != null)
            AnimateExitItem(activeCard, activeTranslate, 60);

        AnimateExitItem(SettingsHeader, HeaderTranslate, 100);

        double notchRadius = 8;
        double notchW = 230, notchH = 32;
        double notchLeft = 0, notchTop = 0;
        if (Owner is MainWindow mainWindow)
        {
            var rect = mainWindow.GetNotchScreenRect();
            notchLeft = rect.Left;
            notchTop = rect.Top;
            notchW = rect.Width;
            notchH = rect.Height;
            notchRadius = rect.CornerRadius;
        }

        double shellWidth = ActualWidth > 0 ? ActualWidth - 36 : 824;
        double shellHeight = ActualHeight > 0 ? ActualHeight - 36 : 584;
        double targetScaleX = Math.Max(0.02, notchW / shellWidth);
        double targetScaleY = Math.Max(0.02, notchH / shellHeight);
        double targetRadius = Math.Max(notchRadius, 12);

        var squishX = new DoubleAnimation(1.0, targetScaleX, totalDur)
        {
            EasingFunction = easeInStrong
        };
        Timeline.SetDesiredFrameRate(squishX, fps);

        var shrinkY = new DoubleAnimation(1.0, targetScaleY, totalDur)
        {
            EasingFunction = easeInStrong
        };
        Timeline.SetDesiredFrameRate(shrinkY, fps);

        _shellCornerRadius = 24;
        var cornerAnim = new DoubleAnimation(24, targetRadius, totalDur)
        {
            EasingFunction = easeIn
        };
        Timeline.SetDesiredFrameRate(cornerAnim, fps);

        double targetLeft = notchLeft + notchW / 2.0 - ActualWidth / 2.0;
        double targetTop = notchTop;

        var flyUpWindow = new DoubleAnimation(Top, targetTop, totalDur)
        {
            EasingFunction = easeInStrong
        };
        Timeline.SetDesiredFrameRate(flyUpWindow, Math.Min(60, fps));

        var flyLeftWindow = new DoubleAnimation(Left, targetLeft, totalDur)
        {
            EasingFunction = easeInStrong
        };
        Timeline.SetDesiredFrameRate(flyLeftWindow, Math.Min(60, fps));

        squishX.Completed += (s, e) =>
        {
            Close();
        };

        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, squishX);
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrinkY);
        this.BeginAnimation(ShellCornerRadiusProperty, cornerAnim);
        this.BeginAnimation(TopProperty, flyUpWindow);
        this.BeginAnimation(LeftProperty, flyLeftWindow);

        void AnimateExitItem(UIElement element, TranslateTransform translate, int delayMs)
        {
            // Enable bitmap caching on child elements to animate their fade & slide on GPU
            element.CacheMode = new BitmapCache { EnableClearType = false, RenderAtScale = 1.0 };

            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(550))
            {
                EasingFunction = easeInStrong,
                BeginTime = TimeSpan.FromMilliseconds(delayMs)
            };
            Timeline.SetDesiredFrameRate(fade, fps);
            element.BeginAnimation(OpacityProperty, fade);

            var slide = new DoubleAnimation(0, 12, TimeSpan.FromMilliseconds(550))
            {
                EasingFunction = easeInStrong,
                BeginTime = TimeSpan.FromMilliseconds(delayMs)
            };
            Timeline.SetDesiredFrameRate(slide, fps);
            translate.BeginAnimation(TranslateTransform.YProperty, slide);
        }
    }

    private bool _isClosing = false;
    private double _shellCornerRadius = 24;
public static readonly DependencyProperty ShellCornerRadiusProperty =
        DependencyProperty.Register("ShellCornerRadius", typeof(double), typeof(SettingsWindow),
            new PropertyMetadata(24.0, OnShellCornerRadiusChanged));

    public double ShellCornerRadius
    {
        get => (double)GetValue(ShellCornerRadiusProperty);
        set => SetValue(ShellCornerRadiusProperty, value);
    }

    private static void OnShellCornerRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SettingsWindow window)
        {
            double r = (double)e.NewValue;
            window.MainShell.CornerRadius = new CornerRadius(r);
            window.FooterBar.CornerRadius = new CornerRadius(0, 0, r, r);
        }
    }

    private void ApplySettingsFromUi(bool persist = true)
    {
        _settings.Width = (int)WidthSlider.Value;
        _settings.DynamicIslandWidth = Math.Max(100, (int)DynamicIslandWidthSlider.Value);
        _settings.DynamicIslandHeight = Math.Max(24, (int)DynamicIslandHeightSlider.Value);
        _settings.Height = (int)HeightSlider.Value;
        _settings.CornerRadius = (int)RadiusSlider.Value;
        _settings.Opacity = OpacitySlider.Value / 100.0;
        _settings.MediaBlurBrightnessBoost = BlurBrightnessSlider.Value / 100.0;
        _settings.MediaBlurDarkOverlay = BlurDarkOverlaySlider.Value / 100.0;
        _settings.AnimationFps = (int)Math.Round(AnimationFpsSlider.Value);
        VNotch.Services.AnimationConfig.Configure(_settings.AnimationFps);
        AnimationPrimitives.ApplyFpsToTree(this);
        _settings.EnableBlurEffects = EnableBlurEffectsCheck.IsChecked ?? true;
        _settings.ShowMediaArtBackground = MediaArtBackgroundCheck.IsChecked ?? true;
        SaveLiquidGlassUi();
        _settings.EnableSubjectBlur = EnableSubjectBlurCheck.IsChecked ?? true;
        _settings.EnableSmartCrop = EnableSmartCropCheck.IsChecked ?? true;
        _settings.EnableSpotifyLyrics = EnableSpotifyLyricsCheck.IsChecked ?? true;
        _settings.EnableYouTubeSubtitles = EnableYouTubeSubtitlesCheck.IsChecked ?? true;

        _settings.SubtitlePriority = GetSubtitlePriorityString();

        _settings.EnableDynamicIslandMode = DynamicIslandModeCheck.IsChecked ?? false;

        _settings.EnableHoverExpand = HoverExpandCheck.IsChecked ?? true;
        _settings.HoverExpandDelay = (int)HoverDelaySlider.Value;
        _settings.DisableMouseLeaveAutoClose = DisableMouseLeaveAutoCloseCheck.IsChecked ?? false;
        _settings.ReopenLastViewOnExpand = ReopenLastViewCheck.IsChecked ?? false;

        _settings.MonitorIndex = MonitorCombo.SelectedIndex;
        if (CameraCombo.SelectedItem is CameraDeviceItem selectedCamera)
            _settings.CameraDeviceId = selectedCamera.Id;
        if (VisualizerAudioCombo.SelectedItem is AudioDeviceItem selectedAudioDevice)
            _settings.VisualizerAudioDeviceId = selectedAudioDevice.Id;
        _settings.AutoStart = AutoStartCheck.IsChecked ?? false;
        _settings.EnableHelloGreeting = HelloGreetingCheck.IsChecked ?? true;
        _settings.HideOnExclusiveFullscreen = HideOnExclusiveFullscreenCheck.IsChecked ?? true;
        _settings.HideOnWindowedFullscreen = HideOnWindowedFullscreenCheck.IsChecked ?? true;
        _settings.EnableIdleAutoHide = IdleAutoHideCheck.IsChecked ?? false;
        _settings.IdleAutoHideDelay = Math.Max(1000, (int)(IdleAutoHideDelaySlider.Value * 1000));
        _settings.ShowMusicNotifications = MusicNotifyCheck.IsChecked ?? true;
        _settings.ShowSystemNotifications = SystemNotifyCheck.IsChecked ?? true;
        _settings.IsShelfUploadLimitUnlocked = ShelfUnlockCheck.IsChecked ?? false;
        _settings.CopyShelfFilesToClipboard = CopyShelfClipboardCheck.IsChecked ?? false;
        _settings.ShowBatteryIndicator = ShowBatteryCheck.IsChecked ?? true;

        _settings.EnableWeather = EnableWeatherCheck.IsChecked ?? false;
        _settings.ManualCity = ManualCityTextBox.Text?.Trim() ?? string.Empty;

        _settings.EnableYouTubeApi = YouTubeApiCheck.IsChecked ?? false;
        _settings.YouTubeApiKey = YouTubeApiKeyPasswordBox.Password?.Trim() ?? "";

        if (LanguageCombo.SelectedItem is System.Windows.Controls.ComboBoxItem langItem && langItem.Tag is string langCode)
            _settings.Language = langCode;

        if (WidgetCombo.SelectedItem is System.Windows.Controls.ComboBoxItem widgetItem && widgetItem.Tag is string widgetCode)
            _settings.ExpandedWidget = widgetCode;
        if (persist)
        {
            _settingsService.Save(_settings);
            StartupManager.SetAutoStart(_settings.AutoStart);
            _originalSettings = _settings.Clone();
        }

        SettingsChanged?.Invoke(this, _settings);
    }

    #endregion

    #region Update Handlers

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            UpdateStatusText.Text = Loc.Get("settings.checkingUpdates");
            CheckUpdateButton.IsEnabled = false;
            DownloadUpdateButton.Visibility = Visibility.Collapsed;

            _availableUpdate = await _updateService.CheckForUpdatesAsync();

            if (_availableUpdate == null)
            {
                UpdateStatusText.Text = Loc.Get("settings.checkUpdate");
                CheckUpdateButton.IsEnabled = true;
                return;
            }

            if (_availableUpdate.IsNewerVersion)
            {
                UpdateStatusText.Text = Loc.Get("settings.updateAvailable", _availableUpdate.Version);
                DownloadUpdateButton.Visibility = Visibility.Visible;
            }
            else
            {
                UpdateStatusText.Text = Loc.Get("settings.upToDate");
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
        try
        {
            await CheckForUpdatesAsync();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("SETTINGS", ex, "CheckUpdate failed");
        }
    }

    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate == null) return;

        UpdateStatusText.Text = Loc.Get("update.preparing");
        DownloadUpdateButton.IsEnabled = false;
        CheckUpdateButton.IsEnabled = false;

        var updateProgressWindow = new UpdateDownloadWindow();
        updateProgressWindow.SetIndeterminate(Loc.Get("update.preparing"));
        updateProgressWindow.Show();

        var downloadProgress = new Progress<double>(p =>
        {
            if (p < 0)
            {
                updateProgressWindow.SetIndeterminate(Loc.Get("update.downloading"));
                UpdateStatusText.Text = Loc.Get("update.downloading");
                return;
            }

            updateProgressWindow.SetStatus(Loc.Get("update.downloadingPercent", (int)p));
            updateProgressWindow.SetProgress(p);
            UpdateStatusText.Text = Loc.Get("update.downloadingPercent", (int)p);
        });

        try
        {
            var success = await _updateService.DownloadAndInstallUpdateAsync(_availableUpdate, downloadProgress);

            if (!success)
            {
                updateProgressWindow.Close();
                UpdateStatusText.Text = Loc.Get("settings.updateAvailable", _availableUpdate.Version);
                DownloadUpdateButton.IsEnabled = true;
                CheckUpdateButton.IsEnabled = true;
                MessageBox.Show(
                    Loc.Get("error.updateFailed"),
                    Loc.Get("error.updateFailedTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            updateProgressWindow.Close();
            UpdateStatusText.Text = $"Error: {ex.Message}";
            DownloadUpdateButton.IsEnabled = true;
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void ViewChangelog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var changelogWindow = new VNotch.Windows.ChangelogWindow(_updateService)
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            changelogWindow.ShowDialog();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("SETTINGS", ex, "Failed to open changelog window");
            MessageBox.Show(
                $"Failed to open changelog: {ex.Message}",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    #endregion

    #region Navigation

    private string _activeNav = "Appearance";
    private readonly Dictionary<string, StackPanel> _navPanels = new();
    private readonly Dictionary<string, Border> _navButtons = new();

    private void InitializeNavigation()
    {
        _navPanels["Searching"] = PanelSearching;
        _navPanels["Appearance"] = PanelAppearance;
        _navPanels["Skins"] = PanelSkins;
        _navPanels["Behavior"] = PanelBehavior;
        _navPanels["Devices"] = PanelDevices;
        _navPanels["System"] = PanelSystem;
        _navPanels["Advanced"] = PanelAdvanced;
        _navPanels["Performance"] = PanelPerformance;
        _navPanels["Donating"] = PanelDonating;
        _navPanels["Updates"] = PanelUpdates;

        _navButtons["Searching"] = NavSearching;
        _navButtons["Appearance"] = NavAppearance;
        _navButtons["Skins"] = NavSkins;
        _navButtons["Behavior"] = NavBehavior;
        _navButtons["Devices"] = NavDevices;
        _navButtons["System"] = NavSystem;
        _navButtons["Advanced"] = NavAdvanced;
        _navButtons["Performance"] = NavPerformance;
        _navButtons["Donating"] = NavDonating;
        _navButtons["Updates"] = NavUpdates;
    }

    private void Nav_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is string section)
        {
            if (_isSearchMode && section != "Searching")
            {
                return;
            }

            NavigateToSection(section);
        }
    }

    private void NavigateToSection(string section)
    {
        if (section == _activeNav) return;

        if (_navPanels.TryGetValue(_activeNav, out var oldPanel))
            oldPanel.Visibility = Visibility.Collapsed;

        if (_navButtons.TryGetValue(_activeNav, out var oldBtn))
        {
            oldBtn.Background = _transparentBrush;
            var oldStack = oldBtn.Child as StackPanel;
            if (oldStack != null && oldStack.Children.Count > 1 && oldStack.Children[1] is TextBlock oldText)
                oldText.Foreground = _navInactiveBrush;
        }

        _activeNav = section;

        if (_navPanels.TryGetValue(section, out var newPanel))
            newPanel.Visibility = Visibility.Visible;

        if (_navButtons.TryGetValue(section, out var newBtn))
        {
            newBtn.Background = (SolidColorBrush)FindResource("NavItemActiveBg");
            var newStack = newBtn.Child as StackPanel;
            if (newStack != null && newStack.Children.Count > 1 && newStack.Children[1] is TextBlock newText)
                newText.Foreground = _whiteBrush;
        }

        SettingsScrollViewer.ScrollToTop();

        AnimateActivePanel(section);
    }

    private void AnimateActivePanel(string section)
    {
        var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };

        UIElement? card = section switch
        {
            "Appearance" => AppearanceCard,
            "Searching" => SearchingCard,
            "Behavior" => BehaviorCard,
            "Devices" => DisplayCard,
            "System" => SystemCard,
            "Advanced" => AdvancedCard,
            "Performance" => PerformanceCard,
            "Donating" => DonatingCard,
            "Updates" => UpdatesCard,
            "Skins" => SkinCard,
            _ => null
        };

        TranslateTransform? translate = section switch
        {
            "Appearance" => AppearanceCardTranslate,
            "Searching" => SearchingCardTranslate,
            "Behavior" => BehaviorCardTranslate,
            "Devices" => DisplayCardTranslate,
            "System" => SystemCardTranslate,
            "Advanced" => AdvancedCardTranslate,
            "Performance" => PerformanceCardTranslate,
            "Donating" => DonatingCardTranslate,
            "Updates" => UpdatesCardTranslate,
            "Skins" => SkinCardTranslate,
            _ => null
        };

        if (card == null || translate == null) return;

        card.Opacity = 0;
        translate.Y = 12;

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350)) { EasingFunction = ease };
        Timeline.SetDesiredFrameRate(fade, VNotch.Services.AnimationConfig.TargetFps);
        var slide = new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease };
        Timeline.SetDesiredFrameRate(slide, VNotch.Services.AnimationConfig.TargetFps);

        card.BeginAnimation(OpacityProperty, fade);
        translate.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    #endregion

    #region Search

    private DispatcherTimer? _searchDebounce;
    private bool _isSearchMode;
    private readonly List<SearchRowEntry> _searchRows = new();

    private void SettingsSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        string query = SettingsSearchBox.Text?.Trim() ?? "";

        UpdateSearchPlaceholderVisibility();

        if (string.IsNullOrEmpty(query))
        {
            _searchDebounce?.Stop();
            ExitSearchMode();
            return;
        }

        if (_searchDebounce == null)
        {
            _searchDebounce = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(150)
            };
            _searchDebounce.Tick += (s, _) =>
            {
                _searchDebounce.Stop();
                ExecuteSearch(SettingsSearchBox.Text?.Trim() ?? "");
            };
        }
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void SettingsSearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        UpdateSearchPlaceholderVisibility();
    }

    private void SettingsSearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        UpdateSearchPlaceholderVisibility();
    }

    private void UpdateSearchPlaceholderVisibility()
    {
        string query = SettingsSearchBox.Text?.Trim() ?? "";
        if (SettingsSearchBox.IsFocused || !string.IsNullOrEmpty(query))
        {
            SearchPlaceholder.Visibility = Visibility.Collapsed;
        }
        else
        {
            SearchPlaceholder.Visibility = Visibility.Visible;
        }
    }

    private static string NormalizeSearchString(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        string normalizedString = input.Normalize(System.Text.NormalizationForm.FormD);
        var stringBuilder = new System.Text.StringBuilder(input.Length);

        foreach (char c in normalizedString)
        {
            var unicodeCategory = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
            if (unicodeCategory != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                stringBuilder.Append(c);
            }
        }
        return stringBuilder.ToString().Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant();
    }

    private static int CalculateLevenshteinDistance(string s, string t)
    {
        if (string.IsNullOrEmpty(s)) return string.IsNullOrEmpty(t) ? 0 : t.Length;
        if (string.IsNullOrEmpty(t)) return s.Length;

        int[] v0 = new int[t.Length + 1];
        int[] v1 = new int[t.Length + 1];

        for (int i = 0; i < v0.Length; i++) v0[i] = i;

        for (int i = 0; i < s.Length; i++)
        {
            v1[0] = i + 1;
            for (int j = 0; j < t.Length; j++)
            {
                int cost = (s[i] == t[j]) ? 0 : 1;
                v1[j + 1] = Math.Min(Math.Min(v1[j] + 1, v0[j + 1] + 1), v0[j] + cost);
            }
            for (int j = 0; j < v0.Length; j++) v0[j] = v1[j];
        }
        return v1[t.Length];
    }

    private static double CalculateFuzzyMatchScore(string source, string target)
    {
        if (source == target) return 1.0;
        if (source.Length == 0 || target.Length == 0) return 0.0;

        int distance = CalculateLevenshteinDistance(source, target);
        int maxLength = Math.Max(source.Length, target.Length);

        return 1.0 - (double)distance / maxLength;
    }

    private void ExecuteSearch(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;

        string normalizedQuery = NormalizeSearchString(query);
        EnterSearchMode();
        SearchResultsStack.Children.Clear();

        var matches = new List<SearchRowEntry>();
        foreach (var row in _searchRows)
        {
            row.SearchText = BuildSearchText(row.Row, row.Section);
            if (SearchTextMatches(row.SearchText, normalizedQuery))
            {
                matches.Add(row);
            }
        }

        foreach (var match in matches)
        {
            match.OriginalVisibility = match.Row.Visibility;

            if (match.Row.Parent is StackPanel currentParent)
            {
                currentParent.Children.Remove(match.Row);
            }

            match.Row.Visibility = Visibility.Visible;
            SearchResultsStack.Children.Add(match.Row);
        }

        SearchingEmptyText.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SettingsScrollViewer.ScrollToTop();
        AnimateActivePanel("Searching");
    }

    private bool SearchTextMatches(string sourceText, string normalizedQuery)
    {
        string normalizedText = NormalizeSearchString(sourceText);
        if (normalizedText.Contains(normalizedQuery))
        {
            return true;
        }

        if (normalizedText.Length == 0 || normalizedQuery.Length < 3)
        {
            return false;
        }

        if (normalizedText.Length <= 30 &&
            CalculateFuzzyMatchScore(normalizedQuery, normalizedText) > 0.75)
        {
            return true;
        }

        string[] words = normalizedText.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var word in words)
        {
            if (word.Length >= 3 && CalculateFuzzyMatchScore(normalizedQuery, word) > 0.75)
            {
                return true;
            }
        }

        return false;
    }

    private void EnterSearchMode()
    {
        if (!_isSearchMode)
        {
            _isSearchMode = true;
            IndexSearchRows();
        }
        else
        {
            RestoreSearchRows();
        }

        _activeNav = "Searching";

        foreach (var kvp in _navPanels)
        {
            kvp.Value.Visibility = kvp.Key == "Searching" ? Visibility.Visible : Visibility.Collapsed;
        }

        foreach (var kvp in _navButtons)
        {
            bool isSearching = kvp.Key == "Searching";
            kvp.Value.Visibility = Visibility.Visible;
            kvp.Value.IsHitTestVisible = isSearching;
            kvp.Value.Opacity = isSearching ? 1.0 : 0.35;
            kvp.Value.Background = isSearching
                ? (SolidColorBrush)FindResource("NavItemActiveBg")
                : _transparentBrush;

            var stack = kvp.Value.Child as StackPanel;
            if (stack?.Children.Count > 1 && stack.Children[1] is TextBlock txt)
                txt.Foreground = isSearching ? _whiteBrush : _navInactiveBrush;
        }
    }

    private void ExitSearchMode()
    {
        RestoreSearchRows();
        SearchResultsStack.Children.Clear();
        SearchingEmptyText.Visibility = Visibility.Collapsed;
        _isSearchMode = false;

        if (_activeNav == "Searching")
        {
            _activeNav = "Appearance";
        }

        ShowAllNavItems();
        if (_navButtons.TryGetValue("Searching", out var searchButton))
        {
            searchButton.Visibility = Visibility.Collapsed;
        }

        foreach (var kvp in _navPanels)
        {
            kvp.Value.Visibility = kvp.Key == _activeNav ? Visibility.Visible : Visibility.Collapsed;
        }

        AnimateActivePanel(_activeNav);
    }

    private void IndexSearchRows()
    {
        if (_searchRows.Count > 0) return;

        var rowStyle = FindResource("SettingRowBorder") as Style;
        foreach (var kvp in _navPanels)
        {
            if (kvp.Key == "Searching") continue;

            foreach (var row in FindVisualChildren<Border>(kvp.Value))
            {
                if (row.Style != rowStyle || row.Parent is not StackPanel parent)
                {
                    continue;
                }

                _searchRows.Add(new SearchRowEntry(
                    kvp.Key,
                    row,
                    parent,
                    parent.Children.IndexOf(row),
                    row.Visibility,
                    BuildSearchText(row, kvp.Key)));
            }
        }
    }

    private void RestoreSearchRows()
    {
        foreach (var row in _searchRows.OrderBy(r => r.OriginalIndex))
        {
            if (row.Row.Parent is StackPanel currentParent)
            {
                currentParent.Children.Remove(row.Row);
            }

            int insertIndex = Math.Clamp(row.OriginalIndex, 0, row.OriginalParent.Children.Count);
            row.OriginalParent.Children.Insert(insertIndex, row.Row);
            row.Row.Visibility = row.OriginalVisibility;
        }
    }

    private string BuildSearchText(DependencyObject root, string section)
    {
        var parts = new List<string> { section };
        if (_navButtons.TryGetValue(section, out var navButton))
        {
            parts.Add(ReadVisibleText(navButton));
        }

        parts.Add(ReadVisibleText(root));
        return string.Join(" ", parts);
    }

    private string ReadVisibleText(DependencyObject root)
    {
        var parts = new List<string>();
        CollectSearchText(root, parts);
        return string.Join(" ", parts);
    }

    private void AddAllTranslations(string text, List<string> parts)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var translations = Loc.GetAllTranslations(text);
        if (translations.Count > 0)
        {
            parts.AddRange(translations);
        }
        else
        {
            parts.Add(text);
        }
    }

    private void CollectSearchText(DependencyObject current, List<string> parts)
    {
        switch (current)
        {
            case TextBlock textBlock when !string.IsNullOrWhiteSpace(textBlock.Text):
                AddAllTranslations(textBlock.Text, parts);
                break;
            case CheckBox checkBox when checkBox.Content is string checkText:
                AddAllTranslations(checkText, parts);
                break;
            case ContentControl contentControl when contentControl.Content is string contentText:
                AddAllTranslations(contentText, parts);
                break;
            case ElasticSlider slider:
                AddAllTranslations(slider.Label, parts);
                AddAllTranslations(slider.Description, parts);
                AddAllTranslations(slider.Unit, parts);
                break;
        }

        int childCount = VisualTreeHelper.GetChildrenCount(current);
        for (int i = 0; i < childCount; i++)
        {
            CollectSearchText(VisualTreeHelper.GetChild(current, i), parts);
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
            {
                yield return typedChild;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed class SearchRowEntry
    {
        public SearchRowEntry(
            string section,
            Border row,
            StackPanel originalParent,
            int originalIndex,
            Visibility originalVisibility,
            string searchText)
        {
            Section = section;
            Row = row;
            OriginalParent = originalParent;
            OriginalIndex = originalIndex;
            OriginalVisibility = originalVisibility;
            SearchText = searchText;
        }

        public string Section { get; }
        public Border Row { get; }
        public StackPanel OriginalParent { get; }
        public int OriginalIndex { get; }
        public Visibility OriginalVisibility { get; set; }
        public string SearchText { get; set; }
    }

    private static readonly SolidColorBrush _whiteBrush = new(Colors.White);
    private static readonly SolidColorBrush _navInactiveBrush = new(Color.FromRgb(0xAA, 0xAA, 0xAA));
    private static readonly SolidColorBrush _transparentBrush = new(Colors.Transparent);

    static SettingsWindow()
    {
        _whiteBrush.Freeze();
        _navInactiveBrush.Freeze();
        _transparentBrush.Freeze();
    }

    private void ShowAllNavItems()
    {
        foreach (var kvp in _navButtons)
        {
            kvp.Value.IsHitTestVisible = true;
            kvp.Value.Opacity = 1.0;
            bool isActive = kvp.Key == _activeNav;
            kvp.Value.Background = isActive
                ? (SolidColorBrush)FindResource("NavItemActiveBg")
                : _transparentBrush;
            var stack = kvp.Value.Child as StackPanel;
            if (stack?.Children.Count > 1 && stack.Children[1] is TextBlock txt)
                txt.Foreground = isActive ? _whiteBrush : _navInactiveBrush;
        }
    }

    #endregion

    #region Smooth Scroll

    private double _scrollVelocity;
    private double _scrollTarget;
    private bool _isScrollAnimating;
    private const double ScrollFriction = 0.82;
    private const double ScrollSensitivity = 1.2;
    private const double ScrollMinVelocity = 0.3;

    private bool IsAnyComboBoxDropDownOpen()
    {
        return MonitorCombo.IsDropDownOpen
            || LanguageCombo.IsDropDownOpen
            || CameraCombo.IsDropDownOpen
            || VisualizerAudioCombo.IsDropDownOpen;
    }

    private void SettingsScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (IsAnyComboBoxDropDownOpen())
        {
            return;
        }

        e.Handled = true;

        double delta = -e.Delta * ScrollSensitivity;
        double maxScroll = SettingsScrollViewer.ScrollableHeight;

        if (!_isScrollAnimating)
        {
            _scrollTarget = SettingsScrollViewer.VerticalOffset;
        }

        _scrollVelocity += delta * 0.3;
        _scrollTarget = Math.Clamp(_scrollTarget + delta, 0, maxScroll);

        if (!_isScrollAnimating)
        {
            _isScrollAnimating = true;
            CompositionTarget.Rendering += SmoothScroll_Tick;
        }
    }

    private void SettingsScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
    }

    private void SmoothScroll_Tick(object? sender, EventArgs e)
    {
        double current = SettingsScrollViewer.VerticalOffset;
        double diff = _scrollTarget - current;

        _scrollVelocity *= ScrollFriction;

        double step = diff * 0.18 + _scrollVelocity * 0.4;
        double newOffset = Math.Clamp(current + step, 0, SettingsScrollViewer.ScrollableHeight);
        SettingsScrollViewer.ScrollToVerticalOffset(newOffset);

        if (Math.Abs(diff) < ScrollMinVelocity && Math.Abs(_scrollVelocity) < ScrollMinVelocity)
        {
            SettingsScrollViewer.ScrollToVerticalOffset(_scrollTarget);
            _scrollVelocity = 0;
            _isScrollAnimating = false;
            CompositionTarget.Rendering -= SmoothScroll_Tick;
        }
    }

    #endregion

    #region Camera Device

    private async Task LoadCameraDevices()
    {
        try
        {
            var groups = await global::Windows.Media.Capture.Frames.MediaFrameSourceGroup.FindAllAsync();
            var cameras = groups
                .Where(g => g.SourceInfos.Any(s => s.SourceKind == global::Windows.Media.Capture.Frames.MediaFrameSourceKind.Color))
                .Select(g => new CameraDeviceItem { Id = g.Id, Name = g.DisplayName })
                .ToList();

            if (cameras.Count == 0)
            {
                cameras.Add(new CameraDeviceItem { Id = "", Name = Loc.Get("settings.camera.none") });
            }

            CameraCombo.ItemsSource = cameras;
            CameraCombo.DisplayMemberPath = "Name";

            var selectedIdx = cameras.FindIndex(c => c.Id == _settings.CameraDeviceId);
            CameraCombo.SelectedIndex = selectedIdx >= 0 ? selectedIdx : 0;
        }
        catch
        {
            CameraCombo.ItemsSource = new[] { new CameraDeviceItem { Id = "", Name = Loc.Get("settings.camera.none") } };
            CameraCombo.DisplayMemberPath = "Name";
            CameraCombo.SelectedIndex = 0;
        }
    }

    private void CameraCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CameraCombo.SelectedItem is CameraDeviceItem item)
        {
            _settings.CameraDeviceId = item.Id;
        }
    }

    #endregion

    #region Visualizer Audio Device

    private void SetVisualizerAudioDevicePlaceholder()
    {
        VisualizerAudioCombo.ItemsSource = new[]
        {
            new AudioDeviceItem { Id = _settings.VisualizerAudioDeviceId, Name = Loc.Get("settings.visualizerAudio.default") }
        };
        VisualizerAudioCombo.DisplayMemberPath = "Name";
        VisualizerAudioCombo.SelectedIndex = 0;
    }

    private async Task LoadVisualizerAudioDevices()
    {
        List<(string Id, string Name)> rawDevices;
        try
        {
            rawDevices = await System.Threading.Tasks.Task.Run(() =>
            {
                var found = new List<(string, string)>();
                using var enumerator = new MMDeviceEnumerator();
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    found.Add((device.ID, device.FriendlyName));
                    device.Dispose();
                }
                return found;
            });
        }
        catch
        {
            rawDevices = new List<(string, string)>();
        }

        var devices = new List<AudioDeviceItem>
        {
            new() { Id = "", Name = Loc.Get("settings.visualizerAudio.default") }
        };

        foreach (var (id, name) in rawDevices)
        {
            devices.Add(new AudioDeviceItem { Id = id, Name = name });
        }

        if (!string.IsNullOrWhiteSpace(_settings.VisualizerAudioDeviceId) &&
            devices.All(d => d.Id != _settings.VisualizerAudioDeviceId))
        {
            devices.Add(new AudioDeviceItem
            {
                Id = _settings.VisualizerAudioDeviceId,
                Name = Loc.Get("settings.visualizerAudio.unavailable", _settings.VisualizerAudioDeviceId)
            });
        }

        VisualizerAudioCombo.ItemsSource = devices;
        VisualizerAudioCombo.DisplayMemberPath = "Name";

        var selectedIdx = devices.FindIndex(d => d.Id == _settings.VisualizerAudioDeviceId);
        VisualizerAudioCombo.SelectedIndex = selectedIdx >= 0 ? selectedIdx : 0;
    }

    private void VisualizerAudioCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VisualizerAudioCombo.SelectedItem is AudioDeviceItem item)
        {
            _settings.VisualizerAudioDeviceId = item.Id;
        }
    }

    #endregion
}

public class CameraDeviceItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    public override string ToString() => Name;
}

public class AudioDeviceItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    public override string ToString() => Name;
}