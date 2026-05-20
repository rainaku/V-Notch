using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Modules;

namespace VNotch.Services;

/// <summary>
/// Eagerly resolves and warms up every long-lived service the moment the DI
/// container is built. Doing so:
///
///  • surfaces constructor failures during startup instead of the first time a
///    feature is used (volume slider, battery icon, bluetooth toast, …);
///  • pays the JIT / WinRT activation / COM cold-start cost up-front so the
///    first user interaction is snappy;
///  • forces background watchers (Bluetooth, Privacy indicator) to enumerate
///    their initial state so events fire correctly on first use;
///  • primes UI Automation and HttpClient on the YouTube/SoundCloud lookup
///    paths so the first browser-tab scan after the notch is opened doesn't
///    have to bring the whole stack on-line.
///
/// All work that touches WinRT, COM, or UI Automation is dispatched off the
/// UI thread to keep the splash-to-window time low.
/// </summary>
internal static class ServicePrewarmer
{
    public static void Prewarm(IServiceProvider provider)
    {
        if (provider == null) throw new ArgumentNullException(nameof(provider));

        try
        {
            // ── Phase 1: synchronous resolution ──────────────────────────────
            // Touching every singleton makes the container assert wiring is
            // correct before any window is shown. Failures here are logged and
            // swallowed; the app keeps booting with degraded features rather
            // than crashing on startup.
            ResolveAll(provider);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PREWARM", ex, "Service resolution failed");
        }

        // ── Phase 2: background warmups ──────────────────────────────────────
        // Anything that performs I/O, COM activation, or device enumeration
        // runs off the UI thread. We don't await — the app is free to render
        // its first frame while these run in parallel.
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

        // Long-lived watchers (own internal timers / device watchers)
        SafeResolve<BluetoothMonitorService>(provider);
        SafeResolve<PrivacyIndicatorService>(provider);

        // Modules & lifecycle host. Resolving the host eagerly registers all
        // modules with it — important for `IModuleLifecycleManager.StartAll`
        // later because the factory only runs once.
        SafeResolve<BatteryModule>(provider);
        SafeResolve<CalendarModule>(provider);
        SafeResolve<BluetoothModule>(provider);
        SafeResolve<PrivacyIndicatorModule>(provider);
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

                // Configure smart-crop now if enabled — model probing and any
                // ONNX initialisation happens off the UI thread.
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

        // Battery: trigger a single read so the system call is JIT-compiled
        // and the very first BatteryModule tick has fresh state.
        try
        {
            var battery = provider.GetService<IBatteryService>();
            _ = battery?.GetBatteryInfo();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PREWARM", ex, "Battery warmup failed");
        }

        // Volume: COM endpoint already initialised in ctor, but reading the
        // current level forces the IAudioEndpointVolume QI to complete.
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

        // WindowTitleScanner: prime UI Automation and the per-window cache.
        // The first call also loads UIAutomationClient.dll which is otherwise
        // pulled in lazily on the first browser scan, adding ~200-400ms.
        try
        {
            var scanner = provider.GetService<IWindowTitleScanner>();
            _ = scanner?.GetAllWindowTitles(isThrottled: true);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PREWARM", ex, "WindowTitleScanner warmup failed");
        }

        // Bluetooth watcher: needs to enumerate connected devices so the
        // initial `EnumerationCompleted` flag is set before any user-driven
        // change can fire a spurious "device connected" toast.
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
