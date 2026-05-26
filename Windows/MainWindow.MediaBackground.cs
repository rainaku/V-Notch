using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Threading;
using VNotch.Models;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    #region Media Background & Color Extraction

    private Color _lastDominantColor = Colors.Transparent;
    private Color _lastSubColor = Colors.White;
    private Color _progressBarVibrantColor = Colors.White;
    private string? _lastTrackId = null;
    private bool _isFadingTrack = false;
    private Color _currentVibrantColor = Colors.White;
    private int _mediaBackgroundAnimationVersion = 0;
    private int _mediaBackgroundRecoveryVersion = 0;

    private void UpdateMediaBackground(MediaInfo? info, bool forceRefresh = false)
    {
        if (info == null || !info.IsAnyMediaPlaying)
        {
            HideMediaBackground();
            return;
        }

        // If thumbnail is temporarily null (e
        if (info.Thumbnail == null)
        {
            return;
        }

        var palette = DynamicIslandColorExtractor.GetDynamicIslandPalette(info.Thumbnail);
        var dominantColor = palette.Main;
        var subColor = palette.Sub;

        // Use track identity without MediaSource to avoid false "new track" detection when background fetches change MediaSource (e
        string currentTrackId = $"{info.CurrentTrack}|{info.CurrentArtist}";
        bool isNewTrack = _lastTrackId != null && _lastTrackId != currentTrackId;
        _lastTrackId = currentTrackId;

        if (isNewTrack && !forceRefresh && !_isFadingTrack && _isExpanded)
        {
            _isFadingTrack = true;
            FadeToBlackThenUpdate(info);
            return;
        }

        UpdateBlurredBackgroundAsync(info.Thumbnail).SafeFireAndForget("MEDIA-BG-BLUR");

        // Detect overly bright thumbnails and apply dimming overlay
        double brightnessDimOpacity = DynamicIslandColorExtractor.GetBrightnessDimOverlay(info.Thumbnail);
        var dimOverlayAnim = new DoubleAnimation
        {
            To = brightnessDimOpacity,
            Duration = TimeSpan.FromMilliseconds(400),
            EasingFunction = _easeQuadOut
        };
        BrightnessDimOverlay.BeginAnimation(OpacityProperty, dimOverlayAnim);
        BrightnessDimOverlay2.BeginAnimation(OpacityProperty, dimOverlayAnim);

        if (!forceRefresh && dominantColor == _lastDominantColor && MediaBackground.Opacity > 0.49 && !isNewTrack)
        {
            return;
        }

        _lastDominantColor = dominantColor;
        _lastSubColor = subColor;

        // Lift dark dominant colors so the blur layer remains visible on the dark UI
        var liftedDominant = LiftDarkColor(dominantColor);
        var liftedSub = LiftDarkColor(subColor);

        var targetColor = Color.FromRgb(liftedDominant.R, liftedDominant.G, liftedDominant.B);
        var vibrantTargetColor = Color.FromRgb(liftedSub.R, liftedSub.G, liftedSub.B);
        double dominantLuminance = (0.2126 * dominantColor.R + 0.7152 * dominantColor.G + 0.0722 * dominantColor.B) / 255.0;

        var colorAnim = new ColorAnimation
        {
            To = targetColor,
            Duration = TimeSpan.FromMilliseconds(500),
            EasingFunction = _easeQuadOut
        };

        var uiColorAnim = new ColorAnimation
        {
            To = vibrantTargetColor,
            Duration = TimeSpan.FromMilliseconds(420),
            EasingFunction = _easeQuadOut
        };

        double targetOpacity = (_isExpanded && (!_isAnimating || forceRefresh))
            ? DynamicIslandColorExtractor.GetAdaptiveBlurOpacity(dominantLuminance, _settings.MediaBlurBrightnessBoost)
            : 0;
        // For dark thumbnails, boost the tint opacity further so the lifted color tint is more present in the final blend (otherwise the bright blurred image dominates and the color barely shows through)
        if (targetOpacity > 0 && dominantLuminance < 0.25)
        {
            double darknessBoost = 1.0 + (0.25 - dominantLuminance) * 1.4; // up to ×1.35
            targetOpacity = Math.Min(targetOpacity * darknessBoost, 0.95);
        }
        int animationVersion = ++_mediaBackgroundAnimationVersion;

        var opacityAnim = new DoubleAnimation
        {
            To = targetOpacity,
            Duration = TimeSpan.FromMilliseconds(500),
            EasingFunction = _easeQuadOut
        };

        if (targetOpacity > 0)
        {
            MediaBackground.BeginAnimation(OpacityProperty, null);
            MediaBackground2.BeginAnimation(OpacityProperty, null);
            MediaBackground.Visibility = Visibility.Visible;
            MediaBackground2.Visibility = Visibility.Visible;
        }
        else
        {
            opacityAnim.Completed += (s, e) =>
            {
                if (animationVersion != _mediaBackgroundAnimationVersion)
                {
                    return;
                }

                if (MediaBackground.Opacity <= 0.001 && (!_isExpanded || _isAnimating))
                {
                    MediaBackground.Visibility = Visibility.Collapsed;
                    MediaBackground2.Visibility = Visibility.Collapsed;
                }
            };
        }

        MediaBackground.BeginAnimation(OpacityProperty, opacityAnim);
        MediaBackground2.BeginAnimation(OpacityProperty, opacityAnim);

        EnsureUnfrozen(IndeterminateProgress.Background, c => IndeterminateProgress.Background = new SolidColorBrush(c ?? Colors.White));
        EnsureUnfrozen(CurrentTimeText.Foreground, c => CurrentTimeText.Foreground = new SolidColorBrush(c ?? Color.FromRgb(136, 136, 136)));
        EnsureUnfrozen(RemainingTimeText.Foreground, c => RemainingTimeText.Foreground = new SolidColorBrush(c ?? Color.FromRgb(136, 136, 136)));
        EnsureUnfrozen(CompactTitleMarquee.Foreground, c => CompactTitleMarquee.Foreground = new SolidColorBrush(c ?? Colors.White));

        // Progress bar gradient: bright color at start → darkened 35% at end
        _progressBarVibrantColor = vibrantTargetColor;
        var progressDarkColor = Color.FromArgb(
            vibrantTargetColor.A,
            (byte)(vibrantTargetColor.R * 0.65),
            (byte)(vibrantTargetColor.G * 0.65),
            (byte)(vibrantTargetColor.B * 0.65));

        var progressStartAnim = new ColorAnimation
        {
            To = vibrantTargetColor,
            Duration = TimeSpan.FromMilliseconds(420),
            EasingFunction = _easeQuadOut
        };
        var progressEndAnim = new ColorAnimation
        {
            To = progressDarkColor,
            Duration = TimeSpan.FromMilliseconds(420),
            EasingFunction = _easeQuadOut
        };
        ProgressBarGradientStart.BeginAnimation(GradientStop.ColorProperty, progressStartAnim);
        ProgressBarGradientEnd.BeginAnimation(GradientStop.ColorProperty, progressEndAnim);

        if (IndeterminateProgress.Background is SolidColorBrush ipb && !ipb.IsFrozen)
            ipb.BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);

        if (CurrentTimeText.Foreground is SolidColorBrush ctf && !ctf.IsFrozen)
            ctf.BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);

        if (RemainingTimeText.Foreground is SolidColorBrush rtf && !rtf.IsFrozen)
            rtf.BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);

        if (CompactTitleMarquee.Foreground is SolidColorBrush cmt && !cmt.IsFrozen)
            cmt.BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);

        AnimateTitleGradient(vibrantTargetColor);

        if (Resources["MusicVisualizerBrush"] is SolidColorBrush visualizerBrush && !visualizerBrush.IsFrozen)
        {
            visualizerBrush.BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);
        }

        EnsureUnfrozen(VolumeIcon.Foreground, c => VolumeIcon.Foreground = new SolidColorBrush(c ?? Color.FromRgb(136, 136, 136)));
        ((SolidColorBrush)VolumeIcon.Foreground).BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);

        // Volume bar gradient: bright color at start → darkened at end
        var volStartAnim = new ColorAnimation
        {
            To = vibrantTargetColor,
            Duration = TimeSpan.FromMilliseconds(420),
            EasingFunction = _easeQuadOut
        };
        var volEndAnim = new ColorAnimation
        {
            To = progressDarkColor,
            Duration = TimeSpan.FromMilliseconds(420),
            EasingFunction = _easeQuadOut
        };
        VolumeBarGradientStart.BeginAnimation(GradientStop.ColorProperty, volStartAnim);
        VolumeBarGradientEnd.BeginAnimation(GradientStop.ColorProperty, volEndAnim);

        // Volume scroll indicator (center bar shown when scrolling on collapsed notch)
        if (VolumeIndicatorFill != null)
        {
            var volIndStartAnim = new ColorAnimation
            {
                To = vibrantTargetColor,
                Duration = TimeSpan.FromMilliseconds(420),
                EasingFunction = _easeQuadOut
            };
            var volIndEndAnim = new ColorAnimation
            {
                To = progressDarkColor,
                Duration = TimeSpan.FromMilliseconds(420),
                EasingFunction = _easeQuadOut
            };
            VolumeIndicatorGradientStart.BeginAnimation(GradientStop.ColorProperty, volIndStartAnim);
            VolumeIndicatorGradientEnd.BeginAnimation(GradientStop.ColorProperty, volIndEndAnim);
        }

        void EnsureUnfrozenFill(System.Windows.Shapes.Shape shape)
        {
            var brush = shape.Fill as SolidColorBrush;
            if (brush == null || brush.IsFrozen) shape.Fill = new SolidColorBrush(brush?.Color ?? Colors.White);
            ((SolidColorBrush)shape.Fill).BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);
        }

        EnsureUnfrozenFill(InlinePrevArrow0);
        EnsureUnfrozenFill(InlinePrevArrow1);
        EnsureUnfrozenFill(InlinePrevArrow2);
        EnsureUnfrozenFill(InlinePauseIconPath);
        EnsureUnfrozenFill(InlinePlayIconPath);
        EnsureUnfrozenFill(InlineNextArrow0);
        EnsureUnfrozenFill(InlineNextArrow1);
        EnsureUnfrozenFill(InlineNextArrow2);

    }

    private static void EnsureUnfrozen(Brush? brush, Action<Color?> replace)
    {
        var sb = brush as SolidColorBrush;
        if (sb == null || sb.IsFrozen)
        {
            replace(sb?.Color);
        }
    }

    private static Color LiftDarkColor(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double v = max;
        double s = max > 0 ? (max - min) / max : 0;

        // Already bright enough — keep as is
        if (v >= 0.55) return c;

        // Compute target V based on how dark the color is
        double targetV;
        if (v < 0.20)        targetV = 0.55;  // very dark → lift to medium-bright
        else if (v < 0.35)   targetV = 0.55;
        else if (v < 0.50)   targetV = 0.55;
        else                 targetV = v;

        if (targetV <= v) return c;

        double scale = targetV / Math.Max(v, 0.01);
        byte newR = (byte)Math.Clamp(c.R * scale, 0, 255);
        byte newG = (byte)Math.Clamp(c.G * scale, 0, 255);
        byte newB = (byte)Math.Clamp(c.B * scale, 0, 255);

        return Color.FromRgb(newR, newG, newB);
    }

    private void FadeToBlackThenUpdate(MediaInfo info)
    {
        int animationVersion = ++_mediaBackgroundAnimationVersion;
        MediaBackground.BeginAnimation(OpacityProperty, null);
        MediaBackground2.BeginAnimation(OpacityProperty, null);
        MediaBackground.Visibility = Visibility.Visible;
        MediaBackground2.Visibility = Visibility.Visible;

        var fadeToBlack = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = _easeQuadOut
        };

        fadeToBlack.Completed += (s, e) =>
        {
            if (animationVersion != _mediaBackgroundAnimationVersion)
            {
                return;
            }

            _isFadingTrack = false;
            UpdateMediaBackground(info, forceRefresh: true);
        };

        MediaBackground.BeginAnimation(OpacityProperty, fadeToBlack);
        MediaBackground2.BeginAnimation(OpacityProperty, fadeToBlack);
    }

    private void HideMediaBackground()
    {
        if (MediaBackground.Opacity == 0 && MediaBackground.Visibility != Visibility.Visible) return;

        _lastDominantColor = Colors.Transparent;
        int animationVersion = ++_mediaBackgroundAnimationVersion;
        var opacityAnim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(400))
        {
            EasingFunction = _easePowerIn2
        };

        opacityAnim.Completed += (s, e) =>
        {
            if (animationVersion != _mediaBackgroundAnimationVersion)
            {
                return;
            }

            if (MediaBackground.Opacity <= 0.001)
            {
                MediaBackground.Visibility = Visibility.Collapsed;
                MediaBackground2.Visibility = Visibility.Collapsed;
            }
        };

        MediaBackground.BeginAnimation(OpacityProperty, opacityAnim);
        MediaBackground2.BeginAnimation(OpacityProperty, opacityAnim);

        var defaultColorAnim = new ColorAnimation
        {
            To = Colors.White,
            Duration = TimeSpan.FromMilliseconds(400)
        };
        var defaultTextAnim = new ColorAnimation
        {
            To = Color.FromRgb(136, 136, 136),
            Duration = TimeSpan.FromMilliseconds(400)
        };

        ProgressBarGradientStart.BeginAnimation(GradientStop.ColorProperty, defaultColorAnim);
        var defaultGradientEndAnim = new ColorAnimation
        {
            To = Color.FromRgb(140, 140, 140),
            Duration = TimeSpan.FromMilliseconds(400)
        };
        ProgressBarGradientEnd.BeginAnimation(GradientStop.ColorProperty, defaultGradientEndAnim);
        if (IndeterminateProgress.Background is SolidColorBrush ipb && !ipb.IsFrozen) ipb.BeginAnimation(SolidColorBrush.ColorProperty, defaultColorAnim);
        if (CurrentTimeText.Foreground is SolidColorBrush st && !st.IsFrozen) st.BeginAnimation(SolidColorBrush.ColorProperty, defaultTextAnim);
        if (RemainingTimeText.Foreground is SolidColorBrush rt && !rt.IsFrozen) rt.BeginAnimation(SolidColorBrush.ColorProperty, defaultTextAnim);
        if (CompactTitleMarquee.Foreground is SolidColorBrush cmt && !cmt.IsFrozen) cmt.BeginAnimation(SolidColorBrush.ColorProperty, defaultColorAnim);

        ResetTitleGradientToWhite();

        if (VolumeIcon.Foreground is SolidColorBrush volIco && !volIco.IsFrozen) volIco.BeginAnimation(SolidColorBrush.ColorProperty, defaultTextAnim);
        VolumeBarGradientStart.BeginAnimation(GradientStop.ColorProperty, defaultColorAnim);
        var defaultVolEndAnim = new ColorAnimation
        {
            To = Color.FromRgb(140, 140, 140),
            Duration = TimeSpan.FromMilliseconds(400)
        };
        VolumeBarGradientEnd.BeginAnimation(GradientStop.ColorProperty, defaultVolEndAnim);
        var defaultVolIndEndAnim = new ColorAnimation
        {
            To = Color.FromRgb(204, 204, 204),
            Duration = TimeSpan.FromMilliseconds(400)
        };
        VolumeIndicatorGradientStart.BeginAnimation(GradientStop.ColorProperty, defaultColorAnim);
        VolumeIndicatorGradientEnd.BeginAnimation(GradientStop.ColorProperty, defaultVolIndEndAnim);

        void ResetUnfrozenFill(System.Windows.Shapes.Shape shape)
        {
            if (shape.Fill is SolidColorBrush brush && !brush.IsFrozen)
                brush.BeginAnimation(SolidColorBrush.ColorProperty, defaultColorAnim);
        }

        ResetUnfrozenFill(InlinePrevArrow0);
        ResetUnfrozenFill(InlinePrevArrow1);
        ResetUnfrozenFill(InlinePrevArrow2);
        ResetUnfrozenFill(InlinePauseIconPath);
        ResetUnfrozenFill(InlinePlayIconPath);
        ResetUnfrozenFill(InlineNextArrow0);
        ResetUnfrozenFill(InlineNextArrow1);
        ResetUnfrozenFill(InlineNextArrow2);
    }

    private void ShowMediaBackground()
    {
        if (!_isExpanded || _isAnimating || _currentMediaInfo == null) return;

        MediaBackground.BeginAnimation(OpacityProperty, null);
        MediaBackground2.BeginAnimation(OpacityProperty, null);
        MediaBackground.Visibility = Visibility.Visible;
        MediaBackground2.Visibility = Visibility.Visible;

        UpdateMediaBackground(_currentMediaInfo, forceRefresh: true);
        ScheduleMediaBackgroundRecovery();
    }

    private void ScheduleMediaBackgroundRecovery()
    {
        int recoveryVersion = Interlocked.Increment(ref _mediaBackgroundRecoveryVersion);

        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            EnsureMediaBackgroundVisible(recoveryVersion);
        }));

        var recoveryTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };

        recoveryTimer.Tick += (s, e) =>
        {
            recoveryTimer.Stop();
            EnsureMediaBackgroundVisible(recoveryVersion);
        };

        recoveryTimer.Start();
    }

    private void EnsureMediaBackgroundVisible(int recoveryVersion)
    {
        if (recoveryVersion != _mediaBackgroundRecoveryVersion)
        {
            return;
        }

        if (!_isExpanded || _isAnimating || _currentMediaInfo?.Thumbnail == null || !_currentMediaInfo.IsAnyMediaPlaying)
        {
            return;
        }

        bool needsRecovery =
            MediaBackground.Visibility != Visibility.Visible ||
            MediaBackground2.Visibility != Visibility.Visible ||
            MediaBackground.Opacity < 0.05 ||
            MediaBackground2.Opacity < 0.05;

        if (!needsRecovery)
        {
            return;
        }

        MediaBackground.BeginAnimation(OpacityProperty, null);
        MediaBackground2.BeginAnimation(OpacityProperty, null);
        MediaBackground.Visibility = Visibility.Visible;
        MediaBackground2.Visibility = Visibility.Visible;

        UpdateMediaBackground(_currentMediaInfo, forceRefresh: true);
    }

    private async Task UpdateBlurredBackgroundAsync(BitmapSource thumbnail)
    {
        try
        {
            var blurredImage = await FastBlurService.GetBlurredImageAsync(thumbnail);
            if (blurredImage != null)
            {
                // Simply set the blurred image source (no crossfade on main blur)
                MediaBackgroundImage.Source = blurredImage;
                MediaBackgroundImage2.Source = blurredImage;
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("MEDIA-BG-BLUR", ex.ToString());
        }
    }

    #endregion

    #region Title Gradient Animation

    private void AnimateTitleGradient(Color vibrantColor)
    {
        _currentVibrantColor = vibrantColor;

        // Subtle tint: blend vibrant color lightly into white (15% tint)
        const double tintStrength = 0.15;
        var tintedWhite = Color.FromRgb(
            (byte)(255 - (255 - vibrantColor.R) * tintStrength),
            (byte)(255 - (255 - vibrantColor.G) * tintStrength),
            (byte)(255 - (255 - vibrantColor.B) * tintStrength));

        // Artist: same tint but at reduced opacity (matches the original 75% alpha)
        var tintedArtist = Color.FromArgb(
            191, // 0xBF = 75% opacity
            tintedWhite.R, tintedWhite.G, tintedWhite.B);

        var colorAnim = new ColorAnimation { To = tintedWhite, Duration = TimeSpan.FromMilliseconds(500), EasingFunction = _easeQuadOut };
        var artistColorAnim = new ColorAnimation { To = tintedArtist, Duration = TimeSpan.FromMilliseconds(500), EasingFunction = _easeQuadOut };

        // Title
        if (Resources["TrackTitleGradient"] is LinearGradientBrush titleBrush)
        {
            foreach (var stop in titleBrush.GradientStops)
                stop.BeginAnimation(GradientStop.ColorProperty, colorAnim);
        }

        if (Resources["TrackTitleNextGradient"] is LinearGradientBrush titleNextBrush)
        {
            foreach (var stop in titleNextBrush.GradientStops)
                stop.BeginAnimation(GradientStop.ColorProperty, colorAnim);
        }

        // Artist
        AnimateForegroundColor(TrackArtist, artistColorAnim);
        AnimateForegroundColor(TrackArtistNext, artistColorAnim);

        // Media control icons (prev/play/pause/next)
        AnimatePathFillAndStroke(PrevArrow0, colorAnim);
        AnimatePathFillAndStroke(PrevArrow1, colorAnim);
        AnimatePathFillAndStroke(PrevArrow2, colorAnim);
        AnimatePathFillAndStroke(NextArrow0, colorAnim);
        AnimatePathFillAndStroke(NextArrow1, colorAnim);
        AnimatePathFillAndStroke(NextArrow2, colorAnim);
        AnimatePathFill(PauseIconPath, colorAnim);
        AnimatePathFill(PlayIconPath, colorAnim);
    }

    private static void AnimateForegroundColor(System.Windows.Controls.TextBlock tb, ColorAnimation anim)
    {
        if (tb.Foreground is SolidColorBrush brush && !brush.IsFrozen)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }
        else
        {
            var newBrush = new SolidColorBrush(tb.Foreground is SolidColorBrush sb ? sb.Color : Colors.White);
            tb.Foreground = newBrush;
            newBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }
    }

    private static void AnimatePathFill(System.Windows.Shapes.Path path, ColorAnimation anim)
    {
        if (path.Fill is SolidColorBrush fillBrush && !fillBrush.IsFrozen)
        {
            fillBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }
        else
        {
            var newBrush = new SolidColorBrush(path.Fill is SolidColorBrush sb ? sb.Color : Colors.White);
            path.Fill = newBrush;
            newBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }
    }

    private static void AnimatePathFillAndStroke(System.Windows.Shapes.Path path, ColorAnimation anim)
    {
        AnimatePathFill(path, anim);
        if (path.Stroke is SolidColorBrush strokeBrush && !strokeBrush.IsFrozen)
        {
            strokeBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }
        else if (path.Stroke != null)
        {
            var newBrush = new SolidColorBrush(path.Stroke is SolidColorBrush sb ? sb.Color : Colors.White);
            path.Stroke = newBrush;
            newBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }
    }

    private void StopTitleGradientShift()
    {
        // No-op: shimmer animation removed, kept for call-site compatibility
    }

    private void ResetTitleGradientToWhite()
    {
        StopTitleGradientShift();
        _currentVibrantColor = Colors.White;

        var whiteAnim = new ColorAnimation
        {
            To = Colors.White,
            Duration = TimeSpan.FromMilliseconds(400)
        };

        var artistWhiteAnim = new ColorAnimation
        {
            To = Color.FromArgb(191, 255, 255, 255), // #BFFFFFFF
            Duration = TimeSpan.FromMilliseconds(400)
        };

        if (Resources["TrackTitleGradient"] is LinearGradientBrush titleBrush)
        {
            foreach (var stop in titleBrush.GradientStops)
                stop.BeginAnimation(GradientStop.ColorProperty, whiteAnim);
        }

        if (Resources["TrackTitleNextGradient"] is LinearGradientBrush titleNextBrush)
        {
            foreach (var stop in titleNextBrush.GradientStops)
                stop.BeginAnimation(GradientStop.ColorProperty, whiteAnim);
        }

        // Reset artist
        AnimateForegroundColor(TrackArtist, artistWhiteAnim);
        AnimateForegroundColor(TrackArtistNext, artistWhiteAnim);

        // Reset media controls
        AnimatePathFillAndStroke(PrevArrow0, whiteAnim);
        AnimatePathFillAndStroke(PrevArrow1, whiteAnim);
        AnimatePathFillAndStroke(PrevArrow2, whiteAnim);
        AnimatePathFillAndStroke(NextArrow0, whiteAnim);
        AnimatePathFillAndStroke(NextArrow1, whiteAnim);
        AnimatePathFillAndStroke(NextArrow2, whiteAnim);
        AnimatePathFill(PauseIconPath, whiteAnim);
        AnimatePathFill(PlayIconPath, whiteAnim);
    }

    #endregion
}
