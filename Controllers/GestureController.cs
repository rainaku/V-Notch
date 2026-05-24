using System;
using System.Windows;

namespace VNotch.Controllers;

/// <summary>
/// Recognizes trackpad/mouse gestures on the notch:
/// - Horizontal swipe left/right → next/previous track
/// - Vertical swipe down → open File Shelf
/// - Double-tap → play/pause
/// 
/// Uses mouse delta accumulation with thresholds to distinguish
/// intentional swipes from incidental movement.
/// </summary>
public sealed class GestureController
{
    // ─── Configuration ───
    private const double HorizontalSwipeThreshold = 40.0;   // px needed to trigger horizontal swipe
    private const double VerticalSwipeThreshold = 35.0;     // px needed to trigger vertical swipe
    private const double DirectionLockRatio = 1.6;          // horizontal must be 1.6x vertical to count as horizontal
    private const int DoubleTapMaxMs = 350;                 // max ms between taps for double-tap
    private const int SwipeCooldownMs = 400;                // cooldown between consecutive swipes
    private const double DeadZone = 4.0;                    // ignore micro-movements below this

    // ─── State ───
    private bool _isTracking;
    private Point _startPoint;
    private double _accumulatedX;
    private double _accumulatedY;
    private DateTime _lastSwipeTime = DateTime.MinValue;
    private DateTime _lastTapTime = DateTime.MinValue;
    private bool _gestureTriggered;

    // ─── Events ───
    public event Action? SwipeLeft;       // → Next track
    public event Action? SwipeRight;      // → Previous track
    public event Action? SwipeDown;       // → Open File Shelf
    public event Action? DoubleTap;       // → Play/Pause

    /// <summary>
    /// Call when mouse button is pressed on the notch.
    /// Returns true if the gesture system is now tracking (caller should not handle as click yet).
    /// </summary>
    public void BeginTracking(Point position)
    {
        _isTracking = true;
        _startPoint = position;
        _accumulatedX = 0;
        _accumulatedY = 0;
        _gestureTriggered = false;
    }

    /// <summary>
    /// Call on mouse move while tracking. Returns true if a gesture was triggered.
    /// </summary>
    public bool UpdateTracking(Point currentPosition)
    {
        if (!_isTracking || _gestureTriggered) return false;

        double deltaX = currentPosition.X - _startPoint.X;
        double deltaY = currentPosition.Y - _startPoint.Y;

        _accumulatedX = deltaX;
        _accumulatedY = deltaY;

        double absX = Math.Abs(_accumulatedX);
        double absY = Math.Abs(_accumulatedY);

        // Dead zone — ignore tiny movements
        if (absX < DeadZone && absY < DeadZone) return false;

        // Check cooldown
        if ((DateTime.UtcNow - _lastSwipeTime).TotalMilliseconds < SwipeCooldownMs) return false;

        // Horizontal swipe detection
        if (absX >= HorizontalSwipeThreshold && absX > absY * DirectionLockRatio)
        {
            _gestureTriggered = true;
            _lastSwipeTime = DateTime.UtcNow;

            if (_accumulatedX < 0)
                SwipeLeft?.Invoke();
            else
                SwipeRight?.Invoke();

            return true;
        }

        // Vertical swipe down detection
        if (absY >= VerticalSwipeThreshold && _accumulatedY > 0 && absY > absX * DirectionLockRatio)
        {
            _gestureTriggered = true;
            _lastSwipeTime = DateTime.UtcNow;
            SwipeDown?.Invoke();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Call when mouse button is released. Returns true if this was a tap (no swipe occurred).
    /// </summary>
    public bool EndTracking(Point position)
    {
        if (!_isTracking) return false;
        _isTracking = false;

        if (_gestureTriggered) return false;

        // If movement was minimal, treat as a tap
        double totalMovement = Math.Abs(position.X - _startPoint.X) + Math.Abs(position.Y - _startPoint.Y);
        if (totalMovement < DeadZone * 2)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastTapTime).TotalMilliseconds <= DoubleTapMaxMs)
            {
                _lastTapTime = DateTime.MinValue; // Reset to prevent triple-tap
                DoubleTap?.Invoke();
                return true; // Consumed as double-tap
            }
            _lastTapTime = now;
        }

        return false;
    }

    /// <summary>
    /// Cancel tracking without triggering any gesture.
    /// </summary>
    public void CancelTracking()
    {
        _isTracking = false;
        _gestureTriggered = false;
    }

    /// <summary>
    /// Whether the controller is currently tracking a potential gesture.
    /// </summary>
    public bool IsTracking => _isTracking;

    /// <summary>
    /// Whether a gesture was already triggered in the current tracking session.
    /// </summary>
    public bool GestureTriggered => _gestureTriggered;

    /// <summary>
    /// Current horizontal accumulation (for visual feedback during drag).
    /// </summary>
    public double AccumulatedX => _accumulatedX;

    /// <summary>
    /// Current vertical accumulation (for visual feedback during drag).
    /// </summary>
    public double AccumulatedY => _accumulatedY;
}
