using VNotch.Controllers;
using Xunit;

namespace VNotch.Tests;

public sealed class LiquidGlassCaptureTests
{
    [Fact]
    public void CompleteFrame_AcceptsExactAndPaddedImages()
    {
        Assert.True(MagnifierCaptureSource.IsCompleteFrame(320, 80, 320, 80, 1280, 102400));
        Assert.True(MagnifierCaptureSource.IsCompleteFrame(320, 80, 400, 100, 1664, 166400));
    }

    [Theory]
    [InlineData(320, 80, 319, 80, 1280, 102400)]
    [InlineData(320, 80, 320, 79, 1280, 102400)]
    [InlineData(320, 80, 320, 80, 1276, 102400)]
    [InlineData(320, 80, 320, 80, 1280, 102399)]
    public void CompleteFrame_RejectsPartialImages(
        int requestedWidth,
        int requestedHeight,
        int receivedWidth,
        int receivedHeight,
        int receivedStride,
        int bufferLength)
    {
        Assert.False(MagnifierCaptureSource.IsCompleteFrame(
            requestedWidth,
            requestedHeight,
            receivedWidth,
            receivedHeight,
            receivedStride,
            bufferLength));
    }

    [Fact]
    public void PresentCapacity_GrowsInChunksAndNeverShrinks()
    {
        int initial = LiquidGlassController.GrowPresentCapacity(0, 300, 128);
        Assert.True(initial >= 428);
        Assert.Equal(0, initial % 64);
        Assert.Equal(initial, LiquidGlassController.GrowPresentCapacity(initial, 280, 128));

        int grown = LiquidGlassController.GrowPresentCapacity(initial, initial + 1, 128);
        Assert.True(grown >= initial + 128);
        Assert.Equal(0, grown % 64);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LensProfile_IsSymmetricAndFlatAtBothEnds(bool broad)
    {
        const double epsilon = 1e-4;

        Assert.Equal(0.0, LiquidGlassController.LensProfile(0.0, broad), 12);
        Assert.Equal(0.0, LiquidGlassController.LensProfile(1.0, broad), 12);
        Assert.Equal(
            LiquidGlassController.LensProfile(0.25, broad),
            LiquidGlassController.LensProfile(0.75, broad),
            10);
        Assert.InRange(LiquidGlassController.LensProfile(epsilon, broad), 0.0, 1e-5);
        Assert.InRange(LiquidGlassController.LensProfile(1.0 - epsilon, broad), 0.0, 1e-5);
    }

    [Fact]
    public void RefractionAmplitude_DefaultCurveAvoidsExtremeSourceFolding()
    {
        double rim = LiquidGlassController.ComputeRimWidth(0.23, 1.0, 80.0);
        double amplitude = LiquidGlassController.RefractionAmplitude(rim, 1.0);

        Assert.Equal(23.0, rim, 6);
        Assert.Equal(rim * 0.24, amplitude, 6);
        Assert.True(amplitude < rim * 0.3);
    }

    [Theory]
    [InlineData(320.0, 80.0, 320.0, 80.0, true)]
    [InlineData(320.0, 80.0, 321.0, 80.0, true)]
    [InlineData(320.0, 80.0, 352.0, 80.0, false)]
    [InlineData(320.0, 80.0, 320.0, 112.0, false)]
    public void GpuGeometry_RequiresExactSizeTexture(
        double srcW, double srcH, double notchW, double notchH, bool expected)
    {
        Assert.Equal(expected, LiquidGlassController.IsGpuGeometryValid(srcW, srcH, notchW, notchH));
    }

    [Theory]
    [InlineData(8, 154)]
    [InlineData(0, 40)]
    [InlineData(-20, 80)]
    public void BitBltFallback_StartsBelowVisibleNotch(int regionY, int displayHeight)
    {
        int sourceY = LiquidGlassController.ComputeFallbackSourceY(regionY, displayHeight);

        Assert.True(sourceY > regionY + displayHeight);
    }

    [Fact]
    public void SamplingMargin_ContainsLensChromaAndFluidOffsets()
    {
        const double rim = 32.0;
        double amplitude = LiquidGlassController.RefractionAmplitude(rim, 1.5, bevelMode: 1);
        int margin = LiquidGlassController.ComputeSamplingMargin(rim, 1.5, 2.0, 2.0, bevelMode: 1);

        Assert.True(margin >= Math.Ceiling(amplitude + 8.0 + 4.5 + 3.0));
        Assert.InRange(margin, 12, 512);
    }
}
