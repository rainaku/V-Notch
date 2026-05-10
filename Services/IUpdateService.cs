using System.Threading.Tasks;

namespace VNotch.Services;

public interface IUpdateService
{
    Task<UpdateInfo?> CheckForUpdatesAsync();
    Task<bool> DownloadAndInstallUpdateAsync(UpdateInfo updateInfo);
    string CurrentVersion { get; }
}

public class UpdateInfo
{
    public string Version { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string ReleaseNotes { get; set; } = string.Empty;
    public DateTime PublishedAt { get; set; }
    public bool IsNewerVersion { get; set; }
}
