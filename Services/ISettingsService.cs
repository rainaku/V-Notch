using VNotch.Models;

namespace VNotch.Services;

/// <summary>
/// Interface for loading and saving application settings.
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// Load settings from persistent storage. Returns defaults if not found.
    /// </summary>
    NotchSettings Load();

    /// <summary>
    /// Save settings to persistent storage.
    /// </summary>
    void Save(NotchSettings settings);
}
