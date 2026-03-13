using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace AzureBackup;

/// <summary>
/// File-based logger that persists to disk immediately on each write.
/// Survives application crashes since logs are flushed after every entry.
/// Stores logs unencrypted in the same directory as the database.
/// </summary>
public sealed class CrashSafeLogger : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _writeLock = new();
    private bool _disposed;

    /// <summary>
    /// Gets the full path to the current log file.
    /// </summary>
    public string LogFilePath { get; }

    public CrashSafeLogger()
    {
        var logDir = AppMode.DataDirectory;
        if (!Directory.Exists(logDir))
        {
            Directory.CreateDirectory(logDir);
        }

        // Use date-based log file names, keeps last few days
        LogFilePath = Path.Combine(logDir, $"azurebackup-{DateTime.Now:yyyy-MM-dd}.log");

        // Append mode, auto-flush on every write
        _writer = new StreamWriter(LogFilePath, append: true)
        {
            AutoFlush = true
        };

        // Clean up old log files (keep last 7 days)
        CleanOldLogs(logDir, keepDays: 7);

        Log("=== Application started ===", callerFile: "CrashSafeLogger", callerMethod: "ctor");
    }

    /// <summary>
    /// Logs a message with timestamp, source file, method name, and optional line number.
    /// </summary>
    public void Log(
        string message,
        [CallerFilePath] string callerFile = "",
        [CallerMemberName] string callerMethod = "",
        [CallerLineNumber] int callerLine = 0)
    {
        if (_disposed) return;

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var source = Path.GetFileNameWithoutExtension(callerFile);
        var entry = $"[{timestamp}] [{source}.{callerMethod}:{callerLine}] {message}";

        lock (_writeLock)
        {
            try
            {
                _writer.WriteLine(entry);
            }
            catch
            {
                // Swallow write errors - logging should never crash the app
            }
        }
    }

    /// <summary>
    /// Logs an exception with full details including inner exceptions and stack trace.
    /// </summary>
    public void LogException(
        Exception ex,
        string context = "",
        [CallerFilePath] string callerFile = "",
        [CallerMemberName] string callerMethod = "",
        [CallerLineNumber] int callerLine = 0)
    {
        if (_disposed) return;

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var source = Path.GetFileNameWithoutExtension(callerFile);
        var prefix = string.IsNullOrEmpty(context) ? "EXCEPTION" : $"EXCEPTION in {context}";

        lock (_writeLock)
        {
            try
            {
                _writer.WriteLine($"[{timestamp}] [{source}.{callerMethod}:{callerLine}] {prefix}:");
                _writer.WriteLine($"  Type: {ex.GetType().FullName}");
                _writer.WriteLine($"  Message: {ex.Message}");
                _writer.WriteLine($"  StackTrace: {ex.StackTrace}");

                var inner = ex.InnerException;
                var depth = 1;
                while (inner != null)
                {
                    _writer.WriteLine($"  --- Inner Exception #{depth} ---");
                    _writer.WriteLine($"  Type: {inner.GetType().FullName}");
                    _writer.WriteLine($"  Message: {inner.Message}");
                    _writer.WriteLine($"  StackTrace: {inner.StackTrace}");
                    inner = inner.InnerException;
                    depth++;
                }

                if (ex is AggregateException agg)
                {
                    foreach (var ie in agg.InnerExceptions)
                    {
                        _writer.WriteLine($"  --- Aggregate Inner: {ie.GetType().FullName}: {ie.Message}");
                        _writer.WriteLine($"  StackTrace: {ie.StackTrace}");
                    }
                }
            }
            catch
            {
                // Swallow write errors
            }
        }
    }

    /// <summary>
    /// Logs a diagnostic event from a service (used as DiagnosticLog event handler).
    /// </summary>
    public void OnDiagnosticLog(object? sender, string message)
    {
        Log($"[DIAG] {message}");
    }

    private static void CleanOldLogs(string logDir, int keepDays)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-keepDays);
            foreach (var file in Directory.GetFiles(logDir, "azurebackup-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // Cleanup is best-effort
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            Log("=== Application shutting down ===");
            _writer.Flush();
            _writer.Dispose();
        }
        catch
        {
            // Ignore
        }
    }
}
