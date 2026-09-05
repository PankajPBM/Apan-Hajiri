namespace E.Attendance.Core.Logging;

using System.Collections.Concurrent;
using System.Text;

/// <summary>
/// Thread-safe daily rolling file logger for E-Attendance system events and errors.
/// Writes directly to the "Event Logs" folder with daily rotation and 30-day retention.
/// </summary>
public static class AppEventLogger
{
    private static readonly object _lock = new();
    private static string _logDirectory = string.Empty;
    private static DateTime _lastPurgeDate = DateTime.MinValue;

    static AppEventLogger()
    {
        Initialize();
    }

    public static string LogDirectory
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_logDirectory))
            {
                Initialize();
            }
            return _logDirectory;
        }
    }

    /// <summary>
    /// Initializes or overrides the logging directory.
    /// </summary>
    public static void Initialize(string? customDirectory = null)
    {
        lock (_lock)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(customDirectory))
                {
                    Directory.CreateDirectory(customDirectory);
                    _logDirectory = customDirectory;
                    return;
                }

                // 1. Primary choice: <AppBaseDir>\Event Logs
                var primaryDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Event Logs");
                try
                {
                    Directory.CreateDirectory(primaryDir);
                    // Test write access
                    var testFile = Path.Combine(primaryDir, ".write_test");
                    File.WriteAllText(testFile, "test");
                    File.Delete(testFile);

                    _logDirectory = primaryDir;
                    PurgeOldLogs();
                    return;
                }
                catch
                {
                    // Fall back to CommonApplicationData (e.g. C:\ProgramData\E-Attendance\Event Logs)
                    var fallbackDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "E-Attendance",
                        "Event Logs");
                    Directory.CreateDirectory(fallbackDir);
                    _logDirectory = fallbackDir;
                    PurgeOldLogs();
                }
            }
            catch
            {
                // Fallback to temp folder in worst-case scenario
                _logDirectory = Path.Combine(Path.GetTempPath(), "E-Attendance", "Event Logs");
                try { Directory.CreateDirectory(_logDirectory); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>
    /// Main entry point for logging an event.
    /// </summary>
    public static void Log(string level, string source, string message, string? detail = null)
    {
        if (string.IsNullOrWhiteSpace(message) && string.IsNullOrWhiteSpace(detail))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var dateStr = now.ToString("yyyy-MM-dd");
        var normLevel = (level ?? "INFO").Trim().ToUpperInvariant();
        var normSource = (source ?? "App").Trim();

        var sb = new StringBuilder(256);
        sb.Append('[').Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(" UTC] ");
        sb.Append('[').Append(normLevel.PadRight(5)).Append("] ");
        sb.Append('[').Append(normSource).Append("] ");
        sb.Append(message);

        if (!string.IsNullOrWhiteSpace(detail))
        {
            sb.AppendLine();
            sb.Append("       Detail: ").Append(detail.TrimEnd());
        }

        var logLine = sb.ToString();

        lock (_lock)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_logDirectory))
                {
                    Initialize();
                }

                // Check for daily purge
                if ((now.Date - _lastPurgeDate).TotalDays >= 1)
                {
                    PurgeOldLogs();
                }

                // 1. Write to general daily event log
                var eventLogPath = Path.Combine(_logDirectory, $"E-Attendance_Events_{dateStr}.log");
                AppendToFile(eventLogPath, logLine);

                // 2. If Warning, Error, Fatal, or Exception, also write to dedicated errors log
                if (normLevel is "WARN" or "WARNING" or "ERROR" or "FATAL" or "CRITICAL")
                {
                    var errorLogPath = Path.Combine(_logDirectory, $"E-Attendance_Errors_{dateStr}.log");
                    AppendToFile(errorLogPath, logLine);
                }
            }
            catch
            {
                // Never allow logging failures to crash the host process
            }
        }
    }

    public static void LogInfo(string source, string message, string? detail = null)
        => Log("INFO", source, message, detail);

    public static void LogWarning(string source, string message, string? detail = null)
        => Log("WARN", source, message, detail);

    public static void LogError(string source, string message, string? detail = null, Exception? ex = null)
    {
        var fullDetail = detail;
        if (ex != null)
        {
            fullDetail = string.IsNullOrWhiteSpace(fullDetail)
                ? ex.ToString()
                : $"{fullDetail}\nException: {ex}";
        }
        Log("ERROR", source, message, fullDetail);
    }

    public static void LogFatal(string source, string message, Exception? ex = null)
    {
        var fullDetail = ex != null ? ex.ToString() : null;
        Log("FATAL", source, message, fullDetail);
    }

    public static void LogException(string source, Exception ex, string? context = null)
    {
        var msg = string.IsNullOrWhiteSpace(context) ? ex.Message : $"{context}: {ex.Message}";
        Log("ERROR", source, msg, ex.ToString());
    }

    private static void AppendToFile(string filePath, string content)
    {
        using var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.WriteLine(content);
    }

    /// <summary>
    /// Deletes log files older than 30 days to avoid filling disk storage.
    /// </summary>
    private static void PurgeOldLogs()
    {
        try
        {
            _lastPurgeDate = DateTime.UtcNow.Date;
            if (!Directory.Exists(_logDirectory)) return;

            var cutoff = DateTime.UtcNow.AddDays(-30);
            var logFiles = Directory.GetFiles(_logDirectory, "E-Attendance_*.log");
            foreach (var file in logFiles)
            {
                try
                {
                    var fi = new FileInfo(file);
                    if (fi.LastWriteTimeUtc < cutoff)
                    {
                        fi.Delete();
                    }
                }
                catch { /* best effort */ }
            }
        }
        catch
        {
            /* best effort */
        }
    }
}
