using System;
using System.IO;
using System.Text;

namespace VNotch.Services;

public static class RuntimeLog
{
    private static readonly object _lock = new();
    private static string _logPath = Path.Combine(AppContext.BaseDirectory, "vnotch-debug.log");
    private static bool _initialized;

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
        lock (_lock)
        {
            if (!_initialized)
            {
                return;
            }

            try
            {
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{category}] {message}{Environment.NewLine}";
                File.AppendAllText(_logPath, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch
            {
                // Best-effort logging only.
            }
        }
    }
}
