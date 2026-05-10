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
    private readonly DispatcherTimer _zOrderWatchdogTimer;
    private readonly DispatcherTimer _zOrderFastTimer;
    private readonly DispatcherTimer _hoverCollapseTimer;
    private readonly DispatcherTimer _hoverThumbnailDelayTimer;
    private readonly DispatcherTimer _compactThumbnailHoverLeaveTimer;


    private bool _isDraggingVolume = false;
    private NotchSettings _settings;
    private bool _isNotchVisible = true;
    private bool _isHiddenByFullscreen = false;
    private IntPtr _hwnd;
    private HwndSource? _hwndSource;
    private IntPtr _foregroundWinEventHook = IntPtr.Zero;
    private WinEventDelegate? _foregroundWinEventProc;
    private DateTime _zOrderBurstUntilUtc = DateTime.MinValue;
    private DateTime _zOrderFastUntilUtc = DateTime.MinValue;
    private DateTime _lastTopmostAssertUtc = DateTime.MinValue;
    private DateTime _lastFullscreenCheckUtc = DateTime.MinValue;
    private bool _isTrayMenuOpen = false;

    private readonly BatteryModule _batteryModule;
    private readonly CalendarModule _calendarModule;
    private readonly IModuleLifecycleManager _moduleHost;


    private bool _isAnimating = false;
    private bool _isExpanded = false;
    private bool _isStartupLayoutReady = false;
    private bool _pendingStartupClickToggle = false;
    private double _collapsedWidth;
    private double _collapsedHeight;
    private double _expandedWidth = 480;
    private double _expandedHeight = 180;
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
        CalendarModule calendarModule)
    {
        InitializeComponent();
        _settingsService = (SettingsService)settingsService;
        _settings = _settingsService.Load();
        _notchManager = new NotchManager(this, _settings);
        _mediaService = (MediaDetectionService)mediaService;
        _updateService = updateService;

        _moduleHost = moduleHost;
        _batteryModule = batteryModule;
        _batteryModule.BatteryUpdated += BatteryModule_BatteryUpdated;

        _calendarModule = calendarModule;
        _calendarModule.CalendarUpdated += CalendarModule_CalendarUpdated;

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

        _zOrderWatchdogTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _zOrderWatchdogTimer.Tick += ZOrderWatchdogTimer_Tick;
        _zOrderFastTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _zOrderFastTimer.Tick += ZOrderFastTimer_Tick;

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
            if (_settings.EnableHoverExpand && !_isExpanded && !_isAnimating && !_isMusicCompactMode)
            {
                ExpandNotch();
            }
            else if (CompactThumbnailBorder.IsMouseOver)
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
    }


    #region Window Lifecycle

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);

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

        // Start update check timer and perform initial check
        _updateCheckTimer.Start();
        _ = CheckForUpdatesAsync();
        
        if (IsEffectivelyNotchVisible)
        {
            PlayAppearAnimation();
        }

        // Layout must be fully measured before media detection starts.
        // MediaDetectionService fires MediaChanged almost immediately (staged
        // refresh at 120 ms / 350 ms / 800 ms), and the media/progress/thumbnail
        // rendering code all reads ActualWidth/ActualHeight of UI elements.
        // Starting the service before ContextIdle means those values are still 0,
        // causing wrong progress ratio, thumbnail zoom, and animation targets on
        // first boot — all of which self-correct after a seek or interaction.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateLayout();
            UpdateNotchClip();
            UpdateMediaBackgroundFootprint();
            _isStartupLayoutReady = true;
            _pendingStartupClickToggle = false;

            // Safe to start now: layout is measured, ActualWidth/Height are valid.
            _mediaService.Start();
            _moduleHost.StartAll();
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
        _notchManager.HoverService.HoverEnter -= HoverService_HoverEnter;
        _notchManager.HoverService.HoverLeave -= HoverService_HoverLeave;
        _hwndSource?.RemoveHook(WndProc);
        StopZOrderWatchdog();
        _mediaService?.Dispose();
        _notchManager?.Dispose();
        TrayIcon?.Dispose();
        _updateTimer?.Stop();
        _updateCheckTimer?.Stop();
        _moduleHost?.Dispose();
        DisposeAllShelfWatchers();
        base.OnClosed(e);
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
        EnsureTopmost(force: true);
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

    private void ApplyNotchVisibilityState()
    {
        if (NotchContainer != null)
        {
            NotchContainer.Visibility = IsEffectivelyNotchVisible ? Visibility.Visible : Visibility.Collapsed;
        }

    }

    private void UpdateFullscreenAutoHideState(IntPtr foregroundHwnd = default, bool force = false)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (!force && (now - _lastFullscreenCheckUtc) < TimeSpan.FromMilliseconds(120))
        {
            return;
        }

        _lastFullscreenCheckUtc = now;
        var targetHwnd = foregroundHwnd == IntPtr.Zero ? GetForegroundWindow() : foregroundHwnd;
        bool shouldHide = IsForegroundWindowFullscreen(targetHwnd);
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

    private bool IsForegroundWindowFullscreen(IntPtr hwnd) =>
        FullscreenDetector.IsForegroundWindowFullscreen(hwnd, _hwnd);

    private void EnsureTopmost()
    {
        EnsureTopmost(force: false);
    }

    private void EnsureTopmost(bool force)
    {
        if (_isTrayMenuOpen)
        {
            return;
        }

        if (_isUpdateTooltipOpen || DateTime.UtcNow < _suspendTopmostUntilUtc)
        {
            return;
        }

        if (_hwnd == IntPtr.Zero || !IsEffectivelyNotchVisible)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (!force && (now - _lastTopmostAssertUtc) < TimeSpan.FromMilliseconds(80))
        {
            return;
        }

        if (force || Win32Interop.GetWindow(_hwnd, GW_HWNDPREV) != IntPtr.Zero)
        {
            _lastTopmostAssertUtc = now;
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
    }

    private void UpdateZOrderTimerInterval()
    {
        
    }

    private void StartZOrderWatchdog()
    {
        if (_hwnd == IntPtr.Zero || _foregroundWinEventHook != IntPtr.Zero)
        {
            return;
        }

        _foregroundWinEventProc = ForegroundWindowChanged;
        _foregroundWinEventHook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND,
            EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _foregroundWinEventProc,
            0,
            0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        TriggerZOrderBurst(TimeSpan.FromSeconds(2));
        EnsureTopmost(force: true);
        _zOrderWatchdogTimer.Start();
    }

    private void StopZOrderWatchdog()
    {
        _zOrderFastTimer.Stop();
        _zOrderWatchdogTimer.Stop();

        if (_foregroundWinEventHook != IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundWinEventHook);
            _foregroundWinEventHook = IntPtr.Zero;
        }

        _foregroundWinEventProc = null;
    }

    private void ForegroundWindowChanged(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint dwmsEventTime)
    {
        if (eventType != EVENT_SYSTEM_FOREGROUND || hwnd == IntPtr.Zero || hwnd == _hwnd)
        {
            return;
        }

        var processName = TryGetProcessName(hwnd);
        var isMyDockFinder = IsMyDockFinder(processName);

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_hwnd == IntPtr.Zero)
            {
                return;
            }

            UpdateFullscreenAutoHideState(hwnd, force: true);
            if (!IsEffectivelyNotchVisible)
            {
                return;
            }

            if (isMyDockFinder)
            {
                TriggerZOrderBurst(TimeSpan.FromSeconds(4), aggressive: true);
            }
            else
            {
                TriggerZOrderBurst(TimeSpan.FromMilliseconds(1200));
            }

            EnsureTopmost(force: true);
        }), DispatcherPriority.Send);
    }

    private void ZOrderWatchdogTimer_Tick(object? sender, EventArgs e)
    {
        if (_isTrayMenuOpen)
        {
            return;
        }

        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        UpdateFullscreenAutoHideState();
        if (!IsEffectivelyNotchVisible)
        {
            return;
        }

        var burstActive = DateTime.UtcNow <= _zOrderBurstUntilUtc;
        var hasWindowAbove = Win32Interop.GetWindow(_hwnd, GW_HWNDPREV) != IntPtr.Zero;

        if (burstActive || hasWindowAbove)
        {
            EnsureTopmost(force: burstActive);
        }
    }

    private void ZOrderFastTimer_Tick(object? sender, EventArgs e)
    {
        if (_isTrayMenuOpen)
        {
            return;
        }

        if (_hwnd == IntPtr.Zero || !IsEffectivelyNotchVisible)
        {
            _zOrderFastTimer.Stop();
            return;
        }

        if (DateTime.UtcNow > _zOrderFastUntilUtc)
        {
            _zOrderFastTimer.Stop();
            return;
        }

        EnsureTopmost(force: true);
    }

    private void TriggerZOrderBurst(TimeSpan duration, bool aggressive = false)
    {
        var now = DateTime.UtcNow;
        var until = now + duration;
        if (until > _zOrderBurstUntilUtc)
        {
            _zOrderBurstUntilUtc = until;
        }

        if (aggressive)
        {
            var fastDuration = duration.TotalMilliseconds >= 800
                ? TimeSpan.FromMilliseconds(800)
                : duration;
            var fastUntil = now + fastDuration;

            if (fastUntil > _zOrderFastUntilUtc)
            {
                _zOrderFastUntilUtc = fastUntil;
            }

            if (!_zOrderFastTimer.IsEnabled)
            {
                _zOrderFastTimer.Start();
            }
        }
    }

    private void AssertTopmostNow()
    {
        if (_isUpdateTooltipOpen || DateTime.UtcNow < _suspendTopmostUntilUtc)
        {
            return;
        }

        if (_hwnd == IntPtr.Zero || !IsEffectivelyNotchVisible)
        {
            return;
        }

        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
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

    private void OpenAppSettings()
    {
        var settingsWindow = new SettingsWindow(_settings, _settingsService)
        {
            Owner = this
        };

        settingsWindow.SettingsChanged += (s, newSettings) =>
        {
            _settings = newSettings.Clone();
            _notchManager.UpdateSettings(_settings);
            ApplySettings();
            ResetPosition();
        };

        settingsWindow.ShowDialog();
    }

    private void ApplySettings()
    {
        _hoverCollapseTimer.Interval = TimeSpan.FromMilliseconds(_settings.HoverCollapseDelay);
        _hoverThumbnailDelayTimer.Interval = TimeSpan.FromMilliseconds(_settings.HoverExpandDelay);
        if (_hwnd != IntPtr.Zero)
        {
            ConfigureOverlayWindow();
        }

        NotchBorder.Width = _settings.Width;
        NotchBorder.Height = _settings.Height;
        var cr = new CornerRadius(0, 0, _settings.CornerRadius, _settings.CornerRadius);
        NotchBorder.CornerRadius = cr;
        InnerClipBorder.CornerRadius = cr;
        UpdateNotchClip();
        MediaBackground.CornerRadius = cr;
        MediaBackground2.CornerRadius = cr;
        UpdateMediaBackgroundFootprint();
        this.Opacity = _settings.Opacity;

        _collapsedWidth = _settings.Width;
        _collapsedHeight = _settings.Height;
        _cornerRadiusCollapsed = _settings.CornerRadius;
        _cachedThumbnailExpandTarget = null;

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

    private void UpdateTimer_Tick(object? sender, EventArgs e)
    {
        if (_isMusicExpanded) SyncVolumeFromActiveSession();
        EnsureTopmost();
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
        _zOrderFastTimer.Stop();
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
        _hwndSource?.RemoveHook(WndProc);
        StopZOrderWatchdog();

        _mediaService.Dispose();
        _notchManager.Dispose();
        TrayIcon.Dispose();
        _updateTimer.Stop();
        DisposeAllShelfWatchers();
        System.Windows.Application.Current.Shutdown();
    }

    #endregion

}
