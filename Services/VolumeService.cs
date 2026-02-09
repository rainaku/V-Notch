using System.Runtime.InteropServices;

namespace VNotch.Services;

/// <summary>
/// Service to manage system master volume using CoreAudio API (WASAPI)
/// Rewritten with proper COM interface definitions and error handling
/// </summary>
public class VolumeService : IDisposable
{
    private IAudioEndpointVolume? _endpointVolume;
    private bool _isInitialized = false;

    public bool IsAvailable => _isInitialized && _endpointVolume != null;

    public VolumeService()
    {
        Initialize();
    }

    private void Initialize()
    {
        try
        {
            // Create device enumerator
            var deviceEnumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
            if (deviceEnumeratorType == null) return;

            var deviceEnumerator = (IMMDeviceEnumerator?)Activator.CreateInstance(deviceEnumeratorType);
            if (deviceEnumerator == null) return;

            // Get default audio endpoint (speakers)
            int hr = deviceEnumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device);
            if (hr != 0 || device == null) return;

            // Activate IAudioEndpointVolume interface
            var iidAudioEndpointVolume = typeof(IAudioEndpointVolume).GUID;
            hr = device.Activate(ref iidAudioEndpointVolume, (uint)CLSCTX.CLSCTX_ALL, IntPtr.Zero, out var endpointVolume);
            if (hr != 0 || endpointVolume == null) return;

            _endpointVolume = (IAudioEndpointVolume)endpointVolume;
            _isInitialized = true;

            System.Diagnostics.Debug.WriteLine("[VolumeService] Initialized successfully");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VolumeService] Init error: {ex.Message}");
            _isInitialized = false;
        }
    }

    public float GetVolume()
    {
        if (!IsAvailable) return 0.5f;
        
        try
        {
            int hr = _endpointVolume!.GetMasterVolumeLevelScalar(out float level);
            if (hr == 0)
            {
                return level;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VolumeService] GetVolume error: {ex.Message}");
        }
        
        return 0.5f;
    }

    public bool SetVolume(float volume)
    {
        if (!IsAvailable) return false;
        
        try
        {
            volume = Math.Clamp(volume, 0f, 1f);
            int hr = _endpointVolume!.SetMasterVolumeLevelScalar(volume, Guid.Empty);
            
            if (hr == 0)
            {
                System.Diagnostics.Debug.WriteLine($"[VolumeService] Volume set to {volume:P0}");
                return true;
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[VolumeService] SetVolume failed with HRESULT: {hr}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VolumeService] SetVolume error: {ex.Message}");
        }
        
        return false;
    }

    public bool GetMute()
    {
        if (!IsAvailable) return false;
        
        try
        {
            int hr = _endpointVolume!.GetMute(out bool mute);
            if (hr == 0) return mute;
        }
        catch { }
        
        return false;
    }

    public void SetMute(bool mute)
    {
        if (!IsAvailable) return;
        
        try
        {
            _endpointVolume!.SetMute(mute, Guid.Empty);
        }
        catch { }
    }

    public void ToggleMute()
    {
        SetMute(!GetMute());
    }

    public void Dispose()
    {
        if (_endpointVolume != null)
        {
            Marshal.ReleaseComObject(_endpointVolume);
            _endpointVolume = null;
        }
        _isInitialized = false;
    }

    #region COM Enums

    private enum EDataFlow
    {
        eRender = 0,
        eCapture = 1,
        eAll = 2
    }

    private enum ERole
    {
        eConsole = 0,
        eMultimedia = 1,
        eCommunications = 2
    }

    [Flags]
    private enum CLSCTX : uint
    {
        CLSCTX_INPROC_SERVER = 0x1,
        CLSCTX_INPROC_HANDLER = 0x2,
        CLSCTX_LOCAL_SERVER = 0x4,
        CLSCTX_REMOTE_SERVER = 0x10,
        CLSCTX_ALL = CLSCTX_INPROC_SERVER | CLSCTX_INPROC_HANDLER | CLSCTX_LOCAL_SERVER | CLSCTX_REMOTE_SERVER
    }

    #endregion

    #region COM Interfaces

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, uint dwStateMask, out IntPtr ppDevices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice? ppDevice);

        [PreserveSig]
        int GetDevice(string pwstrId, out IntPtr ppDevice);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr pClient);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? ppInterface);

        [PreserveSig]
        int OpenPropertyStore(uint stgmAccess, out IntPtr ppProperties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);

        [PreserveSig]
        int GetState(out uint pdwState);
    }

    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig]
        int RegisterControlChangeNotify(IntPtr pNotify);

        [PreserveSig]
        int UnregisterControlChangeNotify(IntPtr pNotify);

        [PreserveSig]
        int GetChannelCount(out uint pnChannelCount);

        [PreserveSig]
        int SetMasterVolumeLevel(float fLevelDB, Guid pguidEventContext);

        [PreserveSig]
        int SetMasterVolumeLevelScalar(float fLevel, Guid pguidEventContext);

        [PreserveSig]
        int GetMasterVolumeLevel(out float pfLevelDB);

        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float pfLevel);

        [PreserveSig]
        int SetChannelVolumeLevel(uint nChannel, float fLevelDB, Guid pguidEventContext);

        [PreserveSig]
        int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, Guid pguidEventContext);

        [PreserveSig]
        int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);

        [PreserveSig]
        int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);

        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, Guid pguidEventContext);

        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);

        [PreserveSig]
        int GetVolumeStepInfo(out uint pnStep, out uint pnStepCount);

        [PreserveSig]
        int VolumeStepUp(Guid pguidEventContext);

        [PreserveSig]
        int VolumeStepDown(Guid pguidEventContext);

        [PreserveSig]
        int QueryHardwareSupport(out uint pdwHardwareSupportMask);

        [PreserveSig]
        int GetVolumeRange(out float pflVolumeMindB, out float pflVolumeMaxdB, out float pflVolumeIncrementdB);
    }

    #endregion
}
