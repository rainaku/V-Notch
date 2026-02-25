using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Media.Effects;

namespace VNotch;

public partial class MainWindow
{
    #region Cached Easing Functions (Frozen - Thread Safe)

    private static readonly ExponentialEase _easeExpOut7 = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 7 };
    private static readonly ExponentialEase _easeExpOut6 = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };
    private static readonly QuadraticEase _easeQuadOut = new QuadraticEase { EasingMode = EasingMode.EaseOut };
    private static readonly QuadraticEase _easeQuadIn = new QuadraticEase { EasingMode = EasingMode.EaseIn };
    private static readonly QuadraticEase _easeQuadInOut = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
    private static readonly PowerEase _easePowerIn2 = new PowerEase { EasingMode = EasingMode.EaseIn, Power = 2 };
    private static readonly PowerEase _easePowerOut3 = new PowerEase { EasingMode = EasingMode.EaseOut, Power = 3 };
    private static readonly ElasticEase _easeSpring = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 8 };
    private static readonly ElasticEase _easeSoftSpring = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 3 };
    private static readonly ElasticEase _easeMenuSpring = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 4 };
    private static readonly ElasticEase _easeThumbSpring = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 6.5 };
    private static readonly SineEase _easeSineInOut = new SineEase { EasingMode = EasingMode.EaseInOut };

    static MainWindow()
    {
        _easeExpOut7.Freeze();
        _easeExpOut6.Freeze();
        _easeQuadOut.Freeze();
        _easeQuadIn.Freeze();
        _easeQuadInOut.Freeze();
        _easePowerIn2.Freeze();
        _easePowerOut3.Freeze();
        _easeSpring.Freeze();
        _easeSoftSpring.Freeze();
        _easeMenuSpring.Freeze();
        _easeThumbSpring.Freeze();
        _easeSineInOut.Freeze();
    }

    #endregion

    #region Cached Durations

    private static readonly Duration _dur600 = new(TimeSpan.FromMilliseconds(600));
    private static readonly Duration _dur500 = new(TimeSpan.FromMilliseconds(500));
    private static readonly Duration _dur450 = new(TimeSpan.FromMilliseconds(450));
    private static readonly Duration _dur400 = new(TimeSpan.FromMilliseconds(400));
    private static readonly Duration _dur350 = new(TimeSpan.FromMilliseconds(350));
    private static readonly Duration _dur250 = new(TimeSpan.FromMilliseconds(250));
    private static readonly Duration _dur200 = new(TimeSpan.FromMilliseconds(200));
    private static readonly Duration _dur150 = new(TimeSpan.FromMilliseconds(150));
    private static readonly Duration _dur100 = new(TimeSpan.FromMilliseconds(100));
    private static readonly Duration _dur80 = new(TimeSpan.FromMilliseconds(80));

    #endregion

    #region Animation Pool Helpers

    private static DoubleAnimation MakeAnim(double? from, double to, Duration duration, IEasingFunction? easing = null, int fps = 120)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(anim, fps);
        return anim;
    }

    private static DoubleAnimation MakeAnim(double to, Duration duration, IEasingFunction? easing = null, int fps = 120)
    {
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = duration,
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(anim, fps);
        return anim;
    }

    private static DoubleAnimation MakeAnim(double from, double to, Duration duration, IEasingFunction? easing, TimeSpan? beginTime)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EasingFunction = easing
        };

        if (beginTime.HasValue)
            anim.BeginTime = beginTime.Value;
        Timeline.SetDesiredFrameRate(anim, 120);
        return anim;
    }

    #endregion

    #region Notch Expand/Collapse

    private (double X, double Y) ComputeThumbnailExpandTarget()
    {
        try
        {

            var thumbPos = ThumbnailBorder.TransformToAncestor(InnerClipBorder).Transform(new Point(0, 0));

            double targetX = thumbPos.X - 8;
            double targetY = thumbPos.Y - 4;
            return (targetX, targetY);
        }
        catch
        {

            return (20, 48);
        }
    }

    private (double X, double Y)? _cachedThumbnailExpandTarget;

    private DoubleAnimation? _cachedThumbWidthExpand;
    private DoubleAnimation? _cachedThumbHeightExpand;
    private RectAnimation? _cachedThumbRectExpand;

    private DoubleAnimation? _cachedThumbWidthCollapse;
    private DoubleAnimation? _cachedThumbHeightCollapse;
    private RectAnimation? _cachedThumbRectCollapse;

    private void ExpandNotch()
    {
        if (_isAnimating || _isExpanded) return;
        _isAnimating = true;

        UpdateZOrderTimerInterval();
        EnsureTopmost();

        // Reset hover scales to prevent stretching inner components
        NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        NotchScale.ScaleX = 1.0;
        NotchScale.ScaleY = 1.0;

        ExpandedContent.BeginAnimation(OpacityProperty, null);
        CollapsedContent.BeginAnimation(OpacityProperty, null);
        MusicCompactContent.BeginAnimation(OpacityProperty, null);
        SecondaryContent.BeginAnimation(OpacityProperty, null);
        AnimationThumbnailBorder.BeginAnimation(WidthProperty, null);
        AnimationThumbnailBorder.BeginAnimation(HeightProperty, null);
        AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        MediaBackground.BeginAnimation(OpacityProperty, null);
        MediaBackground2.BeginAnimation(OpacityProperty, null);
        PaginationDots.BeginAnimation(OpacityProperty, null);

        AnimationThumbnailTranslate.X = 0;
        AnimationThumbnailTranslate.Y = 0;
        MediaBackground.Opacity = 0;
        MediaBackground2.Opacity = 0;

        SecondaryContent.Visibility = Visibility.Collapsed;

        ExpandedContent.Opacity = 0;
        ExpandedContent.Visibility = Visibility.Visible;

        PaginationDots.Visibility = Visibility.Visible;
        PaginationDots.Opacity = 0;
        UpdatePaginationDots();

        if (!_cachedThumbnailExpandTarget.HasValue && _isMusicCompactMode)
        {
            double oldW = NotchBorder.Width;
            double oldH = NotchBorder.Height;

            NotchBorder.Width = _expandedWidth;
            NotchBorder.Height = _expandedHeight;
            ExpandedContent.Width = _expandedWidth - 32;
            ExpandedContent.Height = _expandedHeight - 24;

            this.UpdateLayout();

            _cachedThumbnailExpandTarget = ComputeThumbnailExpandTarget();

            NotchBorder.Width = oldW;
            NotchBorder.Height = oldH;
        }

        NotchBorder.IsHitTestVisible = false;
        var animFps = 144;

        var widthAnim = MakeAnim(_expandedWidth, _dur600, _easeExpOut6, animFps);
        var heightAnim = MakeAnim(_expandedHeight, _dur600, _easeExpOut6, animFps);
        var fadeOutAnim = MakeAnim(0, _dur200, _easeQuadOut);

        // Dimensions already set above if first run, but ensure set here for safety
        ExpandedContent.Width = _expandedWidth - 32;
        ExpandedContent.Height = _expandedHeight - 24;

        var expandedGroup = new TransformGroup();
        var expandedTranslate = new TranslateTransform(0, 10);
        expandedGroup.Children.Add(expandedTranslate);
        ExpandedContent.RenderTransform = expandedGroup;
        ExpandedContent.RenderTransformOrigin = new Point(0.5, 0.4);

        var fadeInAnim = MakeAnim(0d, 1d, _dur400, _easePowerOut3);
        var springSlide = MakeAnim(10, 0, _dur400, _easeExpOut6);

        var glowAnim = MakeAnim(0.15, _dur200);



        if (_isMusicCompactMode && CompactThumbnail.Source != null)
        {

            AnimationThumbnailImage.Source = CompactThumbnail.Source;
            AnimationThumbnailBorder.Visibility = Visibility.Visible;
            AnimationThumbnailBorder.Width = 22;
            AnimationThumbnailBorder.Height = 22;
            AnimationThumbnailClip.Rect = new Rect(0, 0, 22, 22);
            AnimationThumbnailTranslate.X = 0;
            AnimationThumbnailTranslate.Y = 0;

            var (targetX, targetY) = _cachedThumbnailExpandTarget ?? (20, 48);

            var thumbDelay = TimeSpan.FromMilliseconds(40);
            var thumbDur = _dur600;
            var thumbEase = _easeThumbSpring;
            var thumbFps = 144;

            if (_cachedThumbWidthExpand == null || _cachedThumbWidthExpand.Duration != thumbDur)
            {
                _cachedThumbWidthExpand = MakeAnim(22, 50, thumbDur, thumbEase, thumbDelay);
                _cachedThumbHeightExpand = MakeAnim(22, 50, thumbDur, thumbEase, thumbDelay);
                Timeline.SetDesiredFrameRate(_cachedThumbWidthExpand, thumbFps);
                Timeline.SetDesiredFrameRate(_cachedThumbHeightExpand, thumbFps);
                
                _cachedThumbRectExpand = new RectAnimation(new Rect(0, 0, 22, 22), new Rect(0, 0, 50, 50), thumbDur)
                {
                    EasingFunction = thumbEase,
                    BeginTime = thumbDelay
                };
                Timeline.SetDesiredFrameRate(_cachedThumbRectExpand, thumbFps);

                _cachedThumbWidthExpand.Freeze();
                _cachedThumbHeightExpand.Freeze();
                _cachedThumbRectExpand.Freeze();
            }

            var thumbTranslateXAnim = MakeAnim(0, targetX, thumbDur, thumbEase, thumbDelay);
            var thumbTranslateYAnim = MakeAnim(0, targetY, thumbDur, thumbEase, thumbDelay);
            Timeline.SetDesiredFrameRate(thumbTranslateXAnim, thumbFps);
            Timeline.SetDesiredFrameRate(thumbTranslateYAnim, thumbFps);

            AnimationThumbnailBorder.BeginAnimation(WidthProperty, _cachedThumbWidthExpand);
            AnimationThumbnailBorder.BeginAnimation(HeightProperty, _cachedThumbHeightExpand);
            AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.XProperty, thumbTranslateXAnim);
            AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.YProperty, thumbTranslateYAnim);

            AnimationThumbnailClip.BeginAnimation(RectangleGeometry.RectProperty, _cachedThumbRectExpand);

            if (CompactThumbnailBorder != null) CompactThumbnailBorder.Opacity = 0;
            if (ThumbnailBorder != null) ThumbnailBorder.Opacity = 0;
        }

        heightAnim.Completed += (s, e) =>
        {
            _isAnimating = false;
            _isExpanded = true;
            NotchBorder.IsHitTestVisible = true;
            UpdateProgressTimerState();
            UpdateBatteryInfo();
            UpdateCalendarInfo();
            RenderProgressBar();
            ShowMediaBackground();



            ExpandedContent.Opacity = 1;
            ExpandedContent.BeginAnimation(OpacityProperty, null);

            AnimationThumbnailBorder.Visibility = Visibility.Collapsed;
            AnimationThumbnailBorder.BeginAnimation(WidthProperty, null);
            AnimationThumbnailBorder.BeginAnimation(HeightProperty, null);
            AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            AnimationThumbnailTranslate.X = 0;
            AnimationThumbnailTranslate.Y = 0;

            if (_isMusicCompactMode)
            {
                _cachedThumbnailExpandTarget = ComputeThumbnailExpandTarget();
                if (ThumbnailBorder != null) ThumbnailBorder.Opacity = 1;
                if (CompactThumbnailBorder != null) CompactThumbnailBorder.Opacity = 1;
            }

            CollapsedContent.Visibility = Visibility.Collapsed;
            MusicCompactContent.Visibility = Visibility.Collapsed;
        };

        NotchBorder.BeginAnimation(WidthProperty, widthAnim);
        NotchBorder.BeginAnimation(HeightProperty, heightAnim);
        CollapsedContent.BeginAnimation(OpacityProperty, fadeOutAnim);
        MusicCompactContent.BeginAnimation(OpacityProperty, fadeOutAnim);

        ExpandedContent.BeginAnimation(OpacityProperty, fadeInAnim);
        PaginationDots.BeginAnimation(OpacityProperty, fadeInAnim);
        expandedTranslate.BeginAnimation(TranslateTransform.YProperty, springSlide);

        HoverGlow.BeginAnimation(OpacityProperty, glowAnim);
        AnimateCornerRadius(_cornerRadiusExpanded, TimeSpan.FromMilliseconds(400));
    }

    private void CollapseNotch()
    {
        if (_isAnimating || !_isExpanded) return;
        _isAnimating = true;
        _progressTimer?.Stop();

        UpdateZOrderTimerInterval();
        EnsureTopmost();

        ExpandedContent.BeginAnimation(OpacityProperty, null);
        SecondaryContent.BeginAnimation(OpacityProperty, null);
        MusicCompactContent.BeginAnimation(OpacityProperty, null);
        CollapsedContent.BeginAnimation(OpacityProperty, null);
        PaginationDots.BeginAnimation(OpacityProperty, null);
        AnimationThumbnailBorder.BeginAnimation(WidthProperty, null);
        AnimationThumbnailBorder.BeginAnimation(HeightProperty, null);
        AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.YProperty, null);

        AnimationThumbnailBorder.Visibility = Visibility.Collapsed;
        AnimationThumbnailTranslate.X = 0;
        AnimationThumbnailTranslate.Y = 0;

        NotchBorder.IsHitTestVisible = false;
        var animFps = 144;

        var widthAnim = MakeAnim(_collapsedWidth, _dur500, _easeExpOut6, animFps);
        var heightAnim = MakeAnim(_collapsedHeight, _dur500, _easeExpOut6, animFps);

        var expandedGroup = new TransformGroup();
        var expandedTranslate = new TranslateTransform(0, 0);
        expandedGroup.Children.Add(expandedTranslate);
        ExpandedContent.RenderTransform = expandedGroup;
        ExpandedContent.RenderTransformOrigin = new Point(0.5, 0.4);



        var fadeOutAnim = MakeAnim(0, _dur200, _easeQuadOut);
        var slideOutAnim = MakeAnim(0, -10, _dur400, _easeExpOut6);

        fadeOutAnim.Completed += (s, e) =>
        {
            ExpandedContent.BeginAnimation(OpacityProperty, null);
            ExpandedContent.Opacity = 0;
            ExpandedContent.Visibility = Visibility.Collapsed;
            ExpandedContent.RenderTransform = null;

            SecondaryContent.BeginAnimation(OpacityProperty, null);
            SecondaryContent.Opacity = 0;
            SecondaryContent.Visibility = Visibility.Collapsed;
            SecondaryContent.RenderTransform = null;

            _isSecondaryView = false;
        };

        var fadeOutBlurAnim = MakeAnim(0, TimeSpan.FromMilliseconds(150), _easeQuadOut);
        MediaBackground.BeginAnimation(OpacityProperty, fadeOutBlurAnim);
        MediaBackground2.BeginAnimation(OpacityProperty, fadeOutBlurAnim);

        FrameworkElement contentToShow = _isMusicCompactMode ? MusicCompactContent : CollapsedContent;
        FrameworkElement contentToHide = _isMusicCompactMode ? CollapsedContent : MusicCompactContent;

        contentToHide.BeginAnimation(OpacityProperty, null);
        contentToHide.Visibility = Visibility.Collapsed;
        contentToHide.Opacity = 0;

        var showGroup = new TransformGroup();
        var showScale = new ScaleTransform(0.8, 0.8);
        showGroup.Children.Add(showScale);
        contentToShow.RenderTransform = showGroup;
        contentToShow.RenderTransformOrigin = new Point(0.5, 0.5);

        var fadeInAnim = MakeAnim(1, _dur400, _easePowerOut3);
        var springShow = MakeAnim(0.8, 1, _dur400, _easeMenuSpring);

        var glowAnim = MakeAnim(0, _dur150);

        if (_isMusicCompactMode && ThumbnailImage.Source != null)
        {
            var (startX, startY) = _cachedThumbnailExpandTarget ?? ComputeThumbnailExpandTarget();
            _cachedThumbnailExpandTarget = (startX, startY);

            AnimationThumbnailImage.Source = ThumbnailImage.Source;
            AnimationThumbnailBorder.Visibility = Visibility.Visible;
            AnimationThumbnailBorder.Width = 50;
            AnimationThumbnailBorder.Height = 50;
            AnimationThumbnailClip.Rect = new Rect(0, 0, 50, 50);
            AnimationThumbnailTranslate.X = startX;
            AnimationThumbnailTranslate.Y = startY;

            var thumbDelay = TimeSpan.FromMilliseconds(30);
            var thumbDur = _dur500;
            var thumbEase = _easeThumbSpring;
            var thumbFps = 144;

            if (_cachedThumbWidthCollapse == null || _cachedThumbWidthCollapse.Duration != thumbDur)
            {
                _cachedThumbWidthCollapse = MakeAnim(50, 22, thumbDur, thumbEase, thumbDelay);
                _cachedThumbHeightCollapse = MakeAnim(50, 22, thumbDur, thumbEase, thumbDelay);
                Timeline.SetDesiredFrameRate(_cachedThumbWidthCollapse, thumbFps);
                Timeline.SetDesiredFrameRate(_cachedThumbHeightCollapse, thumbFps);

                _cachedThumbRectCollapse = new RectAnimation(new Rect(0, 0, 50, 50), new Rect(0, 0, 22, 22), thumbDur)
                {
                    EasingFunction = thumbEase,
                    BeginTime = thumbDelay
                };
                Timeline.SetDesiredFrameRate(_cachedThumbRectCollapse, thumbFps);

                _cachedThumbWidthCollapse.Freeze();
                _cachedThumbHeightCollapse.Freeze();
                _cachedThumbRectCollapse.Freeze();
            }

            var thumbTranslateXAnim = MakeAnim(startX, 0, thumbDur, thumbEase, thumbDelay);
            var thumbTranslateYAnim = MakeAnim(startY, 0, thumbDur, thumbEase, thumbDelay);
            Timeline.SetDesiredFrameRate(thumbTranslateXAnim, thumbFps);
            Timeline.SetDesiredFrameRate(thumbTranslateYAnim, thumbFps);

            AnimationThumbnailBorder.BeginAnimation(WidthProperty, _cachedThumbWidthCollapse);
            AnimationThumbnailBorder.BeginAnimation(HeightProperty, _cachedThumbHeightCollapse);
            AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.XProperty, thumbTranslateXAnim);
            AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.YProperty, thumbTranslateYAnim);

            AnimationThumbnailClip.BeginAnimation(RectangleGeometry.RectProperty, _cachedThumbRectCollapse);

            if (CompactThumbnailBorder != null) CompactThumbnailBorder.Opacity = 0;
            if (ThumbnailBorder != null) ThumbnailBorder.Opacity = 0;
        }

        heightAnim.Completed += (s, e) =>
        {
            _isAnimating = false;
            _isExpanded = false;
            NotchBorder.IsHitTestVisible = true;
            UpdateProgressTimerState();

            contentToShow.RenderTransform = null;

            if (_isMusicCompactMode)
            {
                if (CompactThumbnailBorder != null) CompactThumbnailBorder.Opacity = 1;
                if (ThumbnailBorder != null) ThumbnailBorder.Opacity = 1;

                contentToShow.Opacity = 1;
                contentToShow.BeginAnimation(OpacityProperty, null);

                AnimationThumbnailBorder.Visibility = Visibility.Collapsed;
                AnimationThumbnailBorder.BeginAnimation(WidthProperty, null);
                AnimationThumbnailBorder.BeginAnimation(HeightProperty, null);
                AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                AnimationThumbnailTranslate.X = 0;
                AnimationThumbnailTranslate.Y = 0;

                // Reset compact hover state
                CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                CompactThumbnailScale.ScaleX = 1.0;
                CompactThumbnailScale.ScaleY = 1.0;
                CompactHoverInfo.BeginAnimation(OpacityProperty, null);
                CompactHoverInfo.Opacity = 0;
                CompactHoverInfo.Visibility = Visibility.Collapsed;
            }

        };

        NotchBorder.BeginAnimation(WidthProperty, widthAnim);
        NotchBorder.BeginAnimation(HeightProperty, heightAnim);

        ExpandedContent.BeginAnimation(OpacityProperty, fadeOutAnim);
        PaginationDots.BeginAnimation(OpacityProperty, fadeOutAnim);
        expandedTranslate.BeginAnimation(TranslateTransform.YProperty, slideOutAnim);

        if (SecondaryContent.Visibility == Visibility.Visible)
        {
            SecondaryContent.BeginAnimation(OpacityProperty, fadeOutAnim);


        }

        contentToShow.Visibility = Visibility.Visible;
        contentToShow.BeginAnimation(OpacityProperty, fadeInAnim);
        showScale.BeginAnimation(ScaleTransform.ScaleXProperty, springShow);
        showScale.BeginAnimation(ScaleTransform.ScaleYProperty, springShow);

        HoverGlow.BeginAnimation(OpacityProperty, glowAnim);
        AnimateCornerRadius(_cornerRadiusCollapsed, TimeSpan.FromMilliseconds(400));
    }

    #endregion

    #region Hover Animations

    private void AnimateNotchHover(bool isHovered)
    {
        if (_isExpanded || _isAnimating) return;

        double targetScale = isHovered ? 1.08 : 1.0;
        var duration = isHovered ? _dur500 : _dur350;
        var easing = isHovered ? (IEasingFunction)_easeSoftSpring : _easeQuadOut;

        var animX = MakeAnim(targetScale, duration, easing);
        NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
    }

    private void AnimateThumbnailHover(bool isHovered)
    {
        if (_isExpanded || _isAnimating) return;

        double thumbScale = isHovered ? 1.6 : 1.0;
        double notchHeight = isHovered ? 84 : _collapsedHeight;
        double infoOpacity = isHovered ? 1 : 0;
        
        var duration = isHovered ? _dur500 : _dur350;
        var easing = isHovered ? (IEasingFunction)_easeThumbSpring : _easeExpOut6;
        var animFps = 144;

        // Notch height (Expands to show title)
        var heightAnim = MakeAnim(notchHeight, duration, isHovered ? _easeExpOut6 : _easeQuadOut, animFps);
        NotchBorder.BeginAnimation(HeightProperty, heightAnim);

        // Thumbnail scale (Anchored at 0,0 grows right and down)
        var thumbScaleAnimX = MakeAnim(thumbScale, duration, easing, animFps);
        var thumbScaleAnimY = MakeAnim(thumbScale, duration, easing, animFps);
        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, thumbScaleAnimX);
        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, thumbScaleAnimY);

        // Info fade
        if (isHovered)
        {
            CompactHoverInfo.Visibility = Visibility.Visible;
            UpdateCompactMarquee();
        }

        var fadeAnim = MakeAnim(infoOpacity, isHovered ? _dur200 : _dur100, _easeQuadOut);
        if (!isHovered)
        {
            fadeAnim.Completed += (s, e) => { if (CompactHoverInfo.Opacity < 0.1) CompactHoverInfo.Visibility = Visibility.Collapsed; };
        }
        CompactHoverInfo.BeginAnimation(OpacityProperty, fadeAnim);

        // Corner radius adjust
        double radius = isHovered ? 24 : _cornerRadiusCollapsed;
        AnimateCornerRadius(radius, duration.TimeSpan);
    }

    private void UpdateCompactMarquee()
    {
        if (_currentMediaInfo == null) return;
        
        CompactTitleMarquee.Text = _currentMediaInfo.CurrentTrack;
        CompactTitleMarquee.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        
        double textWidth = CompactTitleMarquee.DesiredSize.Width;
        // Chiều rộng container bằng chiều rộng Notch trừ đi Margin (4+4=8)
        double containerWidth = _collapsedWidth - 8; 
        
        if (textWidth > containerWidth && containerWidth > 0)
        {
            // Bật hiệu ứng fade khi text dài và phải marquee
            CompactHoverInfo.OpacityMask = CompactMarqueeFadeBrush;
            StartMarqueeAnimation(CompactTitleMarqueeTranslate, textWidth - containerWidth + 20);
        }
        else
        {
            // Tắt hiệu ứng fade khi text ngắn để hiển thị rõ 100%
            CompactHoverInfo.OpacityMask = null;
            
            CompactTitleMarqueeTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            CompactTitleMarqueeTranslate.X = (containerWidth - textWidth) / 2;
        }
    }

    #endregion

    #region Expanded Music Player Animations

    private bool _isMusicExpanded = false;
    private bool _isMusicAnimating = false;
    private double _musicWidgetSmallWidth = 0;

    private void ExpandMusicWidget()
    {
        if (_isMusicAnimating) return;
        _isMusicAnimating = true;
        _isMusicExpanded = true;

        UpdateZOrderTimerInterval();

        _musicWidgetSmallWidth = MediaWidgetContainer.ActualWidth;

        var expandDuration = new Duration(TimeSpan.FromMilliseconds(500));
        var contentDelay = TimeSpan.FromMilliseconds(150);

        var fadeOutCalendar = MakeAnim(1d, 0d, _dur150, _easePowerIn2, null);
        fadeOutCalendar.Completed += (s, e) => CalendarWidget.Visibility = Visibility.Collapsed;
        CalendarWidget.BeginAnimation(OpacityProperty, fadeOutCalendar);

        var fadeOutControls = MakeAnim(1d, 0d, _dur150, _easePowerIn2, null);
        fadeOutControls.Completed += (s, e) => MediaControls.Visibility = Visibility.Collapsed;
        MediaControls.BeginAnimation(OpacityProperty, fadeOutControls);

        double startWidth = MediaWidgetContainer.ActualWidth;
        double finalWidth = ExpandedContent.ActualWidth;

        MediaWidgetContainer.Width = startWidth;
        MediaWidgetContainer.HorizontalAlignment = HorizontalAlignment.Left;
        Panel.SetZIndex(MediaWidgetContainer, 10);
        Grid.SetColumnSpan(MediaWidgetContainer, 3);

        var widthAnim = new DoubleAnimation(startWidth, finalWidth, expandDuration)
        {
            EasingFunction = _easeExpOut7
        };

        var marginAnim = new ThicknessAnimation(new Thickness(0, 0, 8, 0), new Thickness(0), expandDuration)
        {
            EasingFunction = _easeExpOut7
        };

        widthAnim.Completed += (s, e) =>
        {
            MediaWidgetContainer.Width = double.NaN;
            MediaWidgetContainer.Margin = new Thickness(0);
            MediaWidgetContainer.HorizontalAlignment = HorizontalAlignment.Stretch;
            UpdateProgressTimerState();
            MediaWidgetContainer.BeginAnimation(WidthProperty, null);
            MediaWidgetContainer.BeginAnimation(MarginProperty, null);
            _isMusicAnimating = false;
        };

        MediaWidgetContainer.BeginAnimation(WidthProperty, widthAnim);
        MediaWidgetContainer.BeginAnimation(MarginProperty, marginAnim);

        InlineControls.Visibility = Visibility.Visible;

        var fadeInInline = MakeAnim(0d, 1d, _dur350, _easeExpOut7, contentDelay);
        InlineControls.BeginAnimation(OpacityProperty, fadeInInline);

        var slideUpAnim = MakeAnim(10, 0, _dur450, _easeSpring, contentDelay);
        var slideTransform = InlineControls.RenderTransform as TranslateTransform ?? new TranslateTransform(0, 10);
        InlineControls.RenderTransform = slideTransform;
        slideTransform.BeginAnimation(TranslateTransform.YProperty, slideUpAnim);

        InlinePauseIcon.Visibility = _isPlaying ? Visibility.Visible : Visibility.Collapsed;
        InlinePlayIcon.Visibility = _isPlaying ? Visibility.Collapsed : Visibility.Visible;

        SyncVolumeFromSystem();
    }

    private void CollapseMusicWidget()
    {
        if (_isMusicAnimating) return;
        _isMusicAnimating = true;
        _isMusicExpanded = false;

        _progressTimer?.Stop();
        UpdateZOrderTimerInterval();

        var collapseDuration = new Duration(TimeSpan.FromMilliseconds(400));
        var contentDelay = TimeSpan.FromMilliseconds(80);

        var fadeOutInline = MakeAnim(1d, 0d, _dur150, _easePowerIn2, null);
        fadeOutInline.Completed += (s, e) =>
        {
            InlineControls.Visibility = Visibility.Collapsed;
        };
        InlineControls.BeginAnimation(OpacityProperty, fadeOutInline);

        double currentWidth = MediaWidgetContainer.ActualWidth;
        double targetSmallWidth = _musicWidgetSmallWidth > 0 ? _musicWidgetSmallWidth : (ExpandedContent.ActualWidth / 3.0) - 8;

        MediaWidgetContainer.Width = currentWidth;
        MediaWidgetContainer.HorizontalAlignment = HorizontalAlignment.Left;

        var widthAnim = new DoubleAnimation(currentWidth, targetSmallWidth, collapseDuration)
        {
            EasingFunction = _easeExpOut7
        };

        var marginAnim = new ThicknessAnimation(new Thickness(0), new Thickness(0, 0, 8, 0), collapseDuration)
        {
            EasingFunction = _easeExpOut7
        };

        widthAnim.Completed += (s, e) =>
        {
            MediaWidgetContainer.Width = double.NaN;
            MediaWidgetContainer.Margin = new Thickness(0, 0, 8, 0);
            MediaWidgetContainer.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumnSpan(MediaWidgetContainer, 1);
            Panel.SetZIndex(MediaWidgetContainer, 0);
            UpdateProgressTimerState();
            MediaWidgetContainer.BeginAnimation(WidthProperty, null);
            MediaWidgetContainer.BeginAnimation(MarginProperty, null);
            _isMusicAnimating = false;
        };

        MediaWidgetContainer.BeginAnimation(WidthProperty, widthAnim);
        MediaWidgetContainer.BeginAnimation(MarginProperty, marginAnim);

        MediaControls.Visibility = Visibility.Visible;
        var fadeInControls = MakeAnim(0d, 1d, new Duration(TimeSpan.FromMilliseconds(300)), _easePowerOut3, contentDelay);
        MediaControls.BeginAnimation(OpacityProperty, fadeInControls);

        CalendarWidget.Visibility = Visibility.Visible;
        var fadeInCalendar = MakeAnim(0d, 1d, new Duration(TimeSpan.FromMilliseconds(300)), _easePowerOut3, TimeSpan.FromMilliseconds(120));
        CalendarWidget.BeginAnimation(OpacityProperty, fadeInCalendar);
    }

    private void MediaWidgetContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double availableWidth = e.NewSize.Width - 72 - 12;
        double fadeEndX = Math.Max(0, Math.Min(250, availableWidth));
        
        if (TextFadeBrush != null)
        {
            TextFadeBrush.EndPoint = new Point(fadeEndX, 0);
            TextFadeBrush.GradientStops[0].Offset = 0;
            double fadeStartX = Math.Max(0, fadeEndX - 20);
            TextFadeBrush.GradientStops[1].Offset = fadeEndX > 0 ? fadeStartX / fadeEndX : 0.8;
            TextFadeBrush.GradientStops[2].Offset = 1;
        }
    }

    #endregion

    #region Animation Helpers

    private void FadeSwitch(FrameworkElement from, FrameworkElement to)
    {

        from.BeginAnimation(OpacityProperty, null);
        to.BeginAnimation(OpacityProperty, null);

        var fadeOut = MakeAnim(0, _dur100);
        fadeOut.Completed += (s, e) =>
        {
            if (from.Opacity < 0.05) from.Visibility = Visibility.Collapsed;
        };
        from.BeginAnimation(OpacityProperty, fadeOut);

        to.Visibility = Visibility.Visible;
        var fadeIn = MakeAnim(1, _dur200);
        to.BeginAnimation(OpacityProperty, fadeIn);
    }

    private void AnimateButtonScale(ScaleTransform scaleTransform, double targetScale)
    {
        var animX = new DoubleAnimation(scaleTransform.ScaleX, targetScale, _dur150) { EasingFunction = _easeQuadOut };
        var animY = new DoubleAnimation(scaleTransform.ScaleY, targetScale, _dur150) { EasingFunction = _easeQuadOut };

        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
    }

    private void AnimateIconSwitch(Canvas fromIcon, Canvas toIcon, TimeSpan duration, EasingFunctionBase easing)
    {

        var fromTransform = fromIcon.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        fromIcon.RenderTransform = fromTransform;
        fromTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        fromTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        fromIcon.BeginAnimation(OpacityProperty, null);

        var toTransform = toIcon.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        toIcon.RenderTransform = toTransform;
        toTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        toTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        toIcon.BeginAnimation(OpacityProperty, null);

        fromIcon.Visibility = Visibility.Visible;
        fromTransform.ScaleX = 1;
        fromTransform.ScaleY = 1;
        fromIcon.Opacity = 1;

        toIcon.Visibility = Visibility.Visible;
        toTransform.ScaleX = 0.3;
        toTransform.ScaleY = 0.3;
        toIcon.Opacity = 0;

        var dur = new Duration(duration);
        var scaleDown = new DoubleAnimation(1, 0.3, dur) { EasingFunction = easing };
        var fadeOut = new DoubleAnimation(1, 0, dur) { EasingFunction = easing };
        var scaleUp = new DoubleAnimation(0.3, 1, dur) { EasingFunction = easing };
        var fadeIn = new DoubleAnimation(0, 1, dur) { EasingFunction = easing };

        var capturedFromIcon = fromIcon;
        var capturedFromTransform = fromTransform;

        fadeOut.Completed += (s, e) =>
        {

            capturedFromTransform.ScaleX = 1;
            capturedFromTransform.ScaleY = 1;
            capturedFromIcon.Opacity = 1;

            capturedFromIcon.Visibility = Visibility.Collapsed;
            capturedFromTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            capturedFromTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            capturedFromIcon.BeginAnimation(OpacityProperty, null);
        };

        fromTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDown);
        fromTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDown);
        fromIcon.BeginAnimation(OpacityProperty, fadeOut);

        toTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleUp);
        toTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleUp);
        toIcon.BeginAnimation(OpacityProperty, fadeIn);
    }

    private void PlayButtonPressAnimation(Border button)
    {
        var scaleDown = MakeAnim(1d, 0.9d, _dur80, null, null);
        var scaleUp = new DoubleAnimation(0.9, 1, _dur100) { BeginTime = TimeSpan.FromMilliseconds(80) };

        var transform = button.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        button.RenderTransform = transform;

        transform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDown);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDown);

        scaleDown.Completed += (s, e) =>
        {
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleUp);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleUp);
        };
    }

    private void PlayNextSkipAnimation()
    {
        PlayNextSkipAnimation(NextArrow0, NextArrow1, NextArrow2);
    }

    private void PlayNextSkipAnimation(Path arrow0, Path arrow1, Path arrow2)
    {

        var arrow2Transform = arrow2.RenderTransform as TranslateTransform ?? new TranslateTransform();
        arrow2.RenderTransform = arrow2Transform;

        var slideOut2 = new DoubleAnimation(0, 12, _dur250) { EasingFunction = _easeQuadOut };
        var fadeOut2 = new DoubleAnimation(1, 0, _dur250) { EasingFunction = _easeQuadOut };

        var arrow1Transform = arrow1.RenderTransform as TranslateTransform ?? new TranslateTransform();
        arrow1.RenderTransform = arrow1Transform;

        var slideRight1 = new DoubleAnimation(0, 10, _dur250) { EasingFunction = _easeQuadOut };

        var arrow0Transform = arrow0.RenderTransform as TranslateTransform ?? new TranslateTransform();
        arrow0.RenderTransform = arrow0Transform;

        var slideIn0 = new DoubleAnimation(0, 10, _dur250) { EasingFunction = _easeQuadOut };
        var fadeIn0 = new DoubleAnimation(0, 1, _dur250) { EasingFunction = _easeQuadOut };

        arrow2Transform.BeginAnimation(TranslateTransform.XProperty, slideOut2);
        arrow2.BeginAnimation(OpacityProperty, fadeOut2);
        arrow1Transform.BeginAnimation(TranslateTransform.XProperty, slideRight1);
        arrow0Transform.BeginAnimation(TranslateTransform.XProperty, slideIn0);
        arrow0.BeginAnimation(OpacityProperty, fadeIn0);

        fadeOut2.Completed += (s, e) =>
        {

            arrow2Transform.X = 0;
            arrow2.Opacity = 1;
            arrow1Transform.X = 0;
            arrow0Transform.X = 0;
            arrow0.Opacity = 0;

            arrow2Transform.BeginAnimation(TranslateTransform.XProperty, null);
            arrow2.BeginAnimation(OpacityProperty, null);
            arrow1Transform.BeginAnimation(TranslateTransform.XProperty, null);
            arrow0Transform.BeginAnimation(TranslateTransform.XProperty, null);
            arrow0.BeginAnimation(OpacityProperty, null);
        };
    }

    private void PlayPrevSkipAnimation()
    {
        PlayPrevSkipAnimation(PrevArrow0, PrevArrow1, PrevArrow2);
    }

    private void PlayPrevSkipAnimation(Path arrow0, Path arrow1, Path arrow2)
    {
        var arrow2Transform = arrow2.RenderTransform as TranslateTransform ?? new TranslateTransform();
        arrow2.RenderTransform = arrow2Transform;

        var slideOut2 = new DoubleAnimation(0, -12, _dur250) { EasingFunction = _easeQuadOut };
        var fadeOut2 = new DoubleAnimation(1, 0, _dur250) { EasingFunction = _easeQuadOut };

        var arrow1Transform = arrow1.RenderTransform as TranslateTransform ?? new TranslateTransform();
        arrow1.RenderTransform = arrow1Transform;

        var slideLeft1 = new DoubleAnimation(0, -10, _dur250) { EasingFunction = _easeQuadOut };

        var arrow0Transform = arrow0.RenderTransform as TranslateTransform ?? new TranslateTransform();
        arrow0.RenderTransform = arrow0Transform;

        var slideIn0 = new DoubleAnimation(0, -10, _dur250) { EasingFunction = _easeQuadOut };
        var fadeIn0 = new DoubleAnimation(0, 1, _dur250) { EasingFunction = _easeQuadOut };

        arrow2Transform.BeginAnimation(TranslateTransform.XProperty, slideOut2);
        arrow2.BeginAnimation(OpacityProperty, fadeOut2);
        arrow1Transform.BeginAnimation(TranslateTransform.XProperty, slideLeft1);
        arrow0Transform.BeginAnimation(TranslateTransform.XProperty, slideIn0);
        arrow0.BeginAnimation(OpacityProperty, fadeIn0);

        fadeOut2.Completed += (s, e) =>
        {

            arrow2Transform.X = 0;
            arrow2.Opacity = 1;
            arrow1Transform.X = 0;
            arrow0Transform.X = 0;
            arrow0.Opacity = 0;

            arrow2Transform.BeginAnimation(TranslateTransform.XProperty, null);
            arrow2.BeginAnimation(OpacityProperty, null);
            arrow1Transform.BeginAnimation(TranslateTransform.XProperty, null);
            arrow0Transform.BeginAnimation(TranslateTransform.XProperty, null);
            arrow0.BeginAnimation(OpacityProperty, null);
        };
    }

    private void PlayAppearAnimation()
    {
        NotchBorder.Opacity = 0;

        var opacityAnim = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(400)),
            EasingFunction = _easeQuadOut
        };

        NotchBorder.BeginAnimation(OpacityProperty, opacityAnim);
    }

    public static readonly DependencyProperty CurrentCornerRadiusProperty =
        DependencyProperty.Register("CurrentCornerRadius", typeof(double), typeof(MainWindow),
            new PropertyMetadata(0.0, OnCurrentCornerRadiusChanged));

    public double CurrentCornerRadius
    {
        get => (double)GetValue(CurrentCornerRadiusProperty);
        set => SetValue(CurrentCornerRadiusProperty, value);
    }

    private static void OnCurrentCornerRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MainWindow window)
        {
            double radius = (double)e.NewValue;
            var cr = new CornerRadius(0, 0, radius, radius);
            window.NotchBorder.CornerRadius = cr;
            window.InnerClipBorder.CornerRadius = cr;
            window.MediaBackground.CornerRadius = cr;
            window.MediaBackground2.CornerRadius = cr;
            window.NotchBorderShadow.CornerRadius = cr;
            window.UpdateNotchClip();
        }
    }

    private void AnimateCornerRadius(double targetRadius, TimeSpan duration)
    {
        double startRadius = NotchBorder.CornerRadius.BottomLeft;
        
        if (Math.Abs(targetRadius - startRadius) < 0.5) return;

        CurrentCornerRadius = startRadius;

        var anim = MakeAnim(startRadius, targetRadius, new Duration(duration), _easeExpOut6, null);
        this.BeginAnimation(CurrentCornerRadiusProperty, anim);
    }

    public void PlayTrackChangeBounce()
    {
        if (_isExpanded || _isAnimating) return;

        var durPeak = TimeSpan.FromMilliseconds(150);
        var durEnd = TimeSpan.FromMilliseconds(800);

        // Bouncy scale effect (Squash and Stretch)
        // We must return to 1.0 to avoid permanent stretching
        var bounceX = new DoubleAnimationUsingKeyFrames();
        bounceX.KeyFrames.Add(new EasingDoubleKeyFrame(1.12, KeyTime.FromTimeSpan(durPeak), _easeQuadOut));
        bounceX.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(durEnd), _easeSoftSpring));
        Timeline.SetDesiredFrameRate(bounceX, 144);

        var bounceY = new DoubleAnimationUsingKeyFrames();
        bounceY.KeyFrames.Add(new EasingDoubleKeyFrame(0.92, KeyTime.FromTimeSpan(durPeak), _easeQuadOut));
        bounceY.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(durEnd), _easeSoftSpring));
        Timeline.SetDesiredFrameRate(bounceY, 144);

        NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounceX);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounceY);
    }

    private void AnimateProgressBarHover(bool isHovered)
    {
        double margin = isHovered ? 0 : 22;
        double scaleY = isHovered ? 1.8 : 1.0;
        double blurRadius = isHovered ? 8 : 0;
        double bgOpacity = isHovered ? 0.4 : 1.0;
        
        var duration = isHovered ? _dur400 : _dur350;
        var easing = isHovered ? (IEasingFunction)_easeExpOut6 : _easeQuadOut;

        // Margin animation (Expansion)
        var marginAnim = new ThicknessAnimation(ProgressBarContainer.Margin, new Thickness(margin, 0, margin, 0), duration)
        {
            EasingFunction = easing
        };
        ProgressBarContainer.BeginAnimation(MarginProperty, marginAnim);

        // Scale animation
        var scaleAnim = MakeAnim(scaleY, duration, easing);
        ProgressBarMainScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);

        // Blur animation
        var blurAnim = MakeAnim(blurRadius, duration, _easeQuadOut);
        CurrentTimeBlur.BeginAnimation(BlurEffect.RadiusProperty, blurAnim);
        RemainingTimeBlur.BeginAnimation(BlurEffect.RadiusProperty, blurAnim);

        // Subtly fade background to focus on the bar
        var bgFadeAnim = MakeAnim(bgOpacity, duration, _easeQuadOut);
        ProgressBarBg.BeginAnimation(OpacityProperty, bgFadeAnim);
    }

    #endregion
}