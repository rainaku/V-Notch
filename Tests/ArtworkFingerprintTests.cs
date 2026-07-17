using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class ArtworkFingerprintTests
{
    [Fact]
    public void Create_EquivalentArtworkInstances_HaveSameFingerprint()
    {
        byte[] pixels = CreatePixels(96, 54, seed: 17);
        BitmapSource first = CreateBitmap(96, 54, pixels);
        BitmapSource second = CreateBitmap(96, 54, (byte[])pixels.Clone());

        Assert.Equal(ArtworkFingerprint.Create(first), ArtworkFingerprint.Create(second));
    }

    [Fact]
    public void Create_DifferentArtwork_HasDifferentFingerprint()
    {
        BitmapSource first = CreateBitmap(96, 54, CreatePixels(96, 54, seed: 17));
        BitmapSource second = CreateBitmap(96, 54, CreatePixels(96, 54, seed: 53));

        Assert.NotEqual(ArtworkFingerprint.Create(first), ArtworkFingerprint.Create(second));
    }

    [Fact]
    public void Create_IncludesOriginalDimensions()
    {
        BitmapSource wide = CreateBitmap(96, 54, CreatePixels(96, 54, seed: 17));
        BitmapSource square = CreateBitmap(54, 54, CreatePixels(54, 54, seed: 17));

        Assert.NotEqual(ArtworkFingerprint.Create(wide), ArtworkFingerprint.Create(square));
    }

    private static BitmapSource CreateBitmap(int width, int height, byte[] pixels)
    {
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] CreatePixels(int width, int height, int seed)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int pixel = i / 4;
            pixels[i] = (byte)((pixel * 13 + seed) % 256);
            pixels[i + 1] = (byte)((pixel * 29 + seed * 3) % 256);
            pixels[i + 2] = (byte)((pixel * 47 + seed * 5) % 256);
            pixels[i + 3] = 255;
        }

        return pixels;
    }
}
