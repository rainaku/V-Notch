using System.Windows;
using System.Threading;
using CefSharp;
using CefSharp.Wpf;
using System.IO;

namespace VNotch;

public partial class App : Application
{
    private static Mutex? _mutex;
    private const string MutexName = "VNotch_SingleInstance_Mutex";

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single instance check
        _mutex = new Mutex(true, MutexName, out bool createdNew);

        if (!createdNew)
        {
            MessageBox.Show("V-Notch đang chạy!", "V-Notch",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Initialize CefSharp
        InitializeCefSharp();

        // Global exception handling
        DispatcherUnhandledException += (s, args) =>
        {
            MessageBox.Show($"Lỗi: {args.Exception.Message}", "V-Notch Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        base.OnStartup(e);
    }

    private void InitializeCefSharp()
    {
        var settings = new CefSettings();

        // Cấu hình tối ưu cho app background/overlay
        settings.LogSeverity = LogSeverity.Disable;
        settings.WindowlessRenderingEnabled = true;

        // Cho phép YouTube IFrame API và autoplay
        settings.CefCommandLineArgs["autoplay-policy"] = "no-user-gesture-required";
        settings.CefCommandLineArgs["disable-gpu"] = "1";
        settings.CefCommandLineArgs["disable-gpu-compositing"] = "1";

        // Cache directory để lưu session/cookies nếu cần
        string cachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VNotch", "CefCache");
        if (!Directory.Exists(cachePath)) Directory.CreateDirectory(cachePath);
        settings.CachePath = cachePath;

        try
        {
            Cef.Initialize(settings, performDependencyCheck: true, browserProcessHandler: null);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"CefSharp Init Error: {ex.Message}", "V-Notch Error");
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (Cef.IsInitialized == true)
        {
            Cef.Shutdown();
        }

        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
