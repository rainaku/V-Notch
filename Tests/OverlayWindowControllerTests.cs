using VNotch.Controllers;
using Xunit;

namespace VNotch.Tests;

public class OverlayWindowControllerTests
{
    [Fact]
    public void CalculateCenteredBounds_UsesPhysicalPixelsAndScreenOffset()
    {
        var bounds = OverlayWindowController.CalculateCenteredBounds(
            screenLeft: -1920, screenWidth: 1920, widthDip: 500, heightDip: 200, dpiScale: 1.5);

        Assert.Equal(-1335, bounds.X);
        Assert.Equal(750, bounds.Width);
        Assert.Equal(300, bounds.Height);
    }
}
