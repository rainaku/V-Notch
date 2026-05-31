using System;
using VNotch.Controllers;

namespace VNotch.Controllers;

public sealed class DragDropController
{
    private readonly FileShelfController _fileShelf;
    private bool _dragAutoExpanded;

    // ─── Public State ───

    public bool IsDragAutoExpanded => _dragAutoExpanded;

    // ─── Events (MainWindow subscribes to drive UI) ───

    public event Action? ExpandRequested;

    public event Action? SwitchToSecondaryRequested;

    public event Action? SwitchToPrimaryRequested;

    public event Action? CollapseRequested;

    public event Action<string[]>? FilesAccepted;

    public event Action<string[], int>? UnlockPromptRequested;

    public event Action<string>? DropRejected;

    // ─── Constructor ───

    public DragDropController(FileShelfController fileShelf)
    {
        _fileShelf = fileShelf;
    }

    // ─── Drag Enter Logic ───

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

    public void OnAnimationCompleted(bool isExpanded, bool isSecondaryView)
    {
        if (_dragAutoExpanded && isExpanded && !isSecondaryView)
        {
            SwitchToSecondaryRequested?.Invoke();
        }
    }

    // ─── Drag Leave Logic ───

    public bool HandleDragLeave()
    {
        return _dragAutoExpanded;
    }

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

    public void CollapseAfterViewSwitch(bool isSecondaryView, bool isAnimating)
    {
        if (!isSecondaryView && !isAnimating)
        {
            CollapseRequested?.Invoke();
        }
    }

    // ─── Drop Logic ───

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

    public void Reset()
    {
        _dragAutoExpanded = false;
    }
}
