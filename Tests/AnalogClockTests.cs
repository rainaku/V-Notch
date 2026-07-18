using VNotch.Controls;
using Xunit;

namespace VNotch.Tests;

public sealed class AnalogClockTests
{
    [Fact]
    public void CompactClockFaceKeepsFourDipInsetOnEverySide()
    {
        Assert.Equal(42.0, AnalogClock.CalculateFaceRadius(92, 92));
    }

    [Theory]
    [InlineData(92, 80, 36)]
    [InlineData(3, 3, 0)]
    public void FaceRadiusUsesTheSmallerDimensionWithoutGoingNegative(
        double width, double height, double expectedRadius)
    {
        Assert.Equal(expectedRadius, AnalogClock.CalculateFaceRadius(width, height));
    }
}
