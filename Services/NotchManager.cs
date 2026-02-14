using System.Windows;
using System.Windows.Forms;
using VNotch.Models;

namespace VNotch.Services;

public class NotchManager : IDisposable
{
    private readonly Window _window;
    private NotchSettings _settings;
    private readonly NotchStateManager _stateManager;
    private readonly HoverDetectionService _hoverService;

    private double _originalWidth;
    private double _originalHeight;

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

        _originalWidth = settings.Width;
        _originalHeight = settings.Height;

        if (settings.EnableHoverExpand)
        {
            _hoverService.HoverEnter += OnHoverEnter;
            _hoverService.HoverLeave += OnHoverLeave;
        }

        _hoverService.Start();
    }

    public void UpdateSettings(NotchSettings settings)
    {
        var oldHoverEnabled = _settings.EnableHoverExpand;
        _settings = settings;
        _originalWidth = settings.Width;
        _originalHeight = settings.Height;

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

        double maxWidth = _settings.Width * 2.5; 
        double maxHeight = _settings.Height * 3;

        _window.Width = maxWidth;
        _window.Height = maxHeight;
        _window.Left = workingArea.Left + (workingArea.Width - maxWidth) / 2;
        _window.Top = workingArea.Top;

        double notchLeft = workingArea.Left + (workingArea.Width - _settings.Width) / 2;
        _hoverService.UpdateNotchBounds(notchLeft, workingArea.Top, _settings.Width, _settings.Height);

        UpdateSafeArea();

        PositionUpdated?.Invoke(this, EventArgs.Empty);
    }

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

        if (_stateManager.CanExpand())
        {
            _stateManager.ExpandCompact();
        }
    }

    private void OnHoverLeave(object? sender, EventArgs e)
    {

        if (_stateManager.CanCollapse())
        {
            _stateManager.Collapse();
        }
    }

    #endregion

    #region Public Controls

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

    public void Collapse()
    {
        _stateManager.Collapse();
    }

    public void Hide()
    {
        _hoverService.Stop();
        _stateManager.Hide();
    }

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