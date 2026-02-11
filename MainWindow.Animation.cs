using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace VNotch;

/// <summary>
/// Partial class for Animation logic with Object Pooling.
/// All EasingFunctions are cached as frozen static singletons.
/// Reusable DoubleAnimation helpers reduce GC pressure.
/// </summary>
public partial class MainWindow
{
    #region Cached Easing Functions (Frozen - Thread Safe)

    private static readonly ExponentialEase _easeExpOut7 = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 7 };
    private static readonly ExponentialEase _easeExpOut6 = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };
    private static readonly ExponentialEase _easeExpOut5 = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 };
    private static readonly QuadraticEase _easeQuadOut = new QuadraticEase { EasingMode = EasingMode.EaseOut };
    private static readonly QuadraticEase _easeQuadInOut = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
    private static readonly PowerEase _easePowerIn2 = new PowerEase { EasingMode = EasingMode.EaseIn, Power = 2 };
    private static readonly PowerEase _easePowerOut3 = new PowerEase { EasingMode = EasingMode.EaseOut, Power = 3 };
    private static readonly ElasticEase _easeSpring = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 8 };
    private static readonly SineEase _easeSineInOut = new SineEase { EasingMode = EasingMode.EaseInOut };

    static MainWindow()
    {
        _easeExpOut7.Freeze();
        _easeExpOut6.Freeze();
        _easeExpOut5.Freeze();
        _easeQuadOut.Freeze();
        _easeQuadInOut.Freeze();
        _easePowerIn2.Freeze();
        _easePowerOut3.Freeze();
        _easeSpring.Freeze();
        _easeSineInOut.Freeze();
    }

    #endregion

    #region Cached Durations

    private static readonly Duration _dur350 = new(TimeSpan.FromMilliseconds(350));
    private static readonly Duration _dur250 = new(TimeSpan.FromMilliseconds(250));
    private static readonly Duration _dur200 = new(TimeSpan.FromMilliseconds(200));
    private static readonly Duration _dur180 = new(TimeSpan.FromMilliseconds(180));
    private static readonly Duration _dur150 = new(TimeSpan.FromMilliseconds(150));
    private static readonly Duration _dur100 = new(TimeSpan.FromMilliseconds(100));
    private static readonly Duration _dur80 = new(TimeSpan.FromMilliseconds(80));

    #endregion

    #region Animation Pool Helpers

    /// <summary>
    /// Get a DoubleAnimation configured with cached easing. No event handlers.
    /// Safe to reuse since WPF clones internally when frozen, or uses directly otherwise.
    /// </summary>
    private static DoubleAnimation MakeAnim(double? from, double to, Duration duration, IEasingFunction? easing = null, int fps = 60)
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

    private static DoubleAnimation MakeAnim(double to, Duration duration, IEasingFunction? easing = null, int fps = 60)
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
        // Only set BeginTime when explicitly specified (non-null).
        // In WPF, BeginTime = null means the timeline NEVER starts.
        // Default is TimeSpan.Zero (start immediately).
        if (beginTime.HasValue)
            anim.BeginTime = beginTime.Value;
        Timeline.SetDesiredFrameRate(anim, 60);
        return anim;
    }

    #endregion

    #region Notch Expand/Collapse
    /// <summary>
    /// Compute the target translate offset for the floating thumbnail animation.
    /// Returns the offset from the AnimationThumbnailBorder base position (Margin 8,4)
    /// to where ThumbnailBorder actually sits inside the expanded layout.
    /// Must be called AFTER NotchBorder has expanded dimensions set.
    /// </summary>
    private (double X, double Y) ComputeThumbnailExpandTarget()
    {
        try
        {
            // Get the position of ThumbnailBorder relative to InnerClipBorder
            var thumbPos = ThumbnailBorder.TransformToAncestor(InnerClipBorder).Transform(new Point(0, 0));
            // AnimationThumbnailBorder base position is Margin(8,4)
            double targetX = thumbPos.X - 8;
            double targetY = thumbPos.Y - 4;
            return (targetX, targetY);
        }
        catch
        {
            // Fallback to measured defaults
            return (20, 48);
        }
    }

    // Cached target for thumbnail expand animation (computed once after first expand)
    private (double X, double Y)? _cachedThumbnailExpandTarget;

    private void ExpandNotch()
    {
        if (_isAnimating || _isExpanded) return;
        _isAnimating = true;

        UpdateZOrderTimerInterval();
        EnsureTopmost();

        // Clear any stale animations from prior collapse
        ExpandedContent.BeginAnimation(OpacityProperty, null);
        CollapsedContent.BeginAnimation(OpacityProperty, null);
        MusicCompactContent.BeginAnimation(OpacityProperty, null);
        AnimationThumbnailBorder.BeginAnimation(WidthProperty, null);
        AnimationThumbnailBorder.BeginAnimation(HeightProperty, null);
        AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.YProperty, null);

        // Reset translate to base
        AnimationThumbnailTranslate.X = 0;
        AnimationThumbnailTranslate.Y = 0;

        ExpandedContent.Opacity = 0;
        ExpandedContent.Visibility = Visibility.Visible;

        // Base Notch Animations
        var widthAnim = MakeAnim(_expandedWidth, _dur350, _easeExpOut7);
        var heightAnim = MakeAnim(_expandedHeight, _dur350, _easeExpOut7);
        var fadeOutAnim = MakeAnim(0, _dur100, _easeQuadOut);
        var fadeInAnim = MakeAnim(0d, 1d, _dur200, _easeQuadOut, null);
        var glowAnim = MakeAnim(0.15, _dur200);

        // --- Thumbnail Animation Logic ---
        if (_isMusicCompactMode && CompactThumbnail.Source != null)
        {
            // Setup floating thumbnail at compact position (Margin is fixed at 8,4)
            AnimationThumbnailImage.Source = CompactThumbnail.Source;
            AnimationThumbnailBorder.Visibility = Visibility.Visible;
            AnimationThumbnailBorder.Width = 22;
            AnimationThumbnailBorder.Height = 22;
            AnimationThumbnailTranslate.X = 0;
            AnimationThumbnailTranslate.Y = 0;

            // Use cached target or fallback defaults
            // Target will be recalculated and cached in heightAnim.Completed
            var (targetX, targetY) = _cachedThumbnailExpandTarget ?? (20, 48);

            // Animate to the target position
            var thumbWidthAnim = MakeAnim(22, 50, _dur350, _easeExpOut7);
            var thumbHeightAnim = MakeAnim(22, 50, _dur350, _easeExpOut7);
            var thumbTranslateXAnim = MakeAnim(0, targetX, _dur350, _easeExpOut7);
            var thumbTranslateYAnim = MakeAnim(0, targetY, _dur350, _easeExpOut7);

            AnimationThumbnailBorder.BeginAnimation(WidthProperty, thumbWidthAnim);
            AnimationThumbnailBorder.BeginAnimation(HeightProperty, thumbHeightAnim);
            AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.XProperty, thumbTranslateXAnim);
            AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.YProperty, thumbTranslateYAnim);

            // Don't hide CompactThumbnailBorder - let it fade out naturally with MusicCompactContent
            // AnimationThumbnailBorder covers it via Panel.ZIndex="100"
            if (ThumbnailBorder != null) ThumbnailBorder.Opacity = 0;
        }

        heightAnim.Completed += (s, e) =>
        {
            _isAnimating = false;
            _isExpanded = true;
            UpdateProgressTimerState();
            UpdateBatteryInfo();
            UpdateCalendarInfo();
            RenderProgressBar();
            
            // Cleanup Thumbnail Animation - crossfade to avoid snap
            if (_isMusicCompactMode)
            {
                // Cache the accurate target position now that layout is at expanded size
                _cachedThumbnailExpandTarget = ComputeThumbnailExpandTarget();

                // Restore the real expanded thumbnail
                if (ThumbnailBorder != null) ThumbnailBorder.Opacity = 1;

                // Then hide the animation thumbnail
                AnimationThumbnailBorder.Visibility = Visibility.Collapsed;
                // Clear stale animation values so next time starts clean
                AnimationThumbnailBorder.BeginAnimation(WidthProperty, null);
                AnimationThumbnailBorder.BeginAnimation(HeightProperty, null);
                AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                AnimationThumbnailTranslate.X = 0;
                AnimationThumbnailTranslate.Y = 0;
            }
        };

        NotchBorder.BeginAnimation(WidthProperty, widthAnim);
        NotchBorder.BeginAnimation(HeightProperty, heightAnim);
        CollapsedContent.BeginAnimation(OpacityProperty, fadeOutAnim);
        MusicCompactContent.BeginAnimation(OpacityProperty, fadeOutAnim);
        ExpandedContent.BeginAnimation(OpacityProperty, fadeInAnim);
        HoverGlow.BeginAnimation(OpacityProperty, glowAnim);

        AnimateCornerRadius(_cornerRadiusExpanded, TimeSpan.FromMilliseconds(350));
    }

    private void CollapseNotch()
    {
        if (_isAnimating || !_isExpanded) return;
        _isAnimating = true;
        _progressTimer?.Stop();

        UpdateZOrderTimerInterval();
        EnsureTopmost();

        // Clear prior animations
        ExpandedContent.BeginAnimation(OpacityProperty, null);
        AnimationThumbnailBorder.BeginAnimation(WidthProperty, null);
        AnimationThumbnailBorder.BeginAnimation(HeightProperty, null);
        AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        // Reset translate to prevent stale values from prior animations
        AnimationThumbnailTranslate.X = 0;
        AnimationThumbnailTranslate.Y = 0;

        var widthAnim = MakeAnim(_collapsedWidth, _dur350, _easeExpOut7);
        var heightAnim = MakeAnim(_collapsedHeight, _dur350, _easeExpOut7);

        var fadeOutAnim = MakeAnim(0, _dur80, _easeQuadOut);
        fadeOutAnim.Completed += (s, e) =>
        {
            ExpandedContent.BeginAnimation(OpacityProperty, null);
            ExpandedContent.Opacity = 0;
            ExpandedContent.Visibility = Visibility.Collapsed;
        };

        FrameworkElement contentToShow = _isMusicCompactMode ? MusicCompactContent : CollapsedContent;
        FrameworkElement contentToHide = _isMusicCompactMode ? CollapsedContent : MusicCompactContent;

        contentToHide.BeginAnimation(OpacityProperty, null);
        contentToHide.Visibility = Visibility.Collapsed;
        contentToHide.Opacity = 0;

        var fadeInAnim = MakeAnim(1, _dur200, _easeQuadOut);
        var glowAnim = MakeAnim(0, _dur150);

        // --- Thumbnail Animation Logic ---
        if (_isMusicCompactMode && ThumbnailImage.Source != null)
        {
            // Use cached position or compute dynamically (layout is at expanded size here)
            var (startX, startY) = _cachedThumbnailExpandTarget ?? ComputeThumbnailExpandTarget();
            // Update cache
            _cachedThumbnailExpandTarget = (startX, startY);

            // Setup floating thumbnail from expanded state
            AnimationThumbnailImage.Source = ThumbnailImage.Source;
            AnimationThumbnailBorder.Visibility = Visibility.Visible;
            AnimationThumbnailBorder.Width = 50;
            AnimationThumbnailBorder.Height = 50;
            AnimationThumbnailTranslate.X = startX;
            AnimationThumbnailTranslate.Y = startY;

            // Target: Compact position (back to translate 0,0 = base margin 8,4)
            var thumbWidthAnim = MakeAnim(50, 22, _dur350, _easeExpOut7);
            var thumbHeightAnim = MakeAnim(50, 22, _dur350, _easeExpOut7);
            var thumbTranslateXAnim = MakeAnim(startX, 0, _dur350, _easeExpOut7);
            var thumbTranslateYAnim = MakeAnim(startY, 0, _dur350, _easeExpOut7);

            AnimationThumbnailBorder.BeginAnimation(WidthProperty, thumbWidthAnim);
            AnimationThumbnailBorder.BeginAnimation(HeightProperty, thumbHeightAnim);
            AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.XProperty, thumbTranslateXAnim);
            AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.YProperty, thumbTranslateYAnim);
            
            // Don't hide CompactThumbnailBorder - it will be covered by AnimationThumbnailBorder (ZIndex=100)
            // and will fade in naturally with contentToShow
            if (ThumbnailBorder != null) ThumbnailBorder.Opacity = 0;
        }

        heightAnim.Completed += (s, e) =>
        {
            _isAnimating = false;
            _isExpanded = false;
            UpdateProgressTimerState();
            
            // Cleanup Thumbnail Animation
            if (_isMusicCompactMode)
            {
                // Restore real thumbnails first
                if (CompactThumbnailBorder != null) CompactThumbnailBorder.Opacity = 1;
                if (ThumbnailBorder != null) ThumbnailBorder.Opacity = 1;

                // Then hide animation overlay
                AnimationThumbnailBorder.Visibility = Visibility.Collapsed;
                // Clear stale animation values
                AnimationThumbnailBorder.BeginAnimation(WidthProperty, null);
                AnimationThumbnailBorder.BeginAnimation(HeightProperty, null);
                AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                AnimationThumbnailTranslate.X = 0;
                AnimationThumbnailTranslate.Y = 0;
            }
        };

        NotchBorder.BeginAnimation(WidthProperty, widthAnim);
        NotchBorder.BeginAnimation(HeightProperty, heightAnim);
        ExpandedContent.BeginAnimation(OpacityProperty, fadeOutAnim);

        contentToShow.Visibility = Visibility.Visible;
        contentToShow.BeginAnimation(OpacityProperty, fadeInAnim);
        HoverGlow.BeginAnimation(OpacityProperty, glowAnim);

        AnimateCornerRadius(_cornerRadiusCollapsed, TimeSpan.FromMilliseconds(350));
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

        // Step 1: Fade out Calendar & Controls (cached easing)
        var fadeOutCalendar = MakeAnim(1d, 0d, _dur150, _easePowerIn2, null);
        fadeOutCalendar.Completed += (s, e) => CalendarWidget.Visibility = Visibility.Collapsed;
        CalendarWidget.BeginAnimation(OpacityProperty, fadeOutCalendar);

        var fadeOutControls = MakeAnim(1d, 0d, _dur150, _easePowerIn2, null);
        fadeOutControls.Completed += (s, e) => MediaControls.Visibility = Visibility.Collapsed;
        MediaControls.BeginAnimation(OpacityProperty, fadeOutControls);

        // Step 2: Width & Margin animation
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
        Timeline.SetDesiredFrameRate(widthAnim, 60);

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

        // Step 3: Show inline controls
        InlineControls.Visibility = Visibility.Visible;

        var fadeInInline = MakeAnim(0d, 1d, _dur350, _easeExpOut7, contentDelay);
        InlineControls.BeginAnimation(OpacityProperty, fadeInInline);

        var dur450 = new Duration(TimeSpan.FromMilliseconds(450));
        var scaleXAnim = MakeAnim(0.8d, 1.0d, dur450, _easeSpring, contentDelay);
        var scaleYAnim = MakeAnim(0.8d, 1.0d, dur450, _easeSpring, contentDelay);
        InlineControlsScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
        InlineControlsScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);

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

        // Step 1: Scale down + fade out inline controls (cached easing)
        var scaleDownX = MakeAnim(1.0d, 0.85d, _dur150, _easePowerIn2, null);
        var scaleDownY = MakeAnim(1.0d, 0.85d, _dur150, _easePowerIn2, null);
        InlineControlsScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDownX);
        InlineControlsScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDownY);

        var fadeOutInline = MakeAnim(1d, 0d, _dur150, _easePowerIn2, null);
        fadeOutInline.Completed += (s, e) =>
        {
            InlineControls.Visibility = Visibility.Collapsed;
            InlineControlsScale.ScaleX = 0.8;
            InlineControlsScale.ScaleY = 0.8;
        };
        InlineControls.BeginAnimation(OpacityProperty, fadeOutInline);

        // Step 2: Width shrinking & Margin restore
        double currentWidth = MediaWidgetContainer.ActualWidth;
        double targetSmallWidth = _musicWidgetSmallWidth > 0 ? _musicWidgetSmallWidth : (ExpandedContent.ActualWidth / 3.0) - 8;

        MediaWidgetContainer.Width = currentWidth;
        MediaWidgetContainer.HorizontalAlignment = HorizontalAlignment.Left;

        var widthAnim = new DoubleAnimation(currentWidth, targetSmallWidth, collapseDuration)
        {
            EasingFunction = _easeExpOut7
        };
        Timeline.SetDesiredFrameRate(widthAnim, 60);

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

        // Step 3: Fade in controls
        MediaControls.Visibility = Visibility.Visible;
        var fadeInControls = MakeAnim(0d, 1d, new Duration(TimeSpan.FromMilliseconds(300)), _easePowerOut3, contentDelay);
        MediaControls.BeginAnimation(OpacityProperty, fadeInControls);

        // Step 4: Fade in calendar
        CalendarWidget.Visibility = Visibility.Visible;
        var fadeInCalendar = MakeAnim(0d, 1d, new Duration(TimeSpan.FromMilliseconds(300)), _easePowerOut3, TimeSpan.FromMilliseconds(120));
        CalendarWidget.BeginAnimation(OpacityProperty, fadeInCalendar);
    }

    #endregion

    #region Animation Helpers

    private void FadeSwitch(FrameworkElement from, FrameworkElement to)
    {
        var fadeOut = MakeAnim(0, _dur100);
        fadeOut.Completed += (s, e) => from.Visibility = Visibility.Collapsed;
        from.BeginAnimation(OpacityProperty, fadeOut);

        to.Visibility = Visibility.Visible;
        var fadeIn = MakeAnim(0d, 1d, _dur200, null, null);
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
        // Cancel ongoing animations
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

        // Set initial states
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
            capturedFromIcon.Visibility = Visibility.Collapsed;
            capturedFromTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            capturedFromTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            capturedFromIcon.BeginAnimation(OpacityProperty, null);
            capturedFromTransform.ScaleX = 1;
            capturedFromTransform.ScaleY = 1;
            capturedFromIcon.Opacity = 1;
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
        // All use cached _easeQuadOut
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
            arrow2Transform.BeginAnimation(TranslateTransform.XProperty, null);
            arrow2.BeginAnimation(OpacityProperty, null);
            arrow1Transform.BeginAnimation(TranslateTransform.XProperty, null);
            arrow0Transform.BeginAnimation(TranslateTransform.XProperty, null);
            arrow0.BeginAnimation(OpacityProperty, null);

            arrow2Transform.X = 0;
            arrow2.Opacity = 1;
            arrow1Transform.X = 0;
            arrow0Transform.X = 0;
            arrow0.Opacity = 0;
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
            arrow2Transform.BeginAnimation(TranslateTransform.XProperty, null);
            arrow2.BeginAnimation(OpacityProperty, null);
            arrow1Transform.BeginAnimation(TranslateTransform.XProperty, null);
            arrow0Transform.BeginAnimation(TranslateTransform.XProperty, null);
            arrow0.BeginAnimation(OpacityProperty, null);

            arrow2Transform.X = 0;
            arrow2.Opacity = 1;
            arrow1Transform.X = 0;
            arrow0Transform.X = 0;
            arrow0.Opacity = 0;
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

    private DispatcherTimer? _cornerRadiusTimer;

    private void AnimateCornerRadius(double targetRadius, TimeSpan duration)
    {
        _cornerRadiusTimer?.Stop();

        double startRadius = NotchBorder.CornerRadius.BottomLeft;
        double delta = targetRadius - startRadius;

        if (Math.Abs(delta) < 0.5) return;

        int totalSteps = (int)(duration.TotalMilliseconds / 16);
        int currentStep = 0;

        _cornerRadiusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };

        _cornerRadiusTimer.Tick += (s, e) =>
        {
            currentStep++;
            double progress = (double)currentStep / totalSteps;
            double easedProgress = 1 - Math.Pow(1 - Math.Min(progress, 1), 5);
            double currentRadius = startRadius + delta * easedProgress;

            var cr = new CornerRadius(0, 0, currentRadius, currentRadius);
            NotchBorder.CornerRadius = cr;
            InnerClipBorder.CornerRadius = cr;
            MediaBackground.CornerRadius = cr;
            MediaBackground2.CornerRadius = cr;
            UpdateNotchClip();

            if (currentStep >= totalSteps)
            {
                _cornerRadiusTimer?.Stop();
                var finalCr = new CornerRadius(0, 0, targetRadius, targetRadius);
                NotchBorder.CornerRadius = finalCr;
                InnerClipBorder.CornerRadius = finalCr;
                MediaBackground.CornerRadius = finalCr;
                MediaBackground2.CornerRadius = finalCr;
                UpdateNotchClip();
            }
        };

        _cornerRadiusTimer.Start();
    }

    #endregion
}
