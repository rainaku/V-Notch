using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

/// <summary>
/// Pools reusable buffers and MemoryStreams for thumbnail processing to reduce GC pressure.
/// Thread-safe. All returned objects must be returned via the corresponding Return* method.
/// </summary>
public static class BitmapBufferPool
{
    // ─── MemoryStream Pool ───
    // Typical thumbnail PNG is 20-80KB. Pool streams with initial capacity 128KB.
    private static readonly ConcurrentBag<MemoryStream> _streamPool = new();
    private const int MaxPooledStreams = 8;
    private const int StreamInitialCapacity = 128 * 1024;

    /// <summary>
    /// Rents a MemoryStream from the pool (or creates a new one).
    /// The stream is reset to position 0 with length 0.
    /// </summary>
    public static MemoryStream RentStream()
    {
        if (_streamPool.TryTake(out var stream))
        {
            stream.SetLength(0);
            stream.Position = 0;
            return stream;
        }
        return new MemoryStream(StreamInitialCapacity);
    }

    /// <summary>
    /// Returns a MemoryStream to the pool. Do not use the stream after returning.
    /// </summary>
    public static void ReturnStream(MemoryStream stream)
    {
        if (stream == null) return;

        // Don't pool streams that grew too large (> 1MB) — let GC reclaim them
        if (stream.Capacity > 1024 * 1024)
            return;

        if (_streamPool.Count < MaxPooledStreams)
        {
            stream.SetLength(0);
            stream.Position = 0;
            _streamPool.Add(stream);
        }
    }

    // ─── Pixel Buffer Pool (delegates to ArrayPool) ───

    /// <summary>
    /// Rents a byte buffer for pixel data. Size = width * height * 4 (BGRA32).
    /// </summary>
    public static byte[] RentPixelBuffer(int width, int height)
    {
        int size = width * height * 4;
        return ArrayPool<byte>.Shared.Rent(size);
    }

    /// <summary>
    /// Returns a pixel buffer to the pool.
    /// </summary>
    public static void ReturnPixelBuffer(byte[] buffer)
    {
        if (buffer != null)
            ArrayPool<byte>.Shared.Return(buffer);
    }

    // ─── BitmapImage Creation (zero-copy from WriteableBitmap) ───

    /// <summary>
    /// Creates a frozen BitmapImage from a cropped region of a BitmapSource.
    /// Uses pooled buffers internally to minimize allocations.
    /// This is the optimized replacement for the CroppedBitmap → PNG encode → BitmapImage decode path.
    /// </summary>
    public static BitmapImage? CreateCroppedBitmapImage(BitmapSource source, Int32Rect cropRect)
    {
        if (source == null) return null;

        int cropW = cropRect.Width;
        int cropH = cropRect.Height;
        if (cropW <= 0 || cropH <= 0) return null;

        int stride = cropW * 4;
        byte[]? pixels = null;
        MemoryStream? ms = null;

        try
        {
            // Crop the source
            var cropped = new CroppedBitmap(source, cropRect);
            cropped.Freeze();

            // Ensure BGRA32 format
            BitmapSource cropSource = cropped;
            if (cropped.Format != PixelFormats.Bgra32)
            {
                var converted = new FormatConvertedBitmap(cropped, PixelFormats.Bgra32, null, 0);
                converted.Freeze();
                cropSource = converted;
            }

            // Copy pixels into pooled buffer
            pixels = ArrayPool<byte>.Shared.Rent(stride * cropH);
            cropSource.CopyPixels(new Int32Rect(0, 0, cropW, cropH), pixels, stride, 0);

            // Write directly to WriteableBitmap (avoids PNG encode/decode round-trip for WPF usage)
            // But since the caller expects a BitmapImage (frozen, shareable), we use BMP encoding
            // which is ~10x faster than PNG (no compression overhead).
            ms = RentStream();

            // BMP header for BGRA32 (fastest encode — no compression)
            WriteBmpToStream(ms, pixels, cropW, cropH, stride);

            ms.Position = 0;
            var bitmapImage = new BitmapImage();
            bitmapImage.BeginInit();
            bitmapImage.StreamSource = ms;
            bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
            bitmapImage.EndInit();
            bitmapImage.Freeze();

            return bitmapImage;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (pixels != null)
                ArrayPool<byte>.Shared.Return(pixels);
            if (ms != null)
                ReturnStream(ms);
        }
    }

    /// <summary>
    /// Creates a frozen BitmapImage from raw BGRA32 pixel data.
    /// Uses pooled MemoryStream internally.
    /// </summary>
    public static BitmapImage? CreateFromPixels(byte[] pixels, int width, int height, int stride)
    {
        if (pixels == null || width <= 0 || height <= 0) return null;

        MemoryStream? ms = null;
        try
        {
            ms = RentStream();
            WriteBmpToStream(ms, pixels, width, height, stride);
            ms.Position = 0;

            var bitmapImage = new BitmapImage();
            bitmapImage.BeginInit();
            bitmapImage.StreamSource = ms;
            bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
            bitmapImage.EndInit();
            bitmapImage.Freeze();

            return bitmapImage;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (ms != null)
                ReturnStream(ms);
        }
    }

    // ─── BMP Writer (fastest possible encode for BGRA32) ───

    /// <summary>
    /// Writes a minimal BMP file (BITMAPV4 with alpha) to the stream.
    /// BMP encoding has zero compression overhead — just a header + raw pixels.
    /// This is ~10-20x faster than PNG encoding for the same data.
    /// </summary>
    private static void WriteBmpToStream(MemoryStream ms, byte[] pixels, int width, int height, int stride)
    {
        // BMP with BITMAPV4HEADER for BGRA32 support
        const int bmpHeaderSize = 14;
        const int dibHeaderSize = 108; // BITMAPV4HEADER
        int pixelDataSize = stride * height;
        int fileSize = bmpHeaderSize + dibHeaderSize + pixelDataSize;

        // Ensure stream has capacity
        if (ms.Capacity < fileSize)
            ms.Capacity = fileSize;

        var writer = new BinaryWriter(ms);

        // ─── BMP File Header (14 bytes) ───
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(fileSize);          // File size
        writer.Write((short)0);          // Reserved
        writer.Write((short)0);          // Reserved
        writer.Write(bmpHeaderSize + dibHeaderSize); // Pixel data offset

        // ─── BITMAPV4HEADER (108 bytes) ───
        writer.Write(dibHeaderSize);     // Header size
        writer.Write(width);             // Width
        writer.Write(-height);           // Height (negative = top-down)
        writer.Write((short)1);          // Planes
        writer.Write((short)32);         // Bits per pixel
        writer.Write(3);                 // Compression = BI_BITFIELDS
        writer.Write(pixelDataSize);     // Image size
        writer.Write(3780);              // X pixels per meter (~96 DPI)
        writer.Write(3780);              // Y pixels per meter
        writer.Write(0);                 // Colors used
        writer.Write(0);                 // Important colors

        // Channel masks (BGRA32)
        writer.Write(0x00FF0000);        // Red mask
        writer.Write(0x0000FF00);        // Green mask
        writer.Write(0x000000FF);        // Blue mask
        writer.Write(unchecked((int)0xFF000000)); // Alpha mask

        // Color space type (LCS_sRGB)
        writer.Write(0x73524742);        // 'sRGB'

        // CIEXYZTRIPLE endpoints (36 bytes of zeros)
        for (int i = 0; i < 9; i++)
            writer.Write(0);

        // Gamma values (12 bytes of zeros)
        writer.Write(0); // Red gamma
        writer.Write(0); // Green gamma
        writer.Write(0); // Blue gamma

        // ─── Pixel Data ───
        writer.Write(pixels, 0, pixelDataSize);
        writer.Flush();
    }
}
