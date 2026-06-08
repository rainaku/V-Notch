using System;
using System.IO;
using System.Text.Json;
using VNotch.Models;

namespace VNotch.Services;

public class SettingsService : ISettingsService
{
    private readonly string _settingsPath;
    private readonly string _appFolder;

    public SettingsService()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _appFolder = Path.Combine(appDataPath, "V-Notch");

        if (!Directory.Exists(_appFolder))
        {
            Directory.CreateDirectory(_appFolder);
        }

        _settingsPath = Path.Combine(_appFolder, "settings.json");
    }

    public NotchSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            
            var fresh = new NotchSettings { SettingsVersion = SettingsMigrator.CurrentVersion };
            Save(fresh);
            return fresh;
        }

        string raw;
        try
        {
            raw = File.ReadAllText(_settingsPath);
        }
        catch (Exception ex)
        {
            
            RuntimeLog.Log("SETTINGS-LOAD", $"Failed to read {_settingsPath}: {ex}");
            return new NotchSettings { SettingsVersion = SettingsMigrator.CurrentVersion };
        }

        try
        {
            var (settings, migrated) = SettingsMigrator.Migrate(raw);
            bool normalized = NormalizeSettings(settings);

            if (migrated || normalized)
            {
                RuntimeLog.Log(
                    "SETTINGS-LOAD",
                    $"Migrated/normalized settings to version {SettingsMigrator.CurrentVersion}");
                Save(settings);
            }

            return settings;
        }
        catch (JsonException ex)
        {
            var backupPath = QuarantineCorruptFile(raw, ex);
            RuntimeLog.Log(
                "SETTINGS-LOAD",
                $"Corrupt settings file detected. Backed up to '{backupPath}'. Falling back to defaults. Error: {ex.Message}");

            var defaults = new NotchSettings { SettingsVersion = SettingsMigrator.CurrentVersion };
            Save(defaults);
            return defaults;
        }
        catch (Exception ex)
        {
            
            RuntimeLog.Log("SETTINGS-LOAD", $"Unexpected error while loading settings: {ex}");
            return new NotchSettings { SettingsVersion = SettingsMigrator.CurrentVersion };
        }
    }

    public void Save(NotchSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));

        try
        {
            settings.SettingsVersion = SettingsMigrator.CurrentVersion;

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(settings, options);

            var tempPath = _settingsPath + ".tmp";
            File.WriteAllText(tempPath, json);

            if (File.Exists(_settingsPath))
            {
                File.Replace(tempPath, _settingsPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, _settingsPath);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("SETTINGS-SAVE", ex.ToString());
            System.Windows.MessageBox.Show($"Unable to save settings: {ex.Message}", "Error",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }


    private static bool NormalizeSettings(NotchSettings settings)
    {
        bool changed = false;

        if (settings.DynamicIslandWidth < 100)
        {
            settings.DynamicIslandWidth = (int)Math.Round(settings.Width * 1.12 / 10.0) * 10;
            changed = true;
        }

        if (settings.DynamicIslandHeight < 24)
        {
            settings.DynamicIslandHeight = 40;
            changed = true;
        }

        return changed;
    }
    private string QuarantineCorruptFile(string rawContents, Exception reason)
    {
        try
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var backupPath = Path.Combine(_appFolder, $"settings.corrupt-{timestamp}.json");
            File.WriteAllText(backupPath, rawContents);
            return backupPath;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log(
                "SETTINGS-LOAD",
                $"Failed to write corrupt-settings backup (original error: {reason.Message}): {ex}");
            return "<backup-failed>";
        }
    }
}
