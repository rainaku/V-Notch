using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using Microsoft.Win32;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media;
using Windows.Media.MediaProperties;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private bool _isSecondaryView = false;
    private readonly List<string> _shelfFiles = new List<string>();
    private readonly HashSet<string> _selectedFiles = new HashSet<string>();

    private static readonly SolidColorBrush _brushShelfItemBg = CreateFrozenBrush(37, 37, 37);
    private static readonly SolidColorBrush _brushShelfItemBorder = CreateFrozenBrush(51, 51, 51);
    private static readonly SolidColorBrush _brushShelfSelectedBg     = CreateFrozenBrush(255, 255, 255, 40);  
    private static readonly SolidColorBrush _brushShelfSelectedBorder = CreateFrozenBrush(255, 255, 255, 140); 

    private Point _selectionStart;
    private bool _isSelecting = false;
    private Point _dragStartPoint;
    private bool _didDragOutFromShelf = false;
    private bool _isSweepSelecting = false;
    private string? _sweepStartFile = null;
    private bool _wasSelectedOnMouseDown = false;

    private readonly Dictionary<string, FileSystemWatcher> _shelfWatchers = new();
    private bool _isCameraSectionExpanded = false;
    private int _cameraSectionAnimToken = 0;
    private double _cameraSectionCompactWidth = 0;
    private Thickness _cameraSectionCompactMargin = new Thickness(0, 0, 8, 0);


    #region File Shelf Logic

    private void FileShelf_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            FileShelf.Background = (SolidColorBrush)new BrushConverter().ConvertFrom("#2A2A2A")!;
            FileShelfDashedBorder.Stroke = (SolidColorBrush)new BrushConverter().ConvertFrom("#99FFFFFF")!;
            FileShelfDashedBorder.StrokeDashArray = new DoubleCollection { 2, 1 };
        }
    }

    private void FileShelf_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
        }
    }

    private void FileShelf_DragLeave(object sender, DragEventArgs e)
    {
        FileShelf.Background = (SolidColorBrush)new BrushConverter().ConvertFrom("#1A1A1A")!;
        FileShelfDashedBorder.Stroke = (SolidColorBrush)new BrushConverter().ConvertFrom("#333333")!;
        FileShelfDashedBorder.StrokeDashArray = new DoubleCollection { 4, 3 };
    }

    private void FileShelf_Drop(object sender, DragEventArgs e)
    {
        FileShelf.Background = (SolidColorBrush)new BrushConverter().ConvertFrom("#1A1A1A")!;
        FileShelfDashedBorder.Stroke = (SolidColorBrush)new BrushConverter().ConvertFrom("#333333")!;
        FileShelfDashedBorder.StrokeDashArray = new DoubleCollection { 4, 3 };

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            
            var newFiles = files.Where(f => !_shelfFiles.Contains(f)).ToArray();
            if (newFiles.Length > 0)
                AddFilesToShelfSequential(newFiles);
        }
    }

    
    private readonly Queue<string> _pendingShelfFiles = new Queue<string>();
    private bool _isAddingSequential = false;

    private void AddFilesToShelfSequential(string[] filePaths)
    {
        foreach (var f in filePaths)
            _pendingShelfFiles.Enqueue(f);

        if (!_isAddingSequential)
            ProcessNextPendingFile();
    }

    private void ProcessNextPendingFile()
    {
        if (_pendingShelfFiles.Count == 0)
        {
            _isAddingSequential = false;
            return;
        }

        _isAddingSequential = true;
        var filePath = _pendingShelfFiles.Dequeue();

        if (!_shelfFiles.Contains(filePath))
        {
            _shelfFiles.Add(filePath);
            ShelfPlaceholder.Visibility = Visibility.Collapsed;
            WatchShelfDirectory(filePath);

            
            RefreshShelfLayout();

            
            if (ShelfItemsContainer.Children.Count > 0)
            {
                var lastItem = ShelfItemsContainer.Children[ShelfItemsContainer.Children.Count - 1] as FrameworkElement;
                if (lastItem != null)
                {
                    lastItem.Opacity = 0;
                    lastItem.RenderTransformOrigin = new Point(0.5, 0.5);
                    var st = new ScaleTransform(0.75, 0.75);
                    lastItem.RenderTransform = st;

                    var fadeIn  = new DoubleAnimation(0, 1,    new Duration(TimeSpan.FromMilliseconds(200))) { EasingFunction = _easeExpOut6 };
                    var scaleIn = new DoubleAnimation(0.75, 1, new Duration(TimeSpan.FromMilliseconds(280))) { EasingFunction = _easeMenuSpring };
                    Timeline.SetDesiredFrameRate(fadeIn,  120);
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
        }

        
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(90)
        };
        timer.Tick += (s, args) =>
        {
            timer.Stop();
            ProcessNextPendingFile();
        };
        timer.Start();
    }

    
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

    private void AddFileToShelf(string filePath)
    {
        if (_shelfFiles.Contains(filePath)) return;
        _shelfFiles.Add(filePath);
        ShelfPlaceholder.Visibility = Visibility.Collapsed;
        WatchShelfDirectory(filePath);
        RefreshShelfLayout();
    }

    private void RefreshShelfLayout()
    {
        ShelfItemsContainer.Children.Clear();
        bool useSmallSize = _shelfFiles.Count > 4;

        foreach (var file in _shelfFiles)
        {
            var item = CreateShelfItem(file, useSmallSize);

            if (_selectedFiles.Contains(file))
            {
                var border = (Border)item;
                border.Background  = _brushShelfSelectedBg;
                border.BorderBrush = _brushShelfSelectedBorder;
            }
            ShelfItemsContainer.Children.Add(item);
        }
    }

    private FrameworkElement CreateShelfItem(string filePath, bool isSmall)
    {
        var fileName = Path.GetFileName(filePath);
        double width = isSmall ? 52 : 64;
        double height = isSmall ? 52 : 80;
        double iconSize = isSmall ? 28 : 36;

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


    private void AttachShelfItemEvents(Border border, string filePath)
    {
        border.MouseEnter += (s, e) =>
        {
            if (!_selectedFiles.Contains(filePath))
            {
                border.Background = (SolidColorBrush)new BrushConverter().ConvertFrom("#303030")!;
                border.BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFrom("#555555")!;
            }
            if (!_isSweepSelecting && border.RenderTransform is ScaleTransform st)
                AnimateButtonScale(st, 1.05);
        };
        border.MouseLeave += (s, e) =>
        {
            if (!_selectedFiles.Contains(filePath))
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

            _wasSelectedOnMouseDown = _selectedFiles.Contains(filePath);

            border.CaptureMouse();
            e.Handled = true;

            SelectionRect.Width = 0;
            SelectionRect.Height = 0;

            bool isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            if (isCtrl)
            {
                if (_selectedFiles.Contains(filePath)) _selectedFiles.Remove(filePath);
                else _selectedFiles.Add(filePath);
            }
            else
            {
                if (!_wasSelectedOnMouseDown)
                {
                    _selectedFiles.Clear();
                    _selectedFiles.Add(filePath);
                }
            }

            foreach (var child in ShelfItemsContainer.Children)
            {
                if (child is Border b && b.Tag is string p)
                    UpdateShelfItemVisualState(b, _selectedFiles.Contains(p));
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

            if (!isCtrl && _selectedFiles.Count > 1 && _wasSelectedOnMouseDown)
            {
                _selectedFiles.Clear();
                _selectedFiles.Add(filePath);
                foreach (var child in ShelfItemsContainer.Children)
                {
                    if (child is Border b && b.Tag is string p)
                        UpdateShelfItemVisualState(b, _selectedFiles.Contains(p));
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

                        var filesToDrag = _selectedFiles.ToArray();
                        var data = new DataObject(DataFormats.FileDrop, filesToDrag);
                        var result = DragDrop.DoDragDrop(border, data, DragDropEffects.Copy | DragDropEffects.Move);

                        if (result == DragDropEffects.Move)
                        {
                            foreach (var f in filesToDrag)
                            {
                                _shelfFiles.Remove(f);
                                _selectedFiles.Remove(f);
                            }
                            RefreshShelfLayout();
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
                            _selectionInitialState = new HashSet<string>(_selectedFiles);
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

    private HashSet<string> _selectionInitialState = new HashSet<string>();

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
            _selectionInitialState = new HashSet<string>(_selectedFiles);
        }
        else
        {
            _selectedFiles.Clear();
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

        foreach (var child in ShelfItemsContainer.Children)
        {
            if (child is Border item && item.Tag is string path)
            {

                GeneralTransform transform = item.TransformToAncestor(FileShelfGrid);
                Rect itemBounds = transform.TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));

                bool intersects = selectionRect.IntersectsWith(itemBounds);

                if (isCtrl)
                {
                    if (intersects)
                    {
                        if (_selectionInitialState.Contains(path)) _selectedFiles.Remove(path);
                        else _selectedFiles.Add(path);
                    }
                    else
                    {
                        if (_selectionInitialState.Contains(path)) _selectedFiles.Add(path);
                        else _selectedFiles.Remove(path);
                    }
                }
                else
                {
                    if (intersects) _selectedFiles.Add(path);
                    else _selectedFiles.Remove(path);
                }

                UpdateShelfItemVisualState(item, _selectedFiles.Contains(path));
            }
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

        if (!_isSelecting) return;
        _isSelecting = false;
        SelectionCanvas.Visibility = Visibility.Collapsed;
        FileShelfGrid.ReleaseMouseCapture();
        RefreshShelfLayout();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {

        if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) != 0
            && _isSecondaryView && _shelfFiles.Count > 0)
        {
            _selectedFiles.Clear();
            foreach (var file in _shelfFiles)
                _selectedFiles.Add(file);

            foreach (var child in ShelfItemsContainer.Children)
            {
                if (child is Border b && b.Tag is string path)
                    UpdateShelfItemVisualState(b, _selectedFiles.Contains(path));
            }
            e.Handled = true;
        }

        else if (e.Key == Key.Delete && _isSecondaryView && _selectedFiles.Count > 0 && !_isAnimating)
        {
            _isAnimating = true;
            var filesToRemove = _selectedFiles.ToList();
            
            AnimateFileDeletion(filesToRemove, () => 
            {
                foreach (var file in filesToRemove)
                {
                    _shelfFiles.Remove(file);
                    _selectedFiles.Remove(file);
                    UnwatchShelfDirectory(file);
                }

                RefreshShelfLayout();
                if (_shelfFiles.Count == 0)
                {
                    ShelfPlaceholder.Visibility = Visibility.Visible;
                }
                _isAnimating = false;
            });
            e.Handled = true;
        }
    }

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

    private System.Windows.Threading.DispatcherTimer? _scrollbarFadeTimer;

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
        _scrollbarFadeTimer = new System.Windows.Threading.DispatcherTimer
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

    private void UpdatePaginationDots()
    {
        if (Dot1Scale == null || Dot2Scale == null) return;

        var activeBrush = Brushes.White;
        var inactiveBrush = new SolidColorBrush(Color.FromRgb(85, 85, 85)); 
        inactiveBrush.Freeze();

        var dur = new Duration(TimeSpan.FromMilliseconds(600)); 
        var ease = _easeMenuSpring; 

        if (_isSecondaryView)
        {
            Dot1.Fill = inactiveBrush;
            Dot2.Fill = activeBrush;

            Dot1Scale.BeginAnimation(ScaleTransform.ScaleXProperty, MakeAnim(0.7, dur, ease));
            Dot1Scale.BeginAnimation(ScaleTransform.ScaleYProperty, MakeAnim(0.7, dur, ease));

            Dot2Scale.BeginAnimation(ScaleTransform.ScaleXProperty, MakeAnim(1.3, dur, ease));
            Dot2Scale.BeginAnimation(ScaleTransform.ScaleYProperty, MakeAnim(1.3, dur, ease));
        }
        else
        {
            Dot1.Fill = activeBrush;
            Dot2.Fill = inactiveBrush;

            Dot1Scale.BeginAnimation(ScaleTransform.ScaleXProperty, MakeAnim(1.3, dur, ease));
            Dot1Scale.BeginAnimation(ScaleTransform.ScaleYProperty, MakeAnim(1.3, dur, ease));

            Dot2Scale.BeginAnimation(ScaleTransform.ScaleXProperty, MakeAnim(0.7, dur, ease));
            Dot2Scale.BeginAnimation(ScaleTransform.ScaleYProperty, MakeAnim(0.7, dur, ease));
        }
    }

    private void RemoveFileFromShelf(string filePath, Border item)
    {
        if (_isAnimating) return;
        _isAnimating = true;

        AnimateFileDeletion(new[] { filePath }, () => 
        {
            _shelfFiles.Remove(filePath);
            _selectedFiles.Remove(filePath);
            UnwatchShelfDirectory(filePath);
            RefreshShelfLayout();

            if (_shelfFiles.Count == 0)
            {
                ShelfPlaceholder.Visibility = Visibility.Visible;
            }
            _isAnimating = false;
        });
    }

    private void WatchShelfDirectory(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir) || _shelfWatchers.ContainsKey(dir)) return;

        try
        {
            var watcher = new FileSystemWatcher(dir)
            {
                NotifyFilter = NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            watcher.Deleted += OnShelfFileExternalChange;
            watcher.Renamed += OnShelfFileExternalRename;
            _shelfWatchers[dir] = watcher;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("SHELF-WATCH", ex.ToString());
        }
    }

    private void UnwatchShelfDirectory(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir)) return;

        bool hasOtherFiles = _shelfFiles.Any(f =>
            string.Equals(Path.GetDirectoryName(f), dir, StringComparison.OrdinalIgnoreCase));

        if (!hasOtherFiles && _shelfWatchers.TryGetValue(dir, out var watcher))
        {
            watcher.EnableRaisingEvents = false;
            watcher.Deleted -= OnShelfFileExternalChange;
            watcher.Renamed -= OnShelfFileExternalRename;
            watcher.Dispose();
            _shelfWatchers.Remove(dir);
        }
    }

    private void OnShelfFileExternalChange(object sender, FileSystemEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_shelfFiles.Contains(e.FullPath) || _isAddingSequential) return;

            AnimateFileDeletion(new[] { e.FullPath }, () => 
            {
                _shelfFiles.Remove(e.FullPath);
                _selectedFiles.Remove(e.FullPath);
                UnwatchShelfDirectory(e.FullPath);
                RefreshShelfLayout();

                if (_shelfFiles.Count == 0)
                    ShelfPlaceholder.Visibility = Visibility.Visible;
            });
        });
    }

    private void OnShelfFileExternalRename(object sender, RenamedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_shelfFiles.Contains(e.OldFullPath)) return;

            int idx = _shelfFiles.IndexOf(e.OldFullPath);
            if (idx >= 0) _shelfFiles[idx] = e.FullPath;

            if (_selectedFiles.Remove(e.OldFullPath))
                _selectedFiles.Add(e.FullPath);

            WatchShelfDirectory(e.FullPath);
            UnwatchShelfDirectory(e.OldFullPath);

            RefreshShelfLayout();
        });
    }

    private void DisposeAllShelfWatchers()
    {
        foreach (var watcher in _shelfWatchers.Values)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _shelfWatchers.Clear();
    }

    private ImageSource? GetFileIcon(string filePath) => FileIconProvider.GetFileIcon(filePath);

    #endregion

}
