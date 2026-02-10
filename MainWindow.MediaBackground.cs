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

    private void UpdateMediaBackground(MediaInfo? info)
    {
        if (info == null || info.Thumbnail == null || !info.IsPlaying)
        {
            HideMediaBackground();
            return;
        }

        var dominantColor = GetDominantColor(info.Thumbnail);
        if (dominantColor == _lastDominantColor && MediaBackground.Opacity > 0) return;
        
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
        
        var opacityAnim = new DoubleAnimation
        {
            To = 0.5,
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
        double maxComp = Math.Max(c.R, Math.Max(c.G, c.B));
        if (maxComp == 0) return Color.FromRgb(200, 200, 200);

        double scale = 240.0 / maxComp;
        if (scale < 1.0) scale = 1.0;

        byte r = (byte)Math.Min(255, c.R * scale);
        byte g = (byte)Math.Min(255, c.G * scale);
        byte b = (byte)Math.Min(255, c.B * scale);

        double luminance = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
        if (luminance < 0.6)
        {
            byte avg = (byte)((r + g + b) / 3);
            r = (byte)Math.Min(255, r + (r - avg) * 0.5);
            g = (byte)Math.Min(255, g + (g - avg) * 0.5);
            b = (byte)Math.Min(255, b + (b - avg) * 0.5);
        }

        return Color.FromRgb(r, g, b);
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

    private Color GetDominantColor(BitmapSource bitmap)
    {
        try
        {
            var small = new TransformedBitmap(bitmap, new ScaleTransform(10.0 / bitmap.PixelWidth, 10.0 / bitmap.PixelHeight));
            var pixels = new byte[100 * 4];
            small.CopyPixels(pixels, 40, 0);

            long r = 0, g = 0, b = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                b += pixels[i];
                g += pixels[i + 1];
                r += pixels[i + 2];
            }

            return Color.FromRgb((byte)(r / 100), (byte)(g / 100), (byte)(b / 100));
        }
        catch
        {
            return Color.FromRgb(30, 30, 30);
        }
    }

    #endregion
}
