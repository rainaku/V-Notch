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

    private void ResetAnimationThumbnailOverlay(bool clearSource = true)
    {
        AnimationThumbnailBorder.BeginAnimation(WidthProperty, null);
        AnimationThumbnailBorder.BeginAnimation(HeightProperty, null);
        AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        AnimationThumbnailClip.BeginAnimation(RectangleGeometry.RectProperty, null);
        this.BeginAnimation(CurrentThumbnailAnimationRadiusProperty, null);
        CurrentThumbnailAnimationRadius = 6;

        AnimationThumbnailBorder.Visibility = Visibility.Collapsed;
        AnimationThumbnailBorder.Opacity = 0;
        AnimationThumbnailBorder.Width = 22;
        AnimationThumbnailBorder.Height = 22;
        AnimationThumbnailBorder.CornerRadius = new CornerRadius(6);
        AnimationThumbnailClip.Rect = new Rect(0, 0, 22, 22);
        AnimationThumbnailClip.RadiusX = 6;
        AnimationThumbnailClip.RadiusY = 6;
        AnimationThumbnailTranslate.X = 0;
        AnimationThumbnailTranslate.Y = 0;

        if (clearSource)
        {
            AnimationThumbnailImage.Source = null;
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
        bool suppressCompactThumbnailMotion = IsCountdownCompletionVisualActive;
        if (suppressCompactThumbnailMotion)
        {
            SuppressCompactMediaChromeForCountdownCompletion();
        }
        CancelThumbnailSwitchForExpand();

        NotchBorder.BeginAnimation(WidthProperty, null);
        NotchBorder.BeginAnimation(HeightProperty, null);

        _hoverCollapseTimer.Stop();
        _suppressHoverCollapseUntilUtc = DateTime.UtcNow.AddMilliseconds(800);

        if (_isRewindAnimating)
        {
            _isRewindAnimating = false;
            StopRewindTextAnimation();
            ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressBarScale.ScaleX = 0;
            _progressDisplayRatio = 0;
        }

        if (_isVolumeIndicatorActive)
        {
            DismissVolumeIndicatorImmediate();
        }

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
        _compactPillArbiter.ForceClear();

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
        ResetAnimationThumbnailOverlay();
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

        double liveScaleX = NotchScale.ScaleX;
        double liveScaleY = NotchScale.ScaleY;

        NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

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
        ResetAnimationThumbnailOverlay();
        MediaBackground.BeginAnimation(OpacityProperty, null);
        MediaBackground2.BeginAnimation(OpacityProperty, null);

        ExpandedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        CollapsedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        MusicCompactContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);

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

        if (_isMusicCompactMode && CompactThumbnail.Source != null && !suppressCompactThumbnailMotion)
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
                ResetAnimationThumbnailOverlay();
                if (CompactThumbnailBorder != null) CompactThumbnailBorder.Opacity = 1;
                if (ThumbnailBorder != null) ThumbnailBorder.Opacity = 1;
            }
            else
            {
                AnimationThumbnailImage.Source = CompactThumbnail.Source;
                AnimationThumbnailBorder.Visibility = Visibility.Visible;
                AnimationThumbnailBorder.Opacity = 1;
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

            // ─── Restore UI element opacity after expand completes ───
            RestoreExpandedContentOpacity();

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

            ResetAnimationThumbnailOverlay();

            if (_isMusicCompactMode)
            {
                if (TryComputeThumbnailExpandTarget(out var updatedTarget))
                {
                    _cachedThumbnailExpandTarget = updatedTarget;
                }
            }

            // Always restore opacity — it may have been set to 0 during expand animation even if _isMusicCompactMode changed during the animation
            if (ThumbnailBorder != null) ThumbnailBorder.Opacity = 1;
            if (CompactThumbnailBorder != null && !_isClipboardPeekActive && !suppressCompactThumbnailMotion)
            {
                CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
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
            else if (suppressCompactThumbnailMotion)
            {
                SuppressCompactMediaChromeForCountdownCompletion();
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
        if (_isSecondaryView)
        {
            StopCameraPreviewForViewExit();
        }
        _isAnimating = true;
        _notchState.TryTransitionTo(NotchState.Collapsing);
        bool suppressCompactThumbnailMotion = IsCountdownCompletionVisualActive;
        if (suppressCompactThumbnailMotion)
        {
            SuppressCompactMediaChromeForCountdownCompletion();
        }
        CancelThumbnailSwitchAnimations();

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

        // ─── Apple-Style: Fade out UI elements with staggered timing BEFORE notch shrinks ───
        AnimateExpandedContentFadeOut();

        // Hide Status Bar (Battery + Settings)
        AnimateStatusBarReveal(false);

        // Immediately hide nav icons to prevent them staying visible during collapse
        NavIconsBackground.BeginAnimation(OpacityProperty, null);
        NavIconsBackground.Opacity = 0;
        NavIconsBackground.Visibility = Visibility.Collapsed;
        NavIconsPanel.BeginAnimation(OpacityProperty, null);
        NavIconsPanel.Opacity = 0;
        NavIconsPanel.Visibility = Visibility.Collapsed;

        // If collapsing from secondary/timer view, animate that surface out while the notch returns to compact.
        bool wasSecondary = _isSecondaryView;
        bool wasTimer = _isTimerView;
        if (wasSecondary)
        {
            if (IsCameraPreviewLifecycleActive)
            {
                StopCameraPreviewForViewExit();
            }

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

        if (wasTimer)
        {
            if (IsCameraPreviewLifecycleActive)
            {
                StopCameraPreviewForViewExit();
            }

            // ─── Apple-Style: Fade out timer elements with staggered timing ───
            AnimateTimerContentFadeOut();

            TimerContent.BeginAnimation(OpacityProperty, null);

            var timerGroup = new TransformGroup();
            var timerScale = new ScaleTransform(1, 1);
            var timerTranslate = new TranslateTransform(0, 0);
            timerGroup.Children.Add(timerScale);
            timerGroup.Children.Add(timerTranslate);
            TimerContent.RenderTransform = timerGroup;
            TimerContent.RenderTransformOrigin = new Point(0.5, 0.5);

            // Delay container fade to let internal elements fade first
            var timerFadeOut = MakeAnim(TimerContent.Opacity, 0, _dur200, _easeQuadIn, TimeSpan.FromMilliseconds(60));
            var timerSlideDown = MakeAnim(0, 12, _dur250, _easeQuadIn, TimeSpan.FromMilliseconds(60));
            var timerScaleDown = MakeAnim(1, 0.95, _dur250, _easeQuadIn, TimeSpan.FromMilliseconds(60));
            var timerBlur = TimerContent.Effect as BlurEffect ?? new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
            TimerContent.Effect = timerBlur;
            var timerBlurOut = MakeAnim(timerBlur.Radius, 10, _dur200, _easeQuadIn, TimeSpan.FromMilliseconds(60));
            Timeline.SetDesiredFrameRate(timerFadeOut, 144);
            Timeline.SetDesiredFrameRate(timerSlideDown, 144);
            Timeline.SetDesiredFrameRate(timerScaleDown, 144);

            timerFadeOut.Completed += (s, e) =>
            {
                TimerContent.BeginAnimation(OpacityProperty, null);
                TimerContent.Opacity = 0;
                TimerContent.Visibility = Visibility.Collapsed;
                TimerContent.RenderTransform = null;
                TimerContent.Effect = null;
                timerBlur.Radius = 0;
            };

            TimerContent.BeginAnimation(OpacityProperty, timerFadeOut);
            timerTranslate.BeginAnimation(TranslateTransform.YProperty, timerSlideDown);
            timerScale.BeginAnimation(ScaleTransform.ScaleXProperty, timerScaleDown);
            timerScale.BeginAnimation(ScaleTransform.ScaleYProperty, timerScaleDown);
            timerBlur.BeginAnimation(BlurEffect.RadiusProperty, timerBlurOut);
        }

        ExpandedContent.BeginAnimation(OpacityProperty, null);
        MusicCompactContent.BeginAnimation(OpacityProperty, null);
        ResetCalendarScroll();
        ResetCalendarHoverFocusVisualState();
        CollapsedContent.BeginAnimation(OpacityProperty, null);
        ResetAnimationThumbnailOverlay();

        // Cancel any in-progress corner radius animation to prevent jitter
        this.BeginAnimation(CurrentCornerRadiusProperty, null);

        ExpandedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        CollapsedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        MusicCompactContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);

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
                if (!wasTimer)
                {
                    TimerContent.Visibility = Visibility.Collapsed;
                    TimerContent.Opacity = 0;
                }
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

        if (_isMusicCompactMode && ThumbnailImage.Source != null && !suppressCompactThumbnailMotion)
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
                AnimationThumbnailBorder.Opacity = 1;
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

                ResetAnimationThumbnailOverlay();

                CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                CompactThumbnailScale.ScaleX = 1.0;
                CompactThumbnailScale.ScaleY = 1.0;
                CompactHoverInfo.BeginAnimation(OpacityProperty, null);
                CompactHoverInfo.Opacity = 0;
                CompactHoverInfo.Visibility = Visibility.Collapsed;
            }

            if (CompactThumbnailBorder != null && !_isClipboardPeekActive && !_isVolumeIndicatorActive && !suppressCompactThumbnailMotion)
            {
                CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
                CompactThumbnailBorder.Visibility = Visibility.Visible;
                CompactThumbnailBorder.Opacity = 1;
            }
            else if (suppressCompactThumbnailMotion)
            {
                SuppressCompactMediaChromeForCountdownCompletion();
            }
            if (ThumbnailBorder != null) ThumbnailBorder.Opacity = 1;

            // Ensure MusicViz is properly positioned after collapse
            if (_isMusicCompactMode && _currentMediaInfo?.IsPlaying == true && !_isClipboardPeekActive && !_isVolumeIndicatorActive && !suppressCompactThumbnailMotion)
            {
                ShowMusicVisualizer(animate: false);
            }

            // Force layout update to fix any positioning drift from width animation
            MusicCompactContent.InvalidateArrange();
            MusicCompactContent.UpdateLayout();

            if (_pendingFlipThumbnail != null && !suppressCompactThumbnailMotion)
            {
                var thumb = _pendingFlipThumbnail;
                _pendingFlipThumbnail = null;
                AnimateThumbnailSwitchOnly(thumb, force: true);
            }
            else if (suppressCompactThumbnailMotion)
            {
                _pendingFlipThumbnail = null;
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
