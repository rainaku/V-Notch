using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace VNotch.Services;

public class UpdateService : IUpdateService
{
    private const string GITHUB_API_URL = "https://api.github.com/repos/rainaku/V-Notch/releases/latest";
    private const string USER_AGENT = "V-Notch-Updater";
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromSeconds(45);
    private static readonly HttpClient _httpClient = new();
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private string? _latestReleaseEtag;
    private UpdateInfo? _cachedLatestRelease;
    private DateTime _lastCheckUtc = DateTime.MinValue;

    public string CurrentVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "1.7.0";

    static UpdateService()
    {
        _httpClient.DefaultRequestHeaders.Add("User-Agent", USER_AGENT);
    }

    public async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        await _checkLock.WaitAsync();
        try
        {
            var now = DateTime.UtcNow;
            if (_cachedLatestRelease != null && (now - _lastCheckUtc) < MinRefreshInterval)
            {
                return Clone(_cachedLatestRelease);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, GITHUB_API_URL);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            if (!string.IsNullOrWhiteSpace(_latestReleaseEtag))
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", _latestReleaseEtag);
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            _lastCheckUtc = now;

            if (response.StatusCode == HttpStatusCode.NotModified && _cachedLatestRelease != null)
            {
                return Clone(_cachedLatestRelease);
            }

            response.EnsureSuccessStatusCode();

            if (response.Headers.ETag != null)
            {
                _latestReleaseEtag = response.Headers.ETag.ToString();
            }

            var responseText = await response.Content.ReadAsStringAsync();
            var jsonDoc = JsonDocument.Parse(responseText);
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

            var info = new UpdateInfo
            {
                Version = latestVersion,
                DownloadUrl = downloadUrl,
                ReleaseNotes = releaseNotes,
                PublishedAt = publishedAt,
                IsNewerVersion = isNewer
            };

            _cachedLatestRelease = info;
            return Clone(info);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error checking for updates: {ex.Message}");
            return _cachedLatestRelease != null ? Clone(_cachedLatestRelease) : null;
        }
        finally
        {
            _checkLock.Release();
        }
    }

    public async Task<bool> DownloadAndInstallUpdateAsync(UpdateInfo updateInfo, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(updateInfo.DownloadUrl))
                return false;

            var tempPath = Path.Combine(Path.GetTempPath(), "V-Notch-Setup.exe");

            // Download installer with progress reporting.
            using var response = await _httpClient.GetAsync(
                updateInfo.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            progress?.Report(totalBytes.HasValue && totalBytes.Value > 0 ? 0 : -1);

            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var buffer = new byte[81920];
                long received = 0;

                while (true)
                {
                    var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                    if (bytesRead <= 0)
                        break;

                    await fs.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    received += bytesRead;

                    if (totalBytes.HasValue && totalBytes.Value > 0)
                    {
                        var percent = (double)received / totalBytes.Value * 100.0;
                        progress?.Report(percent);
                    }
                }
            }

            if (totalBytes.HasValue && totalBytes.Value > 0)
            {
                progress?.Report(100);
            }

            // Run installer
            var startInfo = new ProcessStartInfo
            {
                FileName = tempPath,
                UseShellExecute = true,
                Verb = "runas" // Run as administrator
            };

            var installerProcess = Process.Start(startInfo);
            if (installerProcess == null)
            {
                return false;
            }

            // If user cancels setup, auto-start V-Notch again.
            var currentExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(currentExe))
            {
                StartRestartWatcherOnCancel(installerProcess.Id, currentExe);
            }

            // Close current application
            Application.Current.Shutdown();

            return true;
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("Update download canceled.");
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error downloading/installing update: {ex.Message}");
            return false;
        }
    }

    private static void StartRestartWatcherOnCancel(int installerPid, string appExePath)
    {
        try
        {
            var watcherPath = Path.Combine(Path.GetTempPath(), $"vnotch-update-watch-{Guid.NewGuid():N}.ps1");
            var escapedAppPath = EscapePowerShellSingleQuoted(appExePath);
            var escapedWatcherPath = EscapePowerShellSingleQuoted(watcherPath);

            // NSIS commonly returns exit code 1 when user cancels.
            var script = $@"
try {{
    $p = Get-Process -Id {installerPid} -ErrorAction Stop
    $p.WaitForExit()
    if ($p.ExitCode -eq 1) {{
        Start-Sleep -Milliseconds 500
        Start-Process -FilePath '{escapedAppPath}'
    }}
}} catch {{}}
Start-Sleep -Milliseconds 200
Remove-Item -LiteralPath '{escapedWatcherPath}' -ErrorAction SilentlyContinue
";

            File.WriteAllText(watcherPath, script);

            var watcherStart = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{watcherPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _ = Process.Start(watcherStart);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to start update-cancel watcher: {ex.Message}");
        }
    }

    private static string EscapePowerShellSingleQuoted(string value)
    {
        return value.Replace("'", "''");
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

    private static UpdateInfo Clone(UpdateInfo source)
    {
        return new UpdateInfo
        {
            Version = source.Version,
            DownloadUrl = source.DownloadUrl,
            ReleaseNotes = source.ReleaseNotes,
            PublishedAt = source.PublishedAt,
            IsNewerVersion = source.IsNewerVersion
        };
    }
}
