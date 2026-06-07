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
    private bool _isAudioView = false;
    private const double _audioViewWidth = 720;
    private const double _audioViewHeight = 378;

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

        ApplyAudioViewWindowSize();

        double fromW = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _expandedWidth;
        double fromH = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _expandedHeight;
        AnimateAudioViewSwap(
            outgoing, AudioContent,
            fromW, fromH, _audioViewWidth, _audioViewHeight,
            prepIncoming: () =>
            {
                if (_lastAudioSnapshot != null)
                    BuildAudioUI(_lastAudioSnapshot);
            },
            onComplete: null);
        RefreshAudioData();
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
            prepIncoming: () => EnableKeyboardInput(),
            onComplete: () =>
            {
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

        var durOut = new Duration(TimeSpan.FromMilliseconds(180));
        var durIn = new Duration(TimeSpan.FromMilliseconds(480));
        var inDelay = TimeSpan.FromMilliseconds(50);
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

            var aFade = MakeAnim(1, 0, durOut, _easeQuadIn);
            var aSlide = MakeAnim(0, 18, durOut, _easeQuadIn);
            var aScaleX = MakeAnim(1, 0.96, durOut, _easeQuadIn);
            var aScaleY = MakeAnim(1, 0.96, durOut, _easeQuadIn);
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

            var fadeOut = MakeAnim(1, 0, durOut, _easeQuadIn);
            var slideUp = MakeAnim(outRestY, outRestY - 16, durOut, _easeQuadIn);
            var scaleDownX = MakeAnim(1, 0.93, durOut, _easeQuadIn);
            var scaleDownY = MakeAnim(1, 0.93, durOut, _easeQuadIn);
            Timeline.SetDesiredFrameRate(slideUp, fps);
            Timeline.SetDesiredFrameRate(scaleDownX, fps);
            Timeline.SetDesiredFrameRate(scaleDownY, fps);

            var outBlur = outgoing.Effect as BlurEffect ?? new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
            outgoing.Effect = outBlur;
            var blurOutAnim = MakeAnim(0, _settings.EnableBlurEffects ? 10 : 0, durOut, _easeQuadIn);

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
            var aFadeIn = MakeAnim(0, 1, durIn, _easeExpOut6, inDelay);
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

            // Primary content rests with a small downward offset in Dynamic Island mode;
            // settle the spring there (not at 0) so it matches a fresh expand.
            double restY = ReferenceEquals(incoming, ExpandedContent) ? ExpandedContentRestY : 0;

            var inGroup = new TransformGroup();
            var inScale = new ScaleTransform(0.93, 0.93);
            var inTranslate = new TranslateTransform(0, 26 + restY);
            inGroup.Children.Add(inScale);
            inGroup.Children.Add(inTranslate);
            incoming.RenderTransform = inGroup;
            incoming.RenderTransformOrigin = new Point(0.5, 0.5);

            var fadeIn = MakeAnim(0, 1, durIn, _easeExpOut6, inDelay);
            var springSlide = MakeAnim(26 + restY, restY, durIn, _easeExpOut7, inDelay);
            var springScaleX = MakeAnim(0.93, 1, durIn, _easeSoftSpring, inDelay);
            var springScaleY = MakeAnim(0.93, 1, durIn, _easeSoftSpring, inDelay);
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

    private void ApplyAudioViewWindowSize() => ResizeHostWindowHeight(_audioViewHeight);
}
