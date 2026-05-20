using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using VNotch.Controllers;
using VNotch.Services;
using VNotch.Models;
using VNotch.Modules;
using VNotch.Contracts;
using static VNotch.Services.Win32Interop;
using static VNotch.Services.AnimationPrimitives;
using POINT = VNotch.Services.Win32Interop.POINT;
using RECT = VNotch.Services.Win32Interop.RECT;
using WINDOWPOS = VNotch.Services.Win32Interop.WINDOWPOS;
using MONITORINFO = VNotch.Services.Win32Interop.MONITORINFO;
using WINDOWPLACEMENT = VNotch.Services.Win32Interop.WINDOWPLACEMENT;
using WinEventDelegate = VNotch.Services.Win32Interop.WinEventDelegate;
using EnumWindowsProc = VNotch.Services.Win32Interop.EnumWindowsProc;
namespace VNotch;

public partial class MainWindow : Window
{
    #region Fields

    private readonly SettingsService _settingsService;
    private readonly NotchManager _notchManager;
    private readonly MediaDetectionService _mediaService;
    private readonly IUpdateService _updateService;
    private readonly DispatcherTimer _updateTimer;
    private readonly DispatcherTimer _updateCheckTimer;
    private readonly ZOrderManager _zOrderManager;
    private readonly DispatcherTimer _hoverCollapseTimer;
    private readonly DispatcherTimer _hoverThumbnailDelayTimer;
    private readonly DispatcherTimer _compactThumbnailHoverLeaveTimer;

    private bool _isDraggingVolume = false;
    private NotchSettings _settings;
    private bool _isNotchVisible = true;
    private bool _isHiddenByFullscreen = false;
    private IntPtr _hwnd;
    private HwndSource? _hwndSource;
    private DateTime _lastFullscreenCheckUtc = DateTime.MinValue;
    private DispatcherTimer? _fullscreenRecheckTimer;
    private bool _isTrayMenuOpen = false;

    private readonly BatteryModule _batteryModule;
    private readonly CalendarModule _calendarModule;
    private readonly BluetoothModule _bluetoothModule;
    private readonly PrivacyIndicatorModule _privacyModule;
    private readonly IModuleLifecycleManager _moduleHost;

    // ─── Notch State (centralized via NotchStateManager) ───
    private readonly NotchStateManager _notchState = new();

    // ─── Controllers (Phase 2-5 refactoring) ───
    private readonly NotchAnimationController _animController;
    private readonly MusicWidgetController _musicController;
    private readonly CameraPreviewController _cameraController;
    private readonly TimerManager _timerManager;

    // _isAnimating guards ALL animations (expand, collapse, view switch, file delete)
    private bool _isAnimating = false;

    // Logical state reads delegate to the state machine
    private bool _isExpanded
    {
        get => _notchState.IsExpanded;
        set {  }
    }

    private bool _isStartupLayoutReady = false;
    private bool _pendingStartupClickToggle = false;
    private double _collapsedWidth;
    private double _collapsedHeight;
    private double _expandedWidth = 480;
    private double _expandedHeight = 146;
    private double _cornerRadiusCollapsed;
    private double _cornerRadiusExpanded = 24;

    private int _fixedX = 0;
    private int _fixedY = 0;
    private int _windowWidth = 0;
    private int _windowHeight = 0;

    private MediaInfo? _currentMediaInfo;
    private bool _isMusicCompactMode = false;
    private bool _isCompactThumbnailHovered = false;
    private const double CompactThumbnailHoverExitMargin = 22.0;
    private DateTime _lastMediaActionTime = DateTime.MinValue;

    private readonly DispatcherTimer _progressTimer;

    private static readonly SolidColorBrush _brushCharging = CreateFrozenBrush(48, 209, 88);
    private static readonly SolidColorBrush _brushLowBattery = CreateFrozenBrush(255, 59, 48);
    private static readonly SolidColorBrush _brushWhite = CreateFrozenBrush(255, 255, 255);
    private static readonly SolidColorBrush _brushBlack = CreateFrozenBrush(0, 0, 0);
    private static readonly SolidColorBrush _brushTransparent = CreateFrozenBrush(0, 0, 0, 0);
    private static readonly SolidColorBrush _brushGray = CreateFrozenBrush(102, 102, 102);

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b, byte a = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private bool _calendarInitialized = false;
    private readonly TextBlock[] _calendarDayNames = new TextBlock[11];
    private readonly Border[] _calendarDayBorders = new Border[11];
    private readonly TextBlock[] _calendarDayNumbers = new TextBlock[11];

    private bool _isUpdateAvailable = false;
    private UpdateInfo? _availableUpdate = null;
    private bool _isUpdateInstalling = false;
    private DispatcherTimer? _updatePulseTimer;
    private DateTime _updatePulseStartedAtUtc = DateTime.MinValue;
    private bool _isUpdateTooltipOpen = false;
    private DateTime _suspendTopmostUntilUtc = DateTime.MinValue;
    
    private const int CalendarTotalDays = 11;   
    private const int CalendarVisibleDays = 3;  
    private const double CalendarCellWidth = 30.0; 
    private double _calendarScrollX = 0.0;      
    private int _currentCalendarCenterIdx = 5;  
    private double _calendarScrollAccumulator = 0; 
    private DateTime _lastCalendarScrollTime = DateTime.MinValue;
    private DateTime _lastCalendarUpdate = DateTime.Now;
    private bool _isMonthAnimating = false;
    private string _pendingMonthText = string.Empty;

    #endregion

    public MainWindow(
        ISettingsService settingsService,
        IMediaDetectionService mediaService,
        IUpdateService updateService,
        IModuleLifecycleManager moduleHost,
        BatteryModule batteryModule,
        CalendarModule calendarModule,
        BluetoothModule bluetoothModule,
        PrivacyIndicatorModule privacyIndicatorModule)
    {
        InitializeComponent();
        _settingsService = (SettingsService)settingsService;
        _settings = _settingsService.Load();
        _notchManager = new NotchManager(this, _settings);
        _mediaService = (MediaDetectionService)mediaService;
        _updateService = updateService;

        // Initialize controllers
        _animController = new NotchAnimationController(_notchState);
        _musicController = new MusicWidgetController(_notchState);
        _cameraController = new CameraPreviewController();
        _timerManager = new TimerManager(Dispatcher);
        _moduleHost = moduleHost;
        _batteryModule = batteryModule;
        _batteryModule.BatteryUpdated += BatteryModule_BatteryUpdated;

        _calendarModule = calendarModule;
        _calendarModule.CalendarUpdated += CalendarModule_CalendarUpdated;

        _bluetoothModule = bluetoothModule;
        _bluetoothModule.DeviceConnected += BluetoothModule_DeviceConnected;
        _bluetoothModule.DeviceDisconnected += BluetoothModule_DeviceDisconnected;

        _privacyModule = privacyIndicatorModule;
        _privacyModule.StateChanged += PrivacyModule_StateChanged;

        _collapsedWidth = _settings.Width;
        _collapsedHeight = _settings.Height;
        _cornerRadiusCollapsed = _settings.CornerRadius;

        _updateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _updateTimer.Tick += UpdateTimer_Tick;

        _updateCheckTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(90)
        };
        _updateCheckTimer.Tick += UpdateCheckTimer_Tick;

        _zOrderManager = new ZOrderManager(
            getHwnd: () => _hwnd,
            isEffectivelyVisible: () => IsEffectivelyNotchVisible,
            isSuspended: () => _isTrayMenuOpen || _isUpdateTooltipOpen || DateTime.UtcNow < _suspendTopmostUntilUtc,
            onForegroundChanged: OnForegroundWindowChanged);

        _progressTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(1000)
        };
        _progressTimer.Tick += ProgressTimer_Tick;
        InputMonitorService.MouseActionTriggered += GlobalMouseHook_MouseLeftButtonDown;

        _hoverCollapseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(_settings.HoverCollapseDelay)
        };
        _hoverCollapseTimer.Tick += (s, e) =>
        {
            _hoverCollapseTimer.Stop();
            if (_isExpanded && !NotchWrapper.IsMouseOver)
            {
                CollapseNotch();
            }
        };

        _hoverThumbnailDelayTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(_settings.HoverExpandDelay)
        };
        _hoverThumbnailDelayTimer.Tick += (s, e) =>
        {
            _hoverThumbnailDelayTimer.Stop();
            if (_settings.EnableHoverExpand && !_isExpanded && !_isAnimating)
            {
                ExpandNotch();
            }
            else if (!_settings.EnableHoverExpand && CompactThumbnailBorder.IsMouseOver)
            {
                SetCompactThumbnailHover(true);
            }
        };

        _compactThumbnailHoverLeaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(140)
        };
        _compactThumbnailHoverLeaveTimer.Tick += (s, e) =>
        {
            if (!_isExpanded && !_isAnimating && _isMusicCompactMode && IsCursorInsideCompactThumbnailExitZone())
            {
                return;
            }

            _compactThumbnailHoverLeaveTimer.Stop();
            SetCompactThumbnailHover(false);
        };

        _notchManager.HoverService.HoverEnter += HoverService_HoverEnter;
        _notchManager.HoverService.HoverLeave += HoverService_HoverLeave;

        Loaded += MainWindow_Loaded;
        Deactivated += MainWindow_Deactivated;

        _mediaService.MediaChanged += OnMediaChanged;

        InitializeFileShelfController();
    }

    #region Window Lifecycle

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);
        RegisterClipboardListener();

        try
        {
            var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logo.ico");
            if (System.IO.File.Exists(iconPath))
            {
                var icon = new System.Drawing.Icon(iconPath);
                TrayIcon.Icon = icon;

                this.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(iconPath));
            }
            else
            {
                TrayIcon.Icon = IconGenerator.CreateNotchIcon(16);
            }
        }
        catch
        {
            TrayIcon.Icon = IconGenerator.CreateNotchIcon(16);
        }

        ApplySettings();
        ConfigureOverlayWindow();
        PositionAtTop();
        StartZOrderWatchdog();
        UpdateFullscreenAutoHideState(GetForegroundWindow(), force: true);

        _updateTimer.Start();
        _updateCheckTimer.Start();
        CheckForUpdatesAsync().SafeFireAndForget("UPDATE-CHECK");

        if (IsEffectivelyNotchVisible)
        {
            PlayAppearAnimation();
        }

        // Start media service after layout is fully measured to avoid zero ActualWidth/Height on first update.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateLayout();
            UpdateNotchClip();
            UpdateMediaBackgroundFootprint();
            _isStartupLayoutReady = true;
            _pendingStartupClickToggle = false;

            _mediaService.Start();
            _moduleHost.StartAll();

            // Trim working set after startup to release pages back to OS.
            Task.Delay(3000).ContinueWith(_ =>
            {
                GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                GC.WaitForPendingFinalizers();
                TrimWorkingSet();
            }, TaskScheduler.Default);
        }), DispatcherPriority.ContextIdle);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_WINDOWPOSCHANGING:
                if (lParam != IntPtr.Zero && _fixedY >= 0)
                {
                    var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                    pos.y = _fixedY;
                    pos.x = _fixedX;
                    pos.hwndInsertAfter = HWND_TOPMOST;
                    Marshal.StructureToPtr(pos, lParam, false);
                }
                break;

            case WM_ACTIVATE:
                if (IsEffectivelyNotchVisible)
                {
                    SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
                break;

            case WM_ACTIVATEAPP:
                if (wParam == IntPtr.Zero)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_isExpanded && !_isAnimating)
                        {
                            CollapseNotch();
                        }
                    }));
                }
                break;

            case WM_DISPLAYCHANGE:
                Dispatcher.BeginInvoke(() => PositionAtTop());
                break;

            case WM_CLIPBOARDUPDATE:
                HandleClipboardUpdate();
                break;
        }

        return IntPtr.Zero;
    }

    private void MainWindow_Deactivated(object? sender, EventArgs e)
    {

        if ((_isExpanded || _isMusicExpanded) && !_isAnimating && !_isSecondaryView)
        {
            CollapseAll();
        }
    }

    private void CollapseAll()
    {
        if (_isMusicExpanded) CollapseMusicWidget();
        if (_isExpanded) CollapseNotch();
    }

    protected override void OnClosed(EventArgs e)
    {
        // Unsubscribe instance events
        _notchManager.HoverService.HoverEnter -= HoverService_HoverEnter;
        _notchManager.HoverService.HoverLeave -= HoverService_HoverLeave;
        _mediaService.MediaChanged -= OnMediaChanged;
        _batteryModule.BatteryUpdated -= BatteryModule_BatteryUpdated;
        _calendarModule.CalendarUpdated -= CalendarModule_CalendarUpdated;
        _privacyModule.StateChanged -= PrivacyModule_StateChanged;

        // Unsubscribe static events
        InputMonitorService.MouseActionTriggered -= GlobalMouseHook_MouseLeftButtonDown;

        UnregisterClipboardListener();
        _hwndSource?.RemoveHook(WndProc);
        StopZOrderWatchdog();
        StopTitleGradientShift();
        _progressTimer?.Stop();
        _mediaService?.Dispose();
        _lyricsService?.Dispose();
        _notchManager?.Dispose();
        _zOrderManager?.Dispose();
        TrayIcon?.Dispose();
        _updateTimer?.Stop();
        _updateCheckTimer?.Stop();
        _moduleHost?.Dispose();
        _cameraController?.Dispose();
        _timerManager?.Dispose();
        DisposeAllShelfWatchers();
        base.OnClosed(e);
    }
    private static void TrimWorkingSet()
    {
        try
        {
            SetProcessWorkingSetSize(
                GetCurrentProcess(),
                (IntPtr)(-1),
                (IntPtr)(-1));
        }
        catch { }
    }

    #endregion

    #region Window Configuration

    private void ConfigureOverlayWindow()
    {
        var exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW;
        exStyle |= WS_EX_TOPMOST;
        exStyle |= WS_EX_NOACTIVATE;
        exStyle |= WS_EX_LAYERED;
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle);
        _zOrderManager.EnsureTopmost(force: true);
    }

    private void EnableKeyboardInput()
    {
        if (_hwnd == IntPtr.Zero) return;
        var exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        exStyle &= ~WS_EX_NOACTIVATE;
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle);
        Activate();
    }

    private void DisableKeyboardInput()
    {
        if (_hwnd == IntPtr.Zero) return;
        var exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_NOACTIVATE;
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle);
    }

    private bool IsEffectivelyNotchVisible => _isNotchVisible && !_isHiddenByFullscreen;

    private bool _fullscreenSlideVisible = true;
    private bool _isFullscreenSlideAnimating = false;

    private void ApplyNotchVisibilityState()
    {
        if (NotchContainer == null) return;

        bool shouldBeVisible = IsEffectivelyNotchVisible;

        // Don't re-trigger if already in the target state
        if (shouldBeVisible == _fullscreenSlideVisible && !_isFullscreenSlideAnimating) return;
        if (shouldBeVisible == _fullscreenSlideVisible) return;

        _fullscreenSlideVisible = shouldBeVisible;
        _isFullscreenSlideAnimating = true;

        double slideDistance = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight + 10 : _collapsedHeight + 10;

        // Cancel any running animation
        NotchContainerTranslate.BeginAnimation(TranslateTransform.YProperty, null);

        if (shouldBeVisible)
        {
            NotchContainer.Visibility = Visibility.Visible;
            NotchContainerTranslate.Y = -slideDistance;
            AnimateNotchSlide(toY: 0, durationMs: 350, easeOut: true, onComplete: () =>
            {
                _isFullscreenSlideAnimating = false;
            });
        }
        else
        {
            NotchContainerTranslate.Y = 0;
            AnimateNotchSlide(toY: -slideDistance, durationMs: 250, easeOut: false, onComplete: () =>
            {
                _isFullscreenSlideAnimating = false;
                NotchContainer.Visibility = Visibility.Collapsed;
                NotchContainerTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                NotchContainerTranslate.Y = 0;
            });
        }
    }

    private void AnimateNotchSlide(double toY, int durationMs, bool easeOut, Action? onComplete)
    {
        var easing = easeOut
            ? (IEasingFunction)new CubicEase { EasingMode = EasingMode.EaseOut }
            : new CubicEase { EasingMode = EasingMode.EaseIn };

        var anim = new DoubleAnimation(toY, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(anim, 144);

        if (onComplete != null)
        {
            anim.Completed += (s, e) => onComplete();
        }

        NotchContainerTranslate.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    private void UpdateFullscreenAutoHideState(IntPtr foregroundHwnd = default, bool force = false)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (!force && (now - _lastFullscreenCheckUtc) < TimeSpan.FromMilliseconds(50))
        {
            return;
        }

        _lastFullscreenCheckUtc = now;
        var targetHwnd = foregroundHwnd == IntPtr.Zero ? GetForegroundWindow() : foregroundHwnd;
        bool shouldHide = ShouldHideForFullscreen(targetHwnd);
        if (shouldHide == _isHiddenByFullscreen)
        {
            return;
        }

        _isHiddenByFullscreen = shouldHide;
        if (_isHiddenByFullscreen)
        {
            if ((_isExpanded || _isMusicExpanded) && !_isAnimating)
            {
                CollapseAll();
            }

            _hoverCollapseTimer.Stop();
            _hoverThumbnailDelayTimer.Stop();
        }

        ApplyNotchVisibilityState();

        if (!_isHiddenByFullscreen && _isNotchVisible)
        {
            TriggerZOrderBurst(TimeSpan.FromMilliseconds(900));
            EnsureTopmost(force: true);
        }
    }

    private bool ShouldHideForFullscreen(IntPtr hwnd)
    {
        if (hwnd == _hwnd)
        {
            return CheckFullscreenBehind(_hwnd);
        }

        if (hwnd != IntPtr.Zero)
        {
            GetWindowThreadProcessId(hwnd, out uint fgPid);
            GetWindowThreadProcessId(_hwnd, out uint myPid);
            if (fgPid == myPid)
            {
                return CheckFullscreenBehind(hwnd);
            }
        }

        return IsForegroundWindowFullscreen(hwnd);
    }

    private bool CheckFullscreenBehind(IntPtr startHwnd)
    {
        var next = Win32Interop.GetWindow(startHwnd, GW_HWNDNEXT);
        const int maxWalk = 20;
        uint myPid;
        GetWindowThreadProcessId(_hwnd, out myPid);

        for (int i = 0; i < maxWalk && next != IntPtr.Zero; i++)
        {
            if (IsWindowVisible(next) && !IsIconic(next))
            {
                GetWindowThreadProcessId(next, out uint nextPid);
                if (nextPid != myPid)
                {
                    var type = FullscreenDetector.DetectFullscreenType(next, _hwnd);
                    if (type != FullscreenType.None)
                    {
                        return type switch
                        {
                            FullscreenType.ExclusiveFullscreen => _settings.HideOnExclusiveFullscreen,
                            FullscreenType.WindowedFullscreen => _settings.HideOnWindowedFullscreen,
                            _ => false
                        };
                    }
                }
            }
            next = Win32Interop.GetWindow(next, GW_HWNDNEXT);
        }
        return false;
    }

    private bool IsForegroundWindowFullscreen(IntPtr hwnd)
    {
        var type = FullscreenDetector.DetectFullscreenType(hwnd, _hwnd);
        return type switch
        {
            FullscreenType.ExclusiveFullscreen => _settings.HideOnExclusiveFullscreen,
            FullscreenType.WindowedFullscreen => _settings.HideOnWindowedFullscreen,
            _ => false
        };
    }

    private void ScheduleFullscreenRecheck()
    {
        _fullscreenRecheckTimer?.Stop();
        _fullscreenRecheckTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _fullscreenRecheckTimer.Tick += (s, e) =>
        {
            _fullscreenRecheckTimer.Stop();
            UpdateFullscreenAutoHideState(force: true);
        };
        _fullscreenRecheckTimer.Start();
    }

    private void EnsureTopmost()
    {
        _zOrderManager.EnsureTopmost(force: false);
    }

    private void EnsureTopmost(bool force)
    {
        _zOrderManager.EnsureTopmost(force: force);
    }

    private void UpdateZOrderTimerInterval()
    {
        
    }

    private void StartZOrderWatchdog()
    {
        _zOrderManager.Start();
    }

    private void StopZOrderWatchdog()
    {
        _zOrderManager.Stop();
    }

    private void OnForegroundWindowChanged(IntPtr hwnd)
    {
        if (_hwnd == IntPtr.Zero) return;

        UpdateFullscreenAutoHideState(hwnd, force: true);
        ScheduleFullscreenRecheck();

        if (!IsEffectivelyNotchVisible) return;

        var processName = TryGetProcessName(hwnd);
        if (IsMyDockFinder(processName))
        {
            _zOrderManager.TriggerBurst(TimeSpan.FromSeconds(4), aggressive: true);
        }
        else
        {
            _zOrderManager.TriggerBurst(TimeSpan.FromMilliseconds(1200));
        }

        _zOrderManager.EnsureTopmost(force: true);
    }

    private void TriggerZOrderBurst(TimeSpan duration, bool aggressive = false)
    {
        _zOrderManager.TriggerBurst(duration, aggressive);
    }

    private void AssertTopmostNow()
    {
        _zOrderManager.AssertNow();
    }

    private static string TryGetProcessName(IntPtr hwnd) => FullscreenDetector.TryGetProcessName(hwnd);

    private static bool IsMyDockFinder(string processName)
    {
        return processName.Contains("mydockfinder", StringComparison.OrdinalIgnoreCase);
    }

    private void PositionAtTop()
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen == null) return;

        _windowWidth = (int)(_expandedWidth + 40);
        _windowHeight = (int)(_expandedHeight + 20);
        _fixedX = screen.Bounds.Left + (screen.Bounds.Width - _windowWidth) / 2;
        _fixedY = 0;

        this.Width = _windowWidth;
        this.Height = _windowHeight;
        SetWindowPos(_hwnd, HWND_TOPMOST, _fixedX, _fixedY, _windowWidth, _windowHeight, SWP_NOACTIVATE);
    }

    private void ResetPosition()
    {
        PositionAtTop();
    }
public (double Left, double Top, double Width, double Height, double CornerRadius) GetNotchScreenRect()
    {
        double notchW = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _collapsedWidth;
        double notchH = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _collapsedHeight;

        var source = PresentationSource.FromVisual(this);
        double dpiX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
        double dpiY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;

        double winLeft = _fixedX * dpiX;
        double winTop = _fixedY * dpiY;
        double winWidth = _windowWidth * dpiX;

        double notchLeft = winLeft + (winWidth - notchW) / 2.0;
        double notchTop = winTop;

        double cr = _cornerRadiusCollapsed;

        return (notchLeft, notchTop, notchW, notchH, cr);
    }

    private void OpenAppSettings()
    {
        var settingsWindow = new SettingsWindow(_settings, _settingsService)
        {
            Owner = this
        };

        settingsWindow.SettingsChanged += (s, newSettings) =>
        {
            bool sizeChanged = newSettings.Width != _settings.Width
                            || newSettings.Height != _settings.Height
                            || newSettings.CornerRadius != _settings.CornerRadius;
            bool languageChanged = newSettings.Language != _settings.Language;
            _settings = newSettings.Clone();
            _notchManager.UpdateSettings(_settings);
            _fileShelf.UpdateSettings(_settings);
            ApplySettings(sizeChanged);

            if (languageChanged)
            {
                Loc.SetLanguage(_settings.Language);
                RefreshNotchLocalization();
            }
        };

        settingsWindow.AnimatedClosing += (s, e) =>
        {
        };

        settingsWindow.Closed += (s, e) =>
        {
            NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

            // Reset scale to 1.0 before bouncing to prevent leftover expanded state
            NotchScale.ScaleX = 1.0;            NotchScale.ScaleY = 1.0;
            NotchShadowScale.ScaleX = 1.0;
            NotchShadowScale.ScaleY = 1.0;

            var bounceAnim = new DoubleAnimationUsingKeyFrames();
            bounceAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.12,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)),
                new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            bounceAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(600)),
                _easeSoftSpring));
            Timeline.SetDesiredFrameRate(bounceAnim, 144);

            NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounceAnim);
            NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounceAnim);
            NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounceAnim);
            NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounceAnim);
        };

        settingsWindow.ShowDialog();
    }

    private void ApplySettings(bool animatePulse = false)
    {
        _hoverCollapseTimer.Interval = TimeSpan.FromMilliseconds(_settings.HoverCollapseDelay);
        _hoverThumbnailDelayTimer.Interval = TimeSpan.FromMilliseconds(_settings.HoverExpandDelay);

        _collapsedWidth = _settings.Width;
        _collapsedHeight = _settings.Height;
        _cornerRadiusCollapsed = _settings.CornerRadius;
        _cachedThumbnailExpandTarget = null;

        // Only update visual dimensions when collapsed to avoid a 1-frame glitch
        if (!_isExpanded && !_isAnimating)
        {
            // Clear held animations so the new local value takes effect
            NotchBorder.BeginAnimation(WidthProperty, null);
            NotchBorder.BeginAnimation(HeightProperty, null);
            this.BeginAnimation(CurrentCornerRadiusProperty, null);

            // On first boot (ActualWidth is 0), set dimensions immediately without animation
            // to ensure layout is measured correctly before any other animations run.
            bool isFirstLayout = NotchBorder.ActualWidth <= 0 || NotchBorder.ActualHeight <= 0;
            const int fps = 144;

            if (isFirstLayout)
            {
                NotchBorder.Width = _settings.Width;
                NotchBorder.Height = _settings.Height;

                var cr = new CornerRadius(0, 0, _settings.CornerRadius, _settings.CornerRadius);
                NotchBorder.CornerRadius = cr;
                InnerClipBorder.CornerRadius = cr;
                NotchBorderShadow.CornerRadius = cr;
                MediaBackground.CornerRadius = cr;
                MediaBackground2.CornerRadius = cr;
            }
            else
            {
            // Animate to new size for a smooth live-preview feel
            var dur = _dur200;
            var easing = _easeExpOut6;

            var widthAnim = new DoubleAnimation(NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _settings.Width, _settings.Width, dur)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop
            };
            Timeline.SetDesiredFrameRate(widthAnim, fps);
            widthAnim.Completed += (s, e) =>
            {
                NotchBorder.BeginAnimation(WidthProperty, null);
                NotchBorder.Width = _settings.Width;
            };

            var heightAnim = new DoubleAnimation(NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _settings.Height, _settings.Height, dur)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop
            };
            Timeline.SetDesiredFrameRate(heightAnim, fps);
            heightAnim.Completed += (s, e) =>
            {
                NotchBorder.BeginAnimation(HeightProperty, null);
                NotchBorder.Height = _settings.Height;
            };

            NotchBorder.BeginAnimation(WidthProperty, widthAnim);
            NotchBorder.BeginAnimation(HeightProperty, heightAnim);

            // Animate corner radius via the dependency property (updates all related borders)
            double currentRadius = NotchBorder.CornerRadius.BottomLeft;
            double targetRadius = _settings.CornerRadius;
            if (Math.Abs(targetRadius - currentRadius) > 0.5)
            {
                CurrentCornerRadius = currentRadius;
                var radiusAnim = new DoubleAnimation(currentRadius, targetRadius, dur)
                {
                    EasingFunction = easing
                };
                Timeline.SetDesiredFrameRate(radiusAnim, fps);
                this.BeginAnimation(CurrentCornerRadiusProperty, radiusAnim);
            }
            else
            {
                var cr = new CornerRadius(0, 0, _settings.CornerRadius, _settings.CornerRadius);
                NotchBorder.CornerRadius = cr;
                InnerClipBorder.CornerRadius = cr;
                NotchBorderShadow.CornerRadius = cr;
                MediaBackground.CornerRadius = cr;
                MediaBackground2.CornerRadius = cr;
            }
            }

            UpdateNotchClip();
            UpdateMediaBackgroundFootprint();

            // Subtle scale pulse to give tactile feedback while dragging size sliders
            if (animatePulse)
            {
                NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

                var pulse = new DoubleAnimationUsingKeyFrames();
                pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.04,
                    KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80)),
                    new QuadraticEase { EasingMode = EasingMode.EaseOut }));
                pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,
                    KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300)),
                    _easeSoftSpring));
                Timeline.SetDesiredFrameRate(pulse, fps);

                NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
                NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
                NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
                NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
            }
        }

        this.Opacity = _settings.Opacity;

        // Apply dark overlay opacity ONLY to lyrics blur image (not the main color blur)
        double lyricsImageOpacity = Math.Max(0.2, 1.0 - _settings.MediaBlurDarkOverlay);
        if (LyricsBlurImage != null)
        {
            LyricsBlurImage.BeginAnimation(UIElement.OpacityProperty, null);
            LyricsBlurImage.Opacity = lyricsImageOpacity;
        }

        if (_currentMediaInfo != null && !_isExpanded)
        {
            UpdateMediaBackground(_currentMediaInfo, forceRefresh: true);
        }

        // Configure smart thumbnail cropping (ONNX/YOLOv8n)
        _mediaService.ArtworkService.ConfigureSmartCrop(_settings.EnableSmartCrop);

        if (_hwnd != IntPtr.Zero)
        {
            SetWindowPos(_hwnd, HWND_TOPMOST, _fixedX, _fixedY, _windowWidth, _windowHeight, SWP_NOACTIVATE);
        }
    }

    private void RefreshNotchLocalization()
    {
        UpdateShelfCapacityIndicator();
        EventText.Text = Loc.Get("greeting.enjoyDay");
        CameraLabel.Text = Loc.Get("notch.camera");
        ShelfUnlockButtonText.Text = Loc.Get("shelf.unlockButton");
        ShelfUnlockDismissText.Text = Loc.Get("shelf.unlockDismiss");
        ShelfUnlockSettingsHint.Text = Loc.Get("shelf.unlockSettingsHint");
    }

    #endregion

    #region Click & Hover Handling

    private void NotchBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isAnimating)
        {
            e.Handled = true;
            return;
        }

        // Don't open notch while volume indicator is active (dragging)
        if (_isVolumeIndicatorActive || _isDraggingVolumeIndicator)        {
            e.Handled = true;
            return;
        }

        if (!_isStartupLayoutReady)
        {
            if (!_pendingStartupClickToggle)
            {
                _pendingStartupClickToggle = true;
                int clickCount = e.ClickCount;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateLayout();
                    UpdateNotchClip();
                    _isStartupLayoutReady = true;
                    _pendingStartupClickToggle = false;

                    if (_isAnimating) return;
                    ToggleNotchFromClick(clickCount);
                }), DispatcherPriority.Render);
            }

            e.Handled = true;
            return;
        }

        ToggleNotchFromClick(e.ClickCount);
        e.Handled = true;
    }

    private void ToggleNotchFromClick(int clickCount)
    {
        if (_isExpanded)
        {
            if (_isSecondaryView)
            {
                if (clickCount == 2) CollapseNotch();
            }
            else
            {
                CollapseNotch();
            }
        }
        else
        {
            ExpandNotch();
        }
    }

    private void NotchWrapper_MouseEnter(object sender, MouseEventArgs e)
    {
        _hoverCollapseTimer.Stop();
        AnimateNotchHover(true);
        QueueHoverExpand();
    }

    private void NotchWrapper_MouseLeave(object sender, MouseEventArgs e)
    {
        _hoverThumbnailDelayTimer.Stop();

        if (_isExpanded && !_isAnimating && !_isSecondaryView)
        {
            _hoverCollapseTimer.Start();
        }
        else if (!_isExpanded)
        {
            AnimateNotchHover(false);
        }
    }

    private void QueueHoverExpand()
    {
        if (_settings.EnableHoverExpand && !_isExpanded && !_isAnimating)
        {
            _hoverThumbnailDelayTimer.Stop();
            _hoverThumbnailDelayTimer.Start();
        }
    }

    private void HoverService_HoverEnter(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(QueueHoverExpand));
    }

    private void HoverService_HoverLeave(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _hoverThumbnailDelayTimer.Stop();
            if (_isExpanded && !_isAnimating && !_isSecondaryView)
            {
                _hoverCollapseTimer.Start();
            }
        }));
    }

    private void CompactThumbnailBorder_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!_isExpanded && !_isAnimating && _isMusicCompactMode)
        {
            _compactThumbnailHoverLeaveTimer.Stop();
            _hoverThumbnailDelayTimer.Stop();
            _hoverThumbnailDelayTimer.Start();
        }
    }

    private void CompactThumbnailBorder_MouseLeave(object sender, MouseEventArgs e)
    {
        _hoverThumbnailDelayTimer.Stop();
        if (!_isExpanded && !_isAnimating && _isMusicCompactMode)
        {
            _compactThumbnailHoverLeaveTimer.Stop();
            _compactThumbnailHoverLeaveTimer.Start();
        }
    }

    private void SetCompactThumbnailHover(bool isHovered)
    {
        if (_isCompactThumbnailHovered == isHovered) return;

        _isCompactThumbnailHovered = isHovered;
        if (!isHovered)
        {
            _compactThumbnailHoverLeaveTimer.Stop();
        }
        AnimateThumbnailHover(isHovered);
    }

    private bool IsCursorInsideCompactThumbnailExitZone()
    {
        var p = Mouse.GetPosition(CompactThumbnailBorder);
        double width = CompactThumbnailBorder.ActualWidth;
        double height = CompactThumbnailBorder.ActualHeight;

        return p.X >= -CompactThumbnailHoverExitMargin &&
               p.Y >= -CompactThumbnailHoverExitMargin &&
               p.X <= width + CompactThumbnailHoverExitMargin &&
               p.Y <= height + CompactThumbnailHoverExitMargin;
    }

    #endregion

    #region Battery & Calendar

    private void Battery_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:batterysaver") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("BATTERY-CLICK", ex.ToString());
        }
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        OpenAppSettings();
    }

    private void Settings_Click(object sender, MouseButtonEventArgs e)
    {
        OpenAppSettings();
        e.Handled = true;
    }

    private void SettingsButton_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void SettingsButton_MouseEnter(object sender, MouseEventArgs e)
    {
        AnimateSettingsHover(true);
    }

    private void SettingsButton_MouseLeave(object sender, MouseEventArgs e)
    {
        AnimateSettingsHover(false);
    }

    private void AnimateSettingsHover(bool isEnter)
    {
        const int fps = 144;
        var dur = TimeSpan.FromMilliseconds(isEnter ? 360 : 300);
        var scaleDur = new Duration(dur);
        var rotateDur = new Duration(TimeSpan.FromMilliseconds(isEnter ? 520 : 360));
        var easeOut = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = isEnter ? 0.32 : 0.18 };
        var rotateEase = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 };

        var scaleAnim = new DoubleAnimation
        {
            To = isEnter ? 1.18 : 1.0,
            Duration = scaleDur,
            EasingFunction = easeOut
        };
        Timeline.SetDesiredFrameRate(scaleAnim, fps);

        var rotateAnim = new DoubleAnimation
        {
            To = isEnter ? 132 : 45,
            Duration = rotateDur,
            EasingFunction = rotateEase
        };
        Timeline.SetDesiredFrameRate(rotateAnim, fps);

        var opacityAnim = new DoubleAnimation
        {
            To = isEnter ? 0.95 : 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = _easeQuadOut
        };
        Timeline.SetDesiredFrameRate(opacityAnim, fps);

        var bg = SettingsButton.Background as SolidColorBrush;
        if (bg == null || bg.IsFrozen)
        {
            bg = new SolidColorBrush(Colors.Transparent);
            SettingsButton.Background = bg;
        }

        var bgAnim = new ColorAnimation
        {
            To = isEnter ? Color.FromArgb(34, 255, 255, 255) : Colors.Transparent,
            Duration = new Duration(TimeSpan.FromMilliseconds(isEnter ? 160 : 240)),
            EasingFunction = _easeQuadOut
        };
        Timeline.SetDesiredFrameRate(bgAnim, fps);

        SettingsScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim, HandoffBehavior.SnapshotAndReplace);
        SettingsScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim, HandoffBehavior.SnapshotAndReplace);
        SettingsRotate.BeginAnimation(RotateTransform.AngleProperty, rotateAnim, HandoffBehavior.SnapshotAndReplace);
        SettingsButton.BeginAnimation(OpacityProperty, opacityAnim, HandoffBehavior.SnapshotAndReplace);
        bg.BeginAnimation(SolidColorBrush.ColorProperty, bgAnim, HandoffBehavior.SnapshotAndReplace);
    }

    #endregion

    #region Timer Handlers

    private int _trimTickCounter = 0;

    private void UpdateTimer_Tick(object? sender, EventArgs e)
    {
        if (_isMusicExpanded) SyncVolumeFromActiveSession();
        EnsureTopmost();

        // Every ~2 minutes (4 ticks × 30s), trim working set when idle
        if (++_trimTickCounter >= 4)        {
            _trimTickCounter = 0;
            if (!_isExpanded && !_isMusicExpanded && !_isAnimating)
            {
                Task.Run(TrimWorkingSet);
            }
        }
    }

    private void BatteryModule_BatteryUpdated(object? sender, BatteryInfo battery)
        => HandleBatteryUpdate(battery);

    #endregion

    #region Notch Clip

    private void NotchContent_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateNotchClip();
        UpdateMediaBackgroundFootprint();
    }

    private void UpdateMediaBackgroundFootprint()
    {
        if (MediaBackground == null || MediaBackground2 == null || NotchBorder == null) return;

        double notchLength = NotchContent?.ActualWidth > 0
            ? NotchContent.ActualWidth
            : (NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : NotchBorder.Width);
        if (notchLength <= 0) return;

        // Use two-thirds of notch length.
        double primaryLength = notchLength * (2.0 / 3.0);        double secondaryLength = primaryLength * 0.84;

        double notchBreadth = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : NotchBorder.Height;
        double primaryBreadth = Math.Clamp(notchBreadth * 1.24, 140.0, 220.0);
        double secondaryBreadth = primaryBreadth * 0.82;

        MediaBackground.Width = primaryLength;
        MediaBackground.Height = primaryBreadth;

        MediaBackground2.Width = secondaryLength;
        MediaBackground2.Height = secondaryBreadth;
    }

    private void UpdateNotchClip()
    {
        if (NotchContent == null || NotchBorder == null) return;

        double w = NotchContent.ActualWidth;
        double h = NotchContent.ActualHeight;

        if (w <= 0 || h <= 0) return;

        double r = NotchBorder.CornerRadius.BottomRight;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(0, 0), true, true);
            ctx.LineTo(new Point(w, 0), true, false);
            ctx.LineTo(new Point(w, h - r), true, false);
            if (r > 0)
                ctx.ArcTo(new Point(w - r, h), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            else
                ctx.LineTo(new Point(w, h), true, false);

            ctx.LineTo(new Point(r, h), true, false);

            if (r > 0)
                ctx.ArcTo(new Point(0, h - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            else
                ctx.LineTo(new Point(0, h), true, false);
        }

        NotchContent.Clip = geometry;
    }

    #endregion

    #region Menu Actions

    private void TrayContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        _isTrayMenuOpen = true;
    }

    private void TrayContextMenu_Closed(object sender, RoutedEventArgs e)
    {
        _isTrayMenuOpen = false;
        TriggerZOrderBurst(TimeSpan.FromMilliseconds(900));
        EnsureTopmost(force: true);
    }

    private void ToggleNotch_Click(object sender, RoutedEventArgs e)
    {
        _isNotchVisible = !_isNotchVisible;
        if (!_isNotchVisible && (_isExpanded || _isMusicExpanded) && !_isAnimating)
        {
            CollapseAll();
        }

        ApplyNotchVisibilityState();

        if (_isNotchVisible)
        {
            UpdateFullscreenAutoHideState(GetForegroundWindow(), force: true);
            if (IsEffectivelyNotchVisible)
            {
                TriggerZOrderBurst(TimeSpan.FromMilliseconds(900));
                EnsureTopmost(force: true);
            }
        }
    }

    private void ResetPosition_Click(object sender, RoutedEventArgs e)
    {
        ResetPosition();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        UnregisterClipboardListener();
        _hwndSource?.RemoveHook(WndProc);
        StopZOrderWatchdog();

        // Unsubscribe events before shutdown
        _mediaService.MediaChanged -= OnMediaChanged;        _batteryModule.BatteryUpdated -= BatteryModule_BatteryUpdated;
        _calendarModule.CalendarUpdated -= CalendarModule_CalendarUpdated;
        _privacyModule.StateChanged -= PrivacyModule_StateChanged;
        InputMonitorService.MouseActionTriggered -= GlobalMouseHook_MouseLeftButtonDown;

        StopTitleGradientShift();
        _progressTimer.Stop();
        _mediaService.Dispose();
        _lyricsService.Dispose();
        _notchManager.Dispose();
        _zOrderManager.Dispose();
        TrayIcon.Dispose();
        _updateTimer.Stop();
        DisposeAllShelfWatchers();
        System.Windows.Application.Current.Shutdown();
    }

    #endregion

}
