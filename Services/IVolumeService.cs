namespace VNotch.Services;

/// <summary>
/// Interface for system volume control operations.
/// </summary>
public interface IVolumeService : IDisposable
{
    /// <summary>
    /// Whether the volume service is initialized and available.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Get current system volume level (0.0 - 1.0).
    /// </summary>
    float GetVolume();

    /// <summary>
    /// Set system volume level (0.0 - 1.0).
    /// </summary>
    bool SetVolume(float volume);

    /// <summary>
    /// Get current mute state.
    /// </summary>
    bool GetMute();

    /// <summary>
    /// Set mute state.
    /// </summary>
    void SetMute(bool mute);

    /// <summary>
    /// Toggle mute on/off.
    /// </summary>
    void ToggleMute();
}
