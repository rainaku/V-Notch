using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class SettingsMigratorTests
{
    [Fact]
    public void Migrate_Version3_AddsDynamicIslandWidthFromExistingWidth()
    {
        const string rawJson = """
            {
              "SettingsVersion": 3,
              "Width": 230
            }
            """;

        var (settings, migrated) = SettingsMigrator.Migrate(rawJson);

        Assert.True(migrated);
        Assert.Equal(SettingsMigrator.CurrentVersion, settings.SettingsVersion);
        Assert.Equal(260, settings.DynamicIslandWidth);
    }

    [Fact]
    public void Migrate_PreservesExistingDynamicIslandWidth()
    {
        const string rawJson = """
            {
              "SettingsVersion": 3,
              "Width": 230,
              "DynamicIslandWidth": 320
            }
            """;

        var (settings, _) = SettingsMigrator.Migrate(rawJson);

        Assert.Equal(320, settings.DynamicIslandWidth);
    }

    [Fact]
    public void NewDefaults_UseSliderAlignedDynamicIslandWidth()
    {
        var settings = new NotchSettings();

        Assert.Equal(220, settings.DynamicIslandWidth);
    }

    [Fact]
    public void NewDefaults_UseReadableSpotifyCanvasBrightness()
    {
        var settings = new NotchSettings();

        Assert.Equal(0.7, settings.SpotifyCanvasBrightness);
    }

    [Fact]
    public void NewDefaults_Use120Fps()
    {
        var settings = new NotchSettings();

        Assert.Equal(120, settings.AnimationFps);
    }

    [Fact]
    public void NewDefaults_EnableGpuGlassWithoutHidingNotchFromCapture()
    {
        var settings = new NotchSettings();

        Assert.True(settings.LiquidGlass.UseGpuRefraction);
        Assert.False(settings.LiquidGlass.HideFromScreenCapture);
    }

    [Fact]
    public void CurrentSettings_PreserveExplicitGpuOptOut()
    {
        string rawJson = $$"""
            {
              "SettingsVersion": {{SettingsMigrator.CurrentVersion}},
              "LiquidGlass": {
                "UseGpuRefraction": false
              }
            }
            """;

        var (settings, migrated) = SettingsMigrator.Migrate(rawJson);

        Assert.False(migrated);
        Assert.False(settings.LiquidGlass.UseGpuRefraction);
    }

    [Fact]
    public void Migrate_Version4_AddsPerformanceDefaults()
    {
        const string rawJson = """
            {
              "SettingsVersion": 4
            }
            """;

        var (settings, migrated) = SettingsMigrator.Migrate(rawJson);

        Assert.True(migrated);
        Assert.Equal(SettingsMigrator.CurrentVersion, settings.SettingsVersion);
        Assert.True(settings.EnableBlurEffects);
        Assert.Equal(120, settings.AnimationFps);
    }

    [Fact]
    public void Migrate_Version10_CapsLegacyAnimationFpsAt120()
    {
        const string rawJson = """
            {
              "SettingsVersion": 10,
              "AnimationFps": 144
            }
            """;

        var (settings, migrated) = SettingsMigrator.Migrate(rawJson);

        Assert.True(migrated);
        Assert.Equal(SettingsMigrator.CurrentVersion, settings.SettingsVersion);
        Assert.Equal(120, settings.AnimationFps);
    }
}
