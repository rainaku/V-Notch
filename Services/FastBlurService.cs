using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

public static class FastBlurService
{
    // Increased default downscale width for better quality, adjusted radius
    public static async Task<BitmapSource?> GetBlurredImageAsync(BitmapSource source, int downscaleWidth = 100, int blurRadius = 8)
    {
        if (source == null) return null;

        return await Task.Run(() =>
        {
            try
            {
                int width = downscaleWidth;
                int height = (int)(source.PixelHeight * ((double)width / source.PixelWidth));
                if (height < 1) height = 1;

                var formattedBitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                // 1. Downscale
                var smallBitmap = new TransformedBitmap(formattedBitmap, new ScaleTransform((double)width / formattedBitmap.PixelWidth, (double)height / formattedBitmap.PixelHeight));
                
                int stride = width * 4;
                byte[] pixels = new byte[height * stride];
                smallBitmap.CopyPixels(pixels, stride, 0);

                // 2. Apply fast Gaussian-like blur (3 passes of separated Box Blur)
                byte[] target = new byte[pixels.Length];
                
                // 3 passes of box blur approximates a true Gaussian blur closely
                int passes = 3;
                for (int i = 0; i < passes; i++)
                {
                    BoxBlurHorizontal(pixels, target, width, height, blurRadius);
                    BoxBlurVertical(target, pixels, width, height, blurRadius);
                }

                // Smooth creamy shadow tint, reduced intensity as requested (0.92f = 92% brightness)
                DarkenPixels(pixels, 0.92f);

                var writeableBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                writeableBitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
                writeableBitmap.Freeze();
                
                return (BitmapSource)writeableBitmap;
            }
            catch
            {
                return null;
            }
        });
    }

    private static void DarkenPixels(byte[] pixels, float factor)
    {
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(pixels[i] * factor);     // B
            pixels[i + 1] = (byte)(pixels[i + 1] * factor); // G
            pixels[i + 2] = (byte)(pixels[i + 2] * factor); // R
            // a is i+3, ignored
        }
    }

    private static void BoxBlurHorizontal(byte[] source, byte[] target, int w, int h, int radius)
    {
        for (int y = 0; y < h; y++)
        {
            int pBase = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                int r = 0, g = 0, b = 0, a = 0;
                int count = 0;

                for (int dx = -radius; dx <= radius; dx++)
                {
                    int nx = Math.Clamp(x + dx, 0, w - 1);
                    int offset = pBase + nx * 4;
                    b += source[offset];
                    g += source[offset + 1];
                    r += source[offset + 2];
                    a += source[offset + 3];
                    count++;
                }

                int tOffset = pBase + x * 4;
                target[tOffset] = (byte)(b / count);
                target[tOffset + 1] = (byte)(g / count);
                target[tOffset + 2] = (byte)(r / count);
                target[tOffset + 3] = (byte)(a / count);
            }
        }
    }

    private static void BoxBlurVertical(byte[] source, byte[] target, int w, int h, int radius)
    {
        for (int x = 0; x < w; x++)
        {
            for (int y = 0; y < h; y++)
            {
                int r = 0, g = 0, b = 0, a = 0;
                int count = 0;

                for (int dy = -radius; dy <= radius; dy++)
                {
                    int ny = Math.Clamp(y + dy, 0, h - 1);
                    int offset = (ny * w + x) * 4;
                    b += source[offset];
                    g += source[offset + 1];
                    r += source[offset + 2];
                    a += source[offset + 3];
                    count++;
                }

                int tOffset = (y * w + x) * 4;
                target[tOffset] = (byte)(b / count);
                target[tOffset + 1] = (byte)(g / count);
                target[tOffset + 2] = (byte)(r / count);
                target[tOffset + 3] = (byte)(a / count);
            }
        }
    }
}
