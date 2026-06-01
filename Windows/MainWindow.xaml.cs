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
    private readonly MediaDisplayController _mediaDisplayController;
    private readonly FullscreenAutoHideController _fullscreenController;
    private readonly BluetoothNotificationController _bluetoothController;
    private DragDropController _dragDropController = null!;
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
    private const double NotchWindowHorizontalPadding = 96;

    private int _fixedX = 0;
    private int _fixedY = 0;
    private int _windowWidth = 0;
    private int _windowHeight = 0;

    private MediaInfo? _currentMediaInfo;
    private bool _isMusicCompactMode = false;
    private bool _isCompactThumbnailHovered = false;
    private const double CompactThumbnailHoverExitMargin = 22.0;
    private DateTime _lastMediaActionTime = DateTime.MinValue;

    private readonly VNotch.Controllers.CompactPillArbiter _compactPillArbiter = new();

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
        _mediaDisplayController = new MediaDisplayController();
        _fullscreenController = new FullscreenAutoHideController(() => _hwnd, _settings);
        _fullscreenController.HideStateChanged += FullscreenController_HideStateChanged;
        _fullscreenController.RecheckNeeded += ScheduleFullscreenRecheck;
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
                // Final safety check: don't collapse if still in grace period
                if (DateTime.UtcNow < _suppressHoverCollapseUntilUtc)
                {
                    RuntimeLog.Log("COLLAPSE-BLOCKED",
                        $"HoverCollapseTimer suppressed at fire time: remaining={(_suppressHoverCollapseUntilUtc - DateTime.UtcNow).TotalMilliseconds:F0}ms");
                    return;
                }

                if (_hwnd != IntPtr.Zero && IsCursorInsideWindow())
                {
                    RuntimeLog.Log("COLLAPSE-BLOCKED",
                        $"HoverCollapseTimer: WPF says IsMouseOver=False but cursor is inside window rect — suppressing");
                    return;
                }

                RuntimeLog.Log("COLLAPSE-TRIGGER",
                    $"HoverCollapseTimer fired: isExpanded={_isExpanded} isMouseOver={NotchWrapper.IsMouseOver} " +
                    $"isSecondary={_isSecondaryView} isMusicExpanded={_isMusicExpanded}");
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
        _dragDropController = new DragDropController(_fileShelf);
        InitializeDragDropController();
        InitializeGestureController();
        _bluetoothController = new BluetoothNotificationController();
        InitializeBluetoothNotificationController();
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

            // Pre-warm layout of hidden content elements so first-time animations get correct ActualWidth/Height and TransformToAncestor results
            PreWarmHiddenContentLayout();

            _isStartupLayoutReady = true;
            _pendingStartupClickToggle = false;

            // Delay media/module start if greeting is active — they will start after greeting completes
            if (!_isGreetingActive)
            {
                _mediaService.Start();
                _moduleHost.StartAll();
            }

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
                        if (!_isSecondaryView && !_isTimerView) return;

                        if (_isExpanded && !_isAnimating)
                        {
                            RuntimeLog.Log("COLLAPSE-TRIGGER",
                                $"WM_ACTIVATEAPP(deactivate) -> CollapseNotch: isSecondary={_isSecondaryView} isTimer={_isTimerView}");
                            CollapseNotch();
                        }
                    }));
                }
                break;

            case WM_DISPLAYCHANGE:
                Dispatcher.BeginInvoke(() => PositionAtTop());
                break;

            case WM_DPICHANGED:
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
        if (!_isSecondaryView && !_isTimerView) return;

        if ((_isExpanded || _isMusicExpanded) && !_isAnimating)
        {
            RuntimeLog.Log("COLLAPSE-TRIGGER",
                $"Deactivated event -> CollapseAll: isExpanded={_isExpanded} isMusicExpanded={_isMusicExpanded} isSecondary={_isSecondaryView} isTimer={_isTimerView}");
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
        _hoverCollapseTimer?.Stop();
        _hoverThumbnailDelayTimer?.Stop();
        _compactThumbnailHoverLeaveTimer?.Stop();
        _moduleHost?.Dispose();
        _cameraController?.Dispose();
        _timerManager?.Dispose();
        DisposeGestureController();
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

        // Already in target state and idle — nothing to do.
        if (shouldBeVisible == _fullscreenSlideVisible && !_isFullscreenSlideAnimating) return;

        // Mid-animation flip (user toggled twice quickly): cancel current animation and re-target
        _fullscreenSlideVisible = shouldBeVisible;
        _isFullscreenSlideAnimating = true;

        double slideDistance = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight + 10 : _collapsedHeight + 10;

        // Capture current Y (in case we are interrupting an in-flight slide) before clearing the running animation.
        double currentY = NotchContainerTranslate.Y;
        NotchContainerTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        NotchContainerTranslate.Y = currentY;

        if (shouldBeVisible)
        {
            NotchContainer.Visibility = Visibility.Visible;
            // If the notch was fully off-screen, start from -slideDistance
            if (currentY > 0 || currentY < -slideDistance)
            {
                NotchContainerTranslate.Y = -slideDistance;
            }
            AnimateNotchSlide(toY: 0, durationMs: 350, easeOut: true, onComplete: () =>
            {
                _isFullscreenSlideAnimating = false;
            });
        }
        else
        {
            AnimateNotchSlide(toY: -slideDistance, durationMs: 250, easeOut: false, onComplete: () =>
            {
                _isFullscreenSlideAnimating = false;
                // Only collapse the container if we're still meant to be hidden; an interrupting show may have re-enabled visibility before the hide animation completed
                if (!_fullscreenSlideVisible)
                {
                    NotchContainer.Visibility = Visibility.Collapsed;
                    NotchContainerTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                    NotchContainerTranslate.Y = 0;
                }
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
        _fullscreenController.Evaluate(foregroundHwnd, force);
    }

    private void FullscreenController_HideStateChanged(bool shouldHide)
    {
        _isHiddenByFullscreen = shouldHide;

        if (_isHiddenByFullscreen)
        {
            if ((_isExpanded || _isMusicExpanded) && !_isAnimating)
            {
                RuntimeLog.Log("COLLAPSE-TRIGGER",
                    $"FullscreenAutoHide -> CollapseAll: shouldHide={shouldHide} isExpanded={_isExpanded} isMusicExpanded={_isMusicExpanded}");
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

    private void ScheduleFullscreenRecheck()
    {
        if (_fullscreenRecheckTimer == null)
        {
            _fullscreenRecheckTimer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };
            _fullscreenRecheckTimer.Tick += (s, e) =>
            {
                _fullscreenRecheckTimer!.Stop();
                UpdateFullscreenAutoHideState(force: true);
            };
        }
        _fullscreenRecheckTimer.Stop();
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

    private void PreWarmHiddenContentLayout()
    {
        try
        {
            // Track and force show all hidden content elements that animations depend on for measure/transform calculations.
            var elementsToWarm = new System.Collections.Generic.List<(FrameworkElement Element, Visibility Original, double Opacity)>();

            void Track(FrameworkElement? el)
            {
                if (el == null) return;
                if (el.Visibility != Visibility.Collapsed) return;
                elementsToWarm.Add((el, el.Visibility, el.Opacity));
                el.Opacity = 0;
                el.Visibility = Visibility.Visible;
            }

            // Content panels that animations transform / measure
            Track(ExpandedContent);
            Track(SecondaryContent);
            Track(MusicCompactContent);

            // Music widget sub-elements
            Track(InlineControls);
            Track(LyricsWidget);
            Track(LyricsBlurBackground);

            // Hover info & nav icons (used by hover/secondary view animations)
            Track(CompactHoverInfo);
            Track(NavIconsPanel);
            Track(NavIconsBackground);

            // Battery & status (used by reveal animations)
            Track(BatterySection);
            Track(SettingsButton);

            // Apply the same dimensions ExpandNotch will use later, so child layouts (especially ThumbnailBorder which determines the flying-thumb target) get the same coordinates as the real expanded state
            double prevExpandedWidth = ExpandedContent.Width;
            double prevExpandedHeight = ExpandedContent.Height;
            ExpandedContent.Width = _expandedWidth - 16;
            ExpandedContent.Height = _expandedHeight - 2;

            // Also temporarily resize the notch border itself so any TransformToAncestor calculations resolve against the final expanded dimensions
            double prevNotchWidth = NotchBorder.Width;
            double prevNotchHeight = NotchBorder.Height;
            NotchBorder.Width = _expandedWidth;
            NotchBorder.Height = _expandedHeight;

            // Force a synchronous layout pass — measure + arrange everything
            UpdateLayout();

            // Pre-compute thumbnail expand target now that real layout is settled
            if (TryComputeThumbnailExpandTarget(out var target))
            {
                _cachedThumbnailExpandTarget = target;
            }

            CompactThumbnailBorder.CacheMode ??= new System.Windows.Media.BitmapCache(2.0);
            AnimationThumbnailBorder.CacheMode ??= new System.Windows.Media.BitmapCache(2.0);
            SettingsButton.CacheMode ??= new System.Windows.Media.BitmapCache(1.5);

            // Pre-warm blur effects by setting radius to 0 (allocates shader resources without visual impact)
            if (ThumbnailOutBlur != null) { ThumbnailOutBlur.Radius = 0; }
            if (ThumbnailNextBlur != null) { ThumbnailNextBlur.Radius = 0; }
            if (CompactThumbnailOutBlur != null) { CompactThumbnailOutBlur.Radius = 0; }
            if (CompactThumbnailNextBlur != null) { CompactThumbnailNextBlur.Radius = 0; }
            if (CollapsedContentBlur != null) { CollapsedContentBlur.Radius = 0; }

            // Pre-create and freeze thumbnail expand/collapse animations so first expand doesn't JIT-compile them
            var thumbDur = new Duration(TimeSpan.FromMilliseconds(500));
            var thumbEase = _easeExpOut6;
            var thumbDelay = TimeSpan.FromMilliseconds(30);
            int thumbFps = 144;

            if (_cachedThumbWidthExpand == null)
            {
                _cachedThumbWidthExpand = MakeAnim(22, 102, thumbDur, thumbEase, thumbDelay);
                _cachedThumbHeightExpand = MakeAnim(22, 102, thumbDur, thumbEase, thumbDelay);
                Timeline.SetDesiredFrameRate(_cachedThumbWidthExpand, thumbFps);
                Timeline.SetDesiredFrameRate(_cachedThumbHeightExpand, thumbFps);

                _cachedThumbRectExpand = new RectAnimation(new Rect(0, 0, 22, 22), new Rect(0, 0, 102, 102), thumbDur)
                {
                    EasingFunction = thumbEase,
                    BeginTime = thumbDelay
                };
                Timeline.SetDesiredFrameRate(_cachedThumbRectExpand, thumbFps);

                _cachedThumbWidthExpand.Freeze();
                _cachedThumbHeightExpand.Freeze();
                _cachedThumbRectExpand.Freeze();
            }

            var collapseDur = new Duration(TimeSpan.FromMilliseconds(420));
            var collapseEase = _easeExpOut6;
            var collapseDelay = TimeSpan.FromMilliseconds(20);

            if (_cachedThumbWidthCollapse == null)
            {
                _cachedThumbWidthCollapse = MakeAnim(102, 22, collapseDur, collapseEase, collapseDelay);
                _cachedThumbHeightCollapse = MakeAnim(102, 22, collapseDur, collapseEase, collapseDelay);
                Timeline.SetDesiredFrameRate(_cachedThumbWidthCollapse, thumbFps);
                Timeline.SetDesiredFrameRate(_cachedThumbHeightCollapse, thumbFps);

                _cachedThumbRectCollapse = new RectAnimation(new Rect(0, 0, 102, 102), new Rect(0, 0, 22, 22), collapseDur)
                {
                    EasingFunction = collapseEase,
                    BeginTime = collapseDelay
                };
                Timeline.SetDesiredFrameRate(_cachedThumbRectCollapse, thumbFps);

                _cachedThumbWidthCollapse.Freeze();
                _cachedThumbHeightCollapse.Freeze();
                _cachedThumbRectCollapse.Freeze();
            }

            // Pre-warm the thumbnail switch layers (Next/Out) so first crossfade doesn't allocate
            ThumbnailImageNext.Opacity = 0;
            CompactThumbnailNext.Opacity = 0;
            ThumbnailNextScale.ScaleX = 1.0;
            ThumbnailNextScale.ScaleY = 1.0;
            CompactThumbnailNextScale.ScaleX = 1.0;
            CompactThumbnailNextScale.ScaleY = 1.0;
            ThumbnailOutScale.ScaleX = 1.0;
            ThumbnailOutScale.ScaleY = 1.0;
            CompactThumbnailOutScale.ScaleX = 1.0;
            CompactThumbnailOutScale.ScaleY = 1.0;

            // Restore original visibility/opacity and dimensions
            foreach (var (el, originalVis, originalOpacity) in elementsToWarm)
            {
                el.Visibility = originalVis;
                el.Opacity = originalOpacity;
            }
            ExpandedContent.Width = prevExpandedWidth;
            ExpandedContent.Height = prevExpandedHeight;
            NotchBorder.Width = prevNotchWidth;
            NotchBorder.Height = prevNotchHeight;
            UpdateLayout();
        }
        catch (Exception ex)
        {
            VNotch.Services.RuntimeLog.Log("LAYOUT-PREWARM", ex.ToString());
        }
    }

    private void PositionAtTop()
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen == null) return;

        // Get the DPI scale factor for this window.
        // GetDpiForWindow returns the DPI (96 = 100%, 144 = 150%, 192 = 200%, etc.)
        double dpiScale = 1.0;
        if (_hwnd != IntPtr.Zero)
        {
            uint dpi = GetDpiForWindow(_hwnd);
            if (dpi > 0) dpiScale = dpi / 96.0;
        }

        // Window dimensions in DIPs (for WPF layout)
        // The shadow wrapper includes 11px ears on each side and a 20px blur. Keep
        // enough transparent window surface so the side shadow is not clipped.
        double windowWidthDip = _expandedWidth + NotchWindowHorizontalPadding;
        double windowHeightDip = _expandedHeight + 80;

        // Physical pixel dimensions for SetWindowPos
        _windowWidth = (int)Math.Round(windowWidthDip * dpiScale);
        _windowHeight = (int)Math.Round(windowHeightDip * dpiScale);

        // Screen.Bounds is in physical pixels, so center using physical pixel width
        _fixedX = screen.Bounds.Left + (screen.Bounds.Width - _windowWidth) / 2;
        _fixedY = 0;

        // WPF Width/Height must be in DIPs
        this.Width = windowWidthDip;
        this.Height = windowHeightDip;
        SetWindowPos(_hwnd, HWND_TOPMOST, _fixedX, _fixedY, _windowWidth, _windowHeight, SWP_NOACTIVATE);

        // Monitor for the notch may have changed (display add/remove, resolution change)
        if (_hwnd != IntPtr.Zero)
        {
            UpdateFullscreenAutoHideState(GetForegroundWindow(), force: true);
        }
    }

    private void ResetPosition()
    {
        PositionAtTop();
    }
public (double Left, double Top, double Width, double Height, double CornerRadius) GetNotchScreenRect()
    {
        double notchW = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _collapsedWidth;
        double notchH = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _collapsedHeight;

        // Get DPI scale to convert physical pixel positions to WPF DIPs
        double dpiScale = 1.0;
        if (_hwnd != IntPtr.Zero)
        {
            uint dpi = GetDpiForWindow(_hwnd);
            if (dpi > 0) dpiScale = dpi / 96.0;
        }

        // _fixedX/_fixedY are in physical pixels; convert to DIPs for WPF coordinate space
        double winLeft = _fixedX / dpiScale;
        double winTop = _fixedY / dpiScale;
        double winWidth = _windowWidth / dpiScale;

        double notchLeft = winLeft + (winWidth - notchW) / 2.0;
        double notchTop = winTop;

        double cr = _cornerRadiusCollapsed;

        return (notchLeft, notchTop, notchW, notchH, cr);
    }

    private void OpenAppSettings()
    {
        var settingsWindow = new SettingsWindow(_settings, _settingsService, _bluetoothModule)
        {
            Owner = this
        };

        settingsWindow.SettingsChanged += (s, newSettings) =>
        {
            bool sizeChanged = newSettings.Width != _settings.Width
                            || newSettings.Height != _settings.Height
                            || newSettings.CornerRadius != _settings.CornerRadius;
            bool languageChanged = newSettings.Language != _settings.Language;
            string oldSubtitlePriority = _settings.SubtitlePriority ?? "";
            _settings = newSettings.Clone();
            _notchManager.UpdateSettings(_settings);
            _fileShelf.UpdateSettings(_settings);
            ApplySettings(sizeChanged);
            UpdateBatteryInfo();

            // Sync subtitle language priority to the service and re-fetch if changed
            _youtubeSubtitleService.SetMode(_settings.SubtitlePriority);

            bool priorityChanged = !string.Equals(oldSubtitlePriority, _settings.SubtitlePriority, StringComparison.Ordinal);
            if (priorityChanged)
            {
                VNotch.Services.RuntimeLog.Log("SUBTITLE-PRIORITY",
                    $"Priority changed: '{oldSubtitlePriority}' -> '{_settings.SubtitlePriority}' " +
                    $"hasMedia={_currentMediaInfo != null} videoId='{_lastKnownYouTubeVideoId}'");

                if (!string.IsNullOrEmpty(_lastKnownYouTubeVideoId))
                {
                    _youtubeSubtitleService.Reset();
                    _lyricsTrackKey = "";
                    _syncedTextSource = SyncedTextSource.None;
                    // Create a minimal MediaInfo with the known video ID for re-fetch
                    var refetchInfo = _currentMediaInfo ?? new VNotch.Models.MediaInfo();
                    if (string.IsNullOrEmpty(refetchInfo.YouTubeVideoId))
                        refetchInfo.YouTubeVideoId = _lastKnownYouTubeVideoId;
                    FetchSubtitlesForTrack(refetchInfo, force: true);
                }
            }

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

            // Ensure thumbnail borders are visible after settings close
            if (CompactThumbnailBorder != null) CompactThumbnailBorder.Opacity = 1;
            if (ThumbnailBorder != null) ThumbnailBorder.Opacity = 1;
            // Reset animation state that may have been left dirty
            _isAnimating = false;

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

        // Sync subtitle mode to the service
        _youtubeSubtitleService.SetMode(_settings.SubtitlePriority);

        if (_settings.DisableMouseLeaveAutoClose)
        {
            _hoverCollapseTimer.Stop();
        }

        // Re-evaluate fullscreen hide state in case user toggled the HideOnExclusiveFullscreen / HideOnWindowedFullscreen options.
        if (_hwnd != IntPtr.Zero)
        {
            UpdateFullscreenAutoHideState(GetForegroundWindow(), force: true);
        }

        _collapsedWidth = _settings.Width;
        _collapsedHeight = _settings.Height;
        _cornerRadiusCollapsed = _settings.CornerRadius;
        _cachedThumbnailExpandTarget = null;

        ApplyDynamicIslandLayout();

        // Only update visual dimensions when collapsed to avoid a 1-frame glitch
        if (!_isExpanded && !_isAnimating)
        {
            // Clear held animations so the new local value takes effect
            NotchBorder.BeginAnimation(WidthProperty, null);
            NotchBorder.BeginAnimation(HeightProperty, null);
            this.BeginAnimation(CurrentCornerRadiusProperty, null);

            // On first boot (ActualWidth is 0), set dimensions immediately without animation to ensure layout is measured correctly before any other animations run
            bool isFirstLayout = NotchBorder.ActualWidth <= 0 || NotchBorder.ActualHeight <= 0;
            const int fps = 144;

            if (isFirstLayout)
            {
                NotchBorder.Width = _settings.Width;
                NotchBorder.Height = _settings.Height;

                var cr = MakeNotchCornerRadius(_settings.CornerRadius);
                NotchBorder.CornerRadius = cr;
                InnerClipBorder.CornerRadius = cr;
                NotchBackground.CornerRadius = cr;
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
                var cr = MakeNotchCornerRadius(_settings.CornerRadius);
                NotchBorder.CornerRadius = cr;
                InnerClipBorder.CornerRadius = cr;
                NotchBackground.CornerRadius = cr;
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

        // Apply dark overlay opacity to lyrics blur image
        double lyricsImageOpacity = Math.Max(0.2, 1.0 - _settings.MediaBlurDarkOverlay);
        if (LyricsBlurImage != null)
        {
            LyricsBlurImage.BeginAnimation(UIElement.OpacityProperty, null);
            LyricsBlurImage.Opacity = lyricsImageOpacity;
        }

        if (!_settings.EnableSpotifyLyrics)
        {
            if (_isLyricsActive && _syncedTextSource == SyncedTextSource.SpotifyLyrics)
            {
                _lyricsTrackKey = "";
                HideLyricsWidget();
            }
        }
        else if (_currentMediaInfo != null
                 && _currentMediaInfo.MediaSource == "Spotify"
                 && !string.IsNullOrEmpty(_currentMediaInfo.CurrentTrack)
                 && !_isLyricsActive)
        {
            _lyricsTrackKey = "";
            FetchLyricsForTrack(_currentMediaInfo);
        }

        // React to YouTube subtitles toggle: same pattern as Spotify lyrics.
        if (!_settings.EnableYouTubeSubtitles)
        {
            if (_isLyricsActive && _syncedTextSource == SyncedTextSource.YouTubeSubtitles)
            {
                _lyricsTrackKey = "";
                HideLyricsWidget();
            }
        }
        else if (_currentMediaInfo != null
                 && _currentMediaInfo.MediaSource == "YouTube"
                 && !string.IsNullOrEmpty(_currentMediaInfo.YouTubeVideoId)
                 && !_isLyricsActive)
        {
            _lyricsTrackKey = "";
            FetchSubtitlesForTrack(_currentMediaInfo);
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
        CameraErrorText.Text = Loc.Get("notch.camera.error");
        CameraRetryText.Text = Loc.Get("notch.camera.retry");
        ShelfUnlockButtonText.Text = Loc.Get("shelf.unlockButton");
        ShelfUnlockDismissText.Text = Loc.Get("shelf.unlockDismiss");
        ShelfUnlockSettingsHint.Text = Loc.Get("shelf.unlockSettingsHint");
    }

    private const double DynamicIslandTopMargin = 8.0;

    private void ApplyDynamicIslandLayout()
    {
        bool islandMode = _settings.EnableDynamicIslandMode;

        if (NotchContainer != null)
        {
            var current = NotchContainer.Margin;
            double targetTop = islandMode ? DynamicIslandTopMargin : 0;
            if (Math.Abs(current.Top - targetTop) > 0.01)
            {
                NotchContainer.Margin = new Thickness(current.Left, targetTop, current.Right, current.Bottom);
            }
        }

        var earVisibility = islandMode ? Visibility.Collapsed : Visibility.Visible;
        if (LeftEar != null) LeftEar.Visibility = earVisibility;
        if (RightEar != null) RightEar.Visibility = earVisibility;
        if (LeftShadowEar != null) LeftShadowEar.Visibility = earVisibility;
        if (RightShadowEar != null) RightShadowEar.Visibility = earVisibility;
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

        // Don't open notch while actively dragging the volume slider
        if (_isDraggingVolumeIndicator)
        {
            e.Handled = true;
            return;
        }

        if (!_isExpanded)
        {
            SuppressCompactVolumeWheelForClick();
        }

        if (_isVolumeIndicatorActive)
        {
            DismissVolumeIndicatorImmediate();
        }

        // Try gesture tracking when collapsed with media playing
        if (TryBeginGesture(e))
        {
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

        if (_settings.DisableMouseLeaveAutoClose)
        {
            if (!_isExpanded)
            {
                AnimateNotchHover(false);
            }
            return;
        }

        if (_isExpanded && !_isAnimating && !_isSecondaryView)
        {
            // Suppress hover-collapse if we're in the grace period after expand/thumbnail animation
            if (DateTime.UtcNow < _suppressHoverCollapseUntilUtc)
            {
                RuntimeLog.Log("COLLAPSE-BLOCKED",
                    $"MouseLeave suppressed during grace period: remaining={(_suppressHoverCollapseUntilUtc - DateTime.UtcNow).TotalMilliseconds:F0}ms");
                return;
            }

            if (IsCursorInsideWindow())
            {
                RuntimeLog.Log("COLLAPSE-BLOCKED",
                    $"MouseLeave: WPF fired leave but cursor still inside window rect — ignoring");
                return;
            }

            RuntimeLog.Log("COLLAPSE-HOVER",
                $"MouseLeave -> starting hoverCollapseTimer: interval={_hoverCollapseTimer.Interval.TotalMilliseconds}ms " +
                $"isExpanded={_isExpanded} isMusicExpanded={_isMusicExpanded}");
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
            if (_settings.DisableMouseLeaveAutoClose) return;
            if (_isExpanded && !_isAnimating && !_isSecondaryView)
            {
                // Suppress hover-collapse if we're in the grace period after expand/thumbnail animation
                if (DateTime.UtcNow < _suppressHoverCollapseUntilUtc)
                {
                    RuntimeLog.Log("COLLAPSE-BLOCKED",
                        $"HoverService_HoverLeave suppressed during grace period: remaining={(_suppressHoverCollapseUntilUtc - DateTime.UtcNow).TotalMilliseconds:F0}ms");
                    return;
                }
                RuntimeLog.Log("COLLAPSE-HOVER",
                    $"HoverService_HoverLeave -> starting hoverCollapseTimer: interval={_hoverCollapseTimer.Interval.TotalMilliseconds}ms");
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

    private void BatterySection_MouseEnter(object sender, MouseEventArgs e)
    {
        AnimateBatteryHover(true);
    }

    private void BatterySection_MouseLeave(object sender, MouseEventArgs e)
    {
        AnimateBatteryHover(false);
    }

    private void AnimateBatteryHover(bool isHovered)
    {
        if (BatterySectionScale == null) return;

        double targetScale = isHovered ? 1.1 : 1.0;
        var dur = isHovered ? TimeSpan.FromMilliseconds(220) : TimeSpan.FromMilliseconds(280);
        var ease = isHovered
            ? (IEasingFunction)new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 }
            : new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 };

        var anim = new DoubleAnimation(targetScale, dur)
        {
            EasingFunction = ease
        };
        Timeline.SetDesiredFrameRate(anim, 144);

        BatterySectionScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        BatterySectionScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
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
        // Enable bitmap caching to prevent sub-pixel jitter during scale/rotate
        SettingsButton.CacheMode ??= new System.Windows.Media.BitmapCache(1.5);
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
        double primaryLength = notchLength * (2.0 / 3.0);
        double secondaryLength = primaryLength * 0.84;

        double notchBreadth = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : NotchBorder.Height;
        double primaryBreadth = Math.Clamp(notchBreadth * 1.24, 140.0, 220.0);
        double secondaryBreadth = primaryBreadth * 0.82;

        // Animate size changes to prevent jarring position shifts during blur crossfade.
        // When the notch resizes (e.g., track title changes width), the blur container
        // should smoothly transition rather than snapping to the new size.
        var dur = TimeSpan.FromMilliseconds(350);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        AnimateDouble(MediaBackground, WidthProperty, primaryLength, dur, ease);
        AnimateDouble(MediaBackground, HeightProperty, primaryBreadth, dur, ease);
        AnimateDouble(MediaBackground2, WidthProperty, secondaryLength, dur, ease);
        AnimateDouble(MediaBackground2, HeightProperty, secondaryBreadth, dur, ease);
    }

    private static void AnimateDouble(UIElement target, DependencyProperty prop, double to, TimeSpan duration, IEasingFunction ease)
    {
        var current = (double)((FrameworkElement)target).GetValue(prop);
        if (double.IsNaN(current) || Math.Abs(current - to) < 0.5)
        {
            // No meaningful change or first time — set directly.
            ((FrameworkElement)target).BeginAnimation(prop, null);
            ((FrameworkElement)target).SetValue(prop, to);
            return;
        }

        var anim = new DoubleAnimation
        {
            To = to,
            Duration = duration,
            EasingFunction = ease
        };
        ((FrameworkElement)target).BeginAnimation(prop, anim);
    }

    private void UpdateNotchClip()
    {
        if (NotchContent == null || NotchBorder == null) return;

        double w = NotchContent.ActualWidth;
        double h = NotchContent.ActualHeight;

        if (w <= 0 || h <= 0) return;

        double rBottom = NotchBorder.CornerRadius.BottomRight;
        double rTop = NotchBorder.CornerRadius.TopLeft;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            // Top-left corner
            if (rTop > 0)
            {
                ctx.BeginFigure(new Point(rTop, 0), true, true);
                ctx.LineTo(new Point(w - rTop, 0), true, false);
                ctx.ArcTo(new Point(w, rTop), new Size(rTop, rTop), 0, false, SweepDirection.Clockwise, true, false);
            }
            else
            {
                ctx.BeginFigure(new Point(0, 0), true, true);
                ctx.LineTo(new Point(w, 0), true, false);
            }

            // Right edge → bottom-right corner
            ctx.LineTo(new Point(w, h - rBottom), true, false);
            if (rBottom > 0)
                ctx.ArcTo(new Point(w - rBottom, h), new Size(rBottom, rBottom), 0, false, SweepDirection.Clockwise, true, false);
            else
                ctx.LineTo(new Point(w, h), true, false);

            // Bottom edge → bottom-left corner
            ctx.LineTo(new Point(rBottom, h), true, false);
            if (rBottom > 0)
                ctx.ArcTo(new Point(0, h - rBottom), new Size(rBottom, rBottom), 0, false, SweepDirection.Clockwise, true, false);
            else
                ctx.LineTo(new Point(0, h), true, false);

            // Left edge → close back into top-left
            if (rTop > 0)
            {
                ctx.LineTo(new Point(0, rTop), true, false);
                ctx.ArcTo(new Point(rTop, 0), new Size(rTop, rTop), 0, false, SweepDirection.Clockwise, true, false);
            }
            else
            {
                ctx.LineTo(new Point(0, 0), true, false);
            }
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
