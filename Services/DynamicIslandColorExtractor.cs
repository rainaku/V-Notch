using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

/// <summary>
/// Pure palette and color-extraction utilities used by the media background.
///
/// Extracted from <c>MainWindow.MediaBackground.cs</c> so that palette selection,
/// HSL conversions, contrast ratios, dominant-hue bucketing, and adaptive opacity
/// curves no longer live on the <see cref="VNotch.MainWindow"/> god-object.
///
/// Nothing here touches WPF elements or shared window state: callers pass a
/// <see cref="BitmapSource"/> / <see cref="Color"/> in, and get a computed value
/// back. Everything is deterministic and side-effect free.
/// </summary>
internal static class DynamicIslandColorExtractor
{
    public readonly record struct Palette(Color Main, Color Sub);
    public readonly record struct PaletteColor(Color Color, int Population, double Score);

    #region Public entry points

    /// <summary>
    /// Returns a <see cref="Palette"/> suitable for the "dynamic island" backdrop
    /// and a legible light-on-dark sub color for text / chrome tinting.
    /// </summary>
    public static Palette GetDynamicIslandPalette(BitmapSource bitmap)
    {
        var palette = ExtractPalette(bitmap, 8);
        if (palette.Count == 0)
        {
            return new Palette(Color.FromRgb(34, 34, 34), Colors.White);
        }

        var main = palette.OrderByDescending(p => p.Score).First().Color;
        var darkUiBackground = Colors.Black;
        var subCandidate = palette
            .Where(p => ColorDistance(p.Color, main) > 28)
            .OrderByDescending(p => ScoreLightTextCandidate(p.Color, darkUiBackground, p.Score))
            .FirstOrDefault().Color;

        if (subCandidate == default)
        {
            subCandidate = Colors.White;
        }

        var sub = EnsureTextOnDarkBackground(subCandidate, darkUiBackground, 4.5);
        return new Palette(main, sub);
    }

    /// <summary>
    /// Hue-bucketed dominant color of the bitmap. Prefers colorful, mid-luminance
    /// regions near the center of the image, and forces white for truly
    /// monochrome or fully-black artwork.
    /// </summary>
    public static Color GetDominantColor(BitmapSource bitmap)
    {
        try
        {
            var formattedBitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);

            const int sampleSize = 32;
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

            double totalWeightAll = 0;
            double avgR = 0, avgG = 0, avgB = 0;
            double satWeightAll = 0;
            double satWeightedSum = 0;
            double colorfulWeight = 0;
            double darkWeight = 0;

            double centerX = width / 2.0;
            double centerY = height / 2.0;

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

                    if (l < 0.08) continue;

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
                    }
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

                if (avgLumAll < 0.06 && darkRatio > 0.90)
                {
                    return Colors.White;
                }

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

    /// <summary>
    /// Brightens deep colors so they stay readable as text; returns the original
    /// color unchanged if already bright enough.
    /// </summary>
    public static Color EnsureBrightColor(Color c)
    {
        double luminance = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
        if (luminance < 0.5)
        {
            const double factor = 0.7;
            byte r = (byte)(c.R + (255 - c.R) * factor);
            byte g = (byte)(c.G + (255 - c.G) * factor);
            byte b = (byte)(c.B + (255 - c.B) * factor);
            return Color.FromRgb(r, g, b);
        }
        return c;
    }

    /// <summary>
    /// Returns a desaturated-lift for near-monochrome inputs and a vibrant
    /// mid-L version of the hue otherwise. Used to tint UI chrome without
    /// losing legibility.
    /// </summary>
    public static Color GetVibrantColor(Color c)
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
            s = Math.Min(s, 0.90);
        }

        if (s < 0.16)
        {
            byte gray = (byte)Math.Clamp(0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B, 0, 255);
            byte lifted = (byte)Math.Clamp(gray + (255 - gray) * 0.18, 0, 255);
            return Color.FromRgb(lifted, lifted, lifted);
        }

        l = Math.Max(l, 0.65);
        l = Math.Min(l, 0.85);

        return HslToColor(h, s, l);
    }

    /// <summary>
    /// Opacity curve for the main media background. Bright palettes are dimmed
    /// slightly so text remains readable on top of them.
    /// </summary>
    public static double GetAdaptiveBlurOpacity(double luminance)
    {
        const double brightnessBoost = 1.4;
        if (luminance <= 0.72) return Math.Min(1.0, 0.90 * brightnessBoost);
        double t = Math.Clamp((luminance - 0.72) / 0.28, 0.0, 1.0);
        return Math.Min(1.0, (0.90 - t * 0.18) * brightnessBoost);
    }

    /// <summary>
    /// Opacity curve for the blurred image layer. Keeps normal covers crisp
    /// while dimming extremely bright palettes.
    /// </summary>
    public static double GetAdaptiveBlurImageOpacity(double luminance)
    {
        if (luminance <= 0.70) return 0.80;
        double t = Math.Clamp((luminance - 0.70) / 0.30, 0.0, 1.0);
        return 0.80 - t * 0.18;
    }

    #endregion

    #region Palette extraction (hue-bucketed)

    private static List<PaletteColor> ExtractPalette(BitmapSource bitmap, int maxColors)
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

    #endregion

    #region HSL / contrast helpers

    public static (double H, double S, double L) ToHsl(Color c)
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

    public static Color HslToColor(double h, double s, double l)
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

    public static double GetRelativeLuminance(Color c)
    {
        static double Linear(byte v)
        {
            double x = v / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
    }

    public static double GetContrastRatio(Color a, Color b)
    {
        double l1 = GetRelativeLuminance(a);
        double l2 = GetRelativeLuminance(b);
        if (l1 < l2) (l1, l2) = (l2, l1);
        return (l1 + 0.05) / (l2 + 0.05);
    }

    public static double ColorDistance(Color a, Color b)
    {
        int dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
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

    public static Color EnsureTextOnDarkBackground(Color color, Color background, double minRatio)
    {
        var hsl = ToHsl(color);
        Color best = color;

        if (GetRelativeLuminance(best) < 0.18 || hsl.L < 0.40)
        {
            best = HslToColor(hsl.H, Math.Max(0.18, hsl.S), Math.Clamp(Math.Max(hsl.L, 0.60), 0.55, 0.65));
        }

        if (GetContrastRatio(best, background) >= minRatio && GetRelativeLuminance(best) >= 0.18)
        {
            return best;
        }

        for (int step = 0; step <= 100; step++)
        {
            double t = step / 100.0;
            double l = hsl.L + (1.0 - hsl.L) * t;
            var candidate = HslToColor(hsl.H, Math.Max(0.18, hsl.S), Math.Clamp(l, 0.55, 0.72));
            if (GetContrastRatio(candidate, background) >= minRatio && GetRelativeLuminance(candidate) >= 0.18)
            {
                return candidate;
            }
        }

        return Colors.White;
    }

    public static Color EnsureContrast(Color sub, Color main, double minRatio)
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

    #endregion
}
