using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzureBackup.Core.Services;

/// <summary>
/// Collects diagnostic entries for a single file backup or restore operation.
/// Thread-safe: multiple producer threads (parallel chunk uploads/downloads) can
/// log concurrently. On error the collected entries are flushed to a per-file
/// <c>.diag</c> log file via <see cref="FileOperationDiagnostics.Flush"/> so
/// that all context for the failure is in one place. On success the entries
/// are discarded via <see cref="FileOperationDiagnostics.Discard"/>; the
/// caller MUST invoke either <c>Flush</c> or <c>Discard</c> exactly once or
/// the shutdown hook will write a stale snapshot at process exit.
///
/// <para><b>Ambient context:</b> Lower-level services (e.g., <c>AzureBlobService</c>)
/// that don't receive a <c>FileOperationDiagnostics</c> parameter can call
/// <see cref="RecordAmbient"/> to write into the current file's diagnostics via
/// <see cref="AsyncLocal{T}"/>. The calling code sets the scope with
/// <see cref="SetAmbient"/>; it flows automatically across <c>await</c> and into
/// <c>Task.Run</c> / <c>Parallel.ForEachAsync</c>.</para>
/// </summary>
public sealed class FileOperationDiagnostics
{
    private static readonly AsyncLocal<FileOperationDiagnostics?> _ambient = new();

    /// <summary>
    /// Gets the ambient diagnostics for the current async execution context, or null.
    /// </summary>
    public static FileOperationDiagnostics? Current => _ambient.Value;

    /// <summary>
    /// Sets this instance as the ambient diagnostics for the current async flow.
    /// Returns an <see cref="IDisposable"/> that restores the previous value.
    /// </summary>
    public IDisposable SetAmbient()
    {
        var previous = _ambient.Value;
        _ambient.Value = this;
        return new AmbientScope(previous);
    }

    /// <summary>
    /// Records a message into the ambient diagnostics (if one is active).
    /// No-op when called outside an ambient scope — safe for hot paths.
    /// </summary>
    public static void RecordAmbient(string message)
    {
        _ambient.Value?.Record(message);
    }

    /// <summary>
    /// Records a chunk-level entry into the ambient diagnostics (if one is active).
    /// </summary>
    public static void RecordChunkAmbient(
        string phase,
        string chunkHash,
        int plainSize,
        int encryptedSize = 0,
        bool? crcValid = null,
        bool? md5Valid = null,
        string? extra = null)
    {
        _ambient.Value?.RecordChunkByBlob(phase, chunkHash, plainSize, encryptedSize, crcValid, md5Valid, extra);
    }

    private sealed class AmbientScope(FileOperationDiagnostics? previous) : IDisposable
    {
        public void Dispose() => _ambient.Value = previous;
    }

    private readonly ConcurrentQueue<string> _entries = new();
    private readonly ConcurrentQueue<ChunkDiagRecord> _chunkRecords = new();
    private readonly string _filePath;
    private readonly string _operation;
    private readonly long _startTicks = Environment.TickCount64;
    private readonly DateTime _startUtc = DateTime.UtcNow;
    private readonly string _diagnosticsDirectory;
    private int _isFlushed;

    /// <summary>
    /// Process-wide registry of diagnostics that have not yet been flushed.
    /// On <see cref="AppDomain.ProcessExit"/> or Ctrl-C, every live entry
    /// is snapshotted to disk so a hard kill mid-operation does not lose
    /// its chunk-level evidence (the most valuable signal when triaging
    /// CRC / corruption bugs).
    /// </summary>
    private static readonly ConcurrentDictionary<FileOperationDiagnostics, byte> _live = new();
    private static int _shutdownHooksInstalled;

    /// <summary>
    /// Creates a new per-file diagnostic collector.
    /// </summary>
    /// <param name="filePath">The local file path being backed up or restored</param>
    /// <param name="operation">Operation label (e.g., "Backup" or "Restore")</param>
    /// <param name="diagnosticsDirectory">
    /// Directory where .diag files are written.  When null, uses the system temp directory.
    /// </param>
    public FileOperationDiagnostics(string filePath, string operation, string? diagnosticsDirectory = null)
    {
        _filePath = filePath;
        _operation = operation;
        _diagnosticsDirectory = diagnosticsDirectory
            ?? Path.Combine(Path.GetTempPath(), "AzureBackup", "diagnostics");

        Record($"=== {operation} started for '{filePath}' ===");
        Record($"Machine: {Environment.MachineName}, Processors: {Environment.ProcessorCount}, OS: {Environment.OSVersion}");
        Record($"64-bit process: {Environment.Is64BitProcess}, GC.TotalMemory: {GC.GetTotalMemory(false):N0}");

        // Register in the live set so the shutdown hook can flush us if the
        // process exits before Flush() runs. Flush() removes us from this set.
        _live[this] = 0;
        EnsureShutdownHooksInstalled();
    }

    /// <summary>
    /// Installs (once per process) AppDomain.ProcessExit and Ctrl-C handlers
    /// that snapshot every live <see cref="FileOperationDiagnostics"/> to its
    /// .diag file before the process tears down. Without these hooks, a Ctrl-C
    /// or Task Manager "End Task" during a multi-hour backup loses every
    /// in-flight file's chunk-level diagnostic trail.
    /// </summary>
    private static void EnsureShutdownHooksInstalled()
    {
        if (Interlocked.CompareExchange(ref _shutdownHooksInstalled, 1, 0) != 0)
            return;

        AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushAllLive("ProcessExit");
        Console.CancelKeyPress += (_, e) =>
        {
            FlushAllLive("CancelKeyPress");
            // Don't set e.Cancel = true; let the host decide whether to
            // continue. We've already snapshotted; if the host kills the
            // process the snapshots survive.
        };
    }

    private static void FlushAllLive(string reason)
    {
        // Snapshot the keys so callers can safely call Flush themselves
        // concurrently (Flush removes from the dictionary).
        foreach (var diag in _live.Keys)
        {
            try
            {
                diag.WriteSnapshot(errorSummary: $"Forced snapshot at {reason}", terminal: false);
            }
            catch
            {
                // Shutdown path -- swallow everything.
            }
        }
    }

    /// <summary>
    /// Records a timestamped, thread-tagged diagnostic entry.
    /// Safe to call from any thread.
    /// </summary>
    public void Record(string message)
    {
        var elapsed = Environment.TickCount64 - _startTicks;
        var entry = $"[+{elapsed,7}ms] [T{Environment.CurrentManagedThreadId,3}] {message}";
        _entries.Enqueue(entry);
    }

    /// <summary>
    /// Records a chunk-level operation with standard fields for correlation.
    /// </summary>
    public void RecordChunk(
        string phase,
        int chunkIndex,
        string chunkHash,
        int plainSize,
        int encryptedSize = 0,
        bool? crcValid = null,
        bool? md5Valid = null,
        string? extra = null)
    {
        var sb = new StringBuilder(256);
        sb.Append($"[CHUNK] {phase}: idx={chunkIndex}, hash={chunkHash[..Math.Min(12, chunkHash.Length)]}..., plain={plainSize:N0}");
        if (encryptedSize > 0) sb.Append($", enc={encryptedSize:N0}");
        if (crcValid.HasValue) sb.Append($", crc={crcValid.Value}");
        if (md5Valid.HasValue) sb.Append($", md5={md5Valid.Value}");
        if (extra != null) sb.Append($", {extra}");

        Record(sb.ToString());
        _chunkRecords.Enqueue(new ChunkDiagRecord
        {
            Phase = phase,
            Index = chunkIndex,
            Hash = chunkHash,
            PlainSize = plainSize,
            EncryptedSize = encryptedSize,
            CrcValid = crcValid,
            Extra = extra
        });
    }

    /// <summary>
    /// Records a chunk-level operation keyed by blob name/hash (no index available).
    /// Used by <c>AzureBlobService</c> via the ambient context.
    /// </summary>
    private void RecordChunkByBlob(
        string phase,
        string chunkHash,
        int plainSize,
        int encryptedSize = 0,
        bool? crcValid = null,
        bool? md5Valid = null,
        string? extra = null)
    {
        var sb = new StringBuilder(256);
        sb.Append($"[BLOB] {phase}: hash={chunkHash[..Math.Min(12, chunkHash.Length)]}..., plain={plainSize:N0}");
        if (encryptedSize > 0) sb.Append($", enc={encryptedSize:N0}");
        if (crcValid.HasValue) sb.Append($", crc={crcValid.Value}");
        if (md5Valid.HasValue) sb.Append($", md5={md5Valid.Value}");
        if (extra != null) sb.Append($", {extra}");

        Record(sb.ToString());
        _chunkRecords.Enqueue(new ChunkDiagRecord
        {
            Phase = phase,
            Index = -1,
            Hash = chunkHash,
            PlainSize = plainSize,
            EncryptedSize = encryptedSize,
            CrcValid = crcValid,
            Extra = extra
        });
    }

    /// <summary>
    /// Records an error with exception details.
    /// </summary>
    public void RecordError(string context, Exception ex)
    {
        Record($"[ERROR] {context}: {ex.GetType().Name}: {ex.Message}");
        if (ex.StackTrace != null)
        {
            // Only first 5 frames to keep the log manageable
            var frames = ex.StackTrace.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var frame in frames.Take(5))
            {
                Record($"  {frame.Trim()}");
            }
        }

        if (ex.InnerException != null)
        {
            Record($"  Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        }
    }

    /// <summary>
    /// Flushes all collected entries to a per-file .diag log file.
    /// The file name is derived from the original file name + operation + timestamp.
    /// Returns the path to the written diagnostic file, or null if writing failed.
    /// </summary>
    /// <summary>
    /// Crash-safe incremental flush: writes the current state of every
    /// collected entry + chunk record to the .diag/.jsonl files NOW,
    /// without consuming them. Safe to call repeatedly during a long
    /// operation (e.g., every N chunks); each call overwrites the .diag
    /// with the latest snapshot. The final <see cref="Flush"/> at end
    /// of operation produces the same shape so callers don't need to
    /// special-case it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pre-fix: a process crash mid-operation lost EVERY chunk-level
    /// diagnostic for the file because the queue was only drained by
    /// <see cref="Flush"/>. Calling FlushNow at chunk-batch boundaries
    /// keeps disk state at most N chunks behind reality.
    /// </para>
    /// <para>
    /// Snapshot semantics: text entries enumerated via
    /// <c>ConcurrentQueue.GetEnumerator</c> (moment-in-time snapshot);
    /// chunk records iterated the same way. Both queues are left intact
    /// so the final <see cref="Flush"/> sees the complete history. The
    /// .diag file is opened with <c>append: false</c> -- each snapshot
    /// is a complete rewrite, so a torn write at worst reverts to the
    /// previous snapshot.
    /// </para>
    /// </remarks>
    public string? FlushNow(string reason)
    {
        Record($"[SNAPSHOT] {reason} -- entries={_entries.Count}, chunks={_chunkRecords.Count}");
        return WriteSnapshot(errorSummary: null, terminal: false);
    }

    /// <summary>
    /// Flushes all collected entries to a per-file .diag log file.
    /// The file name is derived from the original file name + operation + timestamp.
    /// Returns the path to the written diagnostic file, or null if writing failed.
    /// </summary>
    public string? Flush(string? errorSummary = null)
    {
        var path = WriteSnapshot(errorSummary, terminal: true);
        // Deregister so the shutdown hook doesn't write a stale snapshot
        // over our terminal flush. Idempotent -- multiple Flush calls are
        // a no-op on the second one.
        if (Interlocked.Exchange(ref _isFlushed, 1) == 0)
        {
            _live.TryRemove(this, out _);
        }
        return path;
    }

    /// <summary>
    /// B23: success-path counterpart to <see cref="Flush"/>. Removes
    /// this instance from the live registry so the
    /// <see cref="AppDomain.ProcessExit"/> hook does NOT write a stale
    /// snapshot of its in-memory entries to disk on app shutdown.
    /// <para>
    /// Pre-B23 the success path of <c>BackupFileAsync</c> never invoked
    /// any flush at all, so live <see cref="FileOperationDiagnostics"/>
    /// instances accumulated in <see cref="_live"/> until process exit;
    /// when the user clicked Cancel the shutdown hook fired and wrote
    /// hundreds of partial-snapshot .diag files for files that had
    /// already backed up successfully. Calling <c>Discard</c> on the
    /// success path makes the registry honest about which operations
    /// are actually still in flight.
    /// </para>
    /// </summary>
    public void Discard()
    {
        if (Interlocked.Exchange(ref _isFlushed, 1) == 0)
        {
            _live.TryRemove(this, out _);
        }
    }

    private string? WriteSnapshot(string? errorSummary, bool terminal)
    {
        try
        {
            Directory.CreateDirectory(_diagnosticsDirectory);

            var safeFileName = MakeSafeFileName(Path.GetFileName(_filePath));
            var timestamp = _startUtc.ToString("yyyyMMdd_HHmmss");
            var diagFileName = $"{safeFileName}_{_operation}_{timestamp}.diag";
            var diagPath = Path.Combine(_diagnosticsDirectory, diagFileName);

            // Trailing summary record only on the terminal flush -- otherwise
            // a subsequent snapshot/flush would emit it twice.
            if (terminal)
            {
                Record($"GC.TotalMemory at flush: {GC.GetTotalMemory(false):N0}");
                if (errorSummary != null)
                {
                    Record($"[SUMMARY] {errorSummary}");
                }
                Record($"=== {_operation} diagnostics end -- {_entries.Count} entries ===");
            }

            // Snapshot enumeration leaves both queues intact so a follow-up
            // Flush sees the complete history. Critical for crash safety:
            // a snapshot at chunk N must NOT throw away the records so the
            // terminal flush can still produce a complete .diag.
            using var writer = new StreamWriter(diagPath, append: false, Encoding.UTF8);
            foreach (var entry in _entries)
            {
                writer.WriteLine(entry);
            }

            if (!_chunkRecords.IsEmpty)
            {
                var jsonlPath = diagPath + ".jsonl";
                try
                {
                    using var jsonWriter = new StreamWriter(jsonlPath, append: false, Encoding.UTF8);
                    foreach (var record in _chunkRecords)
                    {
                        jsonWriter.WriteLine(JsonSerializer.Serialize(record, ChunkDiagRecord.JsonOptions));
                    }
                }
                catch
                {
                    // Best-effort -- structured file is a bonus, not critical
                }
            }

            return diagPath;
        }
        catch
        {
            return null;
        }
    }

    private static string MakeSafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(invalid.Contains(c) ? '_' : c);
        }
        // Truncate to reasonable length
        return sb.Length > 80 ? sb.ToString(0, 80) : sb.ToString();
    }
}

/// <summary>
/// Machine-readable chunk-level diagnostic record written to .diag.jsonl companion files.
/// Each line represents one chunk operation (download, verify, decrypt) with its outcome.
/// </summary>
public sealed class ChunkDiagRecord
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    /// <summary>Operation phase (e.g., "Downloaded", "Verified", "BestEffortOK", "BestEffortFAIL").</summary>
    public string Phase { get; init; } = string.Empty;

    /// <summary>Chunk index within the file (-1 if only hash is known).</summary>
    public int Index { get; init; }

    /// <summary>Full SHA-256 hash of the chunk content.</summary>
    public string Hash { get; init; } = string.Empty;

    /// <summary>Plaintext size in bytes.</summary>
    public int PlainSize { get; init; }

    /// <summary>Encrypted blob size in bytes (0 if not available).</summary>
    public int EncryptedSize { get; init; }

    /// <summary>CRC32 envelope check result (null if not checked).</summary>
    public bool? CrcValid { get; init; }

    /// <summary>Additional context (e.g., "aesGcm=OK", "blob=404").</summary>
    public string? Extra { get; init; }
}
