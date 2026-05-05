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
namespace VNotch;

public partial class MainWindow : Window
{
    #region Win32 API

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeRect(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeInt(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    private const uint GW_HWNDPREV = 3;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const int WM_ACTIVATE = 0x0006;
    private const int WM_ACTIVATEAPP = 0x001C;
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
    private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
    private const byte VK_MEDIA_PREV_TRACK = 0xB1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const int VK_LBUTTON = 0x01;
    private const int SW_SHOWMAXIMIZED = 3;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint dwmsEventTime);

    #endregion

    #region Fields

    private readonly SettingsService _settingsService;
    private readonly NotchManager _notchManager;
    private readonly MediaDetectionService _mediaService;
    private readonly DispatcherTimer _updateTimer;
    private readonly DispatcherTimer _zOrderWatchdogTimer;
    private readonly DispatcherTimer _zOrderFastTimer;
    private readonly DispatcherTimer _hoverCollapseTimer;
    private readonly DispatcherTimer _hoverThumbnailDelayTimer;


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

    private readonly BatteryModule _batteryModule;
    private readonly CalendarModule _calendarModule;


    private bool _isAnimating = false;
    private bool _isExpanded = false;
    private bool _isStartupLayoutReady = false;
    private bool _pendingStartupClickToggle = false;
    private double _collapsedWidth;
    private double _collapsedHeight;
    private double _expandedWidth = 480;
    private double _expandedHeight = 155;
    private double _cornerRadiusCollapsed;
    private double _cornerRadiusExpanded = 24;

    private int _fixedX = 0;
    private int _fixedY = 0;
    private int _windowWidth = 0;
    private int _windowHeight = 0;

    private MediaInfo? _currentMediaInfo;
    private bool _isMusicCompactMode = false;
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
        IMediaDetectionService mediaService)
    {
        InitializeComponent();
        _settingsService = (SettingsService)settingsService;
        _settings = _settingsService.Load();
        _notchManager = new NotchManager(this, _settings);
        _mediaService = (MediaDetectionService)mediaService;

        _batteryModule = new BatteryModule((IBatteryService)App.Services.GetService(typeof(IBatteryService))!);
        _batteryModule.BatteryUpdated += BatteryModule_BatteryUpdated;
        
        _calendarModule = new CalendarModule();
        _calendarModule.CalendarUpdated += CalendarModule_CalendarUpdated;

        _collapsedWidth = _settings.Width;
        _collapsedHeight = _settings.Height;
        _cornerRadiusCollapsed = _settings.CornerRadius;

        _updateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _updateTimer.Tick += UpdateTimer_Tick;

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
            Interval = TimeSpan.FromMilliseconds(1000)
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
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _hoverThumbnailDelayTimer.Tick += (s, e) =>
        {
            _hoverThumbnailDelayTimer.Stop();
            if (CompactThumbnailBorder.IsMouseOver)
            {
                AnimateThumbnailHover(true);
            }
        };

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

        _mediaService.Start();
        _updateTimer.Start();

        _batteryModule.Start();
        _calendarModule.Start();
        
        if (IsEffectivelyNotchVisible)
        {
            PlayAppearAnimation();
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateLayout();
            UpdateNotchClip();
            _isStartupLayoutReady = true;
            _pendingStartupClickToggle = false;
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
        _hwndSource?.RemoveHook(WndProc);
        StopZOrderWatchdog();
        _mediaService?.Dispose();
        _notchManager?.Dispose();
        TrayIcon?.Dispose();
        _updateTimer?.Stop();
        _batteryModule?.Stop();
        _calendarModule?.Stop();
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

        if (MenuToggle != null)
        {
            MenuToggle.Header = _isNotchVisible ? "Hide Notch" : "Show Notch";
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

    private bool IsForegroundWindowFullscreen(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || hwnd == _hwnd)
        {
            return false;
        }

        if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || IsWindowCloaked(hwnd))
        {
            return false;
        }

        if (!TryGetWindowBounds(hwnd, out var windowRect))
        {
            return false;
        }

        int width = windowRect.Right - windowRect.Left;
        int height = windowRect.Bottom - windowRect.Top;
        if (width < 200 || height < 120)
        {
            return false;
        }

        
        var classNameBuilder = new StringBuilder(128);
        if (GetClassName(hwnd, classNameBuilder, classNameBuilder.Capacity) > 0)
        {
            string className = classNameBuilder.ToString();
            if (string.Equals(className, "Progman", StringComparison.Ordinal) ||
                string.Equals(className, "WorkerW", StringComparison.Ordinal) ||
                string.Equals(className, "Shell_TrayWnd", StringComparison.Ordinal))
            {
                return false;
            }
        }

        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var monitorInfo = new MONITORINFO
        {
            cbSize = Marshal.SizeOf<MONITORINFO>()
        };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return false;
        }

        var monitorRect = monitorInfo.rcMonitor;
        const int fullscreenTolerancePx = 4;
        bool coversMonitor = RectCoversArea(windowRect, monitorRect, fullscreenTolerancePx);
        if (coversMonitor)
        {
            return true;
        }

        int style = GetWindowLong(hwnd, GWL_STYLE);
        bool hasCaption = (style & WS_CAPTION) == WS_CAPTION;
        bool hasResizeFrame = (style & WS_THICKFRAME) != 0;
        bool isBorderless = !hasCaption && !hasResizeFrame;

        var placement = new WINDOWPLACEMENT
        {
            length = Marshal.SizeOf<WINDOWPLACEMENT>()
        };
        bool isMaximized = GetWindowPlacement(hwnd, ref placement) && placement.showCmd == SW_SHOWMAXIMIZED;

        const int workAreaTolerancePx = 6;
        bool matchesWorkArea = RectMatchesArea(windowRect, monitorInfo.rcWork, workAreaTolerancePx);
        bool isWindowedFullscreen = matchesWorkArea && (isBorderless || (isMaximized && !hasCaption));

        return isWindowedFullscreen;
    }

    private static bool TryGetWindowBounds(IntPtr hwnd, out RECT rect)
    {
        if (DwmGetWindowAttributeRect(
                hwnd,
                DWMWA_EXTENDED_FRAME_BOUNDS,
                out rect,
                Marshal.SizeOf<RECT>()) == 0)
        {
            return true;
        }

        return GetWindowRect(hwnd, out rect);
    }

    private static bool IsWindowCloaked(IntPtr hwnd)
    {
        return DwmGetWindowAttributeInt(
                   hwnd,
                   DWMWA_CLOAKED,
                   out int cloaked,
                   sizeof(int)) == 0 &&
               cloaked != 0;
    }

    private static bool RectCoversArea(RECT rect, RECT area, int tolerancePx)
    {
        return rect.Left <= area.Left + tolerancePx &&
               rect.Top <= area.Top + tolerancePx &&
               rect.Right >= area.Right - tolerancePx &&
               rect.Bottom >= area.Bottom - tolerancePx;
    }

    private static bool RectMatchesArea(RECT rect, RECT area, int tolerancePx)
    {
        return Math.Abs(rect.Left - area.Left) <= tolerancePx &&
               Math.Abs(rect.Top - area.Top) <= tolerancePx &&
               Math.Abs(rect.Right - area.Right) <= tolerancePx &&
               Math.Abs(rect.Bottom - area.Bottom) <= tolerancePx;
    }

    private void EnsureTopmost()
    {
        EnsureTopmost(force: false);
    }

    private void EnsureTopmost(bool force)
    {
        if (_hwnd == IntPtr.Zero || !IsEffectivelyNotchVisible)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (!force && (now - _lastTopmostAssertUtc) < TimeSpan.FromMilliseconds(80))
        {
            return;
        }

        if (force || GetWindow(_hwnd, GW_HWNDPREV) != IntPtr.Zero)
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
        var hasWindowAbove = GetWindow(_hwnd, GW_HWNDPREV) != IntPtr.Zero;

        if (burstActive || hasWindowAbove)
        {
            EnsureTopmost(force: burstActive);
        }
    }

    private void ZOrderFastTimer_Tick(object? sender, EventArgs e)
    {
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
        if (_hwnd == IntPtr.Zero || !IsEffectivelyNotchVisible)
        {
            return;
        }

        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private static string TryGetProcessName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return string.Empty;
        }

        _ = GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0)
        {
            return string.Empty;
        }

        try
        {
            return System.Diagnostics.Process.GetProcessById((int)processId).ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

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

    private void ApplySettings()
    {
        NotchBorder.Width = _settings.Width;
        NotchBorder.Height = _settings.Height;
        var cr = new CornerRadius(0, 0, _settings.CornerRadius, _settings.CornerRadius);
        NotchBorder.CornerRadius = cr;
        InnerClipBorder.CornerRadius = cr;
        UpdateNotchClip();
        MediaBackground.CornerRadius = cr;
        this.Opacity = _settings.Opacity;

        _collapsedWidth = _settings.Width;
        _collapsedHeight = _settings.Height;
        _cornerRadiusCollapsed = _settings.CornerRadius;
        _cachedThumbnailExpandTarget = null;

        CameraIndicator.Visibility = _settings.ShowCameraIndicator ? Visibility.Visible : Visibility.Collapsed;
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
    }

    private void NotchWrapper_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_isExpanded && !_isAnimating && !_isSecondaryView)
        {
            _hoverCollapseTimer.Start();
        }
        else if (!_isExpanded)
        {
            AnimateNotchHover(false);
        }
    }

    private void CompactThumbnailBorder_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!_isExpanded && !_isAnimating && _isMusicCompactMode)
        {
            _hoverThumbnailDelayTimer.Start();
        }
    }

    private void CompactThumbnailBorder_MouseLeave(object sender, MouseEventArgs e)
    {
        _hoverThumbnailDelayTimer.Stop();
        if (!_isExpanded && !_isAnimating && _isMusicCompactMode)
        {
            AnimateThumbnailHover(false);
        }
    }

    #endregion

    #region Battery & Calendar

    private void Battery_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:batterysaver") { UseShellExecute = true });
        }
        catch { }
    }

    private void Settings_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            e.Handled = true;
            return;
        }

        try
        {
            // Open Windows Settings
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ms-settings:",
                UseShellExecute = true
            });
        }
        catch { }
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
        var dur = TimeSpan.FromMilliseconds(300);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        // Rotation animation - smooth 90 degree rotation from 45° to 135°
        var rotateAnim = new DoubleAnimation
        {
            To = isEnter ? 135 : 45,
            Duration = TimeSpan.FromMilliseconds(400),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        Timeline.SetDesiredFrameRate(rotateAnim, 120);

        // Opacity animation - subtle fade
        var opacityAnim = new DoubleAnimation
        {
            To = isEnter ? 0.8 : 1.0,
            Duration = dur,
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(opacityAnim, 120);

        SettingsRotate.BeginAnimation(RotateTransform.AngleProperty, rotateAnim);
        SettingsButton.BeginAnimation(OpacityProperty, opacityAnim);
        
        // Animate background color
        if (isEnter)
        {
            SettingsButton.Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        }
        else
        {
            SettingsButton.Background = new SolidColorBrush(Colors.Transparent);
        }
    }

    private void UpdateTimer_Tick(object? sender, EventArgs e)
    {
        if (_isMusicExpanded) SyncVolumeFromActiveSession();
        EnsureTopmost();
    }

    private void BatteryModule_BatteryUpdated(object? sender, BatteryInfo battery)
    {
        BatteryPercent.Text = battery.GetPercentageText();

        // Animate battery fill width with smooth easing
        double targetWidth = Math.Max(1.08, battery.Percentage / 100.0 * 22.8);
        var widthAnimation = new DoubleAnimation
        {
            To = targetWidth,
            Duration = TimeSpan.FromMilliseconds(300),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        BatteryFill.BeginAnimation(WidthProperty, widthAnimation);

        // Determine battery state and colors
        SolidColorBrush fillBrush;
        SolidColorBrush percentBrush;
        bool showLightning = false;

        if (battery.Percentage <= 20 && !battery.IsCharging)
        {
            // Low battery (red)
            fillBrush = _brushLowBattery;
            percentBrush = _brushLowBattery;
            showLightning = false;
        }
        else if (battery.IsCharging)
        {
            // Charging (green + lightning)
            fillBrush = _brushCharging;
            percentBrush = _brushWhite;
            showLightning = true;
        }
        else
        {
            // Normal (white)
            fillBrush = _brushWhite;
            percentBrush = _brushWhite;
            showLightning = false;
        }

        // Animate color transitions
        AnimateBrushTransition(BatteryFill, fillBrush);
        AnimateBrushTransition(BatteryPercent, percentBrush);

        // Animate lightning bolt
        AnimateChargingBolt(showLightning);

        // Add subtle pulse animation when charging
        if (battery.IsCharging)
        {
            StartChargingPulse();
        }
        else
        {
            StopChargingPulse();
        }
    }

    private void AnimateBrushTransition(FrameworkElement element, SolidColorBrush targetBrush)
    {
        var currentBrush = element is TextBlock tb ? tb.Foreground as SolidColorBrush : 
                          element is Border border ? border.Background as SolidColorBrush : null;
        
        if (currentBrush == null || currentBrush.Color == targetBrush.Color) 
        {
            if (element is TextBlock textBlock)
                textBlock.Foreground = targetBrush;
            else if (element is Border borderElement)
                borderElement.Background = targetBrush;
            return;
        }

        var colorAnimation = new ColorAnimation
        {
            To = targetBrush.Color,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };

        var animatedBrush = new SolidColorBrush(currentBrush.Color);
        if (element is TextBlock textBlockElement)
            textBlockElement.Foreground = animatedBrush;
        else if (element is Border borderElement2)
            borderElement2.Background = animatedBrush;

        animatedBrush.BeginAnimation(SolidColorBrush.ColorProperty, colorAnimation);
    }

    private void AnimateChargingBolt(bool show)
    {
        var opacityAnimation = new DoubleAnimation
        {
            To = show ? 1.0 : 0.0,
            Duration = TimeSpan.FromMilliseconds(250),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var scaleAnimation = new DoubleAnimation
        {
            To = show ? 1.0 : 0.95,
            Duration = TimeSpan.FromMilliseconds(250),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        ChargingBolt.BeginAnimation(OpacityProperty, opacityAnimation);
        ChargingBoltScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
        ChargingBoltScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation);
    }

    private Storyboard? _chargingPulseStoryboard;

    private void StartChargingPulse()
    {
        if (_chargingPulseStoryboard != null) return; // Already running

        _chargingPulseStoryboard = new Storyboard
        {
            RepeatBehavior = RepeatBehavior.Forever
        };

        var pulseAnimation = new DoubleAnimation
        {
            From = 1.0,
            To = 0.85,
            Duration = TimeSpan.FromMilliseconds(1000),
            AutoReverse = true,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };

        Storyboard.SetTarget(pulseAnimation, BatteryFill);
        Storyboard.SetTargetProperty(pulseAnimation, new PropertyPath("Opacity"));
        _chargingPulseStoryboard.Children.Add(pulseAnimation);

        _chargingPulseStoryboard.Begin();
    }

    private void StopChargingPulse()
    {
        if (_chargingPulseStoryboard == null) return;

        _chargingPulseStoryboard.Stop();
        _chargingPulseStoryboard = null;

        // Reset opacity
        var resetAnimation = new DoubleAnimation
        {
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        BatteryFill.BeginAnimation(OpacityProperty, resetAnimation);
    }

    private void UpdateBatteryInfo()
    {
        
    }




    private void InitializeCalendar()
    {
        if (_calendarInitialized) return;

        WeekDaysPanel.Children.Clear();
        WeekNumbers.Children.Clear();

        
        for (int i = 0; i < CalendarTotalDays; i++)
        {
            _calendarDayNames[i] = new TextBlock
            {
                Style = (Style)FindResource("SmallText"),
                FontSize = 9,
                Width = CalendarCellWidth,
                TextAlignment = TextAlignment.Center,
                FontWeight = FontWeights.Bold
            };
            WeekDaysPanel.Children.Add(_calendarDayNames[i]);

            _calendarDayNumbers[i] = new TextBlock
            {
                Style = (Style)FindResource("TitleText"),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            _calendarDayBorders[i] = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness((CalendarCellWidth - 24) / 2, 0, (CalendarCellWidth - 24) / 2, 0),
                Child = _calendarDayNumbers[i]
            };
            WeekNumbers.Children.Add(_calendarDayBorders[i]);
        }

        
        
        
        

        
        
        
        
        _currentCalendarCenterIdx = 5;
        _calendarScrollX = (1 * CalendarCellWidth) - (_currentCalendarCenterIdx * CalendarCellWidth);
        CalendarStripTranslate.X = _calendarScrollX;
        CalendarHighlightTranslate.X = _currentCalendarCenterIdx * CalendarCellWidth + (CalendarCellWidth - 24) / 2.0;

        _calendarInitialized = true;
    }

    private void CalendarModule_CalendarUpdated(object? sender, CalendarUpdateEventArgs e)
    {
        if (!_calendarInitialized) InitializeCalendar();

        var now = e.Now;
        _lastCalendarUpdate = now;

        var dayNames = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };

        
        for (int i = -5; i <= 5; i++)
        {
            int idx = i + 5;
            var date = now.AddDays(i);

            _calendarDayNames[idx].Text = dayNames[(int)date.DayOfWeek];
            _calendarDayNumbers[idx].Text = date.Day.ToString();
        }

        UpdateCalendarHighlight(animate: false, pulse: false);
        EventText.Text = "Enjoy your day!";
    }

    private int GetCalendarCenterIndexFromStripX(double stripX)
    {
        int centerIdx = (int)Math.Round((30.0 - stripX) / CalendarCellWidth);
        return Math.Max(0, Math.Min(CalendarTotalDays - 1, centerIdx));
    }

    private static double GetCalendarHighlightXForIndex(int centerIdx)
    {
        return centerIdx * CalendarCellWidth + (CalendarCellWidth - 24) / 2.0;
    }

    private void ApplyCalendarCenterVisualState(int centerIdx)
    {
        var highlightedDate = _lastCalendarUpdate.AddDays(centerIdx - 5);
        string newMonth = highlightedDate.ToString("MMM");
        
        // Animate month text change with morph effect
        if (MonthText.Text != newMonth)
        {
            AnimateMonthTextChange(newMonth);
        }

        for (int i = 0; i < CalendarTotalDays; i++)
        {
            _calendarDayNumbers[i].Foreground = (i == centerIdx) ? _brushBlack : _brushWhite;
            _calendarDayBorders[i].Background = _brushTransparent;
        }
    }

    private void AnimateMonthTextChange(string newMonth)
    {
        // If animation is running, queue the new month text
        if (_isMonthAnimating)
        {
            _pendingMonthText = newMonth;
            return;
        }

        _isMonthAnimating = true;
        _pendingMonthText = string.Empty;

        var duration = new Duration(TimeSpan.FromMilliseconds(280));
        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };

        // Fade out + scale down + slide up
        var fadeOut = new DoubleAnimation
        {
            From = 1.0,
            To = 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(140)),
            EasingFunction = easeIn
        };

        var scaleDown = new DoubleAnimation
        {
            From = 1.0,
            To = 0.85,
            Duration = new Duration(TimeSpan.FromMilliseconds(140)),
            EasingFunction = easeIn
        };

        var slideUp = new DoubleAnimation
        {
            From = 0,
            To = -8,
            Duration = new Duration(TimeSpan.FromMilliseconds(140)),
            EasingFunction = easeIn
        };

        fadeOut.Completed += (s, e) =>
        {
            MonthText.Text = newMonth;

            // Fade in + scale up + slide down
            var fadeIn = new DoubleAnimation
            {
                From = 0.0,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(140)),
                EasingFunction = easeOut
            };

            var scaleUp = new DoubleAnimation
            {
                From = 0.85,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(140)),
                EasingFunction = easeOut
            };

            var slideDown = new DoubleAnimation
            {
                From = 8,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(140)),
                EasingFunction = easeOut
            };

            fadeIn.Completed += (s2, e2) =>
            {
                _isMonthAnimating = false;
                
                // If there's a pending month change, trigger it now
                if (!string.IsNullOrEmpty(_pendingMonthText) && _pendingMonthText != MonthText.Text)
                {
                    AnimateMonthTextChange(_pendingMonthText);
                }
            };

            Timeline.SetDesiredFrameRate(fadeIn, 60);
            Timeline.SetDesiredFrameRate(scaleUp, 60);
            Timeline.SetDesiredFrameRate(slideDown, 60);

            MonthText.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            MonthTextScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleUp);
            MonthTextScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleUp);
            MonthTextTranslate.BeginAnimation(TranslateTransform.YProperty, slideDown);
        };

        Timeline.SetDesiredFrameRate(fadeOut, 60);
        Timeline.SetDesiredFrameRate(scaleDown, 60);
        Timeline.SetDesiredFrameRate(slideUp, 60);

        MonthText.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        MonthTextScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDown);
        MonthTextScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDown);
        MonthTextTranslate.BeginAnimation(TranslateTransform.YProperty, slideUp);
    }

    private void AnimateCalendarHighlightToIndex(int centerIdx, Duration duration, IEasingFunction easing, bool pulse)
    {
        double targetHighlightX = GetCalendarHighlightXForIndex(centerIdx);
        double currentHighlightX = (double)CalendarHighlightTranslate.GetValue(TranslateTransform.XProperty);

        CalendarHighlightScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CalendarHighlightScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CalendarHighlightTranslate.BeginAnimation(TranslateTransform.YProperty, null);

        var moveAnim = new DoubleAnimation
        {
            From = currentHighlightX,
            To = targetHighlightX,
            Duration = duration,
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(moveAnim, 120);
        CalendarHighlightTranslate.BeginAnimation(TranslateTransform.XProperty, moveAnim, HandoffBehavior.SnapshotAndReplace);

        if (!pulse)
        {
            CalendarHighlightScale.ScaleX = 1.0;
            CalendarHighlightScale.ScaleY = 1.0;
            CalendarHighlightTranslate.Y = 0.0;
            return;
        }

        
        var squashX = new DoubleAnimationUsingKeyFrames { Duration = duration };
        squashX.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(0.0)));
        squashX.KeyFrames.Add(new EasingDoubleKeyFrame(1.085, KeyTime.FromPercent(0.28), _easeSineInOut));
        squashX.KeyFrames.Add(new EasingDoubleKeyFrame(0.975, KeyTime.FromPercent(0.62), _easeSineInOut));
        squashX.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), _easeSineInOut));
        Timeline.SetDesiredFrameRate(squashX, 120);

        var squashY = new DoubleAnimationUsingKeyFrames { Duration = duration };
        squashY.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(0.0)));
        squashY.KeyFrames.Add(new EasingDoubleKeyFrame(0.93, KeyTime.FromPercent(0.28), _easeSineInOut));
        squashY.KeyFrames.Add(new EasingDoubleKeyFrame(1.02, KeyTime.FromPercent(0.62), _easeSineInOut));
        squashY.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), _easeSineInOut));
        Timeline.SetDesiredFrameRate(squashY, 120);

        CalendarHighlightTranslate.Y = 0.0;
        CalendarHighlightScale.BeginAnimation(ScaleTransform.ScaleXProperty, squashX, HandoffBehavior.SnapshotAndReplace);
        CalendarHighlightScale.BeginAnimation(ScaleTransform.ScaleYProperty, squashY, HandoffBehavior.SnapshotAndReplace);
    }

    private void UpdateCalendarHighlight(bool animate = true, bool pulse = false)
    {
        if (!_calendarInitialized) return;

        double currentX = (double)CalendarStripTranslate.GetValue(TranslateTransform.XProperty);
        int centerIdx = GetCalendarCenterIndexFromStripX(currentX);
        ApplyCalendarCenterVisualState(centerIdx);

        if (animate)
        {
            AnimateCalendarHighlightToIndex(centerIdx, new Duration(TimeSpan.FromMilliseconds(340)), _easeQuadInOut, pulse);
        }
        else
        {
            CalendarHighlightTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            CalendarHighlightTranslate.X = GetCalendarHighlightXForIndex(centerIdx);
            CalendarHighlightScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CalendarHighlightScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            CalendarHighlightScale.ScaleX = 1.0;
            CalendarHighlightScale.ScaleY = 1.0;
        }
    }

    private void UpdateCalendarInfo()
    {
        
    }

    #endregion

    #region Calendar Hover & Scroll

    private void CalendarWidget_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        AnimateCalendarWidgetHover(isHovered: true);
        AnimateCalendarContextFocus(isFocused: true);
    }

    private void CalendarWidget_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        AnimateCalendarWidgetHover(isHovered: false);
        AnimateCalendarContextFocus(isFocused: false);
    }

    private void AnimateCalendarWidgetHover(bool isHovered)
    {
        
        var duration = new Duration(TimeSpan.FromMilliseconds(isHovered ? 350 : 420));
        double currentScaleX = (double)CalendarWidgetScale.GetValue(ScaleTransform.ScaleXProperty);
        double currentScaleY = (double)CalendarWidgetScale.GetValue(ScaleTransform.ScaleYProperty);
        double currentLiftY = (double)CalendarWidgetTranslate.GetValue(TranslateTransform.YProperty);

        var scaleXAnim = new DoubleAnimationUsingKeyFrames { Duration = duration };
        var scaleYAnim = new DoubleAnimationUsingKeyFrames { Duration = duration };
        var liftAnim = new DoubleAnimationUsingKeyFrames { Duration = duration };

        scaleXAnim.KeyFrames.Add(new EasingDoubleKeyFrame(currentScaleX, KeyTime.FromPercent(0.0)));
        scaleYAnim.KeyFrames.Add(new EasingDoubleKeyFrame(currentScaleY, KeyTime.FromPercent(0.0)));
        liftAnim.KeyFrames.Add(new EasingDoubleKeyFrame(currentLiftY, KeyTime.FromPercent(0.0)));

        if (isHovered)
        {
            scaleXAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.135, KeyTime.FromPercent(0.40), _easeSineInOut));
            scaleYAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0.955, KeyTime.FromPercent(0.40), _easeSineInOut));
            scaleXAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.095, KeyTime.FromPercent(0.72), _easeSineInOut));
            scaleYAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.085, KeyTime.FromPercent(0.72), _easeSineInOut));
            scaleXAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.105, KeyTime.FromPercent(1.0), _easeSineInOut));
            scaleYAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.105, KeyTime.FromPercent(1.0), _easeSineInOut));

            liftAnim.KeyFrames.Add(new EasingDoubleKeyFrame(-3.7, KeyTime.FromPercent(0.48), _easeSineInOut));
            liftAnim.KeyFrames.Add(new EasingDoubleKeyFrame(-3.1, KeyTime.FromPercent(1.0), _easeSineInOut));
        }
        else
        {
            scaleXAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0.985, KeyTime.FromPercent(0.42), _easeSineInOut));
            scaleYAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.02, KeyTime.FromPercent(0.42), _easeSineInOut));
            scaleXAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), _easeSineInOut));
            scaleYAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), _easeSineInOut));

            liftAnim.KeyFrames.Add(new EasingDoubleKeyFrame(-0.6, KeyTime.FromPercent(0.42), _easeSineInOut));
            liftAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromPercent(1.0), _easeSineInOut));
        }

        Timeline.SetDesiredFrameRate(scaleXAnim, 120);
        Timeline.SetDesiredFrameRate(scaleYAnim, 120);
        Timeline.SetDesiredFrameRate(liftAnim, 120);

        CalendarWidgetScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim, HandoffBehavior.SnapshotAndReplace);
        CalendarWidgetScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim, HandoffBehavior.SnapshotAndReplace);
        CalendarWidgetTranslate.BeginAnimation(TranslateTransform.YProperty, liftAnim, HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimateCalendarContextFocus(bool isFocused)
    {
        
        var duration = new Duration(TimeSpan.FromMilliseconds(isFocused ? 450 : 360));
        var easing = (IEasingFunction)_easeSineInOut;

        
        AnimateOpacity(BatterySection, isFocused ? 0.62 : 1.0, duration, easing);
        AnimateOpacity(GreetingSection, isFocused ? 0.62 : 1.0, duration, easing);
        AnimateBlurRadius(CalendarGreetingContextBlur, isFocused ? 4.0 : 0.0, duration, easing);
    }

    private static void AnimateOpacity(UIElement element, double to, Duration duration, IEasingFunction easing)
    {
        var anim = new DoubleAnimation
        {
            From = (double)element.GetValue(UIElement.OpacityProperty),
            To = to,
            Duration = duration,
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(anim, 120);
        element.BeginAnimation(UIElement.OpacityProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    private static void AnimateBlurRadius(BlurEffect effect, double to, Duration duration, IEasingFunction easing)
    {
        var anim = new DoubleAnimation
        {
            From = (double)effect.GetValue(BlurEffect.RadiusProperty),
            To = to,
            Duration = duration,
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(anim, 120);
        effect.BeginAnimation(BlurEffect.RadiusProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    private void ResetCalendarHoverFocusVisualState()
    {
        CalendarWidgetScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CalendarWidgetScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CalendarWidgetTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        CalendarWidgetScale.ScaleX = 1.0;
        CalendarWidgetScale.ScaleY = 1.0;
        CalendarWidgetTranslate.Y = 0.0;

        ResetCalendarContextElement(BatterySection, null);
        ResetCalendarContextElement(GreetingSection, CalendarGreetingContextBlur);
    }

    private static void ResetCalendarContextElement(UIElement element, BlurEffect? effect)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1.0;
        if (effect != null)
        {
            effect.BeginAnimation(BlurEffect.RadiusProperty, null);
            effect.Radius = 0.0;
        }
    }

    private void CalendarWidget_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (!_calendarInitialized) return;

        
        _calendarScrollAccumulator += e.Delta;
        int direction = _calendarScrollAccumulator > 0 ? -1 : 1; 
        int stepCount = (int)(Math.Abs(_calendarScrollAccumulator) / 120.0);
        if (stepCount == 0 && Math.Abs(_calendarScrollAccumulator) >= 72)
        {
            stepCount = 1;
        }
        if (stepCount == 0)
        {
            e.Handled = true;
            return;
        }

        _calendarScrollAccumulator -= Math.Sign(_calendarScrollAccumulator) * stepCount * 120.0;

        
        if ((DateTime.Now - _lastCalendarScrollTime).TotalMilliseconds < 70)
        {
            e.Handled = true;
            return;
        }
        _lastCalendarScrollTime = DateTime.Now;

        int oldIdx = _currentCalendarCenterIdx;
        int newIdx = _currentCalendarCenterIdx + (direction * stepCount);
        newIdx = Math.Max(0, Math.Min(CalendarTotalDays - 1, newIdx));
        
        if (newIdx == oldIdx) 
        {
            e.Handled = true;
            return;
        }

        _currentCalendarCenterIdx = newIdx;
        double newX = (1 * CalendarCellWidth) - (_currentCalendarCenterIdx * CalendarCellWidth);
        _calendarScrollX = newX;

        double currentX = (double)CalendarStripTranslate.GetValue(TranslateTransform.XProperty);
        int movedCells = Math.Abs(newIdx - oldIdx);
        double durationMs = Math.Clamp(240 + (movedCells * 90), 240, 520);
        var duration = new Duration(TimeSpan.FromMilliseconds(durationMs));
        var easing = (IEasingFunction)_easeSoftSpring;

        var scrollAnim = new DoubleAnimation
        {
            From = currentX,
            To = newX,
            Duration = duration,
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(scrollAnim, 120);
        CalendarStripTranslate.BeginAnimation(TranslateTransform.XProperty, scrollAnim, HandoffBehavior.SnapshotAndReplace);

        ApplyCalendarCenterVisualState(_currentCalendarCenterIdx);
        AnimateCalendarHighlightToIndex(_currentCalendarCenterIdx, duration, easing, pulse: true);

        e.Handled = true;
    }

    public void ResetCalendarScroll()
    {
        if (!_calendarInitialized) return;

        _currentCalendarCenterIdx = 5; 
        double targetX = (1 * CalendarCellWidth) - (_currentCalendarCenterIdx * CalendarCellWidth);
        
        if (Math.Abs(_calendarScrollX - targetX) < 0.1) return;

        _calendarScrollX = targetX;
        _calendarScrollAccumulator = 0;

        double currentX = (double)CalendarStripTranslate.GetValue(TranslateTransform.XProperty);
        var duration = new Duration(TimeSpan.FromMilliseconds(420));
        var easing = (IEasingFunction)_easeSoftSpring;

        var scrollAnim = new DoubleAnimation
        {
            From = currentX,
            To = targetX,
            Duration = duration,
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(scrollAnim, 120);
        CalendarStripTranslate.BeginAnimation(TranslateTransform.XProperty, scrollAnim, HandoffBehavior.SnapshotAndReplace);

        ApplyCalendarCenterVisualState(_currentCalendarCenterIdx);
        AnimateCalendarHighlightToIndex(_currentCalendarCenterIdx, duration, easing, pulse: false);
    }

    #endregion

    #region Notch Clip

    private void NotchContent_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateNotchClip();
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
