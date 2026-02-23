using System.Windows;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Services;

namespace VNotch;

public partial class App : Application
{
    private static Mutex? _mutex;
    private const string MutexName = "VNotch_SingleInstance_Mutex";

    /// <summary>
    /// The DI service provider. Available application-wide.
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, MutexName, out bool createdNew);

        if (!createdNew)
        {
            MessageBox.Show("V-Notch đang chạy!", "V-Notch",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (s, args) =>
        {
            MessageBox.Show($"Lỗi: {args.Exception.Message}", "V-Notch Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // Configure DI container
        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        // Create and show MainWindow via DI
        var mainWindow = Services.GetRequiredService<MainWindow>();
        mainWindow.Show();

        base.OnStartup(e);
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // Services — Singleton (shared state across app lifetime)
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IMediaDetectionService, MediaDetectionService>();
        services.AddSingleton<IVolumeService, VolumeService>();
        services.AddSingleton<IBatteryService, BatteryServiceImpl>();
        services.AddSingleton<IDispatcherService>(sp =>
            new DispatcherService(Current.Dispatcher));

        // Views
        services.AddSingleton<MainWindow>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Dispose DI container (disposes all IDisposable singletons)
        if (Services is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}