using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace VNotch.Services;

/// <summary>One application entry in the per-app volume mixer.</summary>
public sealed class AudioSessionInfo
{
    public uint ProcessId { get; init; }
    public string DisplayName { get; init; } = "";
    public float Volume { get; set; }          // 0..1
    public bool IsMuted { get; set; }
    public bool IsSystemSounds { get; init; }
    public ImageSource? Icon { get; init; }
}

/// <summary>A selectable audio output (render) endpoint.</summary>
public sealed class AudioDeviceInfo
{
    public string Id { get; init; } = "";
    public string FriendlyName { get; init; } = "";
    public bool IsDefault { get; set; }
}

public enum SpatialAudioMode
{
    Off,
    WindowsSonic,
    DolbyAtmos
}

/// <summary>
/// Provides the data + control surface for the Audio view: a per-application
/// volume mixer (via NAudio CoreAudio sessions), output-device enumeration and
/// switching (via the undocumented IPolicyConfig COM interface) and a best-effort
/// spatial-audio readout.
/// </summary>
public sealed class AudioMixerService : IDisposable
{
    // Fast-path cache for rapid volume drags: resolve a session once, reuse for a few seconds.
    private SimpleAudioVolume? _setCacheVolume;
    private uint _setCachePid = uint.MaxValue;
    private DateTime _setCacheAtUtc = DateTime.MinValue;
    private const double SetCacheLifetimeMs = 3000;

    /// <summary>
    /// Enumerates the active per-application audio sessions on the default render
    /// endpoint and returns lightweight, thread-safe snapshots (icons are frozen).
    /// Safe to call from a background thread — no COM objects are retained.
    /// </summary>
    public List<AudioSessionInfo> GetSessions() => GetSessions(includeIcons: true);

    /// <summary>
    /// Enumerates the active per-application audio sessions on the default render
    /// endpoint. When <paramref name="includeIcons"/> is false it skips the expensive
    /// icon + file-metadata extraction (uses the raw process name) so it can run
    /// quickly on the UI thread; pass true on a background thread for the rich version.
    /// Safe to call from any thread — no COM objects are retained.
    /// </summary>
    public List<AudioSessionInfo> GetSessions(bool includeIcons)
    {
        var results = new List<AudioSessionInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var renderDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            var sessions = renderDevice.AudioSessionManager.Sessions;
            if (sessions == null) return results;

            var seenProcessIds = new HashSet<uint>();

            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (session == null) continue;

                try
                {
                    if (session.State == AudioSessionState.AudioSessionStateExpired)
                        continue;

                    bool isSystem = session.IsSystemSoundsSession;
                    uint pid = session.GetProcessID;

                    uint dedupeKey = isSystem ? 0u : pid;
                    if (!seenProcessIds.Add(dedupeKey))
                        continue;

                    float volume;
                    bool muted;
                    using (var simpleVolume = session.SimpleAudioVolume)
                    {
                        volume = Math.Clamp(simpleVolume.Volume, 0f, 1f);
                        muted = simpleVolume.Mute;
                    }

                    string name;
                    ImageSource? icon = null;

                    if (isSystem)
                    {
                        name = "System Sounds";
                    }
                    else if (includeIcons)
                    {
                        ResolveProcess(pid, session, out name, out icon);
                    }
                    else
                    {
                        name = ResolveProcessNameFast(pid, session);
                    }

                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    results.Add(new AudioSessionInfo
                    {
                        ProcessId = dedupeKey,
                        DisplayName = name,
                        Volume = volume,
                        IsMuted = muted,
                        IsSystemSounds = isSystem,
                        Icon = icon
                    });
                }
                catch (Exception ex)
                {
                    RuntimeLog.Log("AUDIOMIXER-SESSION", ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("AUDIOMIXER", $"GetSessions error: {ex.Message}");
        }

        return results
            .OrderByDescending(s => s.IsSystemSounds)
            .ThenBy(s => s.ProcessId)
            .ToList();
    }

    private static string ResolveProcessNameFast(uint pid, AudioSessionControl session)
    {
        // Fast path: avoid opening the process (Process.GetProcessById/ProcessName is
        // surprisingly costly when repeated). Use the free session metadata; the rich
        // name is filled in later by the background enrichment pass.
        try
        {
            string display = session.DisplayName;
            if (!string.IsNullOrWhiteSpace(display) && !display.StartsWith("@"))
                return display;
        }
        catch { }
        return "App";
    }

    private static void ResolveProcess(uint pid, AudioSessionControl session, out string name, out ImageSource? icon)
    {
        name = "";
        icon = null;

        string? exePath = null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            try { exePath = process.MainModule?.FileName; } catch { }

            if (!string.IsNullOrEmpty(exePath))
            {
                try
                {
                    var info = FileVersionInfo.GetVersionInfo(exePath);
                    if (!string.IsNullOrWhiteSpace(info.FileDescription))
                        name = info.FileDescription!.Trim();
                }
                catch { }
            }

            if (string.IsNullOrWhiteSpace(name))
                name = process.ProcessName;
        }
        catch { }

        if (string.IsNullOrWhiteSpace(name))
        {
            try
            {
                var display = session.DisplayName;
                if (!string.IsNullOrWhiteSpace(display) && !display.StartsWith("@"))
                    name = display;
            }
            catch { }
        }

        if (!string.IsNullOrEmpty(exePath))
        {
            try { icon = FileIconProvider.GetFileIcon(exePath); } catch { }
        }
    }

    public bool SetSessionVolume(uint processId, float volume)
    {
        float target = Math.Clamp(volume, 0f, 1f);

        // Fast path: reuse the cached session for rapid drags.
        if (_setCacheVolume != null && _setCachePid == processId &&
            (DateTime.UtcNow - _setCacheAtUtc).TotalMilliseconds < SetCacheLifetimeMs)
        {
            try
            {
                _setCacheVolume.Volume = target;
                if (target > 0.001f && _setCacheVolume.Mute) _setCacheVolume.Mute = false;
                return true;
            }
            catch { InvalidateSetCache(); }
        }

        return ResolveSimpleVolume(processId, sv =>
        {
            _setCacheVolume?.Dispose();
            _setCacheVolume = sv;
            _setCachePid = processId;
            _setCacheAtUtc = DateTime.UtcNow;

            sv.Volume = target;
            if (target > 0.001f && sv.Mute) sv.Mute = false;
            return true;
        }, keepAlive: true);
    }

    public bool ToggleSessionMute(uint processId)
    {
        InvalidateSetCache();
        return ResolveSimpleVolume(processId, sv =>
        {
            sv.Mute = !sv.Mute;
            return sv.Mute;
        }, keepAlive: false);
    }

    /// <summary>
    /// Finds the render session matching <paramref name="processId"/> (pid 0 = system
    /// sounds) and runs <paramref name="action"/> against its SimpleAudioVolume.
    /// Must be called on the same apartment that will reuse the cached volume.
    /// </summary>
    private bool ResolveSimpleVolume(uint processId, Func<SimpleAudioVolume, bool> action, bool keepAlive)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;
            if (sessions == null) return false;

            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (session == null) continue;
                try
                {
                    bool match = processId == 0
                        ? session.IsSystemSoundsSession
                        : (!session.IsSystemSoundsSession && session.GetProcessID == processId);
                    if (!match) continue;

                    var sv = session.SimpleAudioVolume;
                    bool result = action(sv);
                    if (!keepAlive) sv.Dispose();
                    return result;
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("AUDIOMIXER-RESOLVE", ex.Message);
        }
        return false;
    }

    private void InvalidateSetCache()
    {
        if (_setCacheVolume != null)
        {
            try { _setCacheVolume.Dispose(); } catch { }
            _setCacheVolume = null;
        }
        _setCachePid = uint.MaxValue;
    }

    // ─── Output devices ───

    public List<AudioDeviceInfo> GetOutputDevices()
    {
        var devices = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string defaultId = "";
            try
            {
                using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                defaultId = def.ID;
            }
            catch { }

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    devices.Add(new AudioDeviceInfo
                    {
                        Id = device.ID,
                        FriendlyName = device.FriendlyName,
                        IsDefault = string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase)
                    });
                }
                finally
                {
                    device.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("AUDIOMIXER-DEVICES", ex.Message);
        }
        return devices;
    }

    public bool SetDefaultOutputDevice(string deviceId)
    {
        return SetDefaultEndpointInternal(deviceId);
    }

    // ─── Input (capture) device ───

    public float GetCaptureVolume()
    {
        try
        {
            var ev = GetCaptureEndpointVolume();
            return ev != null ? Math.Clamp(ev.MasterVolumeLevelScalar, 0f, 1f) : 0.5f;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("AUDIOMIXER-CAPVOL", ex.Message);
            return 0.5f;
        }
    }

    public bool SetCaptureVolume(float volume)
    {
        try
        {
            var ev = GetCaptureEndpointVolume();
            if (ev == null) return false;
            ev.MasterVolumeLevelScalar = Math.Clamp(volume, 0f, 1f);
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("AUDIOMIXER-SETCAPVOL", ex.Message);
            InvalidateCaptureCache();
            return false;
        }
    }

    private MMDevice? _captureDevice;
    private DateTime _captureCacheAtUtc = DateTime.MinValue;

    private AudioEndpointVolume? GetCaptureEndpointVolume()
    {
        if (_captureDevice != null && (DateTime.UtcNow - _captureCacheAtUtc).TotalMilliseconds < SetCacheLifetimeMs)
            return _captureDevice.AudioEndpointVolume;

        InvalidateCaptureCache();
        using var enumerator = new MMDeviceEnumerator();
        _captureDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
        _captureCacheAtUtc = DateTime.UtcNow;
        return _captureDevice.AudioEndpointVolume;
    }

    private void InvalidateCaptureCache()
    {
        if (_captureDevice != null)
        {
            try { _captureDevice.Dispose(); } catch { }
            _captureDevice = null;
        }
    }

    public List<AudioDeviceInfo> GetInputDevices()
    {
        var devices = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string defaultId = "";
            try
            {
                using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                defaultId = def.ID;
            }
            catch { }

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                try
                {
                    devices.Add(new AudioDeviceInfo
                    {
                        Id = device.ID,
                        FriendlyName = device.FriendlyName,
                        IsDefault = string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase)
                    });
                }
                finally
                {
                    device.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("AUDIOMIXER-INDEVICES", ex.Message);
        }
        return devices;
    }

    public bool SetDefaultInputDevice(string deviceId) => SetDefaultEndpointInternal(deviceId);

    private bool SetDefaultEndpointInternal(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        try
        {
            var policyConfig = (IPolicyConfig)new PolicyConfigClient();
            policyConfig.SetDefaultEndpoint(deviceId, ERole.eConsole);
            policyConfig.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
            policyConfig.SetDefaultEndpoint(deviceId, ERole.eCommunications);
            Marshal.ReleaseComObject(policyConfig);
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("AUDIOMIXER-SETDEFAULT", ex.Message);
            return false;
        }
    }

    // ─── Spatial audio (best-effort read of the active spatial APO) ───

    public SpatialAudioMode GetSpatialAudioMode()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            string id = device.ID; // e.g. {0.0.0.00000000}.{guid}
            string guid = ExtractEndpointGuid(id);
            if (string.IsNullOrEmpty(guid)) return SpatialAudioMode.Off;

            string keyPath = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\{guid}\FxProperties";
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(keyPath);
            var value = key?.GetValue("{e4870e26-3cc5-4cd2-ba46-ca0a9a70ed04},6") as string;
            if (string.IsNullOrWhiteSpace(value))
                return SpatialAudioMode.Off;

            if (value.Contains("a44d5fd2", StringComparison.OrdinalIgnoreCase)) // Windows Sonic
                return SpatialAudioMode.WindowsSonic;
            if (value.Contains("265d4a5e", StringComparison.OrdinalIgnoreCase) || // Dolby Atmos for Headphones
                value.Contains("dolby", StringComparison.OrdinalIgnoreCase))
                return SpatialAudioMode.DolbyAtmos;

            return SpatialAudioMode.Off;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("AUDIOMIXER-SPATIAL-GET", ex.Message);
            return SpatialAudioMode.Off;
        }
    }

    private static string ExtractEndpointGuid(string deviceId)
    {
        // Device ID looks like "{0.0.0.00000000}.{b3f8fa53-...}"; the registry uses
        // the trailing endpoint GUID in braces.
        int idx = deviceId.LastIndexOf('}');
        int open = deviceId.LastIndexOf('{');
        if (open >= 0 && idx > open)
            return deviceId.Substring(open, idx - open + 1);
        return "";
    }

    private void ReleaseSessions()
    {
        InvalidateSetCache();
        InvalidateCaptureCache();
    }

    public void Dispose() => ReleaseSessions();

    /// <summary>
    /// Releases the cached per-app session COM objects. Call when the audio view
    /// closes so we don't hold render-session references while the view is idle.
    /// </summary>
    public void ReleaseSessionCache() => ReleaseSessions();

    #region IPolicyConfig COM interop

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string deviceId, IntPtr format);
        [PreserveSig] int GetDeviceFormat(string deviceId, bool def, IntPtr format);
        [PreserveSig] int ResetDeviceFormat(string deviceId);
        [PreserveSig] int SetDeviceFormat(string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(string deviceId, bool def, IntPtr defaultPeriod, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod(string deviceId, IntPtr period);
        [PreserveSig] int GetShareMode(string deviceId, IntPtr mode);
        [PreserveSig] int SetShareMode(string deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue(string deviceId, bool store, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue(string deviceId, bool store, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
        [PreserveSig] int SetEndpointVisibility(string deviceId, bool visible);
    }

    private enum ERole
    {
        eConsole = 0,
        eMultimedia = 1,
        eCommunications = 2
    }

    #endregion
}
