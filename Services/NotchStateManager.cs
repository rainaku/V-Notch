using VNotch.Models;

namespace VNotch.Services;

/// <summary>
/// Manages the different states of the notch (collapsed, expanded, etc.)
/// Similar to Apple's Dynamic Island state management
/// </summary>
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

    /// <summary>
    /// Transition to collapsed state (default notch appearance)
    /// </summary>
    public void Collapse()
    {
        lock (_stateLock)
        {
            _expandMode = NotchExpandMode.Compact;
            CurrentState = NotchState.Collapsed;
        }
    }

    /// <summary>
    /// Transition to compact expanded state (minimal expansion for hover)
    /// </summary>
    public void ExpandCompact()
    {
        lock (_stateLock)
        {
            _expandMode = NotchExpandMode.Compact;
            CurrentState = NotchState.Expanded;
        }
    }

    /// <summary>
    /// Transition to medium expanded state (for music player, timer, etc.)
    /// </summary>
    public void ExpandMedium()
    {
        lock (_stateLock)
        {
            _expandMode = NotchExpandMode.Medium;
            CurrentState = NotchState.Expanded;
        }
    }

    /// <summary>
    /// Transition to large expanded state (for detailed content)
    /// </summary>
    public void ExpandLarge()
    {
        lock (_stateLock)
        {
            _expandMode = NotchExpandMode.Large;
            CurrentState = NotchState.Expanded;
        }
    }

    /// <summary>
    /// Transition to hidden state (notch completely invisible)
    /// </summary>
    public void Hide()
    {
        lock (_stateLock)
        {
            CurrentState = NotchState.Hidden;
        }
    }

    /// <summary>
    /// Restore from hidden state
    /// </summary>
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

    /// <summary>
    /// Check if notch can expand
    /// </summary>
    public bool CanExpand()
    {
        return _currentState == NotchState.Collapsed;
    }

    /// <summary>
    /// Check if notch can collapse
    /// </summary>
    public bool CanCollapse()
    {
        return _currentState == NotchState.Expanded;
    }

    /// <summary>
    /// Get the size multiplier for current expand mode
    /// </summary>
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

/// <summary>
/// Notch states
/// </summary>
public enum NotchState
{
    /// <summary>Default collapsed appearance</summary>
    Collapsed,

    /// <summary>Expanded to show content</summary>
    Expanded,

    /// <summary>Completely hidden</summary>
    Hidden,

    /// <summary>Transitioning between states</summary>
    Transitioning
}

/// <summary>
/// Expansion modes (like Dynamic Island)
/// </summary>
public enum NotchExpandMode
{
    /// <summary>Minimal expansion</summary>
    Compact,

    /// <summary>Medium expansion for music, timer</summary>
    Medium,

    /// <summary>Large expansion for detailed content</summary>
    Large
}

/// <summary>
/// Event args for state changes
/// </summary>
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
