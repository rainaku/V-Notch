using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class PrivacyIndicatorServiceTests
{
    [Theory]
    [InlineData(1L, 0L, true)]
    [InlineData(0L, 0L, false)]
    [InlineData(1L, 2L, false)]
    [InlineData(null, 0L, false)]
    [InlineData(1L, null, false)]
    public void ActiveUsage_RequiresAValidOpenInterval(long? start, long? stop, bool expected)
    {
        Assert.Equal(expected, PrivacyIndicatorService.IsActiveUsage(start, stop));
    }

    [Fact]
    public void ScreenRecording_RequiresSustainedCapture()
    {
        DateTime now = new(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(PrivacyIndicatorService.HasMinimumActiveDuration(
            now.AddMilliseconds(-500).ToFileTimeUtc(), now, TimeSpan.FromSeconds(2)));
        Assert.True(PrivacyIndicatorService.HasMinimumActiveDuration(
            now.AddSeconds(-3).ToFileTimeUtc(), now, TimeSpan.FromSeconds(2)));
        Assert.False(PrivacyIndicatorService.HasMinimumActiveDuration(
            now.AddSeconds(1).ToFileTimeUtc(), now, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Microphone_IgnoresBackgroundServicesButKeepsUserApps()
    {
        var noRegisteredServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.True(PrivacyIndicatorService.IsIgnoredMicrophoneConsumer(
            @"C:#ProgramData#Maono#Ai service#MaonoAiServices.exe", noRegisteredServices));
        Assert.False(PrivacyIndicatorService.IsIgnoredMicrophoneConsumer(
            @"D:#obs-studio#bin#64bit#obs64.exe", noRegisteredServices));
    }

    [Fact]
    public void Microphone_IgnoresExecutablesRegisteredAsWindowsServices()
    {
        string raw = @"C:#Program Files#Vendor#AudioProcessor.exe";
        string path = PrivacyIndicatorService.TryDecodeDesktopConsumerPath(raw)!;
        var registeredServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path };

        Assert.True(PrivacyIndicatorService.IsIgnoredMicrophoneConsumer(raw, registeredServices));
    }

    [Fact]
    public void DesktopConsumerPath_DecodesConsentStoreFormat()
    {
        string? decoded = PrivacyIndicatorService.TryDecodeDesktopConsumerPath(
            @"C:#Program Files#Recorder#recorder.exe");

        Assert.Equal(@"C:\Program Files\Recorder\recorder.exe", decoded);
        Assert.Null(PrivacyIndicatorService.TryDecodeDesktopConsumerPath(
            "Microsoft.WindowsCamera_8wekyb3d8bbwe"));
    }
}
