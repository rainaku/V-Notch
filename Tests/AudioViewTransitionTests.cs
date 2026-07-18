using Xunit;

namespace VNotch.Tests;

public class AudioViewTransitionTests
{
    [Theory]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    public void FirstSnapshot_IsNotDeferredBehindOpeningAnimation(
        bool isAudioView, bool isAnimating, bool hasBuiltUi, bool expected)
    {
        Assert.Equal(expected,
            MainWindow.ShouldDeferAudioSnapshotDuringTransition(
                isAudioView, isAnimating, hasBuiltUi));
    }
}
