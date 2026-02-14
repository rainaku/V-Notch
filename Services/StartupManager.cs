using System.IO;
using Microsoft.Win32;

namespace VNotch.Services;

public class StartupManager
{
    private const string RegistryKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "V-Notch";

    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, false);
            return key?.GetValue(AppName) != null;
        }
        catch
        {
            return false;
        }
    }

    public static void SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
            if (key == null) return;

            if (enable)
            {
                var exePath = Environment.ProcessPath ??
                    Path.Combine(AppContext.BaseDirectory, "V-Notch.exe");
                key.SetValue(AppName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Không thể thay đổi cài đặt khởi động: {ex.Message}",
                "Lỗi", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }
}