using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.Controllers;

public sealed class FileShelfController : IDisposable
{
    private const int DefaultFileLimit = 30;
    private const int UnlockedFileLimit = 999;

    private readonly NotchSettings _settings;
    private readonly ISettingsService _settingsService;
    private readonly List<string> _files = new();
    private readonly HashSet<string> _selectedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new();
    private readonly Queue<string> _addQueue = new();
    private bool _isProcessingQueue = false;

    // ─── Public State ───

    public IReadOnlyList<string> Files => _files;
    public IReadOnlyCollection<string> SelectedFiles => _selectedFiles;
    public int FileCount => _files.Count;
    public int PendingCount => _pendingFiles.Count;
    public int OccupiedSlots => _files.Count + _pendingFiles.Count;
    public int MaxFiles => _settings.IsShelfUploadLimitUnlocked ? UnlockedFileLimit : DefaultFileLimit;
    public int RemainingSlots => Math.Max(0, MaxFiles - OccupiedSlots);
    public bool IsFull => OccupiedSlots >= MaxFiles;
    public bool IsLimitUnlocked => _settings.IsShelfUploadLimitUnlocked;

    // ─── Events ───

    /// <summary>Raised when a file is ready to be added to the UI (one at a time for sequential animation).</summary>
    public event Action<string>? FileReadyToAdd;

    /// <summary>Raised after the sequential add queue is fully drained.</summary>
    public event Action? AddQueueDrained;

    /// <summary>Raised when the shelf layout needs a full refresh.</summary>
    public event Action? LayoutRefreshRequested;

    /// <summary>Raised when capacity indicator should update.</summary>
    public event Action? CapacityChanged;

    /// <summary>Raised when a file is externally deleted or renamed.</summary>
    public event Action<string>? FileExternallyRemoved;

    /// <summary>Raised when a file is externally renamed (old path removed, new path added).</summary>
    public event Action<string, string>? FileExternallyRenamed;

    /// <summary>Request the next queued file to be processed (called by UI after animation delay).</summary>
    public event Action? ProcessNextRequested;

    public FileShelfController(NotchSettings settings, ISettingsService settingsService)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    // ─── Drop Validation ───

    public enum DropResult
    {
        Accept,
        AlreadyOnShelf,
        NoFiles,
        ShelfFull,
        ExceedsLimit,
        UnlockPrompt
    }

    public record DropValidation(DropResult Result, string[] NewFiles, string Message, int FileCount = 0);

    /// <summary>
    /// Validates a set of file paths for drop acceptance.
    /// </summary>
    public DropValidation ValidateDrop(string[]? rawFiles)
    {
        if (rawFiles == null || rawFiles.Length == 0)
            return new DropValidation(DropResult.NoFiles, Array.Empty<string>(), "No files detected.");

        var newFiles = rawFiles
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f => !_files.Contains(f, StringComparer.OrdinalIgnoreCase))
            .Where(f => !_pendingFiles.Contains(f))
            .ToArray();

        if (newFiles.Length == 0)
            return new DropValidation(DropResult.AlreadyOnShelf, Array.Empty<string>(), "These files are already on the shelf.");

        if (_settings.IsShelfUploadLimitUnlocked)
            return new DropValidation(DropResult.Accept, newFiles, string.Empty);

        int totalAfterDrop = OccupiedSlots + newFiles.Length;
        if (totalAfterDrop > DefaultFileLimit)
            return new DropValidation(DropResult.UnlockPrompt, newFiles, string.Empty, newFiles.Length);

        if (RemainingSlots <= 0)
            return new DropValidation(DropResult.ShelfFull, Array.Empty<string>(),
                $"Shelf full ({Math.Min(MaxFiles, OccupiedSlots)}/{MaxFiles}). Remove files before adding more.");

        if (newFiles.Length > RemainingSlots)
            return new DropValidation(DropResult.ExceedsLimit, Array.Empty<string>(),
                $"Shelf limit is {MaxFiles} files. Only {RemainingSlots} slot(s) left.");

        return new DropValidation(DropResult.Accept, newFiles, string.Empty);
    }

    // ─── File Add (Sequential Queue) ───

    /// <summary>
    /// Enqueues files for sequential addition (with animation between each).
    /// </summary>
    public void EnqueueFiles(string[] filePaths)
    {
        foreach (var f in filePaths)
        {
            if (IsFull) break;
            if (_files.Contains(f, StringComparer.OrdinalIgnoreCase) || _pendingFiles.Contains(f))
                continue;

            _addQueue.Enqueue(f);
            _pendingFiles.Add(f);
        }

        CapacityChanged?.Invoke();

        if (!_isProcessingQueue)
            ProcessNext();
    }

    /// <summary>
    /// Processes the next file in the add queue. Called internally and by UI after animation delay.
    /// </summary>
    public void ProcessNext()
    {
        if (_addQueue.Count == 0)
        {
            _isProcessingQueue = false;
            AddQueueDrained?.Invoke();
            return;
        }

        _isProcessingQueue = true;
        var filePath = _addQueue.Dequeue();
        _pendingFiles.Remove(filePath);

        if (!IsFull && !_files.Contains(filePath, StringComparer.OrdinalIgnoreCase))
        {
            _files.Add(filePath);
            WatchDirectory(filePath);
            CapacityChanged?.Invoke();
            FileReadyToAdd?.Invoke(filePath);
        }
        else
        {
            // Skip this file, try next
            ProcessNextRequested?.Invoke();
        }
    }

    /// <summary>
    /// Adds a single file immediately (no queue/animation).
    /// </summary>
    public void AddFileDirect(string filePath)
    {
        if (IsFull || _files.Contains(filePath, StringComparer.OrdinalIgnoreCase)) return;
        _files.Add(filePath);
        WatchDirectory(filePath);
        CapacityChanged?.Invoke();
        LayoutRefreshRequested?.Invoke();
    }

    // ─── Selection ───

    public bool IsSelected(string path) => _selectedFiles.Contains(path);

    public void Select(string path, bool additive)
    {
        if (additive)
        {
            if (_selectedFiles.Contains(path))
                _selectedFiles.Remove(path);
            else
                _selectedFiles.Add(path);
        }
        else
        {
            _selectedFiles.Clear();
            _selectedFiles.Add(path);
        }
    }

    public void SelectAll()
    {
        _selectedFiles.Clear();
        foreach (var f in _files)
            _selectedFiles.Add(f);
    }

    public void ClearSelection()
    {
        _selectedFiles.Clear();
    }

    public void DeselectAll()
    {
        _selectedFiles.Clear();
    }

    public void SetSelection(IEnumerable<string> paths)
    {
        _selectedFiles.Clear();
        foreach (var p in paths)
            _selectedFiles.Add(p);
    }

    public void ToggleSelection(string path)
    {
        if (_selectedFiles.Contains(path))
            _selectedFiles.Remove(path);
        else
            _selectedFiles.Add(path);
    }

    /// <summary>
    /// Performs rectangle/sweep selection logic.
    /// </summary>
    public void ApplyRectangleSelection(IEnumerable<string> intersectedPaths, bool isCtrl, HashSet<string> initialState)
    {
        foreach (var path in _files)
        {
            bool intersects = intersectedPaths.Contains(path);

            if (isCtrl)
            {
                if (intersects)
                {
                    if (initialState.Contains(path)) _selectedFiles.Remove(path);
                    else _selectedFiles.Add(path);
                }
                else
                {
                    if (initialState.Contains(path)) _selectedFiles.Add(path);
                    else _selectedFiles.Remove(path);
                }
            }
            else
            {
                if (intersects) _selectedFiles.Add(path);
                else _selectedFiles.Remove(path);
            }
        }
    }

    // ─── File Removal ───

    /// <summary>
    /// Removes the specified files from the shelf data. Call after animation completes.
    /// </summary>
    public void RemoveFiles(IEnumerable<string> filePaths)
    {
        foreach (var file in filePaths)
        {
            _files.Remove(file);
            _selectedFiles.Remove(file);
            UnwatchDirectory(file);
        }
        CapacityChanged?.Invoke();
    }

    /// <summary>
    /// Removes a single file from the shelf data.
    /// </summary>
    public void RemoveFile(string filePath)
    {
        _files.Remove(filePath);
        _selectedFiles.Remove(filePath);
        UnwatchDirectory(filePath);
        CapacityChanged?.Invoke();
    }

    /// <summary>
    /// Gets the list of currently selected files for deletion.
    /// </summary>
    public List<string> GetSelectedForDeletion() => _selectedFiles.ToList();

    // ─── Drag Out ───

    /// <summary>
    /// Gets the files to include in a drag-out operation.
    /// </summary>
    public string[] GetDragFiles() => _selectedFiles.ToArray();

    /// <summary>
    /// Handles a successful drag-move out of the shelf.
    /// </summary>
    public void HandleDragMoveOut(string[] draggedFiles)
    {
        foreach (var f in draggedFiles)
        {
            _files.Remove(f);
            _selectedFiles.Remove(f);
            UnwatchDirectory(f);
        }
        CapacityChanged?.Invoke();
        LayoutRefreshRequested?.Invoke();
    }

    // ─── Unlock ───

    /// <summary>
    /// Unlocks the shelf upload limit permanently.
    /// </summary>
    public void UnlockLimit()
    {
        _settings.IsShelfUploadLimitUnlocked = true;
        _settingsService.Save(_settings);
        CapacityChanged?.Invoke();
    }

    // ─── Capacity Info (for UI) ───

    public string GetCountDisplayText()
    {
        int currentCount = _files.Count + _pendingFiles.Count;
        if (_settings.IsShelfUploadLimitUnlocked)
            return $"{currentCount}";
        int displayCount = Math.Min(DefaultFileLimit, OccupiedSlots);
        return $"{displayCount}/{DefaultFileLimit}";
    }

    public bool IsCountWarning => IsFull && !_settings.IsShelfUploadLimitUnlocked;

    public string? GetStatusMessage()
    {
        if (IsFull && !_settings.IsShelfUploadLimitUnlocked)
        {
            int currentCount = _files.Count + _pendingFiles.Count;
            return $"Shelf full ({currentCount}/{DefaultFileLimit}). Remove files before adding more.";
        }
        return null;
    }

    // ─── File Rename (external) ───

    public void HandleExternalRename(string oldPath, string newPath)
    {
        int idx = _files.IndexOf(oldPath);
        if (idx >= 0) _files[idx] = newPath;

        if (_selectedFiles.Remove(oldPath))
            _selectedFiles.Add(newPath);

        WatchDirectory(newPath);
        UnwatchDirectory(oldPath);
        CapacityChanged?.Invoke();
        LayoutRefreshRequested?.Invoke();
    }

    // ─── File Watching ───

    private void WatchDirectory(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir) || _watchers.ContainsKey(dir)) return;

        try
        {
            var watcher = new FileSystemWatcher(dir)
            {
                NotifyFilter = NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            watcher.Deleted += OnFileExternallyDeleted;
            watcher.Renamed += OnFileExternallyRenamed;
            _watchers[dir] = watcher;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("SHELF-WATCH", ex.ToString());
        }
    }

    private void UnwatchDirectory(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir)) return;

        bool hasOtherFiles = _files.Any(f =>
            string.Equals(Path.GetDirectoryName(f), dir, StringComparison.OrdinalIgnoreCase));

        if (!hasOtherFiles && _watchers.TryGetValue(dir, out var watcher))
        {
            watcher.EnableRaisingEvents = false;
            watcher.Deleted -= OnFileExternallyDeleted;
            watcher.Renamed -= OnFileExternallyRenamed;
            watcher.Dispose();
            _watchers.Remove(dir);
        }
    }

    private void OnFileExternallyDeleted(object sender, FileSystemEventArgs e)
    {
        if (!_files.Contains(e.FullPath) || _isProcessingQueue) return;
        FileExternallyRemoved?.Invoke(e.FullPath);
    }

    private void OnFileExternallyRenamed(object sender, RenamedEventArgs e)
    {
        if (!_files.Contains(e.OldFullPath)) return;
        FileExternallyRenamed?.Invoke(e.OldFullPath, e.FullPath);
    }

    // ─── Dispose ───

    public void Dispose()
    {
        foreach (var watcher in _watchers.Values)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();
    }
}
