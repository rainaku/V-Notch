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
using VNotch.Controls;
using VNotch.Models;
using VNotch.Services;

namespace VNotch;

public partial class SettingsWindow : Window
{
    private readonly NotchSettings _settings;
    private NotchSettings _originalSettings;
    private readonly SettingsService _settingsService;
    private readonly IUpdateService _updateService;
    private UpdateInfo? _availableUpdate;
    private bool _isLoadingSettings = true;
    private DispatcherTimer? _livePreviewDebounce;

    public event EventHandler<NotchSettings>? SettingsChanged;
public event EventHandler? AnimatedClosing;

    public SettingsWindow(NotchSettings settings, SettingsService settingsService)
    {
        InitializeComponent();

        _settings = settings.Clone();
        _originalSettings = settings.Clone();
        _settingsService = settingsService;
        _updateService = new UpdateService();

        LoadSettings();
        CheckForUpdatesAsync().SafeFireAndForget("SETTINGS-UPDATE-CHECK");
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PlayEntranceAnimation();
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

            // Don't let window drag intercept the subtitle priority drag-reorder area
            if (source is FrameworkElement fe && fe.Name == "SubtitlePriorityItems")
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void LoadSettings()
    {
        _isLoadingSettings = true;

        WidthSlider.Value = _settings.Width;
        HeightSlider.Value = _settings.Height;
        RadiusSlider.Value = _settings.CornerRadius;
        OpacitySlider.Value = _settings.Opacity * 100;
        BlurBrightnessSlider.Value = _settings.MediaBlurBrightnessBoost * 100;
        BlurDarkOverlaySlider.Value = _settings.MediaBlurDarkOverlay * 100;
        EnableSpotifyLyricsCheck.IsChecked = _settings.EnableSpotifyLyrics;
        UpdateLyricsDependentControls(_settings.EnableSpotifyLyrics);
        EnableYouTubeSubtitlesCheck.IsChecked = _settings.EnableYouTubeSubtitles;
        UpdateYouTubeSubtitlesDependentControls(_settings.EnableYouTubeSubtitles);

        LoadSubtitlePriority();

        DynamicIslandModeCheck.IsChecked = _settings.EnableDynamicIslandMode;

        HoverExpandCheck.IsChecked = _settings.EnableHoverExpand;
        HoverDelaySlider.Value = _settings.HoverExpandDelay;
        HoverDelaySlider.IsEnabled = _settings.EnableHoverExpand;
        HoverDelaySlider.Opacity = _settings.EnableHoverExpand ? 1.0 : 0.4;
        DisableMouseLeaveAutoCloseCheck.IsChecked = _settings.DisableMouseLeaveAutoClose;

        var monitors = NotchManager.GetMonitorNames();
        MonitorCombo.ItemsSource = monitors;
        MonitorCombo.SelectedIndex = Math.Min(_settings.MonitorIndex, monitors.Length - 1);

        // Camera device combo
        LoadCameraDevices();

        AutoStartCheck.IsChecked = StartupManager.IsAutoStartEnabled();
        HelloGreetingCheck.IsChecked = _settings.EnableHelloGreeting;
        HideOnExclusiveFullscreenCheck.IsChecked = _settings.HideOnExclusiveFullscreen;
        HideOnWindowedFullscreenCheck.IsChecked = _settings.HideOnWindowedFullscreen;
        MusicNotifyCheck.IsChecked = _settings.ShowMusicNotifications;
        SystemNotifyCheck.IsChecked = _settings.ShowSystemNotifications;
        ShelfUnlockCheck.IsChecked = _settings.IsShelfUploadLimitUnlocked;

        // Language combo
        LanguageCombo.Items.Clear();
        LanguageCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "English", Tag = "en" });
        LanguageCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "Tiếng Việt", Tag = "vi" });
        LanguageCombo.SelectedIndex = _settings.Language == "vi" ? 1 : 0;

        // YouTube API
        YouTubeApiCheck.IsChecked = _settings.EnableYouTubeApi;
        YouTubeApiKeyPasswordBox.Password = _settings.YouTubeApiKey;
        YouTubeApiKeyTextBox.Text = _settings.YouTubeApiKey;
        YouTubeApiKeyRow.Visibility = _settings.EnableYouTubeApi ? Visibility.Visible : Visibility.Collapsed;
        UpdateYouTubeApiKeyStatus();

        _isLoadingSettings = false;
        ApplyLocalization();
    }

    private static string GetAppVersion()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.7.0";
    }

    private void ApplyLocalization()
    {
        // Header
        SettingsTitleText.Text = Loc.Get("settings.title");
        SettingsSubtitleText.Text = Loc.Get("settings.subtitle");

        // Section headers
        AppearanceHeader.Text = Loc.Get("settings.appearance");
        BehaviorHeader.Text = Loc.Get("settings.behavior");
        UpdatesHeader.Text = Loc.Get("settings.updates");
        DisplayHeader.Text = Loc.Get("settings.display");
        SystemHeader.Text = Loc.Get("settings.system");

        // Appearance labels & hints
        WidthLabel.Text = Loc.Get("settings.width");
        WidthSlider.Label = Loc.Get("settings.width");
        WidthSlider.Description = Loc.Get("settings.width.hint");
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
        EnableYouTubeSubtitlesHint.Text = Loc.Get("settings.enableYouTubeSubtitles.hint");

        SubtitlePriorityLabel.Text = Loc.Get("settings.subtitlePriority");
        SubtitlePriorityHint.Text = Loc.Get("settings.subtitlePriority.hint");
        LoadSubtitlePriority(); // Refresh display names for new language

        DynamicIslandModeCheck.Content = Loc.Get("settings.dynamicIslandMode");
        DynamicIslandModeHint.Text = Loc.Get("settings.dynamicIslandMode.hint");

        // Behavior labels & hints
        HoverExpandCheck.Content = Loc.Get("settings.hoverExpand");
        HoverExpandHint.Text = Loc.Get("settings.hoverExpand.hint");
        ExpandDelayLabel.Text = Loc.Get("settings.expandDelay");
        HoverDelaySlider.Label = Loc.Get("settings.expandDelay");
        HoverDelaySlider.Description = Loc.Get("settings.expandDelay.hint");
        DisableMouseLeaveAutoCloseCheck.Content = Loc.Get("settings.disableAutoClose");
        DisableMouseLeaveAutoCloseHint.Text = Loc.Get("settings.disableAutoClose.hint");

        // Updates & Report Bug
        CheckUpdateButton.Content = Loc.Get("settings.checkUpdate");
        UpdateStatusText.Text = Loc.Get("settings.upToDate");
        CurrentVersionText.Text = Loc.Get("settings.currentVersion", GetAppVersion());
        ReportBugLabel.Text = Loc.Get("settings.reportBug");
        ReportBugHint.Text = Loc.Get("settings.reportBug.hint");
        RequestFeatureLabel.Text = Loc.Get("settings.requestFeature");
        RequestFeatureHint.Text = Loc.Get("settings.requestFeature.hint");
        ClearCacheLabel.Text = Loc.Get("settings.clearCache");
        ClearCacheHint.Text = Loc.Get("settings.clearCache.hint");

        // Display
        MonitorLabel.Text = Loc.Get("settings.activeMonitor");
        MonitorHint.Text = Loc.Get("settings.activeMonitor.hint");

        // Footer buttons
        ResetButton.Content = Loc.Get("settings.btn.reset");
        ApplyButton.Content = Loc.Get("settings.btn.apply");
        SaveButton.Content = Loc.Get("settings.btn.save");

        // System checkboxes & hints
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
        LanguageLabel.Text = Loc.Get("settings.language");
        LanguageHint.Text = Loc.Get("settings.language.hint");

        // Advanced
        AdvancedHeader.Text = Loc.Get("settings.advanced");
        YouTubeApiCheck.Content = Loc.Get("settings.youtubeApi");
        YouTubeApiHint.Text = Loc.Get("settings.youtubeApi.hint");
        YouTubeApiKeyLabel.Text = Loc.Get("settings.youtubeApiKey");
        YouTubeApiKeyHint.Text = Loc.Get("settings.youtubeApiKey.hint");
    }

    #region Slider Value Changed Handlers

    private void WidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (WidthValue != null)
            WidthValue.Text = ((int)e.NewValue).ToString();
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
        bool isVi = _settings.Language == "vi";

        var keys = (_settings.SubtitlePriority ?? "native,english,auto")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Ensure all 3 modes are present
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
                DisplayName = GetSubtitleModeName(key, isVi)
            });
        }

        SubtitlePriorityItems.ItemsSource = _subtitleItems;
    }

    private static string GetSubtitleModeName(string key, bool isVi) => key switch
    {
        "native" => isVi ? "Ngôn ngữ gốc video" : "Video's native language",
        "english" => isVi ? "Tiếng Anh" : "English",
        "auto" => isVi ? "Tự động (bất kỳ)" : "Auto (any available)",
        _ => key
    };

    private void SubtitlePriorityItem_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _subtitleDragStart = e.GetPosition(null);
        _subtitleIsDragging = false;
        e.Handled = true; // Prevent window DragMove from intercepting
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

        // Find drop target
        var dropPos = e.GetPosition(SubtitlePriorityItems);
        int newIndex = GetSubtitleDropIndex(dropPos);

        int oldIndex = _subtitleItems.IndexOf(draggedItem);
        if (oldIndex < 0 || oldIndex == newIndex) return;

        // Capture positions before move for animation
        var positions = new Dictionary<SubtitlePriorityItem, double>();
        for (int i = 0; i < _subtitleItems.Count; i++)
        {
            var container = SubtitlePriorityItems.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
            if (container != null)
                positions[_subtitleItems[i]] = container.TranslatePoint(new Point(0, 0), SubtitlePriorityItems).Y;
        }

        _subtitleItems.Move(oldIndex, newIndex);

        // Animate items sliding to new positions
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
                    translate.BeginAnimation(TranslateTransform.YProperty, anim);
                }

                // Scale pop on the moved item
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
                        sc.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
                        sc.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
                    }
                }
            }
        });

        // Persist immediately so the new priority takes effect right away
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
        if (_isLoadingSettings) return;
        PushLivePreview();
    }

    private void UpdateLyricsDependentControls(bool lyricsEnabled)
    {
        // Dim & disable the dark overlay slider when lyrics are off, since it only affects the lyrics background.
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
        // Dim & block the Subtitle Language section when YouTube subtitles are off,
        // since the language priority only applies to YouTube captions.
        if (SubtitlePriorityRow == null) return;

        SubtitlePriorityRow.Opacity = subtitlesEnabled ? 1.0 : 0.45;
        SubtitlePriorityRow.IsEnabled = subtitlesEnabled;
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

    private void YouTubeApiCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        bool enabled = YouTubeApiCheck.IsChecked ?? false;
        YouTubeApiKeyRow.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void YouTubeApiKeyPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        // Sync to hidden TextBox
        if (YouTubeApiKeyTextBox.Visibility == Visibility.Collapsed)
            YouTubeApiKeyTextBox.Text = YouTubeApiKeyPasswordBox.Password;
        UpdateYouTubeApiKeyStatus();
    }

    private void YouTubeApiKeyTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_isLoadingSettings) return;
        // Sync to PasswordBox
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

            // Animate: eye open → eye closed
            var fadeOutOpen = new DoubleAnimation(1, 0, duration) { EasingFunction = easeOut };
            var fadeInClosed = new DoubleAnimation(0, 1, duration) { EasingFunction = easeOut, BeginTime = TimeSpan.FromMilliseconds(100) };
            EyeOpenIcon.BeginAnimation(OpacityProperty, fadeOutOpen);
            EyeClosedIcon.BeginAnimation(OpacityProperty, fadeInClosed);
        }
        else
        {
            YouTubeApiKeyPasswordBox.Password = YouTubeApiKeyTextBox.Text;
            YouTubeApiKeyTextBox.Visibility = Visibility.Collapsed;
            YouTubeApiKeyPasswordBox.Visibility = Visibility.Visible;

            // Animate: eye closed → eye open
            var fadeOutClosed = new DoubleAnimation(1, 0, duration) { EasingFunction = easeOut };
            var fadeInOpen = new DoubleAnimation(0, 1, duration) { EasingFunction = easeOut, BeginTime = TimeSpan.FromMilliseconds(100) };
            EyeClosedIcon.BeginAnimation(OpacityProperty, fadeOutClosed);
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
        const int fps = 30;
        const double slideDist = 3.0;
        int staggerMs = 0;
        const int staggerStep = 20;

        // Collect all text elements that need animated update
        var textUpdates = new (FrameworkElement element, Action update)[]
        {
            // Header
            (SettingsTitleText, () => SettingsTitleText.Text = Loc.Get("settings.title")),
            (SettingsSubtitleText, () => SettingsSubtitleText.Text = Loc.Get("settings.subtitle")),

            // Section headers
            (AppearanceHeader, () => AppearanceHeader.Text = Loc.Get("settings.appearance")),
            (BehaviorHeader, () => BehaviorHeader.Text = Loc.Get("settings.behavior")),
            (UpdatesHeader, () => UpdatesHeader.Text = Loc.Get("settings.updates")),
            (DisplayHeader, () => DisplayHeader.Text = Loc.Get("settings.display")),
            (SystemHeader, () => SystemHeader.Text = Loc.Get("settings.system")),

            // Appearance
            (WidthLabel, () => { WidthLabel.Text = Loc.Get("settings.width"); WidthSlider.Label = Loc.Get("settings.width"); WidthSlider.Description = Loc.Get("settings.width.hint"); }),
            (HeightLabel, () => { HeightLabel.Text = Loc.Get("settings.height"); HeightSlider.Label = Loc.Get("settings.height"); HeightSlider.Description = Loc.Get("settings.height.hint"); }),
            (RadiusLabel, () => { RadiusLabel.Text = Loc.Get("settings.cornerRadius"); RadiusSlider.Label = Loc.Get("settings.cornerRadius"); RadiusSlider.Description = Loc.Get("settings.cornerRadius.hint"); }),
            (OpacityLabel, () => { OpacityLabel.Text = Loc.Get("settings.opacity"); OpacitySlider.Label = Loc.Get("settings.opacity"); OpacitySlider.Description = Loc.Get("settings.opacity.hint"); }),
            (BlurLabel, () => { BlurLabel.Text = Loc.Get("settings.blurBrightness"); BlurBrightnessSlider.Label = Loc.Get("settings.blurBrightness"); BlurBrightnessSlider.Description = Loc.Get("settings.blurBrightness.hint"); }),
            (DarkOverlayLabel, () => { DarkOverlayLabel.Text = Loc.Get("settings.lyricsDarkOverlay"); BlurDarkOverlaySlider.Label = Loc.Get("settings.lyricsDarkOverlay"); BlurDarkOverlaySlider.Description = Loc.Get("settings.lyricsDarkOverlay.hint"); }),
            (EnableSpotifyLyricsHint, () => EnableSpotifyLyricsHint.Text = Loc.Get("settings.enableSpotifyLyrics.hint")),
            (EnableYouTubeSubtitlesHint, () => EnableYouTubeSubtitlesHint.Text = Loc.Get("settings.enableYouTubeSubtitles.hint")),
            (DynamicIslandModeHint, () => DynamicIslandModeHint.Text = Loc.Get("settings.dynamicIslandMode.hint")),

            // Behavior
            (HoverExpandHint, () => HoverExpandHint.Text = Loc.Get("settings.hoverExpand.hint")),
            (ExpandDelayLabel, () => { ExpandDelayLabel.Text = Loc.Get("settings.expandDelay"); HoverDelaySlider.Label = Loc.Get("settings.expandDelay"); HoverDelaySlider.Description = Loc.Get("settings.expandDelay.hint"); }),
            (DisableMouseLeaveAutoCloseHint, () => DisableMouseLeaveAutoCloseHint.Text = Loc.Get("settings.disableAutoClose.hint")),

            // Updates
            (UpdateStatusText, () => UpdateStatusText.Text = Loc.Get("settings.upToDate")),
            (CurrentVersionText, () => CurrentVersionText.Text = Loc.Get("settings.currentVersion", GetAppVersion())),
            (ReportBugLabel, () => ReportBugLabel.Text = Loc.Get("settings.reportBug")),
            (ReportBugHint, () => ReportBugHint.Text = Loc.Get("settings.reportBug.hint")),
            (RequestFeatureLabel, () => RequestFeatureLabel.Text = Loc.Get("settings.requestFeature")),
            (RequestFeatureHint, () => RequestFeatureHint.Text = Loc.Get("settings.requestFeature.hint")),
            (ClearCacheLabel, () => ClearCacheLabel.Text = Loc.Get("settings.clearCache")),
            (ClearCacheHint, () => ClearCacheHint.Text = Loc.Get("settings.clearCache.hint")),

            // Display
            (MonitorLabel, () => MonitorLabel.Text = Loc.Get("settings.activeMonitor")),
            (MonitorHint, () => MonitorHint.Text = Loc.Get("settings.activeMonitor.hint")),

            // System
            (AutoStartHint, () => AutoStartHint.Text = Loc.Get("settings.autoStart.hint")),
            (HelloGreetingHint, () => HelloGreetingHint.Text = Loc.Get("settings.helloGreeting.hint")),
            (HideOnExclusiveFullscreenHint, () => HideOnExclusiveFullscreenHint.Text = Loc.Get("settings.hideExclusiveFs.hint")),
            (HideOnWindowedFullscreenHint, () => HideOnWindowedFullscreenHint.Text = Loc.Get("settings.hideWindowedFs.hint")),
            (MusicNotifyHint, () => MusicNotifyHint.Text = Loc.Get("settings.musicNotify.hint")),
            (SystemNotifyHint, () => SystemNotifyHint.Text = Loc.Get("settings.systemNotify.hint")),
            (ShelfUnlockHint, () => ShelfUnlockHint.Text = Loc.Get("settings.shelfUnlock.hint")),
            (LanguageLabel, () => LanguageLabel.Text = Loc.Get("settings.language")),
            (LanguageHint, () => LanguageHint.Text = Loc.Get("settings.language.hint")),

            // Advanced
            (AdvancedHeader, () => AdvancedHeader.Text = Loc.Get("settings.advanced")),
            (YouTubeApiHint, () => YouTubeApiHint.Text = Loc.Get("settings.youtubeApi.hint")),
            (YouTubeApiKeyLabel, () => YouTubeApiKeyLabel.Text = Loc.Get("settings.youtubeApiKey")),
            (YouTubeApiKeyHint, () => YouTubeApiKeyHint.Text = Loc.Get("settings.youtubeApiKey.hint")),
        };

        // Also update buttons (ContentControl-based, animate parent)
        AnimateContentChange(CheckUpdateButton, () => CheckUpdateButton.Content = Loc.Get("settings.checkUpdate"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(ResetButton, () => ResetButton.Content = Loc.Get("settings.btn.reset"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(ApplyButton, () => ApplyButton.Content = Loc.Get("settings.btn.apply"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(SaveButton, () => SaveButton.Content = Loc.Get("settings.btn.save"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;

        // Checkboxes (Content property)
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
        AnimateContentChange(YouTubeApiCheck, () => YouTubeApiCheck.Content = Loc.Get("settings.youtubeApi"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(HoverExpandCheck, () => HoverExpandCheck.Content = Loc.Get("settings.hoverExpand"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(DisableMouseLeaveAutoCloseCheck, () => DisableMouseLeaveAutoCloseCheck.Content = Loc.Get("settings.disableAutoClose"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(EnableSpotifyLyricsCheck, () => EnableSpotifyLyricsCheck.Content = Loc.Get("settings.enableSpotifyLyrics"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(EnableYouTubeSubtitlesCheck, () => EnableYouTubeSubtitlesLabel.Text = Loc.Get("settings.enableYouTubeSubtitles"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(DynamicIslandModeCheck, () => DynamicIslandModeCheck.Content = Loc.Get("settings.dynamicIslandMode"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;

        foreach (var (element, update) in textUpdates)
        {
            if (element == null) continue;
            AnimateTextSwap(element, update, staggerMs, easeOut, fps, slideDist);
            staggerMs += staggerStep;
        }
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

        // Phase 1: Blur out — fade + slide right + slight scale feel via X offset
        var fadeOut = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(120),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            BeginTime = TimeSpan.FromMilliseconds(delayMs)
        };
        Timeline.SetDesiredFrameRate(fadeOut, fps);

        var slideOut = new DoubleAnimation
        {
            To = -14,
            Duration = TimeSpan.FromMilliseconds(120),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            BeginTime = TimeSpan.FromMilliseconds(delayMs)
        };
        Timeline.SetDesiredFrameRate(slideOut, fps);

        fadeOut.Completed += (s, e) =>
        {
            updateText();

            // Phase 2: Slide in from right with overshoot spring
            translate.X = 18;

            var fadeIn = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(280),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Timeline.SetDesiredFrameRate(fadeIn, fps);

            var slideIn = new DoubleAnimation
            {
                From = 18,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(380),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Timeline.SetDesiredFrameRate(slideIn, fps);

            slideIn.Completed += (s2, e2) =>
            {
                // Clear animation and snap to final position to prevent residual offset
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
                Interval = TimeSpan.FromMilliseconds(16)
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
        const int fps = 144;

        var totalDur = TimeSpan.FromMilliseconds(650);

        // Get actual notch dimensions from owner
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

        // Calculate scale factors: notch size / settings shell size
        double shellWidth = ActualWidth > 0 ? ActualWidth - 36 : 824; // 860 - 2*18 margin
        double shellHeight = ActualHeight > 0 ? ActualHeight - 36 : 584;
        double startScaleX = Math.Max(0.02, notchW / shellWidth);
        double startScaleY = Math.Max(0.02, notchH / shellHeight);
        double startRadius = Math.Max(notchRadius, 12);

        // --- Start from notch-sized state ---
        MainShell.Opacity = 1.0;
        MainShell.RenderTransformOrigin = new Point(0.5, 0.0);
        MainShell.Effect = null;
        ShellScale.ScaleX = startScaleX;
        ShellScale.ScaleY = startScaleY;
        ShellTranslate.Y = 0;
        MainShell.CornerRadius = new CornerRadius(startRadius);
        FooterBar.CornerRadius = new CornerRadius(0, 0, startRadius, startRadius);

        // Save final position
        double finalLeft = Left;
        double finalTop = Top;

        // Start position: aligned with notch
        Left = notchLeft + notchW / 2.0 - ActualWidth / 2.0;
        Top = notchTop;

        // --- Expand ScaleX ---
        var expandX = new DoubleAnimation(startScaleX, 1.0, totalDur)
        {
            EasingFunction = easeOutStrong
        };
        Timeline.SetDesiredFrameRate(expandX, fps);

        // --- Expand ScaleY ---
        var expandY = new DoubleAnimation(startScaleY, 1.0, totalDur)
        {
            EasingFunction = easeOutStrong
        };
        Timeline.SetDesiredFrameRate(expandY, fps);

        // --- CornerRadius: from notch radius to 24 ---
        _shellCornerRadius = startRadius;
        var cornerAnim = new DoubleAnimation(startRadius, 24, totalDur)
        {
            EasingFunction = easeOut
        };
        Timeline.SetDesiredFrameRate(cornerAnim, fps);

        // --- Move window from notch to final position ---
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

        // Restore drop shadow after expand completes
        expandX.Completed += (s, e) =>
        {
            MainShell.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = System.Windows.Media.Colors.Black,
                BlurRadius = 30,
                ShadowDepth = 0,
                Opacity = 0.42
            };
            MainShell.RenderTransformOrigin = new Point(0.5, 0.5);
        };

        // Start all animations
        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, expandX);
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, expandY);
        this.BeginAnimation(ShellCornerRadiusProperty, cornerAnim);
        this.BeginAnimation(TopProperty, moveTop);
        this.BeginAnimation(LeftProperty, moveLeft);

        // --- Staggered content reveal ---
        int contentDelay = 250;
        AnimateEntranceItem(SettingsHeader, HeaderTranslate, contentDelay);

        // Social icons stagger (appear shortly after header)
        int socialDelay = contentDelay + 80;
        AnimateSocialIcon(SocialGitHub, SocialGitHubTranslate, socialDelay);
        AnimateSocialIcon(SocialFacebook, SocialFacebookTranslate, socialDelay + 60);
        AnimateSocialIcon(SocialDiscord, SocialDiscordTranslate, socialDelay + 120);

        AnimateEntranceItem(AppearanceCard, AppearanceCardTranslate, contentDelay + 40);
        AnimateEntranceItem(BehaviorCard, BehaviorCardTranslate, contentDelay + 80);
        AnimateEntranceItem(DisplayCard, DisplayCardTranslate, contentDelay + 120);
        AnimateEntranceItem(SystemCard, SystemCardTranslate, contentDelay + 160);
        AnimateEntranceItem(AdvancedCard, AdvancedCardTranslate, contentDelay + 200);
        AnimateEntranceItem(UpdatesCard, UpdatesCardTranslate, contentDelay + 240);
        AnimateEntranceItem(FooterBar, FooterTranslate, contentDelay + 280);

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

        Timeline.SetDesiredFrameRate(animation, 144);
        return animation;
    }

    #endregion

    #region Button Handlers

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            Loc.Get("settings.reset.confirm"),
            Loc.Get("settings.reset.title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        var defaults = new NotchSettings();

        WidthSlider.Value = defaults.Width;
        HeightSlider.Value = defaults.Height;
        RadiusSlider.Value = defaults.CornerRadius;
        OpacitySlider.Value = defaults.Opacity * 100;
        BlurBrightnessSlider.Value = defaults.MediaBlurBrightnessBoost * 100;
        BlurDarkOverlaySlider.Value = defaults.MediaBlurDarkOverlay * 100;
        EnableSpotifyLyricsCheck.IsChecked = defaults.EnableSpotifyLyrics;
        EnableYouTubeSubtitlesCheck.IsChecked = defaults.EnableYouTubeSubtitles;
        UpdateLyricsDependentControls(defaults.EnableSpotifyLyrics);
        UpdateYouTubeSubtitlesDependentControls(defaults.EnableYouTubeSubtitles);

        _settings.SubtitlePriority = defaults.SubtitlePriority;
        LoadSubtitlePriority();

        DynamicIslandModeCheck.IsChecked = defaults.EnableDynamicIslandMode;

        HoverExpandCheck.IsChecked = defaults.EnableHoverExpand;
        HoverDelaySlider.Value = defaults.HoverExpandDelay;
        HoverDelaySlider.IsEnabled = defaults.EnableHoverExpand;
        HoverDelaySlider.Opacity = defaults.EnableHoverExpand ? 1.0 : 0.4;
        DisableMouseLeaveAutoCloseCheck.IsChecked = defaults.DisableMouseLeaveAutoClose;

        MusicNotifyCheck.IsChecked = defaults.ShowMusicNotifications;
        SystemNotifyCheck.IsChecked = defaults.ShowSystemNotifications;
        ShelfUnlockCheck.IsChecked = defaults.IsShelfUploadLimitUnlocked;
        HideOnExclusiveFullscreenCheck.IsChecked = defaults.HideOnExclusiveFullscreen;
        HideOnWindowedFullscreenCheck.IsChecked = defaults.HideOnWindowedFullscreen;
        LanguageCombo.SelectedIndex = defaults.Language == "vi" ? 1 : 0;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // If the user dragged sliders (live preview), revert the notch to the last-persisted state before closing.
        RevertLivePreviewIfNeeded();
        CloseWithAnimation();
    }
private void RevertLivePreviewIfNeeded()
    {
        SettingsChanged?.Invoke(this, _originalSettings.Clone());
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
        int deletedCount = 0;
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "V-Notch");
        var baseDir = AppContext.BaseDirectory;

        // Files to delete (cache/logs, NOT settings.json)
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
            catch { /* skip locked files */ }
        }

        // Also delete any .corrupt backup files in appData
        try
        {
            foreach (var corrupt in Directory.GetFiles(appData, "settings.corrupt-*.json"))
            {
                try { File.Delete(corrupt); deletedCount++; } catch { }
            }
        }
        catch { }

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
        // Prevent double-trigger
        if (_isClosing) return;
        _isClosing = true;

        // Fire event so the notch can start its absorb animation in sync
        AnimatedClosing?.Invoke(this, EventArgs.Empty);

        var easeIn = new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = 6 };
        var easeInStrong = new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = 7 };
        var itemEase = new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = 5 };
        const int fps = 144;

        var totalDur = TimeSpan.FromMilliseconds(650);

        // Get the actual rendered window position using Win32 (WPF Top/Left may return stale values when animations are active)
        var windowInteropHelper = new System.Windows.Interop.WindowInteropHelper(this);
        var hwnd = windowInteropHelper.Handle;
        double currentTop = Top;
        double currentLeft = Left;
        if (hwnd != IntPtr.Zero)
        {
            VNotch.Services.Win32Interop.GetWindowRect(hwnd, out var rect);
            // Convert screen pixels to WPF units
            var source = PresentationSource.FromVisual(this);
            double dpiX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
            double dpiY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;
            currentTop = rect.Top * dpiY;
            currentLeft = rect.Left * dpiX;
        }

        // Clear any lingering animations
        MainShell.BeginAnimation(OpacityProperty, null);
        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ShellTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        this.BeginAnimation(TopProperty, null);
        this.BeginAnimation(LeftProperty, null);
        this.BeginAnimation(ShellCornerRadiusProperty, null);

        // Restore position to where the window actually is on screen
        Top = currentTop;
        Left = currentLeft;

        // Snap to current state
        MainShell.Opacity = 1.0;
        ShellScale.ScaleX = 1.0;
        ShellScale.ScaleY = 1.0;
        ShellTranslate.Y = 0;

        // Anchor at top-center (same as entrance)
        MainShell.RenderTransformOrigin = new Point(0.5, 0.0);

        // Remove drop shadow during animation
        MainShell.Effect = null;

        // --- Staggered content hide (reverse of entrance reveal) ---
        AnimateExitItem(FooterBar, FooterTranslate, 0);
        AnimateExitItem(UpdatesCard, UpdatesCardTranslate, 20);
        AnimateExitItem(AdvancedCard, AdvancedCardTranslate, 40);
        AnimateExitItem(SystemCard, SystemCardTranslate, 60);
        AnimateExitItem(DisplayCard, DisplayCardTranslate, 80);
        AnimateExitItem(BehaviorCard, BehaviorCardTranslate, 100);
        AnimateExitItem(AppearanceCard, AppearanceCardTranslate, 120);
        AnimateExitItem(SettingsHeader, HeaderTranslate, 140);

        // --- CornerRadius: 24 → notch radius ---
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

        // --- ScaleX: 1.0 → notch scale ---
        var squishX = new DoubleAnimation(1.0, targetScaleX, totalDur)
        {
            EasingFunction = easeInStrong
        };
        Timeline.SetDesiredFrameRate(squishX, fps);

        // --- ScaleY: 1.0 → notch scale ---
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

        // --- Move window back to notch position ---
        double targetLeft = notchLeft + notchW / 2.0 - ActualWidth / 2.0;
        double targetTop = notchTop;

        var flyUpWindow = new DoubleAnimation(Top, targetTop, totalDur)
        {
            EasingFunction = easeInStrong
        };
        Timeline.SetDesiredFrameRate(flyUpWindow, fps);

        var flyLeftWindow = new DoubleAnimation(Left, targetLeft, totalDur)
        {
            EasingFunction = easeInStrong
        };
        Timeline.SetDesiredFrameRate(flyLeftWindow, fps);

        // Close after animation completes
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
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = itemEase,
                BeginTime = TimeSpan.FromMilliseconds(delayMs)
            };
            Timeline.SetDesiredFrameRate(fade, fps);
            element.BeginAnimation(OpacityProperty, fade);

            var slide = new DoubleAnimation(0, 12, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = itemEase,
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
        _settings.Height = (int)HeightSlider.Value;
        _settings.CornerRadius = (int)RadiusSlider.Value;
        _settings.Opacity = OpacitySlider.Value / 100.0;
        _settings.MediaBlurBrightnessBoost = BlurBrightnessSlider.Value / 100.0;
        _settings.MediaBlurDarkOverlay = BlurDarkOverlaySlider.Value / 100.0;
        _settings.EnableSpotifyLyrics = EnableSpotifyLyricsCheck.IsChecked ?? true;
        _settings.EnableYouTubeSubtitles = EnableYouTubeSubtitlesCheck.IsChecked ?? true;

        // Sync subtitle mode from ComboBox
        _settings.SubtitlePriority = GetSubtitlePriorityString();

        _settings.EnableDynamicIslandMode = DynamicIslandModeCheck.IsChecked ?? false;

        _settings.EnableHoverExpand = HoverExpandCheck.IsChecked ?? true;
        _settings.HoverExpandDelay = (int)HoverDelaySlider.Value;
        _settings.DisableMouseLeaveAutoClose = DisableMouseLeaveAutoCloseCheck.IsChecked ?? false;

        _settings.MonitorIndex = MonitorCombo.SelectedIndex;
        if (CameraCombo.SelectedItem is CameraDeviceItem selectedCamera)
            _settings.CameraDeviceId = selectedCamera.Id;
        _settings.AutoStart = AutoStartCheck.IsChecked ?? false;
        _settings.EnableHelloGreeting = HelloGreetingCheck.IsChecked ?? true;
        _settings.HideOnExclusiveFullscreen = HideOnExclusiveFullscreenCheck.IsChecked ?? true;
        _settings.HideOnWindowedFullscreen = HideOnWindowedFullscreenCheck.IsChecked ?? true;
        _settings.ShowMusicNotifications = MusicNotifyCheck.IsChecked ?? true;
        _settings.ShowSystemNotifications = SystemNotifyCheck.IsChecked ?? true;
        _settings.IsShelfUploadLimitUnlocked = ShelfUnlockCheck.IsChecked ?? false;

        // YouTube API
        _settings.EnableYouTubeApi = YouTubeApiCheck.IsChecked ?? false;
        _settings.YouTubeApiKey = YouTubeApiKeyPasswordBox.Password?.Trim() ?? "";

        // Language
        if (LanguageCombo.SelectedItem is System.Windows.Controls.ComboBoxItem langItem && langItem.Tag is string langCode)
            _settings.Language = langCode;

        if (persist)
        {
            _settingsService.Save(_settings);
            StartupManager.SetAutoStart(_settings.AutoStart);
            // Refresh baseline so Cancel after Apply doesn't revert persisted state.
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

    #endregion

    #region Smooth Scroll

    private double _scrollVelocity;
    private double _scrollTarget;
    private bool _isScrollAnimating;
    private const double ScrollFriction = 0.85;
    private const double ScrollSensitivity = 1.2;
    private const double ScrollMinVelocity = 0.5;

    private bool IsAnyComboBoxDropDownOpen()
    {
        return MonitorCombo.IsDropDownOpen || LanguageCombo.IsDropDownOpen || CameraCombo.IsDropDownOpen;
    }

    private void SettingsScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // If a ComboBox dropdown is open, don't intercept scroll - let the dropdown handle it
        if (IsAnyComboBoxDropDownOpen())
        {
            return;
        }

        e.Handled = true;

        double delta = -e.Delta * ScrollSensitivity;

        if (!_isScrollAnimating)
        {
            _scrollTarget = SettingsScrollViewer.VerticalOffset;
        }

        // Add velocity based on scroll direction
        _scrollVelocity += delta * 0.3;
        _scrollTarget += delta;

        // Clamp target to valid range
        double maxScroll = SettingsScrollViewer.ScrollableHeight;
        _scrollTarget = Math.Clamp(_scrollTarget, 0, maxScroll);

        if (!_isScrollAnimating)
        {
            _isScrollAnimating = true;
            CompositionTarget.Rendering += SmoothScroll_Tick;
        }
    }

    private void SettingsScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Don't close ComboBoxes from scroll changes triggered by smooth scroll animation
    }

    private void SmoothScroll_Tick(object? sender, EventArgs e)
    {
        double current = SettingsScrollViewer.VerticalOffset;
        double diff = _scrollTarget - current;

        // Apply velocity with friction
        _scrollVelocity *= ScrollFriction;

        // Lerp towards target
        double step = diff * 0.15 + _scrollVelocity * 0.5;
        double newOffset = current + step;

        // Clamp
        newOffset = Math.Clamp(newOffset, 0, SettingsScrollViewer.ScrollableHeight);
        SettingsScrollViewer.ScrollToVerticalOffset(newOffset);

        // Stop when close enough and velocity is negligible
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

    private async void LoadCameraDevices()
    {
        try
        {
            var groups = await Windows.Media.Capture.Frames.MediaFrameSourceGroup.FindAllAsync();
            var cameras = groups
                .Where(g => g.SourceInfos.Any(s => s.SourceKind == Windows.Media.Capture.Frames.MediaFrameSourceKind.Color))
                .Select(g => new CameraDeviceItem { Id = g.Id, Name = g.DisplayName })
                .ToList();

            if (cameras.Count == 0)
            {
                cameras.Add(new CameraDeviceItem { Id = "", Name = "No camera detected" });
            }

            CameraCombo.ItemsSource = cameras;
            CameraCombo.DisplayMemberPath = "Name";

            var selectedIdx = cameras.FindIndex(c => c.Id == _settings.CameraDeviceId);
            CameraCombo.SelectedIndex = selectedIdx >= 0 ? selectedIdx : 0;
        }
        catch
        {
            CameraCombo.ItemsSource = new[] { new CameraDeviceItem { Id = "", Name = "No camera detected" } };
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
}

public class CameraDeviceItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}
