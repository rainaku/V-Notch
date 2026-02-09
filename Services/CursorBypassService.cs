using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace VNotch.Services;

/// <summary>
/// Service to handle cursor bypass around the notch area
/// Ensures cursor doesn't get "stuck" when moving across the notch
/// Similar to macOS behavior where cursor smoothly bypasses the notch
/// </summary>
public class CursorBypassService : IDisposable
{
    private readonly DispatcherTimer _pollTimer;
    private Rect _notchBounds;
    private Point _lastCursorPos;
    private Point _cursorVelocity;
    private bool _disposed;
    private bool _isEnabled = true;

    // Settings
    private readonly double _bypassSpeed = 2.0; // Speed multiplier for bypass
    private readonly double _bypassMargin = 5.0; // Margin around notch for bypass detection

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    public CursorBypassService()
    {
        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(8) // ~120fps for smooth tracking
        };
        _pollTimer.Tick += PollTimer_Tick;
    }

    /// <summary>
    /// Update the notch bounds for bypass calculations
    /// </summary>
    public void UpdateNotchBounds(double left, double top, double width, double height)
    {
        _notchBounds = new Rect(left, top, width, height);
    }

    /// <summary>
    /// Start cursor bypass monitoring
    /// </summary>
    public void Start()
    {
        if (!_disposed)
        {
            // Initialize last position
            if (GetCursorPos(out POINT point))
            {
                _lastCursorPos = new Point(point.X, point.Y);
            }
            _pollTimer.Start();
        }
    }

    /// <summary>
    /// Stop cursor bypass monitoring
    /// </summary>
    public void Stop()
    {
        _pollTimer.Stop();
    }

    private void PollTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isEnabled) return;

        if (GetCursorPos(out POINT point))
        {
            var currentPos = new Point(point.X, point.Y);
            
            // Calculate velocity
            _cursorVelocity = new Point(
                currentPos.X - _lastCursorPos.X,
                currentPos.Y - _lastCursorPos.Y
            );

            // Check if cursor is approaching the notch horizontally
            if (IsCursorNearNotch(currentPos) && IsMovingTowardsNotch(currentPos))
            {
                // Apply bypass logic
                var bypassPos = CalculateBypassPosition(currentPos);
                if (bypassPos != currentPos)
                {
                    SetCursorPos((int)bypassPos.X, (int)bypassPos.Y);
                    currentPos = bypassPos;
                }
            }

            _lastCursorPos = currentPos;
        }
    }

    /// <summary>
    /// Check if cursor is near the notch area
    /// </summary>
    private bool IsCursorNearNotch(Point pos)
    {
        // Create an extended bounds for detection
        var extendedBounds = new Rect(
            _notchBounds.Left - _bypassMargin,
            _notchBounds.Top - _bypassMargin,
            _notchBounds.Width + _bypassMargin * 2,
            _notchBounds.Height + _bypassMargin * 2
        );

        return extendedBounds.Contains(pos);
    }

    /// <summary>
    /// Check if cursor is moving towards the notch
    /// </summary>
    private bool IsMovingTowardsNotch(Point pos)
    {
        var notchCenter = new Point(
            _notchBounds.Left + _notchBounds.Width / 2,
            _notchBounds.Top + _notchBounds.Height / 2
        );

        // If moving towards the center of notch
        double distanceX = pos.X - notchCenter.X;
        return (distanceX > 0 && _cursorVelocity.X < 0) || 
               (distanceX < 0 && _cursorVelocity.X > 0);
    }

    /// <summary>
    /// Calculate bypass position to smoothly route cursor around notch
    /// </summary>
    private Point CalculateBypassPosition(Point currentPos)
    {
        // If cursor is in the notch vertical zone
        if (currentPos.Y >= _notchBounds.Top && 
            currentPos.Y <= _notchBounds.Bottom)
        {
            // If cursor is inside notch horizontal bounds
            if (currentPos.X >= _notchBounds.Left && 
                currentPos.X <= _notchBounds.Right)
            {
                // Push cursor below the notch
                return new Point(currentPos.X, _notchBounds.Bottom + 2);
            }
        }

        return currentPos;
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
