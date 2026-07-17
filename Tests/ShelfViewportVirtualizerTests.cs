using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class ShelfViewportVirtualizerTests
{
    [Fact]
    public void Calculate_RealizesVisibleColumnsAndOneColumnBuffer()
    {
        var range = ShelfViewportVirtualizer.Calculate(
            itemCount: 100,
            rowCount: 2,
            cellWidth: 56,
            horizontalOffset: 112,
            viewportWidth: 224,
            overscanColumns: 1);

        Assert.Equal(2, range.StartIndex);
        Assert.Equal(14, range.EndIndexExclusive);
    }

    [Fact]
    public void Calculate_ClampsFinalPartialColumnToItemCount()
    {
        var range = ShelfViewportVirtualizer.Calculate(
            itemCount: 9,
            rowCount: 2,
            cellWidth: 56,
            horizontalOffset: 224,
            viewportWidth: 112,
            overscanColumns: 1);

        Assert.Equal(6, range.StartIndex);
        Assert.Equal(9, range.EndIndexExclusive);
    }

    [Theory]
    [InlineData(0, 2, 56)]
    [InlineData(10, 0, 56)]
    [InlineData(10, 2, 0)]
    public void Calculate_InvalidLayout_ReturnsEmpty(int itemCount, int rowCount, double cellWidth)
    {
        var range = ShelfViewportVirtualizer.Calculate(itemCount, rowCount, cellWidth, 0, 200);

        Assert.Equal(ShelfViewportRange.Empty, range);
    }
}
