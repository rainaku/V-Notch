using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private bool _isAudioView
    {
        get => _notchState.IsAudioView;
        set => _notchState.IsAudioView = value;
    }
    private const double _audioViewWidth = 720;

    // The Sound view height adapts to its content: it shrinks to fit when only a few rows are
    // shown (no dead space at the bottom) and grows up to _audioViewMaxHeight, after which the
    // list scrolls. _audioViewHeight holds the current fitted notch height and is recomputed
    // whenever the mixer UI is (re)built or a section is collapsed/expanded. _audioViewChrome is
    // the constant top + bottom space around the scrollable area (AudioContent margins + insets).
    private const double _audioViewMaxHeight = 378;
    private const double _audioViewMinHeight = 150;
    private const double _audioViewChrome = 66;
    private double _audioViewHeight = _audioViewMaxHeight;

    private AudioMixerService? _audioMixerServiceCached;
    private AudioMixerService AudioMixer =>
        _audioMixerServiceCached ??= (AudioMixerService)App.Services.GetService(typeof(AudioMixerService))!;

    // Live volume polling while the Sound view is open.
    private System.Windows.Threading.DispatcherTimer? _audioPollTimer;

    private void StartAudioPoll()
    {
        if (_audioPollTimer == null)
        {
            _audioPollTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _audioPollTimer.Tick += (_, _) => PollAudioVolumes();
        }
        _audioPollTimer.Start();
    }

    private void StopAudioPoll() => _audioPollTimer?.Stop();

    // ─── Scroll edge fades (dynamic marquee) ───
    private bool _audioTopFadeShown;
    private bool _audioBottomFadeShown;

    private void AudioScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        => UpdateAudioScrollFades();

    /// <summary>
    /// Shows the top fade only once the list is scrolled down from the top, and the bottom
    /// fade only while there's still content below — each transition animated so the fade
    /// appears/disappears smoothly instead of popping. At rest (top of a list) only the
    /// bottom edge fades; headers at the very top stay fully opaque.
    /// </summary>
    private void UpdateAudioScrollFades()
    {
        if (AudioScrollViewer == null || AudioFadeTopStop == null || AudioFadeBottomStop == null) return;

        double offset = AudioScrollViewer.VerticalOffset;
        double scrollable = AudioScrollViewer.ScrollableHeight;

        bool showTop = offset > 1.0;
        bool showBottom = scrollable > 1.0 && offset < scrollable - 1.0;

        if (showTop != _audioTopFadeShown)
        {
            _audioTopFadeShown = showTop;
            AnimateAudioFadeStop(AudioFadeTopStop, showTop);
        }
        if (showBottom != _audioBottomFadeShown)
        {
            _audioBottomFadeShown = showBottom;
            AnimateAudioFadeStop(AudioFadeBottomStop, showBottom);
        }
    }

    // Black = fully opaque (no fade), Transparent = edge fades the content out.
    private void AnimateAudioFadeStop(GradientStop stop, bool fade)
    {
        var to = fade ? Colors.Transparent : Colors.Black;
        var anim = new ColorAnimation(to, new Duration(TimeSpan.FromMilliseconds(180)))
        {
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            }
        };
        stop.BeginAnimation(GradientStop.ColorProperty, anim);
    }

    // ─── Navigation ───

    private void AudioIconButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_isAudioView || _isAnimating) return;
        SwitchToAudioView();
    }

    private void SwitchToAudioView()
    {
        if (_isAudioView || _isAnimating) return;

        FrameworkElement outgoing;
        bool fromPrimary = !_isSecondaryView && !_isTimerView;
        if (_isTimerView) outgoing = TimerContent;
        else if (_isSecondaryView) outgoing = SecondaryContent;
        else outgoing = ExpandedContent;

        bool fromSecondary = _isSecondaryView;

        _isAudioView = true;
        _isSecondaryView = false;
        _isTimerView = false;
        _isAnimating = true;
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;

        if (fromSecondary)
        {
            StopCameraPreviewForViewExit();
        }
        if (fromPrimary)
        {
            HideMediaBackground();
            if (LyricsBlurBackground != null && LyricsBlurBackground.Visibility == Visibility.Visible)
            {
                LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
                LyricsBlurBackground.Opacity = 0;
                LyricsBlurBackground.Visibility = Visibility.Collapsed;
            }
        }

        // Show the nav icons + highlight pill (audio view keeps them visible).
        UpdateNavIconsActiveState();
        NavIconsPanel.Visibility = Visibility.Visible;
        NavIconsPanel.Opacity = 1;
        NavIconsBackground.BeginAnimation(OpacityProperty, null);
        NavIconsBackground.Opacity = 0;
        NavIconsBackground.Visibility = Visibility.Visible;
        var navBgFadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(300)))
        {
            EasingFunction = _easePowerOut3,
            BeginTime = TimeSpan.FromMilliseconds(200)
        };
        Timeline.SetDesiredFrameRate(navBgFadeIn, AnimationConfig.TargetFps);
        NavIconsBackground.BeginAnimation(OpacityProperty, navBgFadeIn);

        // Build the mixer from the cached snapshot first so BuildAudioUI can measure its natural
        // height and size the notch to fit the content (clamped to the max). Building before the
        // animation lets the open transition target the fitted height directly.
        bool hadSnapshot = _lastAudioSnapshot != null;
        if (hadSnapshot)
            BuildAudioUI(_lastAudioSnapshot!);   // also recomputes _audioViewHeight to fit
        else
            _audioViewHeight = _audioViewMaxHeight;

        double fromW = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _expandedWidth;
        double fromH = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _expandedHeight;

        // Size the host window to cover both the starting notch height and the fitted target so
        // the notch is never clipped while it animates. If we opened from a taller view (e.g. the
        // clock), shrink the window down to the fitted size once the open finishes.
        double openWindowHeight = Math.Max(fromH, _audioViewHeight);
        ResizeHostWindowHeight(openWindowHeight);

        // The cached snapshot gives an instant first paint, but refresh immediately (don't wait
        // for the open animation to finish) so the app list and volumes are current the moment
        // the view opens. When the structure is unchanged this patches in place (smooth); a
        // structural change rebuilds and re-fits the notch once the open completes.
        AnimateAudioViewSwap(
            outgoing, AudioContent,
            fromW, fromH, _audioViewWidth, _audioViewHeight,
            prepIncoming: null,
            onComplete: () =>
            {
                if (openWindowHeight > _audioViewHeight)
                    ResizeHostWindowHeight(_audioViewHeight);
                // Reconcile the notch height in case fresh data arrived (and changed the row
                // count) while the open animation was running and SettleAudioNotchToFit was
                // suppressed by the _isAnimating guard.
                SettleAudioNotchToFit();
            });

        RefreshAudioData(SettleAudioNotchToFit);
        SyncAudioVolumesImmediate();
        StartAudioPoll();
    }

    private void SwitchFromAudioToPrimaryView()
    {
        if (!_isAudioView || _isAnimating) return;
        _isAudioView = false;
        StopAudioPoll();
        _audioMixerServiceCached?.ReleaseSessionCache();
        _isAnimating = true;
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;

        UpdateNavIconsActiveState();
        NavIconsBackground.BeginAnimation(OpacityProperty, null);
        NavIconsBackground.Opacity = 0;
        NavIconsBackground.Visibility = Visibility.Collapsed;

        double fromW = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _expandedWidth;
        double fromH = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _audioViewHeight;

        AnimateAudioViewSwap(
            AudioContent, ExpandedContent,
            fromW, fromH, _expandedWidth, _expandedHeight,
            prepIncoming: () =>
            {
                ExpandedContent.Effect = null;
                ExpandedContent.Width = _expandedWidth - 16;
                ExpandedContent.Height = _expandedHeight - 10;
            },
            onComplete: () =>
            {
                RestoreExpandedWindowSize();
                ShowMediaBackground();
                UpdateProgressSectionLayout();
                RefreshMediaMarquee();
                if (_settings.EnableBlurEffects && _isLyricsActive && LyricsBlurBackground != null)
                {
                    LyricsBlurImage.BeginAnimation(OpacityProperty, null);
                    LyricsBlurImage.Opacity = 1;
                    LyricsBlurBackground.Visibility = Visibility.Visible;
                    LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
                    var lyricsBlurFadeIn = new DoubleAnimation(0, 0.55, new Duration(TimeSpan.FromMilliseconds(250)))
                    {
                        EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseOut }
                    };
                    LyricsBlurBackground.BeginAnimation(OpacityProperty, lyricsBlurFadeIn);
                }
            });
    }

    private void SwitchFromAudioToSecondaryView()
    {
        if (!_isAudioView || _isAnimating) return;
        _isAudioView = false;
        StopAudioPoll();
        _audioMixerServiceCached?.ReleaseSessionCache();
        _isSecondaryView = true;
        _isAnimating = true;
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;

        UpdateNavIconsActiveState();
        UpdateShelfCapacityIndicator();

        double fromW = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _expandedWidth;
        double fromH = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _audioViewHeight;

        AnimateAudioViewSwap(
            AudioContent, SecondaryContent,
            fromW, fromH, _expandedWidth, _expandedHeight,
            prepIncoming: () =>
            {
                EnableKeyboardInput();
                // SecondaryContent uses star-sized columns; pin its settled width so the right-edge
                // pin during the notch shrink doesn't collapse the columns and squish the layout.
                SecondaryContent.Width = _expandedWidth
                    - SecondaryContent.Margin.Left - SecondaryContent.Margin.Right;
            },
            onComplete: () =>
            {
                // Notch width is fixed again — return to Stretch sizing so it fills the notch.
                SecondaryContent.Width = double.NaN;
                SecondaryContent.UpdateLayout();
                RestoreExpandedWindowSize();
                ResetCameraSectionLayoutInstant();
            });
    }

    private void SwitchFromAudioToTimerView()
    {
        if (!_isAudioView || _isAnimating) return;
        _isAudioView = false;
        StopAudioPoll();
        _audioMixerServiceCached?.ReleaseSessionCache();
        _isTimerView = true;
        _isAnimating = true;
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;

        UpdateTimerNavIconsState();

        double fromW = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _expandedWidth;
        double fromH = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _audioViewHeight;

        AnimateAudioViewSwap(
            AudioContent, TimerContent,
            fromW, fromH, _clockViewWidth, _clockViewHeight,
            prepIncoming: () =>
            {
                ApplyClockViewWindowSize();
                PrepareClockViewContentSize();
                RefreshClockView();
                RestoreTimerContentOpacity();
            },
            onComplete: () => UpdateTimerDisplay());
    }


    private void AnimateAudioViewSwap(
        FrameworkElement outgoing, FrameworkElement incoming,
        double notchFromW, double notchFromH, double notchToW, double notchToH,
        Action? prepIncoming, Action? onComplete)
    {
        NotchBorder.IsHitTestVisible = false;

        var durOut = new Duration(TimeSpan.FromMilliseconds(170));
        var durIn = new Duration(TimeSpan.FromMilliseconds(440));
        var inDelay = TimeSpan.FromMilliseconds(40);
        int fps = AnimationConfig.TargetFps;

        bool outIsAudio = ReferenceEquals(outgoing, AudioContent);
        bool inIsAudio = ReferenceEquals(incoming, AudioContent);

        // ─── Outgoing ───
        if (outIsAudio)
        {

            outgoing.CacheMode = new BitmapCache();
            var closeGroup = new TransformGroup();
            var closeScale = new ScaleTransform(1, 1);
            var closeTranslate = new TranslateTransform(0, 0);
            closeGroup.Children.Add(closeScale);
            closeGroup.Children.Add(closeTranslate);
            outgoing.RenderTransform = closeGroup;
            outgoing.RenderTransformOrigin = new Point(0.5, 0.4);

            var aFade = MakeAnim(1, 0, durOut, _easeAppleIn);
            var aSlide = MakeAnim(0, 12, durOut, _easeAppleIn);
            var aScaleX = MakeAnim(1, 0.97, durOut, _easeAppleIn);
            var aScaleY = MakeAnim(1, 0.97, durOut, _easeAppleIn);
            Timeline.SetDesiredFrameRate(aSlide, fps);
            Timeline.SetDesiredFrameRate(aScaleX, fps);
            Timeline.SetDesiredFrameRate(aScaleY, fps);

            aFade.Completed += (s, e) =>
            {
                outgoing.Visibility = Visibility.Collapsed;
                outgoing.CacheMode = null;
                outgoing.RenderTransform = null;
                outgoing.BeginAnimation(OpacityProperty, null);
                outgoing.Opacity = 1;
            };
            outgoing.BeginAnimation(OpacityProperty, aFade);
            closeTranslate.BeginAnimation(TranslateTransform.YProperty, aSlide);
            closeScale.BeginAnimation(ScaleTransform.ScaleXProperty, aScaleX);
            closeScale.BeginAnimation(ScaleTransform.ScaleYProperty, aScaleY);
        }
        else
        {
            // Outgoing fixed content. ExpandedContent rests shifted down by RestY, so start the
            // slide from there (not 0) to avoid a 1-frame jump up before the transition.
            double outRestY = ReferenceEquals(outgoing, ExpandedContent) ? ExpandedContentRestY : 0;

            var outGroup = new TransformGroup();
            var outScale = new ScaleTransform(1, 1);
            var outTranslate = new TranslateTransform(0, outRestY);
            outGroup.Children.Add(outScale);
            outGroup.Children.Add(outTranslate);
            outgoing.RenderTransform = outGroup;
            outgoing.RenderTransformOrigin = new Point(0.5, 0.5);

            var fadeOut = MakeAnim(1, 0, durOut, _easeAppleIn);
            var slideUp = MakeAnim(outRestY, outRestY - 10, durOut, _easeAppleIn);
            var scaleDownX = MakeAnim(1, 0.96, durOut, _easeAppleIn);
            var scaleDownY = MakeAnim(1, 0.96, durOut, _easeAppleIn);
            Timeline.SetDesiredFrameRate(slideUp, fps);
            Timeline.SetDesiredFrameRate(scaleDownX, fps);
            Timeline.SetDesiredFrameRate(scaleDownY, fps);

            var outBlur = outgoing.Effect as BlurEffect ?? new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
            outgoing.Effect = outBlur;
            var blurOutAnim = MakeAnim(0, _settings.EnableBlurEffects ? 6 : 0, durOut, _easeAppleIn);

            fadeOut.Completed += (s, e) =>
            {
                outgoing.Visibility = Visibility.Collapsed;
                outgoing.RenderTransform = null;
                outgoing.Effect = null;
                outBlur.Radius = 0;
            };

            outgoing.BeginAnimation(OpacityProperty, fadeOut);
            outTranslate.BeginAnimation(TranslateTransform.YProperty, slideUp);
            outScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDownX);
            outScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDownY);
            outBlur.BeginAnimation(BlurEffect.RadiusProperty, blurOutAnim);
        }

        // ─── Incoming ───
        prepIncoming?.Invoke();

        incoming.Visibility = Visibility.Visible;
        incoming.Opacity = 0;

        if (inIsAudio)
        {
            // Fade-only reveal; the notch resize supplies the motion. Cache the tree so
            // the fade is a cheap bitmap blend instead of re-rasterizing every frame.
            incoming.RenderTransform = null;
            incoming.CacheMode = new BitmapCache();
            var aFadeIn = MakeAnim(0, 1, durIn, _easeAppleOut, inDelay);
            Timeline.SetDesiredFrameRate(aFadeIn, fps);
            aFadeIn.Completed += (s, e) =>
            {
                _isAnimating = false;
                _isScrollSessionLocked = false;
                NotchBorder.IsHitTestVisible = true;
                incoming.Opacity = 1;
                incoming.BeginAnimation(OpacityProperty, null);
                incoming.CacheMode = null;
                onComplete?.Invoke();
            };
            incoming.BeginAnimation(OpacityProperty, aFadeIn);
        }
        else
        {
            bool shrinking = notchFromW > notchToW + 0.5;
            var savedRounding = incoming.UseLayoutRounding;
            if (shrinking)
            {
                // With default Stretch alignment the fixed-width content gets centered in the
                // still-wide notch and slides as the border collapses. Pin it to the right edge
                // (tracks the animating notch edge) + drop layout rounding; restored on done.
                incoming.HorizontalAlignment = HorizontalAlignment.Right;
                incoming.UseLayoutRounding = false;
                incoming.UpdateLayout();
            }

            // Returning to the main view: settle progress bar + marquee at the final width so they
            // don't land at a stale position for a frame and jump.
            if (ReferenceEquals(incoming, ExpandedContent))
                PrepareExpandedContentLayoutForReveal();

            // Primary content rests with a small downward offset in Dynamic Island mode;
            // settle the spring there (not at 0) so it matches a fresh expand.
            double restY = ReferenceEquals(incoming, ExpandedContent) ? ExpandedContentRestY : 0;

            var inGroup = new TransformGroup();
            var inScale = new ScaleTransform(0.96, 0.96);
            var inTranslate = new TranslateTransform(0, 16 + restY);
            inGroup.Children.Add(inScale);
            inGroup.Children.Add(inTranslate);
            incoming.RenderTransform = inGroup;
            incoming.RenderTransformOrigin = new Point(0.5, 0.5);

            var fadeIn = MakeAnim(0, 1, durIn, _easeAppleOut, inDelay);
            var springSlide = MakeAnim(16 + restY, restY, durIn, _easeAppleOut, inDelay);
            var springScaleX = MakeAnim(0.96, 1, durIn, _easeAppleOut, inDelay);
            var springScaleY = MakeAnim(0.96, 1, durIn, _easeAppleOut, inDelay);
            Timeline.SetDesiredFrameRate(fadeIn, fps);
            Timeline.SetDesiredFrameRate(springSlide, fps);
            Timeline.SetDesiredFrameRate(springScaleX, fps);
            Timeline.SetDesiredFrameRate(springScaleY, fps);

            fadeIn.Completed += (s, e) =>
            {
                _isAnimating = false;
                _isScrollSessionLocked = false;
                NotchBorder.IsHitTestVisible = true;
                incoming.Opacity = 1;
                incoming.BeginAnimation(OpacityProperty, null);
                if (ReferenceEquals(incoming, ExpandedContent))
                    ApplyExpandedContentRestTransform();
                else
                    incoming.RenderTransform = null;
                if (shrinking)
                {
                    // Notch width is fixed again — restore canonical layout so the content
                    // fills the notch exactly and steady-state text stays crisp.
                    incoming.HorizontalAlignment = HorizontalAlignment.Stretch;
                    incoming.UseLayoutRounding = savedRounding;
                    incoming.UpdateLayout();
                }
                onComplete?.Invoke();
            };

            incoming.BeginAnimation(OpacityProperty, fadeIn);
            inTranslate.BeginAnimation(TranslateTransform.YProperty, springSlide);
            inScale.BeginAnimation(ScaleTransform.ScaleXProperty, springScaleX);
            inScale.BeginAnimation(ScaleTransform.ScaleYProperty, springScaleY);
        }

        AnimateClockViewNotchResize(notchFromW, notchFromH, notchToW, notchToH, durIn, inDelay);
    }

    /// <summary>
    /// Animates the notch (and the scroll viewport + host window) to the current fitted
    /// <see cref="_audioViewHeight"/> using the default open-style easing. Used after a
    /// structural data refresh so the panel keeps hugging its content.
    /// </summary>
    private void SettleAudioNotchToFit()
        => AnimateAudioNotchHeight(_audioViewHeight, new Duration(TimeSpan.FromMilliseconds(300)), _easeExpOut6);

    /// <summary>
    /// Smoothly resizes the Sound-view notch height to <paramref name="target"/>, keeping the
    /// scroll viewport and the host window in sync. The window grows up-front (so a taller notch
    /// isn't clipped) and shrinks on completion, so it never clips the notch mid-animation.
    /// No-op while a view-switch animation is running.
    /// </summary>
    private void AnimateAudioNotchHeight(double target, Duration dur, IEasingFunction ease)
    {
        if (!_isAudioView || _isAnimating || AudioScrollViewer == null) return;

        double current = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : target;
        if (Math.Abs(current - target) < 0.5)
        {
            NotchBorder.BeginAnimation(HeightProperty, null);
            NotchBorder.Height = target;
            AudioScrollViewer.BeginAnimation(HeightProperty, null);
            AudioScrollViewer.Height = target - _audioViewChrome;
            ResizeHostWindowHeight(target);
            return;
        }

        int fps = AnimationConfig.TargetFps;
        bool growing = target > current;
        if (growing) ResizeHostWindowHeight(target);

        NotchBorder.BeginAnimation(HeightProperty, null);
        AudioScrollViewer.BeginAnimation(HeightProperty, null);

        var notchAnim = MakeAnim(current, target, dur, ease);
        var scrollAnim = MakeAnim(current - _audioViewChrome, target - _audioViewChrome, dur, ease);
        Timeline.SetDesiredFrameRate(notchAnim, fps);
        Timeline.SetDesiredFrameRate(scrollAnim, fps);

        notchAnim.Completed += (_, _) =>
        {
            NotchBorder.BeginAnimation(HeightProperty, null);
            NotchBorder.Height = target;
            AudioScrollViewer.BeginAnimation(HeightProperty, null);
            AudioScrollViewer.Height = target - _audioViewChrome;
            if (!growing) ResizeHostWindowHeight(target);
        };

        NotchBorder.BeginAnimation(HeightProperty, notchAnim, HandoffBehavior.SnapshotAndReplace);
        AudioScrollViewer.BeginAnimation(HeightProperty, scrollAnim, HandoffBehavior.SnapshotAndReplace);
    }
}
