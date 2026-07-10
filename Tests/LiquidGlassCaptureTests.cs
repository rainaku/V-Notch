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
}
