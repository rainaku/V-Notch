using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Media.Effects;
using VNotch.Controls;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{

    #region Notch Expand/Collapse

    private bool TryComputeThumbnailExpandTarget(out (double X, double Y) target)
    {
        target = default;
        try
        {
            if (ThumbnailBorder == null || InnerClipBorder == null) return false;
            if (!ThumbnailBorder.IsLoaded || !InnerClipBorder.IsLoaded) return false;
            if (ThumbnailBorder.ActualWidth <= 0 || ThumbnailBorder.ActualHeight <= 0) return false;
            if (InnerClipBorder.ActualWidth <= 0 || InnerClipBorder.ActualHeight <= 0) return false;

            var thumbPos = ThumbnailBorder.TransformToAncestor(InnerClipBorder).Transform(new Point(0, 0));

            double targetX = thumbPos.X - 8;
            double targetY = thumbPos.Y - 4;
            if (double.IsNaN(targetX) || double.IsInfinity(targetX) ||
                double.IsNaN(targetY) || double.IsInfinity(targetY))
            {
                return false;
            }

            if (Math.Abs(targetX) > 2000 || Math.Abs(targetY) > 2000) return false;

            target = (targetX, targetY);
            return true;
        }
        catch
        {
            return false;
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
        if (_isAnimating || _isExpanded || _isGreetingActive) return;
        _isAnimating = true;
        _notchState.TryTransitionTo(NotchState.Expanding);
        CancelThumbnailSwitchForExpand();

        // Cancel any competing width/height animations on NotchBorder (e.g. from
        // UpdateMusicCompactMode or CollapseAfterGreeting) to prevent two animations
        // fighting over the same property simultaneously.
        NotchBorder.BeginAnimation(WidthProperty, null);
        NotchBorder.BeginAnimation(HeightProperty, null);

        // Suppress hover-collapse during expand animation and shortly after.
        // WPF can fire spurious MouseLeave events during layout recalculation
        // when the NotchBorder resizes, causing the collapse timer to start
        // while the user is still hovering over the notch.
        _hoverCollapseTimer.Stop();
        _suppressHoverCollapseUntilUtc = DateTime.UtcNow.AddMilliseconds(800);

        // Cancel any in-progress rewind animation immediately so the user doesn't
        // see the progress bar animating backward while the notch is expanding.
        if (_isRewindAnimating)
        {
            _isRewindAnimating = false;
            StopRewindTextAnimation();
            ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressBarScale.ScaleX = 0;
            _progressDisplayRatio = 0;
        }

        // Immediately dismiss any active notification overlays to prevent them
        // lingering over the expanded content or persisting through collapse
        if (_isChargingNotificationVisible)
        {
            _chargingNotificationDismissTimer?.Stop();
            ChargingNotification.BeginAnimation(OpacityProperty, null);
            ChargingNotification.Opacity = 0;
            ChargingNotification.Visibility = Visibility.Collapsed;
            _isChargingNotificationVisible = false;
        }
        if (_isBluetoothNotificationVisible)
        {
            _bluetoothController.MarkDismissed();
            BluetoothNotification.BeginAnimation(OpacityProperty, null);
            BluetoothNotification.Opacity = 0;
            BluetoothNotification.Visibility = Visibility.Collapsed;
            BluetoothDisconnectNotification.BeginAnimation(OpacityProperty, null);
            BluetoothDisconnectNotification.Opacity = 0;
            BluetoothDisconnectNotification.Visibility = Visibility.Collapsed;
            _isBluetoothNotificationVisible = false;
        }
        // Surrender any compact-pill slot so a stale Completed handler from
        // a transient overlay can't fight the expand animation.
        _compactPillArbiter.ForceClear();

        // Stop hover state tracking but DON'T reset thumbnail scale —
        // let it fade out naturally with the collapsed content
        bool wasHovered = _isCompactThumbnailHovered;
        if (_isCompactThumbnailHovered)
        {
            _isCompactThumbnailHovered = false;
            _compactThumbnailHoverLeaveTimer.Stop();
        }
        // Hide hover info immediately (title text below thumbnail)
        CompactHoverInfo.BeginAnimation(OpacityProperty, null);
        CompactHoverInfo.Opacity = 0;
        CompactHoverInfo.Visibility = Visibility.Collapsed;
        // Ensure animation thumbnail overlay is hidden initially
        AnimationThumbnailBorder.Visibility = Visibility.Collapsed;
        // Reset compact thumbnail corner radius from hover state
        this.BeginAnimation(CurrentCompactThumbnailRadiusProperty, null);
        CurrentCompactThumbnailRadius = 6;

        UpdateZOrderTimerInterval();
        EnsureTopmost();

        // Capture current visual size before cancelling hover animations (prevents snap-back jitter)
        double currentWidth = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _collapsedWidth;
        double currentHeight = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _collapsedHeight;

        // Cancel hover animations on notch size and corner radius
        NotchBorder.BeginAnimation(WidthProperty, null);
        NotchBorder.BeginAnimation(HeightProperty, null);
        this.BeginAnimation(CurrentCornerRadiusProperty, null);

        // Set local value to current visual size so expand animation starts from here (no snap)
        NotchBorder.Width = currentWidth;
        NotchBorder.Height = currentHeight;

        // Capture current animated scale before cancelling — if a track-change bounce
        // is mid-flight, snapping to 1.0 causes a visible reverse-jump.
        double liveScaleX = NotchScale.ScaleX;
        double liveScaleY = NotchScale.ScaleY;

        NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        // If the scale was mid-bounce (not at 1.0), animate it smoothly to 1.0
        // instead of snapping — prevents the visual "reverse" glitch.
        bool wasBouncing = Math.Abs(liveScaleX - 1.0) > 0.005 || Math.Abs(liveScaleY - 1.0) > 0.005;
        if (wasBouncing)
        {
            NotchScale.ScaleX = liveScaleX;
            NotchScale.ScaleY = liveScaleY;
            NotchShadowScale.ScaleX = liveScaleX;
            NotchShadowScale.ScaleY = liveScaleY;

            var settleX = MakeAnim(liveScaleX, 1.0, _dur200, _easeQuadOut);
            var settleY = MakeAnim(liveScaleY, 1.0, _dur200, _easeQuadOut);
            settleX.Completed += (s, e) =>
            {
                NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                NotchScale.ScaleX = 1.0;
                NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                NotchShadowScale.ScaleX = 1.0;
            };
            settleY.Completed += (s, e) =>
            {
                NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                NotchScale.ScaleY = 1.0;
                NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                NotchShadowScale.ScaleY = 1.0;
            };
            NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, settleX);
            NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, settleY);
            NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, settleX);
            NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, settleY);
        }
        else
        {
            NotchScale.ScaleX = 1.0;
            NotchScale.ScaleY = 1.0;
            NotchShadowScale.ScaleX = 1.0;
            NotchShadowScale.ScaleY = 1.0;
        }

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

        ExpandedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        CollapsedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        MusicCompactContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);

        AnimationThumbnailTranslate.X = 0;
        AnimationThumbnailTranslate.Y = 0;
        MediaBackground.Opacity = 0;
        MediaBackground2.Opacity = 0;

        SecondaryContent.Visibility = Visibility.Collapsed;
        TimerContent.Visibility = Visibility.Collapsed;

        ExpandedContent.Opacity = 0;
        ExpandedContent.Visibility = Visibility.Visible;

        // Hide lyrics blur during expand to prevent visual artifacts, will show after completion
        if (_isLyricsActive && LyricsBlurBackground != null)
        {
            LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
            LyricsBlurBackground.Opacity = 0;
            LyricsBlurBackground.Visibility = Visibility.Collapsed;
        }
        // Force layout pass so ThumbnailBorder gets actual dimensions for target compute
        ExpandedContent.Width = _expandedWidth - 16;
        ExpandedContent.Height = _expandedHeight - 2;
        ExpandedContent.UpdateLayout();

        // Animate Status Bar (Battery + Settings) reveal
        AnimateStatusBarReveal(true);

        NotchBorder.IsHitTestVisible = false;
        var animFps = 144;

        var widthAnim = MakeAnim(_expandedWidth, _dur600, _easeExpOut6, animFps);
        var heightAnim = MakeAnim(_expandedHeight, _dur600, _easeExpOut6, animFps);
        var fadeOutAnim = MakeAnim(0, _dur200, _easeQuadOut);

        var expandedGroup = new TransformGroup();
        var expandedTranslate = new TranslateTransform(0, 10);
        expandedGroup.Children.Add(expandedTranslate);
        ExpandedContent.RenderTransform = expandedGroup;
        ExpandedContent.RenderTransformOrigin = new Point(0.5, 0.4);

        var fadeInAnim = MakeAnim(0d, 1d, _dur400, _easePowerOut3);
        var springSlide = MakeAnim(10, 0, _dur400, _easeExpOut6);

        var glowAnim = MakeAnim(0.15, _dur200);

        var blurOutAnim = MakeAnim(0, 24, _dur350, _easeQuadIn);
        var blurInAnim = MakeAnim(24, 0, _dur500, _easePowerOut3);
        ExpandedContentBlur.Radius = 24;

        if (_isMusicCompactMode && CompactThumbnail.Source != null)
        {
            var cachedExpandTarget = _cachedThumbnailExpandTarget;
            if (!cachedExpandTarget.HasValue && TryComputeThumbnailExpandTarget(out var computedTarget))
            {
                _cachedThumbnailExpandTarget = computedTarget;
                cachedExpandTarget = computedTarget;
            }

            if (!cachedExpandTarget.HasValue)
            {
                // No animation overlay possible — keep compact thumbnail visible until expand completes, then expanded view takes over.
                AnimationThumbnailBorder.Visibility = Visibility.Collapsed;
                if (CompactThumbnailBorder != null) CompactThumbnailBorder.Opacity = 1;
                if (ThumbnailBorder != null) ThumbnailBorder.Opacity = 1;
            }
            else
            {
                AnimationThumbnailImage.Source = CompactThumbnail.Source;
                AnimationThumbnailBorder.Visibility = Visibility.Visible;
                AnimationThumbnailBorder.CornerRadius = new CornerRadius(6);
                AnimationThumbnailClip.RadiusX = 6;
                AnimationThumbnailClip.RadiusY = 6;
                AnimationThumbnailBorder.Width = 22;
                AnimationThumbnailBorder.Height = 22;
                AnimationThumbnailClip.Rect = new Rect(0, 0, 22, 22);
                AnimationThumbnailTranslate.X = 0;
                AnimationThumbnailTranslate.Y = 0;

                var (targetX, targetY) = cachedExpandTarget.Value;

                var thumbDelay = TimeSpan.FromMilliseconds(40);
                var thumbDur = _dur600;
                var thumbEase = _easeThumbSpring;
                var thumbFps = 144;

                if (_cachedThumbWidthExpand == null || _cachedThumbWidthExpand.Duration != thumbDur)
                {
        _cachedThumbWidthExpand = MakeAnim(22, 102, thumbDur, thumbEase, thumbDelay);
        _cachedThumbHeightExpand = MakeAnim(22, 102, thumbDur, thumbEase, thumbDelay);
                    Timeline.SetDesiredFrameRate(_cachedThumbWidthExpand, thumbFps);
                    Timeline.SetDesiredFrameRate(_cachedThumbHeightExpand, thumbFps);

        _cachedThumbRectExpand = new RectAnimation(new Rect(0, 0, 22, 22), new Rect(0, 0, 102, 102), thumbDur)
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
                AnimateThumbnailAnimationRadius(6, 14, thumbDur, _easeExpOut6, thumbDelay);

                AnimationThumbnailClip.BeginAnimation(RectangleGeometry.RectProperty, _cachedThumbRectExpand);

                if (CompactThumbnailBorder != null)
                {
                    CompactThumbnailBorder.Opacity = 0;
                    CompactThumbnailBorder.Visibility = Visibility.Collapsed;
                }
                if (ThumbnailBorder != null) ThumbnailBorder.Opacity = 0;
            }
        }

        heightAnim.Completed += (s, e) =>
        {
            _isAnimating = false;
            _isExpanded = true;
            _notchState.TryTransitionTo(NotchState.Expanded);
            NotchBorder.IsHitTestVisible = true;
            UpdateProgressTimerState();
            UpdateBatteryInfo();
            UpdateCalendarInfo();
            ShowMediaBackground();

            // Show lyrics blur now that layout is stable
            if (_isLyricsActive && LyricsBlurBackground != null)
            {
                LyricsBlurImage.BeginAnimation(OpacityProperty, null);
                LyricsBlurImage.Opacity = 1;
                LyricsBlurBackground.Visibility = Visibility.Visible;
                LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
                var fadeIn = new DoubleAnimation(0, 0.55, new Duration(TimeSpan.FromMilliseconds(250)))
                {
                    EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseOut }
                };
                LyricsBlurBackground.BeginAnimation(OpacityProperty, fadeIn);
            }

            // Immediately sync lyrics display to current position (skip placeholder if already past first lyric)
            if (_isLyricsActive)
            {
                UpdateLyricsDisplay();
            }
            
            // Start progress bar catch-up animation BEFORE RenderProgressBar to prevent the snap-to-position that would set _progressDisplayRatio > 0 and cause StartProgressCatchUpAnimation to bail out
            StartProgressCatchUpAnimation();
            RenderProgressBar();

            // Play queued thumbnail flip animation
            // force:true is required — early-exit guards (e.g. _thumbnailShownForCurrentTrack)
            // would otherwise silently skip this morph and the user sees a snap-jump.
            if (_pendingFlipThumbnail != null)
            {
                var thumb = _pendingFlipThumbnail;
                _pendingFlipThumbnail = null;
                AnimateThumbnailSwitchOnly(thumb, force: true);
            }

            ExpandedContent.Opacity = 1;
            ExpandedContent.BeginAnimation(OpacityProperty, null);

            ExpandedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
            ExpandedContentBlur.Radius = 0;
            CollapsedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
            CollapsedContentBlur.Radius = 0;
            MusicCompactContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
            MusicCompactContentBlur.Radius = 0;

            AnimationThumbnailBorder.Visibility = Visibility.Collapsed;
            AnimationThumbnailBorder.BeginAnimation(WidthProperty, null);
            AnimationThumbnailBorder.BeginAnimation(HeightProperty, null);
            AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            AnimationThumbnailClip.BeginAnimation(RectangleGeometry.RectProperty, null);
            this.BeginAnimation(CurrentThumbnailAnimationRadiusProperty, null);
            AnimationThumbnailTranslate.X = 0;
            AnimationThumbnailTranslate.Y = 0;

            if (_isMusicCompactMode)
            {
                if (TryComputeThumbnailExpandTarget(out var updatedTarget))
                {
                    _cachedThumbnailExpandTarget = updatedTarget;
                }
            }

            // Always restore opacity — it may have been set to 0 during expand animation even if _isMusicCompactMode changed during the animation
            if (ThumbnailBorder != null) ThumbnailBorder.Opacity = 1;
            if (CompactThumbnailBorder != null && !_isClipboardPeekActive)
            {
                CompactThumbnailBorder.Visibility = Visibility.Visible;
                CompactThumbnailBorder.Opacity = 1;
                // Reset thumbnail scale after expand completes (was left at hover scale during transition)
                CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                CompactThumbnailScale.ScaleX = 1.0;
                CompactThumbnailScale.ScaleY = 1.0;

                // Reset compact thumbnail morph state (may have been left mid-morph by CancelThumbnailSwitchForExpand)
                CompactThumbnailOutScale.ScaleX = 1.0;
                CompactThumbnailOutScale.ScaleY = 1.0;
                CompactThumbnailOutBlur.Radius = 0.0;
                CompactThumbnail.Opacity = 1.0;
            }

            CollapsedContent.Visibility = Visibility.Collapsed;
            MusicCompactContent.Visibility = Visibility.Collapsed;
        };

        NotchBorder.BeginAnimation(WidthProperty, widthAnim);
        NotchBorder.BeginAnimation(HeightProperty, heightAnim);
        CollapsedContent.BeginAnimation(OpacityProperty, fadeOutAnim);
        MusicCompactContent.BeginAnimation(OpacityProperty, fadeOutAnim);

        CollapsedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, blurOutAnim);
        MusicCompactContentBlur.BeginAnimation(BlurEffect.RadiusProperty, blurOutAnim);

        ExpandedContent.BeginAnimation(OpacityProperty, fadeInAnim);
        expandedTranslate.BeginAnimation(TranslateTransform.YProperty, springSlide);

        ExpandedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, blurInAnim);

        HoverGlow.BeginAnimation(OpacityProperty, glowAnim);
        AnimateCornerRadius(_cornerRadiusExpanded, TimeSpan.FromMilliseconds(400));
    }

    private void CollapseNotch()
    {
        if (_isAnimating || !_isExpanded || _isGreetingActive) return;
        _isAnimating = true;
        _notchState.TryTransitionTo(NotchState.Collapsing);
        CancelThumbnailSwitchAnimations();

        // Cancel any in-progress rewind animation to prevent it continuing
        // invisibly during collapse and causing state drift.
        if (_isRewindAnimating)
        {
            _isRewindAnimating = false;
            StopRewindTextAnimation();
            ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressBarScale.ScaleX = 0;
            _progressDisplayRatio = 0;
        }

        UpdateZOrderTimerInterval();
        EnsureTopmost();

        // Hide Status Bar (Battery + Settings)
        AnimateStatusBarReveal(false);

        // Immediately hide nav icons to prevent them staying visible during collapse
        NavIconsBackground.BeginAnimation(OpacityProperty, null);
        NavIconsBackground.Opacity = 0;
        NavIconsBackground.Visibility = Visibility.Collapsed;
        NavIconsPanel.BeginAnimation(OpacityProperty, null);
        NavIconsPanel.Opacity = 0;
        NavIconsPanel.Visibility = Visibility.Collapsed;

        // If collapsing from secondary view, animate it out with a slide-down + fade
        bool wasSecondary = _isSecondaryView;
        if (wasSecondary)
        {
            SecondaryContent.BeginAnimation(OpacityProperty, null);
            var secondaryGroup = new TransformGroup();
            var secondaryScale = new ScaleTransform(1, 1);
            var secondaryTranslate = new TranslateTransform(0, 0);
            secondaryGroup.Children.Add(secondaryScale);
            secondaryGroup.Children.Add(secondaryTranslate);
            SecondaryContent.RenderTransform = secondaryGroup;
            SecondaryContent.RenderTransformOrigin = new Point(0.5, 0.5);

            var secFadeOut = MakeAnim(1, 0, _dur200, _easeQuadIn);
            var secSlideDown = MakeAnim(0, 16, _dur250, _easeQuadIn);
            var secScaleDown = MakeAnim(1, 0.93, _dur250, _easeQuadIn);
            Timeline.SetDesiredFrameRate(secSlideDown, 144);
            Timeline.SetDesiredFrameRate(secScaleDown, 144);

            secFadeOut.Completed += (s, e) =>
            {
                SecondaryContent.BeginAnimation(OpacityProperty, null);
                SecondaryContent.Opacity = 0;
                SecondaryContent.Visibility = Visibility.Collapsed;
                SecondaryContent.RenderTransform = null;
                TimerContent.Visibility = Visibility.Collapsed;
                TimerContent.Opacity = 0;
            };

            SecondaryContent.BeginAnimation(OpacityProperty, secFadeOut);
            secondaryTranslate.BeginAnimation(TranslateTransform.YProperty, secSlideDown);
            secondaryScale.BeginAnimation(ScaleTransform.ScaleXProperty, secScaleDown);
            secondaryScale.BeginAnimation(ScaleTransform.ScaleYProperty, secScaleDown);

            _isSecondaryView = false;
            _isTimerView = false;
        }
        else
        {
            SecondaryContent.BeginAnimation(OpacityProperty, null);
            _isTimerView = false;
        }

        ExpandedContent.BeginAnimation(OpacityProperty, null);
        MusicCompactContent.BeginAnimation(OpacityProperty, null);
        ResetCalendarScroll();
        ResetCalendarHoverFocusVisualState();
        CollapsedContent.BeginAnimation(OpacityProperty, null);
        AnimationThumbnailBorder.BeginAnimation(WidthProperty, null);
        AnimationThumbnailBorder.BeginAnimation(HeightProperty, null);
        AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.YProperty, null);

        // Cancel any in-progress corner radius animation to prevent jitter
        this.BeginAnimation(CurrentCornerRadiusProperty, null);

        ExpandedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        CollapsedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        MusicCompactContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);

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

            if (!wasSecondary)
            {
                SecondaryContent.BeginAnimation(OpacityProperty, null);
                SecondaryContent.Opacity = 0;
                SecondaryContent.Visibility = Visibility.Collapsed;
                SecondaryContent.RenderTransform = null;
                TimerContent.Visibility = Visibility.Collapsed;
                TimerContent.Opacity = 0;
            }
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

        var blurOutAnim = MakeAnim(0, 24, _dur350, _easeQuadIn);
        var blurInAnim = MakeAnim(24, 0, _dur500, _easePowerOut3);
        
        CollapsedContentBlur.Radius = 24;
        MusicCompactContentBlur.Radius = 24;

        if (_isMusicCompactMode && ThumbnailImage.Source != null)
        {
            // Hide both real thumbnails immediately to prevent double-thumbnail during crossfade
            if (CompactThumbnailBorder != null)
            {
                CompactThumbnailBorder.Opacity = 0;
                CompactThumbnailBorder.Visibility = Visibility.Collapsed;
            }
            if (ThumbnailBorder != null) ThumbnailBorder.Opacity = 0;

            if (!_cachedThumbnailExpandTarget.HasValue &&
                TryComputeThumbnailExpandTarget(out var measuredTarget))
            {
                _cachedThumbnailExpandTarget = measuredTarget;
            }

            var cachedExpandTarget = _cachedThumbnailExpandTarget;
            if (cachedExpandTarget.HasValue)
            {
                var (startX, startY) = cachedExpandTarget.Value;

                AnimationThumbnailImage.Source = ThumbnailImage.Source;
                AnimationThumbnailBorder.Visibility = Visibility.Visible;
                AnimationThumbnailBorder.CornerRadius = new CornerRadius(14);
                AnimationThumbnailClip.RadiusX = 14;
                AnimationThumbnailClip.RadiusY = 14;
        AnimationThumbnailBorder.Width = 102;
        AnimationThumbnailBorder.Height = 102;
        AnimationThumbnailClip.Rect = new Rect(0, 0, 102, 102);
                AnimationThumbnailTranslate.X = startX;
                AnimationThumbnailTranslate.Y = startY;

                var thumbDelay = TimeSpan.FromMilliseconds(30);
                var thumbDur = _dur500;
                var thumbEase = _easeThumbSpring;
                var thumbFps = 144;

                if (_cachedThumbWidthCollapse == null || _cachedThumbWidthCollapse.Duration != thumbDur)
                {
        _cachedThumbWidthCollapse = MakeAnim(102, 22, thumbDur, thumbEase, thumbDelay);
        _cachedThumbHeightCollapse = MakeAnim(102, 22, thumbDur, thumbEase, thumbDelay);
                    Timeline.SetDesiredFrameRate(_cachedThumbWidthCollapse, thumbFps);
                    Timeline.SetDesiredFrameRate(_cachedThumbHeightCollapse, thumbFps);

        _cachedThumbRectCollapse = new RectAnimation(new Rect(0, 0, 102, 102), new Rect(0, 0, 22, 22), thumbDur)
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
                AnimateThumbnailAnimationRadius(14, 6, thumbDur, _easeExpOut6, thumbDelay);

                AnimationThumbnailClip.BeginAnimation(RectangleGeometry.RectProperty, _cachedThumbRectCollapse);
            }
        }

        heightAnim.Completed += (s, e) =>
        {
            _isAnimating = false;
            _isExpanded = false;
            _notchState.TryTransitionTo(NotchState.Collapsed);
            NotchBorder.IsHitTestVisible = true;
            UpdateProgressTimerState();

            contentToShow.RenderTransform = null;

            // Safety: ensure notification overlays are hidden after collapse to prevent
            // them overlapping with the restored collapsed content (thumbnail + equalizer)
            if (_isChargingNotificationVisible)
            {
                _chargingNotificationDismissTimer?.Stop();
                ChargingNotification.BeginAnimation(OpacityProperty, null);
                ChargingNotification.Opacity = 0;
                ChargingNotification.Visibility = Visibility.Collapsed;
                _isChargingNotificationVisible = false;
            }
            if (_isBluetoothNotificationVisible)
            {
                _bluetoothController.MarkDismissed();
                BluetoothNotification.BeginAnimation(OpacityProperty, null);
                BluetoothNotification.Opacity = 0;
                BluetoothNotification.Visibility = Visibility.Collapsed;
                BluetoothDisconnectNotification.BeginAnimation(OpacityProperty, null);
                BluetoothDisconnectNotification.Opacity = 0;
                BluetoothDisconnectNotification.Visibility = Visibility.Collapsed;
                _isBluetoothNotificationVisible = false;
            }
            // Drop any compact-pill slot — the expanded view will repaint freshly.
            _compactPillArbiter.ForceClear();

            // Safety: ensure nav icons are always hidden in collapsed state
            NavIconsPanel.BeginAnimation(OpacityProperty, null);
            NavIconsPanel.Opacity = 0;
            NavIconsPanel.Visibility = Visibility.Collapsed;
            NavIconsBackground.BeginAnimation(OpacityProperty, null);
            NavIconsBackground.Opacity = 0;
            NavIconsBackground.Visibility = Visibility.Collapsed;

            ExpandedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
            ExpandedContentBlur.Radius = 0;
            CollapsedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
            CollapsedContentBlur.Radius = 0;
            MusicCompactContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
            MusicCompactContentBlur.Radius = 0;

            if (_isMusicCompactMode)
            {
                contentToShow.Opacity = 1;
                contentToShow.BeginAnimation(OpacityProperty, null);

                AnimationThumbnailBorder.Visibility = Visibility.Collapsed;
                AnimationThumbnailBorder.BeginAnimation(WidthProperty, null);
                AnimationThumbnailBorder.BeginAnimation(HeightProperty, null);
                AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                AnimationThumbnailClip.BeginAnimation(RectangleGeometry.RectProperty, null);
                this.BeginAnimation(CurrentThumbnailAnimationRadiusProperty, null);
                AnimationThumbnailTranslate.X = 0;
                AnimationThumbnailTranslate.Y = 0;

                CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                CompactThumbnailScale.ScaleX = 1.0;
                CompactThumbnailScale.ScaleY = 1.0;
                CompactHoverInfo.BeginAnimation(OpacityProperty, null);
                CompactHoverInfo.Opacity = 0;
                CompactHoverInfo.Visibility = Visibility.Collapsed;
            }

            // Always restore opacity — may have been set to 0 during collapse animation
            if (CompactThumbnailBorder != null && !_isClipboardPeekActive)
            {
                CompactThumbnailBorder.Visibility = Visibility.Visible;
                CompactThumbnailBorder.Opacity = 1;
            }
            if (ThumbnailBorder != null) ThumbnailBorder.Opacity = 1;

            // Ensure MusicViz is properly positioned after collapse
            MusicViz.BeginAnimation(OpacityProperty, null);
            if (_isMusicCompactMode && _currentMediaInfo?.IsPlaying == true && !_isClipboardPeekActive)
            {
                MusicViz.Visibility = Visibility.Visible;
                MusicViz.Opacity = 1;
            }

            // Force layout update to fix any positioning drift from width animation
            MusicCompactContent.InvalidateArrange();
            MusicCompactContent.UpdateLayout();

            // Play queued thumbnail flip animation (track changed mid-collapse)
            // force:true is required — see expand-completion comment for details.
            if (_pendingFlipThumbnail != null)
            {
                var thumb = _pendingFlipThumbnail;
                _pendingFlipThumbnail = null;
                AnimateThumbnailSwitchOnly(thumb, force: true);
            }

        };

        NotchBorder.BeginAnimation(WidthProperty, widthAnim);
        NotchBorder.BeginAnimation(HeightProperty, heightAnim);

        ExpandedContent.BeginAnimation(OpacityProperty, fadeOutAnim);
        expandedTranslate.BeginAnimation(TranslateTransform.YProperty, slideOutAnim);

        ExpandedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, blurOutAnim);

        if (SecondaryContent.Visibility == Visibility.Visible)
        {
            SecondaryContent.BeginAnimation(OpacityProperty, fadeOutAnim);

        }

        contentToShow.Visibility = Visibility.Visible;
        contentToShow.BeginAnimation(OpacityProperty, fadeInAnim);
        showScale.BeginAnimation(ScaleTransform.ScaleXProperty, springShow);
        showScale.BeginAnimation(ScaleTransform.ScaleYProperty, springShow);

        var compactBlurTarget = _isMusicCompactMode ? MusicCompactContentBlur : CollapsedContentBlur;
        compactBlurTarget.BeginAnimation(BlurEffect.RadiusProperty, blurInAnim);

        HoverGlow.BeginAnimation(OpacityProperty, glowAnim);
        AnimateCornerRadius(_cornerRadiusCollapsed, TimeSpan.FromMilliseconds(400));
    }

    #endregion

}
