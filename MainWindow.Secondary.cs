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

namespace VNotch;

public partial class MainWindow
{
    private bool _isSecondaryView = false;
    private readonly List<string> _shelfFiles = new List<string>();
    private readonly HashSet<string> _selectedFiles = new HashSet<string>();
    
    // Cached Brushes for Shelf Selection
    private static readonly SolidColorBrush _brushShelfItemBg = CreateFrozenBrush(37, 37, 37);
    private static readonly SolidColorBrush _brushShelfItemBorder = CreateFrozenBrush(51, 51, 51);
    private static readonly SolidColorBrush _brushShelfSelectedBg = CreateFrozenBrush(0, 122, 255, 64); // #40007AFF
    private static readonly SolidColorBrush _brushShelfSelectedBorder = CreateFrozenBrush(0, 122, 255); // #007AFF
    
    // Multi-selection state
    private Point _selectionStart;
    private bool _isSelecting = false;
    private Point _dragStartPoint;
    private bool _didDragOutFromShelf = false;
    private bool _isSweepSelecting = false;
    private string? _sweepStartFile = null;
    private bool _wasSelectedOnMouseDown = false;
    
    // File watchers for external deletion detection
    private readonly Dictionary<string, FileSystemWatcher> _shelfWatchers = new();
    
    // Camera state
    // Note: Since we are in WPF but targeting Windows SDK, we can use WinRT if properly referenced.
    // For now, let's use a simpler approach or UI placeholders if WinRT is too restricted here.
    // Given net8.0-windows10.0.19041.0, we can use Windows.Media.Capture.
    
    private void NotchWrapper_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_isExpanded || _isAnimating) return;

        if (e.Delta < 0) // Scroll Down
        {
            if (!_isSecondaryView)
            {
                SwitchToSecondaryView();
            }
        }
        else if (e.Delta > 0) // Scroll Up
        {
            if (_isSecondaryView)
            {
                SwitchToPrimaryView();
            }
        }
    }

    private void SecondaryContent_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // Prevent bubbling to NotchBorder which would close the notch
    }

    private void SwitchToSecondaryView()
    {
        if (_isSecondaryView || _isAnimating) return;
        _isSecondaryView = true;
        _isAnimating = true;

        // Protection: Disable clicks during transit
        NotchBorder.IsHitTestVisible = false;

        // Smooth durations for premium feel
        var durTotal = new Duration(TimeSpan.FromMilliseconds(450));
        var durFast = new Duration(TimeSpan.FromMilliseconds(200));

        // PRIMARY (OUT) -> Scale down and fade
        var primaryGroup = new TransformGroup();
        var primaryScale = new ScaleTransform(1, 1);
        var primaryTranslate = new TranslateTransform(0, 0);
        primaryGroup.Children.Add(primaryScale);
        primaryGroup.Children.Add(primaryTranslate);
        ExpandedContent.RenderTransform = primaryGroup;
        ExpandedContent.RenderTransformOrigin = new Point(0.5, 0.5);

        var fadeOut = MakeAnim(1, 0, durFast, _easeQuadOut);
        var scaleOut = MakeAnim(1, 0.92, durTotal, _easeExpOut7);
        var slideOut = MakeAnim(0, -10, durTotal, _easeExpOut7);

        fadeOut.Completed += (s, e) => {
            ExpandedContent.Visibility = Visibility.Collapsed;
            ExpandedContent.RenderTransform = null;
            ExpandedContent.Effect = null; // Cleanup depth effect
        };

        ExpandedContent.BeginAnimation(OpacityProperty, fadeOut);
        primaryScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleOut);
        primaryScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleOut);
        primaryTranslate.BeginAnimation(TranslateTransform.YProperty, slideOut);

        // Add Depth: Blur outgoing content
        var blur = new BlurEffect { Radius = 0, KernelType = KernelType.Gaussian };
        ExpandedContent.Effect = blur;
        var blurAnim = MakeAnim(0, 15, durFast, _easeQuadOut);
        blur.BeginAnimation(BlurEffect.RadiusProperty, blurAnim);

        // SECONDARY (IN) -> Spring scale up and slide in
        SecondaryContent.Visibility = Visibility.Visible;
        SecondaryContent.Opacity = 0;
        EnableKeyboardInput();

        var secondaryGroup = new TransformGroup();
        var secondaryScale = new ScaleTransform(1.08, 1.08);
        var secondaryTranslate = new TranslateTransform(0, 15);
        secondaryGroup.Children.Add(secondaryScale);
        secondaryGroup.Children.Add(secondaryTranslate);
        SecondaryContent.RenderTransform = secondaryGroup;
        SecondaryContent.RenderTransformOrigin = new Point(0.5, 0.5);

        var fadeIn = MakeAnim(0, 1, durTotal, _easePowerOut3);
        // Tighter spring feel (Damped Spring simulation)
        var springScale = MakeAnim(1.08, 1, durTotal, _easeMenuSpring); 
        var springSlide = MakeAnim(15, 0, durTotal, _easeExpOut7);

        fadeIn.Completed += (s, e) => {
            _isAnimating = false;
            NotchBorder.IsHitTestVisible = true;
            SecondaryContent.BeginAnimation(OpacityProperty, null);
            SecondaryContent.Opacity = 1;
            SecondaryContent.RenderTransform = null;
        };

        SecondaryContent.BeginAnimation(OpacityProperty, fadeIn);
        secondaryScale.BeginAnimation(ScaleTransform.ScaleXProperty, springScale);
        secondaryScale.BeginAnimation(ScaleTransform.ScaleYProperty, springScale);
        secondaryTranslate.BeginAnimation(TranslateTransform.YProperty, springSlide);
    }

    private void SwitchToPrimaryView()
    {
        if (!_isSecondaryView || _isAnimating) return;
        _isSecondaryView = false;
        _isAnimating = true;

        NotchBorder.IsHitTestVisible = false;

        var durTotal = new Duration(TimeSpan.FromMilliseconds(450));
        var durFast = new Duration(TimeSpan.FromMilliseconds(200));

        // SECONDARY (OUT)
        var secondaryGroup = new TransformGroup();
        var secondaryScale = new ScaleTransform(1, 1);
        var secondaryTranslate = new TranslateTransform(0, 0);
        secondaryGroup.Children.Add(secondaryScale);
        secondaryGroup.Children.Add(secondaryTranslate);
        SecondaryContent.RenderTransform = secondaryGroup;
        SecondaryContent.RenderTransformOrigin = new Point(0.5, 0.5);

        var fadeOut = MakeAnim(1, 0, durFast, _easeQuadOut);
        var scaleOut = MakeAnim(1, 0.92, durTotal, _easeExpOut7);
        var slideOut = MakeAnim(0, 10, durTotal, _easeExpOut7);

        fadeOut.Completed += (s, e) => {
            SecondaryContent.Visibility = Visibility.Collapsed;
            SecondaryContent.RenderTransform = null;
            SecondaryContent.Effect = null; // Cleanup depth effect
            DisableKeyboardInput();
        };

        SecondaryContent.BeginAnimation(OpacityProperty, fadeOut);
        secondaryScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleOut);
        secondaryScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleOut);
        secondaryTranslate.BeginAnimation(TranslateTransform.YProperty, slideOut);

        // Add Depth: Blur outgoing content
        var blur = new BlurEffect { Radius = 0, KernelType = KernelType.Gaussian };
        SecondaryContent.Effect = blur;
        var blurAnim = MakeAnim(0, 15, durFast, _easeQuadOut);
        blur.BeginAnimation(BlurEffect.RadiusProperty, blurAnim);

        // PRIMARY (IN)
        ExpandedContent.Visibility = Visibility.Visible;
        ExpandedContent.Opacity = 0;

        var primaryGroup = new TransformGroup();
        var primaryScale = new ScaleTransform(0.92, 0.92);
        var primaryTranslate = new TranslateTransform(0, -10);
        primaryGroup.Children.Add(primaryScale);
        primaryGroup.Children.Add(primaryTranslate);
        ExpandedContent.RenderTransform = primaryGroup;
        ExpandedContent.RenderTransformOrigin = new Point(0.5, 0.5);

        var fadeIn = MakeAnim(0, 1, durTotal, _easePowerOut3);
        var springScale = MakeAnim(0.92, 1, durTotal, _easeMenuSpring);
        var springSlide = MakeAnim(-10, 0, durTotal, _easeExpOut7);

        fadeIn.Completed += (s, e) => {
            _isAnimating = false;
            NotchBorder.IsHitTestVisible = true;
            ExpandedContent.BeginAnimation(OpacityProperty, null);
            ExpandedContent.Opacity = 1;
            ExpandedContent.RenderTransform = null;
        };

        ExpandedContent.BeginAnimation(OpacityProperty, fadeIn);
        primaryScale.BeginAnimation(ScaleTransform.ScaleXProperty, springScale);
        primaryScale.BeginAnimation(ScaleTransform.ScaleYProperty, springScale);
        primaryTranslate.BeginAnimation(TranslateTransform.YProperty, springSlide);
    }

    #region File Shelf Logic

    private void FileShelf_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            FileShelf.Background = (SolidColorBrush)new BrushConverter().ConvertFrom("#2A2A2A")!;
            FileShelfDashedBorder.Stroke = (SolidColorBrush)new BrushConverter().ConvertFrom("#007AFF")!; // Highlight blue on hover
            FileShelfDashedBorder.StrokeDashArray = new DoubleCollection { 2, 1 }; // Faster dash on hover
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
            foreach (string file in files)
            {
                AddFileToShelf(file);
            }
        }
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
            // Apply selected visual if needed
            if (_selectedFiles.Contains(file))
            {
                var border = (Border)item;
                border.Background = (SolidColorBrush)new BrushConverter().ConvertFrom("#40007AFF")!;
                border.BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFrom("#007AFF")!;
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
            Tag = filePath, // Store path for identification
            Cursor = Cursors.Hand,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1)
        };

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        
        // Icon / Thumbnail
        var iconSource = GetFileIcon(filePath);
        var image = new Image
        {
            Source = iconSource,
            Width = iconSize,
            Height = iconSize,
            Margin = new Thickness(0, isSmall ? 2 : 6, 0, isSmall ? 2 : 4),
            Stretch = Stretch.UniformToFill, // Use UniformToFill for better shelf appearance
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 10, Opacity = 0.3, ShadowDepth = 2 },
            Clip = new RectangleGeometry 
            { 
                RadiusX = isSmall ? 4 : 6, 
                RadiusY = isSmall ? 4 : 6, 
                Rect = new Rect(0, 0, iconSize, iconSize) 
            }
        };
        
        stack.Children.Add(image);

        // Text (only if not small or based on preference, here we keep it but smaller or clipped)
        var text = new TextBlock
        {
            Text = fileName,
            FontSize = isSmall ? 8 : 9,
            Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom("#CCCCCC")!,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(isSmall ? 2 : 6, 0, isSmall ? 2 : 6, isSmall ? 2 : 6),
            FontFamily = (FontFamily)FindResource("SFProDisplay"),
            Visibility = isSmall ? Visibility.Collapsed : Visibility.Visible // Hide text for ultra small? Or keep? Let's hide to make it 2-row clean.
        };
        
        if (!isSmall) stack.Children.Add(text);

        border.Child = stack;

        border.MouseEnter += (s, e) => {
            if (!_selectedFiles.Contains(filePath))
            {
                border.Background = (SolidColorBrush)new BrushConverter().ConvertFrom("#303030")!;
                border.BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFrom("#555555")!;
            }
            if (!_isSweepSelecting)
                AnimateButtonScale((ScaleTransform)border.RenderTransform!, 1.05);
        };
        border.MouseLeave += (s, e) => {
            if (!_selectedFiles.Contains(filePath))
            {
                border.Background = (SolidColorBrush)new BrushConverter().ConvertFrom("#252525")!;
                border.BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFrom("#333333")!;
            }
            if (!_isSweepSelecting)
                AnimateButtonScale((ScaleTransform)border.RenderTransform!, 1.0);
        };

        // Item context menu to remove
        var menu = new ContextMenu();
        var remove = new MenuItem { Header = "Xóa khỏi kệ" };
        remove.Click += (s, e) => RemoveFileFromShelf(filePath, border);
        menu.Items.Add(remove);
        border.ContextMenu = menu;

        // Selection logic
        border.MouseLeftButtonDown += (s, e) => {
            _dragStartPoint = e.GetPosition(null);
            _selectionStart = e.GetPosition(FileShelfGrid); // Absolute start point on shelf
            _didDragOutFromShelf = false;
            _isSweepSelecting = false;
            _sweepStartFile = filePath;
            
            // Critical check: Was this file already selected?
            _wasSelectedOnMouseDown = _selectedFiles.Contains(filePath);
            
            border.CaptureMouse();
            e.Handled = true;
            
            // Initialization for lasso (even if we don't start it yet)
            SelectionRect.Width = 0;
            SelectionRect.Height = 0;
            
            bool isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            
            if (isCtrl)
            {
                // Ctrl+Click: toggle selection
                if (_selectedFiles.Contains(filePath)) _selectedFiles.Remove(filePath);
                else _selectedFiles.Add(filePath);
            }
            else
            {
                // If clicked an unselected file -> Select ONLY this one (standard behavior)
                // If it was ALREADY selected, we keep the selection to allow drag-drop of multiple files
                if (!_wasSelectedOnMouseDown)
                {
                    _selectedFiles.Clear();
                    _selectedFiles.Add(filePath);
                }
            }

            // Update visuals immediately
            foreach (var child in ShelfItemsContainer.Children)
            {
                if (child is Border b && b.Tag is string p)
                    UpdateShelfItemVisualState(b, _selectedFiles.Contains(p));
            }
        };

        border.MouseLeftButtonUp += (s, e) => {
            if (_didDragOutFromShelf || _isSweepSelecting || _isSelecting)
            {
                // Drag, Sweep or Lasso happened - reset state
                _isSelecting = false;
                _isSweepSelecting = false;
                _sweepStartFile = null;
                SelectionCanvas.Visibility = Visibility.Collapsed;

                if (border.IsMouseCaptured) border.ReleaseMouseCapture();
                
                // Reset scale on all items
                foreach (var child in ShelfItemsContainer.Children)
                {
                    if (child is Border b)
                        AnimateButtonScale((ScaleTransform)b.RenderTransform!, 1.0);
                }
                return;
            }
            
            _sweepStartFile = null;
            bool isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            
            // If click released without dragging on a multi-selected group
            // and NOT holding Ctrl -> Reduce selection to single clicked file
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

        border.MouseMove += (s, e) => {
            if (e.LeftButton == MouseButtonState.Pressed && !_isSelecting && !_isSweepSelecting)
            {
                Point currentPosGlobal = e.GetPosition(null);
                Vector diff = _dragStartPoint - currentPosGlobal;

                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    // RULE: If file was ALREADY selected on Down -> PRIORITIZE OS DRAG-DROP
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

                    // RULE: If file was NOT selected or dragged inside shelf -> START LASSO
                    Point posInGrid = e.GetPosition(FileShelfGrid);
                    bool isInsideShelf = posInGrid.X >= 0 && posInGrid.Y >= 0 &&
                                         posInGrid.X <= FileShelfGrid.ActualWidth &&
                                         posInGrid.Y <= FileShelfGrid.ActualHeight;

                    if (isInsideShelf)
                    {
                        _isSelecting = true;
                        _isSweepSelecting = true; // Flag we started from an item
                        SelectionCanvas.Visibility = Visibility.Visible;
                        
                        bool isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                        if (isCtrl)
                            _selectionInitialState = new HashSet<string>(_selectedFiles);
                        else
                            _selectionInitialState.Clear();
                    }
                    else
                    {
                        // Immediate drag out for unselected file moved quickly outside
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

        return border;
    }

    private HashSet<string> _selectionInitialState = new HashSet<string>();

    private void FileShelf_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_isAnimating) return;
        
        _dragStartPoint = e.GetPosition(null); // Screen coordinates
        _selectionStart = e.GetPosition(FileShelfGrid); // Absolute start point on shelf grid
        _isSelecting = false; // Don't start until threshold is crossed
        _isSweepSelecting = false; // Started from empty space
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
            
            // Clear visual state of all items immediately (performant)
            foreach (var child in ShelfItemsContainer.Children)
            {
                if (child is Border item) UpdateShelfItemVisualState(item, false);
            }
        }

        // Hide it for now, will show in MouseMove
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
                    // Reset start point to avoid small jump from threshold
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

        // Current coordinates relative to the Shelf Grid
        double startX = _selectionStart.X;
        double startY = _selectionStart.Y;
        double endX = Math.Max(0, Math.Min(gridWidth, currentPos.X));
        double endY = Math.Max(0, Math.Min(gridHeight, currentPos.Y));

        double x = Math.Min(startX, endX);
        double y = Math.Min(startY, endY);
        double width = Math.Abs(startX - endX);
        double height = Math.Abs(startY - endY);

        // Update the visual selection rectangle
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
                // Crucial: Use TransformToAncestor to get position relative to the STATIC grid
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
        // Reset sweep state
        if (_isSweepSelecting)
        {
            _isSweepSelecting = false;
            _sweepStartFile = null;
            // Reset scale on all items
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
        // Ctrl+A: Select all files in shelf (only when shelf is visible)
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
        // Delete: Remove selected files from shelf
        else if (e.Key == Key.Delete && _isSecondaryView && _selectedFiles.Count > 0)
        {
            var filesToRemove = _selectedFiles.ToList();
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
            e.Handled = true;
        }
    }

    private System.Windows.Threading.DispatcherTimer? _scrollbarFadeTimer;

    private void ShelfScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Convert vertical scroll to horizontal scroll
        if (sender is ScrollViewer sv)
        {
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
            e.Handled = true;

            // Show scrollbar on scroll activity
            ShowShelfScrollbar();
        }
    }

    private void ShowShelfScrollbar()
    {
        // Find the horizontal scrollbar and show it
        var scrollBar = FindVisualChild<System.Windows.Controls.Primitives.ScrollBar>(ShelfScrollViewer);
        if (scrollBar != null)
        {
            scrollBar.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
        }

        // Auto-hide after delay
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

    private void RemoveFileFromShelf(string filePath, Border item)
    {
        _shelfFiles.Remove(filePath);
        _selectedFiles.Remove(filePath);
        UnwatchShelfDirectory(filePath);
        RefreshShelfLayout();
        
        if (_shelfFiles.Count == 0)
        {
            ShelfPlaceholder.Visibility = Visibility.Visible;
        }
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
        catch { /* Directory may not exist or be inaccessible */ }
    }

    private void UnwatchShelfDirectory(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir)) return;

        // Only dispose watcher if no more shelf files in this directory
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
            if (!_shelfFiles.Contains(e.FullPath)) return;
            
            _shelfFiles.Remove(e.FullPath);
            _selectedFiles.Remove(e.FullPath);
            UnwatchShelfDirectory(e.FullPath);
            RefreshShelfLayout();
            
            if (_shelfFiles.Count == 0)
                ShelfPlaceholder.Visibility = Visibility.Visible;
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
            
            // Update watcher for potential new directory
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

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, PreserveSig = false)]
    public static extern void SHCreateItemFromParsingName(
        [In, MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        [In] IntPtr pbc,
        [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [Out, MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8746c1f01a3b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage([In, MarshalAs(UnmanagedType.Struct)] SIZE size, [In] int flags, [Out] out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int cx;
        public int cy;
    }

    private ImageSource? GetFileIcon(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                string ext = Path.GetExtension(filePath).ToLower();
                bool isImage = ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".bmp" || ext == ".gif";
                bool isVideo = ext == ".mp4" || ext == ".mkv" || ext == ".avi" || ext == ".mov" || ext == ".wmv" || ext == ".flv" || ext == ".webm";

                if (isImage || isVideo)
                {
                    // For Videos, WinRT Storage API is much more reliable for generating thumbnails
                    if (isVideo)
                    {
                        try
                        {
                            var thumbnailTask = Task.Run(async () =>
                            {
                                var file = await StorageFile.GetFileFromPathAsync(filePath);
                                var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 128);
                                if (thumbnail != null)
                                {
                                    var bitmap = new BitmapImage();
                                    bitmap.BeginInit();
                                    bitmap.StreamSource = thumbnail.AsStream();
                                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                                    bitmap.EndInit();
                                    bitmap.Freeze();
                                    return bitmap;
                                }
                                return null;
                            });
                            
                            var result = thumbnailTask.Result;
                            if (result != null) return result;
                        }
                        catch { /* Fallback to Shell API */ }
                    }

                    // Shell API Fallback / Image handling
                    try
                    {
                        Guid guid = new Guid("bcc18b79-ba16-442f-80c4-8746c1f01a3b");
                        SHCreateItemFromParsingName(filePath, IntPtr.Zero, guid, out IShellItemImageFactory factory);
                        
                        // SIIGBF_BIGGERSIZEOK (0x01) | SIIGBF_RESIZETOFIT (0x00)
                        // Removed THUMBNAILONLY (0x02) to allow on-the-fly generation
                        int hr = factory.GetImage(new SIZE { cx = 128, cy = 128 }, 0x01, out IntPtr hBitmap);
                        
                        if (hr == 0 && hBitmap != IntPtr.Zero)
                        {
                            try
                            {
                                var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                                    hBitmap,
                                    IntPtr.Zero,
                                    Int32Rect.Empty,
                                    BitmapSizeOptions.FromEmptyOptions());
                                source.Freeze();
                                return source;
                            }
                            finally
                            {
                                DeleteObject(hBitmap);
                            }
                        }
                    }
                    catch { }
                    
                    if (isImage)
                    {
                        try
                        {
                            var bitmap = new BitmapImage();
                            bitmap.BeginInit();
                            bitmap.UriSource = new Uri(filePath);
                            bitmap.DecodePixelWidth = 128;
                            bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            bitmap.EndInit();
                            bitmap.Freeze();
                            return bitmap;
                        }
                        catch { }
                    }
                }

                // Fallback for everything else: specific file icon
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(filePath);
                if (icon != null)
                {
                    using var bitmap = icon.ToBitmap();
                    var hBitmap = bitmap.GetHbitmap();
                    try
                    {
                        var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                            hBitmap,
                            IntPtr.Zero,
                            Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                        source.Freeze();
                        return source;
                    }
                    finally
                    {
                        DeleteObject(hBitmap);
                    }
                }
            }
        }
        catch { }
        
        return null; 
    }

    #endregion

    #region Camera Logic

    private bool _isCameraActive = false;
    private MediaCapture? _mediaCapture;
    private MediaFrameReader? _frameReader;

    private void CameraSection_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // Prevent event bubbling to NotchBorder which closes the notch
        
        if (!_isCameraActive)
        {
            StartCameraPreview();
        }
        else
        {
            StopCameraPreview();
        }
    }

    private void CameraSection_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Border border)
        {
            border.Clip = new RectangleGeometry
            {
                RadiusX = 16,
                RadiusY = 16,
                Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height)
            };
        }
    }

    private async void StartCameraPreview()
    {
        if (_isCameraActive && _frameReader != null) return;
        
        try 
        {
            _isCameraActive = true;
            CameraOverlay.Visibility = Visibility.Collapsed;
            CameraLiveIndicator.Visibility = Visibility.Visible;
            
            _mediaCapture = new MediaCapture();
            
            var frameSourceGroups = await MediaFrameSourceGroup.FindAllAsync();
            var selectedGroup = frameSourceGroups.FirstOrDefault(g => g.SourceInfos.Any(s => s.SourceKind == MediaFrameSourceKind.Color));
            
            if (selectedGroup == null)
            {
                throw new Exception("Không tìm thấy camera khả dụng.");
            }

            var settings = new MediaCaptureInitializationSettings
            {
                SourceGroup = selectedGroup,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                StreamingCaptureMode = StreamingCaptureMode.Video
            };

            await _mediaCapture.InitializeAsync(settings);

            var colorSource = _mediaCapture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
            if (colorSource == null)
            {
                throw new Exception("Không tìm thấy nguồn video màu.");
            }

            _frameReader = await _mediaCapture.CreateFrameReaderAsync(colorSource, MediaEncodingSubtypes.Bgra8);
            _frameReader.FrameArrived += FrameReader_FrameArrived;
            await _frameReader.StartAsync();
        }
        catch (Exception ex)
        {
            StopCameraPreview();
            Console.WriteLine("Camera error: " + ex.Message);
        }
    }

    private void FrameReader_FrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        using var frame = sender.TryAcquireLatestFrame();
        if (frame?.VideoMediaFrame?.SoftwareBitmap == null) return;

        var softwareBitmap = frame.VideoMediaFrame.SoftwareBitmap;
        
        if (softwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || 
            softwareBitmap.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
        {
            softwareBitmap = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_isCameraActive) return;
            try
            {
                var width = softwareBitmap.PixelWidth;
                var height = softwareBitmap.PixelHeight;

                if (CameraPreviewImage.Source is not WriteableBitmap wbmp || wbmp.PixelWidth != width || wbmp.PixelHeight != height)
                {
                    wbmp = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                    CameraPreviewImage.Source = wbmp;
                }

                // Copy pixels to buffer then to WriteableBitmap
                var buffer = new byte[width * height * 4];
                softwareBitmap.CopyToBuffer(buffer.AsBuffer());
                wbmp.WritePixels(new Int32Rect(0, 0, width, height), buffer, width * 4, 0);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Frame update error: " + ex.Message);
            }
            finally
            {
                softwareBitmap.Dispose();
            }
        }));
    }

    private async void StopCameraPreview()
    {
        _isCameraActive = false;
        CameraOverlay.Visibility = Visibility.Visible;
        CameraLiveIndicator.Visibility = Visibility.Collapsed;
        
        if (_frameReader != null)
        {
            _frameReader.FrameArrived -= FrameReader_FrameArrived;
            await _frameReader.StopAsync();
            _frameReader.Dispose();
            _frameReader = null;
        }

        if (_mediaCapture != null)
        {
            _mediaCapture.Dispose();
            _mediaCapture = null;
        }

        Dispatcher.Invoke(() => {
            CameraPreviewImage.Source = null;
        });
    }

    #endregion
}
