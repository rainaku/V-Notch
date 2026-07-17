using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class AnimationConfigTests
{
    [Theory]
    [InlineData(144, 144, 60)]
    [InlineData(60, 144, 60)]
    [InlineData(60, 50, 50)]
    [InlineData(30, 144, 30)]
    [InlineData(45, 60, 45)]
    public void ComputeTargetFps_UsesLowestValidCap(int configuredFps, int refreshHz, int expected)
    {
        Assert.Equal(expected, AnimationConfig.ComputeTargetFps(configuredFps, refreshHz));
    }

    [Fact]
    public void ComputeTargetFps_FallsBackTo60WhenRefreshRateIsUnavailable()
    {
        Assert.Equal(60, AnimationConfig.ComputeTargetFps(144, null));
    }
}
