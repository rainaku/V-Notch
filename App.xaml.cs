using System.Windows;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Services;
using VNotch.Modules;
using System.Linq;

namespace VNotch;

public partial class App : Application
{
    private static Mutex? _mutex;
    private const string MutexName = "VNotch_SingleInstance_Mutex";

    
    
    
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        var setupSource = TryGetArgumentValue(e.Args, "--setup-source");
        var launchSetup = e.Args.Contains("--setup") || !string.IsNullOrWhiteSpace(setupSource);
        if (launchSetup)
        {
            var setupWindow = new SetupWindow(setupSource);
            setupWindow.ShowDialog();
            Shutdown(setupWindow.ResultExitCode);
            return;
        }

        if (e.Args.Contains("--uninstall"))
        {
            SetupOperations.RunUninstallFlow();
            return;
        }
        
        _mutex = new Mutex(true, MutexName, out bool createdNew);

        if (!createdNew)
        {
            MessageBox.Show("V-Notch is already running!", "V-Notch",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        RuntimeLog.InitializeNewSession("vnotch-debug.log");
        RuntimeLog.Log("SYSTEM", $"Application startup. Log file: {RuntimeLog.LogPath}");

        DispatcherUnhandledException += (s, args) =>
        {
            RuntimeLog.Log("UNHANDLED", args.Exception.ToString());
            MessageBox.Show($"Error: {args.Exception.Message}", "V-Notch Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        
        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        
        var mainWindow = Services.GetRequiredService<MainWindow>();
        mainWindow.Show();

        base.OnStartup(e);
    }

    private void ConfigureServices(IServiceCollection services)
    {
        
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IMediaMetadataLookupService, MediaMetadataLookupService>();
        services.AddSingleton<IMediaArtworkService, MediaArtworkService>();
        services.AddSingleton<IColorExtractionService, ColorExtractionService>();
        services.AddSingleton<IWindowTitleScanner, WindowTitleScanner>();
        services.AddSingleton<IMediaDetectionService, MediaDetectionService>();
        services.AddSingleton<IVolumeService, VolumeService>();
        services.AddSingleton<IBatteryService, BatteryServiceImpl>();
        services.AddSingleton<IDispatcherService>(sp =>
            new DispatcherService(Current.Dispatcher));
        services.AddSingleton<IUpdateService, UpdateService>();

        
        services.AddSingleton<BatteryModule>();
        services.AddSingleton<CalendarModule>();
        services.AddSingleton<IModuleLifecycleManager>(sp =>
        {
            var host = new ModuleLifecycleManager();
            host.Register(sp.GetRequiredService<BatteryModule>());
            host.Register(sp.GetRequiredService<CalendarModule>());
            return host;
        });

        
        services.AddSingleton<MainWindow>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        RuntimeLog.Log("SYSTEM", "Application exit");

        
        if (Services is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private static string? TryGetArgumentValue(string[] args, string argumentName)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            if (string.Equals(argument, argumentName, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    return args[i + 1];
                }

                return null;
            }

            var prefix = argumentName + "=";
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return argument[prefix.Length..];
            }
        }

        return null;
    }
}
