using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace VNotch;

/// <summary>
/// Drag-to-open behaviour on the collapsed notch:
/// when the user drags files over the notch while collapsed, it auto-expands
/// into the secondary view (shelf) so the files can be dropped. A fallback
/// auto-collapse timer re-collapses the notch if the drag is aborted.
/// Split out of <see cref="MainWindow"/> .Secondary partial for readability.
/// </summary>
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

