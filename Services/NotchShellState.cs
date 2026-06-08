using System;

namespace VNotch.Services;

/// <summary>
/// Tier 2 state owner (per the MVVM refactor design): the single typed home for the
/// cross-cutting window / interop / geometry / visibility state that previously lived
/// loose in the <c>MainWindow</c> shared field block.
///
/// This holds only genuinely shell-level concerns (the native handle, visibility gating,
/// and notch geometry). Feature-specific state belongs in its owning component, not here.
///
/// Behavior note: <see cref="IsEffectivelyNotchVisible"/> reproduces the exact semantics of
/// the former <c>MainWindow.IsEffectivelyNotchVisible</c> expression so z-order / fullscreen /
/// idle paths read an identical value before and after this refactor.
/// </summary>
public sealed class NotchShellState
{
    /// <summary>Native window handle. Assigned once when the window is loaded; read by interop paths.</summary>
    public IntPtr Hwnd { get; set; }

    /// <summary>User-facing visibility toggle (tray "Hide/Show notch").</summary>
    public bool IsNotchVisible { get; set; } = true;

    /// <summary>True while hidden because a foreground app is fullscreen.</summary>
    public bool IsHiddenByFullscreen { get; set; }

    /// <summary>True while slid out of view because the notch has been empty/idle.</summary>
    public bool IsHiddenByIdle { get; set; }

    /// <summary>True while the tray context menu is open (suspends topmost re-assertion).</summary>
    public bool IsTrayMenuOpen { get; set; }

    /// <summary>Until this UTC instant, topmost re-assertion is suspended (e.g. during tooltips).</summary>
    public DateTime SuspendTopmostUntilUtc { get; set; } = DateTime.MinValue;

    // geometry
    public double CollapsedWidth { get; set; }
    public double CollapsedHeight { get; set; }
    public double ExpandedWidth { get; set; } = 480;
    public double ExpandedHeight { get; set; } = 147;
    public double CornerRadiusCollapsed { get; set; }
    public double CornerRadiusExpanded { get; set; } = 24;
    public int FixedX { get; set; }
    public int FixedY { get; set; }

    /// <summary>
    /// The notch is actually shown only when the user has it enabled AND it is not being
    /// hidden by fullscreen or idle auto-hide. Semantics identical to the legacy shell property.
    /// </summary>
    public bool IsEffectivelyNotchVisible =>
        IsNotchVisible && !IsHiddenByFullscreen && !IsHiddenByIdle;
}
