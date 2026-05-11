using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media;
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

    /// <summary>
    /// Fired at the start of the close animation so the owner notch can react
    /// before the window actually disappears.
    /// </summary>
    public event EventHandler? AnimatedClosing;

    public SettingsWindow(NotchSettings settings, SettingsService settingsService)
    {
        InitializeComponent();

        _settings = settings.Clone();
        _settingsService = settingsService;
        _updateService = new UpdateService();

        LoadSettings();
        _ = CheckForUpdatesAsync();
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
            if (source is ButtonBase or Slider or Thumb or ComboBox or ComboBoxItem or CheckBox or ScrollBar or TextBox)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void LoadSettings()
    {

        WidthSlider.Value = _settings.Width;
        HeightSlider.Value = _settings.Height;
        RadiusSlider.Value = _settings.CornerRadius;
        OpacitySlider.Value = _settings.Opacity * 100;
        BlurBrightnessSlider.Value = _settings.MediaBlurBrightnessBoost * 100;

        HoverExpandCheck.IsChecked = _settings.EnableHoverExpand;
        HoverDelaySlider.Value = _settings.HoverExpandDelay;

        var monitors = NotchManager.GetMonitorNames();
        MonitorCombo.ItemsSource = monitors;
        MonitorCombo.SelectedIndex = Math.Min(_settings.MonitorIndex, monitors.Length - 1);

        AutoStartCheck.IsChecked = StartupManager.IsAutoStartEnabled();
        MusicNotifyCheck.IsChecked = _settings.ShowMusicNotifications;
        SystemNotifyCheck.IsChecked = _settings.ShowSystemNotifications;
        ShelfUnlockCheck.IsChecked = _settings.IsShelfUploadLimitUnlocked;
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

    private void BlurBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BlurBrightnessValue != null)
            BlurBrightnessValue.Text = ((int)e.NewValue).ToString();
    }

    private void HoverDelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (HoverDelayValue != null)
            HoverDelayValue.Text = ((int)e.NewValue).ToString();
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
        AnimateEntranceItem(UpdatesCard, UpdatesCardTranslate, contentDelay + 200);
        AnimateEntranceItem(FooterBar, FooterTranslate, contentDelay + 240);

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

        var defaults = new NotchSettings();

        WidthSlider.Value = defaults.Width;
        HeightSlider.Value = defaults.Height;
        RadiusSlider.Value = defaults.CornerRadius;
        OpacitySlider.Value = defaults.Opacity * 100;
        BlurBrightnessSlider.Value = defaults.MediaBlurBrightnessBoost * 100;

        HoverExpandCheck.IsChecked = defaults.EnableHoverExpand;
        HoverDelaySlider.Value = defaults.HoverExpandDelay;

        MusicNotifyCheck.IsChecked = defaults.ShowMusicNotifications;
        SystemNotifyCheck.IsChecked = defaults.ShowSystemNotifications;
        ShelfUnlockCheck.IsChecked = defaults.IsShelfUploadLimitUnlocked;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CloseWithAnimation();
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

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        ApplySettingsFromUi();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ApplySettingsFromUi();
        CloseWithAnimation();
    }

    /// <summary>
    /// Plays the reverse of the entrance animation — collapses back into the notch.
    /// </summary>
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

        // Get the actual rendered window position using Win32
        // (WPF Top/Left may return stale values when animations are active)
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
        AnimateExitItem(SystemCard, SystemCardTranslate, 40);
        AnimateExitItem(DisplayCard, DisplayCardTranslate, 60);
        AnimateExitItem(BehaviorCard, BehaviorCardTranslate, 80);
        AnimateExitItem(AppearanceCard, AppearanceCardTranslate, 100);
        AnimateExitItem(SettingsHeader, HeaderTranslate, 120);

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

    /// <summary>
    /// Dependency property to animate MainShell's CornerRadius (all corners uniform).
    /// </summary>
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

    private void ApplySettingsFromUi()
    {
        _settings.Width = (int)WidthSlider.Value;
        _settings.Height = (int)HeightSlider.Value;
        _settings.CornerRadius = (int)RadiusSlider.Value;
        _settings.Opacity = OpacitySlider.Value / 100.0;
        _settings.MediaBlurBrightnessBoost = BlurBrightnessSlider.Value / 100.0;

        _settings.EnableHoverExpand = HoverExpandCheck.IsChecked ?? true;
        _settings.HoverExpandDelay = (int)HoverDelaySlider.Value;

        _settings.MonitorIndex = MonitorCombo.SelectedIndex;
        _settings.AutoStart = AutoStartCheck.IsChecked ?? false;
        _settings.ShowMusicNotifications = MusicNotifyCheck.IsChecked ?? true;
        _settings.ShowSystemNotifications = SystemNotifyCheck.IsChecked ?? true;
        _settings.IsShelfUploadLimitUnlocked = ShelfUnlockCheck.IsChecked ?? false;

        _settingsService.Save(_settings);

        StartupManager.SetAutoStart(_settings.AutoStart);

        SettingsChanged?.Invoke(this, _settings);
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
                UpdateStatusText.Text = "Check for updates";
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
