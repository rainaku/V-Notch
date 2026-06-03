using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using NAudio.CoreAudioApi;

namespace VNotch.Services;

public sealed class PrivacyIndicatorService : IDisposable
{
    private const string ConsentRoot =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    // Fallback poll cadence — only used if the registry change-notification watch
    // can't be established (older/locked-down systems). Normal operation is event-driven.
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);

    // Coalesce a burst of registry writes (an app opening a device writes several
    // values) into a single scan.
    private const int DebounceMs = 200;

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _fallbackTimer;
    private bool _disposed;
    private bool _started;

    // Registry-watch state (event-driven path).
    private Thread? _watchThread;
    private RegistryKey? _consentKey;
    private AutoResetEvent? _changeEvent;
    private ManualResetEvent? _stopEvent;

    public event EventHandler<PrivacyIndicatorState>? StateChanged;

    public PrivacyIndicatorState CurrentState { get; private set; } = PrivacyIndicatorState.Empty;

    public PrivacyIndicatorService(TimeSpan? pollInterval = null)
    {
        // Captured so the watch thread can marshal Poll() back to the thread that
        // owns StateChanged consumers (the UI thread, as with the old timer).
        _dispatcher = Dispatcher.CurrentDispatcher;
        _fallbackTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = pollInterval ?? DefaultPollInterval
        };
        _fallbackTimer.Tick += (_, _) => Poll();
    }

    public void Start()
    {
        if (_disposed || _started) return;
        _started = true;

        // Initial sample so consumers don't have to wait for the first change.
        Poll();

        // Prefer event-driven registry notifications; only poll if that fails to arm.
        if (!TryStartRegistryWatch())
        {
            _fallbackTimer.Start();
        }
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _fallbackTimer.Stop();

        _stopEvent?.Set();
        _watchThread?.Join(TimeSpan.FromSeconds(1));
        CleanupWatch();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    // ─── Registry change-notification watch ───

    private bool TryStartRegistryWatch()
    {
        try
        {
            _consentKey = Registry.CurrentUser.OpenSubKey(ConsentRoot, writable: false);
            if (_consentKey == null) return false;

            _changeEvent = new AutoResetEvent(false);
            _stopEvent = new ManualResetEvent(false);

            _watchThread = new Thread(WatchLoop)
            {
                IsBackground = true,
                Name = "PrivacyConsentStoreWatch"
            };
            _watchThread.Start();
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PRIVACY", ex, "Registry watch setup failed; falling back to polling");
            CleanupWatch();
            return false;
        }
    }

    private void WatchLoop()
    {
        var handles = new WaitHandle[] { _stopEvent!, _changeEvent! };

        if (!Arm()) { FallBackToPolling(); return; }

        while (true)
        {
            int idx = WaitHandle.WaitAny(handles);
            if (idx == 0) return; // stop signaled

            // A change fired. Coalesce the rest of the burst (or exit early on stop).
            // Scanning happens AFTER the debounce, so writes during the window are
            // captured by the scan itself.
            if (_stopEvent!.WaitOne(DebounceMs)) return;

            // Re-arm BEFORE scanning: a change during the scan then re-signals us,
            // closing the gap between consuming a notification and re-registering
            // (the watch is one-shot). Without this a fast off-toggle could be missed.
            if (!Arm()) { FallBackToPolling(); return; }

            _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Poll));
        }
    }

    private bool Arm()
    {
        int rc = RegNotifyChangeKeyValue(
            _consentKey!.Handle,
            bWatchSubtree: true,
            RegNotifyFilter.Name | RegNotifyFilter.LastSet,
            _changeEvent!.SafeWaitHandle,
            fAsynchronous: true);

        if (rc != 0)
        {
            RuntimeLog.Error("PRIVACY", new System.ComponentModel.Win32Exception(rc),
                "RegNotifyChangeKeyValue failed");
            return false;
        }
        return true;
    }

    private void FallBackToPolling()
        => _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(StartFallbackPolling));

    private void StartFallbackPolling()
    {
        if (_disposed || !_started) return;
        if (!_fallbackTimer.IsEnabled) _fallbackTimer.Start();
    }

    private void CleanupWatch()
    {
        try { _consentKey?.Dispose(); } catch { /* ignore */ }
        _consentKey = null;
        try { _changeEvent?.Dispose(); } catch { /* ignore */ }
        _changeEvent = null;
        try { _stopEvent?.Dispose(); } catch { /* ignore */ }
        _stopEvent = null;
        _watchThread = null;
    }

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern int RegNotifyChangeKeyValue(
        SafeRegistryHandle hKey,
        bool bWatchSubtree,
        RegNotifyFilter dwNotifyFilter,
        SafeWaitHandle hEvent,
        bool fAsynchronous);

    [Flags]
    private enum RegNotifyFilter : uint
    {
        Name = 0x1,       // REG_NOTIFY_CHANGE_NAME — subkeys added/removed (apps)
        Attributes = 0x2,
        LastSet = 0x4,    // REG_NOTIFY_CHANGE_LAST_SET — values changed (LastUsedTimeStop)
        Security = 0x8,
    }

    private void Poll()
    {
        try
        {
            var mic = ScanCapability("microphone");
            var cam = ScanCapability("webcam");
            var screenRec = DetectScreenRecording();

            bool micActuallyActive = mic.Count > 0 && IsMicrophoneActuallyCapturing();

            var next = new PrivacyIndicatorState(
                MicrophoneInUse: micActuallyActive,
                CameraInUse: cam.Count > 0,
                ScreenRecordingActive: screenRec,
                MicrophoneConsumers: micActuallyActive ? mic : Array.Empty<string>(),
                CameraConsumers: cam);

            if (!next.Equals(CurrentState))
            {
                CurrentState = next;
                StateChanged?.Invoke(this, next);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PRIVACY", ex, "PrivacyIndicatorService poll failed");
        }
    }

    private static bool IsMicrophoneActuallyCapturing()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();

            MMDevice? captureDevice = null;
            try
            {
                captureDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // No capture device available
                return false;
            }

            if (captureDevice == null) return false;

            using (captureDevice)
            {
                // Check if the device state is active
                if (captureDevice.State != DeviceState.Active) return false;

                // Check audio sessions on the capture device — if any session has audio flowing, mic is truly in use
                var sessionManager = captureDevice.AudioSessionManager;
                var sessions = sessionManager?.Sessions;
                if (sessions == null) return false;

                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    if (session == null) continue;

                    try
                    {
                        if (session.AudioMeterInformation.MasterPeakValue > 0.0001f)
                        {
                            return true;
                        }
                    }
                    catch { /* session may have been released */ }
                }

                return false;
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PRIVACY", ex, "IsMicrophoneActuallyCapturing check failed");
            // Fall back to registry-only behavior on error
            return true;
        }
    }

    private static IReadOnlyList<string> ScanCapability(string capability)
    {
        var consumers = new List<string>();
        try
        {
            using var capRoot = Registry.CurrentUser.OpenSubKey(
                $"{ConsentRoot}\\{capability}", writable: false);
            if (capRoot == null) return Array.Empty<string>();

            foreach (var subKeyName in capRoot.GetSubKeyNames())
            {
                using var subKey = capRoot.OpenSubKey(subKeyName, writable: false);
                if (subKey == null) continue;

                // Packaged apps store LastUsedTimeStop on the immediate subkey.
                if (TryDetectInUse(subKey, out _))
                {
                    consumers.Add(NormalizeAppName(subKeyName));
                    continue;
                }

                // Desktop apps live under a "NonPackaged" branch keyed by exe path.
                if (string.Equals(subKeyName, "NonPackaged", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var npName in subKey.GetSubKeyNames())
                    {
                        using var npKey = subKey.OpenSubKey(npName, writable: false);
                        if (npKey == null) continue;
                        if (TryDetectInUse(npKey, out _))
                        {
                            consumers.Add(NormalizeAppName(npName));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PRIVACY", ex, $"Scan {capability} failed");
        }

        return consumers.Count == 0 ? Array.Empty<string>() : consumers.Distinct().ToList();
    }

    private static bool TryDetectInUse(RegistryKey key, out long lastStart)
    {
        lastStart = 0;
        var startObj = key.GetValue("LastUsedTimeStart");
        var stopObj = key.GetValue("LastUsedTimeStop");
        if (stopObj is long stop)
        {
            if (startObj is long start) lastStart = start;
            // 0 means "still in use".
            return stop == 0L;
        }
        return false;
    }

    private static bool DetectScreenRecording()
    {
        // Only use ConsentStore registry — this is 100% accurate
        if (ScanCapability("graphicsCaptureProgrammatic").Count > 0)
            return true;
        if (ScanCapability("graphicsCaptureWithoutBorder").Count > 0)
            return true;

        return false;
    }

    private static string NormalizeAppName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;

        // Desktop apps are encoded as "C:#Program Files#App#app.exe" — split and take the exe.
        if (raw.Contains('#'))
        {
            var parts = raw.Split('#');
            var last = parts[^1];
            if (!string.IsNullOrWhiteSpace(last)) return last;
        }

        // Packaged app family name like "Microsoft.YourPhone_8wekyb3d8bbwe" — keep just the readable head.
        var underscore = raw.IndexOf('_');
        if (underscore > 0)
        {
            return raw[..underscore];
        }

        return raw;
    }
}

public sealed record PrivacyIndicatorState(
    bool MicrophoneInUse,
    bool CameraInUse,
    bool ScreenRecordingActive,
    IReadOnlyList<string> MicrophoneConsumers,
    IReadOnlyList<string> CameraConsumers)
{
    public static readonly PrivacyIndicatorState Empty = new(
        false, false, false, Array.Empty<string>(), Array.Empty<string>());

    public bool AnyInUse => MicrophoneInUse || CameraInUse || ScreenRecordingActive;

    public bool Equals(PrivacyIndicatorState? other)
    {
        if (other is null) return false;
        if (MicrophoneInUse != other.MicrophoneInUse) return false;
        if (CameraInUse != other.CameraInUse) return false;
        if (ScreenRecordingActive != other.ScreenRecordingActive) return false;
        return SequenceEquals(MicrophoneConsumers, other.MicrophoneConsumers)
            && SequenceEquals(CameraConsumers, other.CameraConsumers);
    }

    public override int GetHashCode()
    {
        var hash = HashCode.Combine(MicrophoneInUse, CameraInUse, ScreenRecordingActive);
        foreach (var s in MicrophoneConsumers) hash = HashCode.Combine(hash, s);
        foreach (var s in CameraConsumers) hash = HashCode.Combine(hash, s);
        return hash;
    }

    private static bool SequenceEquals(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
}
