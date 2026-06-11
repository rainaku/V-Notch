using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace VNotch.Uninstaller;

/// <summary>
/// Standalone uninstaller for V-Notch. Removes every trace of the app:
/// the install folder, the per-user data folder, shortcuts, startup entry
/// and all registry keys. Run with "/S" (or "--silent") for a quiet wipe.
/// </summary>
internal static class Program
{
    private const string AppName = "V-Notch";
    private const string AppExeName = "V-Notch.exe";
    private const string UninstallRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\V-Notch";
    private const string RunRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppRegistryPath = @"Software\V-Notch";

    // Old Inno Setup install (kept for users upgrading from legacy builds).
    private const string LegacyInnoUninstallKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{B2567AF4-8BAE-46D2-B83E-56539C82F888}_is1";

    [STAThread]
    private static int Main(string[] args)
    {
        bool silent = args.Any(a =>
            a.Equals("/S", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("/silent", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--silent", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("/quiet", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--quiet", StringComparison.OrdinalIgnoreCase));

        Application.EnableVisualStyles();

        if (!silent)
        {
            var confirm = MessageBox.Show(
                "This will completely remove V-Notch and all of its settings, " +
                "cache and data from this computer.\n\nDo you want to continue?",
                "Uninstall V-Notch",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (confirm != DialogResult.Yes)
            {
                return 1;
            }
        }

        var installDirectory = ResolveInstallDirectory();
        var errors = new List<string>();

        TrySilently(StopRunningInstances, errors, "stop running V-Notch");
        TrySilently(RemoveStartupEntry, errors, "remove startup entry");
        TrySilently(RemoveRegistryKeys, errors, "remove registry keys");
        TrySilently(RemoveShortcuts, errors, "remove shortcuts");
        TrySilently(RemoveAppData, errors, "remove app data");

        // The install folder contains this running uninstall.exe, so it cannot
        // delete itself directly. Hand the final wipe to a temp batch script
        // that waits for us to exit, deletes the folder, then deletes itself.
        TrySilently(() => ScheduleInstallDirRemoval(installDirectory), errors,
            "schedule install folder removal");

        if (!silent)
        {
            if (errors.Count == 0)
            {
                MessageBox.Show(
                    "V-Notch has been completely removed from this computer.",
                    "Uninstall complete",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(
                    "V-Notch was uninstalled, but some items could not be removed:\n\n  - " +
                    string.Join("\n  - ", errors) +
                    "\n\nYou may need to delete them manually.",
                    "Uninstall finished with warnings",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        return errors.Count == 0 ? 0 : 2;
    }

    /// <summary>
    /// The uninstaller normally lives inside the install folder, so the folder
    /// it runs from is the install directory. Fall back to the registry value
    /// and finally the default location.
    /// </summary>
    private static string ResolveInstallDirectory()
    {
        var baseDir = AppContext.BaseDirectory
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (File.Exists(Path.Combine(baseDir, AppExeName)) ||
            File.Exists(Path.Combine(baseDir, "uninstall.exe")))
        {
            return baseDir;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AppRegistryPath);
            if (key?.GetValue("InstallDir") is string fromReg && !string.IsNullOrWhiteSpace(fromReg))
            {
                return fromReg.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }
        catch
        {
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            AppName);
    }

    private static void StopRunningInstances()
    {
        var currentId = Environment.ProcessId;
        bool killedAny = false;

        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppExeName)))
        {
            try
            {
                if (process.Id == currentId)
                {
                    continue;
                }

                process.Kill(entireProcessTree: true);
                process.WaitForExit(8000);
                killedAny = true;
            }
            catch
            {
            }
        }

        if (killedAny)
        {
            System.Threading.Thread.Sleep(1200);
        }
    }

    private static void RemoveStartupEntry()
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: true);
        runKey?.DeleteValue(AppName, throwOnMissingValue: false);
    }

    private static void RemoveRegistryKeys()
    {
        DeleteSubKeyTree(Registry.CurrentUser, UninstallRegistryPath);
        DeleteSubKeyTree(Registry.LocalMachine, UninstallRegistryPath);
        DeleteSubKeyTree(Registry.CurrentUser, AppRegistryPath);
        DeleteSubKeyTree(Registry.CurrentUser, LegacyInnoUninstallKey);
        DeleteSubKeyTree(Registry.LocalMachine, LegacyInnoUninstallKey);
    }

    private static void DeleteSubKeyTree(RegistryKey root, string path)
    {
        try
        {
            root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (System.Security.SecurityException)
        {
        }
    }

    private static void RemoveShortcuts()
    {
        var desktopShortcut = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"{AppName}.lnk");
        DeleteFileIfExists(desktopShortcut);

        var startMenuDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            AppName);

        if (Directory.Exists(startMenuDirectory))
        {
            Directory.Delete(startMenuDirectory, recursive: true);
        }
    }

    private static void RemoveAppData()
    {
        var appDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppName);
        DeleteDirectoryIfExists(appDataFolder);

        var localAppDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppName);
        DeleteDirectoryIfExists(localAppDataFolder);

        // Leftover update installer in the temp folder.
        DeleteFileIfExists(Path.Combine(Path.GetTempPath(), "V-Notch-Setup.exe"));
    }

    private static void ScheduleInstallDirRemoval(string installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory))
        {
            return;
        }

        var scriptPath = Path.Combine(
            Path.GetTempPath(),
            $"v-notch-uninstall-{Guid.NewGuid():N}.cmd");

        var script = string.Join(Environment.NewLine, new[]
        {
            "@echo off",
            "setlocal",
            "rem Wait for the uninstaller to exit, then wipe the install folder.",
            "timeout /t 2 /nobreak >nul",
            $"rmdir /S /Q \"{installDirectory}\"",
            "rem Best-effort second pass in case files were still locked.",
            "timeout /t 2 /nobreak >nul",
            $"if exist \"{installDirectory}\" rmdir /S /Q \"{installDirectory}\"",
            $"del /Q \"{scriptPath}\""
        });

        File.WriteAllText(scriptPath, script, new UTF8Encoding(false));

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{scriptPath}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath()
        });
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void TrySilently(Action action, List<string> errors, string description)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            errors.Add($"{description}: {ex.Message}");
        }
    }
}
