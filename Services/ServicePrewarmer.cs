using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Modules;

namespace VNotch.Services;

internal static class ServicePrewarmer
{
    public static void Prewarm(IServiceProvider provider)
    {
        if (provider == null) throw new ArgumentNullException(nameof(provider));

        try
        {
            // ── Phase 1: synchronous resolution ────────────────────────────── Touching every singleton makes the container assert wiring is correct before any window is shown
            ResolveAll(provider);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PREWARM", ex, "Service resolution failed");
        }

        // ── Phase 2: background warmups ────────────────────────────────────── Anything that performs I/O, COM activation, or device enumeration runs off the UI thread
        Task.Run(() => RunBackgroundWarmups(provider));
    }

    private static void ResolveAll(IServiceProvider provider)
    {
        // Core media/system services
        SafeResolve<ISettingsService>(provider);
        SafeResolve<IDispatcherService>(provider);
        SafeResolve<IMediaMetadataLookupService>(provider);
        SafeResolve<IMediaArtworkService>(provider);
        SafeResolve<IColorExtractionService>(provider);
        SafeResolve<IWindowTitleScanner>(provider);
        SafeResolve<IMediaDetectionService>(provider);
        SafeResolve<IVolumeService>(provider);
        SafeResolve<IBatteryService>(provider);
        SafeResolve<IUpdateService>(provider);
        SafeResolve<IWeatherService>(provider);

        // Long-lived watchers (own internal timers / device watchers)
        SafeResolve<BluetoothMonitorService>(provider);
        SafeResolve<PrivacyIndicatorService>(provider);

        // Modules & lifecycle host
        SafeResolve<BatteryModule>(provider);
        SafeResolve<CalendarModule>(provider);
        SafeResolve<BluetoothModule>(provider);
        SafeResolve<PrivacyIndicatorModule>(provider);
        SafeResolve<WeatherModule>(provider);
        SafeResolve<IModuleLifecycleManager>(provider);
    }

    private static void RunBackgroundWarmups(IServiceProvider provider)
    {
        // Settings: load once so first read is in-memory.
        try
        {
            var settings = provider.GetService<ISettingsService>()?.Load();
            if (settings != null)
            {
                RuntimeLog.Log("PREWARM", $"settings loaded (lang={settings.Language})");

                // Configure smart-crop now if enabled — model probing and any ONNX initialisation happens off the UI thread.
                if (settings.EnableSmartCrop)
                {
                    var artwork = provider.GetService<IMediaArtworkService>();
                    artwork?.ConfigureSmartCrop(true);
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PREWARM", ex, "Settings warmup failed");
        }

        // Battery: trigger a single read so the system call is JIT-compiled and the very first BatteryModule tick has fresh state.
        try
        {
            var battery = provider.GetService<IBatteryService>();
            _ = battery?.GetBatteryInfo();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PREWARM", ex, "Battery warmup failed");
        }

        // Volume: COM endpoint already initialised in ctor, but reading the current level forces the IAudioEndpointVolume QI to complete
        try
        {
            var volume = provider.GetService<IVolumeService>();
            if (volume != null && volume.IsAvailable)
            {
                _ = volume.GetVolume();
                _ = volume.GetMute();
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PREWARM", ex, "Volume warmup failed");
        }

        // WindowTitleScanner: prime UI Automation and the per-window cache
        try
        {
            var scanner = provider.GetService<IWindowTitleScanner>();
            _ = scanner?.GetAllWindowTitles(isThrottled: true);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PREWARM", ex, "WindowTitleScanner warmup failed");
        }

        // Bluetooth watcher: needs to enumerate connected devices so the initial `EnumerationCompleted` flag is set before any user-driven change can fire a spurious "device connected" toast
        try
        {
            provider.GetService<BluetoothMonitorService>()?.Start();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PREWARM", ex, "Bluetooth watcher warmup failed");
        }

        // Privacy indicator: starts its own timer; cheap to spin up.
        try
        {
            provider.GetService<PrivacyIndicatorService>()?.Start();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PREWARM", ex, "Privacy indicator warmup failed");
        }

        RuntimeLog.Log("PREWARM", "background warmup complete");
    }

    private static T? SafeResolve<T>(IServiceProvider provider) where T : class
    {
        try
        {
            return provider.GetService<T>();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PREWARM", ex, $"Resolve<{typeof(T).Name}> failed");
            return null;
        }
    }
}
