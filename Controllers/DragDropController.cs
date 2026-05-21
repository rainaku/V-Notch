using System;
using VNotch.Controllers;

namespace VNotch.Controllers;

/// <summary>
/// Manages drag-and-drop orchestration logic: deciding when to auto-expand,
/// navigate to secondary view, and auto-collapse after drag leave.
/// UI actions are delegated back via events.
/// </summary>
public sealed class DragDropController
{
    private readonly FileShelfController _fileShelf;
    private bool _dragAutoExpanded;

    // ─── Public State ───

    public bool IsDragAutoExpanded => _dragAutoExpanded;

    // ─── Events (MainWindow subscribes to drive UI) ───

    /// <summary>Request to expand the notch.</summary>
    public event Action? ExpandRequested;

    /// <summary>Request to switch to secondary (file shelf) view.</summary>
    public event Action? SwitchToSecondaryRequested;

    /// <summary>Request to switch back to primary view.</summary>
    public event Action? SwitchToPrimaryRequested;

    /// <summary>Request to collapse the notch.</summary>
    public event Action? CollapseRequested;

    /// <summary>Fired when files should be enqueued to the shelf.</summary>
    public event Action<string[]>? FilesAccepted;

    /// <summary>Fired when unlock prompt should be shown. Args: files, count.</summary>
    public event Action<string[], int>? UnlockPromptRequested;

    /// <summary>Fired when a drop rejection visual should be shown. Args: message.</summary>
    public event Action<string>? DropRejected;

    // ─── Constructor ───

    public DragDropController(FileShelfController fileShelf)
    {
        _fileShelf = fileShelf;
    }

    // ─── Drag Enter Logic ───

    /// <summary>
    /// Handles drag enter. Returns true if the drag contains files.
    /// </summary>
    public bool HandleDragEnter(bool hasFiles, bool isExpanded, bool isAnimating, bool isSecondaryView)
    {
        if (!hasFiles) return false;

        if (!isExpanded && !isAnimating)
        {
            _dragAutoExpanded = true;
            ExpandRequested?.Invoke();
            return true;
        }

        if (isExpanded && isAnimating)
        {
            // Still animating from a previous expand — will switch to secondary when ready
            return true;
        }

        if (isExpanded && !isSecondaryView && !isAnimating)
        {
            SwitchToSecondaryRequested?.Invoke();
            return true;
        }

        return true;
    }

    /// <summary>
    /// Called when the notch finishes expanding/animating during a drag operation.
    /// Decides whether to switch to secondary view.
    /// </summary>
    public void OnAnimationCompleted(bool isExpanded, bool isSecondaryView)
    {
        if (_dragAutoExpanded && isExpanded && !isSecondaryView)
        {
            SwitchToSecondaryRequested?.Invoke();
        }
    }

    // ─── Drag Leave Logic ───

    /// <summary>
    /// Handles drag leave. Returns true if auto-collapse should be scheduled.
    /// </summary>
    public bool HandleDragLeave()
    {
        return _dragAutoExpanded;
    }

    /// <summary>
    /// Executes the auto-collapse sequence after drag leave timeout.
    /// </summary>
    public void AutoCollapseAfterDrag(bool isExpanded, bool isSecondaryView, bool isAnimating)
    {
        _dragAutoExpanded = false;

        if (!isExpanded) return;

        if (isSecondaryView && !isAnimating)
        {
            SwitchToPrimaryRequested?.Invoke();
            // Caller should wait for animation to complete, then call CollapseAfterViewSwitch
        }
        else if (!isAnimating)
        {
            CollapseRequested?.Invoke();
        }
    }

    /// <summary>
    /// Called after switching back to primary view completes during auto-collapse.
    /// </summary>
    public void CollapseAfterViewSwitch(bool isSecondaryView, bool isAnimating)
    {
        if (!isSecondaryView && !isAnimating)
        {
            CollapseRequested?.Invoke();
        }
    }

    // ─── Drop Logic ───

    /// <summary>
    /// Validates and processes a file drop.
    /// </summary>
    public void HandleDrop(string[]? files)
    {
        _dragAutoExpanded = false;

        if (files == null || files.Length == 0) return;

        var validation = _fileShelf.ValidateDrop(files);

        switch (validation.Result)
        {
            case FileShelfController.DropResult.Accept:
                FilesAccepted?.Invoke(validation.NewFiles);
                break;
            case FileShelfController.DropResult.UnlockPrompt:
                UnlockPromptRequested?.Invoke(validation.NewFiles, validation.FileCount);
                break;
            default:
                if (!string.IsNullOrEmpty(validation.Message))
                    DropRejected?.Invoke(validation.Message);
                break;
        }
    }

    /// <summary>
    /// Resets drag state (e.g., on actual drop or forced cancel).
    /// </summary>
    public void Reset()
    {
        _dragAutoExpanded = false;
    }
}
