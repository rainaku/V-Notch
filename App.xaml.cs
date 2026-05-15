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

        // Load language preference early
        var earlySettings = new SettingsService();
        var loadedSettings = earlySettings.Load();
        Loc.SetLanguage(loadedSettings.Language);

        // ─── Global Error Handlers ───
        DispatcherUnhandledException += (s, args) =>
        {
            RuntimeLog.Error("UNHANDLED-UI", args.Exception);
            // Don't show MessageBox for animation/rendering errors — they're recoverable
            if (IsRecoverableException(args.Exception))
            {
                args.Handled = true;
                return;
            }
            MessageBox.Show($"An unexpected error occurred: {args.Exception.Message}",
                "V-Notch Error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // Catch exceptions from background threads (Task.Run, async void, etc.)
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                RuntimeLog.Error("UNHANDLED-BG", ex, "Background thread crash");
            }
        };

        // Catch unobserved Task exceptions (forgotten awaits)
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            RuntimeLog.Error("UNOBSERVED-TASK", args.Exception?.InnerException ?? args.Exception!,
                "Unobserved task exception");
            args.SetObserved(); // Prevent process termination
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
        services.AddSingleton<BluetoothMonitorService>();
        services.AddSingleton<IDispatcherService>(sp =>
            new DispatcherService(Current.Dispatcher));
        services.AddSingleton<IUpdateService, UpdateService>();

        
        services.AddSingleton<BatteryModule>();
        services.AddSingleton<CalendarModule>();
        services.AddSingleton<BluetoothModule>();
        services.AddSingleton<IModuleLifecycleManager>(sp =>
        {
            var host = new ModuleLifecycleManager();
            host.Register(sp.GetRequiredService<BatteryModule>());
            host.Register(sp.GetRequiredService<CalendarModule>());
            host.Register(sp.GetRequiredService<BluetoothModule>());
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
    private static bool IsRecoverableException(Exception ex)
    {
        // Animation/rendering errors — WPF can recover
        if (ex is InvalidOperationException && ex.Message.Contains("animation"))
            return true;

        // Media session errors — non-fatal
        if (ex.GetType().FullName?.Contains("Windows.Media") == true)
            return true;

        // COM errors from media/camera — non-fatal
        if (ex is System.Runtime.InteropServices.COMException)
            return true;

        return false;
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
