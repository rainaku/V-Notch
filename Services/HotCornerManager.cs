using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace VNotch.Services;

/// <summary>
/// Detects hot corner triggers and ensures they work properly around the notch
/// Similar to macOS where hot corners for Mission Control, etc. work despite the notch
/// </summary>
public class HotCornerManager : IDisposable
{
    private readonly DispatcherTimer _pollTimer;
    private Rect _notchBounds;
    private readonly Dictionary<Corner, HotCornerAction> _cornerActions = new();
    private Corner? _activeCorner;
    private DateTime _cornerActivationTime;
    private readonly TimeSpan _activationDelay = TimeSpan.FromMilliseconds(200);
    private bool _disposed;
    private bool _isEnabled = true;

    public event EventHandler<HotCornerEventArgs>? CornerActivated;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    public HotCornerManager()
    {
        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50) // 20 times per second
        };
        _pollTimer.Tick += PollTimer_Tick;

        // Default corner actions (disabled by default)
        _cornerActions[Corner.TopLeft] = HotCornerAction.None;
        _cornerActions[Corner.TopRight] = HotCornerAction.None;
        _cornerActions[Corner.BottomLeft] = HotCornerAction.None;
        _cornerActions[Corner.BottomRight] = HotCornerAction.None;
    }

    public void UpdateNotchBounds(double left, double top, double width, double height)
    {
        _notchBounds = new Rect(left, top, width, height);
    }

    /// <summary>
    /// Set action for a specific corner
    /// </summary>
    public void SetCornerAction(Corner corner, HotCornerAction action)
    {
        _cornerActions[corner] = action;
    }

    /// <summary>
    /// Get action for a specific corner
    /// </summary>
    public HotCornerAction GetCornerAction(Corner corner)
    {
        return _cornerActions.TryGetValue(corner, out var action) ? action : HotCornerAction.None;
    }

    public void Start()
    {
        if (!_disposed)
        {
            _pollTimer.Start();
        }
    }

    public void Stop()
    {
        _pollTimer.Stop();
    }

    private void PollTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isEnabled) return;

        if (!GetCursorPos(out POINT point)) return;

        var mousePos = new Point(point.X, point.Y);
        var currentCorner = DetectCorner(mousePos);

        if (currentCorner.HasValue)
        {
            if (_activeCorner != currentCorner)
            {
                // New corner detected
                _activeCorner = currentCorner;
                _cornerActivationTime = DateTime.Now;
            }
            else if (DateTime.Now - _cornerActivationTime >= _activationDelay)
            {
                // Corner has been active long enough
                var action = GetCornerAction(currentCorner.Value);
                if (action != HotCornerAction.None)
                {
                    TriggerCornerAction(currentCorner.Value, action);
                    _cornerActivationTime = DateTime.Now + TimeSpan.FromSeconds(1); // Cooldown
                }
            }
        }
        else
        {
            _activeCorner = null;
        }
    }

    private Corner? DetectCorner(Point mousePos)
    {
        // Get screen bounds
        var screen = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point((int)mousePos.X, (int)mousePos.Y));
        var bounds = screen.Bounds;

        int cornerSize = 10; // Corner activation zone size

        // Check if mouse is at screen edges
        bool isLeft = mousePos.X <= bounds.Left + cornerSize;
        bool isRight = mousePos.X >= bounds.Right - cornerSize;
        bool isTop = mousePos.Y <= bounds.Top + cornerSize;
        bool isBottom = mousePos.Y >= bounds.Bottom - cornerSize;

        // Determine which corner
        if (isTop && isLeft)
        {
            // Check if this corner overlaps with notch - if so, skip
            if (IsPointNearNotch(mousePos))
                return null;
            return Corner.TopLeft;
        }
        if (isTop && isRight)
        {
            if (IsPointNearNotch(mousePos))
                return null;
            return Corner.TopRight;
        }
        if (isBottom && isLeft) return Corner.BottomLeft;
        if (isBottom && isRight) return Corner.BottomRight;

        return null;
    }

    private bool IsPointNearNotch(Point point)
    {
        // Create extended bounds around notch
        var extendedNotch = new Rect(
            _notchBounds.Left - 20,
            _notchBounds.Top,
            _notchBounds.Width + 40,
            _notchBounds.Height + 20
        );

        return extendedNotch.Contains(point);
    }

    private void TriggerCornerAction(Corner corner, HotCornerAction action)
    {
        CornerActivated?.Invoke(this, new HotCornerEventArgs(corner, action));

        // Execute built-in actions
        switch (action)
        {
            case HotCornerAction.ShowDesktop:
                ShowDesktop();
                break;
            case HotCornerAction.TaskView:
                OpenTaskView();
                break;
            case HotCornerAction.ActionCenter:
                OpenActionCenter();
                break;
            case HotCornerAction.StartMenu:
                OpenStartMenu();
                break;
        }
    }

    #region Windows Actions

    private void ShowDesktop()
    {
        // Win+D
        SendKeys("{LWIN}D");
    }

    private void OpenTaskView()
    {
        // Win+Tab
        SendKeys("{LWIN}{TAB}");
    }

    private void OpenActionCenter()
    {
        // Win+A
        SendKeys("{LWIN}A");
    }

    private void OpenStartMenu()
    {
        // Win key
        SendKeys("{LWIN}");
    }

    private void SendKeys(string keys)
    {
        System.Windows.Forms.SendKeys.SendWait(keys);
    }

    #endregion

    public void Dispose()
    {
        if (!_disposed)
        {
            _pollTimer.Stop();
            _disposed = true;
        }
    }
}

public enum Corner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

public enum HotCornerAction
{
    None,
    ShowDesktop,
    TaskView,
    ActionCenter,
    StartMenu,
    Custom
}

public class HotCornerEventArgs : EventArgs
{
    public Corner Corner { get; }
    public HotCornerAction Action { get; }

    public HotCornerEventArgs(Corner corner, HotCornerAction action)
    {
        Corner = corner;
        Action = action;
    }
}
