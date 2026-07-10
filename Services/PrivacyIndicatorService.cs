using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;

namespace VNotch.Services;

public sealed class PrivacyIndicatorService : IDisposable
{
    private const string ConsentRoot =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    private static readonly TimeSpan ActivePollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinimumScreenRecordingDuration = TimeSpan.FromSeconds(2);

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInsufficientBuffer = 122;

    private static readonly string[] IgnoredMicrophoneProcessSuffixes =
    {
        "service", "services", "svc", "daemon"
    };

    private static readonly HashSet<string> IgnoredMicrophoneProcessNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "audiodg", "svchost", "system", "registry"
        };

    private static readonly Lazy<IReadOnlySet<string>> ServiceExecutablePaths =
        new(LoadServiceExecutablePaths);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern int GetPackageFamilyName(IntPtr process, ref uint packageFamilyNameLength, IntPtr packageFamilyName);

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
            Interval = _activeInterval
        };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start()
    {
        if (_disposed || _started) return;
        _started = true;

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
            DateTime utcNow = DateTime.UtcNow;
            var micUsage = ScanCapability("microphone");
            var camUsage = ScanCapability("webcam");
            var programmaticCapture = ScanCapability("graphicsCaptureProgrammatic");
            var borderlessCapture = ScanCapability("graphicsCaptureWithoutBorder");

            var running = new ConsumerProcessProbe();
            var mic = GetRelevantConsumers(
                micUsage,
                running,
                usage => !IsIgnoredMicrophoneConsumer(usage.RawName));
            var cam = GetRelevantConsumers(camUsage, running);
            bool screenRec = DetectScreenRecording(
                programmaticCapture.Concat(borderlessCapture), running, utcNow);

            var next = new PrivacyIndicatorState(
                MicrophoneInUse: mic.Count > 0,
                CameraInUse: cam.Count > 0,
                ScreenRecordingActive: screenRec,
                MicrophoneConsumers: mic,
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

    private void AdaptInterval()
    {
        if (!_started) return;
        var desired = CurrentState.AnyInUse ? _activeInterval : IdlePollInterval;
        if (_timer.Interval != desired)
            _timer.Interval = desired;
    }

    private static IReadOnlyList<string> GetRelevantConsumers(
        IEnumerable<CapabilityUsage> usages,
        ConsumerProcessProbe running,
        Func<CapabilityUsage, bool>? additionalRule = null)
    {
        return usages
            .Where(usage => running.IsRunning(usage.RawName))
            .Where(usage => additionalRule == null || additionalRule(usage))
            .Select(usage => usage.DisplayName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<CapabilityUsage> ScanCapability(string capability)
    {
        var consumers = new List<CapabilityUsage>();

        ScanCapabilityHive(Registry.CurrentUser, capability, consumers);
        ScanCapabilityHive(Registry.LocalMachine, capability, consumers);

        return consumers.Count == 0 ? Array.Empty<CapabilityUsage>() : consumers;
    }

    private static void ScanCapabilityHive(RegistryKey hive, string capability, List<CapabilityUsage> consumers)
    {
        try
        {
            using var capRoot = hive.OpenSubKey(
                $"{ConsentRoot}\\{capability}", writable: false);
            if (capRoot == null) return;

            foreach (var subKeyName in capRoot.GetSubKeyNames())
            {
                using var subKey = capRoot.OpenSubKey(subKeyName, writable: false);
                if (subKey == null) continue;

                if (TryDetectInUse(subKey, out long lastStart))
                {
                    consumers.Add(new CapabilityUsage(
                        subKeyName, NormalizeAppName(subKeyName), lastStart));
                    continue;
                }

                if (string.Equals(subKeyName, "NonPackaged", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var npName in subKey.GetSubKeyNames())
                    {
                        using var npKey = subKey.OpenSubKey(npName, writable: false);
                        if (npKey == null) continue;
                        if (TryDetectInUse(npKey, out lastStart))
                        {
                            consumers.Add(new CapabilityUsage(
                                npName, NormalizeAppName(npName), lastStart));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PRIVACY", ex, $"Scan {capability} ({hive.Name}) failed");
        }
    }

    private static bool TryDetectInUse(RegistryKey key, out long lastStart)
    {
        var startObj = key.GetValue("LastUsedTimeStart");
        var stopObj = key.GetValue("LastUsedTimeStop");
        lastStart = startObj is long start ? start : 0;
        return IsActiveUsage(lastStart, stopObj is long stop ? stop : null);
    }

    internal static bool IsActiveUsage(long? lastStart, long? lastStop) =>
        lastStart is > 0 && lastStop == 0;

    private static bool DetectScreenRecording(
        IEnumerable<CapabilityUsage> usages,
        ConsumerProcessProbe running,
        DateTime utcNow)
    {
        return usages.Any(usage =>
            running.IsRunning(usage.RawName) &&
            HasMinimumActiveDuration(usage.LastStartFileTime, utcNow, MinimumScreenRecordingDuration));
    }

    internal static bool HasMinimumActiveDuration(long startFileTime, DateTime utcNow, TimeSpan minimum)
    {
        if (startFileTime <= 0 || minimum < TimeSpan.Zero) return false;
        try
        {
            DateTime start = DateTime.FromFileTimeUtc(startFileTime);
            return start <= utcNow && utcNow - start >= minimum;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    internal static bool IsIgnoredMicrophoneConsumer(
        string rawConsumer,
        IReadOnlySet<string>? serviceExecutablePaths = null)
    {
        string? executablePath = TryDecodeDesktopConsumerPath(rawConsumer);
        string processName = executablePath == null
            ? NormalizeAppName(rawConsumer)
            : Path.GetFileNameWithoutExtension(executablePath);

        if (IgnoredMicrophoneProcessNames.Contains(processName)) return true;
        if (IgnoredMicrophoneProcessSuffixes.Any(suffix =>
            processName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (executablePath == null) return false;
        var servicePaths = serviceExecutablePaths ?? ServiceExecutablePaths.Value;
        return servicePaths.Contains(NormalizeExecutablePath(executablePath));
    }

    internal static string? TryDecodeDesktopConsumerPath(string rawConsumer)
    {
        if (string.IsNullOrWhiteSpace(rawConsumer) || !rawConsumer.Contains('#')) return null;

        string decoded = rawConsumer.Replace('#', Path.DirectorySeparatorChar);
        try
        {
            return NormalizeExecutablePath(decoded);
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeExecutablePath(string path)
    {
        string expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (expanded.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            expanded = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                expanded[12..]);
        }
        return Path.GetFullPath(expanded).TrimEnd(Path.DirectorySeparatorChar);
    }

    private static IReadOnlySet<string> LoadServiceExecutablePaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services", writable: false);
            if (services == null) return paths;

            foreach (string name in services.GetSubKeyNames())
            {
                using var service = services.OpenSubKey(name, writable: false);
                if (service?.GetValue("ImagePath") is not string imagePath) continue;
                string? executable = ExtractExecutablePath(imagePath);
                if (executable != null) paths.Add(executable);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PRIVACY", ex, "Service executable scan failed");
        }
        return paths;
    }

    private static string? ExtractExecutablePath(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        string expanded = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        string candidate;

        if (expanded[0] == '"')
        {
            int endQuote = expanded.IndexOf('"', 1);
            if (endQuote <= 1) return null;
            candidate = expanded[1..endQuote];
        }
        else
        {
            int exeEnd = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeEnd < 0) return null;
            candidate = expanded[..(exeEnd + 4)];
        }

        try
        {
            return NormalizeExecutablePath(candidate);
        }
        catch
        {
            return null;
        }
    }

    private sealed class ConsumerProcessProbe
    {
        private readonly Dictionary<string, bool> _cache = new(StringComparer.OrdinalIgnoreCase);

        public bool IsRunning(string rawConsumer)
        {
            if (_cache.TryGetValue(rawConsumer, out bool running)) return running;
            running = TryDecodeDesktopConsumerPath(rawConsumer) is { } path
                ? IsDesktopExecutableRunning(path)
                : IsPackageFamilyRunning(rawConsumer);
            _cache[rawConsumer] = running;
            return running;
        }

        private static bool IsDesktopExecutableRunning(string executablePath)
        {
            string processName = Path.GetFileNameWithoutExtension(executablePath);
            if (string.IsNullOrWhiteSpace(processName)) return false;

            foreach (Process process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        string? runningPath = process.MainModule?.FileName;
                        if (runningPath != null && string.Equals(
                            NormalizeExecutablePath(runningPath), executablePath,
                            StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch
                    {
                        // Strict privacy evidence: an unverifiable process does not
                        // keep an indicator alive from a possibly stale registry key.
                    }
                }
            }
            return false;
        }

        private static bool IsPackageFamilyRunning(string packageFamily)
        {
            if (string.IsNullOrWhiteSpace(packageFamily)) return false;
            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    IntPtr handle = IntPtr.Zero;
                    try
                    {
                        handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)process.Id);
                        if (handle == IntPtr.Zero) continue;

                        uint chars = 0;
                        int result = GetPackageFamilyName(handle, ref chars, IntPtr.Zero);
                        if (result != ErrorInsufficientBuffer || chars == 0) continue;

                        IntPtr buffer = Marshal.AllocHGlobal(checked((int)chars * sizeof(char)));
                        try
                        {
                            result = GetPackageFamilyName(handle, ref chars, buffer);
                            if (result != 0) continue;
                            string? family = Marshal.PtrToStringUni(buffer);
                            if (string.Equals(family, packageFamily, StringComparison.OrdinalIgnoreCase))
                                return true;
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(buffer);
                        }
                    }
                    catch
                    {
                        // Process exited or cannot be queried.
                    }
                    finally
                    {
                        if (handle != IntPtr.Zero) CloseHandle(handle);
                    }
                }
            }
            return false;
        }
    }

    private static string NormalizeAppName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;

        if (raw.Contains('#'))
        {
            var parts = raw.Split('#');
            var last = parts[^1];
            if (!string.IsNullOrWhiteSpace(last)) return last;
        }

        var underscore = raw.IndexOf('_');
        if (underscore > 0)
        {
            return raw[..underscore];
        }

        return raw;
    }
}

internal readonly record struct CapabilityUsage(
    string RawName,
    string DisplayName,
    long LastStartFileTime);

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
