using Xunit;

namespace VNotch.Tests;

public sealed class SettingsDropDownScrollTests
{
    [Theory]
    [InlineData(80, 240, -120, 122)]
    [InlineData(80, 240, 120, 38)]
    [InlineData(225, 240, -120, 240)]
    [InlineData(10, 240, 120, 0)]
    [InlineData(10, 0, -120, 0)]
    public void ComboWheelInputMovesOnlyWithinTheDropdownRange(
        double currentOffset, double scrollableHeight, int wheelDelta, double expected)
    {
        Assert.Equal(expected,
            SettingsWindow.CalculateDropDownWheelTarget(currentOffset, scrollableHeight, wheelDelta));
    }
}
