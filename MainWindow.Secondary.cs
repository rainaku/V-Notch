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
    private const int DefaultShelfFileLimit = 30;
    private const int UnlockedShelfFileLimit = 999;
    private int MaxShelfFiles => _settings.IsShelfUploadLimitUnlocked ? UnlockedShelfFileLimit : DefaultShelfFileLimit;

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
    private readonly HashSet<string> _pendingShelfFileSet = new(StringComparer.OrdinalIgnoreCase);
    private bool _isCameraSectionExpanded = false;
    private int _cameraSectionAnimToken = 0;
    private double _cameraSectionCompactWidth = 0;
    private Thickness _cameraSectionCompactMargin = new Thickness(0, 0, 8, 0);


    #region File Shelf Logic

    private static readonly SolidColorBrush _brushShelfNormalBg = CreateFrozenBrush(26, 26, 26);
    private static readonly SolidColorBrush _brushShelfActiveBg = CreateFrozenBrush(42, 42, 42);
    private static readonly SolidColorBrush _brushShelfErrorBg = CreateFrozenBrush(56, 24, 24);
    private static readonly SolidColorBrush _brushShelfNormalBorder = CreateFrozenBrush(51, 51, 51);
    private static readonly SolidColorBrush _brushShelfActiveBorder = CreateFrozenBrush(153, 255, 255, 255);
    private static readonly SolidColorBrush _brushShelfErrorBorder = CreateFrozenBrush(255, 107, 107);
    private static readonly SolidColorBrush _brushShelfStatusWarning = CreateFrozenBrush(255, 196, 120);
    private static readonly SolidColorBrush _brushShelfStatusError = CreateFrozenBrush(255, 107, 107);

    private int PendingShelfFileCount => _pendingShelfFileSet.Count;
    private int ShelfOccupiedSlots => _shelfFiles.Count + PendingShelfFileCount;
    private int ShelfRemainingSlots => Math.Max(0, MaxShelfFiles - ShelfOccupiedSlots);
    private bool IsShelfFull => ShelfOccupiedSlots >= MaxShelfFiles;

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

    private static readonly SolidColorBrush _brushShelfUnlockHintBg = CreateFrozenBrush(42, 36, 20);
    private static readonly SolidColorBrush _brushShelfUnlockHintBorder = CreateFrozenBrush(255, 196, 120);

    private void SetShelfDropUnlockHintVisualState()
    {
        FileShelf.Background = _brushShelfUnlockHintBg;
        FileShelfDashedBorder.Stroke = _brushShelfUnlockHintBorder;
        FileShelfDashedBorder.StrokeDashArray = new DoubleCollection { 2, 1 };
        UpdateShelfCapacityIndicator("Drop to unlock upload limit", isError: false);
    }

    private void UpdateShelfCapacityIndicator(string? transientMessage = null, bool isError = false)
    {
        if (ShelfStatusText == null || ShelfPlaceholder == null || ShelfCountText == null)
            return;

        const string defaultPlaceholderText = "Drag files here for temporary storage";
        int currentCount = _shelfFiles.Count + PendingShelfFileCount;

        ShelfPlaceholder.Text = defaultPlaceholderText;
        ShelfPlaceholder.Visibility = _shelfFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_settings.IsShelfUploadLimitUnlocked)
        {
            ShelfCountText.Text = $"{currentCount}";
            ShelfCountText.Foreground = Brushes.White;
        }
        else
        {
            int displayCount = Math.Min(MaxShelfFiles, ShelfOccupiedSlots);
            ShelfCountText.Text = $"{displayCount}/{DefaultShelfFileLimit}";
            ShelfCountText.Foreground = IsShelfFull ? _brushShelfStatusWarning : Brushes.White;
        }

        if (!string.IsNullOrWhiteSpace(transientMessage))
        {
            ShelfStatusText.Text = transientMessage;
            ShelfStatusText.Foreground = isError ? _brushShelfStatusError : _brushShelfStatusWarning;
            ShelfStatusText.Visibility = Visibility.Visible;
            return;
        }

        if (IsShelfFull && !_settings.IsShelfUploadLimitUnlocked)
        {
            ShelfStatusText.Text = $"Shelf full ({currentCount}/{DefaultShelfFileLimit}). Remove files before adding more.";
            ShelfStatusText.Foreground = _brushShelfStatusWarning;
            ShelfStatusText.Visibility = Visibility.Visible;
            return;
        }

        ShelfStatusText.Visibility = Visibility.Collapsed;
    }

    private bool TryGetShelfDropFiles(DragEventArgs e, out string[] newFiles, out string rejectionMessage)
    {
        newFiles = Array.Empty<string>();
        rejectionMessage = string.Empty;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return false;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            rejectionMessage = "No files detected.";
            return false;
        }

        newFiles = files
            .Where(static f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f => !_shelfFiles.Contains(f, StringComparer.OrdinalIgnoreCase))
            .Where(f => !_pendingShelfFileSet.Contains(f))
            .ToArray();

        if (newFiles.Length == 0)
        {
            rejectionMessage = "These files are already on the shelf.";
            return false;
        }

        // When limit is unlocked, allow any number of files
        if (_settings.IsShelfUploadLimitUnlocked)
        {
            return true;
        }

        // Limit is locked: check if total would exceed the default limit
        int totalAfterDrop = ShelfOccupiedSlots + newFiles.Length;
        if (totalAfterDrop > DefaultShelfFileLimit)
        {
            rejectionMessage = $"UNLOCK_PROMPT:{newFiles.Length}";
            return false;
        }

        if (ShelfRemainingSlots <= 0)
        {
            rejectionMessage = $"Shelf full ({Math.Min(MaxShelfFiles, ShelfOccupiedSlots)}/{MaxShelfFiles}). Remove files before adding more.";
            return false;
        }

        if (newFiles.Length > ShelfRemainingSlots)
        {
            rejectionMessage = $"Shelf limit is {MaxShelfFiles} files. Only {ShelfRemainingSlots} slot(s) left.";
            return false;
        }

        return true;
    }

    private void FileShelf_DragEnter(object sender, DragEventArgs e)
    {
        if (TryGetShelfDropFiles(e, out _, out var rejectionMessage))
        {
            e.Effects = DragDropEffects.Copy;
            SetShelfDropAcceptVisualState();
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            if (rejectionMessage.StartsWith("UNLOCK_PROMPT:"))
            {
                // Show a warning-style visual (not error) to hint that unlock is available
                e.Effects = DragDropEffects.Copy;
                SetShelfDropUnlockHintVisualState();
            }
            else
            {
                e.Effects = DragDropEffects.None;
                SetShelfDropRejectVisualState(rejectionMessage);
            }
        }

        e.Handled = true;
    }

    private void FileShelf_DragOver(object sender, DragEventArgs e)
    {
        if (TryGetShelfDropFiles(e, out _, out var rejectionMessage))
        {
            e.Effects = DragDropEffects.Copy;
            SetShelfDropAcceptVisualState();
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            if (rejectionMessage.StartsWith("UNLOCK_PROMPT:"))
            {
                e.Effects = DragDropEffects.Copy;
                SetShelfDropUnlockHintVisualState();
            }
            else
            {
                e.Effects = DragDropEffects.None;
                SetShelfDropRejectVisualState(rejectionMessage);
            }
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

        if (TryGetShelfDropFiles(e, out var newFiles, out var rejectionMessage))
        {
            AddFilesToShelfSequential(newFiles);
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            if (rejectionMessage.StartsWith("UNLOCK_PROMPT:"))
            {
                var countStr = rejectionMessage.Substring("UNLOCK_PROMPT:".Length);
                int fileCount = int.TryParse(countStr, out var c) ? c : 0;
                ShowShelfUnlockBanner(fileCount, e);
            }
            else
            {
                SetShelfDropRejectVisualState(rejectionMessage);
            }
        }

        e.Handled = true;
    }

    private string[]? _pendingUnlockFiles = null;

    private void ShowShelfUnlockBanner(int fileCount, DragEventArgs e)
    {
        // Store the files for later if user unlocks
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            _pendingUnlockFiles = files
                .Where(static f => !string.IsNullOrWhiteSpace(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(f => !_shelfFiles.Contains(f, StringComparer.OrdinalIgnoreCase))
                .Where(f => !_pendingShelfFileSet.Contains(f))
                .ToArray();
        }

        ShelfUnlockBanner.Visibility = Visibility.Visible;
        ShelfCountText.Visibility = Visibility.Collapsed;
        ShelfPlaceholder.Visibility = Visibility.Collapsed;
        ShelfUnlockMessageText.Text = $"You're uploading {fileCount} files. The safe limit is {DefaultShelfFileLimit} files.\nIf you'd like to upload more, you can unlock the upload limit right here.";
        ShelfUnlockSettingsHint.Visibility = Visibility.Visible;

        // Animate in
        ShelfUnlockBanner.Opacity = 0;
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        ShelfUnlockBanner.BeginAnimation(OpacityProperty, fadeIn);
    }

    private void ShelfUnlockButton_Click(object sender, RoutedEventArgs e)
    {
        // Unlock the limit permanently
        _settings.IsShelfUploadLimitUnlocked = true;
        _settingsService.Save(_settings);

        // Hide the banner
        HideShelfUnlockBanner();

        // Process the pending files
        if (_pendingUnlockFiles != null && _pendingUnlockFiles.Length > 0)
        {
            AddFilesToShelfSequential(_pendingUnlockFiles);
            _pendingUnlockFiles = null;
        }

        UpdateShelfCapacityIndicator();
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

    
    private readonly Queue<string> _pendingShelfFiles = new Queue<string>();
    private bool _isAddingSequential = false;

    private void AddFilesToShelfSequential(string[] filePaths)
    {
        foreach (var f in filePaths)
        {
            if (IsShelfFull)
                break;

            if (_shelfFiles.Contains(f, StringComparer.OrdinalIgnoreCase) || _pendingShelfFileSet.Contains(f))
                continue;

            _pendingShelfFiles.Enqueue(f);
            _pendingShelfFileSet.Add(f);
        }

        UpdateShelfCapacityIndicator();

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
        _pendingShelfFileSet.Remove(filePath);

        if (!IsShelfFull && !_shelfFiles.Contains(filePath, StringComparer.OrdinalIgnoreCase))
        {
            _shelfFiles.Add(filePath);
            WatchShelfDirectory(filePath);
            UpdateShelfCapacityIndicator();

            
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
        if (IsShelfFull || _shelfFiles.Contains(filePath, StringComparer.OrdinalIgnoreCase)) return;
        _shelfFiles.Add(filePath);
        WatchShelfDirectory(filePath);
        UpdateShelfCapacityIndicator();
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
                UpdateShelfCapacityIndicator();
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
            UpdateShelfCapacityIndicator();
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
                UpdateShelfCapacityIndicator();
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
            UpdateShelfCapacityIndicator();
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
