using System;

namespace VNotch.Services;

internal readonly record struct ShelfViewportRange(int StartIndex, int EndIndexExclusive)
{
    public static ShelfViewportRange Empty => new(0, 0);
}

internal static class ShelfViewportVirtualizer
{
    public static ShelfViewportRange Calculate(
        int itemCount,
        int rowCount,
        double cellWidth,
        double horizontalOffset,
        double viewportWidth,
        int overscanColumns = 1)
    {
        if (itemCount <= 0 || rowCount <= 0 || cellWidth <= 0 || !double.IsFinite(cellWidth))
            return ShelfViewportRange.Empty;

        int columnCount = (itemCount + rowCount - 1) / rowCount;
        double safeOffset = double.IsFinite(horizontalOffset) ? Math.Max(0, horizontalOffset) : 0;
        double safeViewport = double.IsFinite(viewportWidth) && viewportWidth > 0
            ? viewportWidth
            : cellWidth * 4;
        int overscan = Math.Max(0, overscanColumns);

        int firstVisibleColumn = (int)Math.Floor(safeOffset / cellWidth);
        int endVisibleColumnExclusive = (int)Math.Ceiling((safeOffset + safeViewport) / cellWidth);

        int startColumn = Math.Clamp(firstVisibleColumn - overscan, 0, columnCount - 1);
        int endColumnExclusive = Math.Clamp(endVisibleColumnExclusive + overscan, startColumn + 1, columnCount);

        return new ShelfViewportRange(
            startColumn * rowCount,
            Math.Min(itemCount, endColumnExclusive * rowCount));
    }
}
