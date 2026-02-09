using System.Windows;

namespace VNotch.Services;

/// <summary>
/// Manages safe areas and provides information about where content should not be placed
/// Similar to macOS's safe area insets API for developers
/// </summary>
public class SafeAreaManager
{
    private Rect _notchBounds;
    private Rect _safeAreaInsets;
    private readonly List<Rect> _exclusionZones = new();

    public event EventHandler<SafeAreaChangedEventArgs>? SafeAreaChanged;

    /// <summary>
    /// The current notch bounds
    /// </summary>
    public Rect NotchBounds => _notchBounds;

    /// <summary>
    /// The safe area insets (top, left, right margins to avoid)
    /// </summary>
    public Rect SafeAreaInsets => _safeAreaInsets;

    /// <summary>
    /// Update the notch position and recalculate safe areas
    /// </summary>
    public void UpdateNotchBounds(double left, double top, double width, double height)
    {
        _notchBounds = new Rect(left, top, width, height);
        RecalculateSafeArea();
    }

    /// <summary>
    /// Add an exclusion zone (area where content should not be placed)
    /// </summary>
    public void AddExclusionZone(Rect zone)
    {
        if (!_exclusionZones.Contains(zone))
        {
            _exclusionZones.Add(zone);
            RecalculateSafeArea();
        }
    }

    /// <summary>
    /// Remove an exclusion zone
    /// </summary>
    public void RemoveExclusionZone(Rect zone)
    {
        if (_exclusionZones.Remove(zone))
        {
            RecalculateSafeArea();
        }
    }

    /// <summary>
    /// Clear all exclusion zones
    /// </summary>
    public void ClearExclusionZones()
    {
        _exclusionZones.Clear();
        RecalculateSafeArea();
    }

    /// <summary>
    /// Check if a point is in a safe area (not blocked by notch or exclusions)
    /// </summary>
    public bool IsPointSafe(Point point)
    {
        // Check if point is in notch area
        if (_notchBounds.Contains(point))
            return false;

        // Check exclusion zones
        foreach (var zone in _exclusionZones)
        {
            if (zone.Contains(point))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Check if a rect would be safe (not overlapping with notch or exclusions)
    /// </summary>
    public bool IsRectSafe(Rect rect)
    {
        // Check if rect overlaps with notch
        if (_notchBounds.IntersectsWith(rect))
            return false;

        // Check exclusion zones
        foreach (var zone in _exclusionZones)
        {
            if (zone.IntersectsWith(rect))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Get the safe area for a given screen bounds
    /// Returns the area that is safe for content placement
    /// </summary>
    public Rect GetSafeRect(Rect screenBounds)
    {
        double top = Math.Max(screenBounds.Top, _notchBounds.Bottom + 4);
        double left = screenBounds.Left;
        double right = screenBounds.Right;
        double bottom = screenBounds.Bottom;

        return new Rect(
            left,
            top,
            right - left,
            bottom - top
        );
    }

    /// <summary>
    /// Get the menu bar safe regions (left and right of notch)
    /// Similar to how macOS splits the menu bar around the notch
    /// </summary>
    public (Rect leftRegion, Rect rightRegion) GetMenuBarRegions(Rect screenBounds)
    {
        double menuBarHeight = _notchBounds.Height + 4; // Notch height + margin

        var leftRegion = new Rect(
            screenBounds.Left,
            screenBounds.Top,
            _notchBounds.Left - screenBounds.Left - 8, // 8px margin from notch
            menuBarHeight
        );

        var rightRegion = new Rect(
            _notchBounds.Right + 8, // 8px margin from notch
            screenBounds.Top,
            screenBounds.Right - _notchBounds.Right - 8,
            menuBarHeight
        );

        return (leftRegion, rightRegion);
    }

    /// <summary>
    /// Get content padding recommendations based on notch position
    /// </summary>
    public Thickness GetRecommendedPadding()
    {
        return new Thickness(
            0, // Left
            _notchBounds.Height + 8, // Top (notch height + margin)
            0, // Right
            0  // Bottom
        );
    }

    private void RecalculateSafeArea()
    {
        // Calculate the combined safe area insets
        double topInset = _notchBounds.Height + 4;
        double leftInset = 0;
        double rightInset = 0;
        double bottomInset = 0;

        // Include exclusion zones in calculation
        foreach (var zone in _exclusionZones)
        {
            topInset = Math.Max(topInset, zone.Bottom + 4);
        }

        _safeAreaInsets = new Rect(leftInset, topInset, rightInset, bottomInset);

        SafeAreaChanged?.Invoke(this, new SafeAreaChangedEventArgs(_notchBounds, _safeAreaInsets));
    }
}

/// <summary>
/// Event args for safe area changes
/// </summary>
public class SafeAreaChangedEventArgs : EventArgs
{
    public Rect NotchBounds { get; }
    public Rect SafeAreaInsets { get; }

    public SafeAreaChangedEventArgs(Rect notchBounds, Rect safeAreaInsets)
    {
        NotchBounds = notchBounds;
        SafeAreaInsets = safeAreaInsets;
    }
}
