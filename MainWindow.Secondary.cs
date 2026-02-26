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

    private static readonly SolidColorBrush _brushShelfItemBg = CreateFrozenBrush(37, 37, 37);
    private static readonly SolidColorBrush _brushShelfItemBorder = CreateFrozenBrush(51, 51, 51);
    private static readonly SolidColorBrush _brushShelfSelectedBg     = CreateFrozenBrush(255, 255, 255, 40);  // white ~16%
    private static readonly SolidColorBrush _brushShelfSelectedBorder = CreateFrozenBrush(255, 255, 255, 140); // white ~55%

    private Point _selectionStart;
    private bool _isSelecting = false;
    private Point _dragStartPoint;
    private bool _didDragOutFromShelf = false;
    private bool _isSweepSelecting = false;
    private string? _sweepStartFile = null;
    private bool _wasSelectedOnMouseDown = false;

    private readonly Dictionary<string, FileSystemWatcher> _shelfWatchers = new();

    private void NotchWrapper_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_isExpanded || _isAnimating) return;

        if (e.Delta < 0) 
        {
            if (!_isSecondaryView)
            {
                SwitchToSecondaryView();
            }
        }
        else if (e.Delta > 0) 
        {
            if (_isSecondaryView)
            {
                SwitchToPrimaryView();
            }
        }
    }

    private void SecondaryContent_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; 
    }

    private void SwitchToSecondaryView()
    {
        if (_isSecondaryView || _isAnimating) return;
        _isSecondaryView = true;
        _isAnimating = true;

        NotchBorder.IsHitTestVisible = false;

        var durOut  = new Duration(TimeSpan.FromMilliseconds(180));
        var durIn   = new Duration(TimeSpan.FromMilliseconds(480));
        var inDelay = TimeSpan.FromMilliseconds(50);
        const int fps = 144;

        // ── PRIMARY EXIT: slide up + scale down + fade out ──
        var primaryGroup = new TransformGroup();
        var primaryScale = new ScaleTransform(1, 1);
        var primaryTranslate = new TranslateTransform(0, 0);
        primaryGroup.Children.Add(primaryScale);
        primaryGroup.Children.Add(primaryTranslate);
        ExpandedContent.RenderTransform = primaryGroup;
        ExpandedContent.RenderTransformOrigin = new Point(0.5, 0.5);

        var fadeOut    = MakeAnim(1, 0,    durOut, _easeQuadIn);
        var slideUp    = MakeAnim(0, -16,  durOut, _easeQuadIn);
        var scaleDownX = MakeAnim(1, 0.93, durOut, _easeQuadIn);
        var scaleDownY = MakeAnim(1, 0.93, durOut, _easeQuadIn);
        Timeline.SetDesiredFrameRate(slideUp,    fps);
        Timeline.SetDesiredFrameRate(scaleDownX, fps);
        Timeline.SetDesiredFrameRate(scaleDownY, fps);

        // Blur-out to create depth illusion
        var expandedBlur = ExpandedContent.Effect as BlurEffect ?? new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
        ExpandedContent.Effect = expandedBlur;
        var blurOutAnim = MakeAnim(0, 10, durOut, _easeQuadIn);

        fadeOut.Completed += (s, e) =>
        {
            ExpandedContent.Visibility = Visibility.Collapsed;
            ExpandedContent.RenderTransform = null;
            ExpandedContent.Effect = null;
            expandedBlur.Radius = 0;
        };

        ExpandedContent.BeginAnimation(OpacityProperty, fadeOut);
        primaryTranslate.BeginAnimation(TranslateTransform.YProperty, slideUp);
        primaryScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDownX);
        primaryScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDownY);
        expandedBlur.BeginAnimation(BlurEffect.RadiusProperty, blurOutAnim);

        // ── SECONDARY ENTER: bouncy spring from below + fade in (144fps) ──
        SecondaryContent.Visibility = Visibility.Visible;
        SecondaryContent.Opacity = 0;
        EnableKeyboardInput();

        var secondaryGroup     = new TransformGroup();
        var secondaryScale     = new ScaleTransform(0.93, 0.93);
        var secondaryTranslate = new TranslateTransform(0, 26);
        secondaryGroup.Children.Add(secondaryScale);
        secondaryGroup.Children.Add(secondaryTranslate);
        SecondaryContent.RenderTransform = secondaryGroup;
        SecondaryContent.RenderTransformOrigin = new Point(0.5, 0.5);

        var fadeIn       = MakeAnim(0, 1,    durIn, _easeExpOut6,   inDelay);
        var springSlide  = MakeAnim(26, 0,   durIn, _easeExpOut7,   inDelay);
        var springScaleX = MakeAnim(0.93, 1, durIn, _easeSoftSpring, inDelay);
        var springScaleY = MakeAnim(0.93, 1, durIn, _easeSoftSpring, inDelay);
        Timeline.SetDesiredFrameRate(fadeIn,       fps);
        Timeline.SetDesiredFrameRate(springSlide,  fps);
        Timeline.SetDesiredFrameRate(springScaleX, fps);
        Timeline.SetDesiredFrameRate(springScaleY, fps);

        UpdatePaginationDots();

        fadeIn.Completed += (s, e) =>
        {
            _isAnimating = false;
            NotchBorder.IsHitTestVisible = true;
            SecondaryContent.Opacity = 1;
            SecondaryContent.BeginAnimation(OpacityProperty, null);
            SecondaryContent.RenderTransform = null;
        };

        SecondaryContent.BeginAnimation(OpacityProperty, fadeIn);
        secondaryTranslate.BeginAnimation(TranslateTransform.YProperty, springSlide);
        secondaryScale.BeginAnimation(ScaleTransform.ScaleXProperty, springScaleX);
        secondaryScale.BeginAnimation(ScaleTransform.ScaleYProperty, springScaleY);
    }

    private void SwitchToPrimaryView()
    {
        if (!_isSecondaryView || _isAnimating) return;
        _isSecondaryView = false;
        _isAnimating = true;

        NotchBorder.IsHitTestVisible = false;

        var durOut  = new Duration(TimeSpan.FromMilliseconds(180));
        var durIn   = new Duration(TimeSpan.FromMilliseconds(480));
        var inDelay = TimeSpan.FromMilliseconds(50);
        const int fps = 144;

        // ── SECONDARY EXIT: slide down + scale down + fade out ──
        var secondaryGroup     = new TransformGroup();
        var secondaryScale     = new ScaleTransform(1, 1);
        var secondaryTranslate = new TranslateTransform(0, 0);
        secondaryGroup.Children.Add(secondaryScale);
        secondaryGroup.Children.Add(secondaryTranslate);
        SecondaryContent.RenderTransform = secondaryGroup;
        SecondaryContent.RenderTransformOrigin = new Point(0.5, 0.5);

        var fadeOut    = MakeAnim(1, 0,    durOut, _easeQuadIn);
        var slideDown  = MakeAnim(0, 16,   durOut, _easeQuadIn);
        var scaleDownX = MakeAnim(1, 0.93, durOut, _easeQuadIn);
        var scaleDownY = MakeAnim(1, 0.93, durOut, _easeQuadIn);
        Timeline.SetDesiredFrameRate(slideDown,  fps);
        Timeline.SetDesiredFrameRate(scaleDownX, fps);
        Timeline.SetDesiredFrameRate(scaleDownY, fps);

        // Blur-out shelf content for depth
        var secondaryBlur = SecondaryContent.Effect as BlurEffect ?? new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
        SecondaryContent.Effect = secondaryBlur;
        var blurOutAnim = MakeAnim(0, 10, durOut, _easeQuadIn);

        fadeOut.Completed += (s, e) =>
        {
            SecondaryContent.Visibility = Visibility.Collapsed;
            SecondaryContent.RenderTransform = null;
            SecondaryContent.Effect = null;
            secondaryBlur.Radius = 0;
            DisableKeyboardInput();
        };

        SecondaryContent.BeginAnimation(OpacityProperty, fadeOut);
        secondaryTranslate.BeginAnimation(TranslateTransform.YProperty, slideDown);
        secondaryScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDownX);
        secondaryScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDownY);
        secondaryBlur.BeginAnimation(BlurEffect.RadiusProperty, blurOutAnim);

        // ── PRIMARY ENTER: bouncy spring from above + fade in (144fps) ──
        ExpandedContent.Visibility = Visibility.Visible;
        ExpandedContent.Opacity = 0;
        ExpandedContent.Effect = null;

        var primaryGroup     = new TransformGroup();
        var primaryScale     = new ScaleTransform(0.93, 0.93);
        var primaryTranslate = new TranslateTransform(0, -26);
        primaryGroup.Children.Add(primaryScale);
        primaryGroup.Children.Add(primaryTranslate);
        ExpandedContent.RenderTransform = primaryGroup;
        ExpandedContent.RenderTransformOrigin = new Point(0.5, 0.5);

        var fadeIn       = MakeAnim(0, 1,     durIn, _easeExpOut6,    inDelay);
        var springSlide  = MakeAnim(-26, 0,   durIn, _easeExpOut7,    inDelay);
        var springScaleX = MakeAnim(0.93, 1,  durIn, _easeSoftSpring, inDelay);
        var springScaleY = MakeAnim(0.93, 1,  durIn, _easeSoftSpring, inDelay);
        Timeline.SetDesiredFrameRate(fadeIn,       fps);
        Timeline.SetDesiredFrameRate(springSlide,  fps);
        Timeline.SetDesiredFrameRate(springScaleX, fps);
        Timeline.SetDesiredFrameRate(springScaleY, fps);

        UpdatePaginationDots();

        fadeIn.Completed += (s, e) =>
        {
            _isAnimating = false;
            NotchBorder.IsHitTestVisible = true;
            ExpandedContent.Opacity = 1;
            ExpandedContent.BeginAnimation(OpacityProperty, null);
            ExpandedContent.RenderTransform = null;
        };

        ExpandedContent.BeginAnimation(OpacityProperty, fadeIn);
        primaryTranslate.BeginAnimation(TranslateTransform.YProperty, springSlide);
        primaryScale.BeginAnimation(ScaleTransform.ScaleXProperty, springScaleX);
        primaryScale.BeginAnimation(ScaleTransform.ScaleYProperty, springScaleY);
    }

    #region Drag-to-Open

    // True khi notch được mở tự động do drag (để biết cần đóng lại nếu drag rời đi)
    private bool _dragAutoExpanded = false;

    private System.Windows.Threading.DispatcherTimer? _dragWaitTimer;
    private System.Windows.Threading.DispatcherTimer? _dragCollapseTimer;

    private void NotchWrapper_DragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;

        // Huỷ bất kỳ pending collapse nào
        _dragCollapseTimer?.Stop();

        if (!_isExpanded && !_isAnimating)
        {
            // Notch đang thu gọn → mở ra và sau đó switch sang shelf
            _dragAutoExpanded = true;
            ExpandNotch();
            StartDragWaitForShelf();
        }
        else if (_isExpanded && _isAnimating)
        {
            // Đang animate expand do drag trước đó hay click → chờ xong rồi switch
            StartDragWaitForShelf();
        }
        else if (_isExpanded && !_isSecondaryView && !_isAnimating)
        {
            // Đã mở nhưng chưa ở shelf → switch luôn
            SwitchToSecondaryView();
        }
        // Nếu đã ở secondary view rồi → không cần làm gì
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

        // Debounce: DragLeave bắn nhiều lần khi chuột đi qua child elements
        // Chờ 150ms, nếu DragEnter không bắn lại thì thực sự là đã rời
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
        // User đã drop → giữ nguyên trạng thái mở, xóa cờ auto-expand
        _dragWaitTimer?.Stop();
        _dragCollapseTimer?.Stop();
        _dragAutoExpanded = false;
    }

    /// <summary>Dùng timer poll 40ms để chờ expand hoàn tất rồi switch sang shelf.</summary>
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

    /// <summary>Khi drag rời notch mà không drop: switch lại Primary rồi collapse.</summary>
    private void AutoCollapseAfterDrag()
    {
        _dragAutoExpanded = false;
        _dragWaitTimer?.Stop();

        if (!_isExpanded) return;

        if (_isSecondaryView && !_isAnimating)
        {
            // Switch về primary trước, sau đó collapse
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
            // Lọc file chưa có trong shelf
            var newFiles = files.Where(f => !_shelfFiles.Contains(f)).ToArray();
            if (newFiles.Length > 0)
                AddFilesToShelfSequential(newFiles);
        }
    }

    // Queue tuần tự để tránh lag spike khi drop nhiều file
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

            // Rebuild toàn bộ để đảm bảo layout đúng (an toàn, không bỏ sót file nào)
            RefreshShelfLayout();

            // Animate pop-in cho item CUỐI CÙNG vừa được thêm
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
                        // KHÔNG được set null vì MouseEnter/Leave cần ScaleTransform để animate
                        lastItem.RenderTransform = new ScaleTransform(1, 1);
                    };

                    lastItem.BeginAnimation(OpacityProperty, fadeIn);
                    st.BeginAnimation(ScaleTransform.ScaleXProperty, scaleIn);
                    st.BeginAnimation(ScaleTransform.ScaleYProperty, scaleIn);

                    // Thêm progress bar vào item
                    AnimateShelfItemProgress(lastItem);
                }
            }
        }

        // Delay 90ms trước khi xử lý file tiếp theo
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

    /// <summary>Overlay mini progress bar lên shelf item, tự fade out sau khi đầy.</summary>
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
                    // Restore original structure để tránh nested overlay tích lũy
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
        var remove = new MenuItem { Header = "Xóa khỏi kệ" };
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
            // Sử dụng TransformGroup để kết hợp Scale và Translate
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

            // Đặt fps cao cho mượt
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
        catch {  }
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
                        catch {  }
                    }

                    try
                    {
                        Guid guid = new Guid("bcc18b79-ba16-442f-80c4-8746c1f01a3b");
                        SHCreateItemFromParsingName(filePath, IntPtr.Zero, guid, out IShellItemImageFactory factory);

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
        e.Handled = true; 

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
            CameraErrorOverlay.Visibility = Visibility.Collapsed;
            CameraLiveIndicator.Visibility = Visibility.Visible;

            _mediaCapture = new MediaCapture();

            var frameSourceGroups = await MediaFrameSourceGroup.FindAllAsync();
            var selectedGroup = frameSourceGroups.FirstOrDefault(g => g.SourceInfos.Any(s => s.SourceKind == MediaFrameSourceKind.Color));

            if (selectedGroup == null)
            {
                throw new Exception("Cannot detect camera device");
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
                throw new Exception("Cannot detect color source");
            }

            _frameReader = await _mediaCapture.CreateFrameReaderAsync(colorSource, MediaEncodingSubtypes.Bgra8);
            _frameReader.FrameArrived += FrameReader_FrameArrived;
            await _frameReader.StartAsync();
        }
        catch (Exception ex)
        {
            StopCameraPreview();
            CameraOverlay.Visibility = Visibility.Collapsed;
            CameraErrorOverlay.Visibility = Visibility.Visible;
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
        CameraErrorOverlay.Visibility = Visibility.Collapsed;
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

        Dispatcher.Invoke(() =>
        {
            CameraPreviewImage.Source = null;
        });
    }

    #endregion
}