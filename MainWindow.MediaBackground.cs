using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;

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

    private void UpdateMediaBackground(MediaInfo? info, bool forceRefresh = false)
    {
        if (info == null || info.Thumbnail == null || !info.IsAnyMediaPlaying)
        {
            HideMediaBackground();
            return;
        }

        var palette = GetDynamicIslandPalette(info.Thumbnail);
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
            ? GetAdaptiveBlurOpacity(dominantLuminance)
            : 0;

        var opacityAnim = new DoubleAnimation
        {
            To = targetOpacity,
            Duration = TimeSpan.FromMilliseconds(500),
            EasingFunction = _easeQuadOut
        };

        if (targetOpacity > 0)
        {
            MediaBackground.Visibility = Visibility.Visible;
            MediaBackground2.Visibility = Visibility.Visible;
        }
        else
        {
            opacityAnim.Completed += (s, e) =>
            {
                if (MediaBackground.Opacity == 0)
                {
                    MediaBackground.Visibility = Visibility.Collapsed;
                    MediaBackground2.Visibility = Visibility.Collapsed;
                }
            };
        }

        MediaBackground.BeginAnimation(OpacityProperty, opacityAnim);
        MediaBackground2.BeginAnimation(OpacityProperty, opacityAnim);

        double blurImageOpacity = GetAdaptiveBlurImageOpacity(dominantLuminance);
        var blurImageOpacityAnim = new DoubleAnimation
        {
            To = blurImageOpacity,
            Duration = TimeSpan.FromMilliseconds(450),
            EasingFunction = _easeQuadOut
        };
        MediaBackgroundImage.BeginAnimation(UIElement.OpacityProperty, blurImageOpacityAnim);
        MediaBackgroundImage2.BeginAnimation(UIElement.OpacityProperty, blurImageOpacityAnim);

        var currentBg = ProgressBar.Background as SolidColorBrush;
        if (currentBg == null || currentBg.IsFrozen)
            ProgressBar.Background = new SolidColorBrush(currentBg?.Color ?? Colors.White);

        var currentInd = IndeterminateProgress.Background as SolidColorBrush;
        if (currentInd == null || currentInd.IsFrozen)
            IndeterminateProgress.Background = new SolidColorBrush(currentInd?.Color ?? Colors.White);

        var currentSt = CurrentTimeText.Foreground as SolidColorBrush;
        if (currentSt == null || currentSt.IsFrozen)
            CurrentTimeText.Foreground = new SolidColorBrush(currentSt?.Color ?? Color.FromRgb(136, 136, 136));

        var currentRt = RemainingTimeText.Foreground as SolidColorBrush;
        if (currentRt == null || currentRt.IsFrozen)
            RemainingTimeText.Foreground = new SolidColorBrush(currentRt?.Color ?? Color.FromRgb(136, 136, 136));

        var currentCompactTitle = CompactTitleMarquee.Foreground as SolidColorBrush;
        if (currentCompactTitle == null || currentCompactTitle.IsFrozen)
            CompactTitleMarquee.Foreground = new SolidColorBrush(currentCompactTitle?.Color ?? Colors.White);


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

        
        var currentVolIcon = VolumeIcon.Foreground as SolidColorBrush;
        if (currentVolIcon == null || currentVolIcon.IsFrozen) VolumeIcon.Foreground = new SolidColorBrush(currentVolIcon?.Color ?? Color.FromRgb(136, 136, 136));
        ((SolidColorBrush)VolumeIcon.Foreground).BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);

        var currentVolBar = VolumeBarFront.Background as SolidColorBrush;
        if (currentVolBar == null || currentVolBar.IsFrozen) VolumeBarFront.Background = new SolidColorBrush(currentVolBar?.Color ?? Colors.White);
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

    private void FadeToBlackThenUpdate(MediaInfo info)
    {
        var fadeToBlack = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = _easeQuadOut
        };

        fadeToBlack.Completed += (s, e) =>
        {
            _isFadingTrack = false;
            UpdateMediaBackground(info, forceRefresh: true);
        };

        MediaBackground.BeginAnimation(OpacityProperty, fadeToBlack);
        MediaBackground2.BeginAnimation(OpacityProperty, fadeToBlack);
    }

    private Color GetVibrantColor(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double h = 0, s = 0, l = (max + min) / 2.0;

        double d = max - min;
        
        if (d > 0)
        {
            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);

            if (max == r) h = ((g - b) / d + (g < b ? 6 : 0)) / 6.0;
            else if (max == g) h = ((b - r) / d + 2) / 6.0;
            else h = ((r - g) / d + 4) / 6.0;
            // Keep natural saturation; avoid forcing vivid colors for near-monochrome art.
            s = Math.Min(s, 0.90);
        }

        // If source color is low-saturation, keep it neutral instead of tinting.
        if (s < 0.16)
        {
            byte gray = (byte)Math.Clamp((0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B), 0, 255);
            byte lifted = (byte)Math.Clamp(gray + (255 - gray) * 0.18, 0, 255);
            return Color.FromRgb(lifted, lifted, lifted);
        }

        l = Math.Max(l, 0.65); 
        l = Math.Min(l, 0.85);

        return HslToColor(h, s, l);
    }

    private static Color HslToColor(double h, double s, double l)
    {
        double r, g, b;
        if (s == 0)
        {
            r = g = b = l;
        }
        else
        {
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;
            r = HueToRgb(p, q, h + 1.0 / 3.0);
            g = HueToRgb(p, q, h);
            b = HueToRgb(p, q, h - 1.0 / 3.0);
        }
        return Color.FromRgb(
            (byte)Math.Clamp(r * 255, 0, 255),
            (byte)Math.Clamp(g * 255, 0, 255),
            (byte)Math.Clamp(b * 255, 0, 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
        if (t < 1.0 / 2.0) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
        return p;
    }

    private Color EnsureBrightColor(Color c)
    {
        double luminance = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
        if (luminance < 0.5)
        {
            double factor = 0.7;
            byte r = (byte)(c.R + (255 - c.R) * factor);
            byte g = (byte)(c.G + (255 - c.G) * factor);
            byte b = (byte)(c.B + (255 - c.B) * factor);
            return Color.FromRgb(r, g, b);
        }
        return c;
    }

    private static double GetAdaptiveBlurImageOpacity(double luminance)
    {
        // Keep normal covers close to current look, dim very bright palettes.
        if (luminance <= 0.70) return 0.80;
        double t = Math.Clamp((luminance - 0.70) / 0.30, 0.0, 1.0);
        return 0.80 - t * 0.18; // down to ~0.62
    }

    private static double GetAdaptiveBlurOpacity(double luminance)
    {
        if (luminance <= 0.72) return 0.90;
        double t = Math.Clamp((luminance - 0.72) / 0.28, 0.0, 1.0);
        return 0.90 - t * 0.18; // down to ~0.72
    }

    private void HideMediaBackground()
    {
        if (MediaBackground.Opacity == 0) return;

        _lastDominantColor = Colors.Transparent;
        var opacityAnim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(400))
        {
            EasingFunction = _easePowerIn2
        };

        opacityAnim.Completed += (s, e) =>
        {
            if (MediaBackground.Opacity == 0)
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
        catch { }
    }

    private readonly record struct DynamicIslandPalette(Color Main, Color Sub);
    private readonly record struct PaletteColor(Color Color, int Population, double Score);

    private DynamicIslandPalette GetDynamicIslandPalette(BitmapSource bitmap)
    {
        var palette = ExtractPalette(bitmap, 8);
        if (palette.Count == 0) return new DynamicIslandPalette(Color.FromRgb(34, 34, 34), Colors.White);

        var main = palette.OrderByDescending(p => p.Score).First().Color;
        var darkUiBackground = Colors.Black;
        var subCandidate = palette
            .Where(p => ColorDistance(p.Color, main) > 28)
            .OrderByDescending(p => ScoreLightTextCandidate(p.Color, darkUiBackground, p.Score))
            .FirstOrDefault().Color;

        if (subCandidate == default) subCandidate = Colors.White;
        var sub = EnsureTextOnDarkBackground(subCandidate, darkUiBackground, 4.5);
        return new DynamicIslandPalette(main, sub);
    }

    private List<PaletteColor> ExtractPalette(BitmapSource bitmap, int maxColors)
    {
        try
        {
            var formattedBitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            const int sampleSize = 50;
            double scale = Math.Min((double)sampleSize / formattedBitmap.PixelWidth, (double)sampleSize / formattedBitmap.PixelHeight);
            scale = Math.Min(1.0, Math.Max(0.01, scale));
            var small = new TransformedBitmap(formattedBitmap, new ScaleTransform(scale, scale));

            int width = small.PixelWidth;
            int height = small.PixelHeight;
            if (width <= 0 || height <= 0) return new List<PaletteColor>();

            int stride = width * 4;
            byte[] pixels = new byte[height * stride];
            small.CopyPixels(pixels, stride, 0);

            var buckets = new Dictionary<int, (double R, double G, double B, int Count)>();
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = (y * stride) + (x * 4);
                    byte a = pixels[i + 3];
                    if (a < 80) continue;

                    byte b = pixels[i];
                    byte g = pixels[i + 1];
                    byte r = pixels[i + 2];
                    int key = (r >> 4) << 8 | (g >> 4) << 4 | (b >> 4);
                    buckets.TryGetValue(key, out var bucket);
                    buckets[key] = (bucket.R + r, bucket.G + g, bucket.B + b, bucket.Count + 1);
                }
            }

            int maxPopulation = Math.Max(1, buckets.Values.Select(b => b.Count).DefaultIfEmpty(1).Max());
            return buckets.Values
                .Where(b => b.Count > 0)
                .Select(b =>
                {
                    var c = Color.FromRgb((byte)(b.R / b.Count), (byte)(b.G / b.Count), (byte)(b.B / b.Count));
                    var hsl = ToHsl(c);
                    double pop = b.Count / (double)maxPopulation;
                    double lightnessScore = hsl.L < 0.15 || hsl.L > 0.90 ? 0.08 : 1.0 - Math.Abs(hsl.L - 0.52) * 0.55;
                    double vibrancy = Math.Pow(hsl.S, 0.72) * lightnessScore;
                    double score = (vibrancy * 0.68 + pop * 0.32) * b.Count;
                    return new PaletteColor(c, b.Count, score);
                })
                .OrderByDescending(p => p.Score)
                .Take(maxColors)
                .ToList();
        }
        catch
        {
            return new List<PaletteColor>();
        }
    }

    private static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double h = 0, s = 0, l = (max + min) / 2.0;
        double d = max - min;
        if (d > 0.0001)
        {
            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
            if (max == r) h = ((g - b) / d + (g < b ? 6 : 0)) / 6.0;
            else if (max == g) h = ((b - r) / d + 2) / 6.0;
            else h = ((r - g) / d + 4) / 6.0;
        }
        return (h, s, l);
    }

    private static double ScoreLightTextCandidate(Color color, Color background, double paletteScore)
    {
        double luminance = GetRelativeLuminance(color);
        double contrast = GetContrastRatio(color, background);
        var hsl = ToHsl(color);
        double lightSwatchBonus = hsl.L >= 0.45 ? 1000 : 0;
        double contrastBonus = contrast >= 4.5 ? 600 : 0;
        double darkPenalty = luminance < 0.18 ? 1200 : 0;
        return paletteScore + lightSwatchBonus + contrastBonus - darkPenalty;
    }

    private static Color EnsureTextOnDarkBackground(Color color, Color background, double minRatio)
    {
        var hsl = ToHsl(color);
        Color best = color;

        if (GetRelativeLuminance(best) < 0.18 || hsl.L < 0.40)
        {
            best = HslToColor(hsl.H, Math.Max(0.18, hsl.S), Math.Clamp(Math.Max(hsl.L, 0.60), 0.55, 0.65));
        }

        if (GetContrastRatio(best, background) >= minRatio && GetRelativeLuminance(best) >= 0.18)
            return best;

        for (int step = 0; step <= 100; step++)
        {
            double t = step / 100.0;
            double l = hsl.L + (1.0 - hsl.L) * t;
            var candidate = HslToColor(hsl.H, Math.Max(0.18, hsl.S), Math.Clamp(l, 0.55, 0.72));
            if (GetContrastRatio(candidate, background) >= minRatio && GetRelativeLuminance(candidate) >= 0.18)
                return candidate;
        }

        return Colors.White;
    }

    private static Color EnsureContrast(Color sub, Color main, double minRatio)
    {
        var hsl = ToHsl(sub);
        bool lighten = GetRelativeLuminance(main) < 0.45;
        Color best = sub;
        double bestRatio = GetContrastRatio(best, main);

        for (int step = 0; step <= 100 && bestRatio < minRatio; step++)
        {
            double t = step / 100.0;
            double l = lighten ? hsl.L + (1.0 - hsl.L) * t : hsl.L * (1.0 - t);
            var candidate = HslToColor(hsl.H, Math.Max(0.18, hsl.S), Math.Clamp(l, 0.0, 1.0));
            double ratio = GetContrastRatio(candidate, main);
            if (ratio > bestRatio)
            {
                best = candidate;
                bestRatio = ratio;
            }
        }

        if (bestRatio < minRatio)
        {
            var whiteRatio = GetContrastRatio(Colors.White, main);
            var blackRatio = GetContrastRatio(Colors.Black, main);
            best = whiteRatio >= blackRatio ? Colors.White : Colors.Black;
        }

        return best;
    }

    private static double GetContrastRatio(Color a, Color b)
    {
        double l1 = GetRelativeLuminance(a);
        double l2 = GetRelativeLuminance(b);
        if (l1 < l2) (l1, l2) = (l2, l1);
        return (l1 + 0.05) / (l2 + 0.05);
    }

    private static double GetRelativeLuminance(Color c)
    {
        static double Linear(byte v)
        {
            double x = v / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
    }

    private static double ColorDistance(Color a, Color b)
    {
        int dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }

    private Color GetDominantColor(BitmapSource bitmap)
    {
        try
        {
            var formattedBitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            
            int sampleSize = 32;
            double scaleX = (double)sampleSize / formattedBitmap.PixelWidth;
            double scaleY = (double)sampleSize / formattedBitmap.PixelHeight;
            var small = new TransformedBitmap(formattedBitmap, new ScaleTransform(scaleX, scaleY));

            int width = small.PixelWidth;
            int height = small.PixelHeight;
            if (width == 0 || height == 0) return Color.FromRgb(30, 30, 30);

            int stride = width * 4;
            byte[] pixelBuffer = new byte[height * stride];
            small.CopyPixels(pixelBuffer, stride, 0);

            
            const int BUCKET_COUNT = 24;
            double[] bucketWeight = new double[BUCKET_COUNT];
            double[] bucketR = new double[BUCKET_COUNT];
            double[] bucketG = new double[BUCKET_COUNT];
            double[] bucketB = new double[BUCKET_COUNT];
            double[] bucketS = new double[BUCKET_COUNT]; 
            int[] bucketCount = new int[BUCKET_COUNT];

            double totalWeightAll = 0;
            double avgR = 0, avgG = 0, avgB = 0;
            double satWeightAll = 0;
            double satWeightedSum = 0;
            double colorfulWeight = 0;
            double darkWeight = 0;

            double centerX = width / 2.0;
            double centerY = height / 2.0;
            double maxDist = Math.Sqrt(centerX * centerX + centerY * centerY);

            int pixelIndex = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = (y * stride) + (x * 4);
                    double b = pixelBuffer[i] / 255.0;
                    double g = pixelBuffer[i + 1] / 255.0;
                    double r = pixelBuffer[i + 2] / 255.0;

                    
                    double dx = (x - centerX) / centerX;
                    double dy = (y - centerY) / centerY;
                    double distNorm = Math.Sqrt(dx * dx + dy * dy) / 1.414;
                    double spatialWeight = 1.0 - distNorm * 0.5; 

                    double max = Math.Max(r, Math.Max(g, b));
                    double min = Math.Min(r, Math.Min(g, b));
                    double l = (max + min) / 2.0;

                    
                    avgR += r * spatialWeight;
                    avgG += g * spatialWeight;
                    avgB += b * spatialWeight;
                    totalWeightAll += spatialWeight;
                    if (l < 0.14) darkWeight += spatialWeight;

                    
                    if (l < 0.08) { pixelIndex++; continue; }

                    double s = 0, h = 0;
                    double d = max - min;
                    if (d > 0.001)
                    {
                        s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
                        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
                        else if (max == g) h = (b - r) / d + 2;
                        else h = (r - g) / d + 4;
                        h /= 6.0;
                    }

                    
                    
                    satWeightAll += spatialWeight;
                    satWeightedSum += s * spatialWeight;
                    if (s > 0.22) colorfulWeight += spatialWeight;

                    double satWeight = 0.3 + 0.7 * s; 
                    double lumWeight = l;
                    if (l > 0.85) lumWeight = Math.Max(0.1, 1.0 - (l - 0.85) * 4);
                    if (l < 0.2) lumWeight = l * 3; 

                    double weight = satWeight * lumWeight * spatialWeight;

                    
                    if (s > 0.3 && l > 0.2 && l < 0.8) weight *= 1.5;
                    if (s > 0.5 && l > 0.3 && l < 0.75) weight *= 1.3;

                    if (weight > 0.001)
                    {
                        int hBucket = (int)(h * BUCKET_COUNT) % BUCKET_COUNT;
                        if (hBucket < 0) hBucket += BUCKET_COUNT;

                        bucketWeight[hBucket] += weight;
                        bucketR[hBucket] += r * weight;
                        bucketG[hBucket] += g * weight;
                        bucketB[hBucket] += b * weight;
                        bucketS[hBucket] += s * weight;
                        bucketCount[hBucket]++;
                    }

                    pixelIndex++;
                }
            }

            
            
            double bestScore = -1;
            int bestCenter = -1;

            for (int i = 0; i < BUCKET_COUNT; i++)
            {
                double regionWeight = 0;
                double regionSat = 0;
                double regionSatWeight = 0;

                for (int offset = -1; offset <= 1; offset++)
                {
                    int idx = (i + offset + BUCKET_COUNT) % BUCKET_COUNT;
                    regionWeight += bucketWeight[idx];
                    regionSat += bucketS[idx];
                    regionSatWeight += bucketWeight[idx] > 0 ? bucketWeight[idx] : 0;
                }

                
                double avgSat = regionSatWeight > 0 ? regionSat / regionSatWeight : 0;
                double score = regionWeight * (1.0 + avgSat * 0.5);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestCenter = i;
                }
            }

            if (totalWeightAll > 0)
            {
                double avgSatAll = satWeightAll > 0 ? satWeightedSum / satWeightAll : 0;
                double colorfulRatio = colorfulWeight / totalWeightAll;
                double darkRatio = darkWeight / totalWeightAll;
                double avgLumAll = 0.2126 * (avgR / totalWeightAll) + 0.7152 * (avgG / totalWeightAll) + 0.0722 * (avgB / totalWeightAll);
                double meanR = avgR / totalWeightAll;
                double meanG = avgG / totalWeightAll;
                double meanB = avgB / totalWeightAll;
                double colorSpread = Math.Max(meanR, Math.Max(meanG, meanB)) - Math.Min(meanR, Math.Min(meanG, meanB));

                // Force white only when thumbnail is almost fully black.
                if (avgLumAll < 0.06 && darkRatio > 0.90)
                {
                    return Colors.White;
                }

                // Force white for truly monochrome/single-tone thumbnails.
                bool isMonochrome = avgSatAll < 0.10 && colorfulRatio < 0.10 && colorSpread < 0.08;
                if (isMonochrome)
                {
                    return Colors.White;
                }
            }

            if (bestCenter >= 0 && bestScore > 0)
            {
                
                double sumR = 0, sumG = 0, sumB = 0, sumW = 0;
                for (int offset = -1; offset <= 1; offset++)
                {
                    int idx = (bestCenter + offset + BUCKET_COUNT) % BUCKET_COUNT;
                    sumR += bucketR[idx];
                    sumG += bucketG[idx];
                    sumB += bucketB[idx];
                    sumW += bucketWeight[idx];
                }

                if (sumW > 0)
                {
                    return Color.FromRgb(
                        (byte)Math.Clamp(sumR / sumW * 255.0, 0, 255),
                        (byte)Math.Clamp(sumG / sumW * 255.0, 0, 255),
                        (byte)Math.Clamp(sumB / sumW * 255.0, 0, 255));
                }
            }

            
            if (totalWeightAll > 0)
            {
                return Color.FromRgb(
                    (byte)Math.Clamp(avgR / totalWeightAll * 255.0, 0, 255),
                    (byte)Math.Clamp(avgG / totalWeightAll * 255.0, 0, 255),
                    (byte)Math.Clamp(avgB / totalWeightAll * 255.0, 0, 255));
            }

            return Color.FromRgb(30, 30, 30);
        }
        catch
        {
            return Color.FromRgb(30, 30, 30);
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
