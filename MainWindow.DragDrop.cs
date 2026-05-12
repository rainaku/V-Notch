using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using VNotch.Controllers;

namespace VNotch;
public partial class MainWindow
{
    #region Drag-to-Open

    
    private bool _dragAutoExpanded = false;

    private System.Windows.Threading.DispatcherTimer? _dragWaitTimer;
    private System.Windows.Threading.DispatcherTimer? _dragCollapseTimer;

    private void NotchWrapper_DragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;

        
        _dragCollapseTimer?.Stop();

        if (!_isExpanded && !_isAnimating)
        {
            
            _dragAutoExpanded = true;
            ExpandNotch();
            StartDragWaitForShelf();
        }
        else if (_isExpanded && _isAnimating)
        {
            
            StartDragWaitForShelf();
        }
        else if (_isExpanded && !_isSecondaryView && !_isAnimating)
        {
            
            SwitchToSecondaryView();
        }
        
    }

    private void NotchWrapper_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
            e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void NotchWrapper_DragLeave(object sender, DragEventArgs e)
    {
        if (!_dragAutoExpanded) return;

        
        
        _dragCollapseTimer?.Stop();
        _dragCollapseTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _dragCollapseTimer.Tick += (s, args) =>
        {
            _dragCollapseTimer?.Stop();
            AutoCollapseAfterDrag();
        };
        _dragCollapseTimer.Start();
    }

    private void NotchWrapper_DragDrop(object sender, DragEventArgs e)
    {
        _dragWaitTimer?.Stop();
        _dragCollapseTimer?.Stop();
        _dragAutoExpanded = false;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;

        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        var validation = _fileShelf.ValidateDrop(files);

        // Ensure the notch is expanded and file shelf is visible
        if (!_isExpanded)
        {
            ExpandNotch();
        }

        // Wait for expansion/animation to finish, then process the drop
        var dropProcessTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        dropProcessTimer.Tick += (s, args) =>
        {
            if (!_isAnimating)
            {
                dropProcessTimer.Stop();

                if (!_isSecondaryView)
                {
                    SwitchToSecondaryView();
                    // Wait for secondary view to be ready, then process
                    var shelfReadyTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(40)
                    };
                    shelfReadyTimer.Tick += (s2, args2) =>
                    {
                        if (!_isAnimating)
                        {
                            shelfReadyTimer.Stop();
                            ProcessDroppedFiles(validation);
                        }
                    };
                    shelfReadyTimer.Start();
                }
                else
                {
                    ProcessDroppedFiles(validation);
                }
            }
        };
        dropProcessTimer.Start();
    }

    private void ProcessDroppedFiles(FileShelfController.DropValidation validation)
    {
        switch (validation.Result)
        {
            case FileShelfController.DropResult.Accept:
                _fileShelf.EnqueueFiles(validation.NewFiles);
                break;
            case FileShelfController.DropResult.UnlockPrompt:
                _pendingUnlockFiles = validation.NewFiles;
                ShowShelfUnlockBanner(validation.FileCount);
                break;
            default:
                if (!string.IsNullOrEmpty(validation.Message))
                    SetShelfDropRejectVisualState(validation.Message);
                break;
        }
    }

    
    private void StartDragWaitForShelf()
    {
        _dragWaitTimer?.Stop();
        _dragWaitTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        _dragWaitTimer.Tick += (s, args) =>
        {
            if (_isExpanded && !_isAnimating)
            {
                _dragWaitTimer?.Stop();
                if (!_isSecondaryView) SwitchToSecondaryView();
            }
        };
        _dragWaitTimer.Start();
    }

    
    private void AutoCollapseAfterDrag()
    {
        _dragAutoExpanded = false;
        _dragWaitTimer?.Stop();

        if (!_isExpanded) return;

        if (_isSecondaryView && !_isAnimating)
        {
            
            SwitchToPrimaryView();

            var collapseWait = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(40)
            };
            collapseWait.Tick += (s2, args2) =>
            {
                if (!_isSecondaryView && !_isAnimating)
                {
                    collapseWait.Stop();
                    CollapseNotch();
                }
                else if (!_isAnimating && !_isSecondaryView)
                {
                    collapseWait.Stop();
                }
            };
            collapseWait.Start();
        }
        else if (!_isAnimating)
        {
            CollapseNotch();
        }
    }

    #endregion
}

