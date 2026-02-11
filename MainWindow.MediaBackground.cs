using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using VNotch.Services;

namespace VNotch;

/// <summary>
/// Partial class for Media Background color extraction and glow effects
/// </summary>
public partial class MainWindow
{
    #region Media Background & Color Extraction

    private Color _lastDominantColor = Colors.Transparent;
    private string _lastColorSignature = "";

    private void UpdateMediaBackground(MediaInfo? info)
    {
        if (info == null || info.Thumbnail == null || !info.IsAnyMediaPlaying)
        {
            HideMediaBackground();
            return;
        }

        string currentSignature = $"{info.CurrentTrack}|{info.CurrentArtist}";
        if (currentSignature == _lastColorSignature && MediaBackground.Opacity > 0 && _lastDominantColor != Colors.Transparent) return;
        
        var dominantColor = GetDominantColor(info.Thumbnail);
        if (dominantColor == _lastDominantColor && MediaBackground.Opacity > 0) 
        {
            _lastColorSignature = currentSignature;
            return;
        }
        
        _lastColorSignature = currentSignature;
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
        
        double targetOpacity = (_isExpanded && !_isAnimating) ? 0.5 : 0;
        
        var opacityAnim = new DoubleAnimation
        {
            To = targetOpacity,
            Duration = TimeSpan.FromMilliseconds(500),
            EasingFunction = _easeQuadOut
        };

        MediaBackgroundBrush.BeginAnimation(SolidColorBrush.ColorProperty, colorAnim);
        MediaBackground.BeginAnimation(OpacityProperty, opacityAnim);
        
        MediaBackgroundBrush2.BeginAnimation(SolidColorBrush.ColorProperty, colorAnim);
        MediaBackground2.BeginAnimation(OpacityProperty, opacityAnim);

        // Update Progress Bar and Time labels color
        var currentBg = ProgressBar.Background as SolidColorBrush;
        if (currentBg == null || currentBg.IsFrozen)
            ProgressBar.Background = new SolidColorBrush(currentBg?.Color ?? Colors.White);
            
        var currentSt = CurrentTimeText.Foreground as SolidColorBrush;
        if (currentSt == null || currentSt.IsFrozen)
            CurrentTimeText.Foreground = new SolidColorBrush(currentSt?.Color ?? Color.FromRgb(136, 136, 136));
            
        var currentRt = RemainingTimeText.Foreground as SolidColorBrush;
        if (currentRt == null || currentRt.IsFrozen)
            RemainingTimeText.Foreground = new SolidColorBrush(currentRt?.Color ?? Color.FromRgb(136, 136, 136));

        ((SolidColorBrush)ProgressBar.Background).BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);
        ((SolidColorBrush)CurrentTimeText.Foreground).BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);
        ((SolidColorBrush)RemainingTimeText.Foreground).BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);

        if (Resources["MusicVisualizerBrush"] is SolidColorBrush visualizerBrush && !visualizerBrush.IsFrozen)
        {
            visualizerBrush.BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);
        }
    }

    private Color GetVibrantColor(Color c)
    {
        // Convert RGB to HSL for better color manipulation
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double h = 0, s = 0, l = (max + min) / 2.0;

        // If color is too dark (black/dark gray) or effectively grayscale, return White
        // This handles cases where the album art is mostly black or dark
        if (l < 0.2 || (max - min) < 0.05)
        {
            return Colors.White;
        }

        double d = max - min;
        s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);

        // If saturation is very low (grayish), also return White
        if (s < 0.15)
        {
            return Colors.White;
        }

        if (max == r) h = ((g - b) / d + (g < b ? 6 : 0)) / 6.0;
        else if (max == g) h = ((b - r) / d + 2) / 6.0;
        else h = ((r - g) / d + 4) / 6.0;

        // Boost saturation - keep at least 65% for vibrant look
        s = Math.Max(s, 0.65);
        // Clamp saturation
        s = Math.Min(s, 0.95);

        // Ensure luminance is bright enough for dark background but not washed out
        l = Math.Clamp(l, 0.50, 0.75);

        // Convert HSL back to RGB
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
    }

    private void ShowMediaBackground()
    {
        if (!_isExpanded || _isAnimating || _currentMediaInfo == null || !_currentMediaInfo.IsAnyMediaPlaying) return;

        var opacityAnim = new DoubleAnimation
        {
            To = 0.5,
            Duration = TimeSpan.FromMilliseconds(500),
            EasingFunction = _easeQuadOut
        };

        MediaBackground.BeginAnimation(OpacityProperty, opacityAnim);
        MediaBackground2.BeginAnimation(OpacityProperty, opacityAnim);
    }

    private Color GetDominantColor(BitmapSource bitmap)
    {
        try
        {
            // Use 15x15 for better detail without sacrificing much speed
            var small = new TransformedBitmap(bitmap, new ScaleTransform(15.0 / bitmap.PixelWidth, 15.0 / bitmap.PixelHeight));
            byte[] pixelBuffer = new byte[15 * 15 * 4];
            small.CopyPixels(pixelBuffer, 15 * 4, 0);

            double totalWeight = 0;
            double wr = 0, wg = 0, wb = 0;
            
            for (int i = 0; i < pixelBuffer.Length; i += 4)
            {
                double pb = pixelBuffer[i] / 255.0;
                double pg = pixelBuffer[i + 1] / 255.0;
                double pr = pixelBuffer[i + 2] / 255.0;
                
                double max = Math.Max(pr, Math.Max(pg, pb));
                double min = Math.Min(pr, Math.Min(pg, pb));
                double lum = (pr + pg + pb) / 3.0;
                double sat = max == 0 ? 0 : (max - min) / max;
                
                // IGNORE very dark pixels (black bars, shadows)
                if (lum < 0.12) continue;
                
                // Weighting: 
                // 1. Favor saturated colors heavily
                // 2. Favor mid-brightness (avoid pure white/black)
                double weight = Math.Pow(sat, 1.5) + 0.05; 
                
                // Brightness penalty (avoiding washed out colors)
                if (lum > 0.85) weight *= 0.3;
                
                wr += pr * weight;
                wg += pg * weight;
                wb += pb * weight;
                totalWeight += weight;
            }

            // Fallback if no suitable vibrant pixels found
            if (totalWeight < 0.1) 
            {
                // Try again with simpler average of everything
                totalWeight = 0; wr = 0; wg = 0; wb = 0;
                for (int i = 0; i < pixelBuffer.Length; i += 4)
                {
                    wr += pixelBuffer[i + 2];
                    wg += pixelBuffer[i + 1];
                    wb += pixelBuffer[i];
                    totalWeight++;
                }
                return Color.FromRgb((byte)(wr / totalWeight), (byte)(wg / totalWeight), (byte)(wb / totalWeight));
            }

            return Color.FromRgb(
                (byte)Math.Clamp(wr / totalWeight * 255, 0, 255),
                (byte)Math.Clamp(wg / totalWeight * 255, 0, 255),
                (byte)Math.Clamp(wb / totalWeight * 255, 0, 255));
        }
        catch
        {
            return Color.FromRgb(30, 30, 30);
        }
    }

    #endregion
}
