using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace VNotch.Services;

public class UpdateService : IUpdateService
{
    private const string GITHUB_API_URL = "https://api.github.com/repos/rainaku/V-Notch/releases/latest";
    private const string USER_AGENT = "V-Notch-Updater";
    private static readonly HttpClient _httpClient = new();

    public string CurrentVersion => "1.6.0";

    static UpdateService()
    {
        _httpClient.DefaultRequestHeaders.Add("User-Agent", USER_AGENT);
    }

    public async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        try
        {
            var response = await _httpClient.GetStringAsync(GITHUB_API_URL);
            var jsonDoc = JsonDocument.Parse(response);
            var root = jsonDoc.RootElement;

            var latestVersion = root.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "";
            var publishedAt = root.GetProperty("published_at").GetDateTime();
            var releaseNotes = root.GetProperty("body").GetString() ?? "";
            
            // Find installer asset
            string downloadUrl = "";
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                        break;
                    }
                }
            }

            var isNewer = CompareVersions(latestVersion, CurrentVersion) > 0;

            return new UpdateInfo
            {
                Version = latestVersion,
                DownloadUrl = downloadUrl,
                ReleaseNotes = releaseNotes,
                PublishedAt = publishedAt,
                IsNewerVersion = isNewer
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error checking for updates: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> DownloadAndInstallUpdateAsync(UpdateInfo updateInfo)
    {
        try
        {
            if (string.IsNullOrEmpty(updateInfo.DownloadUrl))
                return false;

            var tempPath = Path.Combine(Path.GetTempPath(), "V-Notch-Setup.exe");

            // Download installer
            var response = await _httpClient.GetAsync(updateInfo.DownloadUrl);
            response.EnsureSuccessStatusCode();

            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await response.Content.CopyToAsync(fs);
            }

            // Run installer
            var startInfo = new ProcessStartInfo
            {
                FileName = tempPath,
                UseShellExecute = true,
                Verb = "runas" // Run as administrator
            };

            Process.Start(startInfo);

            // Close current application
            Application.Current.Shutdown();

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error downloading/installing update: {ex.Message}");
            return false;
        }
    }

    private static int CompareVersions(string version1, string version2)
    {
        var v1Parts = version1.Split('.');
        var v2Parts = version2.Split('.');
        var maxLength = Math.Max(v1Parts.Length, v2Parts.Length);

        for (int i = 0; i < maxLength; i++)
        {
            var v1Part = i < v1Parts.Length && int.TryParse(v1Parts[i], out var v1) ? v1 : 0;
            var v2Part = i < v2Parts.Length && int.TryParse(v2Parts[i], out var v2) ? v2 : 0;

            if (v1Part > v2Part) return 1;
            if (v1Part < v2Part) return -1;
        }

        return 0;
    }
}
