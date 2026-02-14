using System.Windows;
using System.Windows.Forms;
using VNotch.Models;

namespace VNotch.Services;

/// <summary>
/// Enhanced NotchManager with Apple-style animations and interactions
/// Handles positioning, state transitions, and dynamic resizing
/// </summary>
public class NotchManager : IDisposable
{
    private readonly Window _window;
    private NotchSettings _settings;
    private readonly NotchStateManager _stateManager;
    private readonly HoverDetectionService _hoverService;

    // Store original dimensions for collapse animation
    private double _originalWidth;
    private double _originalHeight;

    // Screen bounds for safe area calculations
    private Screen? _currentScreen;
    private Rect _safeArea;
    private bool _disposed;

    public NotchStateManager StateManager => _stateManager;
    public HoverDetectionService HoverService => _hoverService;
    public Rect SafeArea => _safeArea;

    public event EventHandler<Rect>? SafeAreaChanged;
    public event EventHandler? PositionUpdated;

    public NotchManager(Window window, NotchSettings settings)
    {
        _window = window;
        _settings = settings;
        _stateManager = new NotchStateManager();
        _hoverService = new HoverDetectionService(settings.HoverZoneMargin);

        // Store original dimensions
        _originalWidth = settings.Width;
        _originalHeight = settings.Height;

        // Setup event handlers only if hover expand is enabled
        if (settings.EnableHoverExpand)
        {
            _hoverService.HoverEnter += OnHoverEnter;
            _hoverService.HoverLeave += OnHoverLeave;
        }

        // Start hover detection
        _hoverService.Start();
    }

    public void UpdateSettings(NotchSettings settings)
    {
        var oldHoverEnabled = _settings.EnableHoverExpand;
        _settings = settings;
        _originalWidth = settings.Width;
        _originalHeight = settings.Height;

        // Update hover service subscription
        if (settings.EnableHoverExpand && !oldHoverEnabled)
        {
            _hoverService.HoverEnter += OnHoverEnter;
            _hoverService.HoverLeave += OnHoverLeave;
        }
        else if (!settings.EnableHoverExpand && oldHoverEnabled)
        {
            _hoverService.HoverEnter -= OnHoverEnter;
            _hoverService.HoverLeave -= OnHoverLeave;
        }

        UpdatePosition();
    }

    public void UpdatePosition()
    {
        _currentScreen = GetTargetScreen();
        var workingArea = _currentScreen.Bounds;

        // Window size should accommodate the largest possible notch size
        double maxWidth = _settings.Width * 2.5; // Max expansion
        double maxHeight = _settings.Height * 3;

        // Position window at top center, large enough to contain expanded notch
        _window.Width = maxWidth;
        _window.Height = maxHeight;
        _window.Left = workingArea.Left + (workingArea.Width - maxWidth) / 2;
        _window.Top = workingArea.Top;

        // Update hover detection bounds based on actual notch position
        double notchLeft = workingArea.Left + (workingArea.Width - _settings.Width) / 2;
        _hoverService.UpdateNotchBounds(notchLeft, workingArea.Top, _settings.Width, _settings.Height);

        // Calculate safe area
        UpdateSafeArea();

        PositionUpdated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Calculate the safe area that apps should avoid
    /// </summary>
    private void UpdateSafeArea()
    {
        if (_currentScreen == null) return;

        var workingArea = _currentScreen.Bounds;
        double notchWidth = _settings.Width;
        double notchHeight = _settings.Height;

        double margin = 4;
        double notchLeft = workingArea.Left + (workingArea.Width - notchWidth) / 2;

        _safeArea = new Rect(
            notchLeft - margin,
            workingArea.Top,
            notchWidth + margin * 2,
            notchHeight + margin
        );

        SafeAreaChanged?.Invoke(this, _safeArea);
    }

    private Screen GetTargetScreen()
    {
        var screens = Screen.AllScreens;

        if (_settings.MonitorIndex >= 0 && _settings.MonitorIndex < screens.Length)
        {
            return screens[_settings.MonitorIndex];
        }

        return Screen.PrimaryScreen ?? screens[0];
    }

    public static int GetMonitorCount()
    {
        return Screen.AllScreens.Length;
    }

    public static string[] GetMonitorNames()
    {
        return Screen.AllScreens
            .Select((s, i) => $"Màn hình {i + 1}" + (s.Primary ? " (Chính)" : ""))
            .ToArray();
    }

    #region Hover Handling

    private void OnHoverEnter(object? sender, EventArgs e)
    {
        // Expand to compact mode on hover
        if (_stateManager.CanExpand())
        {
            _stateManager.ExpandCompact();
        }
    }

    private void OnHoverLeave(object? sender, EventArgs e)
    {
        // Collapse when mouse leaves
        if (_stateManager.CanCollapse())
        {
            _stateManager.Collapse();
        }
    }

    #endregion

    #region Public Controls

    /// <summary>
    /// Manually trigger expansion
    /// </summary>
    public void Expand(NotchExpandMode mode = NotchExpandMode.Compact)
    {
        switch (mode)
        {
            case NotchExpandMode.Compact:
                _stateManager.ExpandCompact();
                break;
            case NotchExpandMode.Medium:
                _stateManager.ExpandMedium();
                break;
            case NotchExpandMode.Large:
                _stateManager.ExpandLarge();
                break;
        }
    }

    /// <summary>
    /// Manually trigger collapse
    /// </summary>
    public void Collapse()
    {
        _stateManager.Collapse();
    }

    /// <summary>
    /// Hide the notch
    /// </summary>
    public void Hide()
    {
        _hoverService.Stop();
        _stateManager.Hide();
    }

    /// <summary>
    /// Show the notch
    /// </summary>
    public void Show()
    {
        _hoverService.Start();
        _stateManager.Show();
    }

    #endregion

    public void Dispose()
    {
        if (!_disposed)
        {
            _hoverService.HoverEnter -= OnHoverEnter;
            _hoverService.HoverLeave -= OnHoverLeave;
            _hoverService.Dispose();
            _disposed = true;
        }
    }
}
