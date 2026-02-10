using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using VNotch.Services;
using VNotch.Models;
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

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_NOACTIVATE = 0x08000000;
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

    #endregion

    #region Fields

    private readonly SettingsService _settingsService;
    private readonly NotchManager _notchManager;
    private readonly MediaDetectionService _mediaService;
    private readonly DispatcherTimer _updateTimer;
    private readonly DispatcherTimer _zOrderTimer;
    private readonly VolumeService _volumeService;
    private readonly DispatcherTimer _hoverCollapseTimer;
    private bool _isDraggingVolume = false;
    private NotchSettings _settings;
    private bool _isNotchVisible = true;
    private IntPtr _hwnd;
    private HwndSource? _hwndSource;

    // Animation state tracking
    private bool _isAnimating = false;
    private bool _isExpanded = false;
    private double _collapsedWidth;
    private double _collapsedHeight;
    private double _expandedWidth = 480;
    private double _expandedHeight = 155;
    private double _cornerRadiusCollapsed;
    private double _cornerRadiusExpanded = 24;

    // Fixed position
    private int _fixedX = 0;
    private int _fixedY = 0;
    private int _windowWidth = 0;
    private int _windowHeight = 0;

    // Current media state
    private MediaInfo? _currentMediaInfo;
    private bool _isMusicCompactMode = false;
    private DateTime _lastMediaActionTime = DateTime.MinValue;

    // Progress bar (timer defined here, logic in MainWindow.Progress.cs)
    private readonly DispatcherTimer _progressTimer;
    private DateTime _lastMediaUpdate = DateTime.Now;
    private TimeSpan _lastKnownPosition = TimeSpan.Zero;
    private TimeSpan _lastKnownDuration = TimeSpan.Zero;
    private bool _isMediaPlaying = false;
    private DateTime _seekDebounceUntil = DateTime.MinValue;

    #endregion

    public MainWindow()
    {
        InitializeComponent();

        _settingsService = new SettingsService();
        _settings = _settingsService.Load();
        _notchManager = new NotchManager(this, _settings);
        _mediaService = new MediaDetectionService();
        _volumeService = new VolumeService();

        // Store dimensions
        _collapsedWidth = _settings.Width;
        _collapsedHeight = _settings.Height;
        _cornerRadiusCollapsed = _settings.CornerRadius;

        // Setup update timer for battery & calendar only
        _updateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _updateTimer.Tick += UpdateTimer_Tick;
        
        // Setup Z-order timer - fights for top spot every 500ms
        _zOrderTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _zOrderTimer.Tick += (s, e) => EnsureTopmost();
        _zOrderTimer.Start();
        
        // Setup progress timer for realtime progress bar (60fps for maximum smoothness)
        _progressTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _progressTimer.Tick += ProgressTimer_Tick;

        // Setup hover collapse timer (1.0 second delay)
        _hoverCollapseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1000)
        };
        _hoverCollapseTimer.Tick += (s, e) => {
            _hoverCollapseTimer.Stop();
            if (_isExpanded && !NotchWrapper.IsMouseOver)
            {
                CollapseNotch();
            }
        };

        Loaded += MainWindow_Loaded;
        Deactivated += MainWindow_Deactivated;

        // Subscribe to media changes
        _mediaService.MediaChanged += OnMediaChanged;
    }

    #region Window Lifecycle

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);

        TrayIcon.Icon = IconGenerator.CreateNotchIcon(16);
        
        ApplySettings();
        ConfigureOverlayWindow();
        PositionAtTop();

        _mediaService.Start();
        _updateTimer.Start();

        UpdateBatteryInfo();
        UpdateCalendarInfo();
        PlayAppearAnimation();
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
                SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, 
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                break;

            case WM_ACTIVATEAPP:
                if (wParam == IntPtr.Zero)
                {
                    Dispatcher.BeginInvoke(new Action(() => {
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
        if ((_isExpanded || _isMusicExpanded) && !_isAnimating)
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
        _mediaService?.Dispose();
        _notchManager?.Dispose();
        TrayIcon?.Dispose();
        _updateTimer?.Stop();
        _zOrderTimer?.Stop();
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
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle);
        EnsureTopmost();
    }

    private void EnsureTopmost()
    {
        if (_hwnd != IntPtr.Zero)
        {
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, 
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
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

        CameraIndicator.Visibility = _settings.ShowCameraIndicator ? Visibility.Visible : Visibility.Collapsed;
    }

    #endregion

    #region Click & Hover Handling

    private void NotchBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isAnimating) return;

        if (_isExpanded)
            CollapseNotch();
        else
            ExpandNotch();
        
        e.Handled = true;
    }

    private void NotchWrapper_MouseEnter(object sender, MouseEventArgs e)
    {
        _hoverCollapseTimer.Stop();
        if (!_isExpanded && !_isAnimating)
        {
            ExpandNotch();
        }
    }

    private void NotchWrapper_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_isExpanded && !_isAnimating)
        {
            _hoverCollapseTimer.Start();
        }
    }

    #endregion

    #region Battery & Calendar

    private void UpdateTimer_Tick(object? sender, EventArgs e)
    {
        UpdateBatteryInfo();
        UpdateCalendarInfo();
        
        if (_isMusicExpanded) SyncVolumeFromSystem();
        EnsureTopmost();
    }

    private void UpdateBatteryInfo()
    {
        try
        {
            var battery = BatteryService.GetBatteryInfo();

            BatteryPercent.Text = battery.GetPercentageText();

            double fillWidth = Math.Max(2, battery.Percentage / 100.0 * 26);
            BatteryFill.Width = fillWidth;

            if (battery.IsCharging)
            {
                BatteryFill.Background = new SolidColorBrush(Color.FromRgb(48, 209, 88));
                BatteryPercent.Foreground = new SolidColorBrush(Colors.White);
            }
            else if (battery.Percentage < 20)
            {
                BatteryFill.Background = new SolidColorBrush(Color.FromRgb(255, 59, 48));
                BatteryPercent.Foreground = new SolidColorBrush(Color.FromRgb(255, 59, 48));
            }
            else
            {
                BatteryFill.Background = new SolidColorBrush(Colors.White);
                BatteryPercent.Foreground = new SolidColorBrush(Colors.White);
            }
        }
        catch
        {
            BatteryPercent.Text = "N/A";
        }
    }

    private void UpdateCalendarInfo()
    {
        var now = DateTime.Now;
        MonthText.Text = now.ToString("MMM");
        DayText.Text = now.Day.ToString();

        var dayNames = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        
        WeekDaysPanel.Children.Clear();
        WeekNumbers.Children.Clear();
        
        for (int i = -1; i <= 1; i++)
        {
            var date = now.AddDays(i);
            var dayOfWeek = (int)date.DayOfWeek;
            
            var dayNameText = new TextBlock
            {
                Text = dayNames[dayOfWeek],
                Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102)),
                FontSize = 9,
                Width = 26,
                TextAlignment = TextAlignment.Center,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("pack://application:,,,/Fonts/#SF Pro Display")
            };
            WeekDaysPanel.Children.Add(dayNameText);
            
            var border = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Margin = new Thickness(2, 0, 2, 0),
                Background = i == 0 ? 
                    new SolidColorBrush(Colors.White) : 
                    new SolidColorBrush(Colors.Transparent)
            };

            var text = new TextBlock
            {
                Text = date.Day.ToString(),
                Foreground = i == 0 ? 
                    new SolidColorBrush(Colors.Black) : 
                    new SolidColorBrush(Colors.White),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("pack://application:,,,/Fonts/#SF Pro Display"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            border.Child = text;
            WeekNumbers.Children.Add(border);
        }

        EventText.Text = "Enjoy your day!";
        EventText.Foreground = new SolidColorBrush(Colors.White);
        EventText.FontWeight = FontWeights.Bold;
        EventText.Margin = new Thickness(3, 6, 0, 0);
        EventText.FontFamily = new FontFamily("pack://application:,,,/Fonts/#SF Pro Display");
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
        NotchContainer.Visibility = _isNotchVisible ? Visibility.Visible : Visibility.Collapsed;
        MenuToggle.Header = _isNotchVisible ? "Ẩn Notch" : "Hiện Notch";
    }

    private void ResetPosition_Click(object sender, RoutedEventArgs e)
    {
        ResetPosition();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_isExpanded)
        {
            CollapseNotch();
        }

        var settingsWindow = new SettingsWindow(_settings, _settingsService);
        settingsWindow.SettingsChanged += (s, newSettings) =>
        {
            _settings = newSettings;
            ApplySettings();
            _notchManager.UpdateSettings(_settings);
        };
        settingsWindow.ShowDialog();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        _hwndSource?.RemoveHook(WndProc);
        
        _mediaService.Dispose();
        _notchManager.Dispose();
        TrayIcon.Dispose();
        _updateTimer.Stop();
        _zOrderTimer.Stop();
        System.Windows.Application.Current.Shutdown();
    }

    #endregion

    // Media Progress Tracking -> MainWindow.Progress.cs
    // Animation logic (ExpandNotch, CollapseNotch, etc.) -> MainWindow.Animation.cs
    // Media Controls (PlayPause, Next, Prev, Volume) -> MainWindow.Controls.cs
    // Marquee text scrolling -> MainWindow.Marquee.cs
    // Media Changed handler & Compact Mode -> MainWindow.Media.cs
    // Media Background & Color Extraction -> MainWindow.MediaBackground.cs
}
