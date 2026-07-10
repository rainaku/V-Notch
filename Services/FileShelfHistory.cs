using System;
using System.Collections.Generic;
using System.Linq;

namespace VNotch.Services;

/// <summary>
/// Manages undo/redo history for File Shelf operations
/// </summary>
public sealed class FileShelfHistory
{
    private readonly Stack<IFileShelfOperation> _undoStack = new();
    private readonly Stack<IFileShelfOperation> _redoStack = new();
    private const int MaxHistorySize = 50;

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;
    public int UndoCount => _undoStack.Count;
    public int RedoCount => _redoStack.Count;

    public event Action? HistoryChanged;

    /// <summary>
    /// Records a new operation and clears redo stack
    /// </summary>
    public void RecordOperation(IFileShelfOperation operation)
    {
        if (operation == null)
            throw new ArgumentNullException(nameof(operation));

        _undoStack.Push(operation);
        _redoStack.Clear();

        // Limit stack size to prevent memory issues
        while (_undoStack.Count > MaxHistorySize)
        {
            var items = _undoStack.ToList();
            items.Reverse();
            _undoStack.Clear();
            foreach (var item in items.Take(MaxHistorySize))
            {
                _undoStack.Push(item);
            }
        }

        HistoryChanged?.Invoke();
    }

    /// <summary>
    /// Undo the last operation
    /// </summary>
    public IFileShelfOperation? Undo()
    {
        if (!CanUndo)
            return null;

        var operation = _undoStack.Pop();
        _redoStack.Push(operation);
        HistoryChanged?.Invoke();
        return operation;
    }

    /// <summary>
    /// Redo the last undone operation
    /// </summary>
    public IFileShelfOperation? Redo()
    {
        if (!CanRedo)
            return null;

        var operation = _redoStack.Pop();
        _undoStack.Push(operation);
        HistoryChanged?.Invoke();
        return operation;
    }

    /// <summary>
    /// Clear all history
    /// </summary>
    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        HistoryChanged?.Invoke();
    }

    /// <summary>
    /// Get description of the next undo operation
    /// </summary>
    public string? GetUndoDescription()
    {
        return CanUndo ? _undoStack.Peek().Description : null;
    }

    /// <summary>
    /// Get description of the next redo operation
    /// </summary>
    public string? GetRedoDescription()
    {
        return CanRedo ? _redoStack.Peek().Description : null;
    }
}

/// <summary>
/// Base interface for File Shelf operations
/// </summary>
public interface IFileShelfOperation
{
    string Description { get; }
    void Undo(Controllers.FileShelfController controller);
    void Redo(Controllers.FileShelfController controller);
}

/// <summary>
/// Operation for adding files to the shelf
/// </summary>
public sealed class AddFilesOperation : IFileShelfOperation
{
    private readonly string[] _files;

    public AddFilesOperation(string[] files)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    public string Description => _files.Length == 1
        ? $"Add file: {System.IO.Path.GetFileName(_files[0])}"
        : $"Add {_files.Length} files";

    public void Undo(Controllers.FileShelfController controller)
    {
        controller.RemoveFiles(_files);
    }

    public void Redo(Controllers.FileShelfController controller)
    {
        foreach (var file in _files)
        {
            controller.AddFileDirect(file);
        }
    }
}

/// <summary>
/// Operation for removing files from the shelf
/// </summary>
public sealed class RemoveFilesOperation : IFileShelfOperation
{
    private readonly string[] _files;
    private readonly Dictionary<string, bool> _pinnedStates;

    public RemoveFilesOperation(string[] files, Dictionary<string, bool> pinnedStates)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _pinnedStates = pinnedStates ?? throw new ArgumentNullException(nameof(pinnedStates));
    }

    public string Description => _files.Length == 1
        ? $"Remove file: {System.IO.Path.GetFileName(_files[0])}"
        : $"Remove {_files.Length} files";

    public void Undo(Controllers.FileShelfController controller)
    {
        foreach (var file in _files)
        {
            controller.AddFileDirect(file);
            if (_pinnedStates.TryGetValue(file, out bool wasPinned) && wasPinned)
            {
                controller.PinFile(file);
            }
        }
    }

    public void Redo(Controllers.FileShelfController controller)
    {
        controller.RemoveFiles(_files);
    }
}

/// <summary>
/// Operation for pinning/unpinning a file
/// </summary>
public sealed class TogglePinOperation : IFileShelfOperation
{
    private readonly string _file;
    private readonly bool _wasPinned;

    public TogglePinOperation(string file, bool wasPinned)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
        _wasPinned = wasPinned;
    }

    public string Description => _wasPinned
        ? $"Unpin: {System.IO.Path.GetFileName(_file)}"
        : $"Pin: {System.IO.Path.GetFileName(_file)}";

    public void Undo(Controllers.FileShelfController controller)
    {
        if (_wasPinned)
            controller.PinFile(_file);
        else
            controller.UnpinFile(_file);
    }

    public void Redo(Controllers.FileShelfController controller)
    {
        controller.TogglePin(_file);
    }
}

/// <summary>
/// Batch operation for multiple pin/unpin actions
/// </summary>
public sealed class BatchPinOperation : IFileShelfOperation
{
    private readonly Dictionary<string, bool> _fileStates;
    private readonly bool _pinning;

    public BatchPinOperation(Dictionary<string, bool> fileStates, bool pinning)
    {
        _fileStates = fileStates ?? throw new ArgumentNullException(nameof(fileStates));
        _pinning = pinning;
    }

    public string Description => _pinning
        ? $"Pin {_fileStates.Count} files"
        : $"Unpin {_fileStates.Count} files";

    public void Undo(Controllers.FileShelfController controller)
    {
        foreach (var kvp in _fileStates)
        {
            if (kvp.Value) // Was pinned
                controller.PinFile(kvp.Key);
            else
                controller.UnpinFile(kvp.Key);
        }
    }

    public void Redo(Controllers.FileShelfController controller)
    {
        foreach (var file in _fileStates.Keys)
        {
            if (_pinning)
                controller.PinFile(file);
            else
                controller.UnpinFile(file);
        }
    }
}
