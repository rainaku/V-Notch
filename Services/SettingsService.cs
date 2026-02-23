using System.IO;
using System.Text.Json;
using VNotch.Models;

namespace VNotch.Services;

public class SettingsService : ISettingsService
{
    private readonly string _settingsPath;

    public SettingsService()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appFolder = Path.Combine(appDataPath, "V-Notch");

        if (!Directory.Exists(appFolder))
        {
            Directory.CreateDirectory(appFolder);
        }

        _settingsPath = Path.Combine(appFolder, "settings.json");
    }

    public NotchSettings Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                return JsonSerializer.Deserialize<NotchSettings>(json) ?? new NotchSettings();
            }
        }
        catch
        {

        }

        return new NotchSettings();
    }

    public void Save(NotchSettings settings)
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(settings, options);
            File.WriteAllText(_settingsPath, json);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Không thể lưu cài đặt: {ex.Message}", "Lỗi",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }
}