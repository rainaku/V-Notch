using System;
using System.Buffers;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

internal readonly record struct ArtworkFingerprint(ulong ContentHash, int PixelWidth, int PixelHeight)
{
    private const int SampleEdge = 64;
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    public static ArtworkFingerprint Create(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        int sourceWidth = source.PixelWidth;
        int sourceHeight = source.PixelHeight;
        if (sourceWidth <= 0 || sourceHeight <= 0)
            return new ArtworkFingerprint(0, sourceWidth, sourceHeight);

        BitmapSource sampled = source;
        double scale = Math.Min(1.0, (double)SampleEdge / Math.Max(sourceWidth, sourceHeight));
        if (scale < 1.0)
        {
            var resized = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            resized.Freeze();
            sampled = resized;
        }

        if (sampled.Format != PixelFormats.Bgra32)
        {
            var converted = new FormatConvertedBitmap(sampled, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            sampled = converted;
        }

        int stride = sampled.PixelWidth * 4;
        int byteCount = stride * sampled.PixelHeight;
        byte[] pixels = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            sampled.CopyPixels(pixels, stride, 0);

            ulong hash = FnvOffsetBasis;
            MixInt(ref hash, sourceWidth);
            MixInt(ref hash, sourceHeight);
            MixInt(ref hash, sampled.PixelWidth);
            MixInt(ref hash, sampled.PixelHeight);

            for (int i = 0; i < byteCount; i++)
            {
                hash ^= pixels[i];
                hash *= FnvPrime;
            }

            return new ArtworkFingerprint(hash, sourceWidth, sourceHeight);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    private static void MixInt(ref ulong hash, int value)
    {
        unchecked
        {
            hash ^= (byte)value;
            hash *= FnvPrime;
            hash ^= (byte)(value >> 8);
            hash *= FnvPrime;
            hash ^= (byte)(value >> 16);
            hash *= FnvPrime;
            hash ^= (byte)(value >> 24);
            hash *= FnvPrime;
        }
    }
}
