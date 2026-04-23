using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
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

    /// <summary>
    /// Per-process session identifier. Logged in the startup header so that
    /// multiple app launches sharing a daily log file can be visually
    /// separated when triaging a multi-run test session. Tester quotes this
    /// value when filing a bug; <c>grep "$SessionId"</c> on the log file
    /// returns only the relevant run.
    /// </summary>
    public Guid SessionId { get; } = Guid.NewGuid();

    /// <summary>
    /// UTC timestamp at which this logger (and therefore the app session)
    /// started. Used by D2's <c>DataIntegrityViewModel</c> as the anchor
    /// for the "This session" scope preset: any backed-up file with
    /// <c>BackedUpAt &gt;= SessionStartUtc</c> is considered in-scope.
    /// </summary>
    public DateTime SessionStartUtc { get; } = DateTime.UtcNow;

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

        WriteSessionHeader();
    }

    /// <summary>
    /// Emits a multi-line block at the top of every session that captures
    /// everything needed to attribute subsequent entries:
    /// <list type="bullet">
    ///   <item>SessionId (correlate across log + .diag + metrics files)</item>
    ///   <item>PID (distinguish multiple in-flight runs of the same daily log)</item>
    ///   <item>Build / runtime / OS version (rule out env mismatch in bug reports)</item>
    ///   <item>Data directory + log path (so the tester can paste the log header
    ///     into a bug report and a triager knows where to look for siblings)</item>
    /// </list>
    /// </summary>
    private void WriteSessionHeader()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(CrashSafeLogger).Assembly;
        var ver = asm.GetName().Version?.ToString() ?? "?";
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? ver;
        var pid = Environment.ProcessId;
        var dotnet = Environment.Version.ToString();
        var os = Environment.OSVersion.ToString();
        var arch = RuntimeInformation_ProcessArchitecture();
        var dataDir = AppMode.DataDirectory;
        var portable = AppMode.IsPortable;

        Log("=== Application started ===", callerFile: "CrashSafeLogger", callerMethod: "ctor");
        Log($"SessionId: {SessionId:N}", callerFile: "CrashSafeLogger", callerMethod: "ctor");
        Log($"PID: {pid}, .NET: {dotnet}, Arch: {arch}, Portable: {portable}",
            callerFile: "CrashSafeLogger", callerMethod: "ctor");
        Log($"Build: {info} (asm {ver})", callerFile: "CrashSafeLogger", callerMethod: "ctor");
        Log($"OS: {os}", callerFile: "CrashSafeLogger", callerMethod: "ctor");
        Log($"DataDir: {dataDir}", callerFile: "CrashSafeLogger", callerMethod: "ctor");
        Log($"LogFile: {LogFilePath}", callerFile: "CrashSafeLogger", callerMethod: "ctor");
    }

    private static string RuntimeInformation_ProcessArchitecture()
    {
        // Tiny indirection so the field is testable / mockable without
        // pulling RuntimeInformation in the test project.
        return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
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
                    AzureBackup.Core.FileSystemHelper.TryDelete(file);
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
