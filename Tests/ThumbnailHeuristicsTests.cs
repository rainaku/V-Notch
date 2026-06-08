using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class ThumbnailHeuristicsTests
{
    // ── Bitmap fixtures (built in-memory, encoded to PNG, reloaded as a real BitmapImage) ──

    private static BitmapImage FromPixels(int width, int height, byte[] bgra)
    {
        var src = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Solid single-color image.</summary>
    private static BitmapImage Solid(int width, int height, byte r, byte g, byte b)
    {
        var px = new byte[width * height * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255;
        }
        return FromPixels(width, height, px);
    }

    /// <summary>High-saturation red/blue checkerboard — clearly real, colorful artwork.</summary>
    private static BitmapImage Colorful(int width, int height)
    {
        var px = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                bool red = ((x / 8) + (y / 8)) % 2 == 0;
                px[i] = (byte)(red ? 0 : 255);     // B
                px[i + 1] = 0;                      // G
                px[i + 2] = (byte)(red ? 255 : 0);  // R
                px[i + 3] = 255;
            }
        }
        return FromPixels(width, height, px);
    }

    #region IsLikelyPlaceholderThumbnail

    [Fact]
    public void Placeholder_Null_True()
    {
        Assert.True(ThumbnailHeuristics.IsLikelyPlaceholderThumbnail(null));
    }

    [Fact]
    public void Placeholder_SmallSquare_True()
    {
        // Square but <= 320px → treated as placeholder regardless of content.
        Assert.True(ThumbnailHeuristics.IsLikelyPlaceholderThumbnail(Colorful(200, 200)));
    }

    [Fact]
    public void Placeholder_NonSquare_False()
    {
        // Wide aspect ratio is real media art, not a placeholder tile.
        Assert.False(ThumbnailHeuristics.IsLikelyPlaceholderThumbnail(Colorful(640, 320)));
    }

    [Fact]
    public void Placeholder_LargeSquare_LowEntropyGray_True()
    {
        Assert.True(ThumbnailHeuristics.IsLikelyPlaceholderThumbnail(Solid(500, 500, 235, 235, 235)));
    }

    [Fact]
    public void Placeholder_LargeSquare_Colorful_False()
    {
        Assert.False(ThumbnailHeuristics.IsLikelyPlaceholderThumbnail(Colorful(500, 500)));
    }

    #endregion

    #region IsLikelyArtworkCandidate

    [Fact]
    public void Candidate_Null_False()
    {
        Assert.False(ThumbnailHeuristics.IsLikelyArtworkCandidate(null));
    }

    [Fact]
    public void Candidate_LargeColorfulSquare_True()
    {
        Assert.True(ThumbnailHeuristics.IsLikelyArtworkCandidate(Colorful(500, 500)));
    }

    [Fact]
    public void Candidate_TooSmall_False()
    {
        // 300x300 is square and colorful but below the 360px minimum.
        Assert.False(ThumbnailHeuristics.IsLikelyArtworkCandidate(Colorful(300, 300)));
    }

    [Fact]
    public void Candidate_NonSquare_False()
    {
        Assert.False(ThumbnailHeuristics.IsLikelyArtworkCandidate(Colorful(640, 360)));
    }

    [Fact]
    public void Candidate_LargeGraySquare_False()
    {
        // Large square but low-entropy gray → it's a placeholder, not a candidate.
        Assert.False(ThumbnailHeuristics.IsLikelyArtworkCandidate(Solid(500, 500, 235, 235, 235)));
    }

    #endregion

    #region HasLowEntropyMonochromeProfile

    [Fact]
    public void MonochromeProfile_LightGray_True()
    {
        Assert.True(ThumbnailHeuristics.HasLowEntropyMonochromeProfile(Solid(400, 400, 235, 235, 235)));
    }

    [Fact]
    public void MonochromeProfile_Colorful_False()
    {
        Assert.False(ThumbnailHeuristics.HasLowEntropyMonochromeProfile(Colorful(400, 400)));
    }

    [Fact]
    public void MonochromeProfile_SolidBlack_False()
    {
        // Black is monochrome but not bright enough to meet the brightness gate.
        Assert.False(ThumbnailHeuristics.HasLowEntropyMonochromeProfile(Solid(400, 400, 0, 0, 0)));
    }

    #endregion
}
