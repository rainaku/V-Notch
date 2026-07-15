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
        Assert.Equal(144, settings.AnimationFps);
    }
}
