using System;
using System.IO;
using System.Text;

namespace VNotch.Services;

public static class RuntimeLog
{
    private static readonly object _lock = new();
    private static string _logPath = Path.Combine(AppContext.BaseDirectory, "vnotch-debug.log");
    private static bool _initialized;
    private const long MaxLogSizeBytes = 5 * 1024 * 1024; // 5 MB rotation threshold

    public static string LogPath => _logPath;

    public static void InitializeNewSession(string? fileName = null)
    {
        lock (_lock)
        {
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                _logPath = Path.Combine(AppContext.BaseDirectory, fileName);
            }

            try
            {
                var dir = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                RotateIfNeeded();

                File.WriteAllText(
                    _logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [SYSTEM] New session started{Environment.NewLine}",
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                _initialized = true;
            }
            catch
            {
                _initialized = false;
            }
        }
    }

    public static void Log(string category, string message)
    {
        WriteEntry("INFO", category, message);
    }

    public static void Warn(string category, string message)
    {
        WriteEntry("WARN", category, message);
    }

    public static void Error(string category, string message)
    {
        WriteEntry("ERROR", category, message);
    }

    public static void Error(string category, Exception ex, string? context = null)
    {
        var msg = context != null
            ? $"{context}: {ex.GetType().Name}: {ex.Message}"
            : $"{ex.GetType().Name}: {ex.Message}";
        WriteEntry("ERROR", category, msg);

#if DEBUG
        System.Diagnostics.Debug.WriteLine($"[{category}] {msg}");
        if (ex.StackTrace != null)
            System.Diagnostics.Debug.WriteLine(ex.StackTrace);
#endif
    }

    private static void WriteEntry(string level, string category, string message)
    {
        lock (_lock)
        {
            if (!_initialized) return;

            try
            {
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{category}] {message}{Environment.NewLine}";
                File.AppendAllText(_logPath, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch
            {
                // Best-effort logging only.
            }
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(_logPath)) return;
            var info = new FileInfo(_logPath);
            if (info.Length <= MaxLogSizeBytes) return;

            var backupPath = _logPath + ".old";
            if (File.Exists(backupPath))
                File.Delete(backupPath);
            File.Move(_logPath, backupPath);
        }
        catch
        {
            // Best-effort rotation.
        }
    }
}
