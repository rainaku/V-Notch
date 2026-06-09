using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

public static class BitmapBufferPool
{
    private static readonly ConcurrentBag<MemoryStream> _streamPool = new();
    private const int MaxPooledStreams = 8;
    private const int StreamInitialCapacity = 128 * 1024;

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

    public static void ReturnStream(MemoryStream stream)
    {
        if (stream == null) return;

        if (stream.Capacity > 1024 * 1024)
            return;

        if (_streamPool.Count < MaxPooledStreams)
        {
            stream.SetLength(0);
            stream.Position = 0;
            _streamPool.Add(stream);
        }
    }

    public static byte[] RentPixelBuffer(int width, int height)
    {
        int size = width * height * 4;
        return ArrayPool<byte>.Shared.Rent(size);
    }

    public static void ReturnPixelBuffer(byte[] buffer)
    {
        if (buffer != null)
            ArrayPool<byte>.Shared.Return(buffer);
    }

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
            var cropped = new CroppedBitmap(source, cropRect);
            cropped.Freeze();

            BitmapSource cropSource = cropped;
            if (cropped.Format != PixelFormats.Bgra32)
            {
                var converted = new FormatConvertedBitmap(cropped, PixelFormats.Bgra32, null, 0);
                converted.Freeze();
                cropSource = converted;
            }

            pixels = ArrayPool<byte>.Shared.Rent(stride * cropH);
            cropSource.CopyPixels(new Int32Rect(0, 0, cropW, cropH), pixels, stride, 0);

            ms = RentStream();

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

    private static void WriteBmpToStream(MemoryStream ms, byte[] pixels, int width, int height, int stride)
    {
        const int bmpHeaderSize = 14;
        const int dibHeaderSize = 108;
        int pixelDataSize = stride * height;
        int fileSize = bmpHeaderSize + dibHeaderSize + pixelDataSize;

        if (ms.Capacity < fileSize)
            ms.Capacity = fileSize;

        var writer = new BinaryWriter(ms);

        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(fileSize);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write(bmpHeaderSize + dibHeaderSize);

        writer.Write(dibHeaderSize);
        writer.Write(width);
        writer.Write(-height);
        writer.Write((short)1);
        writer.Write((short)32);
        writer.Write(3);
        writer.Write(pixelDataSize);
        writer.Write(3780);
        writer.Write(3780);
        writer.Write(0);
        writer.Write(0);

        writer.Write(0x00FF0000);
        writer.Write(0x0000FF00);
        writer.Write(0x000000FF);
        writer.Write(unchecked((int)0xFF000000));

        writer.Write(0x73524742);

        for (int i = 0; i < 9; i++)
            writer.Write(0);

        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        writer.Write(pixels, 0, pixelDataSize);
        writer.Flush();
    }
}
