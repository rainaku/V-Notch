using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    // ─── Controller (owns all shelf data/logic) ───
    private FileShelfController _fileShelf = null!;

    private bool _isSecondaryView
    {
        get => _notchState.IsSecondaryView;
        set
        {
            if (value && !_notchState.IsSecondaryView)
                _notchState.TryTransitionTo(NotchState.SecondaryView);
            else if (!value && _notchState.IsSecondaryView)
                _notchState.TryTransitionTo(NotchState.Expanded);
        }
    }
    private DateTime _lastViewSwitchUtc = DateTime.MinValue;
    private static readonly TimeSpan ViewSwitchCooldown = TimeSpan.FromMilliseconds(600);

    private static readonly SolidColorBrush _brushShelfItemBg = CreateFrozenBrush(37, 37, 37);
    private static readonly SolidColorBrush _brushShelfItemBorder = CreateFrozenBrush(51, 51, 51);
    private static readonly SolidColorBrush _brushShelfSelectedBg = CreateFrozenBrush(255, 255, 255, 40);
    private static readonly SolidColorBrush _brushShelfSelectedBorder = CreateFrozenBrush(255, 255, 255, 140);

    private Point _selectionStart;
    private bool _isSelecting = false;
    private Point _dragStartPoint;
    private bool _didDragOutFromShelf = false;
    private bool _isSweepSelecting = false;
    private string? _sweepStartFile = null;
    private bool _wasSelectedOnMouseDown = false;
    private HashSet<string> _selectionInitialState = new();

    private bool _isCameraSectionExpanded = false;
    private int _cameraSectionAnimToken = 0;
    private double _cameraSectionCompactWidth = 0;
    private Thickness _cameraSectionCompactMargin = new Thickness(0, 0, 8, 0);

    private string[]? _pendingUnlockFiles = null;

    #region File Shelf Visual State

    private static readonly SolidColorBrush _brushShelfNormalBg = CreateFrozenBrush(26, 26, 26);
    private static readonly SolidColorBrush _brushShelfActiveBg = CreateFrozenBrush(42, 42, 42);
    private static readonly SolidColorBrush _brushShelfErrorBg = CreateFrozenBrush(56, 24, 24);
    private static readonly SolidColorBrush _brushShelfNormalBorder = CreateFrozenBrush(51, 51, 51);
    private static readonly SolidColorBrush _brushShelfActiveBorder = CreateFrozenBrush(153, 255, 255, 255);
    private static readonly SolidColorBrush _brushShelfErrorBorder = CreateFrozenBrush(255, 107, 107);
    private static readonly SolidColorBrush _brushShelfStatusWarning = CreateFrozenBrush(255, 196, 120);
    private static readonly SolidColorBrush _brushShelfStatusError = CreateFrozenBrush(255, 107, 107);
    private static readonly SolidColorBrush _brushShelfUnlockHintBg = CreateFrozenBrush(42, 36, 20);
    private static readonly SolidColorBrush _brushShelfUnlockHintBorder = CreateFrozenBrush(255, 196, 120);

    #endregion

    #region Controller Initialization & Event Wiring

    private void InitializeFileShelfController()
    {
        _fileShelf = new FileShelfController(_settings, _settingsService);

        _fileShelf.FileReadyToAdd += OnShelfFileReadyToAdd;
        _fileShelf.CapacityChanged += OnShelfCapacityChanged;
        _fileShelf.LayoutRefreshRequested += OnShelfLayoutRefreshRequested;
        _fileShelf.FileExternallyRemoved += OnShelfFileExternallyRemoved;
        _fileShelf.FileExternallyRenamed += OnShelfFileExternallyRenamed;

        // Set initial localized text
        UpdateShelfCapacityIndicator();
    }

    private void OnShelfFileReadyToAdd(string filePath)
    {
        RefreshShelfLayout();

        // Animate the last added item
        if (ShelfItemsContainer.Children.Count > 0)
        {
            var lastItem = ShelfItemsContainer.Children[ShelfItemsContainer.Children.Count - 1] as FrameworkElement;
            if (lastItem != null)
            {
                lastItem.Opacity = 0;
                lastItem.RenderTransformOrigin = new Point(0.5, 0.5);
                var st = new ScaleTransform(0.75, 0.75);
                lastItem.RenderTransform = st;

                var fadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(200))) { EasingFunction = _easeExpOut6 };
                var scaleIn = new DoubleAnimation(0.75, 1, new Duration(TimeSpan.FromMilliseconds(280))) { EasingFunction = _easeMenuSpring };
                Timeline.SetDesiredFrameRate(fadeIn, 120);
                Timeline.SetDesiredFrameRate(scaleIn, 120);

                fadeIn.Completed += (s2, e2) =>
                {
                    lastItem.Opacity = 1;
                    lastItem.BeginAnimation(OpacityProperty, null);
                    lastItem.RenderTransform = new ScaleTransform(1, 1);
                };

                lastItem.BeginAnimation(OpacityProperty, fadeIn);
                st.BeginAnimation(ScaleTransform.ScaleXProperty, scaleIn);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, scaleIn);

                AnimateShelfItemProgress(lastItem);
            }
        }

        // Schedule next file processing after animation delay
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        timer.Tick += (s, args) =>
        {
            timer.Stop();
            _fileShelf.ProcessNext();
        };
        timer.Start();
    }

    private void OnShelfCapacityChanged()
    {
        UpdateShelfCapacityIndicator();
    }

    private void OnShelfLayoutRefreshRequested()
    {
        RefreshShelfLayout();
    }

    private void OnShelfFileExternallyRemoved(string filePath)
    {
        Dispatcher.BeginInvoke(() =>
        {
            AnimateFileDeletion(new[] { filePath }, () =>
            {
                _fileShelf.RemoveFile(filePath);
                RefreshShelfLayout();
            });
        });
    }

    private void OnShelfFileExternallyRenamed(string oldPath, string newPath)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _fileShelf.HandleExternalRename(oldPath, newPath);
        });
    }

    #endregion

    #region Drop Visual State

    private void ResetShelfDropVisualState()
    {
        FileShelf.Background = _brushShelfNormalBg;
        FileShelfDashedBorder.Stroke = _brushShelfNormalBorder;
        FileShelfDashedBorder.StrokeDashArray = new DoubleCollection { 4, 3 };
        UpdateShelfCapacityIndicator();
    }

    private void SetShelfDropAcceptVisualState()
    {
        FileShelf.Background = _brushShelfActiveBg;
        FileShelfDashedBorder.Stroke = _brushShelfActiveBorder;
        FileShelfDashedBorder.StrokeDashArray = new DoubleCollection { 2, 1 };
        UpdateShelfCapacityIndicator();
    }

    private void SetShelfDropRejectVisualState(string message)
    {
        FileShelf.Background = _brushShelfErrorBg;
        FileShelfDashedBorder.Stroke = _brushShelfErrorBorder;
        FileShelfDashedBorder.StrokeDashArray = new DoubleCollection { 2, 1 };
        UpdateShelfCapacityIndicator(message, isError: true);
    }

    private void SetShelfDropUnlockHintVisualState()
    {
        FileShelf.Background = _brushShelfUnlockHintBg;
        FileShelfDashedBorder.Stroke = _brushShelfUnlockHintBorder;
        FileShelfDashedBorder.StrokeDashArray = new DoubleCollection { 2, 1 };
        UpdateShelfCapacityIndicator("Drop to unlock upload limit", isError: false);
    }

    #endregion

    #region Capacity Indicator

    private void UpdateShelfCapacityIndicator(string? transientMessage = null, bool isError = false)
    {
        if (ShelfStatusText == null || ShelfPlaceholder == null || ShelfCountText == null)
            return;

        const string defaultPlaceholderText = "Drag files here for temporary storage";

        ShelfPlaceholder.Text = Loc.Get("shelf.placeholder");
        ShelfPlaceholder.Visibility = _fileShelf.FileCount == 0 ? Visibility.Visible : Visibility.Collapsed;

        ShelfCountText.Text = _fileShelf.GetCountDisplayText();
        ShelfCountText.Foreground = _fileShelf.IsCountWarning ? _brushShelfStatusWarning : Brushes.White;

        if (!string.IsNullOrWhiteSpace(transientMessage))
        {
            ShelfStatusText.Text = transientMessage;
            ShelfStatusText.Foreground = isError ? _brushShelfStatusError : _brushShelfStatusWarning;
            ShelfStatusText.Visibility = Visibility.Visible;
            return;
        }

        var statusMsg = _fileShelf.GetStatusMessage();
        if (statusMsg != null)
        {
            ShelfStatusText.Text = statusMsg;
            ShelfStatusText.Foreground = _brushShelfStatusWarning;
            ShelfStatusText.Visibility = Visibility.Visible;
            return;
        }

        ShelfStatusText.Visibility = Visibility.Collapsed;
    }

    #endregion

    #region Drag & Drop Events

    private void FileShelf_DragEnter(object sender, DragEventArgs e)
    {
        var files = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? e.Data.GetData(DataFormats.FileDrop) as string[]
            : null;

        var validation = _fileShelf.ValidateDrop(files);

        switch (validation.Result)
        {
            case FileShelfController.DropResult.Accept:
                e.Effects = DragDropEffects.Copy;
                SetShelfDropAcceptVisualState();
                break;
            case FileShelfController.DropResult.UnlockPrompt:
                e.Effects = DragDropEffects.Copy;
                SetShelfDropUnlockHintVisualState();
                break;
            default:
                e.Effects = DragDropEffects.None;
                if (!string.IsNullOrEmpty(validation.Message))
                    SetShelfDropRejectVisualState(validation.Message);
                break;
        }

        e.Handled = true;
    }

    private void FileShelf_DragOver(object sender, DragEventArgs e)
    {
        var files = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? e.Data.GetData(DataFormats.FileDrop) as string[]
            : null;

        var validation = _fileShelf.ValidateDrop(files);

        switch (validation.Result)
        {
            case FileShelfController.DropResult.Accept:
                e.Effects = DragDropEffects.Copy;
                SetShelfDropAcceptVisualState();
                break;
            case FileShelfController.DropResult.UnlockPrompt:
                e.Effects = DragDropEffects.Copy;
                SetShelfDropUnlockHintVisualState();
                break;
            default:
                e.Effects = DragDropEffects.None;
                if (!string.IsNullOrEmpty(validation.Message))
                    SetShelfDropRejectVisualState(validation.Message);
                break;
        }

        e.Handled = true;
    }

    private void FileShelf_DragLeave(object sender, DragEventArgs e)
    {
        ResetShelfDropVisualState();
        e.Handled = true;
    }

    private void FileShelf_Drop(object sender, DragEventArgs e)
    {
        ResetShelfDropVisualState();

        var files = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? e.Data.GetData(DataFormats.FileDrop) as string[]
            : null;

        var validation = _fileShelf.ValidateDrop(files);

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

        e.Handled = true;
    }

    #endregion

    #region Unlock Banner

    private void ShowShelfUnlockBanner(int fileCount)
    {
        ShelfUnlockBanner.Visibility = Visibility.Visible;
        ShelfCountText.Visibility = Visibility.Collapsed;
        ShelfPlaceholder.Visibility = Visibility.Collapsed;
        ShelfUnlockMessageText.Text = Loc.Get("shelf.unlockMessage", fileCount);
        ShelfUnlockButtonText.Text = Loc.Get("shelf.unlockButton");
        ShelfUnlockDismissText.Text = Loc.Get("shelf.unlockDismiss");
        ShelfUnlockSettingsHint.Text = Loc.Get("shelf.unlockSettingsHint");
        ShelfUnlockSettingsHint.Visibility = Visibility.Visible;

        ShelfUnlockBanner.Opacity = 0;
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        ShelfUnlockBanner.BeginAnimation(OpacityProperty, fadeIn);
    }

    private void ShelfUnlockButton_Click(object sender, RoutedEventArgs e)
    {
        _fileShelf.UnlockLimit();
        HideShelfUnlockBanner();

        if (_pendingUnlockFiles != null && _pendingUnlockFiles.Length > 0)
        {
            _fileShelf.EnqueueFiles(_pendingUnlockFiles);
            _pendingUnlockFiles = null;
        }
    }

    private void ShelfUnlockDismiss_Click(object sender, RoutedEventArgs e)
    {
        _pendingUnlockFiles = null;
        HideShelfUnlockBanner();
    }

    private void HideShelfUnlockBanner()
    {
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += (s, e) =>
        {
            ShelfUnlockBanner.Visibility = Visibility.Collapsed;
            ShelfCountText.Visibility = Visibility.Visible;
            UpdateShelfCapacityIndicator();
        };
        ShelfUnlockBanner.BeginAnimation(OpacityProperty, fadeOut);
    }

    #endregion

    #region Shelf Layout & Item Creation

    private void RefreshShelfLayout()
    {
        ShelfItemsContainer.Children.Clear();
        bool useSmallSize = _fileShelf.FileCount > 4;

        foreach (var file in _fileShelf.Files)
        {
            var item = CreateShelfItem(file, useSmallSize);

            if (_fileShelf.IsSelected(file))
            {
                var border = (Border)item;
                border.Background = _brushShelfSelectedBg;
                border.BorderBrush = _brushShelfSelectedBorder;
            }
            ShelfItemsContainer.Children.Add(item);
        }
    }

    private FrameworkElement CreateShelfItem(string filePath, bool isSmall)
    {
        var fileName = Path.GetFileName(filePath);
        double width = isSmall ? 48 : 58;
        double height = isSmall ? 48 : 74;
        double iconSize = isSmall ? 26 : 34;

        var border = new Border
        {
            Width = width,
            Height = height,
            Margin = new Thickness(4),
            CornerRadius = new CornerRadius(isSmall ? 8 : 12),
            Background = (SolidColorBrush)new BrushConverter().ConvertFrom("#252525")!,
            BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFrom("#333333")!,
            BorderThickness = new Thickness(1),
            ToolTip = fileName,
            Tag = filePath,
            Cursor = Cursors.Hand,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1)
        };

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        var iconSource = GetFileIcon(filePath);
        var image = new Image
        {
            Source = iconSource,
            Width = iconSize,
            Height = iconSize,
            Margin = new Thickness(0, isSmall ? 2 : 6, 0, isSmall ? 2 : 4),
            Stretch = Stretch.UniformToFill,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 10, Opacity = 0.3, ShadowDepth = 2 },
            Clip = new RectangleGeometry
            {
                RadiusX = isSmall ? 4 : 6,
                RadiusY = isSmall ? 4 : 6,
                Rect = new Rect(0, 0, iconSize, iconSize)
            }
        };

        stack.Children.Add(image);

        var text = new TextBlock
        {
            Text = fileName,
            Style = (Style)FindResource("SmallText"),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(isSmall ? 2 : 6, 0, isSmall ? 2 : 6, isSmall ? 2 : 6),
            Visibility = isSmall ? Visibility.Collapsed : Visibility.Visible
        };

        if (!isSmall) stack.Children.Add(text);

        border.Child = stack;

        AttachShelfItemEvents(border, filePath);
        return border;
    }

    #endregion

    #region Shelf Item Events

    private void AttachShelfItemEvents(Border border, string filePath)
    {
        border.MouseEnter += (s, e) =>
        {
            if (!_fileShelf.IsSelected(filePath))
            {
                border.Background = (SolidColorBrush)new BrushConverter().ConvertFrom("#303030")!;
                border.BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFrom("#555555")!;
            }
            if (!_isSweepSelecting && border.RenderTransform is ScaleTransform st)
                AnimateButtonScale(st, 1.05);
        };
        border.MouseLeave += (s, e) =>
        {
            if (!_fileShelf.IsSelected(filePath))
            {
                border.Background = (SolidColorBrush)new BrushConverter().ConvertFrom("#252525")!;
                border.BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFrom("#333333")!;
            }
            if (!_isSweepSelecting && border.RenderTransform is ScaleTransform st)
                AnimateButtonScale(st, 1.0);
        };

        var menu = new ContextMenu();
        var remove = new MenuItem { Header = "Remove from shelf" };
        remove.Click += (s, e) => RemoveFileFromShelf(filePath, border);
        menu.Items.Add(remove);
        border.ContextMenu = menu;

        border.MouseLeftButtonDown += (s, e) =>
        {
            _dragStartPoint = e.GetPosition(null);
            _selectionStart = e.GetPosition(FileShelfGrid);
            _didDragOutFromShelf = false;
            _isSweepSelecting = false;
            _sweepStartFile = filePath;

            _wasSelectedOnMouseDown = _fileShelf.IsSelected(filePath);

            border.CaptureMouse();
            e.Handled = true;

            SelectionRect.Width = 0;
            SelectionRect.Height = 0;

            bool isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            if (isCtrl)
            {
                _fileShelf.ToggleSelection(filePath);
            }
            else
            {
                if (!_wasSelectedOnMouseDown)
                {
                    _fileShelf.Select(filePath);
                }
            }

            foreach (var child in ShelfItemsContainer.Children)
            {
                if (child is Border b && b.Tag is string p)
                    UpdateShelfItemVisualState(b, _fileShelf.IsSelected(p));
            }
        };

        border.MouseLeftButtonUp += (s, e) =>
        {
            if (_didDragOutFromShelf || _isSweepSelecting || _isSelecting)
            {
                _isSelecting = false;
                _isSweepSelecting = false;
                _sweepStartFile = null;
                SelectionCanvas.Visibility = Visibility.Collapsed;

                if (border.IsMouseCaptured) border.ReleaseMouseCapture();

                foreach (var child in ShelfItemsContainer.Children)
                {
                    if (child is Border b && b.RenderTransform is ScaleTransform st)
                        AnimateButtonScale(st, 1.0);
                }
                return;
            }

            _sweepStartFile = null;
            bool isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            if (!isCtrl && _fileShelf.SelectedFiles.Count > 1 && _wasSelectedOnMouseDown)
            {
                _fileShelf.Select(filePath);
                foreach (var child in ShelfItemsContainer.Children)
                {
                    if (child is Border b && b.Tag is string p)
                        UpdateShelfItemVisualState(b, _fileShelf.IsSelected(p));
                }
            }

            if (border.IsMouseCaptured) border.ReleaseMouseCapture();
        };

        border.MouseMove += (s, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed && !_isSelecting && !_isSweepSelecting)
            {
                Point currentPosGlobal = e.GetPosition(null);
                Vector diff = _dragStartPoint - currentPosGlobal;

                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    if (_wasSelectedOnMouseDown)
                    {
                        if (border.IsMouseCaptured) border.ReleaseMouseCapture();
                        _didDragOutFromShelf = true;

                        var filesToDrag = _fileShelf.GetDragFiles();
                        var data = new DataObject(DataFormats.FileDrop, filesToDrag);
                        var result = DragDrop.DoDragDrop(border, data, DragDropEffects.Copy | DragDropEffects.Move);

                        if (result == DragDropEffects.Move)
                        {
                            _fileShelf.HandleDragMoveOut(filesToDrag);
                        }
                        return;
                    }

                    Point posInGrid = e.GetPosition(FileShelfGrid);
                    bool isInsideShelf = posInGrid.X >= 0 && posInGrid.Y >= 0 &&
                                         posInGrid.X <= FileShelfGrid.ActualWidth &&
                                         posInGrid.Y <= FileShelfGrid.ActualHeight;

                    if (isInsideShelf)
                    {
                        _isSelecting = true;
                        _isSweepSelecting = true;
                        SelectionCanvas.Visibility = Visibility.Visible;

                        bool isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                        if (isCtrl)
                            _selectionInitialState = new HashSet<string>(_fileShelf.SelectedFiles);
                        else
                            _selectionInitialState.Clear();
                    }
                    else
                    {
                        if (border.IsMouseCaptured) border.ReleaseMouseCapture();
                        _didDragOutFromShelf = true;
                        var data = new DataObject(DataFormats.FileDrop, new[] { filePath });
                        DragDrop.DoDragDrop(border, data, DragDropEffects.Copy | DragDropEffects.Move);
                    }
                }
            }

            if (_isSelecting && border.IsMouseCaptured)
            {
                HandleRectangleSelection(e.GetPosition(FileShelfGrid));
                e.Handled = true;
            }
        };
    }

    #endregion

    #region Rectangle Selection

    private void FileShelf_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_isAnimating) return;

        _dragStartPoint = e.GetPosition(null);
        _selectionStart = e.GetPosition(FileShelfGrid);
        _isSelecting = false;
        _isSweepSelecting = false;
        _wasSelectedOnMouseDown = false;

        bool isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (isCtrl)
        {
            _selectionInitialState = new HashSet<string>(_fileShelf.SelectedFiles);
        }
        else
        {
            _fileShelf.ClearSelection();
            _selectionInitialState.Clear();

            foreach (var child in ShelfItemsContainer.Children)
            {
                if (child is Border item) UpdateShelfItemVisualState(item, false);
            }
        }

        SelectionCanvas.Visibility = Visibility.Collapsed;
        SelectionRect.Width = 0;
        SelectionRect.Height = 0;

        FileShelfGrid.CaptureMouse();
        e.Handled = true;
    }

    private void FileShelf_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            Point posInGrid = e.GetPosition(FileShelfGrid);

            if (!_isSelecting)
            {
                Point currentPosGlobal = e.GetPosition(null);
                Vector diff = _dragStartPoint - currentPosGlobal;

                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    _isSelecting = true;
                    _selectionStart = posInGrid;
                    SelectionCanvas.Visibility = Visibility.Visible;
                }
            }

            if (_isSelecting)
            {
                HandleRectangleSelection(posInGrid);
            }
        }
        else if (_isSelecting)
        {
            // Mouse button released outside (capture lost or missed MouseUp) — cancel lasso
            CancelLassoSelection();
        }
    }

    private void HandleRectangleSelection(Point currentPos)
    {
        if (!_isSelecting) return;

        double gridWidth = FileShelfGrid.ActualWidth;
        double gridHeight = FileShelfGrid.ActualHeight;

        double startX = _selectionStart.X;
        double startY = _selectionStart.Y;
        double endX = Math.Max(0, Math.Min(gridWidth, currentPos.X));
        double endY = Math.Max(0, Math.Min(gridHeight, currentPos.Y));

        double x = Math.Min(startX, endX);
        double y = Math.Min(startY, endY);
        double width = Math.Abs(startX - endX);
        double height = Math.Abs(startY - endY);

        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width = width;
        SelectionRect.Height = height;

        Rect selectionRect = new Rect(x, y, width, height);
        bool isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        // Determine which items intersect the selection rectangle
        var intersected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in ShelfItemsContainer.Children)
        {
            if (child is Border item && item.Tag is string path)
            {
                GeneralTransform transform = item.TransformToAncestor(FileShelfGrid);
                Rect itemBounds = transform.TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));

                if (selectionRect.IntersectsWith(itemBounds))
                    intersected.Add(path);
            }
        }

        _fileShelf.ApplyRectangleSelection(intersected, isCtrl, _selectionInitialState);

        // Update visuals
        foreach (var child in ShelfItemsContainer.Children)
        {
            if (child is Border item && item.Tag is string path)
                UpdateShelfItemVisualState(item, _fileShelf.IsSelected(path));
        }
    }

    private void UpdateShelfItemVisualState(Border item, bool isSelected)
    {
        var targetBg = isSelected ? _brushShelfSelectedBg : _brushShelfItemBg;
        var targetBorder = isSelected ? _brushShelfSelectedBorder : _brushShelfItemBorder;

        if (item.Background != targetBg) item.Background = targetBg;
        if (item.BorderBrush != targetBorder) item.BorderBrush = targetBorder;
    }

    private void FileShelf_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isSweepSelecting)
        {
            _isSweepSelecting = false;
            _sweepStartFile = null;

            foreach (var child in ShelfItemsContainer.Children)
            {
                if (child is Border b)
                    AnimateButtonScale((ScaleTransform)b.RenderTransform!, 1.0);
            }
        }
        _sweepStartFile = null;

        _isSelecting = false;
        SelectionCanvas.Visibility = Visibility.Collapsed;

        if (FileShelfGrid.IsMouseCaptured)
            FileShelfGrid.ReleaseMouseCapture();
    }

    private void FileShelfGrid_LostMouseCapture(object sender, MouseEventArgs e)
    {
        // If mouse capture is lost (e.g. mouse leaves interaction zone), clean up lasso state
        CancelLassoSelection();
    }

    private void CancelLassoSelection()
    {
        if (!_isSelecting && !FileShelfGrid.IsMouseCaptured) return;

        _isSelecting = false;
        SelectionCanvas.Visibility = Visibility.Collapsed;

        if (FileShelfGrid.IsMouseCaptured)
            FileShelfGrid.ReleaseMouseCapture();
    }

    #endregion

    #region Keyboard Shortcuts

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) != 0
            && _isSecondaryView && _fileShelf.FileCount > 0)
        {
            _fileShelf.SelectAll();

            foreach (var child in ShelfItemsContainer.Children)
            {
                if (child is Border b && b.Tag is string path)
                    UpdateShelfItemVisualState(b, _fileShelf.IsSelected(path));
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && _isSecondaryView && _fileShelf.SelectedFiles.Count > 0 && !_isAnimating)
        {
            _isAnimating = true;
            var filesToRemove = _fileShelf.GetSelectedForDeletion();

            AnimateFileDeletion(filesToRemove, () =>
            {
                _fileShelf.RemoveFiles(filesToRemove);
                RefreshShelfLayout();
                _isAnimating = false;
            });
            e.Handled = true;
        }
    }

    #endregion

    #region File Deletion Animation

    private void AnimateFileDeletion(IEnumerable<string> filePaths, Action onCompletion)
    {
        var targets = new List<FrameworkElement>();
        foreach (var path in filePaths)
        {
            foreach (var child in ShelfItemsContainer.Children)
            {
                if (child is Border b && (string)b.Tag == path)
                {
                    targets.Add(b);
                    break;
                }
            }
        }

        if (targets.Count == 0)
        {
            onCompletion();
            return;
        }

        var dur = new Duration(TimeSpan.FromMilliseconds(320));
        var easeBack = new BackEase { EasingMode = EasingMode.EaseIn, Amplitude = 0.6 };
        easeBack.Freeze();

        int completed = 0;
        int total = targets.Count;

        foreach (var target in targets)
        {
            var st = new ScaleTransform(1, 1);
            var tt = new TranslateTransform(0, 0);
            var group = new TransformGroup();
            group.Children.Add(st);
            group.Children.Add(tt);

            target.RenderTransform = group;
            target.RenderTransformOrigin = new Point(0.5, 0.5);

            var fadeOut = new DoubleAnimation(1, 0, dur) { EasingFunction = _easeExpOut6 };
            var scaleDown = new DoubleAnimation(1, 0, dur) { EasingFunction = easeBack };
            var slideUp = new DoubleAnimation(0, -25, dur) { EasingFunction = _easeExpOut6 };

            Timeline.SetDesiredFrameRate(fadeOut, 144);
            Timeline.SetDesiredFrameRate(scaleDown, 144);
            Timeline.SetDesiredFrameRate(slideUp, 144);

            scaleDown.Completed += (s, e) =>
            {
                completed++;
                if (completed == total)
                {
                    onCompletion();
                }
            };

            target.BeginAnimation(OpacityProperty, fadeOut);
            st.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDown);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDown);
            tt.BeginAnimation(TranslateTransform.YProperty, slideUp);
        }
    }

    #endregion

    #region Progress Animation & Scrollbar

    private void AnimateShelfItemProgress(FrameworkElement item)
    {
        if (item is not Border border) return;

        var progressBar = new Border
        {
            Height = 2,
            CornerRadius = new CornerRadius(1),
            Background = new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(4, 0, 4, 3),
            Width = 0,
            IsHitTestVisible = false
        };

        var originalChild = border.Child;
        var overlay = new Grid();
        if (originalChild != null)
        {
            border.Child = null;
            overlay.Children.Add(originalChild);
        }
        overlay.Children.Add(progressBar);
        border.Child = overlay;

        void StartProgress()
        {
            double targetW = Math.Max(8, border.ActualWidth - 8);
            var grow = new DoubleAnimation(0, targetW, new Duration(TimeSpan.FromMilliseconds(320)))
            {
                EasingFunction = _easeExpOut6
            };
            Timeline.SetDesiredFrameRate(grow, 120);
            grow.Completed += (_, _) =>
            {
                var fade = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(280)))
                {
                    BeginTime = TimeSpan.FromMilliseconds(100)
                };
                fade.Completed += (_, _) =>
                {
                    overlay.Children.Remove(progressBar);
                    if (overlay.Children.Count == 1 && overlay.Children[0] is UIElement only)
                    {
                        overlay.Children.Remove(only);
                        border.Child = only;
                    }
                };
                progressBar.BeginAnimation(OpacityProperty, fade);
            };
            progressBar.BeginAnimation(WidthProperty, grow);
        }

        if (border.IsLoaded)
            StartProgress();
        else
            border.Loaded += (_, _) => StartProgress();
    }

    private DispatcherTimer? _scrollbarFadeTimer;

    private void ShelfScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
            e.Handled = true;
            ShowShelfScrollbar();
        }
    }

    private void ShowShelfScrollbar()
    {
        var scrollBar = FindVisualChild<System.Windows.Controls.Primitives.ScrollBar>(ShelfScrollViewer);
        if (scrollBar != null)
        {
            scrollBar.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
        }

        _scrollbarFadeTimer?.Stop();
        _scrollbarFadeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1200)
        };
        _scrollbarFadeTimer.Tick += (s, e) =>
        {
            _scrollbarFadeTimer.Stop();
            if (scrollBar != null)
            {
                scrollBar.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(400)));
            }
        };
        _scrollbarFadeTimer.Start();
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result) return result;
            var found = FindVisualChild<T>(child);
            if (found != null) return found;
        }
        return null;
    }

    #endregion

    #region Single File Remove

    private void RemoveFileFromShelf(string filePath, Border item)
    {
        if (_isAnimating) return;
        _isAnimating = true;

        AnimateFileDeletion(new[] { filePath }, () =>
        {
            _fileShelf.RemoveFile(filePath);
            RefreshShelfLayout();
            _isAnimating = false;
        });
    }

    private ImageSource? GetFileIcon(string filePath) => FileIconProvider.GetFileIcon(filePath);

    #endregion

    #region Dispose Shelf

    private void DisposeAllShelfWatchers()
    {
        _fileShelf?.Dispose();
    }

    #endregion
}
