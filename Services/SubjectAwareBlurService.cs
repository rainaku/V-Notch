using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

public static class SubjectAwareBlurService
{
    public static async Task<BitmapSource?> GetSubjectBlurredAsync(
        BitmapSource source,
        SubjectBounds? subject,
        int downscaleWidth = 96,
        int backgroundBlurRadius = 8,
        int subjectBlurRadius = 3)
    {
        if (source == null) return null;

        // No subject info → uniform blur (cheaper, identical output to before).
        if (subject is not SubjectBounds s)
        {
            return await FastBlurService.GetBlurredImageAsync(source, downscaleWidth, backgroundBlurRadius);
        }

        return await Task.Run(() =>
        {
            try
            {
                int width = Math.Max(64, downscaleWidth);
                int height = (int)(source.PixelHeight * ((double)width / source.PixelWidth));
                if (height < 1) height = 1;
                backgroundBlurRadius = Math.Clamp(backgroundBlurRadius, 1, 32);
                subjectBlurRadius = Math.Clamp(subjectBlurRadius, 0, backgroundBlurRadius);

                // ─── Downscale to working buffer ───
                var formatted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                var small = new TransformedBitmap(formatted,
                    new ScaleTransform((double)width / formatted.PixelWidth, (double)height / formatted.PixelHeight));

                int stride = width * 4;
                int bufLen = height * stride;

                byte[] basePixels = new byte[bufLen];
                small.CopyPixels(basePixels, stride, 0);

                // ─── Heavy-blur background pass ───
                byte[] background = (byte[])basePixels.Clone();
                byte[] tmp = new byte[bufLen];
                for (int i = 0; i < 2; i++)
                {
                    BoxBlurHorizontal(background, tmp, width, height, backgroundBlurRadius);
                    BoxBlurVertical(tmp, background, width, height, backgroundBlurRadius);
                }
                Darken(background, 0.78f); // moodier than FastBlur's 0.96 — subject reads first

                // ─── Lighter-blur subject pass ───
                byte[] subjectLayer = (byte[])basePixels.Clone();
                if (subjectBlurRadius >= 1)
                {
                    BoxBlurHorizontal(subjectLayer, tmp, width, height, subjectBlurRadius);
                    BoxBlurVertical(tmp, subjectLayer, width, height, subjectBlurRadius);
                }
                Brighten(subjectLayer, 1.04f); // gentle lift so subject "glows"

                float cx = Math.Clamp(s.CenterX, 0f, 1f) * width;
                float cy = Math.Clamp(s.CenterY, 0f, 1f) * height;
                float rx = MathF.Max(width * 0.18f, s.Width * width * 0.55f);
                float ry = MathF.Max(height * 0.22f, s.Height * height * 0.55f);
                float feather = 0.40f; // 0..1 of axis length

                byte[] result = new byte[bufLen];
                for (int y = 0; y < height; y++)
                {
                    int row = y * stride;
                    float dy = (y - cy) / ry;
                    for (int x = 0; x < width; x++)
                    {
                        float dx = (x - cx) / rx;
                        float distSq = dx * dx + dy * dy;
                        float dist = MathF.Sqrt(distSq);

                        // mask: 1 at center, smoothstep down to 0 at (1 + feather)
                        float t;
                        if (dist <= 1f)
                        {
                            t = 1f;
                        }
                        else if (dist >= 1f + feather)
                        {
                            t = 0f;
                        }
                        else
                        {
                            float u = (dist - 1f) / feather; // 0..1
                            // smoothstep
                            t = 1f - u * u * (3f - 2f * u);
                        }

                        int p = row + x * 4;
                        byte bb = background[p];
                        byte gb = background[p + 1];
                        byte rb = background[p + 2];
                        byte bs = subjectLayer[p];
                        byte gs = subjectLayer[p + 1];
                        byte rs = subjectLayer[p + 2];

                        result[p]     = (byte)(bb + (bs - bb) * t);
                        result[p + 1] = (byte)(gb + (gs - gb) * t);
                        result[p + 2] = (byte)(rb + (rs - rb) * t);
                        result[p + 3] = 255;
                    }
                }

                var wb = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                wb.WritePixels(new Int32Rect(0, 0, width, height), result, stride, 0);
                wb.Freeze();
                return (BitmapSource)wb;
            }
            catch
            {
                return null;
            }
        });
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static void Darken(byte[] pixels, float factor)
    {
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i]     = (byte)(pixels[i] * factor);
            pixels[i + 1] = (byte)(pixels[i + 1] * factor);
            pixels[i + 2] = (byte)(pixels[i + 2] * factor);
        }
    }

    private static void Brighten(byte[] pixels, float factor)
    {
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i]     = (byte)Math.Min(255, pixels[i] * factor);
            pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * factor);
            pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * factor);
        }
    }

    private static void BoxBlurHorizontal(byte[] source, byte[] target, int w, int h, int radius)
    {
        for (int y = 0; y < h; y++)
        {
            int pBase = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                int r = 0, g = 0, b = 0, a = 0, count = 0;
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int nx = Math.Clamp(x + dx, 0, w - 1);
                    int o = pBase + nx * 4;
                    b += source[o];
                    g += source[o + 1];
                    r += source[o + 2];
                    a += source[o + 3];
                    count++;
                }
                int t = pBase + x * 4;
                target[t]     = (byte)(b / count);
                target[t + 1] = (byte)(g / count);
                target[t + 2] = (byte)(r / count);
                target[t + 3] = (byte)(a / count);
            }
        }
    }

    private static void BoxBlurVertical(byte[] source, byte[] target, int w, int h, int radius)
    {
        for (int x = 0; x < w; x++)
        {
            for (int y = 0; y < h; y++)
            {
                int r = 0, g = 0, b = 0, a = 0, count = 0;
                for (int dy = -radius; dy <= radius; dy++)
                {
                    int ny = Math.Clamp(y + dy, 0, h - 1);
                    int o = (ny * w + x) * 4;
                    b += source[o];
                    g += source[o + 1];
                    r += source[o + 2];
                    a += source[o + 3];
                    count++;
                }
                int t = (y * w + x) * 4;
                target[t]     = (byte)(b / count);
                target[t + 1] = (byte)(g / count);
                target[t + 2] = (byte)(r / count);
                target[t + 3] = (byte)(a / count);
            }
        }
    }
}
