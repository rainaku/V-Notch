using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.Controllers;

public sealed class FileShelfController : IDisposable
{
    private const int DefaultFileLimit = 30;
    private const int UnlockedFileLimit = 999;

    private readonly NotchSettings _settings;
    private readonly ISettingsService _settingsService;
    private readonly List<string> _filesList = new();           // ordered (for UI index/iteration)
    private readonly HashSet<string> _filesSet = new(StringComparer.OrdinalIgnoreCase); // O(1) lookup
    private readonly HashSet<string> _selectedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pinnedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(); // UI-thread only
    private readonly Queue<string> _addQueue = new();
    private readonly object _lock = new();
    private readonly Dispatcher _dispatcher;
    private bool _isProcessingQueue = false;

    // Cached snapshots — invalidated on mutation, avoids allocating on every property read.
    private List<string>? _filesSnapshot;
    private int _snapshotVersion;
    private int _lastSnapshotVersion = -1;

    // ─── Public State ─── NOTE: Properties that read _filesList/_pendingFiles are guarded by _lock

    public IReadOnlyList<string> Files
    {
        get
        {
            lock (_lock)
            {
                if (_filesSnapshot == null || _lastSnapshotVersion != _snapshotVersion)
                {
                    _filesSnapshot = _filesList.ToList();
                    _lastSnapshotVersion = _snapshotVersion;
                }
                return _filesSnapshot;
            }
        }
    }
    public IReadOnlyCollection<string> SelectedFiles { get { lock (_lock) return _selectedFiles.ToHashSet(); } }
    public int FileCount { get { lock (_lock) return _filesList.Count; } }
    public int PendingCount { get { lock (_lock) return _pendingFiles.Count; } }
    public int OccupiedSlots { get { lock (_lock) return _filesList.Count + _pendingFiles.Count; } }
    public int MaxFiles => _settings.IsShelfUploadLimitUnlocked ? UnlockedFileLimit : DefaultFileLimit;
    public int RemainingSlots { get { lock (_lock) return Math.Max(0, MaxFiles - (_filesList.Count + _pendingFiles.Count)); } }
    public bool IsFull { get { lock (_lock) return (_filesList.Count + _pendingFiles.Count) >= MaxFiles; } }
    public bool IsLimitUnlocked => _settings.IsShelfUploadLimitUnlocked;

    // ─── Events ───
    public event Action<string>? FileReadyToAdd;
    public event Action? AddQueueDrained;
    public event Action? LayoutRefreshRequested;
    public event Action? CapacityChanged;
    public event Action? PinStateChanged;
    public event Action<string>? FileExternallyRemoved;
    public event Action<string, string>? FileExternallyRenamed;
    public event Action<string, Exception>? FileWatchFailed;
    public event Action? ProcessNextRequested;

    public FileShelfController(NotchSettings settings, ISettingsService settingsService)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _dispatcher = Dispatcher.CurrentDispatcher;
    }
    private void InvalidateSnapshot() => _snapshotVersion++;

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
    public DropValidation ValidateDrop(string[]? rawFiles)
    {
        if (rawFiles == null || rawFiles.Length == 0)
            return new DropValidation(DropResult.NoFiles, Array.Empty<string>(), Loc.Get("shelf.noFiles"));

        lock (_lock)
        {
            var newFiles = rawFiles
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(f => !_filesSet.Contains(f))
                .Where(f => !_pendingFiles.Contains(f))
                .ToArray();

            if (newFiles.Length == 0)
                return new DropValidation(DropResult.AlreadyOnShelf, Array.Empty<string>(), Loc.Get("shelf.alreadyOnShelf"));

            if (_settings.IsShelfUploadLimitUnlocked)
                return new DropValidation(DropResult.Accept, newFiles, string.Empty);

            int occupiedSlots = _filesList.Count + _pendingFiles.Count;
            int totalAfterDrop = occupiedSlots + newFiles.Length;
            if (totalAfterDrop > DefaultFileLimit)
                return new DropValidation(DropResult.UnlockPrompt, newFiles, string.Empty, newFiles.Length);

            int remainingSlots = Math.Max(0, MaxFiles - occupiedSlots);
            if (remainingSlots <= 0)
                return new DropValidation(DropResult.ShelfFull, Array.Empty<string>(),
                    Loc.Get("shelf.full", Math.Min(MaxFiles, occupiedSlots), MaxFiles));

            if (newFiles.Length > remainingSlots)
                return new DropValidation(DropResult.ExceedsLimit, Array.Empty<string>(),
                    Loc.Get("shelf.exceedsLimit", MaxFiles, remainingSlots));

            return new DropValidation(DropResult.Accept, newFiles, string.Empty);
        }
    }

    // ─── File Add (Sequential Queue) ───
    public void EnqueueFiles(string[] filePaths)
    {
        bool shouldProcess = false;

        lock (_lock)
        {
            foreach (var f in filePaths)
            {
                if ((_filesList.Count + _pendingFiles.Count) >= MaxFiles) break;
                if (_filesSet.Contains(f) || _pendingFiles.Contains(f))
                    continue;

                _addQueue.Enqueue(f);
                _pendingFiles.Add(f);
            }

            if (!_isProcessingQueue)
                shouldProcess = true;
        }

        CapacityChanged?.Invoke();

        if (shouldProcess)
            ProcessNext();
    }
    public void ProcessNext()
    {
        string? filePath = null;
        bool queueEmpty = false;

        // Phase 1: dequeue under lock
        lock (_lock)
        {
            if (_addQueue.Count == 0)
            {
                _isProcessingQueue = false;
                queueEmpty = true;
            }
            else
            {
                _isProcessingQueue = true;
                filePath = _addQueue.Dequeue();
                _pendingFiles.Remove(filePath);
            }
        }

        if (queueEmpty)
        {
            AddQueueDrained?.Invoke();
            return;
        }

        // Phase 2: I/O check outside lock (avoids blocking other threads on disk access)
        bool fileExists = File.Exists(filePath!) || Directory.Exists(filePath!);

        // Phase 3: commit under lock
        bool shouldAdd = false;
        if (fileExists)
        {
            lock (_lock)
            {
                if ((_filesList.Count + _pendingFiles.Count) < MaxFiles
                    && !_filesSet.Contains(filePath!))
                {
                    _filesList.Add(filePath!);
                    _filesSet.Add(filePath!);
                    InvalidateSnapshot();
                    shouldAdd = true;
                }
            }
        }

        if (shouldAdd)
        {
            WatchDirectory(filePath!);
            CapacityChanged?.Invoke();
            FileReadyToAdd?.Invoke(filePath!);
        }
        else
        {
            // File gone or duplicate — skip, try next
            ProcessNextRequested?.Invoke();
        }
    }
    public void AddFileDirect(string filePath)
    {
        // Quick duplicate/capacity check before I/O
        lock (_lock)
        {
            if ((_filesList.Count + _pendingFiles.Count) >= MaxFiles
                || _filesSet.Contains(filePath))
                return;
        }

        // I/O outside lock
        if (!File.Exists(filePath) && !Directory.Exists(filePath))
            return;

        // Commit under lock (re-check since state may have changed)
        lock (_lock)
        {
            if ((_filesList.Count + _pendingFiles.Count) >= MaxFiles
                || _filesSet.Contains(filePath))
                return;

            _filesList.Add(filePath);
            _filesSet.Add(filePath);
            InvalidateSnapshot();
        }
        WatchDirectory(filePath);
        CapacityChanged?.Invoke();
        LayoutRefreshRequested?.Invoke();
    }

    // ─── Selection ───

    public bool IsSelected(string path) { lock (_lock) return _selectedFiles.Contains(path); }
    public void Select(string path)
    {
        lock (_lock)
        {
            _selectedFiles.Clear();
            _selectedFiles.Add(path);
        }
    }

    public void SelectAll()
    {
        lock (_lock)
        {
            _selectedFiles.Clear();
            foreach (var f in _filesList)
                _selectedFiles.Add(f);
        }
    }

    public void ClearSelection()
    {
        lock (_lock) _selectedFiles.Clear();
    }

    public void SetSelection(IEnumerable<string> paths)
    {
        lock (_lock)
        {
            _selectedFiles.Clear();
            foreach (var p in paths)
                _selectedFiles.Add(p);
        }
    }

    public void ToggleSelection(string path)
    {
        lock (_lock)
        {
            if (_selectedFiles.Contains(path))
                _selectedFiles.Remove(path);
            else
                _selectedFiles.Add(path);
        }
    }
    public void ApplyRectangleSelection(HashSet<string> intersectedPaths, bool isCtrl, HashSet<string> initialState)
    {
        lock (_lock)
        {
            foreach (var path in _filesList)
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
    }

    // ─── File Removal ───
    public void RemoveFiles(IEnumerable<string> filePaths)
    {
        var toRemove = new HashSet<string>(filePaths, StringComparer.OrdinalIgnoreCase);
        // Pinned files cannot be removed — filter them out.
        lock (_lock) toRemove.ExceptWith(_pinnedFiles);
        if (toRemove.Count == 0) return;

        lock (_lock)
        {
            _filesList.RemoveAll(f => toRemove.Contains(f));
            _filesSet.ExceptWith(toRemove);
            _selectedFiles.ExceptWith(toRemove);
            InvalidateSnapshot();
        }
        foreach (var file in toRemove)
        {
            UnwatchDirectory(file);
            FileIconProvider.Invalidate(file);
        }
        CapacityChanged?.Invoke();
    }
    public void RemoveFile(string filePath)
    {
        // Pinned files cannot be removed.
        lock (_lock) { if (_pinnedFiles.Contains(filePath)) return; }

        lock (_lock)
        {
            _filesList.Remove(filePath);
            _filesSet.Remove(filePath);
            _selectedFiles.Remove(filePath);
            InvalidateSnapshot();
        }
        UnwatchDirectory(filePath);
        FileIconProvider.Invalidate(filePath);
        CapacityChanged?.Invoke();
    }
    public List<string> GetSelectedForDeletion() { lock (_lock) return _selectedFiles.Where(f => !_pinnedFiles.Contains(f)).ToList(); }

    // ─── Drag Out ───
    public string[] GetDragFiles() { lock (_lock) return _selectedFiles.ToArray(); }
    public void HandleDragMoveOut(string[] draggedFiles)
    {
        var toRemove = new HashSet<string>(draggedFiles, StringComparer.OrdinalIgnoreCase);
        // Pinned files cannot be dragged out.
        lock (_lock) toRemove.ExceptWith(_pinnedFiles);
        if (toRemove.Count == 0) return;

        lock (_lock)
        {
            _filesList.RemoveAll(f => toRemove.Contains(f));
            _filesSet.ExceptWith(toRemove);
            _selectedFiles.ExceptWith(toRemove);
            InvalidateSnapshot();
        }
        foreach (var f in toRemove)
            UnwatchDirectory(f);
        CapacityChanged?.Invoke();
        LayoutRefreshRequested?.Invoke();
    }

    // ─── Pin ───

    public bool IsPinned(string path) { lock (_lock) return _pinnedFiles.Contains(path); }

    public void TogglePin(string path)
    {
        lock (_lock)
        {
            if (_pinnedFiles.Contains(path))
                _pinnedFiles.Remove(path);
            else
                _pinnedFiles.Add(path);
        }
        SortPinnedFirst();
        // Force full rebuild by invalidating snapshot (pin icon needs re-render)
        lock (_lock) InvalidateSnapshot();
        PinStateChanged?.Invoke();
        LayoutRefreshRequested?.Invoke();
    }

    public void PinFile(string path)
    {
        lock (_lock) _pinnedFiles.Add(path);
        SortPinnedFirst();
        lock (_lock) InvalidateSnapshot();
        PinStateChanged?.Invoke();
        LayoutRefreshRequested?.Invoke();
    }

    public void UnpinFile(string path)
    {
        lock (_lock) _pinnedFiles.Remove(path);
        SortPinnedFirst();
        lock (_lock) InvalidateSnapshot();
        PinStateChanged?.Invoke();
        LayoutRefreshRequested?.Invoke();
    }

    private void SortPinnedFirst()
    {
        lock (_lock)
        {
            _filesList.Sort((a, b) =>
            {
                bool aPinned = _pinnedFiles.Contains(a);
                bool bPinned = _pinnedFiles.Contains(b);
                if (aPinned == bPinned) return 0;
                return aPinned ? -1 : 1;
            });
            InvalidateSnapshot();
        }
    }

    // ─── Unlock ───
    public void UnlockLimit()
    {
        _settings.IsShelfUploadLimitUnlocked = true;
        _settingsService.Save(_settings);
        CapacityChanged?.Invoke();
    }

    public void UpdateSettings(NotchSettings newSettings)
    {
        // Only sync the fields this controller cares about
        _settings.IsShelfUploadLimitUnlocked = newSettings.IsShelfUploadLimitUnlocked;
        CapacityChanged?.Invoke();
    }

    // ─── Capacity Info (for UI) ───

    public string GetCountDisplayText()
    {
        lock (_lock)
        {
            int currentCount = _filesList.Count + _pendingFiles.Count;
            if (_settings.IsShelfUploadLimitUnlocked)
                return $"{currentCount}";
            int displayCount = Math.Min(DefaultFileLimit, currentCount);
            return $"{displayCount}/{DefaultFileLimit}";
        }
    }

    public bool IsCountWarning { get { lock (_lock) return (_filesList.Count + _pendingFiles.Count) >= MaxFiles && !_settings.IsShelfUploadLimitUnlocked; } }

    public string? GetStatusMessage()
    {
        lock (_lock)
        {
            int occupiedSlots = _filesList.Count + _pendingFiles.Count;
            if (occupiedSlots >= MaxFiles && !_settings.IsShelfUploadLimitUnlocked)
            {
                return Loc.Get("shelf.full", occupiedSlots, DefaultFileLimit);
            }
            return null;
        }
    }

    // ─── File Rename (external) ───

    public void HandleExternalRename(string oldPath, string newPath)
    {
        lock (_lock)
        {
            int idx = _filesList.IndexOf(oldPath);
            if (idx >= 0)
            {
                _filesList[idx] = newPath;
                _filesSet.Remove(oldPath);
                _filesSet.Add(newPath);
                InvalidateSnapshot();
            }

            if (_selectedFiles.Remove(oldPath))
                _selectedFiles.Add(newPath);
        }

        WatchDirectory(newPath);
        UnwatchDirectory(oldPath);
        CapacityChanged?.Invoke();
        LayoutRefreshRequested?.Invoke();
    }

    // ─── File Watching ─── IMPORTANT: WatchDirectory/UnwatchDirectory access _watchers without _lock

    private void WatchDirectory(string filePath)
    {
        System.Diagnostics.Debug.Assert(_dispatcher.CheckAccess(), "WatchDirectory must be called on UI thread");

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
            // Directory may not exist, be inaccessible, or on a non-watchable filesystem (network share)
            RuntimeLog.Error("SHELF-WATCH", ex.ToString());
            FileWatchFailed?.Invoke(dir, ex);
        }
    }

    private void UnwatchDirectory(string filePath)
    {
        System.Diagnostics.Debug.Assert(_dispatcher.CheckAccess(), "UnwatchDirectory must be called on UI thread");

        var dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir)) return;

        bool hasOtherFiles;
        lock (_lock)
        {
            hasOtherFiles = false;
            foreach (var f in _filesList)
            {
                if (string.Equals(Path.GetDirectoryName(f), dir, StringComparison.OrdinalIgnoreCase))
                {
                    hasOtherFiles = true;
                    break;
                }
            }
        }

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
        // FileSystemWatcher fires on a thread pool thread — marshal to UI thread
        _dispatcher.BeginInvoke(() =>
        {
            bool shouldNotify;
            lock (_lock)
            {
                shouldNotify = _filesSet.Contains(e.FullPath) && !_isProcessingQueue;
            }
            if (shouldNotify)
                FileExternallyRemoved?.Invoke(e.FullPath);
        });
    }

    private void OnFileExternallyRenamed(object sender, RenamedEventArgs e)
    {
        // FileSystemWatcher fires on a thread pool thread — marshal to UI thread
        _dispatcher.BeginInvoke(() =>
        {
            bool shouldNotify;
            lock (_lock)
            {
                shouldNotify = _filesSet.Contains(e.OldFullPath);
            }
            if (shouldNotify)
                FileExternallyRenamed?.Invoke(e.OldFullPath, e.FullPath);
        });
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
