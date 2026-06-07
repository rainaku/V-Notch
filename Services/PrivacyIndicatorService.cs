using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace VNotch.Services;

public sealed class PrivacyIndicatorService : IDisposable
{
    private const string ConsentRoot =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    private static readonly TimeSpan ActivePollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(5);

    private readonly DispatcherTimer _timer;
    private readonly TimeSpan _activeInterval;
    private bool _disposed;
    private bool _started;

    public event EventHandler<PrivacyIndicatorState>? StateChanged;

    public PrivacyIndicatorState CurrentState { get; private set; } = PrivacyIndicatorState.Empty;

    public PrivacyIndicatorService(TimeSpan? pollInterval = null)
    {
        _activeInterval = pollInterval ?? ActivePollInterval;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            // Start responsive; AdaptInterval() backs off once we confirm nothing is in use.
            Interval = _activeInterval
        };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start()
    {
        if (_disposed || _started) return;
        _started = true;

        // Initial sample so consumers don't have to wait for the first tick.
        Poll();
        _timer.Start();
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _timer.Stop();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
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
        finally
        {
            AdaptInterval();
        }
    }

    // Poll frequently while a device is in use (responsive consumer updates), but back off
    // when idle to cut background registry/audio scans.
    private void AdaptInterval()
    {
        if (!_started) return;
        var desired = CurrentState.AnyInUse ? _activeInterval : IdlePollInterval;
        if (_timer.Interval != desired)
            _timer.Interval = desired;
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
