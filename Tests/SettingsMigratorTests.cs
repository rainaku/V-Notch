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

        Assert.Equal(260, settings.DynamicIslandWidth);
    }

    [Fact]
    public void Migrate_Version5_AddsDynamicIslandHeightFromExistingHeight()
    {
        const string rawJson = """
            {
              "SettingsVersion": 5,
              "Height": 40
            }
            """;

        var (settings, migrated) = SettingsMigrator.Migrate(rawJson);

        Assert.True(migrated);
        Assert.Equal(SettingsMigrator.CurrentVersion, settings.SettingsVersion);
        Assert.Equal(45, settings.DynamicIslandHeight);
    }

    [Fact]
    public void Migrate_PreservesExistingDynamicIslandHeight()
    {
        const string rawJson = """
            {
              "SettingsVersion": 5,
              "Height": 40,
              "DynamicIslandHeight": 52
            }
            """;

        var (settings, _) = SettingsMigrator.Migrate(rawJson);

        Assert.Equal(52, settings.DynamicIslandHeight);
    }

    [Fact]
    public void NewDefaults_UseSliderAlignedDynamicIslandHeight()
    {
        var settings = new NotchSettings();

        Assert.Equal(36, settings.DynamicIslandHeight);
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

