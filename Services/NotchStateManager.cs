using VNotch.Models;

namespace VNotch.Services;

/// <summary>
/// Centralized state machine for the notch UI. Replaces scattered boolean flags
/// (_isAnimating, _isExpanded, _isMusicExpanded, _isSecondaryView, _isCameraSectionExpanded)
/// with a single authoritative state + valid transition table.
/// </summary>
public class NotchStateManager
{
    private NotchState _currentState = NotchState.Collapsed;
    private NotchState _previousState = NotchState.Collapsed;
    private readonly object _stateLock = new();

    public event EventHandler<NotchStateChangedEventArgs>? StateChanged;

    /// <summary>Current state of the notch.</summary>
    public NotchState CurrentState
    {
        get { lock (_stateLock) return _currentState; }
    }

    /// <summary>State before the last transition.</summary>
    public NotchState PreviousState
    {
        get { lock (_stateLock) return _previousState; }
    }

    // ─── Derived convenience properties (replace old boolean flags) ───

    /// <summary>True when any transition animation is in progress.</summary>
    public bool IsAnimating => CurrentState is NotchState.Expanding
                                            or NotchState.Collapsing
                                            or NotchState.MusicExpanding
                                            or NotchState.MusicCollapsing;

    /// <summary>True when the notch is in any expanded visual state.</summary>
    public bool IsExpanded => CurrentState is NotchState.Expanded
                                           or NotchState.SecondaryView
                                           or NotchState.CameraExpanded;

    /// <summary>True when the music widget is fully expanded.</summary>
    public bool IsMusicExpanded => CurrentState is NotchState.MusicExpanded;

    /// <summary>True when showing the secondary (file shelf) view.</summary>
    public bool IsSecondaryView => CurrentState is NotchState.SecondaryView;

    /// <summary>True when the camera section is expanded within secondary view.</summary>
    public bool IsCameraExpanded => CurrentState is NotchState.CameraExpanded;

    /// <summary>True when the notch is in any "open" state (expanded or music expanded).</summary>
    public bool IsAnyExpanded => IsExpanded || IsMusicExpanded;

    // ─── Transition table ───

    private static readonly Dictionary<NotchState, HashSet<NotchState>> ValidTransitions = new()
    {
        [NotchState.Collapsed] = new() { NotchState.Expanding, NotchState.MusicExpanding },
        [NotchState.Expanding] = new() { NotchState.Expanded, NotchState.Collapsed },
        [NotchState.Expanded] = new() { NotchState.Collapsing, NotchState.SecondaryView, NotchState.MusicExpanding },
        [NotchState.Collapsing] = new() { NotchState.Collapsed, NotchState.Expanding },
        [NotchState.SecondaryView] = new() { NotchState.Collapsing, NotchState.Expanded, NotchState.CameraExpanded },
        [NotchState.CameraExpanded] = new() { NotchState.SecondaryView, NotchState.Collapsing },
        [NotchState.MusicExpanding] = new() { NotchState.MusicExpanded, NotchState.Collapsing },
        [NotchState.MusicExpanded] = new() { NotchState.MusicCollapsing, NotchState.Collapsing },
        [NotchState.MusicCollapsing] = new() { NotchState.Expanded, NotchState.Collapsed },
        [NotchState.Hidden] = new() { NotchState.Collapsed },
    };

    /// <summary>Check if a transition from current state to target is valid.</summary>
    public bool CanTransitionTo(NotchState target)
    {
        lock (_stateLock)
        {
            // Hidden can be entered from any state
            if (target == NotchState.Hidden) return true;
            return ValidTransitions.TryGetValue(_currentState, out var valid) && valid.Contains(target);
        }
    }

    /// <summary>
    /// Attempt a state transition. Returns true if successful, false if invalid.
    /// Does NOT throw — callers can check CanTransitionTo first or just try.
    /// </summary>
    public bool TryTransitionTo(NotchState target)
    {
        lock (_stateLock)
        {
            if (!CanTransitionTo(target)) return false;

            _previousState = _currentState;
            _currentState = target;
        }

        // Fire event outside lock to avoid deadlocks
        StateChanged?.Invoke(this, new NotchStateChangedEventArgs(_previousState, target));
        return true;
    }

    /// <summary>
    /// Force a state transition (for recovery/initialization). Use sparingly.
    /// </summary>
    public void ForceState(NotchState target)
    {
        lock (_stateLock)
        {
            _previousState = _currentState;
            _currentState = target;
        }

        StateChanged?.Invoke(this, new NotchStateChangedEventArgs(_previousState, target));
    }

    /// <summary>
    /// Convenience: collapse from any expanded/animating state.
    /// Returns the appropriate collapsing state, or Collapsed if already there.
    /// </summary>
    public NotchState GetCollapseTarget()
    {
        return CurrentState switch
        {
            NotchState.MusicExpanded or NotchState.MusicExpanding => NotchState.MusicCollapsing,
            NotchState.Expanded or NotchState.SecondaryView or NotchState.CameraExpanded => NotchState.Collapsing,
            _ => NotchState.Collapsed
        };
    }

    // ─── Legacy compatibility methods (used by NotchManager) ───

    public bool CanExpand() => CurrentState == NotchState.Collapsed;
    public bool CanCollapse() => IsExpanded || IsMusicExpanded;

    public void Collapse() => TryTransitionTo(NotchState.Collapsing);
    public void ExpandCompact() => TryTransitionTo(NotchState.Expanding);
    public void ExpandMedium() => TryTransitionTo(NotchState.Expanding);
    public void ExpandLarge() => TryTransitionTo(NotchState.Expanding);

    public void Hide() => ForceState(NotchState.Hidden);
    public void Show()
    {
        if (CurrentState == NotchState.Hidden)
            ForceState(NotchState.Collapsed);
    }
}

// ─── Enums ───

public enum NotchState
{
    /// <summary>Default resting state — small pill shape.</summary>
    Collapsed,

    /// <summary>Animating from collapsed to expanded.</summary>
    Expanding,

    /// <summary>Fully expanded showing media controls, calendar, battery.</summary>
    Expanded,

    /// <summary>Animating from expanded to collapsed.</summary>
    Collapsing,

    /// <summary>Showing the secondary view (file shelf).</summary>
    SecondaryView,

    /// <summary>Camera section expanded within secondary view.</summary>
    CameraExpanded,

    /// <summary>Animating music widget expansion.</summary>
    MusicExpanding,

    /// <summary>Music widget fully expanded (inline controls visible).</summary>
    MusicExpanded,

    /// <summary>Animating music widget collapse.</summary>
    MusicCollapsing,

    /// <summary>Notch hidden (fullscreen app or user toggled off).</summary>
    Hidden
}

// ─── Expand Mode (legacy, used by NotchManager/INotchManager) ───

public enum NotchExpandMode
{
    Compact,
    Medium,
    Large
}

// ─── Event Args ───

public class NotchStateChangedEventArgs : EventArgs
{
    public NotchState PreviousState { get; }
    public NotchState NewState { get; }

    public NotchStateChangedEventArgs(NotchState previousState, NotchState newState)
    {
        PreviousState = previousState;
        NewState = newState;
    }
}
