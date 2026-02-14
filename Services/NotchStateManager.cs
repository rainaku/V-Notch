using VNotch.Models;

namespace VNotch.Services;

public class NotchStateManager
{
    private NotchState _currentState = NotchState.Collapsed;
    private NotchState _previousState = NotchState.Collapsed;
    private NotchExpandMode _expandMode = NotchExpandMode.Compact;
    private readonly object _stateLock = new();

    public event EventHandler<NotchStateChangedEventArgs>? StateChanged;

    public NotchState CurrentState
    {
        get => _currentState;
        private set
        {
            if (_currentState != value)
            {
                _previousState = _currentState;
                _currentState = value;
                StateChanged?.Invoke(this, new NotchStateChangedEventArgs(_previousState, _currentState, _expandMode));
            }
        }
    }

    public NotchState PreviousState => _previousState;
    public NotchExpandMode ExpandMode => _expandMode;

    public void Collapse()
    {
        lock (_stateLock)
        {
            _expandMode = NotchExpandMode.Compact;
            CurrentState = NotchState.Collapsed;
        }
    }

    public void ExpandCompact()
    {
        lock (_stateLock)
        {
            _expandMode = NotchExpandMode.Compact;
            CurrentState = NotchState.Expanded;
        }
    }

    public void ExpandMedium()
    {
        lock (_stateLock)
        {
            _expandMode = NotchExpandMode.Medium;
            CurrentState = NotchState.Expanded;
        }
    }

    public void ExpandLarge()
    {
        lock (_stateLock)
        {
            _expandMode = NotchExpandMode.Large;
            CurrentState = NotchState.Expanded;
        }
    }

    public void Hide()
    {
        lock (_stateLock)
        {
            CurrentState = NotchState.Hidden;
        }
    }

    public void Show()
    {
        lock (_stateLock)
        {
            if (_currentState == NotchState.Hidden)
            {
                CurrentState = NotchState.Collapsed;
            }
        }
    }

    public bool CanExpand()
    {
        return _currentState == NotchState.Collapsed;
    }

    public bool CanCollapse()
    {
        return _currentState == NotchState.Expanded;
    }

    public (double widthMultiplier, double heightMultiplier) GetExpandMultiplier()
    {
        return _expandMode switch
        {
            NotchExpandMode.Compact => (1.2, 1.3),
            NotchExpandMode.Medium => (1.8, 2.0),
            NotchExpandMode.Large => (2.5, 3.0),
            _ => (1.0, 1.0)
        };
    }
}

public enum NotchState
{

    Collapsed,

    Expanded,

    Hidden,

    Transitioning
}

public enum NotchExpandMode
{

    Compact,

    Medium,

    Large
}

public class NotchStateChangedEventArgs : EventArgs
{
    public NotchState PreviousState { get; }
    public NotchState NewState { get; }
    public NotchExpandMode ExpandMode { get; }

    public NotchStateChangedEventArgs(NotchState previousState, NotchState newState, NotchExpandMode expandMode)
    {
        PreviousState = previousState;
        NewState = newState;
        ExpandMode = expandMode;
    }
}