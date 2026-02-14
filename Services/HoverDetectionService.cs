using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace VNotch.Services;

/// <summary>
/// Service to detect mouse hover near the notch area
/// Handles the "invisible" hover zone for triggering notch interactions
/// With debounce to prevent flickering
/// </summary>
public class HoverDetectionService : IDisposable
{
    private readonly DispatcherTimer _pollTimer;
    private readonly int _hoverZoneMargin;
    private Rect _notchBounds;
    private Rect _hoverZone;
    private bool _isHovering;
    private bool _disposed;

    // Debounce mechanism
    private DateTime _hoverEnterTime;
    private DateTime _hoverLeaveTime;
    private readonly TimeSpan _enterDelay = TimeSpan.FromMilliseconds(150); // Delay before enter
    private readonly TimeSpan _leaveDelay = TimeSpan.FromMilliseconds(400); // Delay before leave
    private bool _pendingEnter;
    private bool _pendingLeave;

    public event EventHandler? HoverEnter;
    public event EventHandler? HoverLeave;
    public event EventHandler<Point>? MousePositionChanged;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    public bool IsHovering => _isHovering;

    public HoverDetectionService(int hoverZoneMargin = 50)
    {
        _hoverZoneMargin = hoverZoneMargin;
        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33) // ~30fps polling (smoother)
        };
        _pollTimer.Tick += PollTimer_Tick;
    }

    /// <summary>
    /// Update the notch bounds and recalculate hover zone
    /// </summary>
    public void UpdateNotchBounds(double left, double top, double width, double height)
    {
        _notchBounds = new Rect(left, top, width, height);

        // Create extended hover zone around the notch
        _hoverZone = new Rect(
            left - _hoverZoneMargin,
            top, // No margin on top since notch is at screen edge
            width + _hoverZoneMargin * 2,
            height + _hoverZoneMargin
        );
    }

    /// <summary>
    /// Start monitoring mouse position
    /// </summary>
    public void Start()
    {
        if (!_disposed)
        {
            _pollTimer.Start();
        }
    }

    /// <summary>
    /// Stop monitoring mouse position
    /// </summary>
    public void Stop()
    {
        _pollTimer.Stop();
    }

    private void PollTimer_Tick(object? sender, EventArgs e)
    {
        if (!GetCursorPos(out POINT point)) return;

        var mousePoint = new Point(point.X, point.Y);
        MousePositionChanged?.Invoke(this, mousePoint);

        bool isInZone = _hoverZone.Contains(mousePoint);
        var now = DateTime.Now;

        if (isInZone)
        {
            // Mouse is in hover zone
            _pendingLeave = false;

            if (!_isHovering)
            {
                if (!_pendingEnter)
                {
                    // Start enter timer
                    _pendingEnter = true;
                    _hoverEnterTime = now;
                }
                else if (now - _hoverEnterTime >= _enterDelay)
                {
                    // Enter delay passed, trigger hover
                    _isHovering = true;
                    _pendingEnter = false;
                    HoverEnter?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        else
        {
            // Mouse is outside hover zone
            _pendingEnter = false;

            if (_isHovering)
            {
                if (!_pendingLeave)
                {
                    // Start leave timer
                    _pendingLeave = true;
                    _hoverLeaveTime = now;
                }
                else if (now - _hoverLeaveTime >= _leaveDelay)
                {
                    // Leave delay passed, trigger leave
                    _isHovering = false;
                    _pendingLeave = false;
                    HoverLeave?.Invoke(this, EventArgs.Empty);
                }
            }
        }
    }

    /// <summary>
    /// Check if a point is directly over the notch (not just hover zone)
    /// </summary>
    public bool IsPointOverNotch(Point point)
    {
        return _notchBounds.Contains(point);
    }

    /// <summary>
    /// Get the distance from a point to the notch center
    /// </summary>
    public double GetDistanceToNotch(Point point)
    {
        var center = new Point(
            _notchBounds.Left + _notchBounds.Width / 2,
            _notchBounds.Top + _notchBounds.Height / 2
        );

        return Math.Sqrt(
            Math.Pow(point.X - center.X, 2) +
            Math.Pow(point.Y - center.Y, 2)
        );
    }

    /// <summary>
    /// Force reset hover state
    /// </summary>
    public void ResetHoverState()
    {
        _isHovering = false;
        _pendingEnter = false;
        _pendingLeave = false;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _pollTimer.Stop();
            _disposed = true;
        }
    }
}
