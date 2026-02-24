using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using VNotch.Models;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    #region Media Background & Color Extraction

    private Color _lastDominantColor = Colors.Transparent;

    private void UpdateMediaBackground(MediaInfo? info, bool forceRefresh = false)
    {
        if (info == null || info.Thumbnail == null || !info.IsAnyMediaPlaying)
        {
            HideMediaBackground();
            return;
        }

        var dominantColor = GetDominantColor(info.Thumbnail);

        _ = UpdateBlurredBackgroundAsync(info.Thumbnail);

        if (!forceRefresh && dominantColor == _lastDominantColor && MediaBackground.Opacity > 0.49)
        {
            return;
        }

        _lastDominantColor = dominantColor;

        var targetColor = Color.FromRgb(dominantColor.R, dominantColor.G, dominantColor.B);
        var vibrantTargetColor = GetVibrantColor(targetColor);

        var colorAnim = new ColorAnimation
        {
            To = targetColor,
            Duration = TimeSpan.FromMilliseconds(500),
            EasingFunction = _easeQuadOut
        };

        var uiColorAnim = new ColorAnimation
        {
            To = vibrantTargetColor,
            Duration = TimeSpan.FromMilliseconds(500),
            EasingFunction = _easeQuadOut
        };
        // Note: Image blurred masks handle their own visual cross-fading via Image source.
        double targetOpacity = (_isExpanded && (!_isAnimating || forceRefresh)) ? 0.9 : 0;

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

        var currentBg = ProgressBar.Background as SolidColorBrush;
        if (currentBg == null || currentBg.IsFrozen)
            ProgressBar.Background = new SolidColorBrush(currentBg?.Color ?? Colors.White);

        var currentSt = CurrentTimeText.Foreground as SolidColorBrush;
        if (currentSt == null || currentSt.IsFrozen)
            CurrentTimeText.Foreground = new SolidColorBrush(currentSt?.Color ?? Color.FromRgb(136, 136, 136));

        var currentRt = RemainingTimeText.Foreground as SolidColorBrush;
        if (currentRt == null || currentRt.IsFrozen)
            RemainingTimeText.Foreground = new SolidColorBrush(currentRt?.Color ?? Color.FromRgb(136, 136, 136));

        var currentTitle = TrackTitle.Foreground as SolidColorBrush;
        if (currentTitle == null || currentTitle.IsFrozen)
            TrackTitle.Foreground = new SolidColorBrush(currentTitle?.Color ?? Colors.White);

        var currentTitleNext = TrackTitleNext.Foreground as SolidColorBrush;
        if (currentTitleNext == null || currentTitleNext.IsFrozen)
            TrackTitleNext.Foreground = new SolidColorBrush(currentTitleNext?.Color ?? Colors.White);

        if (ProgressBar.Background is SolidColorBrush pbb && !pbb.IsFrozen)
            pbb.BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);

        if (CurrentTimeText.Foreground is SolidColorBrush ctf && !ctf.IsFrozen)
            ctf.BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);

        if (RemainingTimeText.Foreground is SolidColorBrush rtf && !rtf.IsFrozen)
            rtf.BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);

        if (TrackTitle.Foreground is SolidColorBrush ttf && !ttf.IsFrozen)
            ttf.BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);

        if (TrackTitleNext.Foreground is SolidColorBrush ttnf && !ttnf.IsFrozen)
            ttnf.BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);

        if (Resources["MusicVisualizerBrush"] is SolidColorBrush visualizerBrush && !visualizerBrush.IsFrozen)
        {
            visualizerBrush.BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);
        }

        // Apply same color to Media Controls
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
        EnsureUnfrozenFill(InlinePauseBar1);
        EnsureUnfrozenFill(InlinePauseBar2);
        EnsureUnfrozenFill(InlinePlayIconPath);
        EnsureUnfrozenFill(InlineNextArrow0);
        EnsureUnfrozenFill(InlineNextArrow1);
        EnsureUnfrozenFill(InlineNextArrow2);
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
            
            // Giữ lại sắc độ nhưng boost saturation lên để màu không bị quá nhạt
            s = Math.Max(s, 0.45);
            s = Math.Min(s, 0.95);
        }

        // Tăng sáng (Boost Lightness) - đảm bảo màu tối/chìm vẫn nhìn thấy rõ trên nền đen
        l = Math.Max(l, 0.75); 
        l = Math.Min(l, 0.90);

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
        if (CurrentTimeText.Foreground is SolidColorBrush st && !st.IsFrozen) st.BeginAnimation(SolidColorBrush.ColorProperty, defaultTextAnim);
        if (RemainingTimeText.Foreground is SolidColorBrush rt && !rt.IsFrozen) rt.BeginAnimation(SolidColorBrush.ColorProperty, defaultTextAnim);
        if (TrackTitle.Foreground is SolidColorBrush ttf && !ttf.IsFrozen) ttf.BeginAnimation(SolidColorBrush.ColorProperty, defaultColorAnim);
        if (TrackTitleNext.Foreground is SolidColorBrush ttnf && !ttnf.IsFrozen) ttnf.BeginAnimation(SolidColorBrush.ColorProperty, defaultColorAnim);

        // Reset same colors for Media Controls
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
        ResetUnfrozenFill(InlinePauseBar1);
        ResetUnfrozenFill(InlinePauseBar2);
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

    private Color GetDominantColor(BitmapSource bitmap)
    {
        try
        {
            var formattedBitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            double scaleX = 20.0 / formattedBitmap.PixelWidth;
            double scaleY = 20.0 / formattedBitmap.PixelHeight;
            var small = new TransformedBitmap(formattedBitmap, new ScaleTransform(scaleX, scaleY));
            
            int width = small.PixelWidth;
            int height = small.PixelHeight;
            if (width == 0 || height == 0) return Color.FromRgb(30, 30, 30);

            int stride = width * 4;
            byte[] pixelBuffer = new byte[height * stride];
            small.CopyPixels(pixelBuffer, stride, 0);

            var pixels = new (double R, double G, double B, double H, double S, double L, double weight)[width * height];
            int pCount = 0;

            for (int i = 0; i < pixelBuffer.Length; i += 4)
            {
                double b = pixelBuffer[i] / 255.0;
                double g = pixelBuffer[i + 1] / 255.0;
                double r = pixelBuffer[i + 2] / 255.0;

                double max = Math.Max(r, Math.Max(g, b));
                double min = Math.Min(r, Math.Min(g, b));
                double l = (max + min) / 2.0;

                if (l < 0.15) continue; 

                double s = 0;
                if (max != min)
                {
                    double d = max - min;
                    s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
                }

                if (s < 0.15 && l < 0.3) continue;

                double h = 0;
                if (max != min)
                {
                    double d = max - min;
                    if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
                    else if (max == g) h = (b - r) / d + 2;
                    else h = (r - g) / d + 4;
                    h /= 6.0;
                }

                double luminanceWeight = l;
                if (l > 0.8) luminanceWeight = Math.Max(0, 1.0 - (l - 0.8) * 5);
                
                double weight = Math.Pow(s, 1.5) * Math.Pow(luminanceWeight, 1.5);
                
                if (s > 0.5 && l > 0.4) weight *= 2.0;
                if (s > 0.7 && l > 0.5) weight *= 2.0;

                if (weight > 0.001)
                {
                    pixels[pCount++] = (r, g, b, h, s, l, weight);
                }
            }

            if (pCount > 0)
            {
                double[] bucketWeight = new double[12];
                double[] bucketR = new double[12];
                double[] bucketG = new double[12];
                double[] bucketB = new double[12];

                for (int i = 0; i < pCount; i++)
                {
                    var p = pixels[i];
                    int hBucket = (int)(p.H * 12.0) % 12;
                    if (hBucket < 0) hBucket += 12;

                    bucketWeight[hBucket] += p.weight;
                    bucketR[hBucket] += p.R * p.weight;
                    bucketG[hBucket] += p.G * p.weight;
                    bucketB[hBucket] += p.B * p.weight;
                }

                int bestBucket = -1;
                double maxBw = -1;
                for (int i = 0; i < 12; i++)
                {
                    if (bucketWeight[i] > maxBw)
                    {
                        maxBw = bucketWeight[i];
                        bestBucket = i;
                    }
                }

                if (bestBucket != -1 && maxBw > 0)
                {
                    double sumR = bucketR[bestBucket] / maxBw;
                    double sumG = bucketG[bestBucket] / maxBw;
                    double sumB = bucketB[bestBucket] / maxBw;
                    
                    return Color.FromRgb(
                        (byte)Math.Clamp(sumR * 255.0, 0, 255),
                        (byte)Math.Clamp(sumG * 255.0, 0, 255),
                        (byte)Math.Clamp(sumB * 255.0, 0, 255)
                    );
                }
            }

            double totalR = 0, totalG = 0, totalB = 0;
            int total = 0;
            for (int i = 0; i < pixelBuffer.Length; i += 4)
            {
                totalB += pixelBuffer[i];
                totalG += pixelBuffer[i + 1];
                totalR += pixelBuffer[i + 2];
                total++;
            }
            if (total == 0) return Color.FromRgb(30, 30, 30);
            return Color.FromRgb((byte)(totalR / total), (byte)(totalG / total), (byte)(totalB / total));
        }
        catch
        {
            return Color.FromRgb(30, 30, 30);
        }
    }

    #endregion
}