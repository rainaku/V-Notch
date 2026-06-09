using System;
using System.IO;
using System.Text;

namespace VNotch.Services;

public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
    None = 5
}

public static class RuntimeLog
{
    private static readonly object _lock = new();
    private static string _logPath = Path.Combine(AppContext.BaseDirectory, "vnotch-debug.log");
    private static bool _initialized;
    private const long MaxLogSizeBytes = 5 * 1024 * 1024;

    public static LogLevel MinimumLevel { get; set; } =
#if DEBUG
        LogLevel.Debug;
#else
        LogLevel.Info;
#endif

    public static string LogPath => _logPath;

    public static bool IsEnabled(LogLevel level) => _initialized && level >= MinimumLevel;

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

    public static void Trace(string category, string message) => WriteEntry(LogLevel.Trace, category, message);

    public static void Trace(string category, Func<string> messageFactory)
    {
        if (IsEnabled(LogLevel.Trace)) WriteEntry(LogLevel.Trace, category, messageFactory());
    }

    public static void Debug(string category, string message) => WriteEntry(LogLevel.Debug, category, message);

    public static void Debug(string category, Func<string> messageFactory)
    {
        if (IsEnabled(LogLevel.Debug)) WriteEntry(LogLevel.Debug, category, messageFactory());
    }

    public static void Log(string category, string message) => WriteEntry(LogLevel.Info, category, message);

    public static void Info(string category, string message) => WriteEntry(LogLevel.Info, category, message);

    public static void Info(string category, Func<string> messageFactory)
    {
        if (IsEnabled(LogLevel.Info)) WriteEntry(LogLevel.Info, category, messageFactory());
    }

    public static void Warn(string category, string message) => WriteEntry(LogLevel.Warn, category, message);

    public static void Error(string category, string message) => WriteEntry(LogLevel.Error, category, message);

    public static void Error(string category, Exception ex, string? context = null)
    {
        var msg = context != null
            ? $"{context}: {ex.GetType().Name}: {ex.Message}"
            : $"{ex.GetType().Name}: {ex.Message}";
        WriteEntry(LogLevel.Error, category, msg);

#if DEBUG
        System.Diagnostics.Debug.WriteLine($"[{category}] {msg}");
        if (ex.StackTrace != null)
            System.Diagnostics.Debug.WriteLine(ex.StackTrace);
#endif
    }

    private static string LevelLabel(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO",
        LogLevel.Warn => "WARN",
        LogLevel.Error => "ERROR",
        _ => "INFO"
    };

    private static void WriteEntry(LogLevel level, string category, string message)
    {
        if (level < MinimumLevel) return;

        lock (_lock)
        {
            if (!_initialized) return;

            try
            {
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{LevelLabel(level)}] [{category}] {message}{Environment.NewLine}";
                File.AppendAllText(_logPath, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch
            {
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
        }
    }
}
