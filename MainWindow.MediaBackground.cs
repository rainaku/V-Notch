using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    private string? _lastTrackId = null;
    private bool _isFadingTrack = false;
    private DispatcherTimer? _titleGradientTimer;
    private double _titleGradientPhase = 0.0;
    private Color _currentVibrantColor = Colors.White;
    private bool _titleGradientRunning = false;
    private int _mediaBackgroundAnimationVersion = 0;
    private int _mediaBackgroundRecoveryVersion = 0;

    private void UpdateMediaBackground(MediaInfo? info, bool forceRefresh = false)
    {
        if (info == null || info.Thumbnail == null || !info.IsAnyMediaPlaying)
        {
            HideMediaBackground();
            return;
        }

        var palette = DynamicIslandColorExtractor.GetDynamicIslandPalette(info.Thumbnail);
        var dominantColor = palette.Main;
        var subColor = palette.Sub;

        string currentTrackId = info.GetSignature();
        bool isNewTrack = _lastTrackId != null && _lastTrackId != currentTrackId;
        _lastTrackId = currentTrackId;

        if (isNewTrack && !forceRefresh && !_isFadingTrack && _isExpanded)
        {
            _isFadingTrack = true;
            FadeToBlackThenUpdate(info);
            return;
        }

        _ = UpdateBlurredBackgroundAsync(info.Thumbnail);

        if (!forceRefresh && dominantColor == _lastDominantColor && MediaBackground.Opacity > 0.49 && !isNewTrack)
        {
            return;
        }

        _lastDominantColor = dominantColor;
        _lastSubColor = subColor;

        var targetColor = Color.FromRgb(dominantColor.R, dominantColor.G, dominantColor.B);
        var vibrantTargetColor = Color.FromRgb(subColor.R, subColor.G, subColor.B);
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
            ? DynamicIslandColorExtractor.GetAdaptiveBlurOpacity(dominantLuminance)
            : 0;
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

        double blurImageOpacity = DynamicIslandColorExtractor.GetAdaptiveBlurImageOpacity(dominantLuminance);
        var blurImageOpacityAnim = new DoubleAnimation
        {
            To = blurImageOpacity,
            Duration = TimeSpan.FromMilliseconds(450),
            EasingFunction = _easeQuadOut
        };
        MediaBackgroundImage.BeginAnimation(UIElement.OpacityProperty, blurImageOpacityAnim);
        MediaBackgroundImage2.BeginAnimation(UIElement.OpacityProperty, blurImageOpacityAnim);

        EnsureUnfrozen(ProgressBar.Background, c => ProgressBar.Background = new SolidColorBrush(c ?? Colors.White));
        EnsureUnfrozen(IndeterminateProgress.Background, c => IndeterminateProgress.Background = new SolidColorBrush(c ?? Colors.White));
        EnsureUnfrozen(CurrentTimeText.Foreground, c => CurrentTimeText.Foreground = new SolidColorBrush(c ?? Color.FromRgb(136, 136, 136)));
        EnsureUnfrozen(RemainingTimeText.Foreground, c => RemainingTimeText.Foreground = new SolidColorBrush(c ?? Color.FromRgb(136, 136, 136)));
        EnsureUnfrozen(CompactTitleMarquee.Foreground, c => CompactTitleMarquee.Foreground = new SolidColorBrush(c ?? Colors.White));

        if (ProgressBar.Background is SolidColorBrush pbb && !pbb.IsFrozen)
            pbb.BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);

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

        EnsureUnfrozen(VolumeBarFront.Background, c => VolumeBarFront.Background = new SolidColorBrush(c ?? Colors.White));
        ((SolidColorBrush)VolumeBarFront.Background).BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);

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

        if (ProgressBar.Background is SolidColorBrush sb && !sb.IsFrozen) sb.BeginAnimation(SolidColorBrush.ColorProperty, defaultColorAnim);
        if (IndeterminateProgress.Background is SolidColorBrush ipb && !ipb.IsFrozen) ipb.BeginAnimation(SolidColorBrush.ColorProperty, defaultColorAnim);
        if (CurrentTimeText.Foreground is SolidColorBrush st && !st.IsFrozen) st.BeginAnimation(SolidColorBrush.ColorProperty, defaultTextAnim);
        if (RemainingTimeText.Foreground is SolidColorBrush rt && !rt.IsFrozen) rt.BeginAnimation(SolidColorBrush.ColorProperty, defaultTextAnim);
        if (CompactTitleMarquee.Foreground is SolidColorBrush cmt && !cmt.IsFrozen) cmt.BeginAnimation(SolidColorBrush.ColorProperty, defaultColorAnim);

        ResetTitleGradientToWhite();

        if (VolumeIcon.Foreground is SolidColorBrush volIco && !volIco.IsFrozen) volIco.BeginAnimation(SolidColorBrush.ColorProperty, defaultTextAnim);
        if (VolumeBarFront.Background is SolidColorBrush volBar && !volBar.IsFrozen) volBar.BeginAnimation(SolidColorBrush.ColorProperty, defaultColorAnim);

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

        var highlightColor = Color.FromRgb(
            (byte)Math.Min(255, vibrantColor.R + (255 - vibrantColor.R) * 0.42),
            (byte)Math.Min(255, vibrantColor.G + (255 - vibrantColor.G) * 0.42),
            (byte)Math.Min(255, vibrantColor.B + (255 - vibrantColor.B) * 0.42));

        var colorAnimMain = new ColorAnimation { To = vibrantColor, Duration = TimeSpan.FromMilliseconds(500), EasingFunction = _easeQuadOut };
        var colorAnimHighlight = new ColorAnimation { To = highlightColor, Duration = TimeSpan.FromMilliseconds(500), EasingFunction = _easeQuadOut };

        if (Resources["TrackTitleGradient"] is LinearGradientBrush titleBrush)
        {
            titleBrush.SpreadMethod = GradientSpreadMethod.Repeat;
            EnsureTitleGradientSpacing(titleBrush);
            titleBrush.GradientStops[0].BeginAnimation(GradientStop.ColorProperty, colorAnimMain);
            titleBrush.GradientStops[1].BeginAnimation(GradientStop.ColorProperty, colorAnimMain);
            titleBrush.GradientStops[2].BeginAnimation(GradientStop.ColorProperty, colorAnimHighlight);
            titleBrush.GradientStops[3].BeginAnimation(GradientStop.ColorProperty, colorAnimMain);
            titleBrush.GradientStops[4].BeginAnimation(GradientStop.ColorProperty, colorAnimMain);
        }

        if (Resources["TrackTitleNextGradient"] is LinearGradientBrush titleNextBrush)
        {
            titleNextBrush.SpreadMethod = GradientSpreadMethod.Repeat;
            EnsureTitleGradientSpacing(titleNextBrush);
            titleNextBrush.GradientStops[0].BeginAnimation(GradientStop.ColorProperty, colorAnimMain);
            titleNextBrush.GradientStops[1].BeginAnimation(GradientStop.ColorProperty, colorAnimMain);
            titleNextBrush.GradientStops[2].BeginAnimation(GradientStop.ColorProperty, colorAnimHighlight);
            titleNextBrush.GradientStops[3].BeginAnimation(GradientStop.ColorProperty, colorAnimMain);
            titleNextBrush.GradientStops[4].BeginAnimation(GradientStop.ColorProperty, colorAnimMain);
        }

        StartTitleGradientShift();
    }

    private static void EnsureTitleGradientSpacing(LinearGradientBrush brush)
    {
        while (brush.GradientStops.Count < 5)
        {
            brush.GradientStops.Add(new GradientStop(Colors.White, 1));
        }

        brush.GradientStops[0].Offset = 0.00;
        brush.GradientStops[1].Offset = 0.43;
        brush.GradientStops[2].Offset = 0.50;
        brush.GradientStops[3].Offset = 0.57;
        brush.GradientStops[4].Offset = 1.00;
    }

    private void StartTitleGradientShift()
    {
        if (_titleGradientRunning) return;
        _titleGradientRunning = true;

        if (_titleGradientTimer == null)
        {
            _titleGradientTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(30)
            };
            _titleGradientTimer.Tick += TitleGradientTimer_Tick;
        }
        _titleGradientTimer.Start();
    }

    private void StopTitleGradientShift()
    {
        _titleGradientRunning = false;
        _titleGradientTimer?.Stop();
    }

    private void TitleGradientTimer_Tick(object? sender, EventArgs e)
    {
        _titleGradientPhase += 0.012;
        if (_titleGradientPhase > 2.0) _titleGradientPhase -= 2.0;

        double offset = _titleGradientPhase;
        var startPoint = new Point(offset, 0);
        var endPoint = new Point(offset + 1, 0);

        if (Resources["TrackTitleGradient"] is LinearGradientBrush titleBrush)
        {
            titleBrush.StartPoint = startPoint;
            titleBrush.EndPoint = endPoint;
        }

        if (Resources["TrackTitleNextGradient"] is LinearGradientBrush titleNextBrush)
        {
            titleNextBrush.StartPoint = startPoint;
            titleNextBrush.EndPoint = endPoint;
        }
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

        _titleGradientPhase = 0;
        var resetPoint = new Point(0, 0);
        var resetEndPoint = new Point(1, 0);

        if (Resources["TrackTitleGradient"] is LinearGradientBrush tb2)
        {
            tb2.StartPoint = resetPoint;
            tb2.EndPoint = resetEndPoint;
        }
        if (Resources["TrackTitleNextGradient"] is LinearGradientBrush tnb2)
        {
            tnb2.StartPoint = resetPoint;
            tnb2.EndPoint = resetEndPoint;
        }
    }

    #endregion
}
