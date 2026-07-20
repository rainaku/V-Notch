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
    public void LensProfile_PeaksNearOuterEdgeAndIsFlatAtBothEnds(bool broad)
    {
        const double epsilon = 1e-4;

        Assert.Equal(0.0, LiquidGlassController.LensProfile(0.0, broad), 12);
        Assert.Equal(0.0, LiquidGlassController.LensProfile(1.0, broad), 12);
        Assert.True(
            LiquidGlassController.LensProfile(0.25, broad) >
            LiquidGlassController.LensProfile(0.75, broad));
        Assert.InRange(LiquidGlassController.LensProfile(0.25, broad), 0.70, 0.85);
        Assert.InRange(LiquidGlassController.LensProfile(epsilon, broad), 0.0, 1e-5);
        Assert.InRange(LiquidGlassController.LensProfile(1.0 - epsilon, broad), 0.0, 1e-5);
    }

    [Fact]
    public void RefractionAmplitude_DefaultCurveProducesVisibleEdgeFold()
    {
        double rim = LiquidGlassController.ComputeRimWidth(0.23, 1.0, 80.0);
        double amplitude = LiquidGlassController.RefractionAmplitude(rim, 1.0);

        Assert.Equal(23.0, rim, 6);
        Assert.Equal(rim * 0.38, amplitude, 6);
        Assert.True(amplitude < rim * 0.45);
    }

    [Fact]
    public void EdgeBend_IndependentlyScalesOuterRimDisplacement()
    {
        const double rim = 24.0;
        double normal = LiquidGlassController.RefractionAmplitude(rim, 0.7, edgeBend: 1.0);
        double strong = LiquidGlassController.RefractionAmplitude(rim, 0.7, edgeBend: 1.65);

        Assert.Equal(normal * Math.Pow(1.65, 1.5), strong, 8);
    }

    [Fact]
    public void CurrentNarrowRimSettingsStillCreateVisibleBend()
    {
        double amplitude = LiquidGlassController.RefractionAmplitude(
            rimWidth: 8.0, refraction: 0.6, edgeBend: 1.9);

        Assert.True(amplitude > 5.0);
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

    [Theory]
    [InlineData(-46, 664, 0, 1920, 0)]
    [InlineData(1500, 664, 0, 1920, 1256)]
    [InlineData(-1700, 640, -1920, 3840, -1700)]
    public void CaptureOrigin_ClampsWithoutLosingDesktopOffset(
        int requested, int captureLength, int desktopOrigin, int desktopLength, int expected)
    {
        Assert.Equal(expected, LiquidGlassController.ClampCaptureOrigin(
            requested, captureLength, desktopOrigin, desktopLength));
    }

    [Fact]
    public void SamplingMargin_ContainsLensChromaAndFluidOffsets()
    {
        const double rim = 32.0;
        double amplitude = LiquidGlassController.RefractionAmplitude(
            rim, 1.5, bevelMode: 1, edgeBend: 1.9);
        int margin = LiquidGlassController.ComputeSamplingMargin(
            rim, 1.5, 2.0, 2.0, bevelMode: 1, edgeBend: 1.9);

        Assert.True(margin >= Math.Ceiling(amplitude + 8.0 + 4.5 + 3.0));
        Assert.InRange(margin, 12, 512);
    }

    [Fact]
    public void BackdropOptics_UsesTheActualContentTint()
    {
        int[] samples = Enumerable.Repeat(0x2040C0, 16).ToArray();

        var optics = LiquidGlassController.AnalyzeBackdropSamples(samples, 4, 4);

        Assert.Equal(0x20, optics.Red);
        Assert.Equal(0x40, optics.Green);
        Assert.Equal(0xC0, optics.Blue);
        Assert.Equal(0.0, optics.LightX, 8);
        Assert.Equal(0.0, optics.LightY, 8);
        Assert.Equal(0.0, optics.Contrast, 8);
    }

    [Fact]
    public void BackdropOptics_PointsFresnelTowardBrightContent()
    {
        int[] brightRight =
        {
            0x000000, 0x000000, 0xFFFFFF, 0xFFFFFF,
            0x000000, 0x000000, 0xFFFFFF, 0xFFFFFF
        };
        int[] brightTop =
        {
            0xFFFFFF, 0xFFFFFF, 0xFFFFFF, 0xFFFFFF,
            0x000000, 0x000000, 0x000000, 0x000000
        };

        var right = LiquidGlassController.AnalyzeBackdropSamples(brightRight, 4, 2);
        var top = LiquidGlassController.AnalyzeBackdropSamples(brightTop, 4, 2);

        Assert.True(right.LightX > 0.5);
        Assert.InRange(right.LightY, -1e-8, 1e-8);
        Assert.True(top.LightY < -0.9);
        Assert.InRange(top.LightX, -1e-8, 1e-8);
    }
}
