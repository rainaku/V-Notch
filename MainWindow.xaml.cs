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
    private readonly SettingsService _settingsService;
    private readonly NotchManager _notchManager;
    private readonly MediaDetectionService _mediaService;
    private readonly DispatcherTimer _updateTimer;
    private readonly DispatcherTimer _zOrderTimer; // Dedicated timer to fight for Z-order
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
    private double _expandedHeight = 155; // Increased to show progress bar
    private double _cornerRadiusCollapsed;
    private double _cornerRadiusExpanded = 24;

    // Fixed position
    private int _fixedX = 0;
    private int _fixedY = 0;
    private int _windowWidth = 0;
    private int _windowHeight = 0;
    
    // Current media state
    private MediaInfo? _currentMediaInfo;
    
    // Progress bar realtime tracking (used in MainWindow.Progress.cs)
    private readonly DispatcherTimer _progressTimer;
    private DateTime _lastMediaUpdate = DateTime.Now;
    private TimeSpan _lastKnownPosition = TimeSpan.Zero;
    private TimeSpan _lastKnownDuration = TimeSpan.Zero;
    private bool _isMediaPlaying = false;
    private DateTime _seekDebounceUntil = DateTime.MinValue; // Ignore API updates until this time
    
    // Marquee animation for long titles (timer-based)
    private DispatcherTimer? _marqueeTimer;
    private double _titleScrollOffset = 0;
    private double _artistScrollOffset = 0;
    private double _titleScrollDistance = 0;
    private double _artistScrollDistance = 0;
    private bool _titleScrollForward = true;
    private bool _artistScrollForward = true;
    private DateTime _titlePauseUntil = DateTime.MinValue;
    private DateTime _artistPauseUntil = DateTime.MinValue;
    private string _lastTitleText = "";
    private string _lastArtistText = "";
    private DateTime _lastMediaActionTime = DateTime.MinValue;
    private bool _isMusicCompactMode = false; // New: tracking compact pill state

    #region Win32 APIs

    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int GWL_EXSTYLE = -20;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

    // Windows Messages
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const int WM_WINDOWPOSCHANGED = 0x0047;
    private const int WM_ACTIVATE = 0x0006;
    private const int WM_ACTIVATEAPP = 0x001C;
    private const int WM_DISPLAYCHANGE = 0x007E;

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

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    // For media key simulation
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
    private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
    private const byte VK_MEDIA_PREV_TRACK = 0xB1;
    private const byte VK_LEFT = 0x25;  // Left arrow key for seeking backward
    private const byte VK_RIGHT = 0x27; // Right arrow key for seeking forward
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);
    
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private const int VK_LBUTTON = 0x01;

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

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Get window handle
        _hwnd = new WindowInteropHelper(this).Handle;

        // Hook into Windows messages to prevent position changes
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);

        // Set tray icon
        TrayIcon.Icon = IconGenerator.CreateNotchIcon(16);
        
        // Apply settings
        ApplySettings();

        // Configure window for always-on-top overlay
        ConfigureOverlayWindow();

        // Position notch at top of screen (ONE TIME ONLY)
        PositionAtTop();

        // Start services
        _mediaService.Start();
        _updateTimer.Start();

        // Initial updates
        UpdateBatteryInfo();
        UpdateCalendarInfo();

        // Start with appear animation
        PlayAppearAnimation();
    }

    /// <summary>
    /// WndProc hook to prevent other apps from moving our window
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_WINDOWPOSCHANGING:
                // Intercept position changes and force our position
                if (lParam != IntPtr.Zero && _fixedY >= 0)
                {
                    var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                    
                    // Force Y position to 0 (top of screen)
                    pos.y = _fixedY;
                    pos.x = _fixedX;
                    
                    // Ensure we stay topmost
                    pos.hwndInsertAfter = HWND_TOPMOST;
                    
                    // Write back the modified structure
                    Marshal.StructureToPtr(pos, lParam, false);
                }
                break;

            case WM_ACTIVATE:
                // When activated, ensure topmost
                SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, 
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                break;

            case WM_ACTIVATEAPP:
                // If the application is being deactivated (wParam == 0)
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
                // Screen resolution changed, recalculate position
                Dispatcher.BeginInvoke(() => PositionAtTop());
                break;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Auto collapse when window loses focus (click outside)
    /// </summary>
    private void MainWindow_Deactivated(object? sender, EventArgs e)
    {
        // This may not trigger reliably with WS_EX_NOACTIVATE
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

    /// <summary>
    /// Configure window as highest overlay
    /// </summary>
    private void ConfigureOverlayWindow()
    {
        // Set extended window styles
        var exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW; 
        exStyle |= WS_EX_TOPMOST;    
        exStyle |= WS_EX_NOACTIVATE; 
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle);
        
        // Initial force to top
        EnsureTopmost();
    }

    /// <summary>
    /// Aggressively re-assert topmost status to stay above other "topmost" apps like MyDockfinder
    /// </summary>
    private void EnsureTopmost()
    {
        if (_hwnd != IntPtr.Zero)
        {
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, 
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
    }

    /// <summary>
    /// Position window at top center of screen - ONCE ONLY
    /// Uses Win32 SetWindowPos directly to bypass WPF working area calculations
    /// </summary>
    private void PositionAtTop()
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen == null) return;
        
        // Calculate dimensions
        _windowWidth = (int)(_expandedWidth + 40);
        _windowHeight = (int)(_expandedHeight + 20);
        
        // Calculate center X position
        _fixedX = screen.Bounds.Left + (screen.Bounds.Width - _windowWidth) / 2;
        
        // Use absolute Y = 0 (top of screen, not working area)
        _fixedY = 0;
        
        // Set WPF properties
        this.Width = _windowWidth;
        this.Height = _windowHeight;

        // Use Win32 SetWindowPos directly with absolute coordinates
        SetWindowPos(_hwnd, HWND_TOPMOST, _fixedX, _fixedY, _windowWidth, _windowHeight, SWP_NOACTIVATE);
    }

    /// <summary>
    /// Reset position to top - can be called from menu
    /// </summary>
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
        UpdateNotchClip();
        MediaBackground.CornerRadius = cr;
        this.Opacity = _settings.Opacity;

        _collapsedWidth = _settings.Width;
        _collapsedHeight = _settings.Height;
        _cornerRadiusCollapsed = _settings.CornerRadius;

        CameraIndicator.Visibility = _settings.ShowCameraIndicator ? Visibility.Visible : Visibility.Collapsed;
    }

    #region Click Handling

    private void NotchBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isAnimating) return;

        if (_isExpanded)
        {
            CollapseNotch();
        }
        else
        {
            ExpandNotch();
        }
        
        e.Handled = true;
    }

    private void NotchWrapper_MouseEnter(object sender, MouseEventArgs e)
    {
        // Stop any pending collapse
        _hoverCollapseTimer.Stop();

        if (!_isExpanded && !_isAnimating)
        {
            ExpandNotch();
        }
    }

    private void NotchWrapper_MouseLeave(object sender, MouseEventArgs e)
    {
        // Start counting to collapse
        if (_isExpanded && !_isAnimating)
        {
            _hoverCollapseTimer.Start();
        }
    }

    private void ExpandNotch()
    {
        if (_isAnimating || _isExpanded) return;
        _isAnimating = true;

        // Re-assert topmost before expanding
        EnsureTopmost();

        // Show expanded content
        ExpandedContent.Visibility = Visibility.Visible;

        var duration = TimeSpan.FromMilliseconds(350);
        // Velocity clearly faster at start, slower at end
        var easing = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 7 };

        // Width animation
        var widthAnim = new DoubleAnimation
        {
            To = _expandedWidth,
            Duration = duration,
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(widthAnim, 60);

        // Height animation
        var heightAnim = new DoubleAnimation
        {
            To = _expandedHeight,
            Duration = duration,
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(heightAnim, 60);

        // Content fade out (whichever is visible)
        var fadeOutAnim = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(100),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        // Content fade in (Immediate)
        var fadeInAnim = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        // Glow
        var glowAnim = new DoubleAnimation
        {
            To = 0.15,
            Duration = TimeSpan.FromMilliseconds(200)
        };

        heightAnim.Completed += (s, e) =>
        {
            _isAnimating = false;
            _isExpanded = true;
            UpdateBatteryInfo();
            UpdateCalendarInfo();
            
            // Animate progress bar from 0 to current position
            AnimateProgressBarOnExpand();
        };

        NotchBorder.BeginAnimation(WidthProperty, widthAnim);
        NotchBorder.BeginAnimation(HeightProperty, heightAnim);
        
        // Hide whichever collapsed state is active
        CollapsedContent.BeginAnimation(OpacityProperty, fadeOutAnim);
        MusicCompactContent.BeginAnimation(OpacityProperty, fadeOutAnim);
        
        ExpandedContent.BeginAnimation(OpacityProperty, fadeInAnim);
        HoverGlow.BeginAnimation(OpacityProperty, glowAnim);

        AnimateCornerRadius(_cornerRadiusExpanded, duration);
    }

    private void CollapseNotch()
    {
        if (_isAnimating || !_isExpanded) return;
        _isAnimating = true;
        
        // Re-assert topmost when collapsing
        EnsureTopmost();

        var duration = TimeSpan.FromMilliseconds(350);
        // Distinct snappy collapse
        var easing = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 7 };

        // Width animation
        var widthAnim = new DoubleAnimation
        {
            To = _collapsedWidth,
            Duration = duration,
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(widthAnim, 60);

        // Height animation
        var heightAnim = new DoubleAnimation
        {
            To = _collapsedHeight,
            Duration = duration,
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(heightAnim, 60);

        // Expanded content fade out
        var fadeOutAnim = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(80),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        fadeOutAnim.Completed += (s, e) =>
        {
            ExpandedContent.Visibility = Visibility.Collapsed;
        };

        // Which content to show when collapsing?
        FrameworkElement contentToShow = _isMusicCompactMode ? MusicCompactContent : CollapsedContent;
        FrameworkElement contentToHide = _isMusicCompactMode ? CollapsedContent : MusicCompactContent;
        
        contentToHide.Visibility = Visibility.Collapsed;
        contentToHide.Opacity = 0;

        // Content fade in (Immediate)
        var fadeInAnim = new DoubleAnimation
        {
            To = 1,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        // Glow
        var glowAnim = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(150)
        };

        heightAnim.Completed += (s, e) =>
        {
            _isAnimating = false;
            _isExpanded = false;
        };

        NotchBorder.BeginAnimation(WidthProperty, widthAnim);
        NotchBorder.BeginAnimation(HeightProperty, heightAnim);
        ExpandedContent.BeginAnimation(OpacityProperty, fadeOutAnim);
        
        contentToShow.Visibility = Visibility.Visible;
        contentToShow.BeginAnimation(OpacityProperty, fadeInAnim);
        HoverGlow.BeginAnimation(OpacityProperty, glowAnim);

        AnimateCornerRadius(_cornerRadiusCollapsed, duration);
    }

    #endregion

    #region Media Controls

    private bool _isPlaying = true; // Track current playback state
    
    private async void PlayPauseButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        
        if ((DateTime.Now - _lastMediaActionTime).TotalMilliseconds < 500) return;
        _lastMediaActionTime = DateTime.Now;
        
        // Toggle play/pause state
        _isPlaying = !_isPlaying;
        UpdatePlayPauseIcon();
        
        // Play button click animation
        PlayButtonPressAnimation(PlayPauseButton);
        
        // Try Windows Media Session API first (works with YouTube, Spotify, etc.)
        await _mediaService.PlayPauseAsync();
        // Also send media key as fallback
        SendMediaKey(VK_MEDIA_PLAY_PAUSE);
    }

    private async void NextButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        
        if ((DateTime.Now - _lastMediaActionTime).TotalMilliseconds < 500) return;
        _lastMediaActionTime = DateTime.Now;
        
        // Play skip forward animation
        PlayNextSkipAnimation();
        
        if (_currentMediaInfo?.IsVideoSource == true)
        {
            // Tua nhanh 15 giây bằng API SMTC (hoạt động kể cả khi trình duyệt ở background)
            await _mediaService.SeekRelativeAsync(15);
            
            // Fallback: Nếu API không phản hồi, phím tắt vẫn hoạt động nếu browser đang được focus
            // Không gửi phím nữa để tránh bị nhảy 2 lần nếu API hoạt động
        }
        else
        {
            // For music sources (Spotify, Apple Music, etc.), skip to next track
            await _mediaService.NextTrackAsync();
            SendMediaKey(VK_MEDIA_NEXT_TRACK);
        }
    }

    private async void PrevButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        
        if ((DateTime.Now - _lastMediaActionTime).TotalMilliseconds < 500) return;
        _lastMediaActionTime = DateTime.Now;
        
        // Play skip backward animation
        PlayPrevSkipAnimation();
        
        if (_currentMediaInfo?.IsVideoSource == true)
        {
            // Tua ngược 15 giây bằng API
            await _mediaService.SeekRelativeAsync(-15);
        }
        else
        {
            // For music sources (Spotify, etc.), skip to previous track
            await _mediaService.PreviousTrackAsync();
            SendMediaKey(VK_MEDIA_PREV_TRACK);
        }
    }
    
    private void UpdatePlayPauseIcon()
    {
        var duration = TimeSpan.FromMilliseconds(180);
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
        
        if (_isPlaying)
        {
            // Switching to Pause icon (||)
            // Main icon
            AnimateIconSwitch(PlayIcon, PauseIcon, duration, easing);
            // Inline icon
            AnimateIconSwitch(InlinePlayIcon, InlinePauseIcon, duration, easing);
        }
        else
        {
            // Switching to Play icon (▶)
            // Main icon
            AnimateIconSwitch(PauseIcon, PlayIcon, duration, easing);
            // Inline icon
            AnimateIconSwitch(InlinePauseIcon, InlinePlayIcon, duration, easing);
        }
    }
    
    private void AnimateIconSwitch(Canvas fromIcon, Canvas toIcon, TimeSpan duration, EasingFunctionBase easing)
    {
        // CRITICAL: Cancel any ongoing animations first to prevent race conditions
        // This prevents the old animation's Completed handler from hiding the wrong icon
        var fromTransform = fromIcon.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        fromIcon.RenderTransform = fromTransform;
        fromTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        fromTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        fromIcon.BeginAnimation(OpacityProperty, null);
        
        var toTransform = toIcon.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        toIcon.RenderTransform = toTransform;
        toTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        toTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        toIcon.BeginAnimation(OpacityProperty, null);
        
        // Set initial states IMMEDIATELY (not animated) to prevent flicker
        fromIcon.Visibility = Visibility.Visible;
        fromTransform.ScaleX = 1;
        fromTransform.ScaleY = 1;
        fromIcon.Opacity = 1;
        
        toIcon.Visibility = Visibility.Visible;
        toTransform.ScaleX = 0.3;
        toTransform.ScaleY = 0.3;
        toIcon.Opacity = 0;
        
        // Create animations
        var scaleDown = new DoubleAnimation(1, 0.3, duration) { EasingFunction = easing };
        var fadeOut = new DoubleAnimation(1, 0, duration) { EasingFunction = easing };
        var scaleUp = new DoubleAnimation(0.3, 1, duration) { EasingFunction = easing };
        var fadeIn = new DoubleAnimation(0, 1, duration) { EasingFunction = easing };
        
        // Capture references for closure - this is CRITICAL for correct behavior
        var capturedFromIcon = fromIcon;
        var capturedFromTransform = fromTransform;
        
        fadeOut.Completed += (s, e) =>
        {
            // Only hide the icon that was ACTUALLY animated (captured reference)
            capturedFromIcon.Visibility = Visibility.Collapsed;
            // Reset states
            capturedFromTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            capturedFromTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            capturedFromIcon.BeginAnimation(OpacityProperty, null);
            capturedFromTransform.ScaleX = 1;
            capturedFromTransform.ScaleY = 1;
            capturedFromIcon.Opacity = 1;
        };
        
        // Start all animations
        fromTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDown);
        fromTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDown);
        fromIcon.BeginAnimation(OpacityProperty, fadeOut);
        
        toTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleUp);
        toTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleUp);
        toIcon.BeginAnimation(OpacityProperty, fadeIn);
    }
    
    private void PlayButtonPressAnimation(Border button)
    {
        var scaleDown = new DoubleAnimation(1, 0.9, TimeSpan.FromMilliseconds(80));
        var scaleUp = new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(100)) 
        { 
            BeginTime = TimeSpan.FromMilliseconds(80) 
        };
        
        var transform = button.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        button.RenderTransform = transform;
        
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDown);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDown);
        
        scaleDown.Completed += (s, e) =>
        {
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleUp);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleUp);
        };
    }
    
    private async void InlinePlayPauseButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        
        if ((DateTime.Now - _lastMediaActionTime).TotalMilliseconds < 500) return;
        _lastMediaActionTime = DateTime.Now;
        
        _isPlaying = !_isPlaying;
        UpdatePlayPauseIcon();
        PlayButtonPressAnimation(InlinePlayPauseButton);
        
        await _mediaService.PlayPauseAsync();
        SendMediaKey(VK_MEDIA_PLAY_PAUSE);
    }

    private async void InlineNextButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        
        if ((DateTime.Now - _lastMediaActionTime).TotalMilliseconds < 500) return;
        _lastMediaActionTime = DateTime.Now;
        
        PlayNextSkipAnimation(InlineNextArrow0, InlineNextArrow1, InlineNextArrow2);
        
        if (_currentMediaInfo?.IsVideoSource == true)
        {
            // Skip 15s for video via API
            await _mediaService.SeekRelativeAsync(15);
        }
        else
        {
            await _mediaService.NextTrackAsync();
            SendMediaKey(VK_MEDIA_NEXT_TRACK);
        }
    }

    private async void InlinePrevButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        
        if ((DateTime.Now - _lastMediaActionTime).TotalMilliseconds < 500) return;
        _lastMediaActionTime = DateTime.Now;
        
        PlayPrevSkipAnimation(InlinePrevArrow0, InlinePrevArrow1, InlinePrevArrow2);
        
        if (_currentMediaInfo?.IsVideoSource == true)
        {
            // Skip 15s for video via API
            await _mediaService.SeekRelativeAsync(-15);
        }
        else
        {
            await _mediaService.PreviousTrackAsync();
            SendMediaKey(VK_MEDIA_PREV_TRACK);
        }
    }

    private void PlayNextSkipAnimation()
    {
        PlayNextSkipAnimation(NextArrow0, NextArrow1, NextArrow2);
    }

    private void PlayNextSkipAnimation(Path arrow0, Path arrow1, Path arrow2)
    {
        var duration = TimeSpan.FromMilliseconds(250);
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        
        // Arrow 2 (outer): slide right + fade out
        var arrow2Transform = arrow2.RenderTransform as TranslateTransform ?? new TranslateTransform();
        arrow2.RenderTransform = arrow2Transform;
        
        var slideOut2 = new DoubleAnimation(0, 12, duration) { EasingFunction = easing };
        var fadeOut2 = new DoubleAnimation(1, 0, duration) { EasingFunction = easing };
        
        // Arrow 1 (inner): slide right to take Arrow2's position
        var arrow1Transform = arrow1.RenderTransform as TranslateTransform ?? new TranslateTransform();
        arrow1.RenderTransform = arrow1Transform;
        
        var slideRight1 = new DoubleAnimation(0, 10, duration) { EasingFunction = easing };
        
        // Arrow 0 (hidden): slide in from left + fade in to take Arrow1's position
        var arrow0Transform = arrow0.RenderTransform as TranslateTransform ?? new TranslateTransform();
        arrow0.RenderTransform = arrow0Transform;
        
        var slideIn0 = new DoubleAnimation(0, 10, duration) { EasingFunction = easing };
        var fadeIn0 = new DoubleAnimation(0, 1, duration) { EasingFunction = easing };
        
        // Start all animations
        arrow2Transform.BeginAnimation(TranslateTransform.XProperty, slideOut2);
        arrow2.BeginAnimation(OpacityProperty, fadeOut2);
        
        arrow1Transform.BeginAnimation(TranslateTransform.XProperty, slideRight1);
        
        arrow0Transform.BeginAnimation(TranslateTransform.XProperty, slideIn0);
        arrow0.BeginAnimation(OpacityProperty, fadeIn0);
        
        // Reset after animation
        fadeOut2.Completed += (s, e) =>
        {
            // Clear all animations
            arrow2Transform.BeginAnimation(TranslateTransform.XProperty, null);
            arrow2.BeginAnimation(OpacityProperty, null);
            arrow1Transform.BeginAnimation(TranslateTransform.XProperty, null);
            arrow0Transform.BeginAnimation(TranslateTransform.XProperty, null);
            arrow0.BeginAnimation(OpacityProperty, null);
            
            // Reset all values
            arrow2Transform.X = 0;
            arrow2.Opacity = 1;
            arrow1Transform.X = 0;
            arrow0Transform.X = 0;
            arrow0.Opacity = 0;
        };
    }

    private void PlayPrevSkipAnimation()
    {
        PlayPrevSkipAnimation(PrevArrow0, PrevArrow1, PrevArrow2);
    }

    private void PlayPrevSkipAnimation(Path arrow0, Path arrow1, Path arrow2)
    {
        var duration = TimeSpan.FromMilliseconds(250);
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        
        // Arrow 2 (outer): slide left + fade out
        var arrow2Transform = arrow2.RenderTransform as TranslateTransform ?? new TranslateTransform();
        arrow2.RenderTransform = arrow2Transform;
        
        var slideOut2 = new DoubleAnimation(0, -12, duration) { EasingFunction = easing };
        var fadeOut2 = new DoubleAnimation(1, 0, duration) { EasingFunction = easing };
        
        // Arrow 1 (inner): slide left to take Arrow2's position
        var arrow1Transform = arrow1.RenderTransform as TranslateTransform ?? new TranslateTransform();
        arrow1.RenderTransform = arrow1Transform;
        
        var slideLeft1 = new DoubleAnimation(0, -10, duration) { EasingFunction = easing };
        
        // Arrow 0 (hidden): slide in from right + fade in to take Arrow1's position
        var arrow0Transform = arrow0.RenderTransform as TranslateTransform ?? new TranslateTransform();
        arrow0.RenderTransform = arrow0Transform;
        
        var slideIn0 = new DoubleAnimation(0, -10, duration) { EasingFunction = easing };
        var fadeIn0 = new DoubleAnimation(0, 1, duration) { EasingFunction = easing };
        
        // Start all animations
        arrow2Transform.BeginAnimation(TranslateTransform.XProperty, slideOut2);
        arrow2.BeginAnimation(OpacityProperty, fadeOut2);
        
        arrow1Transform.BeginAnimation(TranslateTransform.XProperty, slideLeft1);
        
        arrow0Transform.BeginAnimation(TranslateTransform.XProperty, slideIn0);
        arrow0.BeginAnimation(OpacityProperty, fadeIn0);
        
        // Reset after animation
        fadeOut2.Completed += (s, e) =>
        {
            // Clear all animations
            arrow2Transform.BeginAnimation(TranslateTransform.XProperty, null);
            arrow2.BeginAnimation(OpacityProperty, null);
            arrow1Transform.BeginAnimation(TranslateTransform.XProperty, null);
            arrow0Transform.BeginAnimation(TranslateTransform.XProperty, null);
            arrow0.BeginAnimation(OpacityProperty, null);
            
            // Reset all values
            arrow2Transform.X = 0;
            arrow2.Opacity = 1;
            arrow1Transform.X = 0;
            arrow0Transform.X = 0;
            arrow0.Opacity = 0;
        };
    }

    private void SendMediaKey(byte key)
    {
        keybd_event(key, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        keybd_event(key, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
    }
    /// <summary>
    /// Initialize marquee timer
    /// </summary>
    private void InitializeMarqueeTimer()
    {
        if (_marqueeTimer == null)
        {
            _marqueeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(30)
            };
            _marqueeTimer.Tick += MarqueeTimer_Tick;
        }
    }
    
    private void MarqueeTimer_Tick(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        
        // Update title scroll
        if (_titleScrollDistance > 0 && now > _titlePauseUntil)
        {
            if (_titleScrollForward)
            {
                _titleScrollOffset -= 1.0; // Scroll speed
                if (_titleScrollOffset <= -_titleScrollDistance)
                {
                    _titleScrollOffset = -_titleScrollDistance;
                    _titleScrollForward = false;
                    _titlePauseUntil = now.AddSeconds(1.5); // Pause at end
                }
            }
            else
            {
                _titleScrollOffset += 1.5; // Faster return
                if (_titleScrollOffset >= 0)
                {
                    _titleScrollOffset = 0;
                    _titleScrollForward = true;
                    _titlePauseUntil = now.AddSeconds(2); // Pause at start
                }
            }
            
            if (TrackTitle.RenderTransform is TranslateTransform titleTransform)
            {
                titleTransform.X = _titleScrollOffset;
            }
        }
        
        // Update artist scroll
        if (_artistScrollDistance > 0 && now > _artistPauseUntil)
        {
            if (_artistScrollForward)
            {
                _artistScrollOffset -= 1.0;
                if (_artistScrollOffset <= -_artistScrollDistance)
                {
                    _artistScrollOffset = -_artistScrollDistance;
                    _artistScrollForward = false;
                    _artistPauseUntil = now.AddSeconds(1.5);
                }
            }
            else
            {
                _artistScrollOffset += 1.5;
                if (_artistScrollOffset >= 0)
                {
                    _artistScrollOffset = 0;
                    _artistScrollForward = true;
                    _artistPauseUntil = now.AddSeconds(2);
                }
            }
            
            if (TrackArtist.RenderTransform is TranslateTransform artistTransform)
            {
                artistTransform.X = _artistScrollOffset;
            }
        }
    }
    
    /// <summary>
    /// Update title text with marquee if too long
    /// </summary>
    private void UpdateTitleText(string newText)
    {
        if (newText == _lastTitleText) return;
        _lastTitleText = newText;
        
        TrackTitle.Text = newText;
        
        // Reset scroll
        _titleScrollOffset = 0;
        _titleScrollForward = true;
        _titlePauseUntil = DateTime.Now.AddSeconds(2);
        
        if (TrackTitle.RenderTransform is TranslateTransform transform)
        {
            transform.X = 0;
        }
        
        // Calculate if marquee needed
        TrackTitle.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double textWidth = TrackTitle.DesiredSize.Width;
        double containerWidth = TitleScrollContainer.Width > 0 ? TitleScrollContainer.Width : 250;
        
        if (textWidth > containerWidth)
        {
            _titleScrollDistance = textWidth - containerWidth + 15;
            InitializeMarqueeTimer();
            _marqueeTimer?.Start();
        }
        else
        {
            _titleScrollDistance = 0;
        }
    }
    
    /// <summary>
    /// Update artist text with marquee if too long
    /// </summary>
    private void UpdateArtistText(string newText)
    {
        if (newText == _lastArtistText) return;
        _lastArtistText = newText;
        
        TrackArtist.Text = newText;
        
        // Reset scroll
        _artistScrollOffset = 0;
        _artistScrollForward = true;
        _artistPauseUntil = DateTime.Now.AddSeconds(2.5);
        
        if (TrackArtist.RenderTransform is TranslateTransform transform)
        {
            transform.X = 0;
        }
        
        // Calculate if marquee needed
        TrackArtist.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double textWidth = TrackArtist.DesiredSize.Width;
        double containerWidth = ArtistScrollContainer.Width > 0 ? ArtistScrollContainer.Width : 250;
        
        if (textWidth > containerWidth)
        {
            _artistScrollDistance = textWidth - containerWidth + 15;
            InitializeMarqueeTimer();
            _marqueeTimer?.Start();
        }
        else
        {
            _artistScrollDistance = 0;
        }
    }

    private void OnMediaChanged(object? sender, MediaInfo info)
    {
        // Store current media info for button logic
        _currentMediaInfo = info;
        
        Dispatcher.Invoke(() =>
        {
            // Update Platform Icons
            SpotifyIcon.Visibility = Visibility.Collapsed;
            YouTubeIcon.Visibility = Visibility.Collapsed;
            SoundCloudIcon.Visibility = Visibility.Collapsed;
            FacebookIcon.Visibility = Visibility.Collapsed;
            TikTokIcon.Visibility = Visibility.Collapsed;
            InstagramIcon.Visibility = Visibility.Collapsed;
            TwitterIcon.Visibility = Visibility.Collapsed;
            BrowserIcon.Visibility = Visibility.Collapsed;

            switch (info.MediaSource)
            {
                case "Spotify": SpotifyIcon.Visibility = Visibility.Visible; break;
                case "YouTube": YouTubeIcon.Visibility = Visibility.Visible; break;
                case "SoundCloud": SoundCloudIcon.Visibility = Visibility.Visible; break;
                case "Facebook": FacebookIcon.Visibility = Visibility.Visible; break;
                case "TikTok": TikTokIcon.Visibility = Visibility.Visible; break;
                case "Instagram": InstagramIcon.Visibility = Visibility.Visible; break;
                case "Twitter": case "X": TwitterIcon.Visibility = Visibility.Visible; break;
                default: BrowserIcon.Visibility = Visibility.Visible; break;
            }

            // Update thumbnail
            if (info.HasThumbnail && info.Thumbnail != null)
            {
                ThumbnailImage.Source = info.Thumbnail;
                ThumbnailImage.Visibility = Visibility.Visible;
                ThumbnailFallback.Visibility = Visibility.Collapsed;
                
                // Update blurred background color (Live glow effect)
                UpdateMediaBackground(info);
            }
            else
            {
                ThumbnailImage.Visibility = Visibility.Collapsed;
                ThumbnailFallback.Visibility = Visibility.Visible;
                
                // Hide background if no media image
                HideMediaBackground();
                
                // Set appropriate fallback icon based on source
                ThumbnailFallback.Text = info.MediaSource switch
                {
                    "Spotify" => "🎵",
                    "YouTube" => "▶",
                    "SoundCloud" => "☁",
                    "TikTok" => "♪",
                    "Facebook" => "📺",
                    "Instagram" => "📷",
                    "Twitter" => "🐦",
                    "Browser" => "🌐",
                    _ => "🎵"
                };
            }

            // Sync Play/Pause state (only if not recently clicked to prevent UI fighting)
            if ((DateTime.Now - _lastMediaActionTime).TotalMilliseconds > 500 && _isPlaying != info.IsPlaying)
            {
                _isPlaying = info.IsPlaying;
                UpdatePlayPauseIcon();
            }

            // Update text based on media source with marquee support
            string titleText;
            string artistText;
            
            if (info.IsAnyMediaPlaying && !string.IsNullOrEmpty(info.CurrentTrack))
            {
                MediaAppName.Text = info.MediaSource;
                titleText = string.IsNullOrEmpty(info.CurrentTrack) ? "Playing..." : info.CurrentTrack;
                // Nếu là Browser và không có artist cụ thể, hiển thị "Playing in browser"
                if (info.MediaSource == "Browser" && string.IsNullOrEmpty(info.CurrentArtist))
                {
                    artistText = "Playing in browser";
                }
                else
                {
                    artistText = string.IsNullOrEmpty(info.CurrentArtist) ? info.MediaSource : info.CurrentArtist;
                }
            }
            else if (info.IsSpotifyPlaying)
            {
                MediaAppName.Text = "Spotify";
                titleText = info.CurrentTrack;
                artistText = info.CurrentArtist;
            }
            else if (info.IsYouTubeRunning)
            {
                MediaAppName.Text = "YouTube";
                titleText = info.YouTubeTitle;
                artistText = "Playing in browser";
            }
            else if (info.IsSoundCloudRunning)
            {
                MediaAppName.Text = "SoundCloud";
                titleText = info.CurrentTrack;
                artistText = "Playing";
            }
            else if (info.IsTikTokRunning)
            {
                MediaAppName.Text = "TikTok";
                titleText = info.CurrentTrack;
                artistText = "Playing";
            }
            else if (info.IsFacebookRunning)
            {
                MediaAppName.Text = "Facebook";
                titleText = info.CurrentTrack;
                artistText = "Video";
            }
            else if (info.MediaSource == "Browser")
            {
                // Browser media mà không có thông tin cụ thể
                MediaAppName.Text = "Browser";
                titleText = !string.IsNullOrEmpty(info.CurrentTrack) ? info.CurrentTrack : "Playing...";
                artistText = !string.IsNullOrEmpty(info.CurrentArtist) ? info.CurrentArtist : "Playing in browser";
            }
            else
            {
                MediaAppName.Text = "Now Playing";
                titleText = "No media playing";
                artistText = "Open Spotify or YouTube";
            }
            
            // Update with marquee animation
            UpdateTitleText(titleText);
            UpdateArtistText(artistText);
            
            // Update progress bar tracking info and start realtime timer
            UpdateProgressTracking(info);

            // NEW: Switch to Music Compact Pill if not expanded
            UpdateMusicCompactMode(info);
        });
    }

    private void UpdateMusicCompactMode(MediaInfo info)
    {
        // TRACKING: The state whether we SHOULD be in music compact mode (is playing?)
        // We track this regardless of whether we are currently expanded or not.
        bool shouldBeCompact = info != null && info.IsPlaying;
        
        // CRITICAL: Always update the target collapsed width so the notch knows where to return to.
        _collapsedWidth = shouldBeCompact ? 180 : _settings.Width;
        
        if (shouldBeCompact == _isMusicCompactMode) 
        {
            if (shouldBeCompact) CompactThumbnail.Source = info.Thumbnail;
            return;
        }

        _isMusicCompactMode = shouldBeCompact;

        if (!_isExpanded)
        {
            // Animate Notch width to new compact size
            var widthAnim = new DoubleAnimation(_collapsedWidth, TimeSpan.FromMilliseconds(450))
            {
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 }
            };
            NotchBorder.BeginAnimation(WidthProperty, widthAnim);
            
            if (_isMusicCompactMode)
            {
                // Transition from Camera -> Music Pill
                CompactThumbnail.Source = info.Thumbnail;
                FadeSwitch(CollapsedContent, MusicCompactContent);
                StartVisualizerAnimation();
            }
            else
            {
                // Transition back to Camera
                FadeSwitch(MusicCompactContent, CollapsedContent);
                StopVisualizerAnimation();
            }
        }
        else
        {
            // If expanded, just prepare the content for when it collapses
            if (_isMusicCompactMode)
            {
                CompactThumbnail.Source = info.Thumbnail;
                StartVisualizerAnimation();
                // Ensure visibility is ready for collapse
                MusicCompactContent.Opacity = 0; 
                CollapsedContent.Opacity = 0;
            }
            else
            {
                StopVisualizerAnimation();
            }
        }
    }

    private void FadeSwitch(FrameworkElement from, FrameworkElement to)
    {
        var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(100));
        fadeOut.Completed += (s, e) => from.Visibility = Visibility.Collapsed;
        from.BeginAnimation(OpacityProperty, fadeOut);
        
        to.Visibility = Visibility.Visible;
        // Immediate fade in synchronized with width expansion
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
        to.BeginAnimation(OpacityProperty, fadeIn);
    }

    private void StartVisualizerAnimation()
    {
        AnimateVizBar(VizBar1, 0.4, 1.3, 0.45);
        AnimateVizBar(VizBar2, 0.3, 1.6, 0.55);
        AnimateVizBar(VizBar3, 0.5, 1.2, 0.35);
        AnimateVizBar(VizBar4, 0.2, 1.5, 0.65);
    }

    private void AnimateVizBar(ScaleTransform bar, double from, double to, double durationSec)
    {
        var anim = new DoubleAnimation(from, to, TimeSpan.FromSeconds(durationSec))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        bar.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void StopVisualizerAnimation()
    {
        VizBar1.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        VizBar2.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        VizBar3.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        VizBar4.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }
    

    
    #endregion
    
    #region Expanded Music Player (Inline Expand)
    
    private bool _isMusicExpanded = false;
    private bool _isMusicAnimating = false;
    private double _musicWidgetSmallWidth = 0;
    
    private void ThumbnailBorder_Click(object sender, MouseButtonEventArgs e)
    {
        // Delegate to main handler
        MediaWidget_Click(sender, e);
    }
    
    private void MediaWidget_Click(object sender, MouseButtonEventArgs e)
    {
        // Skip if click originated from inline controls (volume bar, buttons, etc.)
        if (_isMusicExpanded)
        {
            // Check if click source is within InlineControls
            var source = e.OriginalSource as DependencyObject;
            while (source != null)
            {
                if (source == InlineControls)
                {
                    // Click was inside inline controls, don't collapse
                    return;
                }
                if (source == MediaWidget)
                {
                    // Reached MediaWidget without hitting InlineControls, proceed
                    break;
                }
                source = VisualTreeHelper.GetParent(source);
            }
        }
        
        e.Handled = true;
        
        // Allow expand/collapse even when no media is playing
        if (_isMusicAnimating) return;
        
        if (_isMusicExpanded)
        {
            CollapseMusicWidget();
        }
        else
        {
            ExpandMusicWidget();
        }
    }
    
    private void ExpandMusicWidget()
    {
        if (_isMusicAnimating) return;
        _isMusicAnimating = true;
        _isMusicExpanded = true;
        
        // Capture original width for later collapsing
        _musicWidgetSmallWidth = MediaWidgetContainer.ActualWidth;
        
        // Duration optimized for visible acceleration/deceleration
        var expandDuration = TimeSpan.FromMilliseconds(500);
        var contentDelay = TimeSpan.FromMilliseconds(150);
        
        // Velocity: Fast start -> Super Slow end (Exponential) for clear contrast
        var velocityEase = new ExponentialEase 
        { 
            EasingMode = EasingMode.EaseOut,
            Exponent = 7
        };
        
        // Spring for scale content
        var springEase = new ElasticEase 
        { 
            EasingMode = EasingMode.EaseOut,
            Oscillations = 1,
            Springiness = 8
        };
        
        var quickFade = new PowerEase { EasingMode = EasingMode.EaseIn, Power = 2 };
        
        // Step 1: Fade out Calendar & Controls
        var fadeOutCalendar = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = quickFade
        };
        fadeOutCalendar.Completed += (s, e) => CalendarWidget.Visibility = Visibility.Collapsed;
        CalendarWidget.BeginAnimation(OpacityProperty, fadeOutCalendar);
        
        var fadeOutControls = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = quickFade
        };
        fadeOutControls.Completed += (s, e) => MediaControls.Visibility = Visibility.Collapsed;
        MediaControls.BeginAnimation(OpacityProperty, fadeOutControls);
        
        // Step 2: Animate Width & Margin for perfect centering
        
        // Current state
        double startWidth = MediaWidgetContainer.ActualWidth;
        // Target width: Full container width minus some padding
        double finalWidth = ExpandedContent.ActualWidth;
        
        // Lock properties for animation
        MediaWidgetContainer.Width = startWidth;
        MediaWidgetContainer.HorizontalAlignment = HorizontalAlignment.Left;
        Panel.SetZIndex(MediaWidgetContainer, 10);
        Grid.SetColumnSpan(MediaWidgetContainer, 3);
        
        // Animate Width
        var widthAnim = new DoubleAnimation(startWidth, finalWidth, expandDuration)
        {
            EasingFunction = velocityEase
        };
        Timeline.SetDesiredFrameRate(widthAnim, 60);
        
        // Animate Margin from "0,0,8,0" to "0,0,0,0"
        var marginAnim = new ThicknessAnimation(new Thickness(0, 0, 8, 0), new Thickness(0), expandDuration)
        {
            EasingFunction = velocityEase
        };
        
        widthAnim.Completed += (s, e) =>
        {
            // Set to final values BEFORE clearing animation to prevent flicker
            MediaWidgetContainer.Width = double.NaN; // Auto
            MediaWidgetContainer.Margin = new Thickness(0);
            MediaWidgetContainer.HorizontalAlignment = HorizontalAlignment.Stretch;
            
            // Clear animations
            MediaWidgetContainer.BeginAnimation(WidthProperty, null);
            MediaWidgetContainer.BeginAnimation(MarginProperty, null);
            
            // Update MaxWidth here to avoid layout jumps during animation

            
            _isMusicAnimating = false;
        };
        
        MediaWidgetContainer.BeginAnimation(WidthProperty, widthAnim);
        MediaWidgetContainer.BeginAnimation(MarginProperty, marginAnim);
        
        // Step 3: Show inline controls
        InlineControls.Visibility = Visibility.Visible;
        
        // Opacity fade in
        var fadeInInline = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350))
        {
            EasingFunction = velocityEase,
            BeginTime = contentDelay
        };
        InlineControls.BeginAnimation(OpacityProperty, fadeInInline);
        
        // Scale spring animation
        var scaleXAnim = new DoubleAnimation(0.8, 1.0, TimeSpan.FromMilliseconds(450))
        {
            EasingFunction = springEase,
            BeginTime = contentDelay
        };
        var scaleYAnim = new DoubleAnimation(0.8, 1.0, TimeSpan.FromMilliseconds(450))
        {
            EasingFunction = springEase,
            BeginTime = contentDelay
        };
        InlineControlsScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
        InlineControlsScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);
        
        // Sync play/pause icon state
        InlinePauseIcon.Visibility = _isPlaying ? Visibility.Visible : Visibility.Collapsed;
        InlinePlayIcon.Visibility = _isPlaying ? Visibility.Collapsed : Visibility.Visible;
        
        // Sync volume bar with current system volume
        SyncVolumeFromSystem();
    }
    
    private void CollapseMusicWidget()
    {
        if (_isMusicAnimating) return;
        _isMusicAnimating = true;
        _isMusicExpanded = false;
        
        var collapseDuration = TimeSpan.FromMilliseconds(400);
        var contentDelay = TimeSpan.FromMilliseconds(80);
        
        var velocityEase = new ExponentialEase 
        { 
            EasingMode = EasingMode.EaseOut,
            Exponent = 7
        };
        
        var smoothEase = new PowerEase 
        { 
            EasingMode = EasingMode.EaseOut,
            Power = 3
        };
        
        var quickFade = new PowerEase { EasingMode = EasingMode.EaseIn, Power = 2 };
        
        // Step 1: Scale down and fade out inline controls
        var scaleDownX = new DoubleAnimation(1.0, 0.85, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = quickFade
        };
        var scaleDownY = new DoubleAnimation(1.0, 0.85, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = quickFade
        };
        InlineControlsScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDownX);
        InlineControlsScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDownY);
        
        var fadeOutInline = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = quickFade
        };
        fadeOutInline.Completed += (s, e) => 
        {
            InlineControls.Visibility = Visibility.Collapsed;
            // Reset scale for next expand
            InlineControlsScale.ScaleX = 0.8;
            InlineControlsScale.ScaleY = 0.8;
        };
        InlineControls.BeginAnimation(OpacityProperty, fadeOutInline);
        
        // Step 2: Animate Width Shrinking & Margin Restore
        
        double currentWidth = MediaWidgetContainer.ActualWidth;
        // Use captured small width if available, otherwise estimate
        double targetSmallWidth = _musicWidgetSmallWidth > 0 ? _musicWidgetSmallWidth : (ExpandedContent.ActualWidth / 3.0) - 8;
        
        // Lock width for animation
        MediaWidgetContainer.Width = currentWidth;
        MediaWidgetContainer.HorizontalAlignment = HorizontalAlignment.Left;
        
        var widthAnim = new DoubleAnimation(currentWidth, targetSmallWidth, collapseDuration)
        {
            EasingFunction = velocityEase
        };
        Timeline.SetDesiredFrameRate(widthAnim, 60);
        
        // Animate Margin back to "0,0,8,0"
        var marginAnim = new ThicknessAnimation(new Thickness(0), new Thickness(0, 0, 8, 0), collapseDuration)
        {
            EasingFunction = velocityEase
        };
        
        widthAnim.Completed += (s, e) =>
        {
            // Set final values BEFORE clearing animation to prevent flicker
            MediaWidgetContainer.Width = double.NaN;
            MediaWidgetContainer.Margin = new Thickness(0, 0, 8, 0); 
            MediaWidgetContainer.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumnSpan(MediaWidgetContainer, 1);
            Panel.SetZIndex(MediaWidgetContainer, 0);
            
            // Clear animations
            MediaWidgetContainer.BeginAnimation(WidthProperty, null);
            MediaWidgetContainer.BeginAnimation(MarginProperty, null);
            
            // Reset track info max width here to avoid layout jumps during animation

            
            _isMusicAnimating = false;
        };
        
        MediaWidgetContainer.BeginAnimation(WidthProperty, widthAnim);
        MediaWidgetContainer.BeginAnimation(MarginProperty, marginAnim);
        
        // Step 3: Fade in controls
        MediaControls.Visibility = Visibility.Visible;
        var fadeInControls = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = smoothEase,
            BeginTime = contentDelay
        };
        MediaControls.BeginAnimation(OpacityProperty, fadeInControls);
        
        // Step 4: Fade in calendar
        CalendarWidget.Visibility = Visibility.Visible;
        var fadeInCalendar = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = smoothEase,
            BeginTime = TimeSpan.FromMilliseconds(120)
        };
        CalendarWidget.BeginAnimation(OpacityProperty, fadeInCalendar);
    }
    
    #endregion
    
    // Media Progress Tracking is in MainWindow.Progress.cs
    
    private void AnimateButtonScale(ScaleTransform scaleTransform, double targetScale)
    {
        var duration = TimeSpan.FromMilliseconds(150);
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        
        var animX = new DoubleAnimation(scaleTransform.ScaleX, targetScale, duration) { EasingFunction = easing };
        var animY = new DoubleAnimation(scaleTransform.ScaleY, targetScale, duration) { EasingFunction = easing };
        
        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
    }

    #region Battery & Calendar

    private void UpdateTimer_Tick(object? sender, EventArgs e)
    {
        UpdateBatteryInfo();
        UpdateCalendarInfo();
        
        if (_isMusicExpanded) SyncVolumeFromSystem();
        
        // Periodically re-assert topmost status to handle apps like MyDockfinder 
        // that might have climbed over us in the Z-order
        EnsureTopmost();
    }

    private void UpdateBatteryInfo()
    {
        try
        {
            var battery = BatteryService.GetBatteryInfo();

            BatteryPercent.Text = battery.GetPercentageText();

            // Update battery fill width (max 26px)
            double fillWidth = Math.Max(2, battery.Percentage / 100.0 * 26);
            BatteryFill.Width = fillWidth;

            // Update battery color: green when charging, red when low (<20%), white otherwise
            if (battery.IsCharging)
            {
                BatteryFill.Background = new SolidColorBrush(Color.FromRgb(48, 209, 88)); // Green
                BatteryPercent.Foreground = new SolidColorBrush(Colors.White);
            }
            else if (battery.Percentage < 20)
            {
                BatteryFill.Background = new SolidColorBrush(Color.FromRgb(255, 59, 48)); // Red
                BatteryPercent.Foreground = new SolidColorBrush(Color.FromRgb(255, 59, 48)); // Red text
            }
            else
            {
                BatteryFill.Background = new SolidColorBrush(Colors.White); // White
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

        // Update month and day
        MonthText.Text = now.ToString("MMM");
        DayText.Text = now.Day.ToString();

        // Update week days header (show 3 days: yesterday, today, tomorrow)
        var dayNames = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        
        // Clear and rebuild WeekDaysPanel and WeekNumbers
        WeekDaysPanel.Children.Clear();
        WeekNumbers.Children.Clear();
        
        // Show 3 days: yesterday (-1), today (0), tomorrow (+1) - today in the middle
        for (int i = -1; i <= 1; i++)
        {
            var date = now.AddDays(i);
            var dayOfWeek = (int)date.DayOfWeek; // 0 = Sunday, 6 = Saturday
            
            // Create day name TextBlock (same width as number border for alignment)
            var dayNameText = new TextBlock
            {
                Text = dayNames[dayOfWeek],
                Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102)), // #666666
                FontSize = 9,
                Width = 26, // Match border width (22) + margins (4)
                TextAlignment = TextAlignment.Center,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("pack://application:,,,/Fonts/#SF Pro Display")
            };
            WeekDaysPanel.Children.Add(dayNameText);
            
            // Create number border
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
        EventText.Margin = new Thickness(3, 6, 0, 0); // Align with the left edge of 'Sun' (which has 2px margin + centering)
        EventText.FontFamily = new FontFamily("pack://application:,,,/Fonts/#SF Pro Display");
    }

    #endregion

    #region Animations

    private void PlayAppearAnimation()
    {
        NotchBorder.Opacity = 0;
        
        var opacityAnim = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(400),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        NotchBorder.BeginAnimation(OpacityProperty, opacityAnim);
    }

    private void AnimateCornerRadius(double targetRadius, TimeSpan duration)
    {
        double startRadius = NotchBorder.CornerRadius.BottomLeft;
        double delta = targetRadius - startRadius;
        
        if (Math.Abs(delta) < 0.5) return;

        int totalSteps = (int)(duration.TotalMilliseconds / 16);
        int currentStep = 0;
        
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };

        timer.Tick += (s, e) =>
        {
            currentStep++;
            double progress = (double)currentStep / totalSteps;
            // Updated to match Exponential/Quintic feel (Power of 5)
            double easedProgress = 1 - Math.Pow(1 - Math.Min(progress, 1), 5);
            double currentRadius = startRadius + delta * easedProgress;
            
            var cr = new CornerRadius(0, 0, currentRadius, currentRadius);
            NotchBorder.CornerRadius = cr;
            UpdateNotchClip();

            if (currentStep >= totalSteps)
            {
                timer.Stop();
                var finalCr = new CornerRadius(0, 0, targetRadius, targetRadius);
                NotchBorder.CornerRadius = finalCr;
                UpdateNotchClip();
            }
        };

        timer.Start();
    }

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
        // Collapse first if expanded
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
        // Remove WndProc hook
        _hwndSource?.RemoveHook(WndProc);
        
        _mediaService.Dispose();
        _notchManager.Dispose();
        TrayIcon.Dispose();
        _updateTimer.Stop();
        _zOrderTimer.Stop();
        System.Windows.Application.Current.Shutdown();
    }

    #endregion

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
    
    #region Volume Control

    private float _currentVolume = 0.5f; // Store current volume locally

    private void VolumeIcon_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_volumeService.IsAvailable)
        {
            _volumeService.ToggleMute();
            SyncVolumeFromSystem();
        }
    }

    private void VolumeBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _isDraggingVolume = true;
        VolumeBarContainer.CaptureMouse();
        SetVolumeFromMousePosition(e);
    }

    private void VolumeBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDraggingVolume && e.LeftButton == MouseButtonState.Pressed)
        {
            SetVolumeFromMousePosition(e);
        }
    }

    private void VolumeBar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingVolume)
        {
            _isDraggingVolume = false;
            VolumeBarContainer.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void SetVolumeFromMousePosition(MouseEventArgs e)
    {
        // Use fixed width (100px as defined in XAML)
        const double volumeBarWidth = 100.0;
        
        var pos = e.GetPosition(VolumeBarContainer);
        float newVolume = (float)Math.Clamp(pos.X / volumeBarWidth, 0.0, 1.0);
        
        // Update local state
        _currentVolume = newVolume;
        
        // Update UI immediately
        VolumeBarFront.Width = volumeBarWidth * newVolume;
        
        // Update icon
        UpdateVolumeIcon(newVolume, false); // Not muted if sliding
        
        // Set system volume
        if (_volumeService.IsAvailable)
        {
            _volumeService.SetVolume(newVolume);
        }
    }

    private void SyncVolumeFromSystem()
    {
        if (_isDraggingVolume) return;
        
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isDraggingVolume) return;
            
            if (_volumeService.IsAvailable)
            {
                _currentVolume = _volumeService.GetVolume();
                bool isMuted = _volumeService.GetMute();
                
                VolumeBarFront.Width = 100.0 * _currentVolume;
                UpdateVolumeIcon(_currentVolume, isMuted);
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void UpdateVolumeIcon(float volume, bool isMuted)
    {
        if (isMuted || volume <= 0.01f)
        {
            VolumeIcon.Text = "\uE74F"; // Mute
            VolumeIcon.Foreground = new SolidColorBrush(Color.FromRgb(255, 59, 48)); // Red for mute
        }
        else if (volume < 0.33f)
        {
            VolumeIcon.Text = "\uE993"; // Volume 1
            VolumeIcon.Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136));
        }
        else if (volume < 0.66f)
        {
            VolumeIcon.Text = "\uE994"; // Volume 2
            VolumeIcon.Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136));
        }
        else
        {
            VolumeIcon.Text = "\uE995"; // Volume 3
            VolumeIcon.Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136));
        }
    }

    #endregion

    #region Media Background & Color Extraction

    private Color _lastDominantColor = Colors.Transparent;

    private void UpdateMediaBackground(MediaInfo? info)
    {
        if (info == null || info.Thumbnail == null || !info.IsPlaying)
        {
            HideMediaBackground();
            return;
        }

        var dominantColor = GetDominantColor(info.Thumbnail);
        if (dominantColor == _lastDominantColor && MediaBackground.Opacity > 0) return;
        
        _lastDominantColor = dominantColor;
        
        // Define target colors
        var targetColor = Color.FromRgb(dominantColor.R, dominantColor.G, dominantColor.B);
        var vibrantTargetColor = GetVibrantColor(targetColor);
        
        var colorAnim = new ColorAnimation
        {
            To = targetColor,
            Duration = TimeSpan.FromMilliseconds(500), 
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        var uiColorAnim = new ColorAnimation
        {
            To = vibrantTargetColor,
            Duration = TimeSpan.FromMilliseconds(500),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        
        var opacityAnim = new DoubleAnimation
        {
            To = 0.5, // Subtle organic glow
            Duration = TimeSpan.FromMilliseconds(500),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        MediaBackgroundBrush.BeginAnimation(SolidColorBrush.ColorProperty, colorAnim);
        MediaBackground.BeginAnimation(OpacityProperty, opacityAnim);
        
        // Glow 2 (Top-Left)
        MediaBackgroundBrush2.BeginAnimation(SolidColorBrush.ColorProperty, colorAnim);
        MediaBackground2.BeginAnimation(OpacityProperty, opacityAnim);

        // Update Progress Bar and Time labels color
        // Update Progress Bar and Time labels color (Ensure they are not frozen for animation)
        var currentBg = ProgressBar.Background as SolidColorBrush;
        if (currentBg == null || currentBg.IsFrozen)
            ProgressBar.Background = new SolidColorBrush(currentBg?.Color ?? Colors.White);
            
        var currentSt = CurrentTimeText.Foreground as SolidColorBrush;
        if (currentSt == null || currentSt.IsFrozen)
            CurrentTimeText.Foreground = new SolidColorBrush(currentSt?.Color ?? Color.FromRgb(136, 136, 136));
            
        var currentRt = RemainingTimeText.Foreground as SolidColorBrush;
        if (currentRt == null || currentRt.IsFrozen)
            RemainingTimeText.Foreground = new SolidColorBrush(currentRt?.Color ?? Color.FromRgb(136, 136, 136));

        ((SolidColorBrush)ProgressBar.Background).BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);
        ((SolidColorBrush)CurrentTimeText.Foreground).BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);
        ((SolidColorBrush)RemainingTimeText.Foreground).BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);

        // Update Music Visualizer bars color
        if (Resources["MusicVisualizerBrush"] is SolidColorBrush visualizerBrush && !visualizerBrush.IsFrozen)
        {
            visualizerBrush.BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);
        }
    }

    private Color GetVibrantColor(Color c)
    {
        // Convert to HSL or similar logic to boost saturation and brightness
        // Simple version: scale up the most dominant components
        double maxComp = Math.Max(c.R, Math.Max(c.G, c.B));
        if (maxComp == 0) return Color.FromRgb(200, 200, 200); // Default if black

        // Scale factor to make the brightest component around 240-255
        double scale = 240.0 / maxComp;
        
        // Don't scale down if it's already bright
        if (scale < 1.0) scale = 1.0;

        byte r = (byte)Math.Min(255, c.R * scale);
        byte g = (byte)Math.Min(255, c.G * scale);
        byte b = (byte)Math.Min(255, c.B * scale);

        // Additional saturation boost for dark/muddy colors
        double luminance = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
        if (luminance < 0.6)
        {
            // Move away from gray (Saturation boost)
            byte avg = (byte)((r + g + b) / 3);
            r = (byte)Math.Min(255, r + (r - avg) * 0.5);
            g = (byte)Math.Min(255, g + (g - avg) * 0.5);
            b = (byte)Math.Min(255, b + (b - avg) * 0.5);
        }

        return Color.FromRgb(r, g, b);
    }

    private Color EnsureBrightColor(Color c)
    {
        // Keep as fallback or for subtle elements if needed
        double luminance = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
        if (luminance < 0.5)
        {
            double factor = 0.7;
            byte r = (byte)(c.R + (255 - c.R) * factor);
            byte g = (byte)(c.G + (255 - c.G) * factor);
            byte b = (byte)(c.B + (255 - c.B) * factor);
            return Color.FromRgb(r, g, b);
        }
        return c;
    }

    private void HideMediaBackground()
    {
        if (MediaBackground.Opacity == 0) return;
        
        _lastDominantColor = Colors.Transparent;
        var opacityAnim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(400))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        MediaBackground.BeginAnimation(OpacityProperty, opacityAnim);
        MediaBackground2.BeginAnimation(OpacityProperty, opacityAnim);

        // Reset Progress/Time colors
        var defaultColorAnim = new ColorAnimation
        {
            To = Colors.White,
            Duration = TimeSpan.FromMilliseconds(400)
        };
        var defaultTextAnim = new ColorAnimation
        {
            To = Color.FromRgb(136, 136, 136),
            Duration = TimeSpan.FromMilliseconds(400)
        };

        if (ProgressBar.Background is SolidColorBrush sb && !sb.IsFrozen) sb.BeginAnimation(SolidColorBrush.ColorProperty, defaultColorAnim);
        if (CurrentTimeText.Foreground is SolidColorBrush st && !st.IsFrozen) st.BeginAnimation(SolidColorBrush.ColorProperty, defaultTextAnim);
        if (RemainingTimeText.Foreground is SolidColorBrush rt && !rt.IsFrozen) rt.BeginAnimation(SolidColorBrush.ColorProperty, defaultTextAnim);
    }

    private Color GetDominantColor(BitmapSource bitmap)
    {
        try
        {
            // Fast sampling by scaling down to 10x10
            var small = new TransformedBitmap(bitmap, new ScaleTransform(10.0 / bitmap.PixelWidth, 10.0 / bitmap.PixelHeight));
            var pixels = new byte[100 * 4];
            small.CopyPixels(pixels, 40, 0);

            long r = 0, g = 0, b = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                b += pixels[i];
                g += pixels[i + 1];
                r += pixels[i + 2];
            }

            return Color.FromRgb((byte)(r / 100), (byte)(g / 100), (byte)(b / 100));
        }
        catch
        {
            return Color.FromRgb(30, 30, 30);
        }
    }

    #endregion
}
